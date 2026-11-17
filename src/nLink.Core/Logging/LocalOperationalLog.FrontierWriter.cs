using System.Diagnostics;
using NLink.Tools;

namespace NLink.Core.Logging;

public static partial class LocalOperationalLog
{
    internal const int MaximumFrontierCompletionWrites = 16;
    internal const int MaximumFrontierMarkerWrites = 16;
    internal enum FrontierWriteKind { Completion, Marker, Drain }
    private static FrontierWriterRegistration? frontierWriter;

    internal static bool CanAdmitFrontierObservation(FrontierWriterRegistration registration)
    {
        lock (Gate) { return !writerCompletionAttempted && IsFrontierWriterCurrentLocked(registration); }
    }

    // Called before producer admission, outside the service gate. No writer ID,
    // batch identity or process incarnation is accepted from a caller DTO.
    internal static FrontierWriterRegistration? TryRegisterFrontierWriter(
        LocalFrontierAppRequest.AppLease lease, object catalog, LocalFrontierAppRequest.FileHandoff? handoff = null)
    {
        lock (Gate)
        {
            if (!OperatingSystem.IsWindows() || frontierWriter is not null || writerCompletionAttempted ||
                !lease.OwnsCatalog(catalog) || (handoff is not null && !handoff.OwnsWriterScope(catalog))) { return null; }
            try
            {
                using var process = Process.GetCurrentProcess();
                if (process.Id != lease.AppProcessId ||
                    process.StartTime.ToUniversalTime().ToFileTimeUtc() != lease.AppProcessCreationFileTime)
                { return null; }
                var registration = new FrontierWriterRegistration(lease, catalog, handoff);
                frontierWriter = registration;
                return IsFrontierWriterCurrentLocked(registration) ? registration : null;
            }
            catch { return null; }
        }
    }

    internal static FrontierWriteReceipt? InfoWithFrontierReceipt(
        string message, FrontierWriterRegistration registration, object association)
        => Write("INFO", "FileTransferCoordinator", message, registration, association);

    internal static FrontierWriteReceipt? TryWriteFrontierMarker(
        FrontierWriterRegistration registration, FrontierWriteReceipt completion, string message)
    {
        lock (Gate)
        {
            if (writerCompletionAttempted || !completion.IsCompletionFor(registration)) { return null; }
            return Write("INFO", "FileTransferCoordinator", message, registration, completion, FrontierWriteKind.Marker);
        }
    }

    internal static FrontierWriteReceipt? GetFrontierDrain(
        FrontierWriterRegistration registration, FrontierWriteReceipt completion, FrontierWriteReceipt marker)
    {
        lock (Gate)
        {
            var drain = registration.Drain;
            return registration.DrainFlushed && drain is not null && writerSequence == drain.LastSequence &&
                completion.IsCompletionFor(registration) && marker.IsMarkerFor(registration, completion) &&
                drain.IsPersistedFor(registration) && completion.LastSequence < marker.FirstSequence &&
                marker.LastSequence < drain.FirstSequence && completion.BatchGeneration < marker.BatchGeneration &&
                marker.BatchGeneration < drain.BatchGeneration ? drain : null;
        }
    }

    private static bool IsFrontierWriterCurrentLocked(FrontierWriterRegistration registration)
    {
        if (!ReferenceEquals(frontierWriter, registration) || registration.Revoked) { return false; }
        if (!OperatingSystem.IsWindows() || !registration.Lease.OwnsCatalog(registration.Catalog) ||
            (registration.Handoff is not null && !registration.Handoff.OwnsWriterScope(registration.Catalog)) ||
            registration.Lease.AcceptanceRunId != GetEnvironmentEvidenceValue("NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID") ||
            registration.Lease.ScenarioRunId != GetEnvironmentEvidenceValue("NLINK_FILETRANSFER_SCENARIO_RUN_ID"))
        { RevokeFrontierWriterLocked(registration); return false; }
        return true;
    }

