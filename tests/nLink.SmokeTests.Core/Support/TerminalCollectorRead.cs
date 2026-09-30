#if NLINK_TEST_TERMINAL_REQUEST
using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace NLink.Tests
{
    public sealed partial class HeldFileNativeParent
    {
        private object collectorOwner;
        private const int CollectorMagic = 0x48464331;

        // A separate, log-only read. The parent retains all three originals for
        // the later primary handoff; this does not claim writer provenance.
        internal void SendCollectorLog(object owner, Process child, Guid id, string mutation)
        {
            if (!released || !IsCurrent || owner == null ||
                Interlocked.CompareExchange(ref collectorOwner, owner, null) != null)
                throw new InvalidDataException("native_collector_parent_owner");
            using (var packet = new MemoryStream())
            {
                using (var writer = new BinaryWriter(packet, Encoding.UTF8, true))
                {
                    writer.Write(CollectorMagic);
                    writer.Write((mutation == "request" ? Guid.NewGuid() : id).ToByteArray());
                    writer.Write(child.Id); writer.Write(child.StartTime.ToUniversalTime().ToFileTimeUtc());
                    WriteText(writer, Run); WriteText(writer, Scenario);
                    writer.Write(DuplicateForChild(child, files[0].SafeFileHandle));
                    WriteText(writer, measurements[0].Path);
                    WriteText(writer, mutation == "identity" ? "invalid" : measurements[0].Identity);
                    writer.Write(measurements[0].Bytes);
                }
                var bytes = packet.ToArray();
                if (bytes.Length > MaximumPacketBytes) throw new InvalidDataException("native_collector_packet_size");
                using (var writer = new BinaryWriter(child.StandardInput.BaseStream, Encoding.UTF8, true))
                {
                    writer.Write(bytes.Length);
                    writer.Write(bytes, 0, mutation == "truncated" ? bytes.Length / 2 : bytes.Length);
                    writer.Flush();
                }
                child.StandardInput.Close();
            }
        }

        internal static CollectorRead CopyCollectorLog(Guid id, string run, string scenario, string path, string target, bool retainOutput)
        {
            if (!Console.IsInputRedirected) throw new InvalidDataException("native_collector_input");
            using (var input = new BinaryReader(Console.OpenStandardInput(), Encoding.UTF8))
            {
                int length = input.ReadInt32();
                if (length < 1 || length > MaximumPacketBytes) throw new InvalidDataException("native_collector_packet_size");
                var packet = input.ReadBytes(length);
                if (packet.Length != length) throw new InvalidDataException("native_collector_packet_truncated");
                using (var body = new MemoryStream(packet, false))
                using (var reader = new BinaryReader(body, new UTF8Encoding(false, true)))
                using (var current = Process.GetCurrentProcess())
                {
                    if (reader.ReadInt32() != CollectorMagic || new Guid(reader.ReadBytes(16)) != id)
                        throw new InvalidDataException("native_collector_request");
                    if (reader.ReadInt32() != current.Id || reader.ReadInt64() != current.StartTime.ToUniversalTime().ToFileTimeUtc())
                        throw new InvalidDataException("native_collector_process");
                    if (ReadText(reader) != run || ReadText(reader) != scenario)
                        throw new InvalidDataException("native_collector_scope");
                    long handle = reader.ReadInt64();
                    if (handle <= 0) throw new InvalidDataException("native_collector_handle");
                    using (var safe = new SafeFileHandle(new IntPtr(handle), true))
                    using (var source = new FileStream(safe, FileAccess.Read))
                    {
                        string sourcePath = ReadText(reader), identity = ReadText(reader);
                        long bytes = reader.ReadInt64();
                        Information info;
                        if (!String.Equals(sourcePath, path, StringComparison.OrdinalIgnoreCase) ||
                            bytes < 1 || bytes > 4 * 1024 * 1024 || body.Position != body.Length ||
                            GetFileType(safe) != 1 || !GetFileInformationByHandle(safe, out info) ||
                            Identity(info) != identity || (((long)info.SizeHigh << 32) | info.SizeLow) != bytes)
                            throw new InvalidDataException("native_collector_identity");
                        // The collector created this empty ledger through its
                        // existing lifecycle. No path reopen of the source and
                        // no hash or newline normalization is used for copying.
                        FileStream output = null;
                        try
                        {
                            output = new FileStream(target, FileMode.Open, retainOutput ? FileAccess.ReadWrite : FileAccess.Write, FileShare.Read);
                            if (output.Length != 0) throw new InvalidDataException("native_collector_target_not_empty");
                            source.Position = 0;
                            var buffer = new byte[16384];
                            long copied = 0;
                            long lines = 0; byte last = 0;
                            while (copied < bytes)
                            {
                                int count = source.Read(buffer, 0, (int)Math.Min(buffer.Length, bytes - copied));
                                if (count == 0) throw new EndOfStreamException("native_collector_source_short");
                                output.Write(buffer, 0, count); copied += count;
                                if (retainOutput)
                                {
                                    for (int i = 0; i < count; i++) if (buffer[i] == 10) lines++;
                                    last = buffer[count - 1];
                                }
                            }
                            output.Flush(true);
                            if (source.Length != bytes || output.Length != bytes)
                                throw new InvalidDataException("native_collector_length");
                            var result = new CollectorRead(identity, bytes);
                            if (retainOutput)
                            {
                                if (lines < 1 || lines > 262144 || last != 10)
                                    throw new InvalidDataException("native_collector_finalized_lines");
                                result.RetainOutput(output, target, id, run, scenario, lines);
                                output = null;
                            }
                            return result;
                        }
                        finally { if (output != null) output.Dispose(); }
                    }
                }
            }
        }

        // The exported properties are diagnostic only. Optional retained output
        // is claimed by the original finalizer, not by these scalar properties.
        public sealed partial class CollectorRead
        {
            public string SourceIdentity { get; private set; }
            public long CopiedBytes { get; private set; }
            internal CollectorRead(string identity, long bytes) { SourceIdentity = identity; CopiedBytes = bytes; }
        }
    }
}

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        private int collectorSent;
        private static int collectorReadAttempted;

        public bool SendTerminalCollectorLogForTest(NLink.Tests.HeldFileNativeParent files, Process process, string mutation)
        {
            if (!Object.ReferenceEquals(original, this) || files == null || !files.IsCurrent ||
                files.Run != run || files.Scenario != scenario || Volatile.Read(ref sealedState) != 0 ||
                closed == null || closed.WaitOne(0) || workerExit == null || workerExit.WaitOne(0) ||
                process == null || process.HasExited || process.Id != workerId ||
                process.StartTime.ToUniversalTime().ToFileTimeUtc() != workerCreation ||
                (mutation != "" && mutation != "request" && mutation != "identity" && mutation != "truncated")) return false;
            if (Interlocked.Exchange(ref collectorSent, 1) != 0) return false;
            files.SendCollectorLog(this, process, RequestId, mutation);
            return true;
        }

        public static NLink.Tests.HeldFileNativeParent.CollectorRead CopyTerminalCollectorLogForTest(
            string name, Guid id, string run, string scenario, string root, string source, string target, bool retainOutput = false)
        {
            if (Interlocked.Exchange(ref collectorReadAttempted, 1) != 0)
                throw new InvalidDataException("native_collector_already_read");
            // Read-only inspection of the actual bound request. Leave its
            // attach bit untouched: only the original finalizer may take it.
            using (var map = MemoryMappedFile.OpenExisting(name))
            using (var view = map.CreateViewAccessor())
            using (var closed = EventWaitHandle.OpenExisting(name + ".closed"))
            using (var gate = Mutex.OpenExisting(name + ".gate"))
            using (var current = Process.GetCurrentProcess())
            {
                bool held = false;
                try
                {
                    held = gate.WaitOne(0);
                    if (!held || closed.WaitOne(0) || view.ReadInt32(0) != Magic || ReadId(view) != id ||
                        view.ReadInt32(48) != 0 || view.ReadInt32(16) != current.Id ||
                        view.ReadInt64(40) != current.StartTime.ToUniversalTime().ToFileTimeUtc())
                        throw new InvalidDataException("native_collector_gui_request");
                    int length = view.ReadInt32(20);
                    if (length < 1 || length > RequestBytes) throw new InvalidDataException("native_collector_gui_scope");
                    var body = new byte[length];
                    if (view.ReadArray(HeaderBytes, body, 0, length) != length)
                        throw new InvalidDataException("native_collector_gui_scope");
                    using (var bytes = new MemoryStream(body, false))
                    using (var reader = new BinaryReader(bytes, new UTF8Encoding(false, true)))
                        if (reader.ReadString() != run || reader.ReadString() != scenario || reader.ReadString() != root || bytes.Position != bytes.Length)
                            throw new InvalidDataException("native_collector_gui_scope");
                    using (var parent = OpenProcess(0x00101000, false, view.ReadInt32(4)))
                    using (var parentExit = new ProcessWait(parent))
                    {
                        long creation, exit, kernel, user;
                        if (parent.IsInvalid || !GetProcessTimes(parent, out creation, out exit, out kernel, out user) ||
                            creation != view.ReadInt64(8) || parentExit.WaitOne(0))
                            throw new InvalidDataException("native_collector_gui_parent");
                        if (retainOutput && !String.Equals(Path.GetFullPath(target), Path.Combine(root, "append.log"), StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("native_collector_target_scope");
                        var result = NLink.Tests.HeldFileNativeParent.CopyCollectorLog(id, run, scenario, source, target, retainOutput);
                        if (closed.WaitOne(0) || parentExit.WaitOne(0))
                        { result.Dispose(); throw new InvalidDataException("native_collector_gui_closed"); }
                        if (retainOutput) result.SetCollectorChannel(name, root);
                        return result;
                    }
                }
                finally { Release(gate, held); }
            }
        }
    }
}
#endif
