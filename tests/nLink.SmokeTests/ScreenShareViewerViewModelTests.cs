using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using NLink.App.Services.ScreenCapture;
using NLink.App.ViewModels;
using NLink.Core.Logging;
using NLink.Core.ScreenShare;

namespace NLink.SmokeTests;

[Collection(AvaloniaHeadlessUiCollection.Name)]
public sealed class ScreenShareViewerViewModelTests : IClassFixture<ScreenShareCoordinatorFixture>
{
    private readonly ScreenShareCoordinatorFixture fixture;

    public ScreenShareViewerViewModelTests(ScreenShareCoordinatorFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareViewer_DefaultRole_IsViewer()
    {
        using var vm = new ScreenShareViewerViewModel();
        Assert.Equal("viewer", vm.ViewerRoleForDiagnostics);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareViewer_InternalRoleOverride_IsUsedForDiagnostics()
    {
        using var vm = new ScreenShareViewerViewModel(
            decodeFrame: null,
            postToUiAsync: null,
            h264Decoder: null,
            logRole: "helper_remote");

        Assert.Equal("helper_remote", vm.ViewerRoleForDiagnostics);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_DecodeFailure_DoesNotFreezeFutureFrames()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var decodeCalls = 0;

            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ =>
                {
                    var next = Interlocked.Increment(ref decodeCalls);
                    if (next == 1)
                    {
                        throw new InvalidDataException("invalid frame");
                    }

                    return CreateTinyBitmap();
                },
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                });

            vm.OnEncodedFrame("jpeg", new byte[] { 1 });
            await WaitUntilAsync(
                () => string.Equals(vm.StatusText, "Invalid frame received", StringComparison.Ordinal) && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnEncodedFrame("jpeg", new byte[] { 2 });
            await WaitUntilAsync(
                () => vm.CurrentFrame is not null && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            Assert.NotNull(vm.CurrentFrame);
            Assert.Equal("Live", vm.StatusText);
            Assert.Equal(2, decodeCalls);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_FirstFrameActivation_PostsStatusThroughUiDispatcher()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var decodeGate = new SemaphoreSlim(0, 1);
            var statusPostCount = 0;

            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ =>
                {
                    Assert.True(decodeGate.Wait(TimeSpan.FromSeconds(2)));
                    return CreateTinyBitmap();
                },
                postToUiAsync: action =>
                {
                    Interlocked.Increment(ref statusPostCount);
                    action();
                    return Task.CompletedTask;
                },
                h264Decoder: null,
                logRole: "helper_remote");

            vm.OnOwnedEncodedFrame("jpeg", CreateTinyJpegBytes());

            await WaitUntilAsync(
                () => Volatile.Read(ref statusPostCount) >= 1,
                TimeSpan.FromSeconds(2));

            Assert.True(vm.IsActive);
            Assert.Equal("Live", vm.StatusText);

            decodeGate.Release();
            await WaitUntilAsync(() => vm.IsIdleForDiagnostics, TimeSpan.FromSeconds(2));
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_RapidFrames_CoalescesToLatestFrame()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var decodeGate = new SemaphoreSlim(0, 1);
            var firstDecodeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: bytes =>
                {
                    if (!firstDecodeStarted.Task.IsCompleted)
                    {
                        firstDecodeStarted.TrySetResult(true);
                        Assert.True(decodeGate.Wait(TimeSpan.FromSeconds(2)));
                    }

                    return CreateBitmap(bytes.Span[0], 1);
                },
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                });

            vm.OnEncodedFrame("jpeg", new byte[] { 1 });
            await firstDecodeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            for (byte i = 2; i <= 20; i++)
            {
                vm.OnEncodedFrame("jpeg", new byte[] { i });
            }

