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
[Trait("Area", "ScreenShare")]
public sealed class ScreenShareViewerHelperRemoteRecoveryTests : ScreenShareViewerViewModelTestBase, IClassFixture<ScreenShareCoordinatorFixture>
{
    public ScreenShareViewerHelperRemoteRecoveryTests(ScreenShareCoordinatorFixture fixture) : base(fixture)
    {
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
            Assert.True(metrics.RecoveryFollowerWindowBufferedCount >= 1);
            Assert.Equal(0, metrics.StartupCorridorBufferedFollowerCount);
            Assert.Equal(0, metrics.StartupCorridorReleaseCount);
            Assert.Equal(0, metrics.StartupCorridorAbortCount);
            Assert.True(metrics.RecoveryProgressCorridorCount >= 1);
            Assert.True(metrics.RecoveryProgressCorridorAppliedCount >= 1);
            Assert.True(metrics.ProtectedRecoveryDeliveryCount >= 1);
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
            Assert.Equal(1, finalMetrics.RecoveryProgressCorridorCount);
            Assert.Equal(0, finalMetrics.RecoveryProgressCorridorAbortCount);
            Assert.Equal(1, finalMetrics.RecoveryProgressCorridorAppliedCount);
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

            vm.OnOwnedEncodedFrame("h264", new byte[] { 5 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 29, frameId: 4, recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.ProtectedFollower);
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
            Assert.Equal(1, metrics.RecoveryProgressCorridorCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorSuccessCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorAbortCount);
            Assert.True(metrics.RecoveryProgressCorridorAppliedCount >= 2);
            Assert.True(metrics.ProtectedRecoveryDeliveryCount >= 1);
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

            var recoveryNowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            vm.OnOwnedEncodedFrame("h264", new byte[] { 24 }, capturedTsUtcMs: recoveryNowMs, isKeyFrame: true, streamEpoch: 32, frameId: 4, recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.RecoveryOwner);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap recoveryOwner && recoveryOwner.PixelSize.Width == 24 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 25 }, capturedTsUtcMs: recoveryNowMs, isKeyFrame: false, streamEpoch: 32, frameId: 5, recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.ProtectedFollower);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap follower && follower.PixelSize.Width == 25 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            var metrics = vm.GetMetricsSnapshot();
            Assert.Equal(0, metrics.BlockedByReservedRecoveryFrameRejectCount);
            Assert.Equal(0, metrics.DecodedBlockedByReservedRecoveryFrameCount);
            Assert.Equal(1, metrics.RecoveryProgressCorridorCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorSuccessCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorAbortCount);
            Assert.True(metrics.RecoveryProgressCorridorAppliedCount >= 2);
            Assert.Equal(0, metrics.RecoveryFollowerWindowBufferedCount);
            Assert.Equal(0, metrics.StartupCorridorBufferedFollowerCount);
            Assert.Equal(0, metrics.StartupCorridorReleaseCount);
            Assert.True(metrics.FramesDroppedWaitingForRecoveryKeyframe >= 1);
            Assert.True(metrics.ProtectedRecoveryDeliveryCount >= 1);
            return true;
        }, default);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_SimplifiedRecoveryOwner_StartsProtectedFollowerWindow()
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
            Assert.Equal(1, metrics.RecoveryProgressCorridorCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorSuccessCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorAbortCount);
            Assert.Equal(1, metrics.RecoveryProgressCorridorAppliedCount);
            Assert.Equal(0, metrics.RecoveryFollowerWindowBufferedCount);
            Assert.Equal(0, metrics.StartupCorridorBufferedFollowerCount);
            Assert.Equal(0, metrics.ProtectedRecoveryDeliveryCount);
            Assert.True(metrics.FramesDroppedWaitingForRecoveryKeyframe >= 1);
            return true;
        }, default);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_ProtectedFollowerCompatibility_AppliesInsideRecoveryWindow()
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
                () => vm.CurrentFrame is Bitmap recoveryOwner && recoveryOwner.PixelSize.Width == 33 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 34 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 133, frameId: 4, recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.ProtectedFollower);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap follower && follower.PixelSize.Width == 34 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            var metrics = vm.GetMetricsSnapshot();
            Assert.Equal(1, metrics.RecoveryProgressCorridorCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorSuccessCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorAbortCount);
            Assert.True(metrics.RecoveryProgressCorridorAppliedCount >= 1);
            Assert.Equal(0, metrics.StartupCorridorReleaseCount);
            Assert.Equal(0, metrics.RecoveryFollowerWindowBufferedCount);
            Assert.True(metrics.ProtectedRecoveryDeliveryCount >= 1);
            return true;
        }, default);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_ProtectedFollowerTags_ApplyInsideRecoveryWindow()
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
            Assert.Equal(1, metrics.RecoveryProgressCorridorCount);
            Assert.Equal(1, metrics.RecoveryProgressCorridorSuccessCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorAbortCount);
            Assert.True(metrics.RecoveryProgressCorridorAppliedCount >= 3);
            Assert.Equal(0, metrics.StartupCorridorReleaseCount);
            Assert.Equal(0, metrics.RecoveryFollowerWindowBufferedCount);
            Assert.True(metrics.RecoveryFollowerWindowAppliedCount >= 2);
            Assert.True(metrics.ProtectedRecoveryDeliveryCount >= 2);
            return true;
        }, default);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_PostRecoveryFramesResumeAfterProtectedWindow()
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
                () => vm.CurrentFrame is Bitmap firstFollower && firstFollower.PixelSize.Width == 34 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 35 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 300, frameId: 35, recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.ProtectedFollower);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap secondFollower &&
                      secondFollower.PixelSize.Width == 35 &&
                      vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            Assert.True(vm.GetMetricsSnapshot().H264ReferenceQuarantineActive);
            await Task.Delay(350);
            vm.OnOwnedEncodedFrame("h264", new byte[] { 36 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 300, frameId: 36);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap latest &&
                      latest.PixelSize.Width == 36 &&
                      !vm.GetMetricsSnapshot().H264ReferenceTaintActive,
                TimeSpan.FromSeconds(2));

            var metrics = vm.GetMetricsSnapshot();
            Assert.Equal(1, metrics.RecoveryProgressCorridorCount);
            Assert.Equal(1, metrics.RecoveryProgressCorridorSuccessCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorAbortCount);
            Assert.True(metrics.RecoveryProgressCorridorAppliedCount >= 3);
            Assert.True(metrics.RecoveryFollowerWindowAppliedCount >= 2);
            Assert.True(metrics.ProtectedRecoveryDeliveryCount >= 2);
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
    public async Task ScreenShareViewer_HelperRemote_NonContiguousFollowerAfterRecovery_IsBufferedWithoutBecomingVisible()
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
                      vm.GetMetricsSnapshot().RecoveryFollowerWindowBufferedCount >= metricsBeforeGap.RecoveryFollowerWindowBufferedCount + 1,
                TimeSpan.FromSeconds(2));

            var current = Assert.IsAssignableFrom<Bitmap>(vm.CurrentFrame);
            Assert.Equal(23, current.PixelSize.Width);
            var metrics = vm.GetMetricsSnapshot();
            Assert.Equal(metricsBeforeGap.FrameGapContinuityLossCount, metrics.FrameGapContinuityLossCount);
            Assert.Equal(metricsBeforeGap.ContinuityLossCount, metrics.ContinuityLossCount);
            Assert.Equal(metricsBeforeGap.FramesDroppedWaitingForRecoveryKeyframe, metrics.FramesDroppedWaitingForRecoveryKeyframe);
            Assert.Equal(1, metrics.RecoveryProgressCorridorCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorSuccessCount);
            Assert.InRange(metrics.RecoveryProgressCorridorAbortCount, 0, 1);
            Assert.Equal(1, metrics.RecoveryProgressCorridorAppliedCount);
            Assert.True(metrics.RecoveryFollowerWindowBufferedCount >= metricsBeforeGap.RecoveryFollowerWindowBufferedCount + 1);
            Assert.Equal(0, metrics.StartupCorridorAbortCount);
            Assert.Equal("none", metrics.StartupCorridorAbortReason);
            return true;
        }, default);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_OutOfOrderPostRecoveryFollowers_ApplyWhenGapFills()
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
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            vm.OnOwnedEncodedFrame("h264", new byte[] { 25 }, capturedTsUtcMs: nowMs, isKeyFrame: false, streamEpoch: 310, frameId: 25);
            await WaitUntilAsync(
                () => vm.IsIdleForDiagnostics &&
                      vm.GetMetricsSnapshot().RecoveryFollowerWindowBufferedCount >= metricsBeforeGap.RecoveryFollowerWindowBufferedCount + 1,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 24 }, capturedTsUtcMs: nowMs, isKeyFrame: false, streamEpoch: 310, frameId: 24);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap recovered &&
                      recovered.PixelSize.Width == 25 &&
                      vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            var current = Assert.IsAssignableFrom<Bitmap>(vm.CurrentFrame);
            Assert.Equal(25, current.PixelSize.Width);

            var metrics = vm.GetMetricsSnapshot();
            Assert.Equal(1, metrics.RecoveryProgressCorridorCount);
            Assert.Equal(1, metrics.RecoveryProgressCorridorSuccessCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorAbortCount);
            Assert.Equal(0, metrics.StartupCorridorAbortCount);
            Assert.True(metrics.RecoveryFollowerWindowBufferedCount >= metricsBeforeGap.RecoveryFollowerWindowBufferedCount + 1);
            Assert.Equal(0, metrics.StartupCorridorReleaseCount);
            Assert.Equal("visible_stable", metrics.HelperSessionPhase);
            Assert.Equal("none", metrics.HelperRecoveryMechanism);
            Assert.Equal(metricsBeforeGap.FramesDroppedWaitingForRecoveryKeyframe, metrics.FramesDroppedWaitingForRecoveryKeyframe);

            var snapshot = vm.GetFrameLossSnapshotForDiagnostics();
            Assert.Equal(0, snapshot.StaleRunwayWindowAbortCount);
            Assert.Equal(0, snapshot.LateSameEpochAfterHeadAdvancedDropCount);
            Assert.DoesNotContain(
                snapshot.RecentLosses,
                static loss => (loss.FrameId == 24 || loss.FrameId == 25) &&
                               string.Equals(loss.Reason, "waiting_for_recovery_keyframe", StringComparison.Ordinal));
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
            Assert.Equal(1, metrics.RecoveryProgressCorridorCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorSuccessCount);
            Assert.Equal(0, metrics.RecoveryProgressCorridorAbortCount);
            Assert.Equal(1, metrics.RecoveryProgressCorridorAppliedCount);
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

