using System.Security.Cryptography;
using System.Diagnostics;
using NLink.Core.FileTransfer;
using NLink.Infra.Nkn;
using NLink.Tests;
using Stamp = NLink.Core.FileTransfer.FileTransferCoordinator.FrontierPublicationBoundaryStamp;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
[Trait("RequiredConfiguration", "Debug")]
public sealed class AttemptCanonicalTailCarrierTests : FileTransferOpsScriptsTestBase
{
    private static int First(bool canonical) => canonical ? 12476 : 0;
    private static FileTransferReceiverStateFrameV6 Frame() => new()
    {
        ContiguousCommittedChunkIndex = 12480, DurableReceivedHighestChunkIndex = 12483,
        BytesCommitted = 268369920, TerminalReady = false, TransferPaused = false
    };
    private static AttemptOfflineFrameSchedule Schedule(CancellationToken ct = default) =>
        new("sender", "session", "transfer", ct, latePublication: true, canonicalTail: true);
    private static void Receive(AttemptOfflineFrameSchedule schedule, int relative)
    {
        var bytes = new byte[] { (byte)relative };
        schedule.Receive(new("sender", new("session", "transfer", false, 12476 + relative, 1, 0,
            bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)))), bytes, _ => Assert.Fail("Selected frame should not deliver"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OnlyTheExactSparseFrontierStartsTheEpisode(bool canonical)
    {
        var b = First(canonical);
        Assert.True(AttemptOfflineLatePublicationProgress.MatchesSparseShape(canonical, b, b + 7));
        Assert.False(AttemptOfflineLatePublicationProgress.MatchesSparseShape(canonical, b, b + 6));
        Assert.False(AttemptOfflineLatePublicationProgress.MatchesSparseShape(canonical, b, b + 8));
        Assert.Throws<IOException>(() => AttemptOfflineLatePublicationProgress.MatchesSparseShape(canonical, b - 1, b + 7));
        Assert.Throws<IOException>(() => AttemptOfflineLatePublicationProgress.MatchesSparseShape(canonical, b + 1, b + 7));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(12002)] [InlineData(12475)]
    public void CanonicalContiguousPrefixRemainsNotReady(int next)
    {
        Assert.False(AttemptOfflineLatePublicationProgress.MatchesSparseShape(true, next, next - 1));
        Assert.True(AttemptOfflineLatePublicationProgress.MatchesSparseShape(true, 12476, 12483));
    }

    [Theory]
    [InlineData(true, -1, -2)]
    [InlineData(true, 12002, 12000)] [InlineData(true, 12002, 12002)]
    [InlineData(true, 12475, 12483)] [InlineData(true, 12477, 12476)]
    [InlineData(false, -1, -2)] [InlineData(false, 1, 0)] [InlineData(false, 12002, 12001)]
    public void PrefixReadinessStillRejectsGapsInvalidAdvancedAndSmallStates(bool canonical, int next, int highest)
    {
        Assert.Throws<IOException>(() => AttemptOfflineLatePublicationProgress.MatchesSparseShape(canonical, next, highest));
    }

    [Theory]
    [InlineData(false, "valid")] [InlineData(true, "valid")]
    [InlineData(false, "start")] [InlineData(true, "start")]
    [InlineData(false, "count")] [InlineData(true, "count")]
    [InlineData(false, "before")] [InlineData(true, "before")]
    [InlineData(false, "after")] [InlineData(true, "after")]
    public void RepairMustBeTheOriginalSingleChunkAtTheSelectedFrontier(bool canonical, string fault)
    {
        var b = First(canonical); var range = new FileTransferRangeV4 { StartChunkIndex = b, ChunkCount = 1 };
        var before = b; var after = b + 1;
        switch (fault)
        {
            case "start": range = range with { StartChunkIndex = b + 1 }; break;
            case "count": range = range with { ChunkCount = 2 }; break;
            case "before": before--; break;
            case "after": after++; break;
        }
        if (fault == "valid") AttemptOfflineLatePublicationProgress.RequireRepairShape(canonical, range, before, after);
        else Assert.Throws<IOException>(() => AttemptOfflineLatePublicationProgress.RequireRepairShape(canonical, range, before, after));
    }

    [Theory]
    [InlineData(false, "valid")] [InlineData(true, "valid")]
    [InlineData(false, "before")] [InlineData(true, "before")]
    [InlineData(false, "after")] [InlineData(true, "after")]
    [InlineData(false, "count")] [InlineData(true, "count")]
    [InlineData(false, "highest")] [InlineData(true, "highest")]
    [InlineData(false, "next")] [InlineData(true, "next")]
    public void FirstWriteMustCommitExactlyThreeChunksOnTheSameShape(bool canonical, string fault)
    {
        var b = First(canonical); var before = b + 1; var after = b + 4;
        var count = 3; var highest = b + 7; var next = b + 4;
        switch (fault)
        {
            case "before": before++; break;
            case "after": after++; break;
            case "count": count++; break;
            case "highest": highest--; break;
            case "next": next++; break;
        }
        if (fault == "valid") AttemptOfflineLatePublicationProgress.RequireWriteShape(canonical, before, after, count, highest, next, Frame());
        else Assert.Throws<IOException>(() => AttemptOfflineLatePublicationProgress.RequireWriteShape(canonical, before, after, count, highest, next, Frame()));
    }

    [Theory]
    [InlineData("committed")] [InlineData("highest")] [InlineData("bytes")]
    [InlineData("terminal")] [InlineData("paused")]
    public void CanonicalWriteFrameCannotBecomeTerminalPausedOrReportOtherBytes(string fault)
    {
        var frame = Frame();
        frame = fault switch
        {
            "committed" => frame with { ContiguousCommittedChunkIndex = 12481 },
            "highest" => frame with { DurableReceivedHighestChunkIndex = 12482 },
            "bytes" => frame with { BytesCommitted = 86016 },
            "terminal" => frame with { TerminalReady = true },
            _ => frame with { TransferPaused = true }
        };
        Assert.Throws<IOException>(() => AttemptOfflineLatePublicationProgress.RequireWriteShape(true, 12477, 12480, 3, 12483, 12480, frame));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DeferredEventMustMatchTheSelectedGeometryAndExistingReason(bool canonical)
    {
        var b = First(canonical);
        var line = $"event=filetransfer_v6_receiver_state_deferred; reason=frontier_stalled; next_chunk_index={b + 1}; highest_received_chunk_index={b + 7};";
        Assert.True(AttemptOfflineLatePublicationProgress.MatchesDeferredEvent(canonical, line));
        Assert.False(AttemptOfflineLatePublicationProgress.MatchesDeferredEvent(!canonical, line));
        Assert.False(AttemptOfflineLatePublicationProgress.MatchesDeferredEvent(canonical, line.Replace("frontier_stalled", "other")));
        Assert.False(AttemptOfflineLatePublicationProgress.MatchesDeferredEvent(canonical, line.Replace("receiver_state_deferred", "receiver_state_sent")));
    }

    [Fact]
    public void UnboundObservationWithPrefixProgressIsRetainedUnchanged()
    {
        AttemptOfflineLatePublicationProgress.RequireUnboundObservation(null);
        var prefixProgress = new object();
        var observation = new ObservationShape(false, null, null, prefixProgress);
        AttemptOfflineLatePublicationProgress.RequireUnboundObservation(observation);
        Assert.Same(prefixProgress, observation.Progress);
        Assert.Null(observation.Source); Assert.Null(observation.Repair); Assert.False(observation.IsClosed);
    }

    [Theory]
    [InlineData(true, false, false)] [InlineData(false, true, false)] [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public void EarlierClosedOrRepairBoundObservationIsRejectedWithoutReset(bool closed, bool bound, bool repaired)
    {
        var source = bound ? new object() : null; var repair = repaired ? new object() : null; var progress = new object();
        var observation = new ObservationShape(closed, source, repair, progress);
        Assert.Throws<IOException>(() => AttemptOfflineLatePublicationProgress.RequireUnboundObservation(observation));
        Assert.Equal(closed, observation.IsClosed); Assert.Same(source, observation.Source);
        Assert.Same(repair, observation.Repair); Assert.Same(progress, observation.Progress);
    }

    [Fact]
    public async Task PrefixWaitCanExceedEpisodeTimeoutWithoutStartingAnotherClock()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var carrier = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var schedule = Schedule(carrier.Token);
        var checks = 0;
        var pending = AttemptOfflineLatePublicationProgress.WaitForCanonicalSuffixAsync(schedule,
            () => Interlocked.Increment(ref checks), carrier.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(8200), carrier.Token);
        Assert.False(pending.IsCompleted);
        Assert.True(Volatile.Read(ref checks) > 0);
        Receive(schedule, 0);
        await pending.WaitAsync(TimeSpan.FromSeconds(2), carrier.Token);
        Assert.True(schedule.AnyOrdinarySeen); Assert.False(schedule.AllOrdinarySeen);
        Assert.Equal(0, schedule.PendingBytes);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task PrefixWaitUsesTheOriginalCancellationToken(bool alreadyCancelled)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var carrier = new CancellationTokenSource();
        using var schedule = Schedule(carrier.Token);
        var checks = 0;
        if (alreadyCancelled) carrier.Cancel();
        var pending = AttemptOfflineLatePublicationProgress.WaitForCanonicalSuffixAsync(schedule, () => checks++, carrier.Token);
        carrier.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(carrier.Token, error.CancellationToken);
        if (alreadyCancelled) Assert.Equal(0, checks);
        else Assert.True(checks >= 1);
        Assert.Equal(0, schedule.PendingBytes);
    }

    [Fact]
    public async Task PrefixWaitRejectsAnExpiredTokenEvenIfAFrameAlreadyArrived()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var carrier = new CancellationTokenSource();
        using var schedule = Schedule(carrier.Token);
        Receive(schedule, 1);
        var checks = 0;
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AttemptOfflineLatePublicationProgress.WaitForCanonicalSuffixAsync(schedule, () => { checks++; carrier.Cancel(); }, carrier.Token));
        Assert.Equal(carrier.Token, error.CancellationToken); Assert.Equal(1, checks);
        Assert.Equal(0, schedule.PendingBytes);
    }

