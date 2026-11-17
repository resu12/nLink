using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace NLink.Tools
{
    // Local trusted-workflow capability, not a same-account security boundary.
    // No file reads, authority evaluation, diagnostic JSON, or validation flags.
    public sealed partial class LocalGuiWriterRequest : IDisposable
    {
        private const int Magic = 0x4e475731, HeaderBytes = 64, RequestBytes = 16384;
        public const int MaximumCompletionBytes = 65536, MaximumMetadataCodeUnits = 16384;
        public const int MaximumWriters = 64, MaximumCompletionMilliseconds = 250;
        private static int attachAttempted;
        private bool relayMode;
        private MemoryMappedFile map;
        private MemoryMappedViewAccessor view;
        private Mutex gate;
        private EventWaitHandle closed;
        private ProcessWait workerExit;
        private string run, scenario, root;
        private int workerId, sealedState;
        private long workerCreation;
        public string Name { get; private set; }
        public Guid RequestId { get; private set; }
        private readonly LocalGuiWriterRequest original;
        private LocalGuiWriterRequest() { original = this; }

        public static LocalGuiWriterRequest TryCreate(Guid id, string run, string scenario, string artifactRoot)
        { return TryCreateCore(id, run, scenario, artifactRoot, false); }

        private static LocalGuiWriterRequest TryCreateCore(Guid id, string run, string scenario, string artifactRoot, bool relay)
        {
            var request = new LocalGuiWriterRequest();
            try
            {
                if (id == Guid.Empty || !TextValid(run, 1024) || !TextValid(scenario, 1024) || !CanonicalPath(artifactRoot)) return null;
                request.RequestId = id; request.run = run; request.scenario = scenario; request.root = artifactRoot;
                request.relayMode = relay;
                byte[] body;
                using (var bytes = new MemoryStream())
                using (var writer = new BinaryWriter(bytes, new UTF8Encoding(false, true)))
                { writer.Write(run); writer.Write(scenario); writer.Write(artifactRoot); writer.Flush(); body = bytes.ToArray(); }
                if (body.Length > RequestBytes) return null;
                request.Name = (relay ? "Local\\nlink-gui-relay-" : "Local\\nlink-gui-writer-") + Guid.NewGuid().ToString("N");
                request.map = MemoryMappedFile.CreateNew(request.Name, HeaderBytes + RequestBytes + MaximumCompletionBytes);
                request.view = request.map.CreateViewAccessor();
                request.gate = new Mutex(false, request.Name + ".gate");
                request.closed = new EventWaitHandle(false, EventResetMode.ManualReset, request.Name + ".closed");
                using (var current = Process.GetCurrentProcess())
                { request.view.Write(4, current.Id); request.view.Write(8, current.StartTime.ToUniversalTime().ToFileTimeUtc()); }
                request.view.Write(0, relay ? RelayMagic : Magic); request.view.Write(20, body.Length);
                request.view.WriteArray(24, id.ToByteArray(), 0, 16);
                request.view.WriteArray(HeaderBytes, body, 0, body.Length);
                var completed = request; request = null; return completed;
            }
            catch { return null; }
            finally { if (request != null) request.Dispose(); }
        }

        public bool BindWorker(Process process)
        {
            if (!Object.ReferenceEquals(original, this)) return false;
            bool held = false;
            try
            {
                if (Volatile.Read(ref sealedState) != 0 || workerExit != null || process == null || process.HasExited) return false;
                held = gate.WaitOne(0);
                if (!held || closed.WaitOne(0) || workerExit != null) return false;
                SafeWaitHandle handle;
                if (!DuplicateHandle(GetCurrentProcess(), process.Handle, GetCurrentProcess(), out handle, 0, false, 2)) return false;
                workerExit = new ProcessWait(handle);
                long exit, kernel, user;
                if (workerExit.WaitOne(0) || !GetProcessTimes(handle, out workerCreation, out exit, out kernel, out user)) return false;
                workerId = process.Id;
                view.Write(40, workerCreation); view.Write(16, workerId);
                return true;
            }
            catch (AbandonedMutexException) { held = true; return false; }
            catch { return false; }
            finally { Release(gate, held); }
        }

        // Only the actual GUI finalizer calls this in production. Keep the
        // returned issuer in its private local scope, never in formatter inputs.
        public static Finalizer TryAttach(string name, Guid id, string run, string scenario, string artifactRoot)
        { return TryAttachCore(name, id, run, scenario, artifactRoot, false); }

        private static Finalizer TryAttachCore(string name, Guid id, string run, string scenario, string artifactRoot, bool relay)
        {
            string pattern = relay ? @"\ALocal\\nlink-gui-relay-[0-9a-f]{32}\z" : @"\ALocal\\nlink-gui-writer-[0-9a-f]{32}\z";
            if (!TextValid(name, 128) || !Regex.IsMatch(name, pattern) || id == Guid.Empty ||
                (relay ? Interlocked.Exchange(ref relayAttachAttempted, 1) : Interlocked.Exchange(ref attachAttempted, 1)) != 0) return null;
            var owner = new Finalizer();
            bool held = false;
            try
            {
                owner.gate = Mutex.OpenExisting(name + ".gate");
                owner.closed = EventWaitHandle.OpenExisting(name + ".closed");
                if (owner.closed.WaitOne(0)) return null;
                held = owner.gate.WaitOne(0);
                if (!held) return null;
                owner.map = MemoryMappedFile.OpenExisting(name);
                owner.view = owner.map.CreateViewAccessor();
                var view = owner.view;
                if (view.ReadInt32(0) != (relay ? RelayMagic : Magic) || ReadId(view) != id || view.ReadInt32(48) != 0 ||
                    view.ReadInt32(16) != GetCurrentProcessId()) return null;
                using (var current = Process.GetCurrentProcess())
                    if (current.StartTime.ToUniversalTime().ToFileTimeUtc() != view.ReadInt64(40)) return null;
                int length = view.ReadInt32(20);
                if (length < 1 || length > RequestBytes) return null;
                var body = new byte[length];
                if (view.ReadArray(HeaderBytes, body, 0, length) != length) return null;
                using (var bytes = new MemoryStream(body, false))
                using (var reader = new BinaryReader(bytes, new UTF8Encoding(false, true)))
                {
                    if (reader.ReadString() != run || reader.ReadString() != scenario || reader.ReadString() != artifactRoot ||
                        bytes.Position != bytes.Length || !TextValid(run, 1024) || !TextValid(scenario, 1024) || !CanonicalPath(artifactRoot)) return null;
                }
                var parent = OpenProcess(0x00101000, false, view.ReadInt32(4));
                if (parent.IsInvalid) { parent.Dispose(); return null; }
                owner.parentExit = new ProcessWait(parent);
                long creation, exit, kernel, user;
                if (!GetProcessTimes(parent, out creation, out exit, out kernel, out user) || creation != view.ReadInt64(8) || !owner.IsCurrent) return null;
                owner.root = artifactRoot; owner.run = run; owner.scenario = scenario; owner.id = id; owner.endpointName = name;
                owner.relayMode = relay;
                view.Write(48, 1);
                Release(owner.gate, held); held = false;
                var completed = owner; owner = null; return completed;
            }
            catch (AbandonedMutexException) { held = true; return null; }
            catch { return null; }
            finally { if (owner != null) { Release(owner.gate, held); owner.Dispose(); } }
        }

        // The parent calls only after its original successful exit/output/state
        // persistence path. A held kernel process handle independently gates exit.
        public Completion TakeAfterExit()
        {
            if (!Object.ReferenceEquals(original, this)) return null;
            byte[] body = null; Completion result = null;
            try
            {
                BeginFrontierCustodyTake();
                body = TakeBodyAfterExit(false);
                if (body == null) return null;
                result = Decode(body, RequestId, run, scenario, root, workerId, workerCreation);
                if (result != null) result.MarkAcceptedHere();
                return result;
            }
            catch { result = null; return null; }
            finally { FinishFrontierCustody(body, result); }
        }

        private byte[] TakeBodyAfterExit(bool relay, bool failedDiagnostic = false)
        {
            if (!Object.ReferenceEquals(original, this)) return null;
            if (Interlocked.Exchange(ref sealedState, 1) != 0) return null;
            RetireFrontierRelayRegistration();
            if (relay) RetireFrontierCustody();
            RetireFrontierParentRegistration();
            bool held = false;
            try
            {
                closed.Set();
                int code;
                if (relayMode != relay || workerExit == null || !workerExit.WaitOne(0) || !GetExitCodeProcess(workerExit.SafeWaitHandle, out code) ||
                    (failedDiagnostic ? code == 0 : code != 0)) return null;
                held = gate.WaitOne(0);
                if (!held || view.ReadInt32(0) != (relay ? RelayMagic : Magic) || ReadId(view) != RequestId || view.ReadInt32(48) != 2 ||
                    view.ReadInt32(16) != workerId || view.ReadInt64(40) != workerCreation) return null;
                int length = view.ReadInt32(52);
                if (length < 1 || length > MaximumCompletionBytes) return null;
                var body = new byte[length];
                if (view.ReadArray(HeaderBytes + RequestBytes, body, 0, length) != length) return null;
                return body;
            }
            catch (AbandonedMutexException) { held = true; return null; }
            catch { return null; }
            finally { Release(gate, held); }
        }

        public void Dispose()
        {
            if (!Object.ReferenceEquals(original, this)) return;
            Interlocked.Exchange(ref sealedState, 1);
            RetireFrontierRelayRegistration();
            RetireFrontierRelayCustody();
            CloseFrontierRelayStart();
            lock (frontierParentSync)
            { if (frontierGuiStart != null) { frontierGuiStart.Dispose(); frontierGuiStart = null; } }
            RetireFrontierCustody();
            RetireFrontierParentRegistration();
            if (closed != null) { closed.Set(); closed.Dispose(); closed = null; }
            if (view != null) { view.Dispose(); view = null; }
            if (map != null) { map.Dispose(); map = null; }
            if (gate != null) { gate.Dispose(); gate = null; }
            if (workerExit != null) { workerExit.Dispose(); workerExit = null; }
        }

        public sealed partial class Finalizer : IDisposable
        {
            internal MemoryMappedFile map;
            internal MemoryMappedViewAccessor view;
            internal Mutex gate;
            internal EventWaitHandle closed;
            internal ProcessWait parentExit;
            internal string root, run, scenario, endpointName;
            internal Guid id;
            internal bool relayMode;
            private int attempted, disposed;
            private readonly Finalizer original;
            internal Finalizer() { original = this; }
            internal bool IsCurrent { get { return Volatile.Read(ref disposed) == 0 && parentExit != null && !parentExit.WaitOne(0) && !closed.WaitOne(0); } }

            // This is a capability method, not a public success-verdict factory.
            // The sole production owner supplies captured scalars only after its
            // existing complete validation/copy/persistence path succeeds.
            public bool Complete(string ledgerId, string sourcePath, long frozenLines, long bookmark,
                string[] hashes, string[] identities, long[] byteCounts, long[] lineCounts,
                string[] writerIds, long[] lastSequences)
            {
                if (!Object.ReferenceEquals(original, this) || Interlocked.Exchange(ref attempted, 1) != 0) return false;
                RetireFrontierBinding();
                bool held = false;
                try
                {
                    var clock = Stopwatch.StartNew();
                    if (relayMode || !IsCurrent || !TextValid(ledgerId, 1024) || !CanonicalPath(sourcePath) || frozenLines < 1 || bookmark < 0 || bookmark > frozenLines ||
                        hashes == null || hashes.Length != 3 || identities == null || identities.Length != 3 ||
                        byteCounts == null || byteCounts.Length != 3 || lineCounts == null || lineCounts.Length != 3 ||
                        writerIds == null || writerIds.Length < 1 || writerIds.Length > MaximumWriters || lastSequences == null || lastSequences.Length != writerIds.Length) return false;
                    int metadata = root.Length + run.Length + scenario.Length + ledgerId.Length + sourcePath.Length;
                    var unique = new HashSet<string>(StringComparer.Ordinal);
                    for (int i = 0; i < 3; i++)
                    {
                        if (hashes[i] == null || !Regex.IsMatch(hashes[i], @"\A[0-9a-f]{64}\z") || identities[i] == null ||
                            !Regex.IsMatch(identities[i], @"\A[0-9a-f]{8}:[0-9a-f]{16}\z") || byteCounts[i] < 0 || lineCounts[i] < 0) return false;
                        metadata += hashes[i].Length + identities[i].Length;
                    }
                    for (int i = 0; i < writerIds.Length; i++)
                    {
                        if (!TextValid(writerIds[i], 1024) || lastSequences[i] < 1 || !unique.Add(writerIds[i])) return false;
                        metadata += writerIds[i].Length;
                    }
                    if (metadata > MaximumMetadataCodeUnits) return false;
                    byte[] body;
                    using (var bytes = new MemoryStream())
                    using (var writer = new BinaryWriter(bytes, new UTF8Encoding(false, true)))
                    {
                        writer.Write(ledgerId); writer.Write(sourcePath); writer.Write(frozenLines); writer.Write(bookmark);
                        for (int i = 0; i < 3; i++) { writer.Write(hashes[i]); writer.Write(identities[i]); writer.Write(byteCounts[i]); writer.Write(lineCounts[i]); }
                        writer.Write(writerIds.Length);
                        for (int i = 0; i < writerIds.Length; i++) { writer.Write(writerIds[i]); writer.Write(lastSequences[i]); }
                        writer.Flush(); body = bytes.ToArray();
                    }
                    if (body.Length > MaximumCompletionBytes || clock.ElapsedMilliseconds > MaximumCompletionMilliseconds) return false;
                    held = gate.WaitOne(0);
                    if (!held || !IsCurrent || view.ReadInt32(48) != 1 || ReadId(view) != id) return false;
                    view.Write(52, body.Length); view.WriteArray(HeaderBytes + RequestBytes, body, 0, body.Length);
                    if (!IsCurrent || clock.ElapsedMilliseconds > MaximumCompletionMilliseconds) return false;
                    view.Write(48, 2);
                    return true;
                }
                catch (AbandonedMutexException) { held = true; return false; }
                catch { return false; }
                finally { Release(gate, held); }
            }

            public void Dispose()
            {
                if (!Object.ReferenceEquals(original, this) || Interlocked.Exchange(ref disposed, 1) != 0) return;
                RetireFrontierBinding();
                if (view != null) view.Dispose();
                if (map != null) map.Dispose();
                if (gate != null) gate.Dispose();
                if (closed != null) closed.Dispose();
                if (parentExit != null) parentExit.Dispose();
            }
        }

        public sealed class Completion
        {
            private byte[] encodedBody;
            private int acceptedByProcess;
            private long acceptedByCreation;
            public Guid RequestId { get; internal set; }
            public string AcceptanceRunId { get; internal set; }
            public string ScenarioRunId { get; internal set; }
            public string ArtifactRoot { get; internal set; }
            public int GuiProcessId { get; internal set; }
            public long GuiProcessCreationFileTime { get; internal set; }
            public string LedgerId { get; internal set; }
            public string SourcePath { get; internal set; }
            public long FrozenLineCount { get; internal set; }
            public long Bookmark { get; internal set; }
            public ReadOnlyCollection<Output> Outputs { get; internal set; }
            public ReadOnlyCollection<Writer> Writers { get; internal set; }
            internal Completion() { }

            internal void MarkAcceptedHere()
            {
                using (var current = Process.GetCurrentProcess())
                { acceptedByProcess = current.Id; acceptedByCreation = current.StartTime.ToUniversalTime().ToFileTimeUtc(); }
            }
            internal bool WasAcceptedHere()
            {
                using (var current = Process.GetCurrentProcess())
                    return acceptedByProcess == current.Id && acceptedByCreation == current.StartTime.ToUniversalTime().ToFileTimeUtc();
            }

            internal bool MatchesSource(string run, string scenario, string path, string sha256, long bytes, long lines)
            {
                return AcceptanceRunId == run && ScenarioRunId == scenario && Outputs != null && Outputs.Count == 4 &&
                    Outputs[3].Path == path && Outputs[3].Sha256 == sha256 && Outputs[3].ByteCount == bytes && Outputs[3].LineCount == lines;
            }

            // Reuse the already bounded original record, not diagnostic JSON or
            // a caller-supplied re-creation. Accessible only to the request codec.
            internal void WriteTo(BinaryWriter writer)
            {
                writer.Write(RequestId.ToByteArray()); writer.Write(GuiProcessId); writer.Write(GuiProcessCreationFileTime);
                writer.Write(ArtifactRoot); writer.Write(encodedBody.Length); writer.Write(encodedBody);
            }

            internal static Completion ReadFrom(BinaryReader reader, string run, string scenario)
            {
                var id = new Guid(reader.ReadBytes(16)); int process = reader.ReadInt32(); long creation = reader.ReadInt64();
                string root = reader.ReadString(); int length = reader.ReadInt32();
                if (id == Guid.Empty || process < 1 || creation < 1 || !CanonicalPath(root) || length < 1 || length > MaximumCompletionBytes) return null;
                var body = reader.ReadBytes(length);
                return body.Length == length ? Decode(body, id, run, scenario, root, process, creation) : null;
            }

            internal void RetainEncodedBody(byte[] body) { encodedBody = body; }
        }
        public sealed class Output
        {
            public string Path { get; private set; }
            public string Sha256 { get; private set; }
            public string OriginalFileIdentity { get; private set; }
            public long ByteCount { get; private set; }
            public long LineCount { get; private set; }
            internal Output(string path, string sha, string identity, long bytes, long lines)
            { Path = path; Sha256 = sha; OriginalFileIdentity = identity; ByteCount = bytes; LineCount = lines; }
        }
        public sealed class Writer
        {
            public string WriterId { get; private set; }
            public long LastSequence { get; private set; }
            internal Writer(string id, long last) { WriterId = id; LastSequence = last; }
        }

        private static Completion Decode(byte[] body, Guid id, string run, string scenario, string root, int processId, long creation)
        {
            var clock = Stopwatch.StartNew();
            var result = new Completion { RequestId = id, AcceptanceRunId = run, ScenarioRunId = scenario, ArtifactRoot = root,
                GuiProcessId = processId, GuiProcessCreationFileTime = creation };
            using (var bytes = new MemoryStream(body, false))
            using (var reader = new BinaryReader(bytes, new UTF8Encoding(false, true)))
            {
                result.LedgerId = reader.ReadString(); result.SourcePath = reader.ReadString(); result.FrozenLineCount = reader.ReadInt64(); result.Bookmark = reader.ReadInt64();
                if (!TextValid(result.LedgerId, 1024) || !CanonicalPath(result.SourcePath) || result.FrozenLineCount < 1 || result.Bookmark < 0 || result.Bookmark > result.FrozenLineCount) return null;
                int metadata = root.Length + run.Length + scenario.Length + result.LedgerId.Length + result.SourcePath.Length;
                var names = new[] { "filetransfer-gui-log-ledger-slice.log", "filetransfer-gui-log-writer-evidence-slice.log", "filetransfer-gui-semantic-evidence-slice.log" };
                var outputs = new List<Output>(4);
                for (int i = 0; i < 3; i++)
                {
                    string hash = reader.ReadString(), identity = reader.ReadString(); long count = reader.ReadInt64(), lines = reader.ReadInt64();
                    if (!Regex.IsMatch(hash, @"\A[0-9a-f]{64}\z") || !Regex.IsMatch(identity, @"\A[0-9a-f]{8}:[0-9a-f]{16}\z") || count < 0 || lines < 0) return null;
                    metadata += hash.Length + identity.Length;
                    outputs.Add(new Output(Path.Combine(root, names[i]), hash, identity, count, lines));
                }
                // The finalizer verified the analyzer copy against native semantic
                // bytes. Its new file identity was not captured by that writer.
                var semantic = outputs[2];
                outputs.Add(new Output(Path.Combine(root, "filetransfer-retained-log-slice.log"), semantic.Sha256, null, semantic.ByteCount, semantic.LineCount));
                int writerCount = reader.ReadInt32();
                if (writerCount < 1 || writerCount > MaximumWriters) return null;
                var writers = new List<Writer>(writerCount); var unique = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < writerCount; i++)
                {
                    string writerId = reader.ReadString(); long last = reader.ReadInt64();
                    if (!TextValid(writerId, 1024) || last < 1 || !unique.Add(writerId)) return null;
                    metadata += writerId.Length; writers.Add(new Writer(writerId, last));
                }
                if (bytes.Position != bytes.Length || metadata > MaximumMetadataCodeUnits || clock.ElapsedMilliseconds > MaximumCompletionMilliseconds) return null;
                result.Outputs = outputs.AsReadOnly(); result.Writers = writers.AsReadOnly();
                result.RetainEncodedBody(body); return result;
            }
        }

        private static bool TextValid(string value, int limit)
        { return !String.IsNullOrWhiteSpace(value) && value != "(none)" && value.Length <= limit && value.IndexOf('\0') < 0; }
        private static bool CanonicalPath(string path)
        { return TextValid(path, 4096) && Path.GetFullPath(path) == path; }
        private static Guid ReadId(MemoryMappedViewAccessor view)
        { var bytes = new byte[16]; return view.ReadArray(24, bytes, 0, 16) == 16 ? new Guid(bytes) : Guid.Empty; }
        private static void Release(Mutex gate, bool held)
        { if (held && gate != null) { try { gate.ReleaseMutex(); } catch (ApplicationException) { } catch (ObjectDisposedException) { } } }
        internal sealed class ProcessWait : WaitHandle { internal ProcessWait(SafeWaitHandle handle) { SafeWaitHandle = handle; } }
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern int GetCurrentProcessId();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DuplicateHandle(IntPtr source, IntPtr handle, IntPtr target, out SafeWaitHandle duplicate, uint access, bool inherit, uint options);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeWaitHandle OpenProcess(uint access, bool inherit, int id);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeWaitHandle handle, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeWaitHandle handle, out int code);
    }
}