private async Task ScreenShareViewer_HelperRemote_SequentialPFrames_StayLiveWithoutRecovery()
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
    public async Task ScreenShareViewer_HelperRemote_PreCandidateGapTail_DoesNotBecomeVisible()
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
                SessionId = "helper-gap-tail",
                StreamEpoch = 18,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            vm.OnOwnedEncodedFrame("h264", new byte[] { 40 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 18, streamConfig: config, frameId: 40, sessionId: "helper-gap-tail");
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap first && first.PixelSize.Width == 40 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 42 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 18, frameId: 42, sessionId: "helper-gap-tail");
            await WaitUntilAsync(() => vm.IsIdleForDiagnostics, TimeSpan.FromSeconds(2));

            var current = Assert.IsAssignableFrom<Bitmap>(vm.CurrentFrame);
            Assert.Equal(40, current.PixelSize.Width);

            var metrics = vm.GetMetricsSnapshot();
            Assert.True(metrics.FrameGapContinuityLossCount >= 1);
            Assert.True(metrics.FramesDroppedWaitingForRecoveryKeyframe >= 1);
            Assert.Equal(0, metrics.PreCandidateGapTailEmittedToViewerCount);

            var snapshot = vm.GetFrameLossSnapshotForDiagnostics();
            Assert.True(snapshot.WaitingForRecoveryKeyframeRejectCount >= 1);
            Assert.Equal(1, snapshot.FramesApplied);
            return true;
        }, default);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_H264ReferenceTaintBlocksNormalNonKeyUntilTrustedCorridor()
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
                SessionId = "helper-reference-taint",
                StreamEpoch = 41,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            vm.OnOwnedEncodedFrame("h264", new byte[] { 10 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 41, streamConfig: config, frameId: 10, sessionId: "helper-reference-taint");
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap first && first.PixelSize.Width == 10 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 12 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 41, frameId: 12, sessionId: "helper-reference-taint");
            vm.OnOwnedEncodedFrame("h264", new byte[] { 13 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 41, frameId: 13, sessionId: "helper-reference-taint");
            await WaitUntilAsync(() => vm.IsIdleForDiagnostics, TimeSpan.FromSeconds(2));

            Assert.Equal(10, Assert.IsAssignableFrom<Bitmap>(vm.CurrentFrame).PixelSize.Width);
            var taintedMetrics = vm.GetMetricsSnapshot();
            Assert.True(taintedMetrics.H264ReferenceTaintActive);
            Assert.True(taintedMetrics.H264ReferenceTaintEnterCount >= 1);
            Assert.True(taintedMetrics.H264ReferenceTaintDroppedNonKeyCount >= 1);
            Assert.True(taintedMetrics.FramesDroppedWaitingForRecoveryKeyframe >= 1);

            vm.OnOwnedEncodedFrame("h264", new byte[] { 14 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 41, frameId: 14, sessionId: "helper-reference-taint");
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap recoveryOwner && recoveryOwner.PixelSize.Width == 14 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 15 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 41, frameId: 15, sessionId: "helper-reference-taint");
            vm.OnOwnedEncodedFrame("h264", new byte[] { 16 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 41, frameId: 16, sessionId: "helper-reference-taint");
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap recovered && recovered.PixelSize.Width == 16 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            var quarantinedMetrics = vm.GetMetricsSnapshot();
            Assert.True(quarantinedMetrics.H264ReferenceTaintActive);
            Assert.True(quarantinedMetrics.H264ReferenceQuarantineReleaseBlockedCount >= 1);
            Assert.Equal(16, Assert.IsAssignableFrom<Bitmap>(vm.CurrentFrame).PixelSize.Width);

            await Task.Delay(350);
            vm.OnOwnedEncodedFrame("h264", new byte[] { 17 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), isKeyFrame: false, streamEpoch: 41, frameId: 17, sessionId: "helper-reference-taint");
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap afterQuiet &&
                      afterQuiet.PixelSize.Width == 17 &&
                      !vm.GetMetricsSnapshot().H264ReferenceTaintActive &&
                      vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            var recoveredMetrics = vm.GetMetricsSnapshot();
            Assert.True(recoveredMetrics.H264ReferenceTaintReleaseCount >= 1);
            Assert.True(recoveredMetrics.H264ReferenceQuarantineQuietReleaseCount >= 1);
            Assert.True(recoveredMetrics.H264ReferenceTaintDecoderResetCount >= 1);
            Assert.True(recoveredMetrics.RecoveryProgressCorridorSuccessCount >= 1);
            Assert.True(recoveredMetrics.ProtectedRecoveryDeliveryCount >= 2);
            Assert.Equal("visible_stable", recoveredMetrics.HelperSessionPhase);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_PostQuarantineSettleSuppressesOnlyStaleOrdinaryFrames()
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
                SessionId = "helper-post-quarantine-settle",
                StreamEpoch = 43,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            vm.OnOwnedEncodedFrame("h264", new byte[] { 20 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), isKeyFrame: true, streamEpoch: 43, streamConfig: config, frameId: 20, sessionId: "helper-post-quarantine-settle");
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap first && first.PixelSize.Width == 20 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 22 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), isKeyFrame: false, streamEpoch: 43, frameId: 22, sessionId: "helper-post-quarantine-settle");
            await WaitUntilAsync(
                () => vm.GetMetricsSnapshot().H264ReferenceTaintActive && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 23 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), isKeyFrame: true, streamEpoch: 43, frameId: 23, sessionId: "helper-post-quarantine-settle");
            vm.OnOwnedEncodedFrame("h264", new byte[] { 24 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), isKeyFrame: false, streamEpoch: 43, frameId: 24, sessionId: "helper-post-quarantine-settle");
            vm.OnOwnedEncodedFrame("h264", new byte[] { 25 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), isKeyFrame: false, streamEpoch: 43, frameId: 25, sessionId: "helper-post-quarantine-settle");
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap recovered && recovered.PixelSize.Width == 25 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            await Task.Delay(350);
            vm.OnOwnedEncodedFrame("h264", new byte[] { 26 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 900, isKeyFrame: false, streamEpoch: 43, frameId: 26, sessionId: "helper-post-quarantine-settle");
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap held &&
                      held.PixelSize.Width == 25 &&
                      !vm.GetMetricsSnapshot().H264ReferenceTaintActive &&
                      vm.GetMetricsSnapshot().PostQuarantineSettleSuppressCount >= 1 &&
                      vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 27 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), isKeyFrame: false, streamEpoch: 43, frameId: 27, sessionId: "helper-post-quarantine-settle");
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap fresh &&
                      fresh.PixelSize.Width == 27 &&
                      !vm.GetMetricsSnapshot().H264ReferenceTaintActive &&
                      vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            var metrics = vm.GetMetricsSnapshot();
            Assert.True(metrics.DecodedStaleVisibleSuppressCount >= 1);
            Assert.True(metrics.StaleNormalNonKeyVisibleSuppressCount >= 1);
            Assert.Equal(0, metrics.H264ReferenceTaintStaleVisibleStableEnterCount);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_NonHelperH264_DoesNotUseHelperReferenceTaint()
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
                logRole: "viewer");

            var config = new ScreenShareVideoStreamConfigV1
            {
                SessionId = "viewer",
                StreamEpoch = 42,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            vm.OnOwnedEncodedFrame("h264", new byte[] { 50 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 42, streamConfig: config, frameId: 50, sessionId: "viewer");
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap first && first.PixelSize.Width == 50 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 52 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 42, frameId: 52, sessionId: "viewer");
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap second && second.PixelSize.Width == 52 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            var metrics = vm.GetMetricsSnapshot();
            Assert.False(metrics.H264ReferenceTaintActive);
            Assert.Equal(0, metrics.H264ReferenceTaintEnterCount);
            Assert.Equal(0, metrics.H264ReferenceTaintDroppedNonKeyCount);
            return true;
        }, default);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_NeedMoreInputEntersReferenceTaint()
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
                h264Decoder: new NeedMoreInputAfterFirstFrameH264BitmapDecoder(),
                logRole: "helper_remote");

            var config = new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper-need-more-input-taint",
                StreamEpoch = 43,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            vm.OnOwnedEncodedFrame("h264", new byte[] { 10 }, capturedTsUtcMs: 0, isKeyFrame: true, streamEpoch: 43, streamConfig: config, frameId: 10, sessionId: "helper-need-more-input-taint");
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap first && first.PixelSize.Width == 10 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 11 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 43, frameId: 11, sessionId: "helper-need-more-input-taint");
            vm.OnOwnedEncodedFrame("h264", new byte[] { 12 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 43, frameId: 12, sessionId: "helper-need-more-input-taint");
            await WaitUntilAsync(
                () =>
                {
                    var metrics = vm.GetMetricsSnapshot();
                    return metrics.NeedMoreInputCount >= 2 &&
                           metrics.H264ReferenceTaintActive &&
                           vm.IsIdleForDiagnostics;
                },
                TimeSpan.FromSeconds(2));

            var metrics = vm.GetMetricsSnapshot();
            Assert.True(metrics.H264ReferenceTaintEnterCount >= 1);
            Assert.Equal("need_more_input_burst", metrics.H264ReferenceTaintLastReason);
            return true;
        }, default);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_RecoveryOwnerThenLaterFramesApplyInOrderThroughProtectedWindow()
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
                        await reservedApplyReleased.Task;
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
                    var metrics = vm.GetMetricsSnapshot();
                    return metrics.RecoveryProgressCorridorSuccessCount >= 1 &&
                           vm.CurrentFrame is Bitmap recovered &&
                           recovered.PixelSize.Width == 75;
                },
                TimeSpan.FromSeconds(2));
            vm.OnOwnedEncodedFrame("h264", new byte[] { 76 }, capturedTsUtcMs: 0, isKeyFrame: false, streamEpoch: 30, frameId: 76);
            await WaitUntilAsync(() => vm.IsIdleForDiagnostics, TimeSpan.FromSeconds(2));
            Assert.True(vm.GetMetricsSnapshot().H264ReferenceQuarantineActive);

            await Task.Delay(350);
            vm.OnOwnedEncodedFrame("h264", new byte[] { 76 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), isKeyFrame: false, streamEpoch: 30, frameId: 76);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap afterQuiet &&
                      afterQuiet.PixelSize.Width == 76 &&
                      !vm.GetMetricsSnapshot().H264ReferenceTaintActive &&
                      vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            var metrics = vm.GetMetricsSnapshot();
            var appliedSnapshot = appliedFrameIds.ToArray();
            Assert.True(Array.IndexOf(appliedSnapshot, 73) >= 0);
            Assert.Contains(appliedSnapshot, frameId => frameId == 74 || frameId == 75);
            Assert.True(Array.FindIndex(appliedSnapshot, frameId => frameId == 74 || frameId == 75) > Array.IndexOf(appliedSnapshot, 73));
            Assert.True(Assert.IsAssignableFrom<Bitmap>(vm.CurrentFrame).PixelSize.Width >= 76);
            Assert.Equal(0, metrics.DecodedBlockedByReservedRecoveryFrameCount);
            Assert.Equal(0, metrics.BlockedByReservedRecoveryFrameRejectCount);
            Assert.True(metrics.RecoveryFollowerWindowBufferedCount >= 1);
            Assert.True(metrics.RecoveryFollowerWindowAppliedCount >= 1);
            Assert.Equal(0, metrics.RecoveryFollowerWindowTrimmedCount);
            Assert.Equal(0, metrics.StartupCorridorReleaseCount);
            Assert.Equal(1, metrics.RecoveryProgressCorridorCount);
            Assert.Equal(1, metrics.RecoveryProgressCorridorSuccessCount);
            Assert.True(metrics.ProtectedRecoveryDeliveryCount >= 1);
            Assert.True(metrics.H264ReferenceQuarantineQuietReleaseCount >= 1);
            Assert.True(metrics.AverageDecodeCompleteToVisibleApplyMs > 0);
            Assert.True(metrics.AverageVisibleHeadLagFrames >= 0);
            Assert.True(metrics.AverageStableHeadLagFrames >= 0);
            Assert.Equal("visible_stable", metrics.HelperSessionPhase);
            Assert.Equal("none", metrics.HelperRecoveryMechanism);
            return true;
        }, default);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_VisibleStableStaleNormalFrames_AreDropped()
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

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            vm.OnOwnedEncodedFrame("h264", new byte[] { 80 }, capturedTsUtcMs: nowMs, isKeyFrame: true, streamEpoch: 31, streamConfig: config, frameId: 80);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap first && first.PixelSize.Width == 80 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(5));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 81 }, capturedTsUtcMs: nowMs, isKeyFrame: false, streamEpoch: 31, frameId: 81);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap second && second.PixelSize.Width == 81 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(5));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 82 }, capturedTsUtcMs: nowMs, isKeyFrame: false, streamEpoch: 31, frameId: 82);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap third && third.PixelSize.Width == 82 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(5));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 83 }, capturedTsUtcMs: nowMs - 5000, isKeyFrame: false, streamEpoch: 31, frameId: 83);
            await WaitUntilAsync(
                () => vm.IsIdleForDiagnostics && staleDropCount >= 1,
                TimeSpan.FromSeconds(5));

            var current = Assert.IsAssignableFrom<Bitmap>(vm.CurrentFrame);
            Assert.Equal(82, current.PixelSize.Width);
            var metrics = vm.GetMetricsSnapshot();
            Assert.True(staleDropCount >= 1);
            Assert.Equal(0, metrics.DecodeAgeBudgetCount);
            Assert.True(metrics.StaleFrameDropVisibleStableCount >= 1);
            Assert.True(metrics.StaleFrameDropVisibleStableLastAgeMs >= 300);
            Assert.Equal(0, metrics.RecoveryFollowerWindowBufferedCount);
            Assert.Equal(0, metrics.StartupCorridorReleaseCount);
            Assert.Equal(0, metrics.ProtectedRecoveryDeliveryCount);
            return true;
        }, default);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_VisibleStableAssemblyEvictionEntersReferenceTaint()
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
                SessionId = "helper-visible-stable-taint",
                StreamEpoch = 44,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            vm.OnOwnedEncodedFrame("h264", new byte[] { 30 }, capturedTsUtcMs: nowMs, isKeyFrame: true, streamEpoch: 44, streamConfig: config, frameId: 30, sessionId: "helper-visible-stable-taint");
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap first && first.PixelSize.Width == 30 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 31 }, capturedTsUtcMs: nowMs, isKeyFrame: false, streamEpoch: 44, frameId: 31, sessionId: "helper-visible-stable-taint");
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap second && second.PixelSize.Width == 31 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            ScreenShareFrameLossAttributionRegistry.ObserveAssemblyEvicted(
                "helper-visible-stable-taint",
                streamEpoch: 44,
                frameId: 32,
                reason: "assembly_incomplete",
                isKeyFrame: false);
            vm.OnOwnedEncodedFrame("h264", new byte[] { 32 }, capturedTsUtcMs: nowMs, isKeyFrame: false, streamEpoch: 44, assembliesExpired: 1, frameId: 32, sessionId: "helper-visible-stable-taint");

            await WaitUntilAsync(
                () =>
                {
                    var metrics = vm.GetMetricsSnapshot();
                    return metrics.H264ReferenceTaintActive &&
                           metrics.RecoveryKeyframesRequested >= 1 &&
                           vm.IsIdleForDiagnostics;
                },
                TimeSpan.FromSeconds(2));

            Assert.Equal(31, Assert.IsAssignableFrom<Bitmap>(vm.CurrentFrame).PixelSize.Width);
            var taintedMetrics = vm.GetMetricsSnapshot();
            Assert.Equal("assembly_incomplete", taintedMetrics.H264ReferenceTaintLastReason);
            Assert.True(taintedMetrics.FramesDroppedWaitingForRecoveryKeyframe >= 1);
            return true;
        }, default);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_DecodeDropBeforeDecodeEntersReferenceTaint()
    {
        await fixture.Session.Dispatch(async () =>
        {
            ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
            var decodeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDecode = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                h264Decoder: new FrameBlockingH264BitmapDecoder(41, decodeStarted, releaseDecode),
                logRole: "helper_remote");

            var config = new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper-decode-drop-taint",
                StreamEpoch = 45,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            vm.OnOwnedEncodedFrame("h264", new byte[] { 40 }, capturedTsUtcMs: nowMs, isKeyFrame: true, streamEpoch: 45, streamConfig: config, frameId: 40, sessionId: "helper-decode-drop-taint");
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap first && first.PixelSize.Width == 40 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 41 }, capturedTsUtcMs: nowMs, isKeyFrame: false, streamEpoch: 45, frameId: 41, sessionId: "helper-decode-drop-taint");
            await decodeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            for (var frameId = 42; frameId <= 48; frameId++)
            {
                vm.OnOwnedEncodedFrame("h264", new byte[] { (byte)frameId }, capturedTsUtcMs: nowMs, isKeyFrame: false, streamEpoch: 45, frameId: frameId, sessionId: "helper-decode-drop-taint");
            }

            await WaitUntilAsync(
                () =>
                {
                    var metrics = vm.GetMetricsSnapshot();
                    return metrics.H264ReferenceTaintActive &&
                           metrics.RecoveryKeyframesRequested >= 1 &&
                           metrics.DecodeWorkerDroppedBeforeDecodeCount >= 1;
                },
                TimeSpan.FromSeconds(2));

            releaseDecode.TrySetResult(true);
            await WaitUntilAsync(() => vm.IsIdleForDiagnostics, TimeSpan.FromSeconds(2));

            Assert.Equal(40, Assert.IsAssignableFrom<Bitmap>(vm.CurrentFrame).PixelSize.Width);
            var metrics = vm.GetMetricsSnapshot();
            Assert.True(metrics.DecodeQueueOverflowCount >= 1 || metrics.DecodeAgeBudgetCount >= 1);
            Assert.True(metrics.H264ReferenceTaintDroppedNonKeyCount >= 1);
            return true;
        }, default);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_UnresolvedFutureTailKeepsReferenceTaintAfterCorridor()
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
                SessionId = "helper-future-tail-taint",
                StreamEpoch = 46,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            vm.OnOwnedEncodedFrame("h264", new byte[] { 10 }, capturedTsUtcMs: nowMs, isKeyFrame: true, streamEpoch: 46, streamConfig: config, frameId: 10, sessionId: "helper-future-tail-taint");
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap first && first.PixelSize.Width == 10 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 12 }, capturedTsUtcMs: nowMs, isKeyFrame: false, streamEpoch: 46, frameId: 12, sessionId: "helper-future-tail-taint");
            await WaitUntilAsync(
                () => vm.GetMetricsSnapshot().H264ReferenceTaintActive && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 14 }, capturedTsUtcMs: nowMs, isKeyFrame: true, streamEpoch: 46, frameId: 14, sessionId: "helper-future-tail-taint");
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap owner && owner.PixelSize.Width == 14 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 20 }, capturedTsUtcMs: nowMs, isKeyFrame: false, streamEpoch: 46, frameId: 20, sessionId: "helper-future-tail-taint");
            vm.OnOwnedEncodedFrame("h264", new byte[] { 15 }, capturedTsUtcMs: nowMs, isKeyFrame: false, streamEpoch: 46, frameId: 15, sessionId: "helper-future-tail-taint", recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.ProtectedFollower);
            vm.OnOwnedEncodedFrame("h264", new byte[] { 16 }, capturedTsUtcMs: nowMs, isKeyFrame: false, streamEpoch: 46, frameId: 16, sessionId: "helper-future-tail-taint", recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.ProtectedFollower);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap recovered && recovered.PixelSize.Width == 16 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            var metricsAfterCorridor = vm.GetMetricsSnapshot();
            Assert.True(metricsAfterCorridor.RecoveryProgressCorridorSuccessCount >= 1);
            Assert.True(metricsAfterCorridor.H264ReferenceTaintActive);
            Assert.Equal(0, metricsAfterCorridor.H264ReferenceTaintReleaseCount);

            vm.OnOwnedEncodedFrame("h264", new byte[] { 17 }, capturedTsUtcMs: nowMs, isKeyFrame: false, streamEpoch: 46, frameId: 17, sessionId: "helper-future-tail-taint");
            await WaitUntilAsync(() => vm.IsIdleForDiagnostics, TimeSpan.FromSeconds(2));

            Assert.Equal(16, Assert.IsAssignableFrom<Bitmap>(vm.CurrentFrame).PixelSize.Width);
            Assert.True(vm.GetMetricsSnapshot().H264ReferenceTaintDroppedNonKeyCount >= 1);
            return true;
        }, default);
    }

    private sealed class NeedMoreInputAfterFirstFrameH264BitmapDecoder : IWindowsH264BitmapDecoder
    {
        private int decodeCount;

        public bool IsSupported => true;

        public void ConfigureStream(ScreenShareVideoStreamConfigV1 config)
        {
        }

        public void Reset()
        {
        }

        public Bitmap Decode(EncodedFrameDecodeRequest request)
        {
            if (Interlocked.Increment(ref decodeCount) == 1)
            {
                return CreateBitmap(request.EncodedFrameBytes.Span[0], 1);
            }

            throw new H264DecoderNeedsMoreInputException("more input required");
        }

        public void Dispose()
        {
        }
    }

}
