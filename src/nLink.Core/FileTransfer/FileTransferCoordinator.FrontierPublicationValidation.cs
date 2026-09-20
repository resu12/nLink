using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NLink.Core.FileTransfer;

internal static partial class FileTransferCoordinator
{
    // A private offline custody check, not a Snapshot authority evaluator. Only a
    // completed producer can establish the expected receipt; candidate JSON and
    // candidate hashes never establish that expectation.
    internal sealed class FrontierPublicationOfflineCapture
    {
        internal const string Format = "offline_frontier_publication_validation_v3";
        internal const string ClockDomain = "injected_observation_separate_snapshot_clock_v1";
        private static readonly JsonSerializerOptions JsonOptions = new();
        private readonly string full;
        private readonly string compact;
        private readonly string outcome;

        private FrontierPublicationOfflineCapture(string full, string compact, string outcome, int witnessCount)
            => (this.full, this.compact, this.outcome, WitnessCount) = (full, compact, outcome, witnessCount);

        internal int WitnessCount { get; }
        internal string ExportFull() => full;
        internal string ExportCompact() => compact;

        internal sealed record Validation(string Status, string Reason, string? DiagnosticOutcome)
        {
            // The injected clock and private capture have no live acceptance meaning.
            public bool LiveProofEligible => false;
        }

        internal Validation Validate(string? candidate, string view)
        {
            var expected = view switch { "full" => full, "compact" => compact, _ => null };
            if (expected is null) { return new("EvidenceIncomplete", "unsupported_view", null); }
            if (!string.Equals(candidate, expected, StringComparison.Ordinal))
            { return new("EvidenceIncomplete", "frozen_capture_mismatch", null); }
            return new("ValidatedOfflineCapture", "producer_bound_separate_clock", outcome);
        }

