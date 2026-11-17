using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Stamp = NLink.Core.FileTransfer.FileTransferCoordinator.FrontierPublicationBoundaryStamp;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private sealed partial class FrontierBoundaryObservation
    {
        // Called only after closure, outside the service gate. This binds private
        // producer data to diagnostic input lines, NOT native writer/parent custody.
        public sealed class ClosedDiagnosticCapture(string full, string compact, string outcome, int count)
        {
            public int WitnessCount => count;
            public string ExportFull() => full;
            public string ExportCompact() => compact;
            public DiagnosticValidation Validate(string? candidate, string view)
                => (view == "full" ? full : view == "compact" ? compact : null) is { } expected &&
                   string.Equals(candidate, expected, StringComparison.Ordinal)
                    ? new("ValidatedPrivateBoundaryDiagnostic", "producer_bound_mapping_only", outcome)
                    : new("EvidenceIncomplete", "frozen_capture_mismatch", null);
        }

        public sealed record DiagnosticValidation(string Status, string Reason, string? DiagnosticOutcome)
        {
            public bool LiveProofEligible => false;
            public bool NativeWriterCustodyEstablished => false;
            public bool PublicationCompletionIssued => false;
        }

        private static readonly JsonSerializerOptions BoundaryJson = new() { IncludeFields = true };
        private static string CanonicalBoundary(object value)
            => FileTransferCoordinatorTransitionEnvelope.SerializeCanonical(value, BoundaryJson);
        private static string BoundaryHash(string value)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

        public ClosedDiagnosticCapture? TryFreeze(IReadOnlyList<string> input, out string reason)
        {
            reason = "producer_not_closed_positive";
            try
            {
                if (!closed || Timing?.Outcome != Status || Status is not
                    ("PublishedBeforeOriginalDeadline" or "PublishedWithinObservationBound")) { return null; }
                reason = "producer_lifecycle";
                if (!ValidateClosedBindings(out var receipt, out var completion)) { return null; }
                reason = "input_capacity";
                var count = input.Count;
                if (count is < 1 or > 8192) { return null; }
                var lines = new string[count];
                long codeUnits = 0;
                for (var i = 0; i < count; i++)
                {
                    // Validate the exact immutable string being retained, not a
                    // previous enumeration of the caller's mutable collection.
                    var line = input[i];
                    if (line is null || line.Length > 262144 || line.IndexOfAny(['\r', '\n']) >= 0 ||
                        (codeUnits += line.Length) > 8 * 1024 * 1024) { return null; }
                    lines[i] = line;
                }
                if (input.Count != count) { return null; }
                var evidence = CreateCoordinatorSnapshotEvidence(completion!);
                var selected = MapBoundaryLines(lines, receipt!, evidence, out reason);
                if (selected is null) { return null; }
                reason = "capture_projection";
                var lifecycle = entries.Select((entry, index) => new
                {
                    Sequence = index + 1, entry.Kind, entry.Clock,
                    Binding = ProjectBoundaryBinding(entry),
                }).ToArray();
                var producer = new
                {
                    ObservationId = observationId, ClockInstanceId = clockId, ProcessId = processId,
                    ProcessCreationFileTime = processCreation, Frequency = frequency,
                    SourceSha256 = BoundaryHash(CanonicalBoundary(source!)), Repair = capturedRepair,
                    Progress = progress, RootGeneration = root!.WakeGeneration,
                    RecheckedGeneration = recheckedTip!.WakeGeneration, FirstWriteGeneration = writeTip!.WakeGeneration,
                    OriginalDeadlineUtcTicks = root.DueUtc.UtcTicks,
                    PublicationDeadlineUtcTicks = checked(firstWrite!.Clock.UtcTicks + TimeSpan.TicksPerSecond),
                    Outcome = Status, Lifecycle = lifecycle,
                    CompletionSha256 = evidence.CalculatePayloadSha256(), CompletionPayload = evidence.ToBase64UrlPayload(),
                };
                var producerJson = JsonSerializer.Deserialize<JsonElement>(CanonicalBoundary(producer));
                var captureId = Guid.NewGuid();
                var inputHash = BoundaryHash(CanonicalBoundary(lines));
                string Project(string view, object records) => CanonicalBoundary(new
                {
                    Format = "private_frontier_boundary_diagnostic_v1",
                    ClockDomain = FileTransferCoordinator.FrontierPublicationBoundaryTiming.Profile,
                    CaptureId = captureId, View = view, Producer = producerJson,
                    LiveProofEligible = false, NativeWriterCustodyEstablished = false, PublicationCompletionIssued = false,
                    InputHashDomain = "sha256-utf8-canonical-json-lines-v1", InputSha256 = inputHash,
                    InputRecordCount = lines.Length, Records = records,
                });
                var full = Project("full", lines);
                var compact = Project("compact", selected);
                reason = "";
                return new(full, compact, Status, selected.Length);
            }
            catch (Exception) { return null; }
        }

        private object? ProjectBoundaryBinding(Entry entry) => entry.Binding switch
        {
            InboundFallbackFrontierProgressReceipt r => new { r.Authority, r.PreviousFrontierChunkIndex,
                r.CurrentFrontierChunkIndex, r.DataSessionIdentity, r.DataSessionGeneration, Repair = capturedRepair },
            InboundV6FrontierRecentProgressGraceWake w => w with { DataSession = null! },
            InboundV6FrontierStallGraceWake w => w with { DataSession = null! },
            ValueTuple<InboundV6FrontierStallGraceWake, InboundV6FrontierStallGraceWake> p =>
                new { PredecessorGeneration = p.Item1.WakeGeneration, Successor = p.Item2 with { DataSession = null! } },
            ValueTuple<InboundV6FrontierRecentProgressGraceWake, InboundV6FrontierStallGraceWake> p =>
                new { RecentGeneration = p.Item1.WakeGeneration, StallGeneration = p.Item2.WakeGeneration },
            FileTransferCoordinatorTransitionEnvelope e => new { e.TransitionId, e.CoordinatorInstanceId,
                e.DecisionKind, e.DecisionStatus },
            _ => entry.Binding,
        };

        private bool ValidateClosedBindings(out InboundFallbackFrontierProgressReceipt? receipt,
            out FileTransferCoordinatorTransitionEnvelope? completion)
        {
            receipt = null; completion = null;
            if (entries.Count is < 8 or > 64 || capturedRepair is null || progress is null || firstWrite is null ||
                root is null || recent is null || recheckedTip is null || writeTip is null || frozenFrame is null ||
                source is null || token is null || stallClock is null || frameClock is null || bindingClock is null ||
                entries[0] is not { Kind: "RepairProgress", Binding: InboundFallbackFrontierProgressReceipt r } ||
                entries[0].Clock != progress.Clock || entries[^1].Kind != Status ||
                entries[^1].Binding is not null || entries[^1].Clock is not null) { return false; }
            var authority = r.Authority;
            if (authority.IsDefault || capturedRepair.RepairAuthorityId != authority.AuthorityId.ToString() ||
                capturedRepair.RepairGeneration != authority.DecisionGeneration ||
                capturedRepair.ProtocolRepairRequestId != r.ProtocolRepairRequestId ||
                capturedRepair.RequestCorrelationId != r.RequestCorrelationId ||
                r.PreviousFrontierChunkIndex != progress.Before || r.CurrentFrontierChunkIndex != progress.After ||
                progress.Count <= 0 || (long)progress.After - progress.Before != progress.Count ||
                progress.After <= authority.FrontierChunkIndex || progress.Bytes <= 0 ||
                root.FrontierChunkIndex != progress.After || root.RuntimeOwner != authority.RuntimeOwner ||
                root.RoutePublication.RuntimeOwner != root.RuntimeOwner || root.ProtocolVersion != 6 ||
                root.Route != FileTransferRoute.PostTunaFallbackV6 || root.TerminalGeneration != 0 ||
                !ReferenceEquals(root.DataSession, r.DataSession) || root.DataSessionGeneration != r.DataSessionGeneration ||
                root.DataSessionIdentity != r.DataSessionIdentity || root.SessionId != authority.SessionId ||
                root.TransferId != authority.TransferId || root.CheckpointContinuitySource?.CoordinatorInstanceId != capturedRepair.CoordinatorInstanceId ||
                root.CheckpointContinuitySource.SourceScopeFingerprint != capturedRepair.CurrentScopeFingerprint) { return false; }
            var index = 1;
            InboundV6FrontierRecentProgressGraceWake? priorRecent = null;
            while (index < entries.Count && entries[index].Kind == "RecentArmed")
            {
                if (entries[index].Clock is not null || entries[index++].Binding is not InboundV6FrontierRecentProgressGraceWake arm ||
                    !ReferenceEquals(arm.DataSession, r.DataSession) || arm.FrontierChunkIndex != progress.After ||
                    arm.CommittedProgressUtc.UtcTicks != progress.Clock.UtcTicks || arm.DueUtc != arm.CommittedProgressUtc.AddSeconds(1) ||
                    arm.RuntimeOwner != root.RuntimeOwner || arm.RoutePublication != root.RoutePublication ||
                    arm.DataSessionGeneration != root.DataSessionGeneration || arm.TransportEpoch != root.TransportEpoch ||
                    priorRecent is not null && (arm.WakeGeneration - priorRecent.WakeGeneration != 1 ||
                        arm.HighestReceivedChunkIndex <= priorRecent.HighestReceivedChunkIndex ||
                        arm with { WakeGeneration = priorRecent.WakeGeneration, HighestReceivedChunkIndex = priorRecent.HighestReceivedChunkIndex } != priorRecent))
                { return false; }
                priorRecent = arm;
            }
            if (!ReferenceEquals(priorRecent, recent) || !Take("StallCaptured", root, stallClock)) { return false; }
            var current = root;
            var rechecked = false;
            while (index < entries.Count && entries[index].Kind is "StallSuccessor" or "RecentRechecked")
            {
                var entry = entries[index++];
                if (entry.Clock is not null) { return false; }
                if (entry.Kind == "RecentRechecked")
                {
                    if (rechecked || entry.Binding is not ValueTuple<InboundV6FrontierRecentProgressGraceWake, InboundV6FrontierStallGraceWake> link ||
                        !ReferenceEquals(link.Item1, recent) || !ReferenceEquals(link.Item2, current) || !ReferenceEquals(current, recheckedTip))
                    { return false; }
                    rechecked = true;
                }
                else
                {
                    if (entry.Binding is not ValueTuple<InboundV6FrontierStallGraceWake, InboundV6FrontierStallGraceWake> pair ||
                        !ReferenceEquals(pair.Item1, current) || current.WakeGeneration == long.MaxValue ||
                        pair.Item2.WakeGeneration != current.WakeGeneration + 1 || pair.Item2.HighestReceivedChunkIndex <= current.HighestReceivedChunkIndex ||
                        pair.Item2 with { WakeGeneration = current.WakeGeneration, HighestReceivedChunkIndex = current.HighestReceivedChunkIndex } != current)
                    { return false; }
                    current = pair.Item2;
                }
            }
            if (!rechecked || !ReferenceEquals(current, writeTip) || !ReferenceEquals(current, tip)) { return false; }
            var writes = new List<Stamp>();
            var frontier = progress.After; var highest = current.HighestReceivedChunkIndex; var bytes = progress.Bytes;
            while (index < entries.Count && entries[index].Kind == "Write")
            {
                var entry = entries[index++];
                if (entry.Binding is not WriteBoundary write || entry.Clock != write.Clock || write.Before != frontier ||
                    (long)write.After - write.Before != write.Count || write.Count < 0 || write.Highest < highest || write.Bytes < bytes ||
                    write.Path is not ("v6_sparse" or "v4_sparse") ||
                    writes.Count == 0 && (!ReferenceEquals(write, firstWrite) || write.Count <= 0)) { return false; }
                (frontier, highest, bytes) = (write.After, write.Highest, write.Bytes);
                writes.Add(write.Clock);
            }
            if (writes.Count == 0 || index >= entries.Count || entries[index].Binding is not FileTransferReceiverStateFrameV6 frame ||
                frame != frozenFrame || !frame.MissingRanges.SequenceEqual(missingRanges) ||
                frame.SessionId != root.SessionId || frame.TransferId != root.TransferId || frame.Epoch <= 0 ||
                frame.TransportEpoch != root.TransportEpoch || frame.TransferPaused || frame.TerminalReady ||
                frame.ContiguousCommittedChunkIndex != frontier || frame.DurableReceivedHighestChunkIndex != highest || frame.BytesCommitted != bytes ||
                !Take("FrameCaptured", frame, frameClock) || !Take("DeliveryBound", token.Value, bindingClock) ||
                token.Value.IsDefault || token.Value.RuntimeOwner != root.RuntimeOwner || token.Value.TransportEpoch != root.TransportEpoch ||
                token.Value.ControlPlaneKind != FileTransferControlPlaneKind.ReceiverState || index != entries.Count - 2 ||
                entries[index] is not { Kind: "AcceptedCompletion", Binding: FileTransferCoordinatorTransitionEnvelope end, Clock: { } endClock } ||
                end.CoordinatorInstanceId != capturedRepair.CoordinatorInstanceId || end.SessionId != root.SessionId ||
                end.TransferId != root.TransferId || end.Direction != "inbound" || end.DecisionKind != "FallbackControlPlaneSendCompleted" ||
                end.DecisionStatus is not ("Accepted" or "Idempotent")) { return false; }
            var timing = FileTransferCoordinator.FrontierPublicationBoundaryTiming.Evaluate(progress.Clock, recent.DueUtc.UtcTicks,
                stallClock.Value, root.DueUtc.UtcTicks, writes.ToArray(), checked(firstWrite.Clock.UtcTicks + TimeSpan.TicksPerSecond),
                frameClock.Value, bindingClock.Value, endClock);
            if (timing != Timing || timing.Outcome != Status || writes.Append(progress.Clock).Append(stallClock.Value)
                .Append(frameClock.Value).Append(bindingClock.Value).Append(endClock).Any(s => s.ProcessId != processId ||
                    s.ProcessCreationFileTime != processCreation || s.ClockInstanceId != clockId || s.ObservationId != observationId || s.Frequency != frequency))
            { return false; }
            receipt = r; completion = end;
            return true;

            bool Take(string kind, object binding, Stamp? clock)
            {
                if (index >= entries.Count) { return false; }
                var entry = entries[index++];
                var same = binding is InboundV6FrontierStallGraceWake or FileTransferReceiverStateFrameV6
                    ? ReferenceEquals(entry.Binding, binding) : Equals(entry.Binding, binding);
                return entry.Kind == kind && same && entry.Clock == clock;
            }
        }

        private sealed record BoundaryLine(int InputLine, long WriterSequence, string RawLine,
            Dictionary<string, string> Fields, HashSet<string> DuplicateFields);
        private sealed record BoundaryWitness(int InputLine, long WriterSequence, string ContentSha256, string RawLine);

        private BoundaryWitness[]? MapBoundaryLines(string[] lines, InboundFallbackFrontierProgressReceipt repair,
            FileTransferCoordinatorTransitionEnvelope evidence, out string reason)
        {
            reason = "writer_structure";
            try
            {
                var rows = new List<BoundaryLine>();
                var seen = new Dictionary<long, string>();
                string? writer = null;
                long previous = 0;
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    var start = line.IndexOf(" event=", StringComparison.Ordinal);
                    Require(start >= 0 && line.IndexOf(" event=", start + 1, StringComparison.Ordinal) < 0, "writer_event");
                    var parts = line[(start + 1)..].Split(';');
                    Require(parts.Length <= 256, "writer_fields");
                    var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                    var duplicateFields = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var part in parts)
                    {
                        var pair = part.Trim().Split('=', 2);
                        Require(pair.Length == 2 && pair[0].Length > 0, "writer_fields");
                        if (!fields.TryAdd(pair[0], pair[1]))
                        {
                            duplicateFields.Add(pair[0]);
                            Require(pair[0] is not ("event" or "session_id" or "transfer_id" or "coordinator_instance_id") &&
                                !pair[0].StartsWith("log_writer_", StringComparison.Ordinal), "writer_duplicate_identity");
                        }
                    }
                    var id = Value(fields, "log_writer_id");
                    Require(id.Length > 0 && (writer is null || id == writer) &&
                        long.TryParse(Value(fields, "log_writer_sequence"), NumberStyles.None, CultureInfo.InvariantCulture, out _), "writer_identity");
                    var sequence = long.Parse(fields["log_writer_sequence"], CultureInfo.InvariantCulture);
                    Require(sequence > 0 && long.TryParse(Value(fields, "log_writer_attempt_utc_ticks"), NumberStyles.None,
                        CultureInfo.InvariantCulture, out var ticks) && ticks > 0 && ticks <= DateTimeOffset.MaxValue.Ticks, "writer_range");
                    writer = id;
                    if (seen.TryGetValue(sequence, out var prior))
                    { Require(prior == line, "writer_conflicting_copy"); continue; }
                    Require(previous == 0 || sequence - previous == 1, "writer_order_or_gap");
                    previous = sequence;
                    seen.Add(sequence, line);
                    rows.Add(new(i + 1, sequence, line, fields, duplicateFields));
                }
                var selected = new SortedDictionary<int, BoundaryWitness>();
                bool Own(Dictionary<string, string> f) => Value(f, "session_id") == root!.SessionId && Value(f, "transfer_id") == root.TransferId;
                bool Coordinator(Dictionary<string, string> f) => Value(f, "coordinator_instance_id") == evidence.CoordinatorInstanceId;
                var progressRow = One("filetransfer_fallback_frontier_repair_progress_observed", f => Own(f) &&
                    Value(f, "authority_id") == repair.Authority.AuthorityId.ToString() && Number(f, "current_frontier_chunk_index", progress!.After),
                    f => Number(f, "previous_frontier_chunk_index", progress!.Before) && Number(f, "repair_generation", capturedRepair!.RepairGeneration) &&
                        Value(f, "continuity_receipt_id") == CheckpointContinuityEvidenceToken(capturedRepair.ContinuityReceiptIdentity) &&
                        Value(f, "continuity_receipt_fingerprint") == CheckpointContinuityEvidenceToken(capturedRepair.ContinuityReceiptFingerprint) &&
                        Value(f, "continuity_origin_receipt_id") == CheckpointContinuityEvidenceToken(capturedRepair.OriginReceiptIdentity) &&
                        Value(f, "continuity_origin_receipt_fingerprint") == CheckpointContinuityEvidenceToken(capturedRepair.OriginReceiptFingerprint) &&
                        Value(f, "continuity_current_scope_fingerprint") == CheckpointContinuityEvidenceToken(capturedRepair.CurrentScopeFingerprint), "repair_progress");
                var startRow = One("filetransfer_coordinator_snapshot_stream_started", Coordinator, _ => true, "stream_start");
                var endRow = One("filetransfer_coordinator_snapshot_stream_completed", Coordinator, _ => true, "stream_end");
                var completionRow = One("filetransfer_coordinator_transition_snapshot",
                    f => Value(f, "transition_id") == evidence.TransitionId,
                    f => Coordinator(f) && Value(f, "snapshot_sha256") == evidence.CalculatePayloadSha256() &&
                        Value(f, "snapshot_payload") == evidence.ToBase64UrlPayload(), "completion");
                Require(startRow.InputLine < progressRow.InputLine && progressRow.InputLine < completionRow.InputLine &&
                    completionRow.InputLine < endRow.InputLine, "completion_stream_order");
                foreach (var entry in entries)
                {
                    if (entry.Kind == "RecentArmed" && entry.Binding is InboundV6FrontierRecentProgressGraceWake arm)
                    {
                        One("filetransfer_v6_frontier_recent_progress_grace_deferred", f => Own(f) && Number(f, "wake_generation", arm.WakeGeneration),
                            f => Number(f, "frontier_chunk_index", arm.FrontierChunkIndex) && Number(f, "highest_received_chunk_index", arm.HighestReceivedChunkIndex) &&
                                Time(f, "committed_progress_utc", arm.CommittedProgressUtc) && Time(f, "wake_deadline_utc", arm.DueUtc), "recent_arm");
                    }
                    if (entry.Kind == "StallCaptured")
                    {
                        One("filetransfer_v6_frontier_request_deferred", f => Own(f) && Number(f, "wake_generation", root!.WakeGeneration),
                            f => StallFields(f, root!), "stall_root");
                    }
                    if (entry.Kind == "StallSuccessor" && entry.Binding is ValueTuple<InboundV6FrontierStallGraceWake, InboundV6FrontierStallGraceWake> pair)
                    {
                        One("filetransfer_v6_frontier_stall_grace_recheck_decided", f => Own(f) &&
                            Number(f, "wake_generation", pair.Item2.WakeGeneration) && Value(f, "decision_status") == "rearmed",
                            f => StallFields(f, pair.Item2) && Value(f, "decision_reason") == "highest_received_advanced", "stall_successor");
                        OneWrite(pair.Item2.FrontierChunkIndex, pair.Item2.HighestReceivedChunkIndex, 0, null);
                    }
                    if (entry.Kind == "RecentRechecked")
                    {
                        One("filetransfer_v6_frontier_recent_progress_grace_recheck_decided", f => Own(f) &&
                            Number(f, "wake_generation", recent!.WakeGeneration) && Value(f, "decision_status") == "rechecked",
                            f => Number(f, "frontier_chunk_index", recent!.FrontierChunkIndex) &&
                                Time(f, "committed_progress_utc", recent.CommittedProgressUtc) && Time(f, "wake_deadline_utc", recent.DueUtc) &&
                                Value(f, "decision_reason") == "stall_grace_armed", "recent_recheck");
                    }
                    if (entry.Kind == "Write" && entry.Binding is WriteBoundary write)
                    { OneWrite(write.After, write.Highest, write.Count, write.Path); }
                }
                OneWrite(progress!.After, progress.Highest, progress.Count, progress.Path);
                var stateRow = One("filetransfer_v6_receiver_state_sent", f => Own(f) && Number(f, "epoch", frozenFrame!.Epoch) &&
                    Number(f, "transport_epoch", frozenFrame.TransportEpoch), f =>
                        Number(f, "contiguous_committed_chunk_index", frozenFrame!.ContiguousCommittedChunkIndex) &&
                        Number(f, "durable_received_highest_chunk_index", frozenFrame.DurableReceivedHighestChunkIndex) &&
                        Number(f, "bytes_committed", frozenFrame.BytesCommitted) && Number(f, "transfer_paused", 0), "state_identity");
                Require(stateRow.InputLine > completionRow.InputLine && stateRow.InputLine < endRow.InputLine, "state_order");
                foreach (var row in rows)
                {
                    var f = row.Fields;
                    var name = Value(f, "event");
                    var episode = row.InputLine >= progressRow.InputLine && row.InputLine <= stateRow.InputLine;
                    var repairLink = row.InputLine < progressRow.InputLine && Own(f) &&
                        Value(f, "authority_id") == repair.Authority.AuthorityId.ToString() && Number(f, "repair_generation", capturedRepair!.RepairGeneration) &&
                        (name.StartsWith("filetransfer_fallback_frontier_repair_", StringComparison.Ordinal) || name == "filetransfer_v6_frontier_request_sent");
                    if (repairLink)
                    {
                        Require(!row.DuplicateFields.Contains("authority_id") && !row.DuplicateFields.Contains("repair_generation"),
                            "selected_duplicate_identity");
                    }
                    if (repairLink || Coordinator(f) && row.InputLine >= progressRow.InputLine && row.InputLine <= completionRow.InputLine ||
                        Own(f) && episode && (name.StartsWith("filetransfer_fallback_frontier_repair_", StringComparison.Ordinal) ||
                            name.StartsWith("filetransfer_v6_frontier_", StringComparison.Ordinal) || name.StartsWith("filetransfer_v6_receiver_state_", StringComparison.Ordinal) ||
                            name is "filetransfer_control_plane_delivery_observed" or "filetransfer_v6_fallback_frontier_repair_progress_state_forced"))
                    { Select(row, Coordinator(f)); }
                }
                reason = "";
                return selected.Values.ToArray();

                void Select(BoundaryLine row, bool interpreted)
                {
                    // Opaque coverage diagnostics may repeat non-identity fields.
                    // Exact producer links and Coordinator records remain strict.
                    Require(!interpreted || row.DuplicateFields.Count == 0, "selected_duplicate_field");
                    selected.TryAdd(row.InputLine, new(row.InputLine, row.WriterSequence, BoundaryHash(row.RawLine), row.RawLine));
                    Require(selected.Count <= 64, "witness_budget");
                }
                BoundaryLine One(string name, Func<Dictionary<string, string>, bool> identity,
                    Func<Dictionary<string, string>, bool> valid, string role)
                {
                    var matches = rows.Where(r => Value(r.Fields, "event") == name && identity(r.Fields)).ToArray();
                    Require(matches.Length == 1, "writer_" + role + "_missing_or_ambiguous");
                    Require(valid(matches[0].Fields), "writer_" + role + "_mismatch");
                    Select(matches[0], true);
                    return matches[0];
                }
                void OneWrite(int after, int highest, int count, string? path)
                {
                    var matches = rows.Where(r => Own(r.Fields) && Value(r.Fields, "event") is
                        "filetransfer_v6_sparse_write_committed" or "filetransfer_v4_sparse_write_committed" &&
                        Number(r.Fields, "next_chunk_index", after) && Number(r.Fields, "highest_received_chunk_index", highest)).ToArray();
                    Require(matches.Length == 1, "writer_write_missing_or_ambiguous");
                    var row = matches[0];
                    Require(Number(row.Fields, "contiguous_chunks_committed", count) &&
                        (path is null || Value(row.Fields, "event") == "filetransfer_" + path + "_write_committed"), "writer_write_mismatch");
                    Select(row, true);
                }
                bool StallFields(Dictionary<string, string> f, InboundV6FrontierStallGraceWake wake)
                    => Number(f, "frontier_chunk_index", wake.FrontierChunkIndex) && Number(f, "highest_received_chunk_index", wake.HighestReceivedChunkIndex) &&
                       Time(f, "stall_started_utc", wake.StallStartedUtc) && Time(f, "wake_deadline_utc", wake.DueUtc) &&
                       Value(f, "continuity_source_fingerprint") == CheckpointContinuityEvidenceToken(capturedRepair!.CurrentScopeFingerprint) &&
                       Value(f, "route") == wake.RouteToken && Number(f, "protocol_version", wake.ProtocolVersion) &&
                       Number(f, "runtime_generation", wake.RuntimeGeneration) && Number(f, "transport_epoch", wake.TransportEpoch) &&
                       Value(f, "data_session_identity") == wake.DataSessionIdentity && Number(f, "data_session_generation", wake.DataSessionGeneration);
            }
            catch (InvalidDataException ex) { reason = ex.Message; return null; }
        }

        private static void Require(bool valid, string reason) { if (!valid) { throw new InvalidDataException(reason); } }
        private static string Value(Dictionary<string, string> fields, string key) => fields.GetValueOrDefault(key, "");
        private static bool Number(Dictionary<string, string> fields, string key, long expected)
            => long.TryParse(Value(fields, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value == expected;
        private static bool Time(Dictionary<string, string> fields, string key, DateTimeOffset expected)
            => DateTimeOffset.TryParseExact(Value(fields, key), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) && value == expected;
    }
}
