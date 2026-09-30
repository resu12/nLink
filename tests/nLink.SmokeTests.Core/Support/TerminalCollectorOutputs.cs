#if NLINK_TEST_TERMINAL_REQUEST
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using NLink.Tools.FrontierLedger;

namespace NLink.Tools.FrontierLedger
{
    public sealed partial class GuiSmokeLedgerStreamCapture
    {
        internal bool WriteCollectorOutputs(BinaryWriter writer, GuiSmokeLedgerCapturedOutput copy)
        { return IsCurrent && OwnsSemanticCopy(copy) && outputs.WriteCollectorOutputs(writer, copy); }
    }

    internal sealed partial class GuiSmokeLedgerRetainedOutputs
    {
        internal bool WriteCollectorOutputs(BinaryWriter writer, GuiSmokeLedgerCapturedOutput copy)
        {
            lock (sync)
            {
                if (!OwnsSemanticCopy(copy)) return false;
                for (int i = 0; i < 4; i++)
                {
                    var file = i == 3 ? semanticCopy : files[i];
                    var info = i == 3 ? semanticCopyInfo : metadata[i];
                    writer.Write(file.SafeFileHandle.DangerousGetHandle().ToInt64());
                    writer.Write(info.CreationPath); writer.Write(info.FileIdentity); writer.Write(info.Sha256);
                    writer.Write(info.ByteCount); writer.Write(info.LineCount);
                }
                return IsCurrent;
            }
        }
    }
}

namespace NLink.Tools
{
    // Test-only trusted-workflow channel. It transports four actual read-only
    // file objects, not a provenance flag or a production security capability.
    public sealed partial class LocalGuiWriterRequest
    {
        private const int CollectorPacketBytes = 16384;
        private MemoryMappedFile collectorMap;
        private MemoryMappedViewAccessor collectorView;
        private EventWaitHandle collectorReady, collectorAck;
        private CollectorOutputs collectorPending, collectorAccepted;
        private int collectorPollAttempted;