        internal static FrontierPublicationOfflineCapture? TryFreeze(FrontierPublicationObservation producer,
            IReadOnlyList<string> writerInput, out string reason)
        {
            reason = "producer_receipt_missing";
            if (producer.Receipt is not { } receipt || producer.Status != receipt.Outcome) { return null; }
            if (!ValidateStructure(receipt, out reason)) { return null; }
            if (producer.Lifecycle != receipt.Lifecycle || !ValidateLifecycle(receipt, receipt.Lifecycle))
            { reason = "receipt_lifecycle"; return null; }
            if (producer.WriterBinding is not { } binding || binding != FrontierPublicationWriterBinding.Create(receipt))
            { reason = "producer_writer_binding_missing"; return null; }
            // These are fixture transport bounds, not runtime policy or a primary
            // witness allocation. No file reads or mutable input references escape.
            reason = "writer_capacity";
            if (writerInput.Count is < 1 or > 8192) { return null; }
            var lines = writerInput.ToImmutableArray();
            if (lines.Any(line => line is null || line.IndexOfAny(['\r', '\n']) >= 0) ||
                lines.Sum(line => (long)line.Length) > 8 * 1024 * 1024) { return null; }
            var sequenceLines = new Dictionary<long, string>();
            var witnesses = new List<object>();
            string? writer = null;
            long previousSequence = 0;
            var completion = SessionFileTransferService.CreateCoordinatorSnapshotEvidence(receipt.Completion);
            var completionCount = 0;
            var bindingCount = 0;
            object? bindingLocation = null;
            var streamStarts = 0;
            var streamEnds = 0;
            var completed = false;
            var completionSeen = false;
            reason = "writer_binding";
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                if (line.IndexOf(" event=", StringComparison.Ordinal) < 0 ||
                    line.IndexOf("event=", line.IndexOf(" event=", StringComparison.Ordinal) + 7, StringComparison.Ordinal) >= 0 ||
                    !Field(line, "log_writer_id", out var id) || string.IsNullOrWhiteSpace(id) ||
                    !Field(line, "log_writer_sequence", out var sequenceText) ||
                    !long.TryParse(sequenceText, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var sequence) || sequence <= 0 ||
                    !Field(line, "log_writer_attempt_utc_ticks", out var ticksText) ||
                    !long.TryParse(ticksText, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var ticks) || ticks <= 0 || ticks > DateTime.MaxValue.Ticks ||
                    (writer is not null && id != writer)) { return null; }
                writer = id;
                if (sequenceLines.TryGetValue(sequence, out var prior))
                {
                    // Exact retained copies are idempotent, but remain in the
                    // frozen full input. Never discard a conflicting copy.
                    if (prior != line) { reason = "conflicting_writer_copy"; return null; }
                    continue;
                }
                if (previousSequence > 0 && (sequence <= previousSequence || sequence - previousSequence != 1))
                { reason = "writer_order_or_gap"; return null; }
                previousSequence = sequence;
                sequenceLines.Add(sequence, line);
                var ownCoordinator = Field(line, "coordinator_instance_id", out var coordinator) &&
                    coordinator == completion.CoordinatorInstanceId;
                var ownTransfer = Field(line, "transfer_id", out var transfer) &&
                    transfer == receipt.Frame.TransferId && Field(line, "session_id", out var session) &&
                    session == receipt.Frame.SessionId;
                var isSnapshot = Event(line, "filetransfer_coordinator_transition_snapshot");
                var isBinding = Event(line, FrontierPublicationWriterBinding.EventName) &&
                    (Is(line, "frontier_coordinator_id", binding.CoordinatorId) ||
                     Is(line, "frontier_observation_id", binding.ObservationId) ||
                     Is(line, "frontier_receipt_hash", binding.ReceiptHash) ||
                     Is(line, "frontier_completion_id", binding.CompletionId) ||
                     Is(line, "frontier_completion_hash", binding.CompletionHash));
                if (isBinding)
                {
                    var start = line.IndexOf(" event=", StringComparison.Ordinal) + 1;
                    var end = line.IndexOf("; log_writer_id=", start, StringComparison.Ordinal);
                    if (!completionSeen || completed || end <= start ||
                        line[start..end] != binding.ToLogMessage() || ++bindingCount != 1)
                    { reason = "receipt_writer_binding"; return null; }
                    foreach (var part in binding.ToLogMessage().Split(';').Skip(1))
                    {
                        var pair = part.Trim().Split('=', 2);
                        if (!Is(line, pair[0], pair[1])) { reason = "receipt_writer_binding"; return null; }
                    }
                    bindingLocation = new { LedgerLine = index + 1, WriterSequence = sequence, RawLine = line };
                }
                if (ownCoordinator && Event(line, "filetransfer_coordinator_snapshot_stream_started")) { streamStarts++; }
                if (ownCoordinator && isSnapshot && Field(line, "snapshot_sha256", out var hash) &&
                    hash == completion.CalculatePayloadSha256())
                {
                    if (streamStarts != 1 || completed ||
                        !Field(line, "snapshot_payload", out var payload) || payload != completion.ToBase64UrlPayload())
                    { reason = "completion_binding"; return null; }
                    completionCount++;
                    completionSeen = true;
                }
                if (ownCoordinator && Event(line, "filetransfer_coordinator_snapshot_stream_completed"))
                {
                    if (!completionSeen) { reason = "early_stream_end"; return null; }
                    streamEnds++;
                    completed = true;
                }
                if (isBinding || ownCoordinator || ownTransfer && (Event(line, "filetransfer_v6_sparse_write_committed") ||
                    Event(line, "filetransfer_v6_receiver_state_sent") ||
                    Event(line, "filetransfer_v6_frontier_recent_progress_grace_deferred") ||
                    Event(line, "filetransfer_v6_frontier_recent_progress_grace_recheck_decided") ||
                    Event(line, "filetransfer_v6_frontier_request_deferred") ||
                    Event(line, "filetransfer_v6_frontier_stall_grace_recheck_decided") ||
                    Event(line, "filetransfer_fallback_frontier_repair_authority_granted") ||
                    Event(line, "filetransfer_fallback_frontier_repair_progress_observed") ||
                    Event(line, "filetransfer_control_plane_delivery_observed")))
                {
                    witnesses.Add(new { LedgerLine = index + 1, WriterSequence = sequence, RawLine = line });
                }
            }
            if (streamStarts != 1 || streamEnds != 1 || completionCount != 1)
            { reason = "incomplete_completion_stream"; return null; }
            if (bindingCount != 1) { reason = "receipt_writer_binding_missing"; return null; }
            if (!ValidateWriterLinks(receipt, sequenceLines.Values.ToArray()))
            { reason = "receipt_writer_links"; return null; }
            if (witnesses.Count is < 1 or > 64) { reason = "witness_budget"; return null; }
            var receiptJson = Canonical(receipt);
            var inputHash = Hash(Canonical(lines));
            var captureId = Guid.NewGuid().ToString("N");
            // Both projections are independently built from the frozen writer,
            // not from each other or from caller-supplied selections/digests.
            string Project(string view, object records) => Canonical(new
            {
                Format, ClockDomain, CaptureId = captureId, View = view,
                Receipt = JsonSerializer.Deserialize<JsonElement>(receiptJson),
                WriterBinding = binding, WriterBindingLocation = bindingLocation,
                ReceiptHashDomain = FrontierPublicationWriterBinding.HashDomain,
                InputHashDomain = "sha256-utf8-canonical-json-records-v1", InputSha256 = inputHash,
                WriterId = writer, InputRecordCount = lines.Length, Records = records,
            });
            reason = "";
            return new(Project("full", lines), Project("compact", witnesses), receipt.Outcome, witnesses.Count);
        }