    internal static OperationalLogNativeSource? TryAcquireFrontierSource(FrontierWriterRegistration registration,
        IReadOnlyList<(FrontierWriteReceipt Completion, FrontierWriteReceipt Marker)> writes)
    {
        FrontierWriteReceipt drain;
        OrderedOperationalLogWritePump.ExactBatchReceipt[] exact;
        bool Current() => IsFrontierWriterCurrentLocked(registration) && writes.Count is > 0 and <= MaximumFrontierCompletionWrites &&
            writes.Count == registration.Writes.Count && writes.Count == registration.Markers.Count &&
            writes.All(w => ReferenceEquals(GetFrontierDrain(registration, w.Completion, w.Marker), registration.Drain)) &&
            registration.Drain is not null;
        lock (Gate)
        {
            if (!Current()) { return null; }
            drain = registration.Drain!;
            exact = writes.SelectMany(w => new[] { w.Completion.Exact, w.Marker.Exact }).Append(drain.Exact).ToArray();
        }
        var source = WritePump.TryAcquireSource(drain.Exact, exact);
        lock (Gate)
        {
            if (source is not null && Current() && ReferenceEquals(drain, registration.Drain) && source.IsVerified) { return source; }
            source?.Dispose(); return null;
        }
    }

    internal static OperationalLogNativeAnchor? TryAcquireFrontierProvisionalSource(FrontierWriterRegistration registration,
        IReadOnlyList<(FrontierWriteReceipt Completion, FrontierWriteReceipt Marker)> writes, out FrontierWriteReceipt? drain)
    {
        drain = null;
        lock (Gate)
        {
            if (!IsFrontierWriterCurrentLocked(registration) || registration.Handoff is null ||
                writes.Count > MaximumFrontierCompletionWrites || writes.Count != registration.Writes.Count ||
                writes.Count != registration.Markers.Count || !registration.DrainFlushed || registration.Drain is not { } original ||
                writerSequence != original.LastSequence || !original.IsPersistedFor(registration) ||
                writes.Any(w => !ReferenceEquals(GetFrontierDrain(registration, w.Completion, w.Marker), original))) { return null; }
            var exact = writes.SelectMany(w => new[] { w.Completion.Exact, w.Marker.Exact }).Append(original.Exact).ToArray();
            // Only native metadata is checked here; no content read or whole-source hash.
            var source = WritePump.TryAcquireProvisionalSource(original.Exact, exact);
            if (source is null) { return null; }
            drain = original; return source;
        }
    }

    private static bool TryBeginFrontierWriteLocked(FrontierWriterRegistration registration, object association, FrontierWriteKind kind)
    {
        if (!IsFrontierWriterCurrentLocked(registration)) { return false; }
        if (kind == FrontierWriteKind.Marker)
        {
            if (writerCompletionAttempted || association is not FrontierWriteReceipt completion ||
                !completion.IsCompletionFor(registration)) { return false; }
            if (registration.Markers.ContainsKey(association) || registration.Markers.Count == MaximumFrontierMarkerWrites)
            { RevokeFrontierWriterLocked(registration); return false; }
            registration.Markers.Add(association, null);
            return true;
        }
        if (registration.Writes.ContainsKey(association)) { return false; }
        if (registration.Writes.Count == MaximumFrontierCompletionWrites) { RevokeFrontierWriterLocked(registration); return false; }
        registration.Writes.Add(association, null);
        return true;
    }

    private static void RevokeFrontierWriterLocked(FrontierWriterRegistration registration)
    {
        if (!ReferenceEquals(frontierWriter, registration)) { return; }
        registration.Revoked = true;
        if (OperatingSystem.IsWindows()) { registration.Handoff?.RetireCatalog(registration.Catalog); }
        WritePump.RevokeExactBatchReceipts();
        registration.Writes.Clear();
        registration.Markers.Clear();
        registration.Drain = null;
        registration.DrainFlushed = false;
    }

