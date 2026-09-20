using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private sealed partial class FrontierPayloadCatalog
    {
        private bool markersAttempted;
        private bool sourceAttempted;
        private SourceFileEvidence? sourceFile;

        private SourceFileEvidence? TryAcquireSourceFile(PreparedCatalog candidate)
        {
            LocalOperationalLog.FrontierWriterRegistration registration;
            (LocalOperationalLog.FrontierWriteReceipt, LocalOperationalLog.FrontierWriteReceipt)[] writes;
            lock (gate)
            {
                if (!Owns(candidate) || writer is null || sourceAttempted) { return null; }
                sourceAttempted = true;
                if (GetOrderedWriterEvidence(candidate, 0) is null) { writer.Revoke(); return null; }
                registration = writer;
                writes = slots.Select(s => (s.Completion!, s.Marker!)).ToArray();
            }
            var source = LocalOperationalLog.TryAcquireFrontierSource(registration, writes);
            lock (gate)
            {
                if (source is null) { registration.Revoke(); return null; }
                if (!Owns(candidate) || GetOrderedWriterEvidence(candidate, 0) is null || !source.IsVerified)
                { source.Dispose(); return null; }
                return sourceFile = new(this, candidate, source);
            }
        }

        private bool OwnsSourceFile(SourceFileEvidence evidence, PreparedCatalog candidate, OperationalLogNativeSource source)
        {
            lock (gate)
            {
                var current = ReferenceEquals(sourceFile, evidence) && GetOrderedWriterEvidence(candidate, 0) is not null && source.IsVerified;
                // A copied wrapper must not revoke the original owner's handle.
                if (!current && ReferenceEquals(sourceFile, evidence)) { source.Dispose(); }
                return current;
            }
        }

        // App-local read custody only. No path, digest or copied wrapper can
        // establish ownership, and no cross-process descriptor is issued here.
        public sealed class SourceFileEvidence(FrontierPayloadCatalog owner, PreparedCatalog snapshot, OperationalLogNativeSource source)
        {
            public string FileIdentity => source.FileIdentity;
            public long ByteCount => source.ByteCount;
            public string Sha256 => source.Sha256!;
            public bool IsCurrent => owner.OwnsSourceFile(this, snapshot, source);
            public FileStream? TryOpenRead()
            {
                if (!IsCurrent) { return null; }
                var read = source.TryOpenRead();
                if (IsCurrent) { return read; }
                read?.Dispose(); return null;
            }
            public bool LiveProofEligible => false;
            public bool NativeWriterCustodyEstablished => false;
            public bool PublicationCompletionIssued => false;
        }

        // Explicit finalizer call only, outside the service gate. True means all
        // markers were submitted, not that their writes or the drain persisted.
        private bool TrySubmitMarkers(PreparedCatalog candidate)
        {
            lock (gate)
            {
                if (!Owns(candidate) || writer is null || markersAttempted) { return false; }
                markersAttempted = true;
                try
                {
                    foreach (var slot in slots)
                    {
                        slot.Completion = slot.Observation.GetCompletionWrite();
                        if (slot.Completion is null) { Invalidate("marker_completion_missing"); return false; }
                    }
                    foreach (var slot in slots)
                    {
                        var message = slot.Observation.CreatePayloadMarker(slot.Payload!, slot.Completion!);
                        slot.Marker = message is null ? null : LocalOperationalLog.TryWriteFrontierMarker(writer, slot.Completion!, message);
                        if (slot.Marker is null) { Invalidate("marker_submission_failed"); return false; }
                    }
                    return CurrentFailure().Length == 0;
                }
                catch (Exception) { Invalidate("marker_submission_failed"); return false; }
            }
        }

        private OrderedWriterEvidence? GetOrderedWriterEvidence(PreparedCatalog candidate, int index)
        {
            lock (gate)
            {
                if (!Owns(candidate) || writer is null || index < 0 || index >= slots.Count) { return null; }
                LocalOperationalLog.FrontierWriteReceipt? drain = null;
                foreach (var entry in slots)
                {
                    if (entry.Completion is null || entry.Marker is null || !entry.Observation.OwnsPreparedPayload(entry.Payload) ||
                        !ReferenceEquals(entry.Completion, entry.Observation.GetCompletionWrite())) { return null; }
                    drain = LocalOperationalLog.GetFrontierDrain(writer, entry.Completion, entry.Marker);
                    if (drain is null) { return null; }
                }
                var slot = slots[index];
                return slot.WriterOrder ??= new(this, candidate, index, slot.Completion!, slot.Marker!, drain!);
            }
        }

        private bool OwnsWriterOrder(OrderedWriterEvidence evidence, PreparedCatalog candidate, int index)
        {
            lock (gate)
            {
                return Owns(candidate) && index >= 0 && index < slots.Count &&
                    ReferenceEquals(slots[index].WriterOrder, evidence) &&
                    ReferenceEquals(GetOrderedWriterEvidence(candidate, index), evidence);
            }
        }

        // This proves only exact append order. It is not payload-file publication,
        // retained source-file custody, fsync durability or an acceptance verdict.
        public sealed class OrderedWriterEvidence(FrontierPayloadCatalog owner, PreparedCatalog snapshot, int index,
            LocalOperationalLog.FrontierWriteReceipt completion, LocalOperationalLog.FrontierWriteReceipt marker,
            LocalOperationalLog.FrontierWriteReceipt drain)
        {
            public LocalOperationalLog.FrontierWriteReceipt Completion { get; } = completion;
            public LocalOperationalLog.FrontierWriteReceipt Marker { get; } = marker;
            public LocalOperationalLog.FrontierWriteReceipt Drain { get; } = drain;
            public bool IsCurrent => owner.OwnsWriterOrder(this, snapshot, index);
            public bool LiveProofEligible => false;
            public bool NativeWriterCustodyEstablished => false;
            public bool PublicationCompletionIssued => false;
        }
    }

    private sealed partial class FrontierBoundaryObservation
    {
        public string? CreatePayloadMarker(PreparedBoundaryPayload payload, LocalOperationalLog.FrontierWriteReceipt completion)
        {
            lock (payloadGate)
            {
                if (!OwnsPreparedPayload(payload) || !ReferenceEquals(GetCompletionWrite(), completion)) { return null; }
                return "event=filetransfer_frontier_payload_marker" +
                    $"; frontier_payload_format={BoundaryPayloadFormat}; frontier_payload_hash_domain={BoundaryPayloadHashDomain}" +
                    $"; frontier_payload_observation_id={observationId:N}; frontier_payload_sha256={payload.Sha256}" +
                    $"; frontier_payload_byte_count={payload.ByteCount}" +
                    $"; frontier_completion_batch_generation={completion.BatchGeneration}" +
                    $"; frontier_completion_first_sequence={completion.FirstSequence}; frontier_completion_last_sequence={completion.LastSequence}" +
                    $"; frontier_completion_sha256={completion.PayloadSha256}; authority_effect=none";
            }
        }
    }
}