    [Fact]
    public async Task PrefixWaitPropagatesCurrentnessFailureWithoutResetOrAnotherEpisode()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var schedule = Schedule();
        var failure = new IOException("original_currentness_failure"); var checks = 0;
        var error = await Assert.ThrowsAsync<IOException>(() => AttemptOfflineLatePublicationProgress.WaitForCanonicalSuffixAsync(schedule,
            () => { checks++; throw failure; }, CancellationToken.None));
        Assert.Same(failure, error); Assert.Equal(1, checks); Assert.False(schedule.AnyOrdinarySeen);
    }

    [Fact]
    public async Task PrefixWaitCannotUseTheSmallSchedule()
    {
        using var small = new AttemptOfflineFrameSchedule("sender", "session", "transfer", CancellationToken.None);
        var error = await Assert.ThrowsAsync<IOException>(() => AttemptOfflineLatePublicationProgress.WaitForCanonicalSuffixAsync(small,
            () => Assert.Fail("Wrong profile read"), CancellationToken.None));
        Assert.Equal("late_publication_schedule_profile_mismatch", error.Message);
    }

    [Fact]
    public async Task ExistingPublicationOwnerAdmitsExactTailFrameWithoutChangingItsWindowOrCompletingCopies()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Synthetic value checks only; no native receipt, transfer or authority
        // is produced. The carrier still uses the original captured stamps.
        var s = new Stamp(Environment.ProcessId, 1, Guid.NewGuid(), Guid.NewGuid(), 1,
            Stopwatch.Frequency, DateTimeOffset.UtcNow.UtcTicks, Stopwatch.GetTimestamp(), Stopwatch.GetTimestamp());
        var window = AttemptLatePublicationWindow.Create(s, s.UtcTicks + TimeSpan.TicksPerMillisecond * 750,
            new(s.CounterAfter, 3000, 1000, 65536, 2), s.CounterAfter);
        Assert.Equal(s.CounterAfter + Stopwatch.Frequency / 4, window.ChunkRelease);
        Assert.Equal(s.CounterAfter + Stopwatch.Frequency * 4 / 5, window.CopyRelease);
        var frame = Frame() with
        {
            SessionId = "session", TransferId = "transfer", TransportEpoch = 2, Epoch = 7,
            MissingRanges = [new() { StartChunkIndex = 12480, ChunkCount = 1 }]
        };
        var metadata = new OfflinePublicationMetadata(frame, "source", "peer", "bulk.peer",
            new("control", 100, new string('A', 64)), new("bulk", 100, new string('B', 64)),
            FileTransferDirection.Inbound, FileTransferRouteResolver.PostTunaFallbackV6Token,
            6, 2, 3, 4, 5, true, false, 1, TimeSpan.FromMilliseconds(750));
        await using var owner = new AttemptLatePublicationOwner(window, new("session", "transfer", "source", "peer", 2, 3, 4, 5), CancellationToken.None,
            canonicalTail: true);
        Assert.Same(owner, owner.Registration().Bind(metadata));
        AttemptOfflineLatePublicationProgress.RequireWriteShape(true, 12477, 12480, 3, 12483, 12480, frame);
        Assert.True(owner.CheckNativeWrite(s with
        {
            CaptureSequence = 2, UtcTicks = s.UtcTicks + TimeSpan.TicksPerMillisecond * 250,
            CounterBefore = window.ChunkRelease, CounterAfter = window.ChunkRelease + 1
        }, frame));
        // Pending counts only started copies; the actual completeness guard,
        // not a zero-pending snapshot, must reject the three missing writes.
        Assert.Equal("publication_physical_completion_missing",
            Assert.Throws<InvalidOperationException>(owner.VerifyComplete).Message);
    }

    [Theory]
    [InlineData(true, false, false)] [InlineData(false, true, false)] [InlineData(false, false, true)]
    [InlineData(true, true, false)] [InlineData(true, false, true)] [InlineData(false, true, true)] [InlineData(true, true, true)]
    public async Task NamedCanonicalCarrierRejectsLegacyFlagCombinationsBeforeOpening(bool hold, bool schedule, bool late)
    {
        var previous = Environment.GetEnvironmentVariable(AttemptSingleSegmentProfile.Variable);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NknAccelerationTransportTests.RunCrossProcessCarrierAsync(null!, null!, "", "helpee", "",
                nativeCase: AttemptCanonicalTailProfile.Name, frontierHold: hold, frontierSchedule: schedule, latePublication: late));
        Assert.Equal("canonical_tail_requires_separate_profile", error.Message);
        Assert.Equal(previous, Environment.GetEnvironmentVariable(AttemptSingleSegmentProfile.Variable));
    }

    [Theory]
    [InlineData("writer-source-read-cross-process-carrier-full-canonical-tail-late-publication", "")]
    [InlineData("writer-source-read-cross-process-carrier-full-canonical-tail-late-publication", "canonical-tail-late-publication")]
    [InlineData("writer-valid", "canonical-tail-late-publication")]
    public async Task PartialCompositeDispatchFailsBeforeAnyLaunch(string scenario, string primary)
    {
        if (!OperatingSystem.IsWindows()) return;
        var script = await File.ReadAllTextAsync(Path.Combine(FindRepoRoot(), "tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatch.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, ["unused-root", "unused-directory", scenario, "", "", primary], "pwsh");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Canonical tail requires the exact actual-pair composite profile", result.Stderr);
        Assert.DoesNotContain("attempt-dispatch-wrapper-ok", result.Stdout);
    }

    // Value-shape tests only: these are not native observations or proof custody.
    private sealed class ObservationShape(bool closed, object? sourceValue, object? repairValue, object progress)
    {
        private readonly object? source = sourceValue, capturedRepair = repairValue;
        public bool IsClosed => closed;
        internal object? Source => source;
        internal object? Repair => capturedRepair;
        internal object Progress => progress;
    }
}