        private static bool ValidateStructure(FrontierPublicationReceipt r, out string reason)
        {
            reason = "receipt_source";
            var s = r.Source;
            var b = s.Boundary;
            var a = r.RepairAuthority;
            var owner = r.SourceRuntimeOwner;
            var publication = r.SourcePublication;
            if (r.ContractVersion != 2 || owner.IsDefault || publication.IsDefault || a.IsDefault ||
                r.Repair.CoordinatorInstanceId != r.Completion.CoordinatorInstanceId ||
                r.Repair.RepairAuthorityId != a.AuthorityId.ToString() || r.Repair.RepairGeneration != a.DecisionGeneration ||
                r.Repair.ProtocolRepairRequestId != a.ProtocolRepairRequestId || r.Repair.RequestCorrelationId != a.RequestCorrelationId ||
                r.Repair.CurrentScopeFingerprint != s.ScopeFingerprint || a.RuntimeOwner != owner || publication.RuntimeOwner != owner ||
                s.RuntimeOwnerFingerprint != CalculateSnapshotFingerprint(owner) ||
                s.PublicationFingerprint != CalculateSnapshotFingerprint(publication) ||
                s.CommitFingerprint != CalculateSnapshotFingerprint(r.SourceCommit) ||
                b.Route != "post_tuna_fallback_v6" || b.ProtocolVersion != 6 || b.TerminalGeneration != 0 ||
                b.DataSessionGeneration <= 0 || string.IsNullOrWhiteSpace(b.DataSessionIdentity) ||
                owner.Direction != FileTransferDirection.Inbound || owner.Route != FileTransferRoute.PostTunaFallbackV6 ||
                owner.ProtocolVersion != 6 || owner.TerminalGeneration != 0 ||
                owner.SessionId != r.Frame.SessionId || owner.TransferId != r.Frame.TransferId ||
                r.Completion.SessionId != owner.SessionId || r.Completion.TransferId != owner.TransferId ||
                r.Completion.Direction != "inbound" || r.Completion.DecisionKind != "FallbackControlPlaneSendCompleted" ||
                r.Completion.DecisionStatus is not ("Accepted" or "Idempotent")) { return false; }
            // An accepted older issued send does not retire the newer current
            // token. Its existing Snapshot is legitimately state-idempotent;
            // the producer's accepted peer-visible decision remains essential.
            if (r.Completion.DecisionStatus == "Idempotent" &&
                (r.Completion.Aggregate.BeforeStateGeneration != r.Completion.Aggregate.AfterStateGeneration ||
                 r.Completion.Aggregate.BeforeStateFingerprint != r.Completion.Aggregate.AfterStateFingerprint ||
                 !r.Completion.Aggregate.ChangedFamilies.IsEmpty)) { return false; }
            if (s.ScopeFingerprint != CalculateSnapshotFingerprint(new
            {
                s.Boundary, s.RuntimeOwnerFingerprint, s.PublicationFingerprint, s.CommitFingerprint,
                s.BridgeDecisionGeneration, s.AggregateBridgeDecisionGeneration, s.AggregateTransportEpoch,
                s.RetainedLegCheckpointGeneration, s.RetainedLegCheckpointRequestId, s.LeaseGeneration,
            }) || b.LiveRouteEpoch != owner.LiveRouteEpoch || b.RuntimeGeneration != owner.RuntimeGeneration ||
                b.RoutePublicationGeneration != publication.PublicationGeneration || b.LegGeneration != publication.LegGeneration ||
                b.RouteCommitGeneration != (r.SourceCommit?.CommitGeneration ?? 0)) { return false; }
            reason = "receipt_wake_chain";
            if (r.RecentArms.IsDefaultOrEmpty || r.Commits.IsDefaultOrEmpty ||
                r.RecentArms.Length + r.Commits.Length + 5 >= 64 || r.StallGeneration <= 0 ||
                r.StallDeadline - r.StallStartedUtc != TimeSpan.FromMilliseconds(750) ||
                r.StallStartedUtc < r.RecentDeadline) { return false; }
            var root = r.RecentArms[0];
            var previousArm = root;
            for (var i = 0; i < r.RecentArms.Length; i++)
            {
                var arm = r.RecentArms[i];
                if (arm.Generation <= 0 || arm.Frontier != root.Frontier || arm.Frontier <= a.FrontierChunkIndex ||
                    arm.Highest < arm.Frontier - 1 || arm.Deadline - arm.ProgressUtc != TimeSpan.FromSeconds(1) ||
                    arm.ProgressUtc != root.ProgressUtc || arm.Deadline != root.Deadline ||
                    arm.ObservedUtc < arm.ProgressUtc || arm.ObservedUtc >= arm.Deadline ||
                    i > 0 && (arm.Generation - previousArm.Generation != 1 || arm.Highest <= previousArm.Highest ||
                        arm.ObservedUtc < previousArm.ObservedUtc)) { return false; }
                previousArm = arm;
            }
            if (previousArm.Generation != r.RecentGeneration || previousArm.Deadline != r.RecentDeadline) { return false; }
            reason = "receipt_commit_chain";
            var first = r.Commits[0];
            if (first.Before != root.Frontier || first.Count <= 0 || first.ObservedUtc < r.StallStartedUtc ||
                first.ObservedUtc >= r.StallDeadline || r.PublicationDeadline - first.ObservedUtc != TimeSpan.FromSeconds(1)) { return false; }
            var frontier = first.Before;
            var highest = previousArm.Highest;
            long bytes = 0;
            var time = r.StallStartedUtc;
            foreach (var commit in r.Commits)
            {
                if (commit.Before != frontier || commit.Count < 0 || (long)commit.After - commit.Before != commit.Count ||
                    commit.Highest < highest || commit.Bytes < bytes || commit.ObservedUtc < time ||
                    commit.ObservedUtc >= r.PublicationDeadline) { return false; }
                (frontier, highest, bytes, time) = (commit.After, commit.Highest, commit.Bytes, commit.ObservedUtc);
            }
            reason = "receipt_frame_completion";
            var token = r.DeliveryToken;
            if (r.Frame.Epoch <= 0 || r.Frame.ContiguousCommittedChunkIndex != frontier ||
                r.Frame.DurableReceivedHighestChunkIndex != highest || r.Frame.BytesCommitted != bytes ||
                r.Frame.TransferPaused || r.Frame.TerminalReady || r.Frame.TransportEpoch != b.TransportEpoch ||
                r.FrameFingerprint != CalculateSnapshotFingerprint(r.Frame) || token.IsDefault || token.RuntimeOwner != owner ||
                token.ControlPlaneKind != FileTransferControlPlaneKind.ReceiverState || token.TransportEpoch != b.TransportEpoch ||
                r.CapturedUtc < time || r.CompletedUtc < r.CapturedUtc || r.CompletedUtc >= r.PublicationDeadline ||
                r.Outcome != (r.CompletedUtc < r.StallDeadline ? "PublishedBeforeOriginalDeadline" : "PublishedWithinObservationBound"))
            { return false; }
            reason = "";
            return true;
        }

