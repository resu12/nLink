using System.Diagnostics;
using NLink.Core.Logging;
using NLink.Tools;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    // Private admission/preparation, optionally bound to a native app lease.
    // Writer/source custody and runtime activation remain separate gates.
    private sealed partial class FrontierPayloadCatalog : IDisposable
    {
        public const int MaximumAdmissions = 16;
        public const int MaximumPayloadBytes = FrontierNativePayloadFile.MaximumBytes;
        public const int MaximumRetainedBytes = 4194304;
        private static readonly TimeSpan MaximumPreparationTime = TimeSpan.FromSeconds(2);
        private readonly object gate = new();
        private readonly FrontierPayloadCatalog original;
        private readonly List<Slot> slots = [];
        private int admissions, attempts, retainedBytes;
        private bool preparing, taken, admissionClosed;
        private string failure = "";
        private TimeSpan preparationTime;
        private PreparedCatalog? snapshot;
        private LocalFrontierAppRequest.AppLease? nativeLease;
        private FrontierProducerRegistry? producerRegistry;
        private LocalOperationalLog.FrontierWriterRegistration? writer;
        private bool writerBindingAttempted;
        private FrontierPayloadCatalog() { original = this; }

        public static FrontierPayloadCatalog? TryCreateNativeForApp(LocalFrontierAppRequest.AppLease? lease,
            FrontierProducerRegistry registry)
        {
            if (!registry.IsCurrent) { return null; }
            var catalog = TryCreateNative(lease);
            if (catalog is not null) { catalog.producerRegistry = registry; }
            return catalog;
        }

        public static FrontierPayloadCatalog? TryCreateNative(LocalFrontierAppRequest.AppLease? lease)
        {
            if (!OperatingSystem.IsWindows() || lease is null) { return null; }
            var catalog = new FrontierPayloadCatalog { nativeLease = lease };
            if (lease.TryBindCatalog(catalog)) { return catalog; }
            catalog.Cancel();
            return null;
        }

        public bool TryBindOperationalWriter()
        {
            lock (gate)
            {
                if (CurrentFailure().Length != 0 || nativeLease is null || writerBindingAttempted || admissions != 0 || taken)
                { return false; }
                writerBindingAttempted = true;
                if (fileHandoffAttempted && fileHandoff is null) { return false; }
                writer = LocalOperationalLog.TryRegisterFrontierWriter(nativeLease, this, fileHandoff);
                return writer is not null;
            }
        }

        private sealed class Slot(FrontierBoundaryObservation observation)
        {
            public readonly FrontierBoundaryObservation Observation = observation;
            public bool Attempted;
            public FrontierBoundaryObservation.PreparedBoundaryPayload? Payload;
            public LocalOperationalLog.FrontierWriteReceipt? Completion, Marker;
            public OrderedWriterEvidence? WriterOrder;
        }

        public int AdmissionCount { get { lock (gate) { return admissions; } } }
        public int PreparationAttempts { get { lock (gate) { return attempts; } } }
        public int RetainedByteCount { get { lock (gate) { return retainedBytes; } } }
        public string FailureReason { get { lock (gate) { return failure; } } }

        public FrontierBoundaryObservation? TryAdmit(out string reason)
            => TryAdmitCore(null, out reason);

        public FrontierBoundaryObservation? TryAdmitService(object serviceGate, out string reason)
            => TryAdmitCore(serviceGate, out reason);

        private FrontierBoundaryObservation? TryAdmitCore(object? serviceGate, out string reason)
        {
            lock (gate)
            {
                reason = CurrentFailure();
                if (reason.Length == 0 && taken) { reason = "catalog_taken"; }
                if (reason.Length == 0 && admissionClosed) { reason = "catalog_closing"; }
                if (reason.Length != 0) { return null; }
                if (serviceGate is not null && (writer is null || fileHandoff is null ||
                    !LocalOperationalLog.CanAdmitFrontierObservation(writer)))
                { reason = Invalidate("service_writer_unavailable"); return null; }
                if (preparationTime >= MaximumPreparationTime)
                { reason = Invalidate("preparation_budget"); return null; }
                if (admissions == MaximumAdmissions)
                { reason = Invalidate("admission_capacity"); return null; }
                var observation = new FrontierBoundaryObservation(this, serviceGate);
                if (nativeLease is not null && (!OperatingSystem.IsWindows() ||
                    !observation.MatchesProcess(nativeLease.AppProcessId, nativeLease.AppProcessCreationFileTime)))
                { reason = Invalidate("native_process_mismatch"); return null; }
                if (writer is not null) { observation.BindCompletionWriter(writer); }
                slots.Add(new(observation));
                admissions++;
                return observation;
            }
        }

        public bool TryPrepare(FrontierBoundaryObservation observation, out string reason)
        {
            Slot slot;
            lock (gate)
            {
                reason = CurrentFailure();
                if (reason.Length == 0 && taken) { reason = "catalog_taken"; }
                if (reason.Length != 0) { return false; }
                var owned = slots.Find(s => ReferenceEquals(s.Observation, observation));
                reason = "foreign_producer";
                if (owned is null) { return false; }
                reason = "preparation_attempted";
                if (owned.Attempted) { return false; }
                if (preparing) { reason = Invalidate("preparation_busy"); return false; }
                if (preparationTime >= MaximumPreparationTime)
                { reason = Invalidate("preparation_budget"); return false; }
                slot = owned;
                slot.Attempted = true;
                attempts++;
                preparing = true;
            }

            FrontierBoundaryObservation.PreparedBoundaryPayload? payload = null;
            var started = Stopwatch.GetTimestamp();
            reason = "preparation_failed";
            try { payload = observation.TryPrepareCatalogPayload(this, out reason); }
            catch (Exception) { }
            var elapsed = Stopwatch.GetElapsedTime(started);
            lock (gate)
            {
                preparing = false;
                if (CurrentFailure().Length != 0) { reason = failure; return false; }
                if (!TryChargePreparation(preparationTime, elapsed, out var total))
                { reason = Invalidate("preparation_budget"); return false; }
                preparationTime = total;
                if (payload is null || !observation.OwnsPreparedPayload(payload))
                { reason = Invalidate(reason.Length == 0 ? "preparation_failed" : reason); return false; }
                if (!FitsPayloadBytes(retainedBytes, payload.ByteCount))
                { reason = Invalidate("payload_capacity"); return false; }
                slot.Payload = payload;
                retainedBytes += payload.ByteCount;
                reason = "";
                return true;
            }
        }

        public PreparedCatalog? TryTake(out string reason)
        {
            lock (gate)
            {
                reason = CurrentFailure();
                if (reason.Length == 0 && taken) { reason = "catalog_taken"; }
                if (reason.Length != 0) { return null; }
                taken = true;
                if (preparing || slots.Count == 0 || slots.Any(s => s.Payload is null || !s.Observation.OwnsPreparedPayload(s.Payload)))
                { reason = Invalidate("catalog_incomplete"); return null; }
                return snapshot = new(this, slots.Count, retainedBytes);
            }
        }

        public bool Owns(PreparedCatalog? candidate)
        { lock (gate) { return CurrentFailure().Length == 0 && candidate is not null && ReferenceEquals(snapshot, candidate); } }

        private byte[]? CopyPayload(PreparedCatalog candidate, int index)
        {
            lock (gate)
            {
                if (!Owns(candidate) || index < 0 || index >= slots.Count) { return null; }
                return slots[index].Payload!.CopyBytes();
            }
        }

        private LocalOperationalLog.FrontierWriteReceipt? GetCompletionWrite(PreparedCatalog candidate, int index)
        {
            lock (gate)
            {
                if (!Owns(candidate) || index < 0 || index >= slots.Count || writer is null) { return null; }
                return slots[index].Observation.GetCompletionWrite();
            }
        }

        public void Cancel() { lock (gate) { if (ReferenceEquals(original, this)) { Invalidate("catalog_cancelled"); } } }
        public void Dispose() => Cancel();

        private string CurrentFailure()
        {
            if (!ReferenceEquals(original, this)) { return "foreign_catalog"; }
            if (failure.Length == 0 && producerRegistry is not null && !producerRegistry.IsCurrent)
            { Invalidate("producer_registry_invalid"); }
            if (failure.Length == 0 && nativeLease is not null && (!OperatingSystem.IsWindows() || !nativeLease.OwnsCatalog(this)))
            { Invalidate("native_request_revoked"); }
            return failure;
        }

        private string Invalidate(string reason)
        {
            if (failure.Length == 0) { failure = reason; }
            if (OperatingSystem.IsWindows()) { fileHandoff?.RetireCatalog(this); }
            payloadFiles?.Retire();
            payloadFiles = null;
            provisionalSubmission?.Retire();
            provisionalSubmission = null;
            writer?.Revoke();
            slots.Clear();
            snapshot = null;
            retainedBytes = 0;
            return failure;
        }

        private static bool FitsPayloadBytes(int retained, int next)
            => retained >= 0 && next is > 0 and <= MaximumPayloadBytes && retained <= MaximumRetainedBytes - next;

        private static bool TryChargePreparation(TimeSpan used, TimeSpan elapsed, out TimeSpan total)
        {
            total = used;
            if (used < TimeSpan.Zero || elapsed < TimeSpan.Zero || used > MaximumPreparationTime || elapsed > MaximumPreparationTime - used)
            { return false; }
            total = used + elapsed;
            return true;
        }

        public sealed class PreparedCatalog
        {
            private readonly FrontierPayloadCatalog owner;
            internal PreparedCatalog(FrontierPayloadCatalog owner, int count, int bytes)
            { this.owner = owner; Count = count; ByteCount = bytes; }
            public int Count { get; }
            public int ByteCount { get; }
            public bool IsCurrent => owner.Owns(this);
            public byte[]? CopyPayload(int index) => owner.CopyPayload(this, index);
            public LocalOperationalLog.FrontierWriteReceipt? GetCompletionWrite(int index) => owner.GetCompletionWrite(this, index);
            public bool TrySubmitMarkers() => owner.TrySubmitMarkers(this);
            public OrderedWriterEvidence? GetOrderedWriterEvidence(int index) => owner.GetOrderedWriterEvidence(this, index);
            public SourceFileEvidence? TryAcquireSourceFile() => owner.TryAcquireSourceFile(this);
            public PayloadFilesEvidence? TryWritePayloadFiles() => owner.TryWritePayloadFiles(this);
            public bool TrySubmitFiles() => owner.TrySubmitProvisionalFiles(this, null);
            public bool LiveProofEligible => false;
            public bool NativeWriterCustodyEstablished => false;
            public bool PublicationCompletionIssued => false;
        }
    }
}
