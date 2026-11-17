using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData("v6", "positive")]
    [InlineData("v4", "positive")]
    [InlineData("v6", "successors")]
    [InlineData("v4", "successors")]
    [InlineData("v6", "before_recheck")]
    [InlineData("v4", "before_recheck")]
    [Trait("FileTransferRuntimeSuite", "FrontierBoundaryValidation")]
    public async Task FrontierBoundaryValidation_ClosedProducerMapsExactDiagnosticLines(string path, string variant)
    {
        var fixture = await RunFrontierBoundaryAsync(path, variant, true);
        var capture = FreezeBoundary(fixture.Observer, fixture.Lines, out var reason);
        Assert.True(capture is not null, reason);
        var full = BoundaryExport(capture!, "Full");
        var compact = BoundaryExport(capture!, "Compact");
        AssertBoundaryValidated(capture!, full, "full");
        AssertBoundaryValidated(capture!, compact, "compact");
        using var f = JsonDocument.Parse(full);
        using var c = JsonDocument.Parse(compact);
        Assert.Equal(f.RootElement.GetProperty("Producer").GetRawText(), c.RootElement.GetProperty("Producer").GetRawText());
        Assert.Equal(FileTransferCoordinator.FrontierPublicationBoundaryTiming.Profile, f.RootElement.GetProperty("ClockDomain").GetString());
        var witnesses = c.RootElement.GetProperty("Records").EnumerateArray().ToArray();
        Assert.InRange(witnesses.Length, 1, 64);
        Assert.Equal(witnesses.Length, GetPrivateProperty<int>(capture!, "WitnessCount"));
        var indices = witnesses.Select(w => w.GetProperty("InputLine").GetInt32()).ToArray();
        Assert.Equal(indices.Order().Distinct(), indices);
        foreach (var witness in witnesses)
        {
            var line = fixture.Lines[witness.GetProperty("InputLine").GetInt32() - 1];
            Assert.Equal(line, witness.GetProperty("RawLine").GetString());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line))).ToLowerInvariant(),
                witness.GetProperty("ContentSha256").GetString());
        }
        Assert.Contains(witnesses, w => w.GetProperty("RawLine").GetString()!.Contains(
            "event=filetransfer_v6_fallback_frontier_repair_progress_state_forced;", StringComparison.Ordinal));
        var directory = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            File.WriteAllText(Path.Combine(directory, $"{path}-{variant}-full.json"), full);
            File.WriteAllText(Path.Combine(directory, $"{path}-{variant}-compact.json"), compact);
        }
    }

    [Theory]
    [InlineData("v6")]
    [InlineData("v4")]
    [Trait("FileTransferRuntimeSuite", "FrontierBoundaryValidation")]
    public async Task FrontierBoundaryValidation_UnrelatedHistoryDoesNotConsumeWitnessBudget(string path)
    {
        var normal = await RunFrontierBoundaryAsync(path, "positive", true);
        var normalCapture = FreezeBoundary(normal.Observer, normal.Lines, out var reason);
        Assert.True(normalCapture is not null, reason);
        var history = await RunFrontierBoundaryAsync(path, "positive", true, priorStateSends: 16, laterStateSends: 16);
        var historyCapture = FreezeBoundary(history.Observer, history.Lines, out reason);
        Assert.True(historyCapture is not null, reason);
        Assert.True(history.Lines.Count > normal.Lines.Count + 100);
        using var compact = JsonDocument.Parse(BoundaryExport(historyCapture!, "Compact"));
        var witnesses = compact.RootElement.GetProperty("Records").EnumerateArray().ToArray();
        Assert.InRange(witnesses.Length, 1, 64);
        var progress = history.Lines.ToList().FindIndex(l => l.Contains("event=filetransfer_fallback_frontier_repair_progress_observed;")) + 1;
        var completion = history.Lines.ToList().FindIndex(l => l.Contains("snapshot_sha256=" +
            compact.RootElement.GetProperty("Producer").GetProperty("CompletionSha256").GetString() + ";")) + 1;
        Assert.True(progress > 0 && completion > progress);
        foreach (var witness in witnesses)
        {
            var line = witness.GetProperty("RawLine").GetString()!;
            Assert.DoesNotContain("before_publication_episode_fixture", line);
            Assert.DoesNotContain("after-boundary-episode", line);
            if (line.Contains("event=filetransfer_coordinator_transition_snapshot;"))
            { Assert.InRange(witness.GetProperty("InputLine").GetInt32(), progress, completion); }
        }
        var copiedInput = normal.Lines.Take(2).Concat(normal.Lines.Skip(1)).ToArray();
        var copyCapture = FreezeBoundary(normal.Observer, copiedInput, out reason);
        Assert.True(copyCapture is not null, reason);
        Assert.Equal(GetPrivateProperty<int>(normalCapture!, "WitnessCount"), GetPrivateProperty<int>(copyCapture!, "WitnessCount"));
    }

    [Theory]
    [InlineData("v6")]
    [InlineData("v4")]
    [Trait("FileTransferRuntimeSuite", "FrontierBoundaryValidation")]
    public async Task FrontierBoundaryValidation_RequiredEpisodeOverBudgetIsOmitted(string path)
    {
        var fixture = await RunFrontierBoundaryAsync(path, "positive", true, episodeStateSends: 16);
        Assert.Equal("PublishedBeforeOriginalDeadline", GetPrivateProperty<string>(fixture.Observer, "Status"));
        Assert.InRange(GetPrivateProperty<int>(fixture.Observer, "EntryCount"), 8, 64);
        Assert.Null(FreezeBoundary(fixture.Observer, fixture.Lines, out var reason));
        Assert.Equal("witness_budget", reason);
    }

    [Theory]
    [InlineData("v6")]
    [InlineData("v4")]
    [Trait("FileTransferRuntimeSuite", "FrontierBoundaryValidation")]
    public async Task FrontierBoundaryValidation_MissingOriginalRootLogCannotUseSuccessor(string path)
    {
        var fixture = await RunFrontierBoundaryAsync(path, "before_recheck", true);
        Assert.Equal("PublishedBeforeOriginalDeadline", GetPrivateProperty<string>(fixture.Observer, "Status"));
        Assert.NotNull(FreezeBoundary(fixture.Observer, fixture.Lines, out _));
        var input = fixture.Lines.ToArray();
        var generation = GetPrivateProperty<long>(BoundaryField(fixture.Observer, "root")!, "WakeGeneration");
        var rootLine = Array.FindIndex(input, l => l.Contains("event=filetransfer_v6_frontier_request_deferred;") &&
            GetDelimitedLogField(l, "wake_generation") == generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(rootLine >= 0);
        input[rootLine] = input[rootLine].Replace("event=filetransfer_v6_frontier_request_deferred;", "event=omitted_original_root;");
        Assert.Null(FreezeBoundary(fixture.Observer, input, out var reason));
        Assert.Equal("writer_stall_root_missing_or_ambiguous", reason);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FrontierBoundaryValidation")]
    public async Task FrontierBoundaryValidation_CandidateCannotReplacePrivateProducerOrInput()
    {
        var fixture = await RunFrontierBoundaryAsync("v6", "successors", true);
        var input = fixture.Lines.ToArray();
        var capture = FreezeBoundary(fixture.Observer, input, out var reason);
        Assert.True(capture is not null, reason);
        var full = BoundaryExport(capture!, "Full");
        var compact = BoundaryExport(capture!, "Compact");
        input[0] = "mutated caller buffer";
        foreach (var view in new[] { "full", "compact" })
        {
            var payload = view == "full" ? full : compact;
            foreach (var field in new[] { "Format", "ClockDomain", "CaptureId", "InputSha256", "InputHashDomain", "View", "Producer", "Records" })
            {
                var node = JsonNode.Parse(payload)!;
                node[field] = "forged";
                Reject(node.ToJsonString(), view);
            }
            foreach (var field in new[] { "ObservationId", "ClockInstanceId", "ProcessId", "ProcessCreationFileTime", "Frequency",
                "SourceSha256", "Repair", "Progress", "RootGeneration", "RecheckedGeneration", "FirstWriteGeneration",
                "OriginalDeadlineUtcTicks", "PublicationDeadlineUtcTicks", "Outcome", "Lifecycle", "CompletionSha256", "CompletionPayload" })
            {
                var node = JsonNode.Parse(payload)!;
                node["Producer"]![field] = "forged";
                node["InputSha256"] = new string('a', 64);
                Reject(node.ToJsonString(), view);
            }
            Reject(payload + " ", view);
            Reject(payload[..^1] + ",\"LiveProofEligible\":true}", view);
            Reject(null, view);
            AssertBoundaryValidated(capture!, payload, view);
        }
        Reject(full, "compact"); Reject(compact, "full"); Reject(full, "live");
        var another = FreezeBoundary(fixture.Observer, fixture.Lines, out reason);
        Assert.NotNull(another);
        Reject(BoundaryExport(another!, "Full"), "full");

        void Reject(string? payload, string view)
        {
            var result = BoundaryValidate(capture!, payload, view);
            Assert.Equal("EvidenceIncomplete", GetPrivateProperty<string>(result, "Status"));
            Assert.Null(GetPrivatePropertyValue(result, "DiagnosticOutcome"));
            Assert.False(GetPrivateProperty<bool>(result, "LiveProofEligible"));
            Assert.False(GetPrivateProperty<bool>(result, "NativeWriterCustodyEstablished"));
        }
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FrontierBoundaryValidation")]
    public async Task FrontierBoundaryValidation_InputContradictionsOmitWithoutChangingProducer()
    {
        var fixture = await RunFrontierBoundaryAsync("v4", "successors", true);
        var originalStatus = GetPrivateProperty<string>(fixture.Observer, "Status");
        var originalTiming = GetPrivatePropertyValue(fixture.Observer, "Timing");
        var input = fixture.Lines.ToArray();
        var write = Array.FindIndex(input, l => l.Contains("event=filetransfer_v4_sparse_write_committed;") && l.Contains("next_chunk_index=102;"));
        var successor = Array.FindIndex(input, l => l.Contains("event=filetransfer_v6_frontier_stall_grace_recheck_decided;") && l.Contains("decision_status=rearmed;"));
        var state = Array.FindIndex(input, l => l.Contains("event=filetransfer_v6_receiver_state_sent;") && l.Contains("contiguous_committed_chunk_index=102;"));
        Assert.True(write >= 0 && successor >= 0 && state >= 0);
        foreach (var change in new Func<string[], string[]>[]
        {
            a => a.Skip(1).ToArray(),
            a => a[..^1],
            a => { a[write] = a[write].Replace("event=filetransfer_v4_sparse_write_committed;", "event=omitted_write;"); return a; },
            a => { a[write] = a[write].Replace("contiguous_chunks_committed=1;", "contiguous_chunks_committed=2;"); return a; },
            a => { a[successor] = a[successor].Replace("decision_reason=highest_received_advanced;", "decision_reason=foreign;"); return a; },
            a => { a[state] = a[state].Replace("transfer_paused=0;", "transfer_paused=1;"); return a; },
            a => { a[state] += "; epoch=999"; return a; },
            a => { a[write] += "; log_writer_id=forged"; return a; },
            a => { (a[write], a[write + 1]) = (a[write + 1], a[write]); return a; },
            a => { a[0] += "\nsecond line"; return a; },
            a => [],
            a => { a[0] = new string('x', 262145); return a; },
            a => Enumerable.Repeat(new string('x', 262144), 33).ToArray(),
            a => Enumerable.Repeat(a[0], 8193).ToArray(),
        })
        {
            Assert.Null(FreezeBoundary(fixture.Observer, change(input.ToArray()), out _));
            Assert.Equal(originalStatus, GetPrivateProperty<string>(fixture.Observer, "Status"));
            Assert.Same(originalTiming, GetPrivatePropertyValue(fixture.Observer, "Timing"));
        }
        Assert.NotNull(FreezeBoundary(fixture.Observer, input, out _));
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("rejected")]
    [InlineData("write_before_recheck")]
    [InlineData("capacity")]
    [Trait("FileTransferRuntimeSuite", "FrontierBoundaryValidation")]
    public async Task FrontierBoundaryValidation_InvalidCaptureCannotBorrowValidWriter(string variant)
    {
        var fixture = await RunFrontierBoundaryAsync("v6", variant, true);
        Assert.Null(FreezeBoundary(fixture.Observer, fixture.Lines, out var reason));
        Assert.Equal("producer_not_closed_positive", reason);
    }

    private static object? FreezeBoundary(object observer, IReadOnlyList<string> lines, out string reason)
    {
        object?[] args = [lines, null];
        var result = observer.GetType().GetMethod("TryFreeze")!.Invoke(observer, args);
        reason = (string)args[1]!;
        return result;
    }
    private static string BoundaryExport(object capture, string view)
        => (string)capture.GetType().GetMethod("Export" + view)!.Invoke(capture, null)!;
    private static object BoundaryValidate(object capture, string? payload, string view)
        => capture.GetType().GetMethod("Validate")!.Invoke(capture, [payload, view])!;
    private static void AssertBoundaryValidated(object capture, string payload, string view)
    {
        var result = BoundaryValidate(capture, payload, view);
        Assert.Equal("ValidatedPrivateBoundaryDiagnostic", GetPrivateProperty<string>(result, "Status"));
        Assert.False(GetPrivateProperty<bool>(result, "LiveProofEligible"));
        Assert.False(GetPrivateProperty<bool>(result, "NativeWriterCustodyEstablished"));
        Assert.False(GetPrivateProperty<bool>(result, "PublicationCompletionIssued"));
    }
}