        // Structural validation only. Success here does not establish producer,
        // writer or primary custody, and never creates an acceptance capability.
        internal static bool ValidateLifecycle(FrontierPublicationReceipt r, FrontierPublicationLifecycle history)
        {
            if (history.ContractVersion != 1 || !Guid.TryParseExact(history.ObservationId, "N", out _) ||
                history.Coverage != "offline_service_source_mutations_v1" || history.Outcome != r.Outcome ||
                history.SourceGraphFingerprint != CalculateSnapshotFingerprint(r.SourceGraph) ||
                history.Entries.IsDefaultOrEmpty || history.Entries.Length is < 1 or > 64 ||
                history.LastSequence != history.Entries.Length || history.Entries.Any(entry => entry is null) ||
                history.Entries.Length != r.RecentArms.Length + r.Commits.Length + 6) { return false; }
            var entries = history.Entries;
            var index = 0;
            DateTimeOffset? lastTime = null;
            bool Take(string kind, object binding, DateTimeOffset? exactTime, bool requireTime = true)
            {
                var entry = entries[index++];
                if (entry.Sequence != index || entry.Kind != kind || entry.Boundary is not null || entry.SourceFingerprint != r.Source.ScopeFingerprint ||
                    entry.BindingFingerprint != CalculateSnapshotFingerprint(binding) ||
                    requireTime != entry.ObservedUtc.HasValue || exactTime.HasValue && entry.ObservedUtc != exactTime ||
                    entry.ObservedUtc.HasValue && lastTime.HasValue && entry.ObservedUtc < lastTime) { return false; }
                if (entry.ObservedUtc.HasValue) { lastTime = entry.ObservedUtc; }
                return true;
            }
            if (!Take("RepairObserved", r.Repair, null)) { return false; }
            foreach (var arm in r.RecentArms)
            { if (!Take("RecentArmed", arm, arm.ObservedUtc)) { return false; } }
            if (!Take("StallCaptured", new { r.RecentGeneration, r.StallGeneration, r.StallStartedUtc, r.StallDeadline }, null) ||
                lastTime < r.StallStartedUtc ||
                !Take("StallArmed", new { r.RecentGeneration, r.StallGeneration }, null, requireTime: false)) { return false; }
            foreach (var commit in r.Commits)
            { if (!Take("ProgressObserved", commit, commit.ObservedUtc)) { return false; } }
            return Take("FrameCaptured", r.Frame, r.CapturedUtc) &&
                Take("DeliveryBound", r.DeliveryToken, null) &&
                Take(r.Outcome, r.Completion, r.CompletedUtc);
        }

