using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32.SafeHandles;
using NLink.Core.Logging;
using NLink.Tools;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private sealed partial class FrontierPayloadCatalog
    {
        private WriterOnlyContribution? writerOnly;
        private ProvisionalSubmission? provisionalSubmission;

        // Deliberately separate from a positive PreparedCatalog. An empty
        // positive catalog still fails; only an original no-admission writer
        // may make this explicit contribution to the two-app source barrier.
        public WriterOnlyContribution? TryTakeWriterOnly()
        {
            if (!OperatingSystem.IsWindows()) { return null; }
            lock (gate)
            {
                if (CurrentFailure().Length != 0 || taken || preparing || admissions != 0 || slots.Count != 0 ||
                    writer is null || fileHandoff is null || !fileHandoff.OwnsWriterScope(this)) { return null; }
                taken = true; return writerOnly = new(this);
            }
        }

        private bool OwnsContribution(PreparedCatalog? positive, WriterOnlyContribution? empty)
            => CurrentFailure().Length == 0 && (positive is not null
                ? empty is null && Owns(positive)
                : empty is not null && ReferenceEquals(writerOnly, empty) && taken && admissions == 0 && slots.Count == 0);

        public sealed class WriterOnlyContribution
        {
            private readonly FrontierPayloadCatalog owner;
            internal WriterOnlyContribution(FrontierPayloadCatalog owner) { this.owner = owner; }
            public bool TrySubmitFiles() => owner.TrySubmitProvisionalFiles(null, this);
            public bool LiveProofEligible => false;
            public bool NativeWriterCustodyEstablished => false;
            public bool PublicationCompletionIssued => false;
        }

        private bool TrySubmitProvisionalFiles(PreparedCatalog? positive, WriterOnlyContribution? empty)
        {
            if (!OperatingSystem.IsWindows()) { return false; }
            LocalFrontierAppRequest.FileHandoff handoff;
            LocalOperationalLog.FrontierWriterRegistration registration;
            Slot[] entries;
            (LocalOperationalLog.FrontierWriteReceipt, LocalOperationalLog.FrontierWriteReceipt)[] writes;
            lock (gate)
            {
                if (!OwnsContribution(positive, empty)) { return false; }
                if (payloadOutputAttempted || sourceAttempted) { Invalidate("provisional_submission_repeated"); return false; }
                payloadOutputAttempted = true; sourceAttempted = true;
                if (writer is null || fileHandoff is null || !ReferenceEquals(writer.Handoff, fileHandoff) ||
                    (positive is not null && GetOrderedWriterEvidence(positive, 0) is null))
                { Invalidate("provisional_writer_missing"); return false; }
                handoff = fileHandoff; registration = writer;
                entries = slots.ToArray();
                writes = entries.Select(s => (s.Completion!, s.Marker!)).ToArray();
            }
            var clock = Stopwatch.StartNew();
            OperationalLogNativeAnchor? source = null;
            var files = new List<FrontierNativePayloadFile>();
            bool submitted = false;
            try
            {
                source = LocalOperationalLog.TryAcquireFrontierProvisionalSource(registration, writes, out var drain);
                if (source is null || drain is null) { return false; }
                bool Current()
                {
                    lock (gate)
                    {
                        return OwnsContribution(positive, empty) && ReferenceEquals(writer, registration) &&
                            ReferenceEquals(registration.Drain, drain) && drain.IsPersistedFor(registration) &&
                            (positive is null || GetOrderedWriterEvidence(positive, 0) is not null) &&
                            source.IsRetained && clock.Elapsed <= MaximumOutputTime;
                    }
                }
                if (!Current() || !handoff.IsCurrent) { return false; }
                var sourceHandle = source.TryGetSubmissionHandle();
                if (sourceHandle is null) { return false; }
                var handles = new List<SafeFileHandle> { sourceHandle };
                var payloads = new List<LocalFrontierFileManifest.Payload>();
                foreach (var entry in entries)
                {
                    if (!Current()) { return false; }
                    var payload = entry.Payload!;
                    var file = FrontierNativePayloadFile.TryCreate(LocalOperationalLog.LogsDirectoryPath, payload.CopyBytes());
                    if (file is null) { return false; }
                    files.Add(file);
                    if (file.ByteCount != payload.ByteCount || file.Sha256 != payload.Sha256 || !Current()) { return false; }
                    var handle = file.TryGetSubmissionHandle();
                    if (handle is null) { return false; }
                    handles.Add(handle);
                    payloads.Add(new(entry.Observation.ProvisionalObservationId,
                        DescribeSubmissionFile(handle, file.FileIdentity, file.ByteCount), Convert.FromHexString(file.Sha256),
                        DescribeSubmissionRange(entry.Completion!), DescribeSubmissionRange(entry.Marker!)));
                }
                // Encoding, file creation and byte verification are outside all
                // service/catalog/logging/channel gates. Only original objects
                // above supply metadata; this API accepts no caller manifest.
                var manifest = new LocalFrontierFileManifest(handoff.GuiRequest, registration.Lease.RequestId,
                    registration.Lease.AppProcessId, registration.Lease.AppProcessCreationFileTime, drain.WriterIdentity,
                    DescribeSubmissionFile(sourceHandle, source.FileIdentity, source.CapturedByteCount),
                    DescribeSubmissionRange(drain), payloads.ToArray()).Encode();
                if (manifest is null || !Current() || !handoff.TrySubmit(manifest, handles.ToArray())) { return false; }
                lock (gate)
                {
                    if (!Current() || files.Any(f => !f.IsCurrent)) { return false; }
                    provisionalSubmission = new(source, files.ToArray());
                    submitted = true; return true;
                }
            }
            catch { return false; }
            finally
            {
                if (!submitted)
                {
                    handoff.Cancel();
                    foreach (var file in files) { file.Dispose(); }
                    source?.Dispose();
                    lock (gate) { Invalidate("provisional_submission_failed"); }
                }
            }
        }

        private static LocalFrontierFileManifest.FileEntry DescribeSubmissionFile(SafeFileHandle handle, string identity, long bytes)
            => new(handle.DangerousGetHandle().ToInt64(), uint.Parse(identity.AsSpan(0, 8), NumberStyles.HexNumber),
                uint.Parse(identity.AsSpan(9, 8), NumberStyles.HexNumber), uint.Parse(identity.AsSpan(17, 8), NumberStyles.HexNumber), bytes);

        private static LocalFrontierFileManifest.Range DescribeSubmissionRange(LocalOperationalLog.FrontierWriteReceipt receipt)
        {
            var span = receipt.Exact.Span!;
            return new(receipt.BatchGeneration, receipt.FirstSequence, receipt.LastSequence, receipt.AttemptUtcTicks,
                receipt.Utf8ByteCount, span.Offset, span.ByteCount, Convert.FromHexString(receipt.PayloadSha256), Convert.FromHexString(span.Sha256));
        }

        private sealed class ProvisionalSubmission(OperationalLogNativeAnchor source, FrontierNativePayloadFile[] files)
        {
            internal void Retire() { foreach (var file in files) { file.Dispose(); } source.Dispose(); }
        }
    }

    private sealed partial class FrontierBoundaryObservation
    {
        // Consumed only together with this observation's original prepared
        // payload and completion receipt, never reconstructed from payload JSON.
        public Guid ProvisionalObservationId => observationId;
    }
}