    internal sealed class FrontierWriterRegistration(LocalFrontierAppRequest.AppLease lease, object catalog,
        LocalFrontierAppRequest.FileHandoff? handoff = null)
    {
        internal readonly LocalFrontierAppRequest.AppLease Lease = lease;
        internal readonly object Catalog = catalog;
        internal readonly LocalFrontierAppRequest.FileHandoff? Handoff = handoff;
        internal readonly Dictionary<object, FrontierWriteReceipt?> Writes = new(ReferenceEqualityComparer.Instance);
        internal readonly Dictionary<object, FrontierWriteReceipt?> Markers = new(ReferenceEqualityComparer.Instance);
        internal FrontierWriteReceipt? Drain;
        internal bool DrainFlushed;
        internal bool Revoked;
        internal void Revoke() { lock (Gate) { RevokeFrontierWriterLocked(this); } }
    }

    internal sealed class FrontierWriteReceipt
    {
        private readonly FrontierWriterRegistration registration;
        private readonly object association;
        private readonly OrderedOperationalLogWritePump.ExactBatchReceipt exact;
        private readonly FrontierWriteKind kind;

        internal FrontierWriteReceipt(FrontierWriterRegistration registration, object association,
            OrderedOperationalLogWritePump.ExactBatchReceipt exact, OperationalLogWriteBatch batch,
            FrontierWriteKind kind = FrontierWriteKind.Completion)
        {
            this.registration = registration; this.association = association; this.exact = exact;
            this.kind = kind;
            WriterIdentity = WriterId;
            BatchGeneration = batch.BatchGeneration; FirstSequence = batch.FirstSequence; LastSequence = batch.LastSequence;
            AttemptUtcTicks = batch.AttemptUtcTicks; PayloadSha256 = batch.PayloadSha256; Utf8ByteCount = batch.Utf8ByteCount;
        }

        internal string WriterIdentity { get; }
        internal long BatchGeneration { get; }
        internal long FirstSequence { get; }
        internal long LastSequence { get; }
        internal long AttemptUtcTicks { get; }
        internal string PayloadSha256 { get; }
        internal long Utf8ByteCount { get; }
        internal OrderedOperationalLogWritePump.ExactBatchReceipt Exact => exact;

        internal bool IsCompletionFor(FrontierWriterRegistration candidate)
            => kind == FrontierWriteKind.Completion && ReferenceEquals(registration, candidate) && IsPersistedFor(association);

        internal bool IsMarkerFor(FrontierWriterRegistration candidate, FrontierWriteReceipt completion)
            => kind == FrontierWriteKind.Marker && ReferenceEquals(registration, candidate) && IsPersistedFor(completion);

        internal bool IsPersistedFor(object candidate)
        {
            lock (Gate)
            {
                return IsOwnedForLocked(candidate) &&
                    WritePump.GetPersistence(exact) == OrderedOperationalLogWritePump.ExactBatchPersistence.Persisted;
            }
        }

        internal bool WaitForPersistence(object candidate, TimeSpan timeout)
        {
            lock (Gate) { if (!IsOwnedForLocked(candidate)) { return false; } }
            // The high-watermark is only a wakeup condition. A later successful
            // batch cannot turn this exact failed/revoked receipt into proof.
            _ = WritePump.FlushThroughSequence(LastSequence, timeout);
            return IsPersistedFor(candidate);
        }

        private bool IsOwnedForLocked(object candidate)
        {
            var owned = kind == FrontierWriteKind.Drain ? registration.Drain :
                (kind == FrontierWriteKind.Marker ? registration.Markers : registration.Writes).GetValueOrDefault(association);
            return ReferenceEquals(association, candidate) && IsFrontierWriterCurrentLocked(registration) && ReferenceEquals(owned, this);
        }
    }
}
