using System.Text.Json;
using System.Text.Json.Nodes;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData("single")]
    [InlineData("coalesced")]
    [InlineData("late")]
    [InlineData("post_deadline_write")]
    [InlineData("newer_token")]
    [InlineData("duplicate")]
    [Trait("FileTransferRuntimeSuite", "FrontierPublicationValidation")]
    public async Task FrontierPublicationValidation_FreshProducerHasBoundFullCompactParity(string variant)
    {
        var fixture = await RunFrontierPublicationFixtureAsync(variant, true);
        var capture = FileTransferCoordinator.FrontierPublicationOfflineCapture.TryFreeze(fixture.Producer, fixture.Lines, out var reason);
        Assert.True(capture is not null, reason);
        Assert.InRange(capture!.WitnessCount, 1, 64);
        var full = capture.Validate(capture.ExportFull(), "full");
        var compact = capture.Validate(capture.ExportCompact(), "compact");
        Assert.Equal(full, compact);
        Assert.Equal("ValidatedOfflineCapture", full.Status);
        Assert.Equal(fixture.Producer.Receipt!.Outcome, full.DiagnosticOutcome);
        Assert.False(full.LiveProofEligible);
        if (variant == "newer_token") { Assert.Equal("Idempotent", fixture.Producer.Receipt!.Completion.DecisionStatus); }
        var path = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR");
        if (!string.IsNullOrWhiteSpace(path))
        {
            File.WriteAllText(Path.Combine(path, $"validated-{variant}-full.json"), capture.ExportFull());
            File.WriteAllText(Path.Combine(path, $"validated-{variant}-compact.json"), capture.ExportCompact());
        }
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FrontierPublicationValidation")]
    public async Task FrontierPublicationValidation_CandidateCannotNominateReceiptClockOrCustody()
    {
        var fixture = await RunFrontierPublicationFixtureAsync("post_deadline_write", true);
        var input = fixture.Lines.ToArray();
        var capture = FileTransferCoordinator.FrontierPublicationOfflineCapture.TryFreeze(fixture.Producer, input, out var reason);
        Assert.True(capture is not null, reason);
        var full = capture!.ExportFull();
        var compact = capture.ExportCompact();
        input[0] = "changed after freeze";
        Assert.Equal("ValidatedOfflineCapture", capture.Validate(full, "full").Status);
        foreach (var view in new[] { "full", "compact" })
        {
            var payload = view == "full" ? full : compact;
            foreach (var field in new[] { "Format", "ClockDomain", "CaptureId", "InputSha256", "WriterId", "View" })
            {
                var body = JsonNode.Parse(payload)!;
                body[field] = "forged";
                Reject(Canonical(body), view);
            }
            foreach (var path in new[]
            {
                "Repair.CoordinatorInstanceId", "Repair.ContinuityReceiptIdentity", "Repair.OriginReceiptFingerprint",
                "Source.ScopeFingerprint", "Source.Boundary.DataSessionIdentity", "SourceRuntimeOwner.SessionId",
                "SourcePublication.TransferId", "FrameFingerprint", "Frame.SessionId", "Frame.TransferId", "Outcome",
                "CapturedUtc", "CompletedUtc", "PublicationDeadline", "StallDeadline", "RecentDeadline",
                "DeliveryToken.AuthorityId.Value", "Completion.TransitionId", "Completion.DecisionStatus",
            })
            {
                var body = JsonNode.Parse(payload)!;
                var node = body["Receipt"]!;
                var parts = path.Split('.');
                foreach (var part in parts[..^1]) { node = node[part]!; }
                node[parts[^1]] = "forged";
                // A self-declared hash recalculation cannot replace the producer's expectation.
                body["InputSha256"] = new string('a', 64);
                Reject(Canonical(body), view);
            }
            foreach (var collection in new[] { "Commits", "RecentArms" })
            {
                var body = JsonNode.Parse(payload)!;
                body["Receipt"]![collection]!.AsArray().RemoveAt(0);
                Reject(Canonical(body), view);
            }
            var missing = JsonNode.Parse(payload)!;
            missing["Records"]!.AsArray().RemoveAt(0);
            Reject(Canonical(missing), view);
            var unknown = JsonNode.Parse(payload)!;
            unknown["AuthorityAccepted"] = true;
            Reject(Canonical(unknown), view);
            Reject(payload[..^1] + ",\"ClockDomain\":\"live_utc\"}", view);
            Reject(payload + " ", view);
            Reject("{}", view);
            Reject(null, view);
        }
        Reject(full, "compact");
        Reject(compact, "full");
        Reject(full, "live");
        var other = FileTransferCoordinator.FrontierPublicationOfflineCapture.TryFreeze(fixture.Producer, fixture.Lines, out reason);
        Assert.NotNull(other);
        Reject(other!.ExportFull(), "full");
        Assert.Equal("ValidatedOfflineCapture", capture.Validate(full, "full").Status);

        void Reject(string? payload, string view)
        {
            var result = capture.Validate(payload, view);
            Assert.Equal("EvidenceIncomplete", result.Status);
            Assert.Null(result.DiagnosticOutcome);
            Assert.False(result.LiveProofEligible);
        }
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FrontierPublicationValidation")]
    public async Task FrontierPublicationValidation_MissingConflictingAndWrongWriterInputsFailClosed()
    {
        var fixture = await RunFrontierPublicationFixtureAsync("single", true);
        var original = fixture.Lines.ToArray();
        var producer = fixture.Producer;
        var capture = FileTransferCoordinator.FrontierPublicationOfflineCapture.TryFreeze(producer, original, out var reason);
        Assert.True(capture is not null, reason);
        Assert.Null(FileTransferCoordinator.FrontierPublicationOfflineCapture.TryFreeze(
            new(() => DateTimeOffset.UtcNow), original, out reason));
        Assert.Equal("producer_receipt_missing", reason);
        foreach (var name in new[]
        {
            "filetransfer_coordinator_snapshot_stream_started", "filetransfer_coordinator_snapshot_stream_completed",
            "filetransfer_fallback_frontier_repair_progress_observed", "filetransfer_v6_frontier_recent_progress_grace_deferred",
            "filetransfer_v6_frontier_recent_progress_grace_recheck_decided", "filetransfer_v6_frontier_request_deferred",
            "filetransfer_v6_sparse_write_committed", "filetransfer_v6_receiver_state_sent",
        })
        {
            var missing = original.Where(line => !line.Contains("event=" + name + ";", StringComparison.Ordinal)).ToArray();
            Reject(missing);
        }
        var completion = SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting(producer.Receipt!.Completion);
        var index = Array.FindIndex(original, line => line.Contains("snapshot_sha256=" + completion.CalculatePayloadSha256(), StringComparison.Ordinal));
        Assert.True(index >= 0);
        var duplicate = original.ToList();
        duplicate.Insert(index + 1, original[index]);
        Assert.NotNull(FileTransferCoordinator.FrontierPublicationOfflineCapture.TryFreeze(producer, duplicate, out reason));
        duplicate[index + 1] = original[index].Replace("authority_effect=none", "authority_effect=forged", StringComparison.Ordinal);
        Reject(duplicate);
        var wrong = original.ToArray();
        wrong[index] = wrong[index].Replace("snapshot_sha256=" + completion.CalculatePayloadSha256(), "snapshot_sha256=" + new string('0', 64));
        Reject(wrong);
        wrong = original.ToArray();
        var writer = GetDelimitedLogField(wrong[index], "log_writer_id");
        wrong[index] = wrong[index].Replace("log_writer_id=" + writer, "log_writer_id=wrong_writer", StringComparison.Ordinal);
        Reject(wrong);
        wrong = original.ToArray();
        wrong[index] += "; log_writer_sequence=999999";
        Reject(wrong);
        wrong = original.ToArray();
        wrong[index] += "; event=filetransfer_coordinator_snapshot_stream_completed";
        Reject(wrong);
        wrong = original.Reverse().ToArray();
        Reject(wrong);
        wrong = original.Where((_, i) => i != index - 1).ToArray();
        Reject(wrong);
        Reject(original.Take(index).ToArray());
        var delayedWriter = original.Select(line =>
        {
            var ticks = GetDelimitedLogField(line, "log_writer_attempt_utc_ticks");
            return line.Replace("log_writer_attempt_utc_ticks=" + ticks,
                "log_writer_attempt_utc_ticks=" + (long.Parse(ticks) + TimeSpan.TicksPerHour), StringComparison.Ordinal);
        }).ToArray();
        var delayed = FileTransferCoordinator.FrontierPublicationOfflineCapture.TryFreeze(producer, delayedWriter, out reason);
        Assert.True(delayed is not null, reason);
        Assert.Equal(producer.Receipt.Outcome, delayed!.Validate(delayed.ExportFull(), "full").DiagnosticOutcome);
        var overBudget = original.ToList();
        var diagnostic = original.First(line => line.Contains("event=filetransfer_coordinator_authority_effect;", StringComparison.Ordinal));
        overBudget.InsertRange(overBudget.Count - 1, Enumerable.Repeat(diagnostic, 65));
        var firstSequence = long.Parse(GetDelimitedLogField(original[0], "log_writer_sequence"));
        for (var i = 0; i < overBudget.Count; i++)
        {
            var prior = GetDelimitedLogField(overBudget[i], "log_writer_sequence");
            overBudget[i] = overBudget[i].Replace("log_writer_sequence=" + prior + ";",
                "log_writer_sequence=" + (firstSequence + i) + ";", StringComparison.Ordinal);
        }
        Assert.Null(FileTransferCoordinator.FrontierPublicationOfflineCapture.TryFreeze(producer, overBudget, out reason));
        Assert.Equal("witness_budget", reason);
        // The final completion remains bound to its original line, not the most
        // convenient copy in another writer or after a conflicting duplicate.
        Assert.Equal("ValidatedOfflineCapture", capture!.Validate(capture.ExportCompact(), "compact").Status);

        void Reject(IReadOnlyList<string> lines) => Assert.Null(
            FileTransferCoordinator.FrontierPublicationOfflineCapture.TryFreeze(producer, lines, out _));
    }

    private static string Canonical(JsonNode node) => FileTransferCoordinatorTransitionEnvelope.SerializeCanonical(node, new JsonSerializerOptions());
}
