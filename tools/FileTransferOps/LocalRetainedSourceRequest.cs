using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Management.Automation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace NLink.Tools
{
    // Source ownership with an optional accepted GUI writer context. Neither
    // metadata association nor writer verification grants evaluator authority.
    public sealed partial class LocalRetainedSourceRequest : IDisposable
    {
        private const int Magic = 0x4e525331;
        private const int WriterMagic = 0x4e525332;
        private const int PublicationMagic = 0x4e525333;
        private const int HeaderBytes = 64;
        public const int MaximumRequestBytes = 16384;
        public const int MaximumWriterRequestBytes = 98304;
        public const long MaximumSourceBytes = LocalPublicationInputLimits.FileBytes;
        public const long MaximumSourceLines = LocalPublicationInputLimits.InputRecords;
        private static int attachAttempted;
        private FileStream frozenSource;
        private MemoryMappedFile map;
        private MemoryMappedViewAccessor view;
        private Mutex gate;
        private EventWaitHandle closed;
        private ProcessWait workerExit;
        private int workerId;
        private long workerCreation;
        private int disposed;
        public string Name { get; private set; }
        public Guid RequestId { get; private set; }

        private LocalRetainedSourceRequest() { frontierOriginal = this; }

        // The lease owns the parent's actual primary hash pass. Keep a separate
        // read-only handle; never accept a caller-declared digest as that proof.
        public static LocalRetainedSourceRequest TryCreate(Guid attempt, string owner, string run, string scenario,
            LocalInputReadLease lease, long lines)
        { return TryCreateCore(attempt, owner, run, scenario, lease, lines, null); }

        public static LocalRetainedSourceRequest TryCreateWithWriter(Guid attempt, string owner, string run, string scenario,
            LocalInputReadLease lease, long lines, LocalGuiWriterRequest.Completion writer)
        {
            if (lease == null || !lease.IsVerified || writer == null ||
                !writer.MatchesSource(run, scenario, lease.Path, lease.ExpectedSha256, lease.ByteCount, lines)) return null;
            return TryCreateCore(attempt, owner, run, scenario, lease, lines, writer);
        }

        private static LocalRetainedSourceRequest TryCreateCore(Guid attempt, string owner, string run, string scenario,
            LocalInputReadLease lease, long lines, LocalGuiWriterRequest.Completion writerContext, bool publicationRequested = false)
        {
            var request = new LocalRetainedSourceRequest();
            try
            {
                if (lease == null || !lease.IsVerified) return null;
                string path = lease.Path, sha256 = lease.ExpectedSha256;
                request.frozenSource = lease.TryOpenVerifiedRead();
                FileStream ownedSource = request.frozenSource;
                if (attempt == Guid.Empty || (owner != "RouteAcceptance" && owner != "Wrapper") ||
                    !TextValid(run, 1024) || !TextValid(scenario, 1024) || !TextValid(path, 4096) ||
                    Path.GetFullPath(path) != path || sha256 == null || !Regex.IsMatch(sha256, @"\A[0-9a-f]{64}\z") ||
                    lines < 1 || lines > MaximumSourceLines || ownedSource == null || !ownedSource.CanRead || ownedSource.CanWrite ||
                    ownedSource.Length > MaximumSourceBytes) return null;
                string identity = FileIdentity(ownedSource.SafeFileHandle);
                if (identity == null || identity != lease.FileIdentity || ownedSource.Length != lease.ByteCount || !lease.IsVerified) return null;
                byte[] body;
                using (var bytes = new MemoryStream())
                using (var writer = new BinaryWriter(bytes, new UTF8Encoding(false, true)))
                {
                    writer.Write(owner); writer.Write(run); writer.Write(scenario); writer.Write(path);
                    writer.Write(sha256); writer.Write(identity); writer.Write(ownedSource.Length); writer.Write(lines);
                    if (writerContext != null) writerContext.WriteTo(writer);
                    writer.Flush(); body = bytes.ToArray();
                }
                int maximumBytes = writerContext == null ? MaximumRequestBytes : MaximumWriterRequestBytes;
                if (body.Length > maximumBytes) return null;
                request.Name = "Local\\nlink-retained-source-" + Guid.NewGuid().ToString("N");
                request.RequestId = attempt;
                if (publicationRequested)
                {
                    if (writerContext == null) return null;
                    request.publicationChannel = PublicationChannel.Create(request.Name + ".publication");
                    var codec = new PublicationCodec(2000, () => lease.IsVerified);
                    string source = PublicationSource(codec, path, sha256, identity, ownedSource.Length, lines);
                    string writer = PublicationWriter(codec, writerContext);
                    request.publicationRequestHash = codec.Hash(PublicationRequest(codec, attempt, owner, run, scenario,
                        source, writer, request.Name + ".publication"));
                    request.parentPublicationSource = new ParentPublicationSource(owner, run, scenario, path, sha256, lines);
                }
                request.map = MemoryMappedFile.CreateNew(request.Name, HeaderBytes + maximumBytes);
                request.view = request.map.CreateViewAccessor();
                request.gate = new Mutex(false, request.Name + ".gate");
                request.closed = new EventWaitHandle(false, EventResetMode.ManualReset, request.Name + ".closed");
                using (var current = Process.GetCurrentProcess())
                {
                    request.view.Write(0, publicationRequested ? PublicationMagic : writerContext == null ? Magic : WriterMagic); request.view.Write(4, current.Id);
                    request.view.Write(8, current.StartTime.ToUniversalTime().ToFileTimeUtc());
                }
                request.view.Write(20, body.Length);
                request.view.WriteArray(24, attempt.ToByteArray(), 0, 16);
                request.view.WriteArray(HeaderBytes, body, 0, body.Length);
                request.RememberFrontierSource(body, writerContext);
                if (!lease.IsVerified) return null;
                var completed = request; request = null; return completed;
            }
            catch { return null; }
            finally { if (request != null) request.Dispose(); }
        }

        public bool BindWorker(Process process)
        {
            if (!Object.ReferenceEquals(frontierOriginal, this)) return false;
            bool held = false;
            try
            {
                if (Volatile.Read(ref disposed) != 0 || workerExit != null || process == null || process.HasExited) return false;
                held = gate.WaitOne(0);
                if (!held || closed.WaitOne(0) || workerExit != null) return false;
                SafeWaitHandle handle;
                if (!DuplicateHandle(GetCurrentProcess(), process.Handle, GetCurrentProcess(), out handle, 0, false, 2)) return false;
                workerExit = new ProcessWait(handle);
                long creation, exit, kernel, user;
                if (workerExit.WaitOne(0) || !GetProcessTimes(handle, out creation, out exit, out kernel, out user)) return false;
                view.Write(40, creation);
                view.Write(16, process.Id);
                workerId = process.Id; workerCreation = creation;
                WatchFrontierWorker();
                return true;
            }
            catch (AbandonedMutexException) { held = true; return false; }
            catch { return false; }
            finally { Release(gate, held); }
        }

        public static Worker TryAttach(string name, Guid requestId, string path, string run, string scenario)
        { return TryAttachCore(name, requestId, path, run, scenario, false); }

        private static Worker TryAttachCore(string name, Guid requestId, string path, string run, string scenario, bool frontier, bool waitForFrontierStart = false)
        {
            if (!TextValid(name, 128) || !Regex.IsMatch(name, @"\ALocal\\nlink-retained-source-[0-9a-f]{32}\z") ||
                requestId == Guid.Empty || Interlocked.Exchange(ref attachAttempted, 1) != 0) return null;
            if (waitForFrontierStart && !WaitForFrontierStart(name, requestId)) return null;
            Worker worker = new Worker();
            bool held = false;
            Mutex gate = null;
            try
            {
                gate = Mutex.OpenExisting(name + ".gate");
                worker.closed = EventWaitHandle.OpenExisting(name + ".closed");
                if (worker.closed.WaitOne(0)) return null;
                held = gate.WaitOne(0);
                if (!held) return null;
                using (var map = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read))
                using (var view = map.CreateViewAccessor(0, HeaderBytes + MaximumRequestBytes, MemoryMappedFileAccess.Read))
                {
                    var id = new byte[16];
                    int magic = view.ReadInt32(0);
                    if (frontier && (magic != WriterMagic || view.ReadInt32(4) == view.ReadInt32(16))) return null;
                    int maximumBytes = magic == WriterMagic || magic == PublicationMagic ? MaximumWriterRequestBytes : MaximumRequestBytes;
                    if ((magic != Magic && magic != WriterMagic && magic != PublicationMagic) ||
                        view.ReadInt32(16) != GetCurrentProcessId() ||
                        view.ReadArray(24, id, 0, 16) != 16 || new Guid(id) != requestId) return null;
                    using (var current = Process.GetCurrentProcess())
                    {
                        if (current.StartTime.ToUniversalTime().ToFileTimeUtc() != view.ReadInt64(40)) return null;
                        worker.ProcessId = current.Id;
                        worker.ProcessCreationFileTime = view.ReadInt64(40);
                    }
                    int length = view.ReadInt32(20);
                    if (length < 1 || length > maximumBytes) return null;
                    var body = new byte[length];
                    using (var bodyView = map.CreateViewAccessor(HeaderBytes, length, MemoryMappedFileAccess.Read))
                        if (bodyView.ReadArray(0, body, 0, length) != length) return null;
                    using (var bytes = new MemoryStream(body, false))
                    using (var reader = new BinaryReader(bytes, new UTF8Encoding(false, true)))
                    {
                        worker.Owner = reader.ReadString(); worker.AcceptanceRunId = reader.ReadString();
                        worker.ScenarioRunId = reader.ReadString(); worker.SourcePath = reader.ReadString();
                        worker.SourceSha256 = reader.ReadString(); worker.SourceIdentity = reader.ReadString();
                        worker.SourceBytes = reader.ReadInt64(); worker.SourceLines = reader.ReadInt64();
                        if (magic == WriterMagic || magic == PublicationMagic)
                        {
                            worker.WriterContext = LocalGuiWriterRequest.Completion.ReadFrom(reader, run, scenario);
                            if (worker.WriterContext == null || !worker.WriterContext.MatchesSource(run, scenario,
                                worker.SourcePath, worker.SourceSha256, worker.SourceBytes, worker.SourceLines)) return null;
                        }
                        if (bytes.Position != bytes.Length) return null;
                    }
                    if ((worker.Owner != "RouteAcceptance" && worker.Owner != "Wrapper") || worker.SourcePath != path ||
                        worker.AcceptanceRunId != run || worker.ScenarioRunId != scenario ||
                        !TextValid(path, 4096) || Path.GetFullPath(path) != path || !TextValid(run, 1024) || !TextValid(scenario, 1024) ||
                        !Regex.IsMatch(worker.SourceSha256, @"\A[0-9a-f]{64}\z") ||
                        worker.SourceLines < 1 || worker.SourceLines > MaximumSourceLines ||
                        worker.SourceBytes < 0 || worker.SourceBytes > MaximumSourceBytes) return null;
                    var parent = OpenProcess(frontier ? 0x00101040U : 0x00101000U, false, view.ReadInt32(4));
                    if (parent.IsInvalid) { parent.Dispose(); return null; }
                    worker.parentExit = new ProcessWait(parent);
                    long creation, exit, kernel, user;
                    if (!GetProcessTimes(parent, out creation, out exit, out kernel, out user) || creation != view.ReadInt64(8)) return null;
                    worker.RequestId = requestId;
                    if (!worker.IsCurrent) return null;
                    if (magic == PublicationMagic) worker.OpenPublicationChannel(name + ".publication");
                    if (frontier && !worker.AttachFrontierInputs(name, map, body)) return null;
                }
                var completed = worker; worker = null; return completed;
            }
            catch (AbandonedMutexException) { held = true; return null; }
            catch { return null; }
            finally { Release(gate, held); if (gate != null) gate.Dispose(); if (worker != null) worker.Dispose(); }
        }

        public void Dispose()
        {
            if (!Object.ReferenceEquals(frontierOriginal, this)) return;
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            CloseFrontierInputs();
            if (frontierStart != null) frontierStart.Dispose();
            if (frontierDiagnosticReceiver != null) frontierDiagnosticReceiver.Close();
            if (frontierOutput != null) frontierOutput.Close();
            if (adoptionCustody != null) adoptionCustody.Dispose();
            if (publicationChannel != null) publicationChannel.Dispose();
            if (closed != null) { closed.Set(); closed.Dispose(); }
            if (view != null) view.Dispose();
            if (map != null) map.Dispose();
            if (gate != null) gate.Dispose();
            if (workerExit != null) workerExit.Dispose();
            if (frozenSource != null) frozenSource.Dispose();
        }

        public sealed partial class Worker : IDisposable
        {
            internal EventWaitHandle closed;
            internal ProcessWait parentExit;
            private int disposed;
            private int opened;
            private int writerOpened;
            private int primaryOpened;
            private SourceReader sourceReader;
            private WriterReader writerReader;
            private PrimaryEvaluationCustody primary;
            public Guid RequestId { get; internal set; }
            public string Owner { get; internal set; }
            public string AcceptanceRunId { get; internal set; }
            public string ScenarioRunId { get; internal set; }
            public string SourcePath { get; internal set; }
            public string SourceSha256 { get; internal set; }
            public string SourceIdentity { get; internal set; }
            public long SourceBytes { get; internal set; }
            public long SourceLines { get; internal set; }
            public int ProcessId { get; internal set; }
            public long ProcessCreationFileTime { get; internal set; }
            public LocalGuiWriterRequest.Completion WriterContext { get; internal set; }
            internal Worker() { frontierOriginal = this; }
            public bool IsCurrent
            {
                get { try { return Object.ReferenceEquals(frontierOriginal, this) && Volatile.Read(ref disposed) == 0 && parentExit != null && !parentExit.WaitOne(0) && !closed.WaitOne(0) && (!frontierRequested || FrontierWorkerCurrent); } catch { return false; } }
            }
            public SourceReader TryOpenSource()
            {
                if (frontierRequested) return TryOpenFrontierSource();
                if (!IsCurrent || Interlocked.Exchange(ref opened, 1) != 0) return null;
                FileStream stream = null;
                try
                {
                    stream = File.OpenRead(SourcePath);
                    if (stream.Length != SourceBytes || FileIdentity(stream.SafeFileHandle) != SourceIdentity || !IsCurrent) return null;
                    sourceReader = new SourceReader(this, stream); stream = null; return sourceReader;
                }
                catch { return null; }
                finally { if (stream != null) stream.Dispose(); }
            }
            public bool OwnsCompletedSource(SourceReader candidate)
            {
                // InputReadComplete already checks this worker's current lifetime.
                return Object.ReferenceEquals(frontierOriginal, this) && candidate != null &&
                    Object.ReferenceEquals(candidate, sourceReader) && candidate.InputReadComplete;
            }
            public LocalNativeInputCapture.NativeInputs CaptureNativeInputs(SourceReader source, LocalNativeInputCapture capture,
                string[] full, string[] compact, Stopwatch work)
            {
                if (frontierRequested || capture == null || work == null || !OwnsCompletedSource(source)) return null;
                return capture.CaptureInputs(new[] { full, full, full, compact },
                    () => work.ElapsedMilliseconds <= LocalPublicationInputLimits.ProducerMilliseconds && OwnsCompletedSource(source));
            }
            public bool ValidateInputReferences(SourceReader source, string[] full, string[] fullOriginal,
                string[] compact, string[] compactOriginal, Stopwatch work)
            {
                if (frontierRequested || work == null || full == null || compact == null || fullOriginal == null || compactOriginal == null ||
                    full.Length > LocalPublicationInputLimits.InputRecords || compact.Length > full.Length ||
                    full.Length != fullOriginal.Length || compact.Length != compactOriginal.Length) return false;
                for (int role = 0; role < 2; role++)
                {
                    var lines = role == 0 ? full : compact;
                    var originals = role == 0 ? fullOriginal : compactOriginal;
                    for (int i = 0; i < lines.Length; i++)
                        if (work.ElapsedMilliseconds > LocalPublicationInputLimits.ProducerMilliseconds || !OwnsCompletedSource(source) ||
                            !Object.ReferenceEquals(lines[i], originals[i])) return false;
                }
                return work.ElapsedMilliseconds <= LocalPublicationInputLimits.ProducerMilliseconds && OwnsCompletedSource(source);
            }
            // Input provenance only, never completed-view or route authority.
            // Preserve the original PowerShell comparisons without a per-record
            // interpreter call to the witness's ownership/work check.
            public bool ValidateFullInputOrigins(SourceReader source, LocalNativeInputCapture capture,
                LocalNativeInputCapture.NativeInputs inputs, object[] events, string[] full,
                string[] fullOriginal, int[] origins, Stopwatch work)
            {
                if (frontierRequested || capture == null || inputs == null || work == null || events == null || full == null ||
                    fullOriginal == null || origins == null || full.Length > LocalPublicationInputLimits.InputRecords ||
                    events.Length != full.Length || fullOriginal.Length != full.Length || origins.Length != full.Length) return false;
                try
                {
                    int previous = 0;
                    for (int i = 0; i < full.Length; i++)
                    {
                        if (work.ElapsedMilliseconds > LocalPublicationInputLimits.ProducerMilliseconds ||
                            !OwnsCompletedSource(source) || !capture.Owns(inputs)) return false;
                        var item = PSObject.AsPSObject(events[i]);
                        int physical = origins[i];
                        if (!Object.ReferenceEquals(full[i], fullOriginal[i]) ||
                            !Object.ReferenceEquals(full[i], item.Properties["RawLine"].Value) ||
                            !LanguagePrimitives.Equals(item.Properties["FilePath"].Value, SourcePath, false, System.Globalization.CultureInfo.InvariantCulture) ||
                            !LanguagePrimitives.Equals(item.Properties["LineNumber"].Value, physical, false, System.Globalization.CultureInfo.InvariantCulture) ||
                            !LanguagePrimitives.Equals(item.Properties["Sequence"].Value, physical, false, System.Globalization.CultureInfo.InvariantCulture) ||
                            physical <= previous || physical > SourceLines) return false;
                        previous = physical;
                    }
                    return work.ElapsedMilliseconds <= LocalPublicationInputLimits.ProducerMilliseconds &&
                        OwnsCompletedSource(source) && capture.Owns(inputs);
                }
                catch { return false; }
            }
            // Claimed once by the primary, before either original evaluation.
            // The returned capability must never be stored in the mutable input
            // context or passed to an evaluator, resolver or formatter helper.
            public PrimaryEvaluationCustody TryBeginPrimary(SourceReader source, object context, string[] full, string[] compact)
            {
                if (frontierRequested) return null; // Native array/view ownership is a separate gate.
                if (Interlocked.Exchange(ref primaryOpened, 1) != 0 || !OwnsCompletedSource(source) || WriterContext == null ||
                    context == null || full == null || compact == null || compact.Length > full.Length ||
                    full.Length > LocalPublicationInputLimits.InputRecords ||
                    7L * full.Length + 4L * compact.Length > LocalPublicationInputLimits.ProducerRecords) return null;
                primary = new PrimaryEvaluationCustody(this, source, context, full, compact);
                if (!OwnsCompletedSource(source)) { primary.Dispose(); return null; }
                return primary;
            }
            // No pre-launch writer hash. This candidate is pinned by identity and
            // length; its actual bytes remain unverified until admitted EOF.
            public WriterReader TryOpenWriter()
            {
                if (frontierRequested) return null; // Never reopen a legacy writer path for a native worker.
                if (Interlocked.Exchange(ref writerOpened, 1) != 0 || !IsCurrent || WriterContext == null ||
                    sourceReader == null || !sourceReader.InputReadComplete) return null;
                LocalInputReadLease lease = null;
                try
                {
                    var output = WriterContext.Outputs[1];
                    lease = LocalInputReadLease.OpenExpected(output.Path, output.Sha256, output.LineCount);
                    if (lease == null || lease.FileIdentity != output.OriginalFileIdentity || lease.ByteCount != output.ByteCount || !IsCurrent) return null;
                    writerReader = new WriterReader(this, sourceReader, lease); lease = null; return writerReader;
                }
                catch { return null; }
                finally { if (lease != null) lease.Dispose(); }
            }
            internal bool OwnsVerifiedWriter(WriterReader candidate)
            { return candidate != null && Object.ReferenceEquals(candidate, writerReader) && candidate.IsVerified; }
            public void Dispose()
            {
                if (!Object.ReferenceEquals(frontierOriginal, this)) return;
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                CloseFrontierWorker();
                if (primary != null) primary.Dispose();
                ClosePublicationChannel();
                if (writerReader != null) writerReader.Dispose();
                if (closed != null) closed.Dispose();
                if (parentExit != null) parentExit.Dispose();
            }
        }

        // Invocation custody only, not publication completion or authority.
        // One primary thread owns recording/taking/disposal. Parent cancellation
        // is observed through the already-held source-worker lifetime.
        public sealed partial class PrimaryEvaluationCustody : IDisposable
        {
            private readonly Worker owner;
            private readonly SourceReader source;
            private object context, fullView, compactView;
            private string[] full, compact;
            private Guid fullId, compactId;
            private CompletedEvaluations issued;
            private int state; // 0 full, 1 compact, 2 take, 3 taken, 4 omitted/disposed
            internal PrimaryEvaluationCustody(Worker owner, SourceReader source, object context, string[] full, string[] compact)
            { this.owner = owner; this.source = source; this.context = context; this.full = full; this.compact = compact; }

            public bool RecordFullEvaluation(string[] lines, object view)
            {
                if (state != 0 || !owner.OwnsCompletedSource(source) || !Object.ReferenceEquals(full, lines) || view == null)
                    return Omit();
                fullView = view; fullId = Guid.NewGuid(); state = 1; return true;
            }

            public bool RecordCompactEvaluation(string[] lines, object view)
            {
                if (state != 1 || !owner.OwnsCompletedSource(source) || !Object.ReferenceEquals(compact, lines) || view == null)
                    return Omit();
                compactView = view; compactId = Guid.NewGuid(); state = 2; return true;
            }

            public CompletedEvaluations Take(object inputContext, string[] fullLines, string[] compactLines, object fullResult, object compactResult)
            {
                if (state == 3 || state == 4) return null;
                if (state != 2 || !Matches(inputContext, fullLines, compactLines, fullResult, compactResult))
                { Omit(); return null; }
                issued = new CompletedEvaluations(owner.RequestId, fullId, compactId);
                state = 3;
                if (!owner.OwnsCompletedSource(source)) { Omit(); return null; }
                return issued;
            }

            public bool Owns(CompletedEvaluations candidate, object inputContext, string[] fullLines, string[] compactLines,
                object fullResult, object compactResult)
            {
                return state == 3 && candidate != null && Object.ReferenceEquals(issued, candidate) &&
                    Matches(inputContext, fullLines, compactLines, fullResult, compactResult);
            }

            private bool Matches(object inputContext, string[] fullLines, string[] compactLines, object fullResult, object compactResult)
            {
                return owner.OwnsCompletedSource(source) && NativeCurrent() && Object.ReferenceEquals(context, inputContext) &&
                    Object.ReferenceEquals(full, fullLines) && Object.ReferenceEquals(compact, compactLines) &&
                    Object.ReferenceEquals(fullView, fullResult) && Object.ReferenceEquals(compactView, compactResult);
            }

            private bool Omit() { Dispose(); return false; }
            public void Dispose()
            {
                ClearPublication();
                nativeOwner = null; nativeInputs = null;
                state = 4; issued = null; context = null; full = null; compact = null; fullView = null; compactView = null;
            }
        }

        public sealed class CompletedEvaluations
        {
            public Guid RequestId { get; private set; }
            public Guid FullInvocationId { get; private set; }
            public Guid CompactInvocationId { get; private set; }
            internal CompletedEvaluations(Guid request, Guid full, Guid compact)
            { RequestId = request; FullInvocationId = full; CompactInvocationId = compact; }
        }

        public sealed class WriterReader : IDisposable
        {
            private readonly Worker owner;
            private readonly SourceReader source;
            private readonly LocalInputReadLease lease;
            private int disposed;
            internal WriterReader(Worker owner, SourceReader source, LocalInputReadLease lease)
            { this.owner = owner; this.source = source; this.lease = lease; }
            public bool IsVerified { get { return Volatile.Read(ref disposed) == 0 && owner.OwnsCompletedSource(source) && lease.IsVerified; } }
            public long LinesRead { get { return lease.VerificationRecordVisits; } }
            public IEnumerable<string> VerifyAndReadLines()
            {
                foreach (var line in lease.VerifyAndReadLines())
                {
                    if (!owner.OwnsCompletedSource(source)) { Dispose(); yield break; }
                    yield return line;
                }
            }
            public NativeMembership TryVerifyMembership(string[] full, string[] fullOriginal, string selectedWriter,
                long selectedSequence, long initialCodeUnits, Stopwatch work)
            {
                bool accepted = false;
                try
                {
                    if (full == null || fullOriginal == null || full.Length != fullOriginal.Length ||
                        full.Length > LocalPublicationInputLimits.InputRecords || work == null ||
                        initialCodeUnits < 0 || initialCodeUnits > LocalNativeInputCapture.MaximumCodeUnits ||
                        String.IsNullOrEmpty(selectedWriter) || selectedSequence < 1) return null;
                    long records = 0, units = initialCodeUnits;
                    Action<bool> check = checkOwner => {
                        if (Volatile.Read(ref disposed) != 0 || (checkOwner && !owner.OwnsCompletedSource(source)) ||
                            work.ElapsedMilliseconds > LocalPublicationInputLimits.ProducerMilliseconds)
                            throw new InvalidDataException("primary_witness_owner_or_budget");
                    };
                    Action<string, bool> visit = (line, checkOwner) => {
                        check(checkOwner);
                        if (line == null || line.Length > LocalPublicationInputLimits.LineCodeUnits ||
                            ++records > LocalPublicationInputLimits.ProducerRecords ||
                            line.Length > LocalPublicationInputLimits.ProducerCodeUnits - units)
                            throw new InvalidDataException("primary_witness_input_budget");
                        units += line.Length;
                    };
                    check(true);
                    var descriptor = owner.WriterContext.Outputs[1];
                    if (descriptor.LineCount < 1 || descriptor.LineCount > LocalPublicationInputLimits.InputRecords) return null;
                    var lines = new List<string>((int)descriptor.LineCount);
                    // The iterator just checked this exact source's owner for
                    // each yielded record. Keep local lifetime/work checks here
                    // without polling the same process handles again.
                    foreach (var line in VerifyAndReadLines()) { visit(line, false); lines.Add(line); }
                    if (!IsVerified || lines.Count != descriptor.LineCount) return null;
                    var clock = Stopwatch.StartNew();
                    var owners = new Dictionary<string, int>(StringComparer.Ordinal);
                    var writers = owner.WriterContext.Writers;
                    if (writers.Count < 1 || writers.Count > 64) return null;
                    var counts = new long[writers.Count]; var positions = new int[writers.Count][];
                    long total = 0;
                    for (int i = 0; i < writers.Count; i++)
                    {
                        check(true);
                        if (writers[i].LastSequence < 1 || writers[i].LastSequence > LocalPublicationInputLimits.InputRecords - total) return null;
                        total += writers[i].LastSequence;
                        owners.Add(writers[i].WriterId, i);
                    }
                    if (total != lines.Count) return null;
                    for (int i = 0; i < writers.Count; i++) positions[i] = new int[(int)writers[i].LastSequence];
                    for (int i = 0; i < lines.Count; i++)
                    {
                        visit(lines[i], true);
                        if (clock.ElapsedMilliseconds > LocalPublicationInputLimits.MembershipMilliseconds) return null;
                        var fields = MembershipFields(lines[i]); int writer; long sequence;
                        if (fields.ContainsKey("__duplicate") || !owners.TryGetValue(Field(fields, "log_writer_id") ?? "", out writer) ||
                            !Int64.TryParse(Field(fields, "log_writer_sequence"), out sequence) || sequence != ++counts[writer] ||
                            sequence > writers[writer].LastSequence || Field(fields, "log_writer_sequence") != sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)) return null;
                        if (sequence == 1 && (Field(fields, "event") != "operational_log_writer_started" ||
                            Field(fields, "acceptance_run_id") != owner.AcceptanceRunId || Field(fields, "scenario_run_id") != owner.ScenarioRunId)) return null;
                        if (sequence != 1 && Field(fields, "event") == "operational_log_writer_started") return null;
                        positions[writer][(int)sequence - 1] = i;
                    }
                    for (int i = 0; i < writers.Count; i++) if (counts[i] != writers[i].LastSequence) return null;
                    var seen = new bool[lines.Count]; int selected = -1;
                    for (int i = 0; i < full.Length; i++)
                    {
                        visit(full[i], true);
                        if (clock.ElapsedMilliseconds > LocalPublicationInputLimits.MembershipMilliseconds ||
                            !Object.ReferenceEquals(full[i], fullOriginal[i])) return null;
                        var fields = MembershipFields(full[i]); int writer; long sequence;
                        string id = Field(fields, "log_writer_id");
                        if (fields.ContainsKey("__duplicate") || !owners.TryGetValue(id ?? "", out writer) ||
                            !Int64.TryParse(Field(fields, "log_writer_sequence"), out sequence) || sequence < 1 ||
                            sequence > writers[writer].LastSequence || Field(fields, "log_writer_sequence") != sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)) return null;
                        int position = positions[writer][(int)sequence - 1];
                        if (seen[position] || !String.Equals(full[i], lines[position], StringComparison.Ordinal)) return null;
                        seen[position] = true;
                        if (id == selectedWriter && sequence == selectedSequence) selected = i;
                    }
                    check(true);
                    if (!IsVerified || selected < 0 || clock.ElapsedMilliseconds > LocalPublicationInputLimits.MembershipMilliseconds) return null;
                    accepted = true;
                    return new NativeMembership(selected, records, units);
                }
                catch { return null; }
                finally { if (!accepted) Dispose(); }
            }
            private static readonly Regex MembershipPrefix = new Regex(@"^(?:\[[^\]]+\]\s+)+(?<message>.*)$", RegexOptions.Compiled);
            private static readonly HashSet<string> MembershipKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "event", "acceptance_run_id", "scenario_run_id", "log_writer_id", "log_writer_sequence", "log_writer_batch_generation",
              "log_writer_batch_first_sequence", "log_writer_batch_last_sequence", "log_writer_attempt_utc_ticks" };
            private static Dictionary<string, string> MembershipFields(string line)
            {
                var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var prefix = MembershipPrefix.Match(line);
                int start = prefix.Success ? prefix.Groups["message"].Index : 0, segments = 0;
                while (start <= line.Length)
                {
                    if (++segments > 256) throw new InvalidDataException("primary_witness_field_budget");
                    int end = line.IndexOf(';', start); if (end < 0) end = line.Length;
                    int left = start, right = end;
                    while (left < right && Char.IsWhiteSpace(line[left])) left++;
                    while (right > left && Char.IsWhiteSpace(line[right - 1])) right--;
                    int separator = line.IndexOf('=', left, right - left);
                    if (separator > left)
                    {
                        string key = line.Substring(left, separator - left).Trim();
                        if (MembershipKeys.Contains(key))
                        {
                            if (fields.ContainsKey(key)) fields["__duplicate"] = "true";
                            else fields[key] = line.Substring(separator + 1, right - separator - 1).Trim();
                        }
                    }
                    if (end == line.Length) break;
                    start = end + 1;
                }
                return fields;
            }
            private static string Field(Dictionary<string, string> fields, string name)
            { string value; return fields.TryGetValue(name, out value) ? value : null; }
            public void Dispose()
            { if (Interlocked.Exchange(ref disposed, 1) == 0) lease.Dispose(); }
        }

        // Bounded membership facts only, never a publication/authority token.
        public sealed class NativeMembership
        {
            public int SelectedIndex { get; private set; }
            public long RecordVisits { get; private set; }
            public long CodeUnitVisits { get; private set; }
            internal NativeMembership(int selected, long records, long units)
            { SelectedIndex = selected; RecordVisits = records; CodeUnitVisits = units; }
        }

        public sealed partial class SourceReader : IDisposable
        {
            private readonly Worker worker;
            private StreamReader reader;
            private int started;
            private int disposed;
            private bool complete;
            public string SourcePath { get { return worker.SourcePath; } }
            public long LinesRead { get; private set; }
            public bool InputReadComplete { get { return frontier != null ? frontier.SourceComplete(this) : complete && Volatile.Read(ref disposed) == 0 && worker.IsCurrent; } }
            internal SourceReader(Worker owner, FileStream stream)
            { worker = owner; reader = new StreamReader(stream, Encoding.UTF8, true, 1024); }
            public IEnumerable<string> ReadLines()
            {
                if (frontier != null) { foreach (string nativeLine in ReadFrontierLines()) yield return nativeLine; yield break; }
                if (Volatile.Read(ref disposed) != 0 || Interlocked.Exchange(ref started, 1) != 0)
                    throw new InvalidOperationException("source_reader_already_consumed");
                string line;
                while ((line = reader.ReadLine()) != null) { LinesRead++; yield return line; }
                complete = LinesRead == worker.SourceLines && worker.IsCurrent;
            }
            public void Dispose()
            {
                if (frontier != null) { frontier.DisposeSource(this); return; }
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                complete = false; reader.Dispose();
            }
        }

        private static bool TextValid(string value, int limit)
        { return !String.IsNullOrWhiteSpace(value) && value != "(none)" && value.Length <= limit && value.IndexOf('\0') < 0; }
        private static void Release(Mutex mutex, bool held)
        { if (held && mutex != null) { try { mutex.ReleaseMutex(); } catch (ApplicationException) { } catch (ObjectDisposedException) { } } }
        internal sealed class ProcessWait : WaitHandle
        { internal ProcessWait(SafeWaitHandle handle) { SafeWaitHandle = handle; } }
        private static string FileIdentity(SafeFileHandle handle)
        {
            FileInformation info;
            return GetFileInformationByHandle(handle, out info) ? info.Volume.ToString("x8") + ":" + info.IndexHigh.ToString("x8") + info.IndexLow.ToString("x8") : null;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern int GetCurrentProcessId();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DuplicateHandle(IntPtr source, IntPtr handle, IntPtr target, out SafeWaitHandle duplicate, uint access, bool inherit, uint options);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeWaitHandle OpenProcess(uint access, bool inherit, int id);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeWaitHandle handle, out long creation, out long exit, out long kernel, out long user);
    }
}