        public bool EnableCollectorOutputsForTest()
        {
            if (!Object.ReferenceEquals(original, this) || workerExit == null || workerExit.WaitOne(0) ||
                closed == null || closed.WaitOne(0) || collectorMap != null) return false;
            try
            {
                string name = Name + ".terminal-outputs-v1";
                collectorMap = MemoryMappedFile.CreateNew(name, CollectorPacketBytes + 16);
                collectorView = collectorMap.CreateViewAccessor();
                collectorReady = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".ready");
                collectorAck = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".ack");
                return true;
            }
            catch { CloseCollectorOutputsForTest(); return false; }
        }

        public void PollCollectorOutputsForTest()
        {
            if (!Object.ReferenceEquals(original, this) || collectorReady == null || !collectorReady.WaitOne(0) ||
                Interlocked.Exchange(ref collectorPollAttempted, 1) != 0) return;
            CollectorOutputs pending = null;
            try
            {
                if (workerExit == null || workerExit.WaitOne(0) || closed.WaitOne(0)) return;
                int length = collectorView.ReadInt32(4);
                if (length < 1 || length > CollectorPacketBytes) return;
                var packet = new byte[length];
                if (collectorView.ReadArray(16, packet, 0, length) != length) return;
                pending = new CollectorOutputs();
                using (var bytes = new MemoryStream(packet, false))
                using (var reader = new BinaryReader(bytes, new UTF8Encoding(false, true)))
                {
                    if (new Guid(reader.ReadBytes(16)) != RequestId || reader.ReadInt32() != workerId ||
                        reader.ReadInt64() != workerCreation || reader.ReadString() != run || reader.ReadString() != scenario ||
                        reader.ReadString() != root) return;
                    pending.source = reader.ReadString(); pending.lines = reader.ReadInt64(); pending.bookmark = reader.ReadInt64();
                    if (pending.source != Path.Combine(root, "append.log") || pending.lines < 1 || pending.lines > 262144 || pending.bookmark != 0) return;
                    var handles = new HashSet<long>();
                    var identities = new HashSet<string>(StringComparer.Ordinal);
                    string[] names = { "filetransfer-gui-log-ledger-slice.log", "filetransfer-gui-log-writer-evidence-slice.log",
                        "filetransfer-gui-semantic-evidence-slice.log", "filetransfer-retained-log-slice.log" };
                    for (int i = 0; i < 4; i++)
                    {
                        long remote = reader.ReadInt64();
                        string path = reader.ReadString(), identity = reader.ReadString(), hash = reader.ReadString();
                        long count = reader.ReadInt64(), lines = reader.ReadInt64();
                        if (remote <= 0 || !handles.Add(remote) || !identities.Add(identity) || path != Path.Combine(root, names[i]) ||
                            !System.Text.RegularExpressions.Regex.IsMatch(hash, "\\A[a-f0-9]{64}\\z") ||
                            count < 0 || count > 8 * 1024 * 1024 || lines < 0 || lines > 262144) return;
                        SafeFileHandle handle;
                        // Pull only from the original parent's held, bound process
                        // handle. Never reopen a PID or an output path.
                        if (!DuplicateCollectorOutput(workerExit.SafeWaitHandle, new IntPtr(remote), GetCurrentProcess(),
                            out handle, 0x80000000, false, 0)) throw new IOException("collector_output_duplicate");
                        FileStream file;
                        try { file = new FileStream(handle, FileAccess.Read); }
                        catch { handle.Dispose(); throw; }
                        pending.files.Add(file);
                        var info = GuiSmokeLedgerCapturedOutput.Capture(file, path, hash, lines, count);
                        if (info == null || info.FileIdentity != identity || !info.Matches(file)) return;
                        pending.metadata.Add(info);
                    }
                    pending.origins = TerminalCollectorOrigins.Read(reader, pending.metadata);
                    if (!pending.origins.MatchesLines(pending.lines)) return;
                    if (bytes.Position != bytes.Length || !pending.FilesCurrent || workerExit.WaitOne(0) || closed.WaitOne(0)) return;
                }
                collectorPending = pending; pending = null;
                collectorView.Write(0, 1);
            }
            finally
            {
                if (pending != null) pending.Dispose();
                if (collectorPending == null) collectorView.Write(0, -1);
                collectorView.Flush(); collectorAck.Set();
            }
        }

        // This wrapper is installed only at the actual parent's existing
        // successful exit/output/persistence seam. The original take runs once.
        public Completion TakeAfterExitWithCollectorOutputsForTest()
        {
            if (!Object.ReferenceEquals(original, this)) return null;
            var completion = TakeAfterExit();
            if (completion != null && collectorPending != null && collectorPending.Accept(completion))
            { collectorAccepted = collectorPending; collectorPending = null; }
            return completion;
        }

        public CollectorOutputs TakeCollectorOutputsForTest(Completion completion)
        {
            if (!Object.ReferenceEquals(original, this) || collectorAccepted == null || !collectorAccepted.OwnsCompletion(completion)) return null;
            var result = collectorAccepted; collectorAccepted = null; return result;
        }

        public void CloseCollectorOutputsForTest()
        {
            if (!Object.ReferenceEquals(original, this)) return;
            if (collectorPending != null) { collectorPending.Dispose(); collectorPending = null; }
            if (collectorAccepted != null) { collectorAccepted.Dispose(); collectorAccepted = null; }
            if (collectorAck != null) { collectorAck.Dispose(); collectorAck = null; }
            if (collectorReady != null) { collectorReady.Dispose(); collectorReady = null; }
            if (collectorView != null) { collectorView.Dispose(); collectorView = null; }
            if (collectorMap != null) { collectorMap.Dispose(); collectorMap = null; }
        }

        public sealed partial class CollectorOutputs : IDisposable
        {
            private readonly CollectorOutputs original;
            internal readonly System.Collections.Generic.List<FileStream> files = new System.Collections.Generic.List<FileStream>();
            internal readonly System.Collections.Generic.List<GuiSmokeLedgerCapturedOutput> metadata = new System.Collections.Generic.List<GuiSmokeLedgerCapturedOutput>();
            internal string source;
            internal long lines, bookmark;
            internal TerminalCollectorOrigins origins;
            private Completion accepted;
            private bool disposed;
            internal CollectorOutputs() { original = this; }
            internal bool FilesCurrent
            {
                get
                {
                    if (!Object.ReferenceEquals(original, this) || disposed || files.Count != 4 || metadata.Count != 4) return false;
                    try { for (int i = 0; i < 4; i++) if (!files[i].CanRead || files[i].CanWrite || !metadata[i].Matches(files[i])) return false; }
                    catch { return false; }
                    return true;
                }
            }
            internal bool Accept(Completion completion)
            {
                if (!FilesCurrent || accepted != null || completion == null || !completion.WasAcceptedHere() ||
                    completion.SourcePath != source || completion.FrozenLineCount != lines || completion.Bookmark != bookmark || completion.Outputs.Count != 4) return false;
                for (int i = 0; i < 4; i++)
                {
                    var info = metadata[i]; var output = completion.Outputs[i];
                    if (output.Path != info.CreationPath || output.Sha256 != info.Sha256 || output.ByteCount != info.ByteCount ||
                        output.LineCount != info.LineCount || (i < 3 && output.OriginalFileIdentity != info.FileIdentity)) return false;
                }
                accepted = completion; return true;
            }
            public bool IsCurrent { get { return accepted != null && FilesCurrent; } }
            public bool OwnsCompletion(Completion completion)
            { return completion != null && Object.ReferenceEquals(accepted, completion) && IsCurrent; }
            public void Dispose()
            {
                if (!Object.ReferenceEquals(original, this) || disposed) return;
                disposed = true; foreach (var file in files) file.Dispose();
            }
        }

        public sealed partial class Finalizer
        {
            private GuiSmokeLedgerCapturedOutput collectorCopy;
            private int collectorOfferAttempted;
            public GuiSmokeLedgerCapturedOutput CopyCollectorSemanticForTest(NLink.Tests.HeldFileNativeParent.CollectorRead value,
                GuiSmokeLedgerStreamingResult result, string destination)
            {
                if (!OwnsCollectorCaptureForTest(value, result, result == null ? null : result.NativeCapture) ||
                    !result.StreamCapture.HasRetainedOutputs || collectorCopy != null || destination != Path.Combine(root, "filetransfer-retained-log-slice.log")) return null;
                var clock = Stopwatch.StartNew();
                collectorCopy = result.StreamCapture.CopySemantic(destination, delegate { return IsCurrent && value.OutputIsCurrent && clock.ElapsedMilliseconds <= 5000; });
                return collectorCopy;
            }

            public bool PublishCollectorOutputsForTest(NLink.Tests.HeldFileNativeParent.CollectorRead value, GuiSmokeLedgerStreamingResult result,
                GuiSmokeLedgerCapturedOutput copy)
            {
                if (!OwnsCollectorCaptureForTest(value, result, result == null ? null : result.NativeCapture) ||
                    !Object.ReferenceEquals(copy, collectorCopy) || !result.StreamCapture.OwnsSemanticCopy(copy) ||
                    Interlocked.Exchange(ref collectorOfferAttempted, 1) != 0) return false;
                using (var packet = new MemoryStream())
                {
                    using (var writer = new BinaryWriter(packet, new UTF8Encoding(false, true), true))
                    {
                        writer.Write(id.ToByteArray()); writer.Write(view.ReadInt32(16)); writer.Write(view.ReadInt64(40));
                        writer.Write(run); writer.Write(scenario); writer.Write(root);
                        writer.Write(result.NativeCapture.SourcePath); writer.Write(result.NativeCapture.FrozenLineCount); writer.Write(result.NativeCapture.Bookmark);
                        if (!result.StreamCapture.WriteCollectorOutputs(writer, copy)) return false;
                        TerminalCollectorOrigins.WriteOriginal(writer, value, result.StreamCapture);
                    }
                    byte[] body = packet.ToArray();
                    if (body.Length < 1 || body.Length > CollectorPacketBytes) return false;
                    string name = endpointName + ".terminal-outputs-v1";
                    using (var map = MemoryMappedFile.OpenExisting(name))
                    using (var channel = map.CreateViewAccessor())
                    using (var ready = EventWaitHandle.OpenExisting(name + ".ready"))
                    using (var ack = EventWaitHandle.OpenExisting(name + ".ack"))
                    {
                        channel.WriteArray(16, body, 0, body.Length); channel.Write(4, body.Length); channel.Flush(); ready.Set();
                        // Bounded overlap only. The parent still adopts after
                        // genuine successful child exit; this is not completion.
                        int signaled = WaitHandle.WaitAny(new WaitHandle[] { ack, closed, parentExit }, 5000);
                        return signaled == 0 && channel.ReadInt32(0) == 1 &&
                            OwnsCollectorCaptureForTest(value, result, result.NativeCapture) && result.StreamCapture.OwnsSemanticCopy(copy);
                    }
                }
            }
        }

        [DllImport("kernel32.dll", EntryPoint = "DuplicateHandle", SetLastError = true)]
        private static extern bool DuplicateCollectorOutput(SafeWaitHandle process, IntPtr source, IntPtr target,
            out SafeFileHandle duplicate, uint access, bool inherit, uint options);
    }
}
#endif
