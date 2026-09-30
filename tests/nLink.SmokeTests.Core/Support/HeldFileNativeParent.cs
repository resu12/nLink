#if NET8_0_OR_GREATER
#pragma warning disable 8600, 8601, 8602, 8603, 8604, 8618, 8625
#endif
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace NLink.Tests
{
    // Test-only, fixed three-file native hop over the actual child's stdin.
    // Neither this transport nor its diagnostic descriptors issue integrity,
    // writer, original-primary or route authority. No production caller exists.
    public sealed partial class HeldFileNativeParent : IDisposable
    {
        public const int Magic = 0x48465031, MaximumPacketBytes = 16384;
        private static int attempted;
        private readonly List<FileStream> files = new List<FileStream>();
        private readonly List<Measurement> measurements = new List<Measurement>();
        private readonly Stream input;
        private readonly HeldFileNativeParent original;
        private object requestOwner;
        private bool disposed, released;
        private TransferBinding transfer;
        public Guid RequestId { get; private set; }
        internal string Run { get; private set; }
        internal string Scenario { get; private set; }
        private HeldFileNativeParent(Stream input) { this.input = input; original = this; }

        public static HeldFileNativeParent Receive(string request, string run, string scenario, string logPath)
        {
            if (Interlocked.Exchange(ref attempted, 1) != 0) throw new InvalidDataException("native_parent_already_adopted");
            var result = new HeldFileNativeParent(Console.OpenStandardInput());
            bool success = false;
            try
            {
                if (!Console.IsInputRedirected) throw new InvalidDataException("native_parent_input");
                var input = new BinaryReader(result.input, Encoding.UTF8, true);
                int length = input.ReadInt32();
                if (length < 1 || length > MaximumPacketBytes) throw new InvalidDataException("native_parent_packet_size");
                var packet = input.ReadBytes(length);
                if (packet.Length != length) throw new InvalidDataException("native_parent_packet_truncated");
                using (var body = new MemoryStream(packet, false))
                using (var reader = new BinaryReader(body, new UTF8Encoding(false, true)))
                using (var current = Process.GetCurrentProcess())
                {
                    if (reader.ReadInt32() != Magic || reader.ReadInt32() != 2) throw new InvalidDataException("native_parent_packet_version");
                    result.RequestId = new Guid(reader.ReadBytes(16));
                    if (result.RequestId.ToString("D") != request || result.RequestId == Guid.Empty)
                        throw new InvalidDataException("native_parent_request");
                    if (reader.ReadInt32() != current.Id || reader.ReadInt64() != current.StartTime.ToUniversalTime().ToFileTimeUtc())
                        throw new InvalidDataException("native_parent_process");
                    result.Run = ReadText(reader); result.Scenario = ReadText(reader);
                    if (result.Run != run || result.Scenario != scenario) throw new InvalidDataException("native_parent_scope");
                    result.transfer = TransferBinding.Read(reader);
                    var handles = new HashSet<long>();
                    for (int i = 0; i < 3; i++)
                    {
                        long handle = reader.ReadInt64();
                        if (handle <= 0 || !handles.Add(handle)) throw new InvalidDataException("native_parent_handle");
                        // Take local responsibility before parsing any remaining metadata.
                        var safe = new SafeFileHandle(new IntPtr(handle), true);
                        FileStream file;
                        try { file = new FileStream(safe, FileAccess.Read); }
                        catch { safe.Dispose(); throw; }
                        result.files.Add(file);
                        string path = ReadText(reader), identity = ReadText(reader);
                        long bytes = reader.ReadInt64();
                        byte[] hash = reader.ReadBytes(32);
                        if (hash.Length != 32 || bytes < 1 || (i == 0 ? bytes > 4 * 1024 * 1024 : bytes != 262144) ||
                            (i == 0 && !String.Equals(path, logPath, StringComparison.OrdinalIgnoreCase)))
                            throw new InvalidDataException("native_parent_descriptor");
                        result.measurements.Add(new Measurement(i == 0 ? "log" : i == 1 ? "source" : "destination",
                            path, identity, bytes, Convert.ToBase64String(hash)));
                    }
                    if (body.Position != body.Length || !result.IsCurrent) throw new InvalidDataException("native_parent_file_identity");
                }
                success = true;
                return result;
            }
            finally { if (!success) result.Dispose(); }
        }

        // The producer acknowledges only after retiring its original handles.
        // Parent process exit/EOF bounds failed exchanges; the owner harness also
        // imposes a ten-second acknowledgement deadline and kills its own child.
        public void ConfirmProducerRelease()
        {
            if (released || !IsCurrent) throw new InvalidDataException("native_parent_release_state");
            byte[] bytes = new BinaryReader(input, Encoding.UTF8, true).ReadBytes(16);
            if (bytes.Length != 16 || new Guid(bytes) != RequestId || !IsCurrent)
                throw new InvalidDataException("native_parent_release");
            released = true;
        }

        public bool IsCurrent
        {
            get
            {
                if (!Object.ReferenceEquals(original, this) || disposed || files.Count != 3 || measurements.Count != 3) return false;
                try
                {
                    for (int i = 0; i < files.Count; i++)
                    {
                        Information info;
                        if (!files[i].CanRead || GetFileType(files[i].SafeFileHandle) != 1 ||
                            !GetFileInformationByHandle(files[i].SafeFileHandle, out info) ||
                            Identity(info) != measurements[i].Identity || (((long)info.SizeHigh << 32) | info.SizeLow) != measurements[i].Bytes)
                            return false;
                    }
                    return true;
                }
                catch { return false; }
            }
        }

        public Measurement[] Describe()
        {
            if (!released || !IsCurrent) throw new InvalidDataException("native_parent_inactive");
            return measurements.ToArray();
        }

        // In-process test association only. A request cannot be substituted by
        // its ID, a copied owner, or a second otherwise genuine request.
        internal bool TryClaimRequest(object request)
        {
            return request != null && released && IsCurrent &&
                Interlocked.CompareExchange(ref requestOwner, request, null) == null;
        }

        internal bool IsClaimedBy(object request)
        { return request != null && Object.ReferenceEquals(requestOwner, request) && released && IsCurrent; }

        // Second hop is available only to the exact request that claimed this
        // owner. It forwards already measured descriptors, never rehashes files.
        internal void SendToClaimedWorker(object request, Process process, Guid id, string mutation)
        {
            if (!IsClaimedBy(request)) throw new InvalidDataException("native_primary_parent_owner");
            using (var packet = new MemoryStream())
            {
                using (var writer = new BinaryWriter(packet, Encoding.UTF8, true))
                {
                    writer.Write(Magic); writer.Write(2);
                    writer.Write((mutation == "request" ? Guid.NewGuid() : id).ToByteArray());
                    writer.Write(process.Id); writer.Write(process.StartTime.ToUniversalTime().ToFileTimeUtc());
                    WriteText(writer, Run); WriteText(writer, Scenario);
                    transfer.Write(writer);
                    for (int i = 0; i < files.Count; i++)
                    {
                        writer.Write(DuplicateForChild(process, files[i].SafeFileHandle));
                        WriteText(writer, measurements[i].Path); WriteText(writer, measurements[i].Identity);
                        writer.Write(measurements[i].Bytes); writer.Write(Convert.FromBase64String(measurements[i].Sha256Base64));
                    }
                }
                byte[] bytes = packet.ToArray();
                if (bytes.Length > MaximumPacketBytes) throw new InvalidDataException("native_primary_packet_size");
                using (var writer = new BinaryWriter(process.StandardInput.BaseStream, Encoding.UTF8, true))
                { writer.Write(bytes.Length); writer.Write(bytes, 0, mutation == "truncated" ? bytes.Length / 2 : bytes.Length); writer.Flush(); }
            }
        }

        public void Dispose()
        {
            if (!Object.ReferenceEquals(original, this) || disposed) return;
            disposed = true;
            foreach (var file in files) file.Dispose();
            files.Clear();
        }

        public sealed class Measurement
        {
            public string Role { get; private set; }
            public string Path { get; private set; }
            public string Identity { get; private set; }
            public long Bytes { get; private set; }
            public string Sha256Base64 { get; private set; }
            internal Measurement(string role, string path, string identity, long bytes, string hash)
            { Role = role; Path = path; Identity = identity; Bytes = bytes; Sha256Base64 = hash; }
        }

        // Only metadata inside the original owned native hop, never a receipt
        // accepted independently of that hop or its protected payload handles.
        internal sealed class TransferBinding
        {
            internal readonly string Session, Transfer;
            internal readonly int InboundLine, OutboundLine;
            private TransferBinding(string session, string transfer, int inbound, int outbound)
            { Session = session; Transfer = transfer; InboundLine = inbound; OutboundLine = outbound; }
            internal static TransferBinding Read(BinaryReader reader)
            {
                try
                {
                    string session = ReadText(reader), transfer = ReadText(reader);
                    int inbound = reader.ReadInt32(), outbound = reader.ReadInt32();
                    if (!Token(session) || !Token(transfer) || inbound < 1 || outbound < 1 ||
                        inbound > 4096 || outbound > 4096 || inbound == outbound)
                        throw new InvalidDataException("native_parent_transfer_binding");
                    return new TransferBinding(session, transfer, inbound, outbound);
                }
                catch (EndOfStreamException) { throw new InvalidDataException("native_parent_transfer_binding_truncated"); }
            }
            private static bool Token(string value)
            {
                if (value.Length != 29 || !value.StartsWith("fte1-", StringComparison.Ordinal)) return false;
                for (int i = 5; i < value.Length; i++)
                    if (!(value[i] >= '0' && value[i] <= '9') && !(value[i] >= 'a' && value[i] <= 'f')) return false;
                return true;
            }
            internal void Write(BinaryWriter writer)
            { WriteText(writer, Session); WriteText(writer, Transfer); writer.Write(InboundLine); writer.Write(OutboundLine); }
        }

        internal TransferBinding OwnedTransfer(object request)
        { return IsClaimedBy(request) ? transfer : null; }
        internal bool PayloadsMatch(object request, long bytes)
        {
            return IsClaimedBy(request) && bytes == 262144 && measurements[1].Bytes == bytes &&
                measurements[2].Bytes == bytes && measurements[1].Sha256Base64 == measurements[2].Sha256Base64;
        }

        internal static long DuplicateForChild(Process child, SafeFileHandle source)
        {
            IntPtr duplicate;
            if (child.HasExited || !DuplicateHandle(GetCurrentProcess(), source, child.Handle, out duplicate, 0, false, 2))
                throw new IOException("native_parent_duplicate_failed");
            return duplicate.ToInt64();
        }

        internal static void WriteText(BinaryWriter writer, string value)
        {
            var bytes = new UTF8Encoding(false, true).GetBytes(value);
            if (bytes.Length < 1 || bytes.Length > 2048) throw new InvalidDataException("native_parent_text");
            writer.Write(bytes.Length); writer.Write(bytes);
        }
        private static string ReadText(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 1 || length > 2048) throw new InvalidDataException("native_parent_text");
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new InvalidDataException("native_parent_text");
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        private static string Identity(Information info)
        { return info.Volume.ToString("x8") + ":" + info.IndexHigh.ToString("x8") + info.IndexLow.ToString("x8"); }
        [StructLayout(LayoutKind.Sequential)] private struct Information
        {
            internal uint Attributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeFileHandle source,
            IntPtr targetProcess, out IntPtr target, uint access, bool inherit, uint options);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Information info);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetFileType(SafeFileHandle handle);
    }
}
