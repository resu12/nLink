#if NET8_0_OR_GREATER
#pragma warning disable 8600, 8601, 8602, 8603, 8604, 8618, 8625
#endif
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace NLink.Tools
{
    // Original GUI -> native app file handoff only, not a runtime-read factory.
    // stdin belongs to the checked launch; packet scalars grant no route authority.
    public sealed class LocalCanonicalSenderSource : IDisposable
    {
        private const int Magic = 0x41535231, MaximumPacketBytes = 512;
        private static int receiveAttempted;
        private readonly LocalCanonicalSenderSource original;
        private readonly LocalFrontierAppRequest.AppLease lease;
        private FileStream file;
        private string identity;
        private int taken, disposed;
        internal string Sha256 { get; private set; }
        internal string FileIdentity { get { return IsCurrent ? identity : null; } }
        private LocalCanonicalSenderSource(LocalFrontierAppRequest.AppLease lease)
        { original = this; this.lease = lease; }

        internal static bool Send(FileStream file, SafeWaitHandle process, Stream output, Guid request,
            int pid, long creation, string identity, string hash)
        {
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows()) return false;
#endif
            IntPtr remote;
            if (!DuplicateHandle(GetCurrentProcess(), file.SafeFileHandle, process, out remote, 0, false, 2)) return false;
            // After duplication the owned child's exit bounds even a truncated
            // delivery. Do not close a remote numeric handle after publication:
            // the child may already have closed and reused it.
            using (var body = new MemoryStream())
            using (var writer = new BinaryWriter(body, Encoding.UTF8, true))
            {
                writer.Write(Magic); writer.Write(remote.ToInt64());
                writer.Write(request.ToByteArray());
                writer.Write(pid); writer.Write(creation);
                writer.Write(identity); writer.Write(hash); writer.Flush();
                var bytes = body.ToArray();
                if (bytes.Length > MaximumPacketBytes) return false;
                using (var packet = new BinaryWriter(output, Encoding.UTF8, true))
                { packet.Write(bytes.Length); packet.Write(bytes); packet.Flush(); }
                return true;
            }
        }

        internal static LocalCanonicalSenderSource Receive(LocalFrontierAppRequest.AppLease lease, Stream input)
        {
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows()) return null;
#endif
            if (lease == null || !lease.IsCurrent || Interlocked.Exchange(ref receiveAttempted, 1) != 0) return null;
            var result = new LocalCanonicalSenderSource(lease);
            bool retained = false;
            try
            {
                using (var channel = new BinaryReader(input, Encoding.UTF8, true))
                {
                    int length = channel.ReadInt32();
                    if (length < 12 || length > MaximumPacketBytes) return null;
                    var packet = channel.ReadBytes(length);
                    using (var body = new MemoryStream(packet, false))
                    using (var reader = new BinaryReader(body, new UTF8Encoding(false, true)))
                    {
                        if (reader.ReadInt32() != Magic) return null;
                        long raw = reader.ReadInt64();
                        if (raw <= 0) return null;
                        var handle = new SafeFileHandle(new IntPtr(raw), true);
                        try { result.file = new FileStream(handle, FileAccess.Read); }
                        catch { handle.Dispose(); throw; }
                        if (new Guid(reader.ReadBytes(16)) != lease.RequestId || reader.ReadInt32() != lease.AppProcessId ||
                            reader.ReadInt64() != lease.AppProcessCreationFileTime) return null;
                        result.identity = reader.ReadString(); result.Sha256 = reader.ReadString();
                        if (packet.Length != length || body.Position != body.Length || result.Sha256.Length != 64 || !result.IsCurrent) return null;
                    }
                }
                retained = true; return result;
            }
            catch (IOException) { return null; }
            catch (ArgumentException) { return null; }
            finally { if (!retained) result.Dispose(); }
        }

        // The original GUI publishes the entire packet before invoking the
        // picker. Never wait for missing/truncated stdin or consume another
        // packet. This is the sole reader of the canonical launch's stdin.
        internal static LocalCanonicalSenderSource ReceiveAvailable(LocalFrontierAppRequest.AppLease lease, CancellationToken ct)
        {
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows()) return null;
#endif
            ct.ThrowIfCancellationRequested();
            if (lease == null || !lease.IsCurrent || Volatile.Read(ref receiveAttempted) != 0) return null;
            using (var input = new SafeFileHandle(GetStdHandle(-10), false))
            {
                if (input.IsInvalid || GetFileType(input) != 3) return null;
                var header = new byte[4];
                uint peeked, available, remaining;
                if (!PeekNamedPipe(input, header, 4, out peeked, out available, out remaining) || peeked != 4) return null;
                int length = BitConverter.ToInt32(header, 0);
                if (length < 12 || length > MaximumPacketBytes || available < length + 4) return null;
                ct.ThrowIfCancellationRequested();
                var packet = new byte[length + 4];
                uint read;
                if (!ReadFile(input, packet, (uint)packet.Length, out read, IntPtr.Zero) || read != packet.Length) return null;
                LocalCanonicalSenderSource result;
                using (var stream = new MemoryStream(packet, false)) result = Receive(lease, stream);
                if (ct.IsCancellationRequested)
                {
                    if (result != null) result.Dispose();
                    ct.ThrowIfCancellationRequested();
                }
                return result;
            }
        }

        internal bool IsCurrent
        {
            get
            {
#if NET8_0_OR_GREATER
                if (!OperatingSystem.IsWindows()) return false;
#endif
                if (!Object.ReferenceEquals(original, this) || disposed != 0) return false;
                bool current = false;
                try
                {
                    current = lease.IsCurrent && file != null && MatchesIdentity(file.SafeFileHandle);
                }
                catch (ObjectDisposedException) { }
                if (!current) Dispose();
                return current;
            }
        }

        internal FileStream TakeRead(LocalFrontierAppRequest.AppLease candidate)
        {
            if (!Object.ReferenceEquals(lease, candidate) || !IsCurrent || Interlocked.CompareExchange(ref taken, 1, 0) != 0) return null;
            return file;
        }
        private bool MatchesIdentity(SafeFileHandle handle)
        {
            Information info;
            return !handle.IsClosed && GetFileType(handle) == 1 && GetFileInformationByHandle(handle, out info) &&
                info.Volume.ToString("x8") + ":" + info.IndexHigh.ToString("x8") + info.IndexLow.ToString("x8") == identity &&
                (((long)info.SizeHigh << 32) | info.SizeLow) == 268435456;
        }
        public void Dispose()
        {
            if (!Object.ReferenceEquals(original, this)) return;
            lock (original)
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                if (file != null) file.Dispose();
            }
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Information
        {
            internal uint Attributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern bool DuplicateHandle(IntPtr source, SafeFileHandle file,
            SafeWaitHandle target, out IntPtr remote, uint access, bool inherit, uint options);
        [DllImport("kernel32.dll")] private static extern uint GetFileType(SafeFileHandle handle);
        [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int standardHandle);
        [DllImport("kernel32.dll")] private static extern bool PeekNamedPipe(SafeFileHandle pipe, byte[] buffer, uint size,
            out uint read, out uint available, out uint remaining);
        [DllImport("kernel32.dll")] private static extern bool ReadFile(SafeFileHandle file, byte[] buffer, uint size,
            out uint read, IntPtr overlapped);
        [DllImport("kernel32.dll")] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Information info);
    }
}