        private static bool ValidateWriterLinks(FrontierPublicationReceipt r, string[] input)
        {
            var own = input.Where(line => Is(line, "session_id", r.Frame.SessionId) &&
                Is(line, "transfer_id", r.Frame.TransferId)).ToArray();
            bool One(string name, Func<string, bool> match) => own.Count(line => Event(line, name) && match(line)) == 1;
            var root = r.RecentArms[0];
            if (!One("filetransfer_fallback_frontier_repair_progress_observed", line =>
                Is(line, "authority_id", r.RepairAuthority.AuthorityId.ToString()) &&
                Is(line, "continuity_receipt_id", Evidence(r.Repair.ContinuityReceiptIdentity)) &&
                Is(line, "continuity_receipt_fingerprint", Evidence(r.Repair.ContinuityReceiptFingerprint)) &&
                Is(line, "continuity_origin_receipt_id", Evidence(r.Repair.OriginReceiptIdentity)) &&
                Is(line, "continuity_origin_receipt_fingerprint", Evidence(r.Repair.OriginReceiptFingerprint)) &&
                Is(line, "continuity_current_scope_fingerprint", Evidence(r.Source.ScopeFingerprint)) &&
                Number(line, "current_frontier_chunk_index", root.Frontier))) { return false; }
            foreach (var arm in r.RecentArms)
            {
                if (!One("filetransfer_v6_frontier_recent_progress_grace_deferred", line =>
                    Number(line, "wake_generation", arm.Generation) && Number(line, "frontier_chunk_index", arm.Frontier) &&
                    Number(line, "highest_received_chunk_index", arm.Highest) &&
                    Time(line, "committed_progress_utc", arm.ProgressUtc) && Time(line, "wake_deadline_utc", arm.Deadline))) { return false; }
            }
            if (!One("filetransfer_v6_frontier_request_deferred", line =>
                Number(line, "wake_generation", r.StallGeneration) && Number(line, "frontier_chunk_index", root.Frontier) &&
                Time(line, "stall_started_utc", r.StallStartedUtc) && Time(line, "wake_deadline_utc", r.StallDeadline) &&
                Is(line, "continuity_source_fingerprint", Evidence(r.Source.ScopeFingerprint))) ||
                !One("filetransfer_v6_frontier_recent_progress_grace_recheck_decided", line =>
                    Number(line, "wake_generation", r.RecentGeneration) && Number(line, "frontier_chunk_index", root.Frontier) &&
                    Is(line, "decision_status", "rechecked") && Is(line, "decision_reason", "stall_grace_armed"))) { return false; }
            foreach (var commit in r.Commits)
            {
                if (!One("filetransfer_v6_sparse_write_committed", line =>
                    Number(line, "next_chunk_index", commit.After) && Number(line, "contiguous_chunks_committed", commit.Count) &&
                    Number(line, "highest_received_chunk_index", commit.Highest))) { return false; }
            }
            return One("filetransfer_v6_receiver_state_sent", line =>
                Number(line, "epoch", r.Frame.Epoch) && Number(line, "contiguous_committed_chunk_index", r.Frame.ContiguousCommittedChunkIndex) &&
                Number(line, "durable_received_highest_chunk_index", r.Frame.DurableReceivedHighestChunkIndex) &&
                Number(line, "bytes_committed", r.Frame.BytesCommitted) && Number(line, "transport_epoch", r.Frame.TransportEpoch) &&
                Number(line, "transfer_paused", 0));
        }

        private static string Evidence(string value) => "fte1-" + Hash(value)[..24];
        private static bool Is(string line, string key, string expected) => Field(line, key, out var value) && value == expected;
        private static bool Number(string line, string key, long expected) =>
            Field(line, key, out var value) && long.TryParse(value, out var number) && number == expected;
        private static bool Time(string line, string key, DateTimeOffset expected) => Field(line, key, out var value) &&
            DateTimeOffset.TryParseExact(value, "O", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var time) && time == expected;

        private static bool Event(string line, string name) => line.Contains(" event=" + name + ";", StringComparison.Ordinal);
        private static bool Field(string line, string name, out string value)
        {
            var prefix = name + "=";
            var matches = line.Split(';').Select(segment => segment.Trim())
                .Where(segment => segment.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            value = matches.Length == 1 ? matches[0][prefix.Length..] : "";
            return matches.Length == 1;
        }
        private static string Canonical(object value) => FileTransferCoordinatorTransitionEnvelope.SerializeCanonical(value, JsonOptions);
        private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}