            decodeGate.Release();

            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap latest && latest.PixelSize.Width == 20 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            var metrics = vm.GetMetricsSnapshot();
            Assert.True(metrics.FramesDecoded >= 1);
            Assert.True(metrics.FramesCoalesced >= 1);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_ProtectedRecoveryBurstWhileUiApplyIsBlocked_DecodesBeyondSingleLatestFrame()
    {
        await fixture.Session.Dispatch(async () =>
        {
            ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
            var releaseUiApply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var applyStarted = 0;
            var decodeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDecode = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                postToUiAsync: async action =>
                {
                    if (Interlocked.Increment(ref applyStarted) == 1)
                    {
                        await releaseUiApply.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    }

                    action();
                },
                h264Decoder: new BlockingH264BitmapDecoder(decodeStarted, releaseDecode),
                logRole: "helper_remote");

            vm.OnEncodedFrame("h264", new byte[] { 1 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), isKeyFrame: true, streamEpoch: 1, streamConfig: new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper-burst-attribution",
                StreamEpoch = 1,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            }, frameId: 1, sessionId: "helper-burst-attribution", recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.RecoveryOwner);
            await decodeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            vm.OnEncodedFrame("h264", new byte[] { 2 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), isKeyFrame: false, streamEpoch: 1, frameId: 2, sessionId: "helper-burst-attribution", recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.ProtectedFollower);
            vm.OnEncodedFrame("h264", new byte[] { 3 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), isKeyFrame: false, streamEpoch: 1, frameId: 3, sessionId: "helper-burst-attribution", recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.ProtectedFollower);

            releaseDecode.TrySetResult(true);
            await WaitUntilAsync(() => Volatile.Read(ref applyStarted) >= 1, TimeSpan.FromSeconds(2));

            releaseUiApply.TrySetResult(true);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap current && current.PixelSize.Width >= 1 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            var metrics = vm.GetMetricsSnapshot();
            var current = Assert.IsAssignableFrom<Bitmap>(vm.CurrentFrame);
            Assert.True(metrics.FramesDecoded >= 1);
            Assert.True(metrics.MaxPendingEncodedDepth >= 1);
            Assert.InRange(metrics.DecodeWorkerDroppedBeforeDecodeCount, 0, 1);
            Assert.Equal(0, metrics.DecodeQueueOverflowCount);
            Assert.Equal(0, metrics.DecodeWorkerDropQueueOverflowCount);
            Assert.Equal(0, metrics.BlockedByReservedRecoveryFrameRejectCount);
            Assert.Equal(0, metrics.DecodedBlockedByReservedRecoveryFrameCount);
            Assert.Equal(0, metrics.RecoveryFollowerWindowBufferedCount);
            Assert.Equal(0, metrics.StartupCorridorReleaseCount);
            Assert.Equal(0, metrics.StartupCorridorAbortCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorSuccessCount);
            Assert.Equal(0, metrics.ProtectedRecoveryDeliveryCount);
            Assert.True(current.PixelSize.Width >= 1);
            var snapshot = vm.GetFrameLossSnapshotForDiagnostics();
            Assert.DoesNotContain(
                snapshot.RecentLosses,
                static loss => string.Equals(loss.Reason, "recovery_runway_overflow", StringComparison.Ordinal));
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_OnEncodedFrame_CopiesInputBeforeAsyncDecode()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var decodeGate = new SemaphoreSlim(0, 1);
            var decodedMarker = 0;

            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: bytes =>
                {
                    Assert.True(decodeGate.Wait(TimeSpan.FromSeconds(2)));
                    decodedMarker = bytes.Span[0];
                    return CreateBitmap(bytes.Span[0], 1);
                },
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                });

            var source = new byte[] { 7 };
            vm.OnEncodedFrame("jpeg", source);
            source[0] = 9;
            decodeGate.Release();

            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap current && current.PixelSize.Width == 7 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            Assert.Equal(7, decodedMarker);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_OnOwnedEncodedFrame_UsesOwnedBufferWithoutExtraCopy()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var decodeGate = new SemaphoreSlim(0, 1);
            var decodedMarker = 0;

            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: bytes =>
                {
                    Assert.True(decodeGate.Wait(TimeSpan.FromSeconds(2)));
                    decodedMarker = bytes.Span[0];
                    return CreateBitmap(bytes.Span[0], 1);
                },
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                });

            var source = new byte[] { 7 };
            vm.OnOwnedEncodedFrame("jpeg", source);
            source[0] = 9;
            decodeGate.Release();

            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap current && current.PixelSize.Width == 9 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            Assert.Equal(9, decodedMarker);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_ReassemblerToViewer_RepeatedFrames_StayBounded()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var memoryBeforeBytes = GC.GetTotalMemory(forceFullCollection: true);
            var reassembler = new ScreenShareVideoFrameReassembler();

            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                h264Decoder: new FakeH264BitmapDecoder());

            reassembler.FrameReady += (_, frame) => vm.OnOwnedEncodedFrame(
                frame.Encoding,
                frame.EncodedFrameBytes,
                frame.CapturedTsUtcMs,
                frame.IsKeyFrame,
                frame.StreamEpoch,
                frame.StreamConfig,
                frameId: frame.FrameId,
                sessionId: frame.SessionId,
                recoveryDeliveryClass: frame.RecoveryDeliveryClass);

            reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
            {
                SessionId = "viewer-bounded",
                StreamEpoch = 1,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            });

            for (var frameId = 0; frameId < 120; frameId++)
            {
                var frameBytes = new byte[] { (byte)((frameId % 250) + 1), (byte)frameId, (byte)(frameId + 1), (byte)(frameId + 2) };
                var fragments = ScreenShareVideoFragmenter.FragmentAccessUnit(
                    sessionId: "viewer-bounded",
                    streamEpoch: 1,
                    frameId: frameId,
                    capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    width: 640,
                    height: 360,
                    encoding: "h264",
                    isKeyFrame: frameId == 0,
                    accessUnitBytes: frameBytes);

                foreach (var fragment in fragments)
                {
                    Assert.True(ScreenShareVideoPayloadCodec.TryDeserializeFragment(ScreenShareVideoPayloadCodec.SerializeFragment(fragment), out var decodedFragment));
                    reassembler.OnFragment(decodedFragment);
                }
            }

            await WaitUntilAsync(
                () => vm.GetMetricsSnapshot().FramesDecoded >= 1 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.Clear();
            await WaitUntilAsync(() => vm.IsIdleForDiagnostics, TimeSpan.FromSeconds(2));

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var memoryAfterBytes = GC.GetTotalMemory(forceFullCollection: true);
            Assert.True(memoryAfterBytes - memoryBeforeBytes < 4 * 1024 * 1024);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_ClearAndDispose_AreIdempotent()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var vm = new ScreenShareViewerViewModel(decodeFrame: _ => CreateTinyBitmap());

            vm.OnEncodedFrame("jpeg", CreateTinyJpegBytes());
            await WaitUntilAsync(() => vm.CurrentFrame is not null, TimeSpan.FromSeconds(2));

            vm.Clear();
            vm.Clear();
            Assert.False(vm.IsActive);
            Assert.Null(vm.CurrentFrame);

            vm.Dispose();
            vm.Dispose();
            Assert.False(vm.IsActive);
            Assert.Null(vm.CurrentFrame);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_Dispose_PreventsFurtherFrameApply()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => CreateTinyBitmap(),
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                });

            vm.OnEncodedFrame("jpeg", CreateTinyJpegBytes());
            await WaitUntilAsync(() => vm.CurrentFrame is not null && vm.IsIdleForDiagnostics, TimeSpan.FromSeconds(2));

            vm.Dispose();

            var exception = Assert.Throws<ObjectDisposedException>(() => vm.OnEncodedFrame("jpeg", CreateTinyJpegBytes()));
            Assert.Contains(nameof(ScreenShareViewerViewModel), exception.ObjectName ?? string.Empty, StringComparison.Ordinal);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_Metrics_TrackRenderInterval_CaptureToRender_AndStaleFrames()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => CreateTinyBitmap(),
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                });

            vm.OnEncodedFrame("jpeg", CreateTinyJpegBytes(), capturedTsUtcMs: DateTimeOffset.UtcNow.AddMilliseconds(-1500).ToUnixTimeMilliseconds());
            await WaitUntilAsync(() => vm.GetMetricsSnapshot().FramesDecoded >= 1, TimeSpan.FromSeconds(2));

            await Task.Delay(40);

            vm.OnEncodedFrame("jpeg", CreateTinyJpegBytes(), capturedTsUtcMs: DateTimeOffset.UtcNow.AddMilliseconds(-100).ToUnixTimeMilliseconds());
            await WaitUntilAsync(() => vm.GetMetricsSnapshot().FramesDecoded >= 2, TimeSpan.FromSeconds(2));

            var metrics = vm.GetMetricsSnapshot();
            Assert.Equal(2, metrics.FramesDecoded);
            Assert.True(metrics.AverageRenderIntervalMs > 0);
            Assert.True(metrics.AverageCaptureToRenderMs > 0);
            Assert.Equal(1, metrics.StaleFrameRenders);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_StaleDecodedFrame_DoesNotReplaceVisibleFrame()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: bytes => CreateBitmap(bytes.Span[0], 1),
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                });

            vm.OnEncodedFrame("jpeg", new byte[] { 7 }, capturedTsUtcMs: DateTimeOffset.UtcNow.AddMilliseconds(-100).ToUnixTimeMilliseconds());
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap first && first.PixelSize.Width == 7 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnEncodedFrame("jpeg", new byte[] { 9 }, capturedTsUtcMs: DateTimeOffset.UtcNow.AddMilliseconds(-2500).ToUnixTimeMilliseconds());
            await WaitUntilAsync(() => vm.IsIdleForDiagnostics, TimeSpan.FromSeconds(2));

            var current = Assert.IsAssignableFrom<Bitmap>(vm.CurrentFrame);
            Assert.Equal(7, current.PixelSize.Width);
            Assert.Equal(2, vm.GetMetricsSnapshot().FramesDecoded);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_RequestsSingleKeyframeForRealGap_AndCapsFutureNonKeyTail()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var reassembler = new ScreenShareVideoFrameReassembler();
        var keyframeRequests = new List<ScreenShareVideoKeyframeRequestV1>();
        reassembler.KeyframeRequested += (_, e) => keyframeRequests.Add(e);

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = "viewer-supersede",
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        CompleteFrame(reassembler, "viewer-supersede", streamEpoch: 1, frameId: 0, isKeyFrame: true);
        reassembler.OnFragment(CreatePartialFragment("viewer-supersede", streamEpoch: 1, frameId: 2, fragmentIndex: 0, isKeyFrame: false));
        reassembler.OnFragment(CreatePartialFragment("viewer-supersede", streamEpoch: 1, frameId: 3, fragmentIndex: 0, isKeyFrame: false));
        reassembler.OnFragment(CreatePartialFragment("viewer-supersede", streamEpoch: 1, frameId: 4, fragmentIndex: 0, isKeyFrame: false));

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-supersede");
        Assert.InRange(keyframeRequests.Count, 0, 1);
        if (keyframeRequests.Count == 1)
        {
            Assert.Equal("frame_gap_reassembler", keyframeRequests[0].Reason);
        }
        Assert.Equal(0, snapshot.GapNonKeyPrunedCount);
        Assert.Equal(0, snapshot.FutureTailQuarantinedDuringGapCount);
        Assert.Equal(0, snapshot.FutureTailQuarantinedAfterGapCount);
        Assert.Equal(3, snapshot.PreCandidateGapTailRejectedCount);
        Assert.Equal("future_tail_pruned_while_gap_active", snapshot.DominantReassemblerRootCause);
        var epochDiagnostics = Assert.Single(snapshot.EpochDiagnostics);
        Assert.Equal("future_tail_pruned_while_gap_active", epochDiagnostics.DominantReassemblerRootCause);
        Assert.Contains(epochDiagnostics.TimelineEvents, static timelineEvent => string.Equals(timelineEvent.EventName, "gap_detected", StringComparison.Ordinal));
        Assert.Contains(epochDiagnostics.TopLossBursts, static burst => string.Equals(burst.RootCause, "future_tail_pruned_while_gap_active", StringComparison.Ordinal));
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 2 && string.Equals(loss.Reason, "pre_candidate_gap_tail_rejected", StringComparison.Ordinal));
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 3 && string.Equals(loss.Reason, "pre_candidate_gap_tail_rejected", StringComparison.Ordinal));
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 4 && string.Equals(loss.Reason, "pre_candidate_gap_tail_rejected", StringComparison.Ordinal));
        Assert.Equal(0, snapshot.UnattributedLossCount);
        Assert.DoesNotContain(snapshot.RecentLosses, static loss => loss.FrameId == 0);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_AllowsCurrentBurstBudgetWithoutDroppingFrames()
    {
        var reassembler = new ScreenShareVideoFrameReassembler();
        var keyframeRequests = new List<ScreenShareVideoKeyframeRequestV1>();
        reassembler.KeyframeRequested += (_, e) => keyframeRequests.Add(e);

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = "viewer-burst-headroom",
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        for (var frameId = 0; frameId < ScreenShareVideoFrameReassembler.MaxInFlightAssembliesPerSession; frameId++)
        {
            reassembler.OnFragment(CreatePartialFragment("viewer-burst-headroom", streamEpoch: 1, frameId: frameId, fragmentIndex: 0, isKeyFrame: false));
        }

        Assert.Empty(keyframeRequests);
        Assert.Equal(0, reassembler.GetMetricsSnapshot().FramesDropped);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_DropsLateMissingHeadWhileGapIsActive()
    {
        var reassembler = new ScreenShareVideoFrameReassembler();
        var keyframeRequests = new List<ScreenShareVideoKeyframeRequestV1>();
        var readyFrameIds = new List<long>();
        reassembler.KeyframeRequested += (_, e) => keyframeRequests.Add(e);
        reassembler.FrameReady += (_, e) => readyFrameIds.Add(e.FrameId);

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = "viewer-ordered-ready",
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        CompleteFrame(reassembler, "viewer-ordered-ready", streamEpoch: 1, frameId: 0, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-ordered-ready", streamEpoch: 1, frameId: 2, isKeyFrame: false);

        Assert.Equal(new long[] { 0 }, readyFrameIds);
        Assert.Single(keyframeRequests);

        CompleteFrame(reassembler, "viewer-ordered-ready", streamEpoch: 1, frameId: 1, isKeyFrame: false);

        Assert.Equal(new long[] { 0 }, readyFrameIds);
        Assert.Single(keyframeRequests);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-ordered-ready");
        Assert.Equal(2, snapshot.PreCandidateGapTailRejectedCount);
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 2 && string.Equals(loss.Reason, "pre_candidate_gap_tail_rejected", StringComparison.Ordinal));
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 1 && string.Equals(loss.Reason, "pre_candidate_gap_tail_rejected", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_DropsStartupFollowers_UntilMissingHeadArrives()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var reassembler = new ScreenShareVideoFrameReassembler();
        var keyframeRequests = new List<ScreenShareVideoKeyframeRequestV1>();
        var readyFrameIds = new List<long>();
        reassembler.KeyframeRequested += (_, e) => keyframeRequests.Add(e);
        reassembler.FrameReady += (_, e) => readyFrameIds.Add(e.FrameId);

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = "viewer-startup-reorder",
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        CompleteFrame(reassembler, "viewer-startup-reorder", streamEpoch: 1, frameId: 1, isKeyFrame: false);
        CompleteFrame(reassembler, "viewer-startup-reorder", streamEpoch: 1, frameId: 2, isKeyFrame: false);
        CompleteFrame(reassembler, "viewer-startup-reorder", streamEpoch: 1, frameId: 3, isKeyFrame: false);

        Assert.Empty(readyFrameIds);
        Assert.Single(keyframeRequests);

        CompleteFrame(reassembler, "viewer-startup-reorder", streamEpoch: 1, frameId: 0, isKeyFrame: true);

        Assert.Equal(new long[] { 0 }, readyFrameIds);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-startup-reorder");
        Assert.Equal(3, snapshot.PreCandidateGapTailRejectedCount);
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 1 && string.Equals(loss.Reason, "pre_candidate_gap_tail_rejected", StringComparison.Ordinal));
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 2 && string.Equals(loss.Reason, "pre_candidate_gap_tail_rejected", StringComparison.Ordinal));
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 3 && string.Equals(loss.Reason, "pre_candidate_gap_tail_rejected", StringComparison.Ordinal));
        Assert.Equal(0, snapshot.UnattributedLossCount);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_WaitsBeforeResyncingToBufferedRecoveryKeyframe()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var now = new DateTimeOffset(2026, 4, 14, 15, 0, 0, TimeSpan.Zero);
        var reassembler = new ScreenShareVideoFrameReassembler(() => now);
        var keyframeRequests = new List<ScreenShareVideoKeyframeRequestV1>();
        var readyFrameIds = new List<long>();
        reassembler.KeyframeRequested += (_, e) => keyframeRequests.Add(e);
        reassembler.FrameReady += (_, e) => readyFrameIds.Add(e.FrameId);

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = "viewer-keyframe-resync",
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        CompleteFrame(reassembler, "viewer-keyframe-resync", streamEpoch: 1, frameId: 0, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-keyframe-resync", streamEpoch: 1, frameId: 2, isKeyFrame: false);
        now = now.AddMilliseconds(300);
        CompleteFrame(reassembler, "viewer-keyframe-resync", streamEpoch: 1, frameId: 4, isKeyFrame: true);

        Assert.Equal(new long[] { 0, 4 }, readyFrameIds);
        Assert.Single(keyframeRequests);
        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-keyframe-resync");
        Assert.Equal(1, snapshot.RecoveryKeyframeResyncCount);
        Assert.False(snapshot.GapActive);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_AllowsOrdinaryFramesAfterRecoveryKeyframeWithoutRunwayTrim()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var reassembler = new ScreenShareVideoFrameReassembler();
        var readyFrameIds = new List<long>();
        reassembler.FrameReady += (_, e) => readyFrameIds.Add(e.FrameId);

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = "viewer-gap-quarantine",
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        CompleteFrame(reassembler, "viewer-gap-quarantine", streamEpoch: 1, frameId: 0, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-gap-quarantine", streamEpoch: 1, frameId: 2, isKeyFrame: false);
        CompleteFrame(reassembler, "viewer-gap-quarantine", streamEpoch: 1, frameId: 4, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-gap-quarantine", streamEpoch: 1, frameId: 5, isKeyFrame: false);
        CompleteFrame(reassembler, "viewer-gap-quarantine", streamEpoch: 1, frameId: 6, isKeyFrame: false);
        CompleteFrame(reassembler, "viewer-gap-quarantine", streamEpoch: 1, frameId: 7, isKeyFrame: false);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-gap-quarantine");
        var epochSnapshot = Assert.Single(snapshot.EpochSnapshots);
        Assert.Equal(0, snapshot.FutureTailQuarantinedDuringGapCount);
        Assert.Equal(0, snapshot.FutureTailQuarantinedAfterGapCount);
        Assert.Equal(1, snapshot.PreCandidateGapTailRejectedCount);
        Assert.Equal(0, epochSnapshot.FutureTailQuarantinedDuringGapCount);
        Assert.Equal(0, epochSnapshot.FutureTailQuarantinedAfterGapCount);
        Assert.Equal(1, epochSnapshot.PreCandidateGapTailRejectedCount);
        Assert.Equal(new long[] { 0, 4, 5, 6, 7 }, readyFrameIds);
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 2 && string.Equals(loss.Reason, "pre_candidate_gap_tail_rejected", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.RecentLosses, static loss => loss.FrameId == 5);
        Assert.DoesNotContain(snapshot.RecentLosses, static loss => loss.FrameId == 6);
        Assert.DoesNotContain(snapshot.RecentLosses, static loss => loss.FrameId == 7);
        var epochDiagnostics = Assert.Single(snapshot.EpochDiagnostics);
        Assert.Contains(epochDiagnostics.TimelineEvents, static timelineEvent => string.Equals(timelineEvent.EventName, "recovery_keyframe_buffered", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_SimplifiedRecovery_AllowsOrdinaryTailAfterRecoveryKeyframeEmits()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var now = new DateTimeOffset(2026, 4, 20, 8, 0, 0, TimeSpan.Zero);
        var reassembler = new ScreenShareVideoFrameReassembler(() => now);
        var readyFrameIds = new List<long>();
        reassembler.FrameReady += (_, e) => readyFrameIds.Add(e.FrameId);

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = "viewer-keyframe-only-tail-drop",
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        CompleteFrame(reassembler, "viewer-keyframe-only-tail-drop", streamEpoch: 1, frameId: 0, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-keyframe-only-tail-drop", streamEpoch: 1, frameId: 2, isKeyFrame: false);
        CompleteFrame(reassembler, "viewer-keyframe-only-tail-drop", streamEpoch: 1, frameId: 4, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-keyframe-only-tail-drop", streamEpoch: 1, frameId: 5, isKeyFrame: false);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-keyframe-only-tail-drop");
        Assert.Equal(0, snapshot.RecoveryRunwayOverflowRejectCount);
        Assert.Equal(new long[] { 0, 4, 5 }, readyFrameIds);
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 2 && string.Equals(loss.Reason, "pre_candidate_gap_tail_rejected", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.RecentLosses, static loss => loss.FrameId == 5);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_SimplifiedRecovery_EmitsRecoveryOwnerWithoutProtectedFollowers()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var now = new DateTimeOffset(2026, 4, 20, 8, 10, 0, TimeSpan.Zero);
        var reassembler = new ScreenShareVideoFrameReassembler(() => now);
        var readyFrames = new List<(long FrameId, ScreenShareRecoveryDeliveryClass RecoveryDeliveryClass)>();
        reassembler.FrameReady += (_, e) => readyFrames.Add((e.FrameId, e.RecoveryDeliveryClass));

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = "viewer-keyframe-only-emits",
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        CompleteFrame(reassembler, "viewer-keyframe-only-emits", streamEpoch: 1, frameId: 0, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-keyframe-only-emits", streamEpoch: 1, frameId: 2, isKeyFrame: false);
        now = now.AddMilliseconds(300);
        CompleteFrame(reassembler, "viewer-keyframe-only-emits", streamEpoch: 1, frameId: 4, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-keyframe-only-emits", streamEpoch: 1, frameId: 5, isKeyFrame: false);

        Assert.Contains(readyFrames, static frame => frame.FrameId == 4 && frame.RecoveryDeliveryClass == ScreenShareRecoveryDeliveryClass.RecoveryOwner);
        Assert.Contains(readyFrames, static frame => frame.FrameId == 5 && frame.RecoveryDeliveryClass == ScreenShareRecoveryDeliveryClass.Normal);
        Assert.DoesNotContain(readyFrames, static frame => frame.RecoveryDeliveryClass == ScreenShareRecoveryDeliveryClass.ProtectedFollower);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_ResyncPreservesContiguousFollowersBehindRecoveryKeyframe()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var now = new DateTimeOffset(2026, 4, 17, 10, 0, 0, TimeSpan.Zero);
        var reassembler = new ScreenShareVideoFrameReassembler(() => now);
        var readyFrameIds = new List<long>();
        reassembler.FrameReady += (_, e) => readyFrameIds.Add(e.FrameId);

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = "viewer-recovery-followers",
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        CompleteFrame(reassembler, "viewer-recovery-followers", streamEpoch: 1, frameId: 0, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-recovery-followers", streamEpoch: 1, frameId: 2, isKeyFrame: false);
        now = now.AddMilliseconds(300);
        CompleteFrame(reassembler, "viewer-recovery-followers", streamEpoch: 1, frameId: 4, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-recovery-followers", streamEpoch: 1, frameId: 5, isKeyFrame: false);
        CompleteFrame(reassembler, "viewer-recovery-followers", streamEpoch: 1, frameId: 6, isKeyFrame: false);

        Assert.Equal(new long[] { 0, 4, 5, 6 }, readyFrameIds);
        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-recovery-followers");
        Assert.Equal(1, snapshot.RecoveryKeyframeResyncCount);
        Assert.Equal(4, snapshot.FramesEmitted);
        Assert.Equal(0, snapshot.RunwayFollowersEmittedWithinActionableWindowCount);
        Assert.Equal(0, snapshot.UnattributedLossCount);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_NonContiguousFollowerStartsNewGapAndStillWaitsForKeyframe()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var now = new DateTimeOffset(2026, 4, 17, 10, 10, 0, TimeSpan.Zero);
        var reassembler = new ScreenShareVideoFrameReassembler(() => now);
        var readyFrameIds = new List<long>();
        reassembler.FrameReady += (_, e) => readyFrameIds.Add(e.FrameId);

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = "viewer-noncontiguous-followers",
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        CompleteFrame(reassembler, "viewer-noncontiguous-followers", streamEpoch: 1, frameId: 0, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-noncontiguous-followers", streamEpoch: 1, frameId: 2, isKeyFrame: false);
        now = now.AddMilliseconds(300);
        CompleteFrame(reassembler, "viewer-noncontiguous-followers", streamEpoch: 1, frameId: 4, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-noncontiguous-followers", streamEpoch: 1, frameId: 6, isKeyFrame: false);

        Assert.Equal(new long[] { 0, 4 }, readyFrameIds);

        CompleteFrame(reassembler, "viewer-noncontiguous-followers", streamEpoch: 1, frameId: 5, isKeyFrame: false);
        Assert.Equal(new long[] { 0, 4 }, readyFrameIds);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-noncontiguous-followers");
        Assert.Equal(1, snapshot.RecoveryKeyframeResyncCount);
        Assert.Equal(0, snapshot.RecoveryRunwayOverflowRejectCount);
        Assert.Equal(0, snapshot.StaleRunwayWindowAbortCount);
        Assert.Equal(0, snapshot.LateSameEpochAfterHeadAdvancedDropCount);
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 5 && string.Equals(loss.Reason, "pre_candidate_gap_tail_rejected", StringComparison.Ordinal));
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 6 && string.Equals(loss.Reason, "pre_candidate_gap_tail_rejected", StringComparison.Ordinal));
        Assert.Equal(0, snapshot.UnattributedLossCount);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_Attribution_TracksOneShotResyncPurge()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var now = new DateTimeOffset(2026, 4, 14, 15, 5, 0, TimeSpan.Zero);
        var reassembler = new ScreenShareVideoFrameReassembler(() => now);
        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = "viewer-attribution",
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        CompleteFrame(reassembler, "viewer-attribution", streamEpoch: 1, frameId: 0, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-attribution", streamEpoch: 1, frameId: 2, isKeyFrame: false);
        CompleteFrame(reassembler, "viewer-attribution", streamEpoch: 1, frameId: 3, isKeyFrame: false);
        now = now.AddMilliseconds(300);
        CompleteFrame(reassembler, "viewer-attribution", streamEpoch: 1, frameId: 5, isKeyFrame: true);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-attribution");
        Assert.Equal(2, snapshot.PreCandidateGapTailRejectedCount);
        Assert.Equal(0, snapshot.ReadyFrameSkippedReplacedLossCount);
        Assert.Equal(1, snapshot.RecoveryKeyframeResyncCount);
        Assert.Equal("future_tail_pruned_while_gap_active", snapshot.DominantReassemblerRootCause);
        var epochDiagnostics = Assert.Single(snapshot.EpochDiagnostics);
        Assert.Equal("future_tail_pruned_while_gap_active", epochDiagnostics.DominantReassemblerRootCause);
        Assert.Contains(epochDiagnostics.TimelineEvents, static timelineEvent => string.Equals(timelineEvent.EventName, "resync_triggered", StringComparison.Ordinal));
        Assert.Contains(epochDiagnostics.TopLossBursts, static burst => string.Equals(burst.RootCause, "future_tail_pruned_while_gap_active", StringComparison.Ordinal));
        Assert.Equal(0, snapshot.UnattributedLossCount);
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 2 && string.Equals(loss.Reason, "pre_candidate_gap_tail_rejected", StringComparison.Ordinal));
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 3 && string.Equals(loss.Reason, "pre_candidate_gap_tail_rejected", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_NewerSameEpochRecoveryCandidate_ReplacesOlderBufferedOwner()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var sessionId = "viewer-superseded-recovery-tail-" + Guid.NewGuid().ToString("N");
        var reassembler = new ScreenShareVideoFrameReassembler();
        var readyFrameIds = new List<long>();
        reassembler.FrameReady += (_, e) => readyFrameIds.Add(e.FrameId);

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = sessionId,
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        CompleteFrame(reassembler, sessionId, streamEpoch: 1, frameId: 0, isKeyFrame: true);
        CompleteFrame(reassembler, sessionId, streamEpoch: 1, frameId: 2, isKeyFrame: false);
        reassembler.OnFragment(CreatePartialFragment(sessionId, streamEpoch: 1, frameId: 4, fragmentIndex: 0, isKeyFrame: true));
        CompleteFrame(reassembler, sessionId, streamEpoch: 1, frameId: 7, isKeyFrame: true);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot(sessionId);
        var epochDiagnostics = Assert.Single(snapshot.EpochDiagnostics);

        Assert.Equal(new long[] { 0, 7 }, readyFrameIds);
        Assert.Equal(7L, snapshot.WinningRecoveryFrameId);
        Assert.Equal(7L, epochDiagnostics.WinningRecoveryFrameId);
        Assert.True(snapshot.RecoveryOwnerReplacedCount >= 1);
        Assert.True(epochDiagnostics.RecoveryOwnerReplacedCount >= 1);
        Assert.True(snapshot.RecoveryKeyframeSupersededOrReplacedCount >= 1);
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 4 && string.Equals(loss.Reason, "gap_recovery_keyframe_replaced", StringComparison.Ordinal));
        Assert.Equal(0, snapshot.LateFragmentAfterHeadAdvancedCount);
        Assert.Equal(0, epochDiagnostics.LateFragmentAfterHeadAdvancedCount);
        var logText = LocalOperationalLog.GetRecentLogText();
        Assert.Contains("event=screenshare_reassembler_recovery_owner_replaced", logText, StringComparison.Ordinal);
        Assert.Contains("stream_epoch=1", logText, StringComparison.Ordinal);
        Assert.Contains("previous_recovery_owner_frame_id=4", logText, StringComparison.Ordinal);
        Assert.Contains("new_recovery_owner_frame_id=7", logText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_Investigation_DistinguishesGapTailSuppressionRecoveryOwnerSuppressionAndOrderedHeadCleanup()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();

        var preCandidateSessionId = "viewer-investigation-pre-candidate-" + Guid.NewGuid().ToString("N");
        var preCandidateReassembler = new ScreenShareVideoFrameReassembler();
        preCandidateReassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = preCandidateSessionId,
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });
        CompleteFrame(preCandidateReassembler, preCandidateSessionId, streamEpoch: 1, frameId: 0, isKeyFrame: true);
        CompleteFrame(preCandidateReassembler, preCandidateSessionId, streamEpoch: 1, frameId: 2, isKeyFrame: false);
        var preCandidateSnapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot(preCandidateSessionId);
        Assert.Contains(
            preCandidateSnapshot.RecentLosses,
            static loss => loss.FrameId == 2 && string.Equals(loss.Reason, "pre_candidate_gap_tail_rejected", StringComparison.Ordinal));

        var suppressedRecoveryOwnerSessionId = "viewer-investigation-owner-suppressed-" + Guid.NewGuid().ToString("N");
        var suppressedRecoveryOwnerReassembler = new ScreenShareVideoFrameReassembler();
        suppressedRecoveryOwnerReassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = suppressedRecoveryOwnerSessionId,
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });
        CompleteFrame(suppressedRecoveryOwnerReassembler, suppressedRecoveryOwnerSessionId, streamEpoch: 1, frameId: 0, isKeyFrame: true);
        suppressedRecoveryOwnerReassembler.OnFragment(CreatePartialFragment(suppressedRecoveryOwnerSessionId, streamEpoch: 1, frameId: 7, fragmentIndex: 0, isKeyFrame: true));
        CompleteFrame(suppressedRecoveryOwnerReassembler, suppressedRecoveryOwnerSessionId, streamEpoch: 1, frameId: 4, isKeyFrame: true);
        var suppressedRecoveryOwnerSnapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot(suppressedRecoveryOwnerSessionId);
        Assert.Contains(
            suppressedRecoveryOwnerSnapshot.RecentLosses,
            static loss => loss.FrameId == 4 && string.Equals(loss.Reason, "same_epoch_recovery_owner_suppressed", StringComparison.Ordinal));

        var orderedHeadCleanupSessionId = "viewer-investigation-ordered-head-" + Guid.NewGuid().ToString("N");
        var orderedHeadCleanupReassembler = new ScreenShareVideoFrameReassembler();
        orderedHeadCleanupReassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = orderedHeadCleanupSessionId,
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });
        CompleteFrame(orderedHeadCleanupReassembler, orderedHeadCleanupSessionId, streamEpoch: 1, frameId: 0, isKeyFrame: true);
        CompleteFrame(orderedHeadCleanupReassembler, orderedHeadCleanupSessionId, streamEpoch: 1, frameId: 1, isKeyFrame: false);
        CompleteFrame(orderedHeadCleanupReassembler, orderedHeadCleanupSessionId, streamEpoch: 1, frameId: 2, isKeyFrame: false);
        CompleteFrame(orderedHeadCleanupReassembler, orderedHeadCleanupSessionId, streamEpoch: 1, frameId: 1, isKeyFrame: false);
        var orderedHeadCleanupSnapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot(orderedHeadCleanupSessionId);
        Assert.Contains(
            orderedHeadCleanupSnapshot.RecentLosses,
            static loss => loss.FrameId == 1 && string.Equals(loss.Reason, "late_fragment_after_ordered_head", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_OlderEpochLateBurstAfterEpochAdvance_IsClassifiedAsNonLossCleanup()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var now = new DateTimeOffset(2026, 4, 22, 12, 38, 34, TimeSpan.Zero);
        var sessionId = "viewer-investigation-older-epoch-late-burst-" + Guid.NewGuid().ToString("N");
        var reassembler = new ScreenShareVideoFrameReassembler(() => now);
        var readyFrameIds = new List<(long StreamEpoch, long FrameId)>();
        reassembler.FrameReady += (_, e) => readyFrameIds.Add((e.StreamEpoch, e.FrameId));

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = sessionId,
            StreamEpoch = 5,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        CompleteFrame(reassembler, sessionId, streamEpoch: 5, frameId: 0, isKeyFrame: true);
        reassembler.OnFragment(CreatePartialFragment(sessionId, streamEpoch: 5, frameId: 114, fragmentIndex: 0, isKeyFrame: true));
        now = now.AddMilliseconds(300);
        CompleteFrame(reassembler, sessionId, streamEpoch: 5, frameId: 116, isKeyFrame: true);

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = sessionId,
            StreamEpoch = 6,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });
        CompleteFrame(reassembler, sessionId, streamEpoch: 6, frameId: 0, isKeyFrame: true);

        var snapshotBeforeOlderEpochCleanup = ScreenShareFrameLossAttributionRegistry.GetSnapshot(sessionId);
        var epochFiveBeforeOlderEpochCleanup = snapshotBeforeOlderEpochCleanup.EpochDiagnostics.Single(static epoch => epoch.StreamEpoch == 5);

        for (var frameId = 117L; frameId <= 124L; frameId++)
        {
            CompleteFrame(reassembler, sessionId, streamEpoch: 5, frameId: frameId, isKeyFrame: false);
        }

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot(sessionId);
        var epochFive = snapshot.EpochDiagnostics.Single(static epoch => epoch.StreamEpoch == 5);

        Assert.Equal(
            new (long StreamEpoch, long FrameId)[] { (5, 0), (5, 116), (6, 0) },
            readyFrameIds);
        Assert.True(epochFive.RecoveryOwnerReplacedCount >= 1);
        Assert.True(snapshot.OlderEpochCleanupAfterEpochAdvanceCount >= 8);
        Assert.True(epochFive.OlderEpochCleanupAfterEpochAdvanceCount >= 8);
        Assert.Equal(snapshotBeforeOlderEpochCleanup.ReassemblerLossCount, snapshot.ReassemblerLossCount);
        Assert.Equal(0, snapshot.LateFragmentAfterHeadAdvancedCount);
        Assert.Equal(epochFiveBeforeOlderEpochCleanup.LateFragmentAfterHeadAdvancedCount, epochFive.LateFragmentAfterHeadAdvancedCount);
        Assert.DoesNotContain(
            epochFive.TopLossBursts,
            static burst =>
                string.Equals(burst.RootCause, "late_fragment_after_head_advanced", StringComparison.Ordinal) &&
                burst.ExpectedNextFrameId == 1 &&
                burst.ReceivedFrameIdStart == 117 &&
                burst.ReceivedFrameIdEnd == 124);

        var logText = LocalOperationalLog.GetRecentLogText();
        Assert.Contains("event=screenshare_reassembler_older_epoch_cleanup_after_epoch_advance", logText, StringComparison.Ordinal);
        Assert.Contains("stream_epoch=5", logText, StringComparison.Ordinal);
        Assert.Contains("session_current_stream_epoch=6", logText, StringComparison.Ordinal);
        Assert.Contains("source=incoming_fragment", logText, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "event=screenshare_reassembler_actionable_late_fragment; session_id=(redacted); stream_epoch=5; session_current_stream_epoch=6; frame_id=117",
            logText,
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_EpochAdvancePurge_IsClassifiedAsNonLossCleanup()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var sessionId = "viewer-investigation-epoch-advance-purge-" + Guid.NewGuid().ToString("N");
        var reassembler = new ScreenShareVideoFrameReassembler();

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = sessionId,
            StreamEpoch = 5,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        CompleteFrame(reassembler, sessionId, streamEpoch: 5, frameId: 0, isKeyFrame: true);
        reassembler.OnFragment(CreatePartialFragment(sessionId, streamEpoch: 5, frameId: 10, fragmentIndex: 0, isKeyFrame: true));

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = sessionId,
            StreamEpoch = 6,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot(sessionId);
        var epochFive = snapshot.EpochDiagnostics.Single(static epoch => epoch.StreamEpoch == 5);

        Assert.Equal(0, snapshot.ReassemblerLossCount);
        Assert.Equal(0, snapshot.LateFragmentAfterHeadAdvancedCount);
        Assert.Equal(0, epochFive.LateFragmentAfterHeadAdvancedCount);
        Assert.Equal(1, snapshot.OlderEpochCleanupAfterEpochAdvanceCount);
        Assert.Equal(1, epochFive.OlderEpochCleanupAfterEpochAdvanceCount);

        var logText = LocalOperationalLog.GetRecentLogText();
        Assert.Contains("event=screenshare_reassembler_older_epoch_cleanup_after_epoch_advance", logText, StringComparison.Ordinal);
        Assert.Contains("session_current_stream_epoch=6", logText, StringComparison.Ordinal);
        Assert.Contains("frame_id=10", logText, StringComparison.Ordinal);
        Assert.Contains("source=epoch_advance_purge", logText, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "event=screenshare_reassembler_actionable_late_fragment; session_id=(redacted); stream_epoch=5; session_current_stream_epoch=6; frame_id=10",
            logText,
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_PostResyncOlderTail_IsCountedAsSupersededCleanup()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var now = new DateTimeOffset(2026, 4, 18, 11, 0, 0, TimeSpan.Zero);
        var reassembler = new ScreenShareVideoFrameReassembler(() => now);
        var readyFrameIds = new List<long>();
        reassembler.FrameReady += (_, e) => readyFrameIds.Add(e.FrameId);

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = "viewer-post-resync-superseded-tail",
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        CompleteFrame(reassembler, "viewer-post-resync-superseded-tail", streamEpoch: 1, frameId: 0, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-post-resync-superseded-tail", streamEpoch: 1, frameId: 2, isKeyFrame: false);
        now = now.AddMilliseconds(300);
        CompleteFrame(reassembler, "viewer-post-resync-superseded-tail", streamEpoch: 1, frameId: 4, isKeyFrame: true);

        Assert.Equal(new long[] { 0, 4 }, readyFrameIds);

        CompleteFrame(reassembler, "viewer-post-resync-superseded-tail", streamEpoch: 1, frameId: 3, isKeyFrame: false);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-post-resync-superseded-tail");
        var epochDiagnostics = Assert.Single(snapshot.EpochDiagnostics);

        Assert.True(snapshot.SupersededRecoveryTailCleanupCount >= 1);
        Assert.True(epochDiagnostics.SupersededRecoveryTailCleanupCount >= 1);
        Assert.Equal(0, snapshot.LateFragmentAfterHeadAdvancedCount);
        Assert.Equal(0, epochDiagnostics.LateFragmentAfterHeadAdvancedCount);
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 3 && string.Equals(loss.Reason, "superseded_recovery_tail_cleanup", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_LateFragmentBehindOrderedHead_IsBenignCleanup()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var reassembler = new ScreenShareVideoFrameReassembler();
        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = "viewer-ordered-head-cleanup",
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        CompleteFrame(reassembler, "viewer-ordered-head-cleanup", streamEpoch: 1, frameId: 0, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-ordered-head-cleanup", streamEpoch: 1, frameId: 1, isKeyFrame: false);
        CompleteFrame(reassembler, "viewer-ordered-head-cleanup", streamEpoch: 1, frameId: 2, isKeyFrame: false);

        CompleteFrame(reassembler, "viewer-ordered-head-cleanup", streamEpoch: 1, frameId: 1, isKeyFrame: false);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-ordered-head-cleanup");
        var epochDiagnostics = Assert.Single(snapshot.EpochDiagnostics);

        Assert.True(snapshot.OrderedEmitHeadFrameId >= 2);
        Assert.True(epochDiagnostics.OrderedEmitHeadFrameId >= 2);
        Assert.Equal(0, snapshot.LateFragmentAfterHeadAdvancedCount);
        Assert.Equal(0, epochDiagnostics.LateFragmentAfterHeadAdvancedCount);
        Assert.Contains(snapshot.RecentLosses, static loss => loss.FrameId == 1 && string.Equals(loss.Reason, "late_fragment_after_ordered_head", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_TrimPurgesBufferedFramesBehindAppliedHeadFloor()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var reassembler = new ScreenShareVideoFrameReassembler();
        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = "viewer-proven-head-trim",
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        reassembler.OnFragment(CreatePartialFragment("viewer-proven-head-trim", streamEpoch: 1, frameId: 0, fragmentIndex: 0, isKeyFrame: true));
        ScreenShareFrameLossAttributionRegistry.ObserveFrameApplied(
            "viewer-proven-head-trim",
            streamEpoch: 1,
            frameId: 4,
            isKeyFrame: false);

        reassembler.OnFragment(CreatePartialFragment("viewer-proven-head-trim", streamEpoch: 1, frameId: 6, fragmentIndex: 0, isKeyFrame: true));

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-proven-head-trim");
        var epochDiagnostics = Assert.Single(snapshot.EpochDiagnostics);

        Assert.True(
            snapshot.AssemblyEvictedLossCount + snapshot.ReassemblerStaleSupersededLossCount >= 1,
            $"Expected the stale buffered frame to be dropped from tracked state, but saw assembly_evicted={snapshot.AssemblyEvictedLossCount} and stale_superseded={snapshot.ReassemblerStaleSupersededLossCount}.");
        Assert.Equal(1, snapshot.LateFragmentAfterAppliedHeadCount);
        Assert.Equal(1, epochDiagnostics.LateFragmentAfterAppliedHeadCount);
        Assert.Equal(0, snapshot.LateFragmentAfterHeadAdvancedCount);
        Assert.Equal(0, epochDiagnostics.LateFragmentAfterHeadAdvancedCount);
        Assert.Contains(
            snapshot.RecentLosses,
            static loss => loss.FrameId == 0 && string.Equals(loss.Reason, "late_fragment_after_applied_head", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareVideoFrameReassembler_OlderTailBehindRecoveryOwner_IsBenignCleanup()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var now = new DateTimeOffset(2026, 4, 17, 20, 0, 0, TimeSpan.Zero);
        var reassembler = new ScreenShareVideoFrameReassembler(() => now);
        var readyFrameIds = new List<long>();
        reassembler.FrameReady += (_, e) => readyFrameIds.Add(e.FrameId);

        reassembler.OnStreamConfig(new ScreenShareVideoStreamConfigV1
        {
            SessionId = "viewer-recovery-floor-suppression",
            StreamEpoch = 1,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        });

        CompleteFrame(reassembler, "viewer-recovery-floor-suppression", streamEpoch: 1, frameId: 0, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-recovery-floor-suppression", streamEpoch: 1, frameId: 2, isKeyFrame: false);
        CompleteFrame(reassembler, "viewer-recovery-floor-suppression", streamEpoch: 1, frameId: 4, isKeyFrame: true);
        CompleteFrame(reassembler, "viewer-recovery-floor-suppression", streamEpoch: 1, frameId: 1, isKeyFrame: false);

        Assert.Equal(new long[] { 0, 4 }, readyFrameIds);

        now = now.AddMilliseconds(300);
        CompleteFrame(reassembler, "viewer-recovery-floor-suppression", streamEpoch: 1, frameId: 5, isKeyFrame: false);

        Assert.Equal(new long[] { 0, 4, 5 }, readyFrameIds);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-recovery-floor-suppression");
        Assert.Equal(0, snapshot.SuppressedEmitDuringRecoveryWaitCount);
        Assert.Contains(
            snapshot.RecentLosses,
            static loss => loss.FrameId == 1 && string.Equals(loss.Reason, "superseded_recovery_tail_cleanup", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareFrameLossAttribution_LateFragmentsAfterSuccessfulRecovery_AdvanceStableVisibleHeadFloor()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();

        ScreenShareFrameLossAttributionRegistry.ObserveRecoveryWindowSucceeded(
            "viewer-late-fragment-success",
            streamEpoch: 3,
            recoveryFrameId: 40,
            lastContiguousFrameId: 42);

        ScreenShareFrameLossAttributionRegistry.ObserveFrameApplied(
            "viewer-late-fragment-success",
            streamEpoch: 3,
            frameId: 45,
            isKeyFrame: false);

        ScreenShareFrameLossAttributionRegistry.ObserveReassemblerRootCause(
            "viewer-late-fragment-success",
            streamEpoch: 3,
            frameId: 44,
            rootCause: ScreenShareReassemblerRootCauseBucket.LateFragmentAfterHeadAdvanced,
            expectedNextFrameId: 46,
            receivedFrameId: 44,
            futureNonKeyBufferedCount: 0,
            bufferedRecoveryKeyframeFrameId: -1);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-late-fragment-success");
        var epochDiagnostics = Assert.Single(snapshot.EpochDiagnostics);
        Assert.Equal(45, snapshot.StableVisibleHeadFrameId);
        Assert.Equal(45, epochDiagnostics.StableVisibleHeadFrameId);
        Assert.Equal(45, snapshot.AppliedHeadFrameId);
        Assert.Equal(45, epochDiagnostics.AppliedHeadFrameId);
        Assert.Equal(1, snapshot.LateFragmentAfterAppliedHeadCount);
        Assert.Equal(1, epochDiagnostics.LateFragmentAfterAppliedHeadCount);
        Assert.Equal(0, snapshot.LateFragmentAfterStableVisibleHeadCount);
        Assert.Equal(0, epochDiagnostics.LateFragmentAfterStableVisibleHeadCount);
        Assert.Equal(0, snapshot.LateFragmentAfterSuccessfulRecoveryCount);
        Assert.Equal(0, epochDiagnostics.LateFragmentAfterSuccessfulRecoveryCount);
        Assert.Equal(0, snapshot.LateFragmentAfterHeadAdvancedCount);
        Assert.Equal(0, epochDiagnostics.LateFragmentAfterHeadAdvancedCount);
        Assert.Empty(epochDiagnostics.TopLossBursts);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareFrameLossAttribution_FourContiguousVisibleApplies_ActivateStableVisibleHeadFloor()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();

        for (var frameId = 10; frameId <= 13; frameId++)
        {
            ScreenShareFrameLossAttributionRegistry.ObserveFrameApplied(
                "viewer-stable-visible-head",
                streamEpoch: 4,
                frameId: frameId,
                isKeyFrame: frameId == 10);
        }

        ScreenShareFrameLossAttributionRegistry.ObserveReassemblerRootCause(
            "viewer-stable-visible-head",
            streamEpoch: 4,
            frameId: 12,
            rootCause: ScreenShareReassemblerRootCauseBucket.LateFragmentAfterHeadAdvanced,
            expectedNextFrameId: 14,
            receivedFrameId: 12,
            futureNonKeyBufferedCount: 0,
            bufferedRecoveryKeyframeFrameId: -1);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-stable-visible-head");
        var epochDiagnostics = Assert.Single(snapshot.EpochDiagnostics);
        Assert.Equal(13, snapshot.StableVisibleHeadFrameId);
        Assert.Equal(13, epochDiagnostics.StableVisibleHeadFrameId);
        Assert.Equal(13, snapshot.AppliedHeadFrameId);
        Assert.Equal(13, epochDiagnostics.AppliedHeadFrameId);
        Assert.Equal(1, snapshot.LateFragmentAfterAppliedHeadCount);
        Assert.Equal(1, epochDiagnostics.LateFragmentAfterAppliedHeadCount);
        Assert.Equal(0, snapshot.LateFragmentAfterStableVisibleHeadCount);
        Assert.Equal(0, epochDiagnostics.LateFragmentAfterStableVisibleHeadCount);
        Assert.Equal(0, snapshot.LateFragmentAfterHeadAdvancedCount);
        Assert.Equal(0, epochDiagnostics.LateFragmentAfterHeadAdvancedCount);
        Assert.Equal("none", snapshot.DominantReassemblerRootCause);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareFrameLossAttribution_LateFragmentsAfterAppliedHead_AreCountedSeparately()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();

        ScreenShareFrameLossAttributionRegistry.ObserveFrameApplied(
            "viewer-late-fragment-applied-head",
            streamEpoch: 5,
            frameId: 20,
            isKeyFrame: true);

        ScreenShareFrameLossAttributionRegistry.ObserveReassemblerRootCause(
            "viewer-late-fragment-applied-head",
            streamEpoch: 5,
            frameId: 19,
            rootCause: ScreenShareReassemblerRootCauseBucket.LateFragmentAfterHeadAdvanced,
            expectedNextFrameId: 21,
            receivedFrameId: 19,
            futureNonKeyBufferedCount: 0,
            bufferedRecoveryKeyframeFrameId: -1);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-late-fragment-applied-head");
        var epochDiagnostics = Assert.Single(snapshot.EpochDiagnostics);
        Assert.Equal(20, snapshot.AppliedHeadFrameId);
        Assert.Equal(20, epochDiagnostics.AppliedHeadFrameId);
        Assert.Equal(1, snapshot.LateFragmentAfterAppliedHeadCount);
        Assert.Equal(1, epochDiagnostics.LateFragmentAfterAppliedHeadCount);
        Assert.Equal(0, snapshot.LateFragmentAfterHeadAdvancedCount);
        Assert.Equal(0, epochDiagnostics.LateFragmentAfterHeadAdvancedCount);
        Assert.Equal("none", snapshot.DominantReassemblerRootCause);
        Assert.Empty(epochDiagnostics.TopLossBursts);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareFrameLossAttribution_LateFragmentsAfterVisibleRecovery_AreCountedSeparately()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();

        ScreenShareFrameLossAttributionRegistry.ObserveVisibleRecoveryFloor(
            "viewer-late-fragment-visible-recovery",
            streamEpoch: 3,
            frameId: 40);

        ScreenShareFrameLossAttributionRegistry.ObserveReassemblerRootCause(
            "viewer-late-fragment-visible-recovery",
            streamEpoch: 3,
            frameId: 39,
            rootCause: ScreenShareReassemblerRootCauseBucket.LateFragmentAfterHeadAdvanced,
            expectedNextFrameId: 41,
            receivedFrameId: 39,
            futureNonKeyBufferedCount: 0,
            bufferedRecoveryKeyframeFrameId: -1);

        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot("viewer-late-fragment-visible-recovery");
        var epochDiagnostics = Assert.Single(snapshot.EpochDiagnostics);
        Assert.Equal(1, snapshot.LateFragmentAfterVisibleRecoveryCount);
        Assert.Equal(1, epochDiagnostics.LateFragmentAfterVisibleRecoveryCount);
        Assert.Equal(0, snapshot.LateFragmentAfterHeadAdvancedCount);
        Assert.Equal(0, epochDiagnostics.LateFragmentAfterHeadAdvancedCount);
        Assert.Empty(epochDiagnostics.TopLossBursts);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_H264Frames_WaitForStreamConfigBeforeDecode()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var h264Decoder = new FakeH264BitmapDecoder();
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                h264Decoder: h264Decoder);

            vm.OnOwnedEncodedFrame(
                "h264",
                new byte[] { 4 },
                capturedTsUtcMs: 0,
                isKeyFrame: true,
                streamEpoch: 7);

            await Task.Delay(50);
            Assert.Null(vm.CurrentFrame);
            Assert.Equal(0, h264Decoder.ConfigureCallCount);
            Assert.Equal(0, h264Decoder.DecodeCallCount);

            vm.OnOwnedEncodedFrame(
                "h264",
                new byte[] { 9 },
                capturedTsUtcMs: 0,
                isKeyFrame: true,
                streamEpoch: 7,
                streamConfig: new ScreenShareVideoStreamConfigV1
                {
                    SessionId = "viewer",
                    StreamEpoch = 7,
                    Encoding = "h264",
                    CodecProfile = "baseline",
                    DecoderConfigData = new byte[] { 1, 2, 3 },
                });

            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap latest && latest.PixelSize.Width == 9 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            Assert.Equal(1, h264Decoder.ConfigureCallCount);
            Assert.Equal(1, h264Decoder.DecodeCallCount);
            Assert.Equal(7, h264Decoder.LastConfiguredEpoch);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemoteNeedMoreInput_KeepsViewerLiveWithoutSurfacingFrame()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                h264Decoder: new NeedMoreInputH264BitmapDecoder(),
                logRole: "helper_remote");

            var config = new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper",
                StreamEpoch = 9,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            vm.OnOwnedEncodedFrame("h264", new byte[] { 9 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 9, streamConfig: config);
            await WaitUntilAsync(
                () => vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 10 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 9);
            await WaitUntilAsync(() => vm.IsIdleForDiagnostics, TimeSpan.FromSeconds(2));

            Assert.True(vm.IsActive);
            Assert.Equal("Live", vm.StatusText);
            Assert.Null(vm.CurrentFrame);
            var metrics = vm.GetMetricsSnapshot();
            Assert.Equal(2, metrics.NeedMoreInputCount);
            Assert.Equal(2, metrics.CompletedWithoutPictureCount);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemoteNeedMoreInput_RaisesDecodeNeedsMoreInputForEpoch()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                h264Decoder: new NeedMoreInputH264BitmapDecoder(),
                logRole: "helper_remote");

            var signaledEpochs = new List<long>();
            vm.DecodeNeedsMoreInput += (_, e) => signaledEpochs.Add(e.StreamEpoch);

            var config = new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper",
                StreamEpoch = 11,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            vm.OnOwnedEncodedFrame("h264", new byte[] { 11 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 11, streamConfig: config);
            await WaitUntilAsync(
                () => vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            Assert.Equal(new long[] { 11 }, signaledEpochs);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_FrameGap_RequestsRecoveryBeforeDecode()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                h264Decoder: new FakeH264BitmapDecoder(),
                logRole: "helper_remote");
            var staleDropCount = 0;
            vm.StaleFrameDropped += (_, _) => staleDropCount++;

            ScreenShareViewerContinuityLostEventArgs? continuityLost = null;
            var recoveryAppliedEpochs = new List<long>();
            vm.ContinuityLost += (_, e) => continuityLost = e;
            vm.RecoveryKeyframeApplied += (_, e) => recoveryAppliedEpochs.Add(e.StreamEpoch);

            var config = new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper",
                StreamEpoch = 15,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            vm.OnOwnedEncodedFrame("h264", new byte[] { 10 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 15, streamConfig: config, frameId: 10);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap first && first.PixelSize.Width == 10 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 12 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 15, frameId: 12);
            await WaitUntilAsync(
                () => continuityLost is not null && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            var stillVisible = Assert.IsAssignableFrom<Bitmap>(vm.CurrentFrame);
            Assert.Equal(10, stillVisible.PixelSize.Width);
            Assert.NotNull(continuityLost);
            Assert.Equal("frame_gap", continuityLost!.Reason);
            Assert.True(continuityLost.ShouldRequestRecoveryKeyframe);
            Assert.Equal(11, continuityLost.ExpectedNextFrameId);
            Assert.Equal(12, continuityLost.ReceivedFrameId);
            Assert.Equal(10, continuityLost.LastCleanFrameId);

            var midMetrics = vm.GetMetricsSnapshot();
            Assert.Equal(1, midMetrics.FrameGapContinuityLossCount);
            Assert.Equal(1, midMetrics.FramesDroppedForFrameGap);

            vm.OnOwnedEncodedFrame(
                "h264",
                new byte[] { 13 },
                capturedTsUtcMs: 0,
                isKeyFrame: true,
                streamEpoch: 15,
                frameId: 13,
                recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.RecoveryOwner);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap recovered && recovered.PixelSize.Width == 13 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            Assert.Equal(new long[] { 15 }, recoveryAppliedEpochs);
            var finalMetrics = vm.GetMetricsSnapshot();
            Assert.Equal(0, finalMetrics.BlockedByReservedRecoveryFrameRejectCount);
            Assert.Equal(0, finalMetrics.DecodedBlockedByReservedRecoveryFrameCount);
            Assert.Equal(0, finalMetrics.StartupCorridorReleaseCount);
            Assert.Equal(0, finalMetrics.StartupCorridorAbortCount);
            Assert.Equal(0, finalMetrics.RecoveryProgressCorridorCount);
            Assert.Equal(0, finalMetrics.RecoveryProgressCorridorAbortCount);
            Assert.Equal(0, finalMetrics.ProtectedRecoveryDeliveryCount);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_FirstFrameNonKey_RequestsRecoveryUntilKeyframe()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                h264Decoder: new FakeH264BitmapDecoder(),
                logRole: "helper_remote");

            ScreenShareViewerContinuityLostEventArgs? continuityLost = null;
            vm.ContinuityLost += (_, e) => continuityLost = e;

            var config = new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper",
                StreamEpoch = 16,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            vm.OnOwnedEncodedFrame("h264", new byte[] { 20 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 16, streamConfig: config, frameId: 20);
            await WaitUntilAsync(
                () => continuityLost is not null && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            Assert.Null(vm.CurrentFrame);
            Assert.NotNull(continuityLost);
            Assert.Equal("frame_gap", continuityLost!.Reason);
            Assert.Equal(0, continuityLost.ExpectedNextFrameId);
            Assert.Equal(20, continuityLost.ReceivedFrameId);
            Assert.Equal(-1, continuityLost.LastCleanFrameId);

            vm.OnOwnedEncodedFrame("h264", new byte[] { 21 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 16, frameId: 21);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap recovered && recovered.PixelSize.Width == 21 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_StaleSupersededBeforeVisibleHead_TriggersConservativeRecovery()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                h264Decoder: new FakeH264BitmapDecoder(),
                logRole: "helper_remote");

            ScreenShareViewerContinuityLostEventArgs? continuityLost = null;
            vm.ContinuityLost += (_, e) => continuityLost = e;

            var config = new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper",
                StreamEpoch = 160,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            vm.OnOwnedEncodedFrame(
                "h264",
                new byte[] { 20 },
                capturedTsUtcMs: 0,
                isKeyFrame: true,
                streamEpoch: 160,
                streamConfig: config,
                chunksDroppedOlderFrame: 1,
                frameId: 20);
            await WaitUntilAsync(
                () => continuityLost is not null &&
                      vm.CurrentFrame is Bitmap recovered &&
                      recovered.PixelSize.Width == 20 &&
                      vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            Assert.NotNull(continuityLost);
            Assert.Equal("stale_frame_superseded", continuityLost!.Reason);
            Assert.False(continuityLost.ShouldRequestRecoveryKeyframe);

            var metrics = vm.GetMetricsSnapshot();
            Assert.Equal(1, metrics.ContinuityLossCount);
            Assert.Equal(0, metrics.StaleSupersededRecoverySuppressedCount);
            Assert.Equal(0, metrics.SoftStaleCleanupCount);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_StaleSupersededAfterVisibleHead_DoesNotReopenRecovery()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                h264Decoder: new FakeH264BitmapDecoder(),
                logRole: "helper_remote");

            var continuityLossCount = 0;
            vm.ContinuityLost += (_, _) => continuityLossCount++;

            var config = new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper",
                StreamEpoch = 161,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            vm.OnOwnedEncodedFrame("h264", new byte[] { 30 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 161, streamConfig: config, frameId: 30);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap first && first.PixelSize.Width == 30 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 31 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 161, frameId: 31);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap second && second.PixelSize.Width == 31 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame(
                "h264",
                new byte[] { 32 },
                capturedTsUtcMs: 0,
                isKeyFrame: false,
                streamEpoch: 161,
                chunksDroppedOlderFrame: 1,
                frameId: 32);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap latest && latest.PixelSize.Width == 32 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            var metrics = vm.GetMetricsSnapshot();
            Assert.Equal(0, continuityLossCount);
            Assert.Equal(1, metrics.StaleSupersededRecoverySuppressedCount);
            Assert.Equal(1, metrics.SoftStaleCleanupCount);
            Assert.Equal(32, metrics.VisibleHeadFrameId);
            Assert.Equal(0, metrics.FrameGapContinuityLossCount);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_ProvenHeadFloor_PreservesSoftCleanupAfterViewerReset()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                h264Decoder: new FakeH264BitmapDecoder(),
                logRole: "helper_remote");

            var continuityLossCount = 0;
            vm.ContinuityLost += (_, _) => continuityLossCount++;

            var config = new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper-proven-floor",
                StreamEpoch = 162,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            vm.OnOwnedEncodedFrame("h264", new byte[] { 40 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 162, streamConfig: config, frameId: 40);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap first && first.PixelSize.Width == 40 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.Clear();

            vm.OnOwnedEncodedFrame(
                "h264",
                new byte[] { 41 },
                capturedTsUtcMs: 0,
                isKeyFrame: true,
                streamEpoch: 162,
                streamConfig: config,
                chunksDroppedOlderFrame: 1,
                frameId: 41);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap next && next.PixelSize.Width == 41 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            var metrics = vm.GetMetricsSnapshot();
            Assert.Equal(0, continuityLossCount);
            Assert.Equal(0, metrics.ContinuityLossCount);
            Assert.Equal(1, metrics.SoftStaleCleanupCount);
            Assert.Equal(0, metrics.ActionableLateFragmentCount);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_RecoveryKeyframe_BypassesStaleDropThreshold()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                h264Decoder: new FakeH264BitmapDecoder(),
                logRole: "helper_remote");
            var staleDropCount = 0;
            vm.StaleFrameDropped += (_, _) => staleDropCount++;

            var priorConfig = new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper",
                StreamEpoch = 20,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            vm.OnOwnedEncodedFrame("h264", new byte[] { 40 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), isKeyFrame: true, streamEpoch: 20, streamConfig: priorConfig, frameId: 40);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap first && first.PixelSize.Width == 40 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            var nextConfig = new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper",
                StreamEpoch = 21,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3, 4 },
            };

            vm.OnOwnedEncodedFrame("h264", new byte[] { 50 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), isKeyFrame: false, streamEpoch: 21, streamConfig: nextConfig, frameId: 50);
            await WaitUntilAsync(
                () => vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 51 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 5000, isKeyFrame: true, streamEpoch: 21, frameId: 51);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap recovered && recovered.PixelSize.Width == 51 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            Assert.Equal(0, staleDropCount);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_ProtectedRecoveryFrames_BypassStartupCorridorAndApplyImmediately()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                h264Decoder: new FakeH264BitmapDecoder(),
                logRole: "helper_remote");

            var config = new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper",
                StreamEpoch = 29,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            vm.OnOwnedEncodedFrame("h264", new byte[] { 1 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 29, streamConfig: config, frameId: 0);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap first && first.PixelSize.Width == 1 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 3 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 29, frameId: 2);
            await WaitUntilAsync(() => vm.IsIdleForDiagnostics, TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 4 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 29, frameId: 3, recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.RecoveryOwner);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap recoveryKeyframe && recoveryKeyframe.PixelSize.Width == 4 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 5 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 29, frameId: 4);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap current && current.PixelSize.Width == 5 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            var finalFrame = Assert.IsAssignableFrom<Bitmap>(vm.CurrentFrame);
            Assert.Equal(5, finalFrame.PixelSize.Width);
            Assert.True(vm.IsIdleForDiagnostics);

            var metrics = vm.GetMetricsSnapshot();
            Assert.Equal(0, metrics.StartupCorridorBufferedFollowerCount);
            Assert.Equal(0, metrics.StartupCorridorReleaseCount);
            Assert.Equal(0, metrics.StartupCorridorAbortCount);
            Assert.Equal(0, metrics.RecoveryFollowerWindowBufferedCount);
            Assert.Equal(0, metrics.BlockedByReservedRecoveryFrameRejectCount);
            Assert.Equal(0, metrics.DecodedBlockedByReservedRecoveryFrameCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorSuccessCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorAbortCount);
            Assert.Equal(0, metrics.ProtectedRecoveryDeliveryCount);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_NormalFollowersBeforeRecoveryOwner_AreRejected_NotBuffered()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                h264Decoder: new FakeH264BitmapDecoder(),
                logRole: "helper_remote");

            var config = new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper",
                StreamEpoch = 32,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            vm.OnOwnedEncodedFrame("h264", new byte[] { 20 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 32, streamConfig: config, frameId: 0);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap first && first.PixelSize.Width == 20 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 21 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 32, frameId: 2);
            await WaitUntilAsync(() => vm.IsIdleForDiagnostics, TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 22 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 32, frameId: 3);
            await WaitUntilAsync(() => vm.IsIdleForDiagnostics, TimeSpan.FromSeconds(2));

            var metricsBeforeRecoveryOwner = vm.GetMetricsSnapshot();
            Assert.Equal(0, metricsBeforeRecoveryOwner.RecoveryFollowerWindowBufferedCount);
            Assert.Equal(0, metricsBeforeRecoveryOwner.StartupCorridorBufferedFollowerCount);
            Assert.True(metricsBeforeRecoveryOwner.FramesDroppedWaitingForRecoveryKeyframe >= 1);
            Assert.Equal(20, Assert.IsAssignableFrom<Bitmap>(vm.CurrentFrame).PixelSize.Width);

            vm.OnOwnedEncodedFrame("h264", new byte[] { 24 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 32, frameId: 4, recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.RecoveryOwner);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap recoveryOwner && recoveryOwner.PixelSize.Width == 24,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 25 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 32, frameId: 5);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap follower && follower.PixelSize.Width == 25,
                TimeSpan.FromSeconds(2));

            var metrics = vm.GetMetricsSnapshot();
            Assert.Equal(0, metrics.BlockedByReservedRecoveryFrameRejectCount);
            Assert.Equal(0, metrics.DecodedBlockedByReservedRecoveryFrameCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorSuccessCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorAbortCount);
            Assert.Equal(0, metrics.RecoveryFollowerWindowBufferedCount);
            Assert.Equal(0, metrics.StartupCorridorBufferedFollowerCount);
            Assert.Equal(0, metrics.StartupCorridorReleaseCount);
            Assert.True(metrics.FramesDroppedWaitingForRecoveryKeyframe >= 1);
            Assert.Equal(0, metrics.ProtectedRecoveryDeliveryCount);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_SimplifiedRecoveryOwner_ClearsRecoveryWithoutCorridor()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                h264Decoder: new FakeH264BitmapDecoder(),
                logRole: "helper_remote");

            var config = new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper",
                StreamEpoch = 132,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            vm.OnOwnedEncodedFrame("h264", new byte[] { 20 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 132, streamConfig: config, frameId: 0);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap first && first.PixelSize.Width == 20 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 22 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 132, frameId: 2);
            await WaitUntilAsync(() => vm.IsIdleForDiagnostics, TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 24 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 132, frameId: 4, recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.RecoveryOwner);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap recoveryOwner && recoveryOwner.PixelSize.Width == 24,
                TimeSpan.FromSeconds(2));

            var metrics = vm.GetMetricsSnapshot();
            Assert.Equal(0, metrics.RecoveryProgressCorridorCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorSuccessCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorAbortCount);
            Assert.Equal(0, metrics.RecoveryFollowerWindowBufferedCount);
            Assert.Equal(0, metrics.StartupCorridorBufferedFollowerCount);
            Assert.Equal(0, metrics.ProtectedRecoveryDeliveryCount);
            Assert.True(metrics.FramesDroppedWaitingForRecoveryKeyframe >= 1);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_SimplifiedProtectedFollowerCompatibility_IsTreatedAsNormalWithoutCorridor()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                h264Decoder: new FakeH264BitmapDecoder(),
                logRole: "helper_remote");

            var config = new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper",
                StreamEpoch = 133,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            vm.OnOwnedEncodedFrame("h264", new byte[] { 30 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 133, streamConfig: config, frameId: 0);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap first && first.PixelSize.Width == 30 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 32 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 133, frameId: 2);
            await WaitUntilAsync(() => vm.IsIdleForDiagnostics, TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 33 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 133, frameId: 3, recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.RecoveryOwner);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap recoveryOwner && recoveryOwner.PixelSize.Width == 33,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 34 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 133, frameId: 4, recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.ProtectedFollower);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap follower && follower.PixelSize.Width == 34,
                TimeSpan.FromSeconds(2));

            var metrics = vm.GetMetricsSnapshot();
            Assert.Equal(0, metrics.RecoveryProgressCorridorCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorSuccessCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorAbortCount);
            Assert.Equal(0, metrics.StartupCorridorReleaseCount);
            Assert.Equal(0, metrics.RecoveryFollowerWindowBufferedCount);
            Assert.Equal(0, metrics.ProtectedRecoveryDeliveryCount);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_ProtectedFollowerTags_AreTreatedAsOrdinaryAfterRecoveryKeyframe()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                h264Decoder: new FakeH264BitmapDecoder(),
                logRole: "helper_remote");

            var config = new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper",
                StreamEpoch = 30,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            vm.OnOwnedEncodedFrame("h264", new byte[] { 10 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 30, streamConfig: config, frameId: 10);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap first && first.PixelSize.Width == 10 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 12 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 30, frameId: 12);
            await WaitUntilAsync(() => vm.IsIdleForDiagnostics, TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 13 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 30, frameId: 13, recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.RecoveryOwner);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap recoveryOwner &&
                      recoveryOwner.PixelSize.Width == 13 &&
                      vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 14 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 30, frameId: 14, recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.ProtectedFollower);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap firstFollower &&
                      firstFollower.PixelSize.Width == 14 &&
                      vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 15 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 30, frameId: 15, recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.ProtectedFollower);

            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap recovered &&
                      recovered.PixelSize.Width == 15 &&
                      vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            var metrics = vm.GetMetricsSnapshot();
            Assert.Equal(0, metrics.BlockedByReservedRecoveryFrameRejectCount);
            Assert.Equal(0, metrics.DecodedBlockedByReservedRecoveryFrameCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorSuccessCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorAbortCount);
            Assert.Equal(0, metrics.StartupCorridorReleaseCount);
            Assert.Equal(0, metrics.RecoveryFollowerWindowBufferedCount);
            Assert.Equal(0, metrics.ProtectedRecoveryDeliveryCount);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_PostRecoveryFramesResumeAsOrdinaryTraffic()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                h264Decoder: new FakeH264BitmapDecoder(),
                logRole: "helper_remote");

            var config = new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper",
                StreamEpoch = 300,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            vm.OnOwnedEncodedFrame("h264", new byte[] { 30 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 300, streamConfig: config, frameId: 30);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap first && first.PixelSize.Width == 30 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 32 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 300, frameId: 32);
            await WaitUntilAsync(() => vm.IsIdleForDiagnostics, TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 33 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 300, frameId: 33, recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.RecoveryOwner);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap recoveryOwner &&
                      recoveryOwner.PixelSize.Width == 33 &&
                      vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 34 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 300, frameId: 34, recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.ProtectedFollower);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap firstFollower && firstFollower.PixelSize.Width == 34,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 35 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 300, frameId: 35, recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.ProtectedFollower);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap secondFollower &&
                      secondFollower.PixelSize.Width == 35 &&
                      vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 36 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 300, frameId: 36);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap latest && latest.PixelSize.Width == 36,
                TimeSpan.FromSeconds(2));

            var metrics = vm.GetMetricsSnapshot();
            Assert.Equal(0, metrics.RecoveryProgressCorridorCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorSuccessCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorAbortCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorAppliedCount);
            Assert.Equal(0, metrics.StartupCorridorReleaseCount);
            Assert.Equal(0, metrics.RecoveryFollowerWindowBufferedCount);
            Assert.Equal(0, metrics.ProtectedRecoveryDeliveryCount);
            Assert.True(metrics.PostRecoveryVisibleGenerationResetCount >= 1);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_InFlightKeyframe_DoesNotBecomeRecoveryApply_WhenRecoveryStartsLater()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var decodeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDecode = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var recoveryKeyframeAppliedCount = 0;

            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                h264Decoder: new FrameBlockingH264BitmapDecoder(11, decodeStarted, releaseDecode),
                logRole: "helper_remote");

            var config = new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper",
                StreamEpoch = 301,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            vm.OnOwnedEncodedFrame("h264", new byte[] { 10 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 301, streamConfig: config, frameId: 10);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap first && first.PixelSize.Width == 10 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            var initialMetrics = vm.GetMetricsSnapshot();
            vm.RecoveryKeyframeApplied += (_, _) => Interlocked.Increment(ref recoveryKeyframeAppliedCount);

            vm.OnOwnedEncodedFrame("h264", new byte[] { 11 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 301, frameId: 11);
            await decodeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 12 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 301, frameId: 12);
            vm.OnOwnedEncodedFrame("h264", new byte[] { 13 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 301, frameId: 13);
            vm.OnOwnedEncodedFrame("h264", new byte[] { 14 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 301, frameId: 14);
            vm.OnOwnedEncodedFrame("h264", new byte[] { 15 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 301, frameId: 15);
            vm.OnOwnedEncodedFrame("h264", new byte[] { 16 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 301, frameId: 16);

            await WaitUntilAsync(
                () => vm.GetMetricsSnapshot().ContinuityLossCount >= initialMetrics.ContinuityLossCount + 1,
                TimeSpan.FromSeconds(2));

            releaseDecode.TrySetResult(true);
            await WaitUntilAsync(() => vm.IsIdleForDiagnostics, TimeSpan.FromSeconds(2));

            var metrics = vm.GetMetricsSnapshot();
            Assert.Equal(0, Volatile.Read(ref recoveryKeyframeAppliedCount));
            Assert.Equal(initialMetrics.RecoveryProgressCorridorCount, metrics.RecoveryProgressCorridorCount);
            Assert.True(metrics.ContinuityLossCount >= initialMetrics.ContinuityLossCount + 1);
            Assert.True(metrics.FramesDroppedWaitingForRecoveryKeyframe >= initialMetrics.FramesDroppedWaitingForRecoveryKeyframe + 1);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_NonContiguousFollowerAfterRecovery_StartsNewGapRecovery()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                h264Decoder: new FakeH264BitmapDecoder(),
                logRole: "helper_remote");

            var config = new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper",
                StreamEpoch = 31,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            vm.OnOwnedEncodedFrame("h264", new byte[] { 20 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 31, streamConfig: config, frameId: 20);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap first && first.PixelSize.Width == 20 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 22 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 31, frameId: 22);
            await WaitUntilAsync(() => vm.IsIdleForDiagnostics, TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 23 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 31, frameId: 23, recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.RecoveryOwner);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap recoveryOwner && recoveryOwner.PixelSize.Width == 23,
                TimeSpan.FromSeconds(2));
            var metricsBeforeGap = vm.GetMetricsSnapshot();
            vm.OnOwnedEncodedFrame("h264", new byte[] { 25 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 31, frameId: 25, recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.ProtectedFollower);

            await WaitUntilAsync(
                () => vm.IsIdleForDiagnostics &&
                      vm.GetMetricsSnapshot().FramesDroppedWaitingForRecoveryKeyframe >= metricsBeforeGap.FramesDroppedWaitingForRecoveryKeyframe + 1,
                TimeSpan.FromSeconds(2));

            var current = Assert.IsAssignableFrom<Bitmap>(vm.CurrentFrame);
            Assert.Equal(23, current.PixelSize.Width);
            var metrics = vm.GetMetricsSnapshot();
            Assert.True(metrics.FrameGapContinuityLossCount >= metricsBeforeGap.FrameGapContinuityLossCount + 1);
            Assert.True(metrics.ContinuityLossCount >= metricsBeforeGap.ContinuityLossCount + 1);
            Assert.Equal(0, metrics.RecoveryProgressCorridorCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorSuccessCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorAbortCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorAppliedCount);
            Assert.Equal(0, metrics.StartupCorridorAbortCount);
            Assert.Equal("none", metrics.StartupCorridorAbortReason);
            Assert.Equal(0, metrics.ProtectedRecoveryDeliveryCount);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_LateMissingFollowerDuringGap_IsDroppedWaitingForKeyframe()
    {
        await fixture.Session.Dispatch(async () =>
        {
            ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                h264Decoder: new FakeH264BitmapDecoder(),
                logRole: "helper_remote");

            var config = new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper",
                StreamEpoch = 310,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            vm.OnOwnedEncodedFrame("h264", new byte[] { 20 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 310, streamConfig: config, frameId: 20);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap first && first.PixelSize.Width == 20 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 23 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 310, frameId: 23, recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.RecoveryOwner);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap recoveryOwner && recoveryOwner.PixelSize.Width == 23 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            var metricsBeforeGap = vm.GetMetricsSnapshot();
            vm.OnOwnedEncodedFrame("h264", new byte[] { 25 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 310, frameId: 25);
            await WaitUntilAsync(
                () => vm.IsIdleForDiagnostics &&
                      vm.GetMetricsSnapshot().FramesDroppedWaitingForRecoveryKeyframe >= metricsBeforeGap.FramesDroppedWaitingForRecoveryKeyframe + 1,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 24 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 310, frameId: 24);
            await WaitUntilAsync(
                () => vm.IsIdleForDiagnostics &&
                      vm.GetMetricsSnapshot().FramesDroppedWaitingForRecoveryKeyframe >= metricsBeforeGap.FramesDroppedWaitingForRecoveryKeyframe + 2,
                TimeSpan.FromSeconds(2));

            var current = Assert.IsAssignableFrom<Bitmap>(vm.CurrentFrame);
            Assert.Equal(23, current.PixelSize.Width);

            var metrics = vm.GetMetricsSnapshot();
            Assert.Equal(0, metrics.RecoveryProgressCorridorCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorAbortCount);
            Assert.Equal(0, metrics.StartupCorridorAbortCount);

            var snapshot = vm.GetFrameLossSnapshotForDiagnostics();
            Assert.Equal(0, snapshot.StaleRunwayWindowAbortCount);
            Assert.Equal(0, snapshot.LateSameEpochAfterHeadAdvancedDropCount);
            Assert.Contains(
                snapshot.RecentLosses,
                static loss => loss.FrameId == 25 && string.Equals(loss.Reason, "waiting_for_recovery_keyframe", StringComparison.Ordinal));
            Assert.Contains(
                snapshot.RecentLosses,
                static loss => loss.FrameId == 24 && string.Equals(loss.Reason, "waiting_for_recovery_keyframe", StringComparison.Ordinal));
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_RecoveryOwnerUiDelay_DoesNotUseStartupCorridorTimeout()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var pendingUiActions = new Queue<Action>();
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                postToUiAsync: action =>
                {
                    pendingUiActions.Enqueue(action);
                    return Task.CompletedTask;
                },
                h264Decoder: new FakeH264BitmapDecoder(),
                logRole: "helper_remote");

            var config = new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper",
                StreamEpoch = 32,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            vm.OnOwnedEncodedFrame("h264", new byte[] { 23 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 32, streamConfig: config, frameId: 23, recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.RecoveryOwner);
            await WaitUntilAsync(() => pendingUiActions.Count > 0, TimeSpan.FromSeconds(2));

            await Task.Delay(TimeSpan.FromMilliseconds(500));
            var metricsBeforeRelease = vm.GetMetricsSnapshot();
            Assert.Equal(0, metricsBeforeRelease.StartupCorridorAbortCount);
            Assert.Equal(0, metricsBeforeRelease.RecoveryProgressCorridorAbortCount);
            Assert.Null(vm.CurrentFrame);

            while (pendingUiActions.Count > 0)
            {
                pendingUiActions.Dequeue().Invoke();
                await Task.Yield();
            }

            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap progressed && progressed.PixelSize.Width == 23,
                TimeSpan.FromSeconds(2));

            var metrics = vm.GetMetricsSnapshot();
            Assert.Equal(0, metrics.BlockedByReservedRecoveryFrameRejectCount);
            Assert.Equal(0, metrics.DecodedBlockedByReservedRecoveryFrameCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorSuccessCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorAbortCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorAppliedCount);
            Assert.Equal(0, metrics.StartupCorridorAbortCount);
            Assert.Equal("none", metrics.StartupCorridorAbortReason);
            Assert.Equal(0, metrics.ProtectedRecoveryDeliveryCount);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_NewerEpochNonKey_IsIgnoredUntilRecoveryKeyframeArrives()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                h264Decoder: new FakeH264BitmapDecoder(),
                logRole: "helper_remote");

            var config = new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper",
                StreamEpoch = 18,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            vm.OnOwnedEncodedFrame("h264", new byte[] { 40 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 18, streamConfig: config, frameId: 40);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap first && first.PixelSize.Width == 40 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 42 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 18, frameId: 42);
            await WaitUntilAsync(() => vm.IsIdleForDiagnostics, TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame(
                "h264",
                new byte[] { 50 },
                capturedTsUtcMs: 0,
                isKeyFrame: false,
                streamEpoch: 19,
                streamConfig: new ScreenShareVideoStreamConfigV1
                {
                    SessionId = "helper",
                    StreamEpoch = 19,
                    Encoding = "h264",
                    CodecProfile = "baseline",
                    DecoderConfigData = new byte[] { 4, 5, 6 },
                },
                frameId: 0);
            await WaitUntilAsync(() => vm.IsIdleForDiagnostics, TimeSpan.FromSeconds(2));

            var stillVisible = Assert.IsAssignableFrom<Bitmap>(vm.CurrentFrame);
            Assert.Equal(40, stillVisible.PixelSize.Width);

            vm.OnOwnedEncodedFrame("h264", new byte[] { 51 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 19, frameId: 1);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap recovered && recovered.PixelSize.Width == 51 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            var metrics = vm.GetMetricsSnapshot();
            Assert.True(metrics.FramesDroppedWaitingForRecoveryKeyframe >= 1);
            Assert.True(metrics.NewerEpochNonKeyIgnoredDuringLockCount >= 1);
            return true;
        }, default);
    }

    public async Task ScreenShareViewer_HelperRemote_SequentialPFrames_StayLiveWithoutRecovery()
    {
        await fixture.Session.Dispatch(async () =>
        {
            ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                h264Decoder: new FakeH264BitmapDecoder(),
                logRole: "helper_remote");

            var continuityLossCount = 0;
            vm.ContinuityLost += (_, _) => continuityLossCount++;

            var config = new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper",
                StreamEpoch = 17,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            vm.OnOwnedEncodedFrame("h264", new byte[] { 30 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 17, streamConfig: config, frameId: 30, sessionId: "helper-sequential");
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap first && first.PixelSize.Width == 30 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 31 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 17, frameId: 31, sessionId: "helper-sequential");
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap second && second.PixelSize.Width == 31 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 32 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 17, frameId: 32, sessionId: "helper-sequential");
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap third && third.PixelSize.Width == 32 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            Assert.Equal(0, continuityLossCount);
            Assert.Equal(0, vm.GetMetricsSnapshot().FrameGapContinuityLossCount);
            var snapshot = vm.GetFrameLossSnapshotForDiagnostics();
            Assert.Equal(3, snapshot.FramesEmitted);
            Assert.Equal(3, snapshot.FramesApplied);
            Assert.Equal(0, snapshot.UnattributedLossCount);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_RecoveryOwnerThenLaterFramesApplyInOrderWithoutProtectedWindow()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var blockReservedApply = false;
            var reservedApplyReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var appliedFrameIds = new List<long>();
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                postToUiAsync: async action =>
                {
                    if (blockReservedApply)
                    {
                        await reservedApplyReleased.Task.ConfigureAwait(false);
                    }

                    action();
                },
                h264Decoder: new FakeH264BitmapDecoder(),
                logRole: "helper_remote");
            vm.FrameApplied += (_, args) =>
            {
                appliedFrameIds.Add(args.FrameId);
            };

            var config = new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper",
                StreamEpoch = 30,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            vm.OnOwnedEncodedFrame("h264", new byte[] { 70 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 30, streamConfig: config, frameId: 70);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap first && first.PixelSize.Width == 70 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 72 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 30, frameId: 72);
            await WaitUntilAsync(() => vm.IsIdleForDiagnostics, TimeSpan.FromSeconds(2));

            blockReservedApply = true;
            vm.OnOwnedEncodedFrame("h264", new byte[] { 73 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 30, frameId: 73, recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.RecoveryOwner);
            await Task.Delay(100);

            var stillVisible = Assert.IsAssignableFrom<Bitmap>(vm.CurrentFrame);
            Assert.Equal(70, stillVisible.PixelSize.Width);

            reservedApplyReleased.TrySetResult();
            blockReservedApply = false;
            await WaitUntilAsync(
                () => appliedFrameIds.Contains(73),
                TimeSpan.FromSeconds(2));
            vm.OnOwnedEncodedFrame("h264", new byte[] { 74 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 30, frameId: 74, recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.ProtectedFollower);
            vm.OnOwnedEncodedFrame("h264", new byte[] { 75 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 30, frameId: 75, recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.ProtectedFollower);
            await WaitUntilAsync(
                () =>
                {
                    var appliedSnapshot = appliedFrameIds.ToArray();
                    return appliedSnapshot.Any(frameId => frameId == 74 || frameId == 75);
                },
                TimeSpan.FromSeconds(2));
            vm.OnOwnedEncodedFrame("h264", new byte[] { 76 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 30, frameId: 76);
            await WaitUntilAsync(() => vm.IsIdleForDiagnostics, TimeSpan.FromSeconds(2));

            var metrics = vm.GetMetricsSnapshot();
            var appliedSnapshot = appliedFrameIds.ToArray();
            Assert.True(Array.IndexOf(appliedSnapshot, 73) >= 0);
            Assert.True(appliedSnapshot.Any(frameId => frameId == 74 || frameId == 75));
            Assert.True(Array.FindIndex(appliedSnapshot, frameId => frameId == 74 || frameId == 75) > Array.IndexOf(appliedSnapshot, 73));
            Assert.True(Assert.IsAssignableFrom<Bitmap>(vm.CurrentFrame).PixelSize.Width >= 74);
            Assert.Equal(0, metrics.DecodedBlockedByReservedRecoveryFrameCount);
            Assert.Equal(0, metrics.BlockedByReservedRecoveryFrameRejectCount);
            Assert.Equal(0, metrics.RecoveryFollowerWindowBufferedCount);
            Assert.Equal(0, metrics.RecoveryFollowerWindowAppliedCount);
            Assert.Equal(0, metrics.RecoveryFollowerWindowTrimmedCount);
            Assert.Equal(0, metrics.StartupCorridorReleaseCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorSuccessCount);
            Assert.Equal(0, metrics.ProtectedRecoveryDeliveryCount);
            Assert.True(metrics.AverageDecodeCompleteToVisibleApplyMs > 0);
            Assert.True(metrics.LastReservedApplyHoldMs > 0);
            Assert.True(metrics.AverageVisibleHeadLagFrames >= 0);
            Assert.True(metrics.AverageStableHeadLagFrames >= 0);
            Assert.NotEqual("no_visible_baseline", metrics.HelperSessionPhase);
            Assert.False(string.IsNullOrWhiteSpace(metrics.HelperRecoveryMechanism));
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_MetricsSerializeAuthoritativeSessionSnapshot()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                h264Decoder: new FakeH264BitmapDecoder(),
                logRole: "helper_remote");

            var config = new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper-sequential",
                StreamEpoch = 17,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            vm.OnOwnedEncodedFrame(
                "h264",
                new byte[] { 30 },
                capturedTsUtcMs: 0,
                isKeyFrame: true,
                streamEpoch: 17,
                streamConfig: config,
                frameId: 30,
                sessionId: "helper-sequential");
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap first && first.PixelSize.Width == 30 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame(
                "h264",
                new byte[] { 31 },
                capturedTsUtcMs: 0,
                isKeyFrame: false,
                streamEpoch: 17,
                frameId: 31,
                sessionId: "helper-sequential");
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap second && second.PixelSize.Width == 31 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame(
                "h264",
                new byte[] { 32 },
                capturedTsUtcMs: 0,
                isKeyFrame: false,
                streamEpoch: 17,
                frameId: 32,
                sessionId: "helper-sequential");

            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap current && current.PixelSize.Width == 32 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            var helperSessionSnapshot = vm.GetHelperRemoteSessionSnapshot();
            var metrics = vm.GetMetricsSnapshot();

            Assert.Equal(ScreenShareConceptualModelFormatter.FormatHelperSessionPhase(helperSessionSnapshot.Phase), metrics.HelperSessionPhase);
            Assert.Equal(ScreenShareConceptualModelFormatter.FormatHelperRecoveryMechanism(helperSessionSnapshot.RecoveryMechanism), metrics.HelperRecoveryMechanism);
            Assert.Equal(helperSessionSnapshot.BaselineEstablished, metrics.BaselineEstablished);
            Assert.Equal(helperSessionSnapshot.SteadyVisibleProgressActive, metrics.SteadyVisibleProgressActive);
            Assert.Equal(helperSessionSnapshot.VisibleHeadFrameId, metrics.VisibleHeadFrameId);
            Assert.Equal(helperSessionSnapshot.StableVisibleHeadFrameId, metrics.StableVisibleHeadFrameId);
            Assert.Equal(helperSessionSnapshot.AppliedHeadFrameId, metrics.AppliedHeadFrameId);
            Assert.Equal(helperSessionSnapshot.VisibleRecoveryFloorFrameId, metrics.VisibleRecoveryFloorFrameId);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_PostRecoveryStaleFrames_DoNotBypassStaleDrop()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                h264Decoder: new FakeH264BitmapDecoder(),
                logRole: "helper_remote");
            var staleDropCount = 0;
            vm.StaleFrameDropped += (_, _) => staleDropCount++;

            var config = new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper",
                StreamEpoch = 31,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            vm.OnOwnedEncodedFrame("h264", new byte[] { 80 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), isKeyFrame: true, streamEpoch: 31, streamConfig: config, frameId: 80);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap first && first.PixelSize.Width == 80 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(5));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 82 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 31, frameId: 82);
            await WaitUntilAsync(() => vm.IsIdleForDiagnostics, TimeSpan.FromSeconds(5));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 83 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), isKeyFrame: true, streamEpoch: 31, frameId: 83, recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.RecoveryOwner);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap recovered && recovered.PixelSize.Width == 83 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(5));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 84 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 5000, isKeyFrame: false, streamEpoch: 31, frameId: 84, recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.ProtectedFollower);
            await WaitUntilAsync(
                () => vm.IsIdleForDiagnostics && staleDropCount >= 1,
                TimeSpan.FromSeconds(5));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 85 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 5000, isKeyFrame: false, streamEpoch: 31, frameId: 85, recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.ProtectedFollower);
            await WaitUntilAsync(
                () => vm.IsIdleForDiagnostics && staleDropCount >= 2,
                TimeSpan.FromSeconds(5));

            var current = Assert.IsAssignableFrom<Bitmap>(vm.CurrentFrame);
            Assert.Equal(83, current.PixelSize.Width);
            var metrics = vm.GetMetricsSnapshot();
            Assert.True(staleDropCount >= 2);
            Assert.Equal(0, metrics.DecodeAgeBudgetCount);
            Assert.Equal(0, metrics.RecoveryFollowerWindowBufferedCount);
            Assert.Equal(0, metrics.StartupCorridorReleaseCount);
            Assert.True(metrics.PostRecoveryVisibleGenerationResetCount >= 1);
            Assert.Equal(0, metrics.ProtectedRecoveryDeliveryCount);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_StableVisibleProgress_BypassesDecodeAgeBudget()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var decodeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDecode = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: null,
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                h264Decoder: new FrameBlockingH264BitmapDecoder(
                    blockedFrameId: 12,
                    decodeStarted,
                    releaseDecode),
                logRole: "helper_remote");

            var config = new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper",
                StreamEpoch = 41,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            var nowUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            vm.OnOwnedEncodedFrame("h264", new byte[] { 10 }, capturedTsUtcMs: nowUtcMs, isKeyFrame: true, streamEpoch: 41, streamConfig: config, frameId: 10);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap keyframe && keyframe.PixelSize.Width == 10 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(5));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 11 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), isKeyFrame: false, streamEpoch: 41, frameId: 11);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap follower && follower.PixelSize.Width == 11 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(5));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 12 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), isKeyFrame: false, streamEpoch: 41, frameId: 12);
            await decodeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var staleCapturedTsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 5000;
            vm.OnOwnedEncodedFrame("h264", new byte[] { 13 }, capturedTsUtcMs: staleCapturedTsUtcMs, isKeyFrame: false, streamEpoch: 41, frameId: 13);
            vm.OnOwnedEncodedFrame("h264", new byte[] { 14 }, capturedTsUtcMs: staleCapturedTsUtcMs, isKeyFrame: false, streamEpoch: 41, frameId: 14);

            releaseDecode.TrySetResult(true);

            await WaitUntilAsync(
                () => vm.IsIdleForDiagnostics &&
                      vm.GetMetricsSnapshot().OrdinaryNonKeyAgeBudgetBypassCount >= 1 &&
                      vm.CurrentFrame is Bitmap latest && latest.PixelSize.Width >= 12,
                TimeSpan.FromSeconds(5));

            var metrics = vm.GetMetricsSnapshot();
            Assert.Equal(0, metrics.DecodeAgeBudgetCount);
            Assert.Equal(0, metrics.FramesDroppedBeforeDecode);
            Assert.True(metrics.OrdinaryNonKeyAgeBudgetBypassCount >= 1);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_VisibleStableOrdinaryStaleFrame_DoesNotUseVisibleStableFreshnessDrop()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                h264Decoder: new FakeH264BitmapDecoder(),
                logRole: "helper_remote");
            var staleDropCount = 0;
            vm.StaleFrameDropped += (_, _) => staleDropCount++;

            var config = new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper",
                StreamEpoch = 51,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            vm.OnOwnedEncodedFrame("h264", new byte[] { 50 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), isKeyFrame: true, streamEpoch: 51, streamConfig: config, frameId: 50);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap first && first.PixelSize.Width == 50 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(5));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 51 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), isKeyFrame: false, streamEpoch: 51, frameId: 51);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap follower && follower.PixelSize.Width == 51 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(5));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 52 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 900, isKeyFrame: false, streamEpoch: 51, frameId: 52);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap stale && stale.PixelSize.Width == 52 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(5));

            var current = Assert.IsAssignableFrom<Bitmap>(vm.CurrentFrame);
            Assert.Equal(52, current.PixelSize.Width);

            var metrics = vm.GetMetricsSnapshot();
            Assert.Equal(0, metrics.StaleFrameDropVisibleStableCount);
            Assert.Equal(-1, metrics.StaleFrameDropVisibleStableLastAgeMs);
            Assert.True(metrics.OrdinaryNonKeyAgeBudgetBypassCount >= 1);

            var frameLossSnapshot = vm.GetFrameLossSnapshotForDiagnostics();
            Assert.DoesNotContain(
                frameLossSnapshot.RecentLosses,
                static loss => loss.FrameId == 52 && string.Equals(loss.Reason, "stale_frame_drop_visible_stable", StringComparison.Ordinal));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 53 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 200, isKeyFrame: false, streamEpoch: 51, frameId: 53);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap latest && latest.PixelSize.Width == 53 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(5));

            return true;
        }, default);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Yield();
        }

        Assert.True(predicate(), $"Condition not met within {timeout.TotalSeconds:N1}s.");
    }

    private static Bitmap CreateTinyBitmap()
    {
        using var stream = new MemoryStream(CreateTinyPngBytes(), writable: false);
        return new Bitmap(stream);
    }

    private static Bitmap CreateBitmap(int width, int height)
    {
        var writeable = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using (var locked = writeable.Lock())
        {
            var totalBytes = width * height * 4;
            var pixels = new byte[totalBytes];
            Marshal.Copy(pixels, 0, locked.Address, totalBytes);
        }

        return writeable;
    }

    private static byte[] CreateTinyPngBytes()
    {
        return Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/a5kAAAAASUVORK5CYII=");
    }

    private static byte[] CreateTinyJpegBytes()
    {
        return Convert.FromBase64String("/9j/4AAQSkZJRgABAQAAAQABAAD/2wCEAAkGBxAQEBUQEBAVFRUVFRUVFRUVFRUVFRUVFRUWFhUVFRUYHSggGBolHRUVITEhJSkrLi4uFx8zODMsNygtLisBCgoKDg0OGxAQGi0fHyUtLS0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tLf/AABEIAAEAAQMBIgACEQEDEQH/xAAXAAEBAQEAAAAAAAAAAAAAAAAAAQID/8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAwDAQACEAMQAAAB6AAAAP/EABQQAQAAAAAAAAAAAAAAAAAAACD/2gAIAQEAAT8Af//EABQRAQAAAAAAAAAAAAAAAAAAACD/2gAIAQIBAT8Af//EABQRAQAAAAAAAAAAAAAAAAAAACD/2gAIAQMBAT8Af//Z");
    }

    private static ScreenShareVideoFragmentV1 CreatePartialFragment(string sessionId, long streamEpoch, long frameId, int fragmentIndex, bool? isKeyFrame = null)
    {
        return new ScreenShareVideoFragmentV1
        {
            Type = ScreenShareVideoPayloadCodec.ScreenShareVideoFragmentTypeV1,
            SessionId = sessionId,
            StreamEpoch = streamEpoch,
            FrameId = frameId,
            Width = 640,
            Height = 360,
            CapturedTsUtcMs = (streamEpoch * 1000) + frameId,
            Encoding = "h264",
            IsKeyFrame = isKeyFrame ?? frameId == 10,
            FragmentIndex = fragmentIndex,
            FragmentCount = 2,
            Data = new byte[] { (byte)frameId, (byte)fragmentIndex },
        };
    }

    private static void CompleteFrame(
        ScreenShareVideoFrameReassembler reassembler,
        string sessionId,
        long streamEpoch,
        long frameId,
        bool? isKeyFrame = null)
    {
        reassembler.OnFragment(CreateFragment(sessionId, streamEpoch, frameId, fragmentIndex: 0, isKeyFrame));
        reassembler.OnFragment(CreateFragment(sessionId, streamEpoch, frameId, fragmentIndex: 1, isKeyFrame));
    }

    private static ScreenShareVideoFragmentV1 CreateFragment(
        string sessionId,
        long streamEpoch,
        long frameId,
        int fragmentIndex,
        bool? isKeyFrame = null)
    {
        return new ScreenShareVideoFragmentV1
        {
            Type = ScreenShareVideoPayloadCodec.ScreenShareVideoFragmentTypeV1,
            SessionId = sessionId,
            StreamEpoch = streamEpoch,
            FrameId = frameId,
            Width = 640,
            Height = 360,
            CapturedTsUtcMs = (streamEpoch * 1000) + frameId,
            Encoding = "h264",
            IsKeyFrame = isKeyFrame ?? frameId == 10,
            FragmentIndex = fragmentIndex,
            FragmentCount = 2,
            Data = new byte[] { (byte)frameId, (byte)fragmentIndex },
        };
    }

    private sealed class FakeH264BitmapDecoder : IWindowsH264BitmapDecoder
    {
        public bool IsSupported => true;

        public int ConfigureCallCount { get; private set; }

        public int DecodeCallCount { get; private set; }

        public long LastConfiguredEpoch { get; private set; }

        public void ConfigureStream(ScreenShareVideoStreamConfigV1 config)
        {
            ConfigureCallCount++;
            LastConfiguredEpoch = config.StreamEpoch;
        }

        public void Reset()
        {
        }

        public Bitmap Decode(EncodedFrameDecodeRequest request)
        {
            DecodeCallCount++;
            return CreateBitmap(request.EncodedFrameBytes.Span[0], 1);
        }

        public void Dispose()
        {
        }
    }

    private sealed class NeedMoreInputH264BitmapDecoder : IWindowsH264BitmapDecoder
    {
        public bool IsSupported => true;

        public void ConfigureStream(ScreenShareVideoStreamConfigV1 config)
        {
        }

        public void Reset()
        {
        }

        public Bitmap Decode(EncodedFrameDecodeRequest request)
        {
            throw new H264DecoderNeedsMoreInputException("more input required");
        }

        public void Dispose()
        {
        }
    }

    private sealed class BlockingH264BitmapDecoder : IWindowsH264BitmapDecoder
    {
        private readonly TaskCompletionSource<bool> decodeStarted;
        private readonly TaskCompletionSource<bool> releaseDecode;
        private int decodeCalls;

        public BlockingH264BitmapDecoder(
            TaskCompletionSource<bool> decodeStarted,
            TaskCompletionSource<bool> releaseDecode)
        {
            this.decodeStarted = decodeStarted;
            this.releaseDecode = releaseDecode;
        }

        public bool IsSupported => true;

        public void ConfigureStream(ScreenShareVideoStreamConfigV1 config)
        {
        }

        public void Reset()
        {
        }

        public Bitmap Decode(EncodedFrameDecodeRequest request)
        {
            if (Interlocked.Increment(ref decodeCalls) == 1)
            {
                decodeStarted.TrySetResult(true);
                Assert.True(releaseDecode.Task.Wait(TimeSpan.FromSeconds(2)));
            }

            return CreateBitmap(request.EncodedFrameBytes.Span[0], 1);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FrameBlockingH264BitmapDecoder : IWindowsH264BitmapDecoder
    {
        private readonly long blockedFrameId;
        private readonly TaskCompletionSource<bool> decodeStarted;
        private readonly TaskCompletionSource<bool> releaseDecode;

        public FrameBlockingH264BitmapDecoder(
            long blockedFrameId,
            TaskCompletionSource<bool> decodeStarted,
            TaskCompletionSource<bool> releaseDecode)
        {
            this.blockedFrameId = blockedFrameId;
            this.decodeStarted = decodeStarted;
            this.releaseDecode = releaseDecode;
        }

        public bool IsSupported => true;

        public void ConfigureStream(ScreenShareVideoStreamConfigV1 config)
        {
        }

        public void Reset()
        {
        }

        public Bitmap Decode(EncodedFrameDecodeRequest request)
        {
            if (request.FrameId == blockedFrameId)
            {
                decodeStarted.TrySetResult(true);
                Assert.True(releaseDecode.Task.Wait(TimeSpan.FromSeconds(2)));
            }

            return CreateBitmap(request.EncodedFrameBytes.Span[0], 1);
        }

        public void Dispose()
        {
        }
    }
}
