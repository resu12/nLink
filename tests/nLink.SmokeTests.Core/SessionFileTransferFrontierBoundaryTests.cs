using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData("v6")]
    [InlineData("v4")]
    [Trait("FileTransferRuntimeSuite", "FrontierPublicationBoundary")]
    public async Task FrontierBoundary_RealSparseWriteCapturesExactRuntimeClock(string path)
        => await RunFrontierBoundaryAsync(path, "positive", true);

    [Theory]
    [InlineData("v6", "successors")]
    [InlineData("v4", "successors")]
    [InlineData("v6", "before_recheck")]
    [InlineData("v4", "before_recheck")]
    [InlineData("v6", "write_before_recheck")]
    [InlineData("v6", "duplicate_conflict")]
    [InlineData("v6", "gapped")]
    [InlineData("v6", "changed_deadline")]
    [InlineData("v6", "capacity")]
    [Trait("FileTransferRuntimeSuite", "FrontierPublicationBoundary")]
    public async Task FrontierBoundary_ActualSuccessorsPreserveOriginalRootAndRecheckTip(string path, string variant)
        => await RunFrontierBoundaryAsync(path, variant, true);

    [Theory]
    [InlineData("pause")]
    [InlineData("source")]
    [InlineData("data")]
    [InlineData("flush")]
    [InlineData("rejected")]
    [InlineData("missing_current")]
    [Trait("FileTransferRuntimeSuite", "FrontierPublicationBoundary")]
    public async Task FrontierBoundary_ClosedObservationCannotBecomePositive(string variant)
        => await RunFrontierBoundaryAsync("v6", variant, true);

    [Theory]
    [InlineData("v6")]
    [InlineData("v4")]
    [Trait("FileTransferRuntimeSuite", "FrontierPublicationBoundary")]
    public async Task FrontierBoundary_OptInDoesNotChangeRuntimeDecisionsOrSends(string path)
    {
        var enabled = await RunFrontierBoundaryAsync(path, "positive", true);
        var disabled = await RunFrontierBoundaryAsync(path, "positive", false);
        Assert.Equal(enabled.Calls, disabled.Calls);
        Assert.Equal(Decisions(enabled.Lines), Decisions(disabled.Lines));
        static string[] Decisions(IReadOnlyList<string> lines) => lines
            .Where(line => line.Contains("event=filetransfer_coordinator_transition_snapshot;", StringComparison.Ordinal))
            .Select(line =>
            {
                var payload = GetDelimitedLogField(line, "snapshot_payload").Replace('-', '+').Replace('_', '/');
                using var json = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
                var root = json.RootElement;
                var aggregate = root.GetProperty("aggregate");
                return string.Join('|', root.GetProperty("decisionKind"), root.GetProperty("decisionStatus"),
                    root.GetProperty("decisionReason"), aggregate.GetProperty("beforeStateGeneration"),
                    aggregate.GetProperty("afterStateGeneration"), aggregate.GetProperty("changedFamilies"));
            }).ToArray();
    }

    private static async Task<(IReadOnlyList<string> Lines, string[] Calls, object Observer)> RunFrontierBoundaryAsync(
        string path, string variant, bool enabled, int priorStateSends = 0, int episodeStateSends = 0, int laterStateSends = 0,
        Action<object>? afterCaptureClosed = null, object? suppliedObserver = null, Action<object>? beforeCapture = null,
        bool originalPrimaryFailure = false, int additionalWitnessWrites = 0, bool appOwnedObservation = false,
        bool resetAfterCapture = false, bool waitForLogEvidence = true)
    {
        var type = typeof(SessionFileTransferService);
        var observer = appOwnedObservation ? null! : suppliedObserver ?? Activator.CreateInstance(type.GetNestedType("FrontierBoundaryObservation", BindingFlags.NonPublic)!)!;
        string[] calls = [];
        var lines = await CreateCheckpointRepairContinuityServiceEvidenceFixtureAsync(true, null, async (service, context, baseTransport) =>
        {
            if (appOwnedObservation)
            {
                observer = GetPrivatePropertyValue(context, "FrontierBoundaryObservation")!;
                Assert.NotNull(observer);
                Assert.True(GetPrivateProperty<bool>(context, "FrontierAdmissionAttempted"));
                Assert.Same(GetServiceGate(service), BoundaryField(observer, "serviceGate"));
            }
            var transport = Assert.IsType<FrontierPublicationDeliveryTransport>(baseTransport);
            using var destination = new MemoryStream(new byte[256 * 21 * 1024], writable: true);
            SetPrivateProperty(context, "WriteStream", destination);
            // Real receive path creates the original deferred-state candidate;
            // no synthetic failure record or projected producer clock.
            if (originalPrimaryFailure) { await Write(180); }
            SetPrivateProperty(context, "PullPostTunaRecoveryActive", false);
            SetPrivateProperty(context, "V6LastReceiverStateSentUtc", DateTimeOffset.UtcNow.AddMinutes(1));
            for (var i = 0; i < episodeStateSends; i++) { await ExtraState("inside-boundary-episode"); }
            if (episodeStateSends > 0) { SetPrivateProperty(context, "V6LastReceiverStateSentUtc", DateTimeOffset.UtcNow.AddMinutes(1)); }
            object? raceTip = null;
            if (variant is "before_recheck" or "write_before_recheck")
            {
                SetPrivateProperty(observer, "AfterStallForTesting", (Action)(() =>
                {
                    Write(variant == "before_recheck" ? 200 : 101).GetAwaiter().GetResult();
                    raceTip = GetPrivatePropertyValue(context, "CurrentV6FrontierStallGraceWake");
                }));
            }
            await CoreSmokeTestsBase.WaitUntilAsync(() =>
            {
                lock (GetServiceGate(service))
                {
                    return enabled ? GetPrivateProperty<string>(observer, "Status") is "RecentRechecked" or "EvidenceIncomplete" or "ObservationInvalidated" :
                        GetPrivatePropertyValue(context, "CurrentV6FrontierStallGraceWake") is not null;
                }
            }, TimeSpan.FromSeconds(5));
            if (variant == "write_before_recheck")
            {
                Assert.Equal("EvidenceIncomplete", GetPrivateProperty<string>(observer, "Status"));
                Assert.Null(GetPrivatePropertyValue(observer, "Timing"));
                Assert.Equal(102, GetPrivateProperty<int>(context, "NextChunkIndex"));
                return;
            }
            if (enabled) { Assert.Equal("RecentRechecked", GetPrivateProperty<string>(observer, "Status")); }
            var root = enabled ? BoundaryField(observer, "root")! : GetPrivatePropertyValue(context, "CurrentV6FrontierStallGraceWake")!;
            Assert.NotNull(root);
            if (raceTip is not null) { Assert.Same(raceTip, BoundaryField(observer, "recheckedTip")); Assert.NotSame(root, raceTip); }
            if (variant is "successors" or "duplicate_conflict" or "gapped" or "changed_deadline" or "capacity")
            {
                await Write(variant == "capacity" ? 160 : 200);
                var successor = GetPrivatePropertyValue(context, "CurrentV6FrontierStallGraceWake")!;
                Assert.NotSame(root, successor);
                Assert.Same(root, BoundaryField(observer, "root"));
                Assert.Equal(GetPrivateProperty<DateTimeOffset>(root, "DueUtc"), GetPrivateProperty<DateTimeOffset>(successor, "DueUtc"));
                var count = GetPrivateProperty<int>(observer, "EntryCount");
                Observe("Successor", context, root, successor); // exact duplicate
                Observe("Successor", context, successor, root); // stale notification
                var foreign = successor.GetType().GetMethod("<Clone>$")!.Invoke(successor, null)!;
                SetPrivateProperty(foreign, "SessionId", "foreign-source");
                Observe("Successor", context, root, foreign); // never assigned current
                Assert.Equal(count, GetPrivateProperty<int>(observer, "EntryCount"));
                if (variant == "duplicate_conflict") { Observe("Successor", context, successor, successor); }
                else if (variant is "gapped" or "changed_deadline")
                {
                    var clone = successor.GetType().GetMethod("<Clone>$")!.Invoke(successor, null)!;
                    SetPrivateProperty(clone, "WakeGeneration", GetPrivateProperty<long>(successor, "WakeGeneration") + (variant == "gapped" ? 2 : 1));
                    SetPrivateProperty(clone, "HighestReceivedChunkIndex", 201);
                    if (variant == "changed_deadline") { SetPrivateProperty(clone, "DueUtc", GetPrivateProperty<DateTimeOffset>(successor, "DueUtc").AddTicks(1)); }
                    SetPrivateProperty(context, "CurrentV6FrontierStallGraceWake", clone);
                    Observe("Successor", context, successor, clone);
                }
                else if (variant == "capacity")
                {
                    // Exercise the real assignment with a bounded series of highest advances.
                    lock (GetServiceGate(service))
                    {
                        for (var i = 161; i < 230; i++)
                        {
                            SetPrivateProperty(context, "PullHighestReceivedChunkIndex", i);
                            type.GetMethod("RefreshInboundV6FrontierStallGraceWakeForHighestProgressLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
                                .Invoke(service, [context]);
                        }
                    }
                    Assert.Equal(64, GetPrivateProperty<int>(observer, "EntryCount"));
                }
                else { await Write(201); }
                if (variant != "successors")
                {
                    Assert.Equal("EvidenceIncomplete", GetPrivateProperty<string>(observer, "Status"));
                    Assert.Null(GetPrivatePropertyValue(observer, "Timing"));
                    return;
                }
            }
            var oldCommittedUtc = GetPrivatePropertyValue(context, "PullLastCommittedProgressUtc");
            if (variant == "missing_current") { SetPrivateProperty(context, "CurrentV6FrontierStallGraceWake", null); }
            await Write(101);
            if (path == "v4") { Assert.Equal(oldCommittedUtc, GetPrivatePropertyValue(context, "PullLastCommittedProgressUtc")); }
            Assert.Equal(102, GetPrivateProperty<int>(context, "NextChunkIndex"));
            if (enabled && variant != "missing_current")
            {
                var first = BoundaryField(observer, "firstWrite")!;
                Assert.NotNull(first);
                var clock = GetPrivateProperty<FileTransferCoordinator.FrontierPublicationBoundaryStamp>(first, "Clock");
                Assert.Equal(GetPrivateProperty<DateTimeOffset>(context, "PullLastProgressUtc").UtcTicks, clock.UtcTicks);
                Assert.Equal(path + "_sparse", GetPrivateProperty<string>(first, "Path"));
                Assert.Same(root, BoundaryField(observer, "root"));
                Assert.Same(BoundaryField(observer, "tip"), BoundaryField(observer, "writeTip"));
            }
            if (variant is "pause" or "data" or "source")
            {
                if (variant == "pause") { SetPrivateProperty(context, "UserPaused", true); SetPrivateProperty(context, "UserPaused", false); }
                else if (variant == "data")
                {
                    var generation = GetPrivateProperty<long>(context, "DataSessionGeneration");
                    SetPrivateProperty(context, "DataSessionGeneration", generation + 1); SetPrivateProperty(context, "DataSessionGeneration", generation);
                }
                else { var leg = GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg"); leg.CheckpointGeneration++; leg.CheckpointGeneration--; }
            }
            if (variant == "flush")
            { await Assert.IsAssignableFrom<Task>(type.GetMethod("FlushInboundV4PausedProgressAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [context, "boundary-flush"])); }
            if (additionalWitnessWrites > 0)
            {
                var previous = GetPrivateProperty<int>(context, "V6LastReceiverStateCommittedChunkIndex");
                try
                {
                    for (var chunk = 102; chunk < 102 + additionalWitnessWrites; chunk++)
                    {
                        SetPrivateProperty(context, "V6LastReceiverStateCommittedChunkIndex", chunk);
                        await Write(chunk);
                    }
                }
                finally { SetPrivateProperty(context, "V6LastReceiverStateCommittedChunkIndex", previous); }
            }
            transport.Defer = variant == "rejected";
            transport.QueueOnly = variant == "rejected";
            if (variant == "rejected") { transport.Release.TrySetResult(); }
            if (originalPrimaryFailure)
            {
                var due = GetPrivateProperty<DateTimeOffset>(root, "DueUtc").AddMilliseconds(20);
                var delay = due - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero) { await Task.Delay(delay); }
            }
            SetPrivateProperty(context, "V6LastReceiverStateSentUtc", DateTimeOffset.UtcNow.AddSeconds(-2));
            var sent = await Assert.IsAssignableFrom<Task<bool>>(type.GetMethod("SendInboundV6ReceiverStateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [context, "boundary-publication", true, false, null]));
            Assert.Equal(variant != "rejected", sent);
            if (variant == "rejected")
            {
                transport.Defer = false;
                transport.QueueOnly = false;
                SetPrivateProperty(context, "V6LastReceiverStateSentUtc", DateTimeOffset.UtcNow.AddSeconds(-2));
                Assert.True(await Assert.IsAssignableFrom<Task<bool>>(type.GetMethod("SendInboundV6ReceiverStateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(service, [context, "later-boundary-publication", true, false, null])));
            }
            if (enabled)
            {
                var expected = variant switch { "pause" or "data" or "source" => "ObservationInvalidated",
                    "flush" => "FlushInvalidated", "rejected" => "PublicationRejected", "missing_current" => "EvidenceIncomplete",
                    _ => originalPrimaryFailure ? "PublishedWithinObservationBound" : "PublishedBeforeOriginalDeadline" };
                Assert.Equal(expected, GetPrivateProperty<string>(observer, "Status"));
                Assert.False(GetPrivateProperty<bool>(observer, "LiveProofEligible"));
                if (expected.StartsWith("Published")) { Assert.NotNull(GetPrivatePropertyValue(observer, "Timing")); }
                else { Assert.Null(GetPrivatePropertyValue(observer, "Timing")); }
                Assert.InRange(GetPrivateProperty<int>(observer, "EntryCount"), 5, 64);
                Assert.Null(BoundaryField(observer, "watchedLeg"));
                Assert.Null(BoundaryField(observer, "watchedEpoch"));
            }
            afterCaptureClosed?.Invoke(observer);
            if (resetAfterCapture)
            {
                var status = GetPrivateProperty<string>(observer, "Status");
                service.ResetSessionState();
                Assert.Equal(status, GetPrivateProperty<string>(observer, "Status"));
            }
            for (var i = 0; i < laterStateSends; i++) { await ExtraState("after-boundary-episode"); }
            calls = transport.Calls.ToArray();

            async Task ExtraState(string reason)
            {
                SetPrivateProperty(context, "V6LastReceiverStateSentUtc", DateTimeOffset.UtcNow.AddSeconds(-2));
                Assert.True(await Assert.IsAssignableFrom<Task<bool>>(type.GetMethod("SendInboundV6ReceiverStateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(service, [context, reason, true, false, null])));
            }

            void Observe(string method, params object[] arguments)
            {
                lock (GetServiceGate(service)) { observer.GetType().GetMethod(method)!.Invoke(observer, arguments); }
            }

            async Task Write(int index)
            {
                var chunk = Enumerable.Repeat((byte)0xb7, 21 * 1024).ToArray();
                var batch = new FileTransferChunkBatchFrameV6
                {
                    SessionId = GetPrivateProperty<string>(context, "SessionId"), TransferId = GetPrivateProperty<string>(context, "TransferId"),
                    TransportEpoch = GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg").TransportEpochId,
                    StartChunkIndex = index, ChunkCount = 1, BatchId = "boundary-write", DataSegments = [chunk],
                };
                if (path == "v6")
                { await Assert.IsAssignableFrom<Task>(type.GetMethod("HandleInboundV6SparseChunkBatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(service, [context, batch, new List<(int, byte[])> { (index, chunk) }, destination, FileTransferTransportKind.RegularNkn, null])); }
                else
                { await Assert.IsAssignableFrom<Task>(type.GetMethod("HandleInboundV4ChunkBatchCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(service, [context, batch, FileTransferTransportKind.RegularNkn, null])); }
                Assert.Equal(chunk, destination.ToArray().Skip(index * chunk.Length).Take(chunk.Length));
            }
        }, priorStateSends: priorStateSends, beforeRepairWrite: context =>
        {
            if (enabled && !appOwnedObservation) { SetPrivateProperty(context, "FrontierBoundaryObservation", observer); }
            beforeCapture?.Invoke(context);
        }, initialHighest: 150, appOwnedService: appOwnedObservation, waitForLogEvidence: waitForLogEvidence);
        return (lines, calls, observer);
    }

    private static object? BoundaryField(object target, string name)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
}
