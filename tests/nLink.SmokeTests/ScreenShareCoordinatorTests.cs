using System.Runtime.InteropServices;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NLink.App.Configuration;
using NLink.App.Services.ScreenCapture;
using NLink.App.Views;
using NLink.Core.Logging;
using NLink.Core.ScreenShare;
using NLink.Infra.Nkn;
using NLink.SmokeTests.Fakes;
using System.Collections.Concurrent;

namespace NLink.SmokeTests;

[Collection(AvaloniaHeadlessUiCollection.Name)]
public sealed class ScreenShareCoordinatorTests : IClassFixture<ScreenShareCoordinatorFixture>
{
    private const int TransportClarityFpsFloorForTesting = 5;
    private readonly ScreenShareCoordinatorFixture fixture;

    public ScreenShareCoordinatorTests(ScreenShareCoordinatorFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeeScreenShareCoordinator_SlowDecode_CoalescesToLatestFrame()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var fakeSource = new FakeScreenCaptureSource();
            var decodeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDecode = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var active = false;
            Bitmap? currentFrame = null;
            var decodeCount = 0;

            var coordinator = new HelpeeScreenShareCoordinator(
                isDisposed: static () => false,
                canShowScreenShareAction: static () => true,
                isPreviewActive: () => active,
                captureSourceFactory: new FixedCaptureSourceFactory(fakeSource),
                setPreviewActive: value => active = value,
                setStatus: _ => { },
                getPreviewFrame: () => currentFrame,
                setPreviewFrame: value => currentFrame = value,
                decodeFrame: bytes =>
                {
                    var call = Interlocked.Increment(ref decodeCount);
                    if (call == 1)
                    {
                        decodeStarted.TrySetResult(true);
                        WaitForSignal(releaseDecode.Task, TimeSpan.FromSeconds(2));
                    }

                    return CreateBitmap(bytes[0], 1);
                });

            try
            {
                coordinator.Toggle();
                await WaitUntilAsync(() => active, TimeSpan.FromSeconds(2));

                fakeSource.RaiseFrame(1, 1, new byte[] { 1 }, "jpeg");
                await decodeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

                for (byte i = 2; i <= 5; i++)
                {
                    fakeSource.RaiseFrame(1, 1, new byte[] { i }, "jpeg");
                }

                releaseDecode.TrySetResult(true);
                await WaitUntilAsync(
                    () => currentFrame is Bitmap latest && latest.PixelSize.Width == 5,
                    TimeSpan.FromSeconds(2));
                await WaitUntilAsync(
                    () => coordinator.DecodeTasksActive == 0,
                    TimeSpan.FromSeconds(2));

                Assert.True(active);
                Assert.NotNull(currentFrame);
                Assert.Equal(2, decodeCount);
                Assert.Equal(2, coordinator.FramesDecoded);
                Assert.Equal(1, coordinator.MaxDecodeTasksActive);
                Assert.Equal(0, coordinator.DecodeTasksActive);

                await coordinator.StopAsync();
                await WaitUntilAsync(() => !active, TimeSpan.FromSeconds(2));
                await WaitUntilAsync(() => currentFrame is null, TimeSpan.FromSeconds(2));
            }
            finally
            {
                currentFrame?.Dispose();
            }

            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeeScreenShareCoordinator_ToggleIsIdempotent_AndStopClearsFrame()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var fakeSource = new FakeScreenCaptureSource();
            var active = false;
            Bitmap? currentFrame = null;

            var coordinator = new HelpeeScreenShareCoordinator(
                isDisposed: static () => false,
                canShowScreenShareAction: static () => true,
                isPreviewActive: () => active,
                captureSourceFactory: new FixedCaptureSourceFactory(fakeSource),
                setPreviewActive: value => active = value,
                setStatus: _ => { },
                getPreviewFrame: () => currentFrame,
                setPreviewFrame: value => currentFrame = value,
                decodeFrame: _ => CreateTinyBitmap());

            try
            {
                coordinator.Toggle();
                coordinator.Toggle();

                await WaitUntilAsync(() => active, TimeSpan.FromSeconds(2));
                Assert.Equal(1, fakeSource.StartCallCount);

                fakeSource.RaiseFrame(1, 1, new byte[] { 1 }, "jpeg");
                await WaitUntilAsync(() => currentFrame is not null, TimeSpan.FromSeconds(2));

                await coordinator.StopAsync();

                Assert.False(active);
                Assert.False(fakeSource.IsStarted);
                Assert.Equal(1, fakeSource.StopCallCount);
                Assert.Equal(0, fakeSource.FrameSubscriberCount);
                Assert.Equal(0, coordinator.DecodeTasksActive);
                Assert.Null(currentFrame);
            }
            finally
            {
                currentFrame?.Dispose();
            }

            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeeScreenShareCoordinator_H264Preview_WaitsForStreamConfigBeforeDecode()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var fakeSource = new FakeScreenCaptureSource();
            var fakeDecoder = new FakePreviewH264BitmapDecoder();
            var active = false;
            Bitmap? currentFrame = null;

            var coordinator = new HelpeeScreenShareCoordinator(
                isDisposed: static () => false,
                canShowScreenShareAction: static () => true,
                isPreviewActive: () => active,
                captureSourceFactory: new FixedCaptureSourceFactory(fakeSource),
                setPreviewActive: value => active = value,
                setStatus: _ => { },
                getPreviewFrame: () => currentFrame,
                setPreviewFrame: value => currentFrame = value,
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                h264Decoder: fakeDecoder);

            try
            {
                coordinator.Toggle();
                await WaitUntilAsync(() => active, TimeSpan.FromSeconds(2));

                fakeSource.RaiseFrame(1, 1, new byte[] { 3 }, "h264", isKeyFrame: true, streamEpoch: 4);
                await Task.Delay(50);
                Assert.Null(currentFrame);
                Assert.Equal(0, fakeDecoder.ConfigureCallCount);
                Assert.Equal(0, fakeDecoder.DecodeCallCount);

                fakeSource.RaiseFrame(
                    1,
                    1,
                    new byte[] { 8 },
                    "h264",
                    isKeyFrame: true,
                    streamEpoch: 4,
                    streamConfig: new ScreenShareVideoStreamConfigV1
                    {
                        SessionId = "preview",
                        StreamEpoch = 4,
                        Encoding = "h264",
                        CodecProfile = "baseline",
                        DecoderConfigData = new byte[] { 1, 2, 3 },
                    });

                await WaitUntilAsync(
                    () => currentFrame is Bitmap latest && latest.PixelSize.Width == 8 && coordinator.DecodeTasksActive == 0,
                    TimeSpan.FromSeconds(2));

                Assert.Equal(1, fakeDecoder.ConfigureCallCount);
                Assert.Equal(1, fakeDecoder.DecodeCallCount);
                Assert.Equal(4, fakeDecoder.LastConfiguredEpoch);

                await coordinator.StopAsync();
            }
            finally
            {
                currentFrame?.Dispose();
            }

            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeeScreenShareCoordinator_H264Preview_NeedMoreInput_DoesNotResetStreamState_AndLaterAppliesFrame()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var fakeSource = new FakeScreenCaptureSource();
            var fakeDecoder = new FakePreviewH264BitmapDecoder
            {
                NeedMoreInputBeforeSuccessCount = 1,
            };
            var active = false;
            Bitmap? currentFrame = null;
            var status = new ScreenShareStatus(ScreenShareState.Off, null, DateTimeOffset.UtcNow);

            var coordinator = new HelpeeScreenShareCoordinator(
                isDisposed: static () => false,
                canShowScreenShareAction: static () => true,
                isPreviewActive: () => active,
                captureSourceFactory: new FixedCaptureSourceFactory(fakeSource),
                setPreviewActive: value => active = value,
                setStatus: value => status = value,
                getPreviewFrame: () => currentFrame,
                setPreviewFrame: value => currentFrame = value,
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                h264Decoder: fakeDecoder);

            try
            {
                coordinator.Toggle();
                await WaitUntilAsync(() => active, TimeSpan.FromSeconds(2));

                var streamConfig = new ScreenShareVideoStreamConfigV1
                {
                    SessionId = "preview",
                    StreamEpoch = 7,
                    Encoding = "h264",
                    CodecProfile = "baseline",
                    DecoderConfigData = new byte[] { 1, 2, 3 },
                };

                fakeSource.RaiseFrame(1, 1, new byte[] { 21 }, "h264", isKeyFrame: true, streamEpoch: 7, streamConfig: streamConfig);
                await Task.Delay(100);
                Assert.Null(currentFrame);
                Assert.Equal(ScreenShareState.Active, status.State);
                Assert.Equal(1, fakeDecoder.ConfigureCallCount);
                Assert.Equal(1, fakeDecoder.DecodeCallCount);
                Assert.Equal(0, fakeDecoder.ResetCallCount);

                fakeSource.RaiseFrame(1, 1, new byte[] { 22 }, "h264", isKeyFrame: false, streamEpoch: 7);
                await WaitUntilAsync(
                    () => currentFrame is Bitmap latest && latest.PixelSize.Width == 22 && coordinator.DecodeTasksActive == 0,
                    TimeSpan.FromSeconds(2));

                Assert.Equal(1, fakeDecoder.ConfigureCallCount);
                Assert.Equal(2, fakeDecoder.DecodeCallCount);
                Assert.Equal(0, fakeDecoder.ResetCallCount);
                Assert.Equal(ScreenShareState.Active, status.State);

                await coordinator.StopAsync();
            }
            finally
            {
                currentFrame?.Dispose();
            }

            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Helpee_Toggle_RapidStartStop_DoesNotHangOrStick()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var fakeSource = new FakeScreenCaptureSource();
            var active = false;
            Bitmap? currentFrame = null;
            var status = new ScreenShareStatus(ScreenShareState.Off, null, DateTimeOffset.UtcNow);
            var decodeCalls = 0;

            var coordinator = new HelpeeScreenShareCoordinator(
                isDisposed: static () => false,
                canShowScreenShareAction: static () => true,
                isPreviewActive: () => active,
                captureSourceFactory: new FixedCaptureSourceFactory(fakeSource),
                setPreviewActive: value => active = value,
                setStatus: value => status = value,
                getPreviewFrame: () => currentFrame,
                setPreviewFrame: value => currentFrame = value,
                decodeFrame: bytes =>
                {
                    Interlocked.Increment(ref decodeCalls);
                    return CreateBitmap(bytes[0], 1);
                });

            try
            {
                for (var i = 0; i < 10; i++)
                {
                    coordinator.Toggle();
                    coordinator.Toggle();
                }

                await AwaitCompletesAsync(
                    coordinator.StopAsync(),
                    TimeSpan.FromSeconds(2),
                    "helpee rapid-toggle final stop");

                await WaitUntilAsync(
                    () => !active &&
                          currentFrame is null &&
                          status.State == ScreenShareState.Off &&
                          fakeSource.FrameSubscriberCount == 0 &&
                          !fakeSource.IsStarted,
                    TimeSpan.FromSeconds(2));

                var decodeCallsBeforePostStopFrame = Volatile.Read(ref decodeCalls);
                fakeSource.RaiseFrame(1, 1, new byte[] { 9 }, "jpeg");
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                await Task.Yield();

                Assert.False(active);
                Assert.Null(currentFrame);
                Assert.Equal(ScreenShareState.Off, status.State);
                Assert.Equal(0, fakeSource.FrameSubscriberCount);
                Assert.False(fakeSource.IsStarted);
                Assert.Equal(0, coordinator.DecodeTasksActive);
                Assert.Equal(
                    decodeCallsBeforePostStopFrame,
                    Volatile.Read(ref decodeCalls));
            }
            finally
            {
                currentFrame?.Dispose();
            }

            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeeScreenShareCoordinator_StopDuringBlockedDecode_LeavesNoDecodeTaskRunning()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var fakeSource = new FakeScreenCaptureSource();
            var decodeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDecode = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var active = false;
            Bitmap? currentFrame = null;

            var coordinator = new HelpeeScreenShareCoordinator(
                isDisposed: static () => false,
                canShowScreenShareAction: static () => true,
                isPreviewActive: () => active,
                captureSourceFactory: new FixedCaptureSourceFactory(fakeSource),
                setPreviewActive: value => active = value,
                setStatus: _ => { },
                getPreviewFrame: () => currentFrame,
                setPreviewFrame: value => currentFrame = value,
                decodeFrame: _ =>
                {
                    decodeStarted.TrySetResult(true);
                    WaitForSignal(releaseDecode.Task, TimeSpan.FromSeconds(2));
                    return CreateTinyBitmap();
                });

            try
            {
                coordinator.Toggle();
                await WaitUntilAsync(() => active, TimeSpan.FromSeconds(2));

                fakeSource.RaiseFrame(1, 1, new byte[] { 1 }, "jpeg");
                await AwaitCompletesAsync(
                    decodeStarted.Task,
                    TimeSpan.FromSeconds(2),
                    "helpee decode start");

                var stopTask = Task.Run(coordinator.StopAsync);
                releaseDecode.TrySetResult(true);

                await AwaitCompletesAsync(
                    stopTask,
                    TimeSpan.FromSeconds(2),
                    "helpee stop during blocked decode");

                Assert.False(active);
                Assert.Equal(0, coordinator.DecodeTasksActive);
                Assert.Null(currentFrame);
            }
            finally
            {
                currentFrame?.Dispose();
            }

            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeeScreenShareCoordinator_StartFailure_SetsFailedStatus_AndCleansUp()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var failingSource = new FakeScreenCaptureSource
            {
                StartException = new InvalidOperationException("capture init failed"),
            };
            var previewActive = false;
            Bitmap? currentFrame = null;
            var status = new ScreenShareStatus(ScreenShareState.Off, null, DateTimeOffset.UtcNow);

            var coordinator = new HelpeeScreenShareCoordinator(
                isDisposed: static () => false,
                canShowScreenShareAction: static () => true,
                isPreviewActive: () => previewActive,
                captureSourceFactory: new FixedCaptureSourceFactory(failingSource),
                setPreviewActive: value => previewActive = value,
                setStatus: value => status = value,
                getPreviewFrame: () => currentFrame,
                setPreviewFrame: value => currentFrame = value,
                decodeFrame: _ => CreateTinyBitmap());

            try
            {
                coordinator.Toggle();

                await WaitUntilAsync(
                    () => status.State == ScreenShareState.Failed,
                    TimeSpan.FromSeconds(2));

                Assert.Equal("Screen sharing failed to start", status.UserMessage);
                Assert.False(previewActive);
                Assert.Null(currentFrame);
                Assert.False(failingSource.IsStarted);
                Assert.Equal(1, failingSource.StartCallCount);
                Assert.Equal(1, failingSource.DisposeCallCount);
            }
            finally
            {
                currentFrame?.Dispose();
            }

            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_UnsupportedTransportCapture_ThrowsNotSupported()
    {
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => new NotSupportedCaptureSource(),
            sendPayloadAsync: (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => coordinator.StartAsync("session-live", CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_Disconnected_StopsCapture_AndPreventsFurtherSends()
    {
        var fakeSource = new FakeScreenCaptureSource();
        var probe = new ScreenShareSendProbe(recentPayloadCapacity: 2);

        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: probe.SendReadOnlyPayloadAsync);

        await coordinator.StartAsync("session-live", CancellationToken.None);
        Assert.True(fakeSource.IsStarted);

        RaiseTransportFrame(fakeSource, 1, 1, new byte[] { 1, 2, 3 });
        await AwaitCompletesAsync(
            probe.WaitForPayloadCountAsync(1, TimeSpan.FromSeconds(2)),
            TimeSpan.FromSeconds(2),
            "first transport payload send");
        await AwaitCompletesAsync(
            coordinator.HandleDisconnectedAsync(),
            TimeSpan.FromSeconds(2),
            "disconnect stop");

        Assert.False(coordinator.IsActive);
        Assert.False(fakeSource.IsStarted);
        Assert.Equal(0, fakeSource.FrameSubscriberCount);
        Assert.Equal(1, fakeSource.StartCallCount);
        Assert.Equal(1, fakeSource.StopCallCount);

        RaiseTransportFrame(fakeSource, 1, 1, new byte[] { 4, 5, 6 });
        Assert.Equal(1, probe.PayloadsSent);

        var sentPayload = Assert.Single(probe.GetRecentPayloadsSnapshot());
        var fragment = Assert.Single(ExpandFragmentsFromPayload(sentPayload));
        Assert.Equal("session-live", fragment.SessionId);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_StampsSessionIdOntoVideoStreamConfigBeforeSending()
    {
        var fakeSource = new FakeScreenCaptureSource();
        ScreenShareVideoStreamConfigV1? sentStreamConfig = null;
        var probe = new ScreenShareSendProbe(recentPayloadCapacity: 2);

        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: probe.SendReadOnlyPayloadAsync,
            sendVideoStreamConfigAsync: (message, _) =>
            {
                sentStreamConfig = message;
                return Task.CompletedTask;
            });

        await coordinator.StartAsync("session-live", CancellationToken.None);

        fakeSource.RaiseFrame(
            new ScreenCaptureFrameEventArgs(
                1280,
                720,
                new byte[] { 1, 2, 3 },
                "h264",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                isKeyFrame: true,
                streamEpoch: 1,
                streamConfig: new ScreenShareVideoStreamConfigV1
                {
                    SessionId = string.Empty,
                    StreamEpoch = 1,
                    Encoding = "h264",
                    CodecProfile = "baseline",
                    DecoderConfigData = new byte[] { 7, 8, 9 },
                }));

        await AwaitCompletesAsync(
            probe.WaitForPayloadCountAsync(1, TimeSpan.FromSeconds(2)),
            TimeSpan.FromSeconds(2),
            "h264 fragment send after stamped stream config");

        Assert.NotNull(sentStreamConfig);
        Assert.Equal("session-live", sentStreamConfig!.SessionId);
        Assert.Equal(1, sentStreamConfig.StreamEpoch);
        Assert.Equal("h264", sentStreamConfig.Encoding);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_SendsStreamConfigEvenIfFirstMediaFrameGetsSuperseded()
    {
        var fakeSource = new FakeScreenCaptureSource();
        var probe = new ScreenShareSendProbe(recentPayloadCapacity: 4, maxInFlight: 1, startBlocked: true);
        var sentConfigs = new List<ScreenShareVideoStreamConfigV1>();

        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: probe.SendReadOnlyPayloadAsync,
            sendVideoStreamConfigAsync: (message, _) =>
            {
                sentConfigs.Add(message);
                return Task.CompletedTask;
            });

        await coordinator.StartAsync("session-live", CancellationToken.None);

        RaiseTransportFrame(fakeSource, 1280, 720, new byte[] { 1 }, streamEpoch: 1, isKeyFrame: true);
        await AwaitCompletesAsync(
            probe.FirstSendStarted,
            TimeSpan.FromSeconds(2),
            "first blocked transport send");

        fakeSource.RaiseFrame(
            new ScreenCaptureFrameEventArgs(
                1280,
                720,
                new byte[] { 2 },
                "h264",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                isKeyFrame: false,
                streamEpoch: 1,
                streamConfig: null));

        probe.ReleaseBlockedSends();
        await AwaitCompletesAsync(
            probe.WaitForPayloadCountAsync(1, TimeSpan.FromSeconds(2)),
            TimeSpan.FromSeconds(2),
            "superseded media payload send");

        Assert.Equal(2, sentConfigs.Count);
        Assert.All(sentConfigs, config =>
        {
            Assert.Equal("session-live", config.SessionId);
            Assert.Equal(1, config.StreamEpoch);
        });
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ResendsStreamConfigDuringEpochBootstrap()
    {
        var fakeSource = new FakeScreenCaptureSource();
        var probe = new ScreenShareSendProbe(recentPayloadCapacity: 4);
        var sentConfigs = new List<ScreenShareVideoStreamConfigV1>();

        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: probe.SendReadOnlyPayloadAsync,
            sendVideoStreamConfigAsync: (message, _) =>
            {
                sentConfigs.Add(message);
                return Task.CompletedTask;
            });

        await coordinator.StartAsync("session-live", CancellationToken.None);

        fakeSource.RaiseFrame(
            new ScreenCaptureFrameEventArgs(
                1280,
                720,
                new byte[] { 1 },
                "h264",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                isKeyFrame: true,
                streamEpoch: 2,
                streamConfig: new ScreenShareVideoStreamConfigV1
                {
                    SessionId = string.Empty,
                    StreamEpoch = 2,
                    Encoding = "h264",
                    CodecProfile = "baseline",
                    DecoderConfigData = new byte[] { 7, 8, 9 },
                }));

        fakeSource.RaiseFrame(
            new ScreenCaptureFrameEventArgs(
                1280,
                720,
                new byte[] { 2 },
                "h264",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                isKeyFrame: false,
                streamEpoch: 2,
                streamConfig: null));
        fakeSource.RaiseFrame(
            new ScreenCaptureFrameEventArgs(
                1280,
                720,
                new byte[] { 3 },
                "h264",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                isKeyFrame: false,
                streamEpoch: 2,
                streamConfig: null));
        fakeSource.RaiseFrame(
            new ScreenCaptureFrameEventArgs(
                1280,
                720,
                new byte[] { 4 },
                "h264",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                isKeyFrame: false,
                streamEpoch: 2,
                streamConfig: null));

        await Task.Delay(150);

        Assert.Equal(3, sentConfigs.Count);
        Assert.All(sentConfigs, config =>
        {
            Assert.Equal("session-live", config.SessionId);
            Assert.Equal(2, config.StreamEpoch);
            Assert.Equal("h264", config.Encoding);
        });
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_StopSendsRemoteStop_BeforeSlowCaptureShutdownCompletes()
    {
        var fakeSource = new FakeScreenCaptureSource
        {
            StopBlocker = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var probe = new ScreenShareSendProbe(recentPayloadCapacity: 2);

        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: probe.SendReadOnlyPayloadAsync);

        await coordinator.StartAsync("session-live", CancellationToken.None);

        var stopTask = coordinator.StopAsync(sendStopMessage: true, reason: "preview_stopped", CancellationToken.None);

        await AwaitCompletesAsync(
            probe.WaitForPayloadCountAsync(1, TimeSpan.FromSeconds(2)),
            TimeSpan.FromSeconds(2),
            "screen-share stop payload before capture shutdown");

        Assert.False(stopTask.IsCompleted, "Expected stop to still be waiting on capture shutdown.");

        var sentPayload = Assert.Single(probe.GetRecentPayloadsSnapshot());
        Assert.True(ScreenSharePayloadCodec.TryDeserializeStop(sentPayload, out var stop));
        Assert.Equal("session-live", stop.SessionId);
        Assert.Equal("preview_stopped", stop.Reason);

        fakeSource.StopBlocker.TrySetResult(true);
        await AwaitCompletesAsync(
            stopTask,
            TimeSpan.FromSeconds(2),
            "screen-share stop final completion");
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_FrameSizeOnlyChange_DoesNotResendDisplayInfo()
    {
        var fakeSource = new FakeScreenCaptureSource
        {
            CaptureMetadata = new ScreenCaptureMetadata(
                DisplayId: "primary",
                CaptureRegionPx: new ScreenCapturePixelRect(0, 0, 1920, 1080),
                DpiScale: 1.25),
        };
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 5, 16, 0, 0, TimeSpan.Zero));
        var sentRevisions = new ConcurrentQueue<long>();

        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock,
            sendDisplayInfoAsync: (_, message, _) =>
            {
                sentRevisions.Enqueue(message.Revision);
                return Task.CompletedTask;
            });

        await coordinator.StartAsync("session-live", CancellationToken.None);
        RaiseTransportFrame(fakeSource, 1280, 720, new byte[] { 1 });
        await WaitUntilAsync(() => sentRevisions.Count == 1, TimeSpan.FromSeconds(2));

        clock.Advance(TimeSpan.FromMilliseconds(300));
        RaiseTransportFrame(fakeSource, 960, 540, new byte[] { 2 });
        clock.Advance(TimeSpan.FromMilliseconds(300));
        RaiseTransportFrame(fakeSource, 1280, 720, new byte[] { 3 });
        await Task.Delay(100);

        Assert.Equal(1, sentRevisions.Count);
        Assert.Equal(new long[] { 1 }, sentRevisions.ToArray());
        Assert.Equal(1, coordinator.GetMetricsSnapshot().DisplayInfoSendCount);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_MappingChange_IsDebounced_AndIncrementsRevisionOnce()
    {
        var fakeSource = new FakeScreenCaptureSource
        {
            CaptureMetadata = new ScreenCaptureMetadata(
                DisplayId: "primary",
                CaptureRegionPx: new ScreenCapturePixelRect(0, 0, 1920, 1080),
                DpiScale: 1.25),
        };
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 5, 16, 10, 0, TimeSpan.Zero));
        var sentRevisions = new ConcurrentQueue<long>();

        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock,
            sendDisplayInfoAsync: (_, message, _) =>
            {
                sentRevisions.Enqueue(message.Revision);
                return Task.CompletedTask;
            });

        await coordinator.StartAsync("session-live", CancellationToken.None);
        RaiseTransportFrame(fakeSource, 1280, 720, new byte[] { 1 });
        await WaitUntilAsync(() => sentRevisions.Count == 1, TimeSpan.FromSeconds(2));

        fakeSource.CaptureMetadata = new ScreenCaptureMetadata(
            DisplayId: "primary",
            CaptureRegionPx: new ScreenCapturePixelRect(100, 50, 1720, 980),
            DpiScale: 1.25);
        RaiseTransportFrame(fakeSource, 1280, 720, new byte[] { 2 });
        clock.Advance(TimeSpan.FromMilliseconds(100));
        RaiseTransportFrame(fakeSource, 1280, 720, new byte[] { 3 });
        await Task.Delay(100);
        Assert.Equal(1, sentRevisions.Count);

        clock.Advance(TimeSpan.FromMilliseconds(200));
        RaiseTransportFrame(fakeSource, 1280, 720, new byte[] { 4 });
        await WaitUntilAsync(() => sentRevisions.Count == 2, TimeSpan.FromSeconds(2));
        Assert.Equal(new long[] { 1, 2 }, sentRevisions.ToArray());
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_MappingChange_FlushesQueuedFrames_SoFirstPostChangeFrameUsesNewSize()
    {
        var fakeSource = new FakeScreenCaptureSource
        {
            CaptureMetadata = new ScreenCaptureMetadata(
                DisplayId: "primary",
                CaptureRegionPx: new ScreenCapturePixelRect(0, 0, 1920, 1080),
                DpiScale: 1.25),
        };
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 8, 12, 0, 0, TimeSpan.Zero));
        var probe = new ScreenShareSendProbe(recentPayloadCapacity: 8, maxInFlight: 1, startBlocked: true);
        var sentRevisions = new ConcurrentQueue<long>();

        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: probe.SendReadOnlyPayloadAsync,
            clock: clock,
            sendDisplayInfoAsync: (_, message, _) =>
            {
                sentRevisions.Enqueue(message.Revision);
                return Task.CompletedTask;
            });

        await coordinator.StartAsync("session-live", CancellationToken.None);

        RaiseTransportFrame(fakeSource, 1280, 720, new byte[] { 1 });
        await AwaitCompletesAsync(
            probe.FirstSendStarted,
            TimeSpan.FromSeconds(2),
            "initial blocked frame send");
        await WaitUntilAsync(() => sentRevisions.Count == 1, TimeSpan.FromSeconds(2));

        RaiseTransportFrame(fakeSource, 1280, 720, new byte[] { 2 });

        fakeSource.CaptureMetadata = new ScreenCaptureMetadata(
            DisplayId: "primary",
            CaptureRegionPx: new ScreenCapturePixelRect(100, 50, 1720, 980),
            DpiScale: 1.25);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        RaiseTransportFrame(fakeSource, 960, 540, new byte[] { 3 });
        await Task.Delay(100);
        Assert.Equal(1, sentRevisions.Count);

        clock.Advance(TimeSpan.FromMilliseconds(300));
        RaiseTransportFrame(fakeSource, 960, 540, new byte[] { 4 });
        await WaitUntilAsync(() => sentRevisions.Count == 2, TimeSpan.FromSeconds(2));

        probe.ReleaseBlockedSends();
        await AwaitCompletesAsync(
            probe.WaitForPayloadCountAsync(2, TimeSpan.FromSeconds(2)),
            TimeSpan.FromSeconds(2),
            "flushed queued frames final payloads");
        await Task.Delay(150);

        var payloads = probe.GetRecentPayloadsSnapshot();
        Assert.Equal(2, probe.PayloadsSent);
        Assert.Equal(2, payloads.Length);
        Assert.Equal(new long[] { 1, 2 }, sentRevisions.ToArray());

        var firstFragment = Assert.Single(ExpandFragmentsFromPayload(payloads[0]));
        var secondFragment = Assert.Single(ExpandFragmentsFromPayload(payloads[1]));

        Assert.Equal((1280, 720), (firstFragment.Width, firstFragment.Height));
        Assert.Equal(new byte[] { 1 }, firstFragment.Data);
        Assert.Equal((960, 540), (secondFragment.Width, secondFragment.Height));
        Assert.Equal(new byte[] { 4 }, secondFragment.Data);

        var senderMetrics = coordinator.GetMetricsSnapshot();
        Assert.True(senderMetrics.FramesDropped >= 2, $"Expected queued stale frames to be dropped. Metrics={senderMetrics}");
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_DisplayInfoSendFailure_DoesNotRaiseUnobservedException_AndLaterFrameRetries()
    {
        using var unobserved = new UnobservedTaskExceptionRecorder();
        var fakeSource = new FakeScreenCaptureSource
        {
            CaptureMetadata = new ScreenCaptureMetadata(
                DisplayId: "primary",
                CaptureRegionPx: new ScreenCapturePixelRect(0, 0, 1920, 1080),
                DpiScale: 1.0),
        };
        var sentRevisions = new ConcurrentQueue<long>();
        var attempts = 0;

        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            sendDisplayInfoAsync: (_, message, _) =>
            {
                var nextAttempt = Interlocked.Increment(ref attempts);
                if (nextAttempt == 1)
                {
                    throw new InvalidOperationException("display-info-send-failed");
                }

                sentRevisions.Enqueue(message.Revision);
                return Task.CompletedTask;
            });

        await coordinator.StartAsync("session-live", CancellationToken.None);

        RaiseTransportFrame(fakeSource, 1280, 720, new byte[] { 1 });
        await WaitUntilAsync(() => Volatile.Read(ref attempts) >= 1, TimeSpan.FromSeconds(2));
        await Task.Yield();
        ForceFullCollection();
        Assert.Empty(unobserved.Exceptions);

        RaiseTransportFrame(fakeSource, 1280, 720, new byte[] { 2 });
        await WaitUntilAsync(() => sentRevisions.Count == 1, TimeSpan.FromSeconds(2));

        Assert.Equal(2, Volatile.Read(ref attempts));
        Assert.Equal(new long[] { 2 }, sentRevisions.ToArray());
        Assert.Equal(1, coordinator.GetMetricsSnapshot().DisplayInfoSendCount);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_StaleDisplayInfoFailureAfterRestart_DoesNotForceRedundantResend()
    {
        using var unobserved = new UnobservedTaskExceptionRecorder();
        var fakeSource = new FakeScreenCaptureSource
        {
            CaptureMetadata = new ScreenCaptureMetadata(
                DisplayId: "primary",
                CaptureRegionPx: new ScreenCapturePixelRect(0, 0, 1920, 1080),
                DpiScale: 1.0),
        };
        var staleSendStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStaleSend = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleFailureObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sentRevisions = new ConcurrentQueue<long>();
        var attempts = 0;

        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            sendDisplayInfoAsync: async (_, message, _) =>
            {
                var attempt = Interlocked.Increment(ref attempts);
                if (attempt == 1)
                {
                    staleSendStarted.TrySetResult(true);
                    await releaseStaleSend.Task.ConfigureAwait(false);
                    staleFailureObserved.TrySetResult(true);
                    throw new InvalidOperationException("stale-display-info-send");
                }

                sentRevisions.Enqueue(message.Revision);
            });

        await coordinator.StartAsync("session-live", CancellationToken.None);

        RaiseTransportFrame(fakeSource, 1280, 720, new byte[] { 1 });
        await AwaitCompletesAsync(
            staleSendStarted.Task,
            TimeSpan.FromSeconds(2),
            "stale display-info send start");

        await coordinator.StopAsync(sendStopMessage: false, reason: null, CancellationToken.None);
        await coordinator.StartAsync("session-live", CancellationToken.None);

        RaiseTransportFrame(fakeSource, 1280, 720, new byte[] { 2 });
        await WaitUntilAsync(() => sentRevisions.Count == 1, TimeSpan.FromSeconds(2));

        releaseStaleSend.TrySetResult(true);
        await AwaitCompletesAsync(
            staleFailureObserved.Task,
            TimeSpan.FromSeconds(2),
            "stale display-info send failure");
        await Task.Yield();
        ForceFullCollection();
        Assert.Empty(unobserved.Exceptions);

        RaiseTransportFrame(fakeSource, 1280, 720, new byte[] { 3 });
        await Task.Delay(150);

        Assert.Equal(2, Volatile.Read(ref attempts));
        Assert.Equal(new long[] { 1 }, sentRevisions.ToArray());
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_FrameSizeOnlyChange_RemainsSuppressed_AcrossRapidRestart()
    {
        var fakeSource = new FakeScreenCaptureSource
        {
            CaptureMetadata = new ScreenCaptureMetadata(
                DisplayId: "primary",
                CaptureRegionPx: new ScreenCapturePixelRect(0, 0, 1920, 1080),
                DpiScale: 1.25),
        };
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 5, 16, 20, 0, TimeSpan.Zero));
        var sentRevisions = new ConcurrentQueue<long>();

        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock,
            sendDisplayInfoAsync: (_, message, _) =>
            {
                sentRevisions.Enqueue(message.Revision);
                return Task.CompletedTask;
            });

        await coordinator.StartAsync("session-live", CancellationToken.None);
        RaiseTransportFrame(fakeSource, 1280, 720, new byte[] { 1 });
        await WaitUntilAsync(() => sentRevisions.Count == 1, TimeSpan.FromSeconds(2));

        await coordinator.StopAsync(sendStopMessage: false, reason: null, CancellationToken.None);
        await coordinator.StartAsync("session-live", CancellationToken.None);

        RaiseTransportFrame(fakeSource, 960, 540, new byte[] { 2 });
        await WaitUntilAsync(() => sentRevisions.Count == 2, TimeSpan.FromSeconds(2));

        clock.Advance(TimeSpan.FromMilliseconds(300));
        RaiseTransportFrame(fakeSource, 1280, 720, new byte[] { 3 });
        clock.Advance(TimeSpan.FromMilliseconds(300));
        RaiseTransportFrame(fakeSource, 800, 450, new byte[] { 4 });
        await Task.Delay(100);

        Assert.Equal(new long[] { 1, 1 }, sentRevisions.ToArray());
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_MappingChangeDebounce_RemainsScoped_AcrossRapidRestart()
    {
        var fakeSource = new FakeScreenCaptureSource
        {
            CaptureMetadata = new ScreenCaptureMetadata(
                DisplayId: "primary",
                CaptureRegionPx: new ScreenCapturePixelRect(0, 0, 1920, 1080),
                DpiScale: 1.25),
        };
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 5, 16, 30, 0, TimeSpan.Zero));
        var sentRevisions = new ConcurrentQueue<long>();

        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock,
            sendDisplayInfoAsync: (_, message, _) =>
            {
                sentRevisions.Enqueue(message.Revision);
                return Task.CompletedTask;
            });

        await coordinator.StartAsync("session-live", CancellationToken.None);
        RaiseTransportFrame(fakeSource, 1280, 720, new byte[] { 1 });
        await WaitUntilAsync(() => sentRevisions.Count == 1, TimeSpan.FromSeconds(2));

        fakeSource.CaptureMetadata = new ScreenCaptureMetadata(
            DisplayId: "primary",
            CaptureRegionPx: new ScreenCapturePixelRect(100, 50, 1720, 980),
            DpiScale: 1.25);
        RaiseTransportFrame(fakeSource, 1280, 720, new byte[] { 2 });
        clock.Advance(TimeSpan.FromMilliseconds(100));
        RaiseTransportFrame(fakeSource, 1280, 720, new byte[] { 3 });
        await Task.Delay(100);
        Assert.Equal(1, sentRevisions.Count);

        await coordinator.StopAsync(sendStopMessage: false, reason: null, CancellationToken.None);
        await coordinator.StartAsync("session-live", CancellationToken.None);

        RaiseTransportFrame(fakeSource, 1280, 720, new byte[] { 4 });
        await WaitUntilAsync(() => sentRevisions.Count == 2, TimeSpan.FromSeconds(2));

        fakeSource.CaptureMetadata = new ScreenCaptureMetadata(
            DisplayId: "primary",
            CaptureRegionPx: new ScreenCapturePixelRect(150, 80, 1600, 900),
            DpiScale: 1.25);
        RaiseTransportFrame(fakeSource, 1280, 720, new byte[] { 5 });
        clock.Advance(TimeSpan.FromMilliseconds(100));
        RaiseTransportFrame(fakeSource, 1280, 720, new byte[] { 6 });
        await Task.Delay(100);
        Assert.Equal(2, sentRevisions.Count);

        clock.Advance(TimeSpan.FromMilliseconds(200));
        RaiseTransportFrame(fakeSource, 1280, 720, new byte[] { 7 });
        await WaitUntilAsync(() => sentRevisions.Count == 3, TimeSpan.FromSeconds(2));

        Assert.Equal(new long[] { 1, 1, 2 }, sentRevisions.ToArray());
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Transport_Stop_PreventsFurtherSends()
    {
        var fakeSource = new FakeScreenCaptureSource();
        var payloadsSent = 0;
        var firstSendObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var postStopSendObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observePostStopSends = 0;

        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) =>
            {
                Interlocked.Increment(ref payloadsSent);
                firstSendObserved.TrySetResult(true);

                if (Volatile.Read(ref observePostStopSends) == 1)
                {
                    postStopSendObserved.TrySetResult(true);
                }

                return Task.CompletedTask;
            });

        await AwaitCompletesAsync(
            coordinator.StartAsync("s1", CancellationToken.None),
            TimeSpan.FromSeconds(3),
            "transport stop regression start");
        DisableStartupWarmupForCoordinatorOnly(coordinator);

        for (var i = 0; i < 20; i++)
        {
            RaiseTransportFrame(fakeSource, 1, 1, new byte[] { (byte)(i + 1) });
        }

        await AwaitCompletesAsync(
            firstSendObserved.Task,
            TimeSpan.FromSeconds(3),
            "transport stop regression first send");

        await AwaitCompletesAsync(
            coordinator.StopAsync(sendStopMessage: false, reason: "test", CancellationToken.None),
            TimeSpan.FromSeconds(3),
            "transport stop regression stop");

        var payloadCountAfterStop = Volatile.Read(ref payloadsSent);
        Volatile.Write(ref observePostStopSends, 1);

        for (var i = 0; i < 50; i++)
        {
            RaiseTransportFrame(fakeSource, 1, 1, new byte[] { (byte)((i % 250) + 21) });
        }

        var completed = await Task.WhenAny(
            postStopSendObserved.Task,
            Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None));

        Assert.False(
            ReferenceEquals(completed, postStopSendObserved.Task),
            $"Observed a transport payload send after stop. PayloadCountAfterStop={payloadCountAfterStop}, CurrentPayloadCount={Volatile.Read(ref payloadsSent)}.");
        Assert.Equal(payloadCountAfterStop, Volatile.Read(ref payloadsSent));
        Assert.False(coordinator.IsActive);
        Assert.False(fakeSource.IsStarted);
        Assert.Equal(0, fakeSource.FrameSubscriberCount);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_StartTwiceSameSession_AndStopTwice_IsIdempotent()
    {
        var fakeSource = new FakeScreenCaptureSource();
        var sentPayloads = new List<byte[]>();
        var firstPayloadSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (payload, _) =>
            {
                lock (sentPayloads)
                {
                    sentPayloads.Add(payload.ToArray());
                }

                firstPayloadSent.TrySetResult(true);
                return Task.CompletedTask;
            });

        await coordinator.StartAsync("session-live", CancellationToken.None);
        await coordinator.StartAsync("session-live", CancellationToken.None);

        Assert.True(coordinator.IsActive);
        Assert.True(fakeSource.IsStarted);
        Assert.Equal(1, fakeSource.StartCallCount);

        RaiseTransportFrame(fakeSource, 1, 1, new byte[] { 1, 2, 3 });
        await AwaitCompletesAsync(
            firstPayloadSent.Task,
            TimeSpan.FromSeconds(2),
            "first payload send before stop");

        await AwaitCompletesAsync(
            coordinator.StopAsync(sendStopMessage: false, reason: null, CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "first stop");
        await AwaitCompletesAsync(
            coordinator.StopAsync(sendStopMessage: false, reason: null, CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "second stop");

        Assert.False(coordinator.IsActive);
        Assert.False(fakeSource.IsStarted);
        Assert.Equal(0, fakeSource.FrameSubscriberCount);
        Assert.Equal(1, fakeSource.StopCallCount);

        RaiseTransportFrame(fakeSource, 1, 1, new byte[] { 4, 5, 6 });
        lock (sentPayloads)
        {
            Assert.Single(sentPayloads);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RapidFrames_AreThrottledForTransport()
    {
        await RunRapidFramesThrottledScenarioAsync(iterationLabel: "single");
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_Stop_UnderRapidBlockedFrameLoad_Completes()
    {
        await RunStopOrDisconnectUnderLoadScenarioAsync(
            scenarioLabel: "stop under rapid blocked load",
            stopAsync: coordinator => coordinator.StopAsync(sendStopMessage: false, reason: null, CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_StopUnderLoad_Completes()
    {
        await RunStopOrDisconnectUnderLoadScenarioAsync(
            scenarioLabel: "stop under load",
            stopAsync: coordinator => coordinator.StopAsync(sendStopMessage: false, reason: null, CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_Disconnect_UnderRapidBlockedFrameLoad_Completes()
    {
        await RunStopOrDisconnectUnderLoadScenarioAsync(
            scenarioLabel: "disconnect under rapid blocked load",
            stopAsync: coordinator => coordinator.HandleDisconnectedAsync());
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Transport_Start_Stop_Disconnect_Restart_NoHang()
    {
        var fakeSource = new FakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 4, 16, 0, 0, TimeSpan.Zero));
        var probe = new ScreenShareSendProbe(recentPayloadCapacity: 8);

        var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: probe.SendReadOnlyPayloadAsync,
            clock: clock);
        try
        {
            await AwaitCompletesAsync(
                coordinator.StartAsync("s1", CancellationToken.None),
                TimeSpan.FromSeconds(3),
                "transport lifecycle step 1 start s1");
            DisableStartupWarmupForCoordinatorOnly(coordinator);

            RaiseTransportFrame(fakeSource, 1, 1, new byte[] { 1 });
            await AwaitCompletesAsync(
                probe.WaitForPayloadCountAsync(1, TimeSpan.FromSeconds(3)),
                TimeSpan.FromSeconds(3),
                "transport lifecycle step 2 first payload for s1");

            DriveTransportFrames(fakeSource, clock, count: 24, advancePerFrame: TimeSpan.FromMilliseconds(500));
            await AwaitCompletesAsync(
                probe.WaitForPayloadCountAsync(2, TimeSpan.FromSeconds(3)),
                TimeSpan.FromSeconds(3),
                "transport lifecycle step 3 payloads for s1");

            await AwaitCompletesAsync(
                coordinator.StopAsync(sendStopMessage: false, reason: "test", CancellationToken.None),
                TimeSpan.FromSeconds(3),
                "transport lifecycle step 4 first stop");
            await AwaitCompletesAsync(
                coordinator.StopAsync(sendStopMessage: false, reason: "test", CancellationToken.None),
                TimeSpan.FromSeconds(3),
                "transport lifecycle step 5 second stop");

            var payloadCountAfterStop = probe.PayloadsSent;

            await AwaitCompletesAsync(
                coordinator.StartAsync("s2", CancellationToken.None),
                TimeSpan.FromSeconds(3),
                "transport lifecycle step 6 restart s2");
            DisableStartupWarmupForCoordinatorOnly(coordinator);

            RaiseTransportFrame(fakeSource, 1, 1, new byte[] { 101 });
            await AwaitCompletesAsync(
                probe.WaitForPayloadCountAsync(payloadCountAfterStop + 1, TimeSpan.FromSeconds(3)),
                TimeSpan.FromSeconds(3),
                "transport lifecycle step 7 first payload for s2");

            DriveTransportFrames(fakeSource, clock, count: 24, advancePerFrame: TimeSpan.FromMilliseconds(500));
            await AwaitCompletesAsync(
                probe.WaitForPayloadCountAsync(payloadCountAfterStop + 2, TimeSpan.FromSeconds(3)),
                TimeSpan.FromSeconds(3),
                "transport lifecycle step 8 payloads for s2");

            await AwaitCompletesAsync(
                coordinator.HandleDisconnectedAsync(),
                TimeSpan.FromSeconds(3),
                "transport lifecycle step 9 disconnect");

            Assert.False(coordinator.IsActive);
            Assert.False(fakeSource.IsStarted);
            Assert.Equal(0, fakeSource.FrameSubscriberCount);

            var payloadCountAfterDisconnect = probe.PayloadsSent;
            RaiseTransportFrame(fakeSource, 1, 1, new byte[] { 201 });
            RaiseTransportFrame(fakeSource, 1, 1, new byte[] { 202 });
            await Task.Yield();

            Assert.Equal(payloadCountAfterDisconnect, probe.PayloadsSent);
        }
        finally
        {
            await AwaitCompletesAsync(
                coordinator.DisposeAsync().AsTask(),
                TimeSpan.FromSeconds(3),
                "transport lifecycle final dispose");
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_AutoTuneHint_ResetsAcrossStopRestart()
    {
        using var autoTuneFlag = new EnvironmentOverride("NLINK_FEATURE_SCREENCAP_TRANSPORT_AUTOTUNE", "1");
        var fakeSource = new AdaptiveFakeScreenCaptureSource
        {
            CaptureMetadata = new ScreenCaptureMetadata(
                DisplayId: "primary",
                CaptureRegionPx: new ScreenCapturePixelRect(0, 0, 1920, 1080),
                DpiScale: 1.0),
        };
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 6, 10, 0, 0, TimeSpan.Zero));
        var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        try
        {
            await AwaitCompletesAsync(
                coordinator.StartAsync("session-auto-1", CancellationToken.None),
                TimeSpan.FromSeconds(2),
                "auto-tune cycle 1 start");

            Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);

            DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource);

            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { 1, 2, 3 },
                    capturedTsUtcMs: clock.UtcNow.AddMilliseconds(-1000).ToUnixTimeMilliseconds()));

            await Task.Delay(50);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

            Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
            Assert.Equal(ScreenShareTransportTuningLevel.BandwidthReduced, fakeSource.LastTransportTuningLevel);
            Assert.Contains(FeatureFlags.ScreenShareTransportMaxFps, fakeSource.CaptureFrameRateHints);

            await AwaitCompletesAsync(
                coordinator.StopAsync(sendStopMessage: false, reason: "restart", CancellationToken.None),
                TimeSpan.FromSeconds(2),
                "auto-tune cycle 1 stop");

            await AwaitCompletesAsync(
                coordinator.StartAsync("session-auto-2", CancellationToken.None),
                TimeSpan.FromSeconds(2),
                "auto-tune cycle 2 start");

            Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);

            DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource);

            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { 4, 5, 6 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));

            await Task.Delay(50);
            autoTuneTick.Invoke(coordinator, Array.Empty<object>());

            Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
        }
        finally
        {
            await AwaitCompletesAsync(
                coordinator.DisposeAsync().AsTask(),
                TimeSpan.FromSeconds(2),
                "auto-tune final dispose");
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_AutoTuneHint_CoalescesWithinSendSlot_WithoutRateGateDrops()
    {
        using var autoTuneFlag = new EnvironmentOverride("NLINK_FEATURE_SCREENCAP_TRANSPORT_AUTOTUNE", "1");
        var fakeSource = new AdaptiveFakeScreenCaptureSource
        {
            CaptureMetadata = new ScreenCaptureMetadata(
                DisplayId: "primary",
                CaptureRegionPx: new ScreenCapturePixelRect(0, 0, 1920, 1080),
                DpiScale: 1.0),
        };
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 8, 11, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-rate-pressure", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "rate pressure start");

        Assert.InRange(fakeSource.LastCaptureFrameRateHint, 5, 8);
        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource);

        for (var i = 0; i < 5; i++)
        {
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(i + 1), 7, 9 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));
            clock.Advance(TimeSpan.FromMilliseconds(40));
        }

        await WaitUntilAsync(
            () => coordinator.GetMetricsSnapshot().FramesCaptured >= 1,
            TimeSpan.FromSeconds(2));
        var burstMetrics = coordinator.GetMetricsSnapshot();
        Assert.Equal(0, burstMetrics.FramesDroppedByRateGate);
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

        Assert.InRange(fakeSource.LastCaptureFrameRateHint, 5, 8);
        Assert.Equal(ScreenShareTransportTuningLevel.BandwidthReduced, fakeSource.LastTransportTuningLevel);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_AutoTuneHint_ReducesOnQueuePressure_BeforeAnySuccessfulSend()
    {
        using var autoTuneFlag = new EnvironmentOverride("NLINK_FEATURE_SCREENCAP_TRANSPORT_AUTOTUNE", "1");
        var fakeSource = new AdaptiveFakeScreenCaptureSource
        {
            CaptureMetadata = new ScreenCaptureMetadata(
                DisplayId: "primary",
                CaptureRegionPx: new ScreenCapturePixelRect(0, 0, 1920, 1080),
                DpiScale: 1.0),
        };
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 8, 11, 30, 0, TimeSpan.Zero));
        var sendEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: async (_, ct) =>
            {
                sendEntered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            },
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-queue-pressure", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "queue pressure start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource);

        fakeSource.RaiseFrame(
            CreateTransportFrameEventArgs(
                1280,
                720,
                new byte[] { 1, 2, 3 },
                capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));
        await AwaitCompletesAsync(
            sendEntered.Task,
            TimeSpan.FromSeconds(2),
            "queue pressure blocked send entered");

        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(10 + i), 2, 3 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));
        }

        await Task.Delay(50);
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

        Assert.InRange(fakeSource.LastCaptureFrameRateHint, 5, 8);
        Assert.Equal(ScreenShareTransportTuningLevel.BandwidthReduced, fakeSource.LastTransportTuningLevel);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_AutoTuneHint_MildQueuePressure_EntersReducedModeBeforeCatchUp()
    {
        using var autoTuneFlag = new EnvironmentOverride("NLINK_FEATURE_SCREENCAP_TRANSPORT_AUTOTUNE", "1");
        var fakeSource = new AdaptiveFakeScreenCaptureSource
        {
            CaptureMetadata = new ScreenCaptureMetadata(
                DisplayId: "primary",
                CaptureRegionPx: new ScreenCapturePixelRect(0, 0, 1920, 1080),
                DpiScale: 1.0),
        };
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 8, 11, 45, 0, TimeSpan.Zero));
        var sendEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: async (_, ct) =>
            {
                sendEntered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            },
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-quality-protected", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "quality protected start");

        DisableStartupWarmupForAutoTuneTests(
            coordinator,
            fakeSource,
            initialFpsHint: TransportClarityFpsFloorForTesting);

        fakeSource.RaiseFrame(
            CreateTransportFrameEventArgs(
                1280,
                720,
                new byte[] { 1, 2, 3 },
                capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));
        await AwaitCompletesAsync(
            sendEntered.Task,
            TimeSpan.FromSeconds(2),
            "quality protected blocked send entered");

        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(20 + i), 4, 5 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));
            await Task.Delay(20);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.InRange(fakeSource.LastCaptureFrameRateHint, 5, 8);
        Assert.Equal(ScreenShareTransportTuningLevel.BandwidthReduced, fakeSource.LastTransportTuningLevel);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_AutoTuneHint_EscalatesToCatchUpAfterSustainedSeverePressure()
    {
        using var autoTuneFlag = new EnvironmentOverride("NLINK_FEATURE_SCREENCAP_TRANSPORT_AUTOTUNE", "1");
        var fakeSource = new AdaptiveFakeScreenCaptureSource
        {
            CaptureMetadata = new ScreenCaptureMetadata(
                DisplayId: "primary",
                CaptureRegionPx: new ScreenCapturePixelRect(0, 0, 1920, 1080),
                DpiScale: 1.0),
        };
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 8, 12, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-bandwidth-reduced", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "bandwidth reduced start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeTotalDurationMs: 30));
        fakeSource.RaiseFrame(
            CreateTransportFrameEventArgs(
                1280,
                720,
                new byte[] { 1, 2, 3 },
                capturedTsUtcMs: clock.UtcNow.AddMilliseconds(-700).ToUnixTimeMilliseconds()));
        await Task.Delay(20);
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(ScreenShareTransportTuningLevel.BandwidthReduced, fakeSource.LastTransportTuningLevel);
        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);

        clock.Advance(TimeSpan.FromSeconds(1));
        fakeSource.RaiseFrame(
            CreateTransportFrameEventArgs(
                1280,
                720,
                new byte[] { 4, 5, 6 },
                capturedTsUtcMs: clock.UtcNow.AddMilliseconds(-720).ToUnixTimeMilliseconds()));
        await Task.Delay(20);
        autoTuneTick.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal(3, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(ScreenShareTransportTuningLevel.BandwidthReduced, fakeSource.LastTransportTuningLevel);
        Assert.Equal("catch_up", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(0, fakeSource.PurgePendingRawFramesCallCount);
        Assert.Contains("catch_up_mode_enter", fakeSource.KeyFrameRequestReasons);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_StartsInReducedProfile_AndPromotesAfterHealthyTicks()
    {
        using var autoTuneFlag = new EnvironmentOverride("NLINK_FEATURE_SCREENCAP_TRANSPORT_AUTOTUNE", "1");
        var fakeSource = new AdaptiveFakeScreenCaptureSource
        {
            CaptureMetadata = new ScreenCaptureMetadata(
                DisplayId: "primary",
                CaptureRegionPx: new ScreenCapturePixelRect(0, 0, 1920, 1080),
                DpiScale: 1.0),
        };
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 8, 12, 5, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-startup-warmup", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "reduced start");

        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(ScreenShareTransportTuningLevel.BandwidthReduced, fakeSource.LastTransportTuningLevel);

        clock.Advance(TimeSpan.FromSeconds(4));
        fakeSource.RaiseFrame(
            CreateTransportFrameEventArgs(
                1280,
                720,
                new byte[] { 1, 2, 3 },
                capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            PendingRawFrameCount: 0,
            OldestPendingRawFrameAgeMs: 0,
            LastCaptureToEncodeStartAgeMs: 0,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 30,
            SupersededPendingRawFrameCount: 9));

        await Task.Delay(50);
        for (var i = 0; i < 5; i++)
        {
            SendHealthyRemotePressure(coordinator);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(8, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(ScreenShareTransportTuningLevel.Normal, fakeSource.LastTransportTuningLevel);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_AdvisoryBridgeHealth_DoesNotBlockPromotionToNormal()
    {
        using var autoTuneFlag = new EnvironmentOverride("NLINK_FEATURE_SCREENCAP_TRANSPORT_AUTOTUNE", "1");
        var fakeSource = new AdaptiveFakeScreenCaptureSource
        {
            CaptureMetadata = new ScreenCaptureMetadata(
                DisplayId: "primary",
                CaptureRegionPx: new ScreenCapturePixelRect(0, 0, 1920, 1080),
                DpiScale: 1.0),
        };
        var backpressureProbe = new FakeScreenShareBackpressureProbe
        {
            RecentHealthIssueCount = 2,
        };
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 13, 20, 15, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock,
            transportBackpressureProbeResolver: () => backpressureProbe);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-advisory-bridge-health", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "advisory bridge health start");

        clock.Advance(TimeSpan.FromSeconds(4));
        fakeSource.RaiseFrame(
            CreateTransportFrameEventArgs(
                1280,
                720,
                new byte[] { 1, 2, 3 },
                capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            PendingRawFrameCount: 0,
            OldestPendingRawFrameAgeMs: 0,
            LastCaptureToEncodeStartAgeMs: 0,
            LastEncodeDurationMs: 16,
            LastEncodeTotalDurationMs: 28,
            SupersededPendingRawFrameCount: 12));

        await Task.Delay(50);
        for (var i = 0; i < 5; i++)
        {
            SendHealthyRemotePressure(coordinator);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(8, fakeSource.LastCaptureFrameRateHint);
        Assert.Contains("bridge_health_kind=advisory", LocalOperationalLog.GetRecentLogText(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_NormalMode_RequiresTwoConsecutivePressureTicksToDemoteToReduced()
    {
        using var autoTuneFlag = new EnvironmentOverride("NLINK_FEATURE_SCREENCAP_TRANSPORT_AUTOTUNE", "1");
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 8, 12, 7, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-normal-demotion", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "normal demotion start");

        DisableStartupWarmupForCoordinatorOnly(coordinator);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 30));

        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(8, fakeSource.LastCaptureFrameRateHint);

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 20,
            LastEncodeTotalDurationMs: 105));

        clock.Advance(TimeSpan.FromMilliseconds(500));
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 90 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));
        await Task.Delay(20);
        autoTuneTick.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(8, fakeSource.LastCaptureFrameRateHint);

        clock.Advance(TimeSpan.FromMilliseconds(500));
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 91 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));
        await Task.Delay(20);
        autoTuneTick.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ReducedMode_UsesProfileRelativeEncodeBudgetForPromotion()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 13, 21, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-reduced-promotion-budget", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "reduced promotion budget start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 60,
            CurrentStreamEpoch: 1));

        for (var i = 0; i < 3; i++)
        {
            SendHealthyRemotePressure(coordinator);
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(40 + i), 2, 3 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
                    streamEpoch: 1));
            await Task.Delay(20);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(8, fakeSource.LastCaptureFrameRateHint);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ReducedMode_UsesReportedHelperApplyCountForPromotion()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 14, 13, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-reduced-promotion-helper-apply-count", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "reduced promotion helper apply count start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 60,
            CurrentStreamEpoch: 1));

        SendHealthyRemotePressure(
            coordinator,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 3,
            currentEpochNeedMoreInputCount: 0);

        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(48 + i), 2, 3 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
                    streamEpoch: 1));
            await Task.Delay(20);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(8, fakeSource.LastCaptureFrameRateHint);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ReducedMode_UsesStableVisibleProgressProofForPromotion()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-reduced-promotion-stable-progress", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "reduced promotion stable-progress start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 60,
            CurrentStreamEpoch: 1));

        for (var i = 0; i < 6; i++)
        {
            SendHealthyRemotePressure(
                coordinator,
                currentEpochWarmupActive: false,
                currentEpochApplyCount: 2,
                currentEpochNeedMoreInputCount: 0,
                steadyVisibleProgressActive: true,
                stableVisibleHeadFrameId: 12 + i,
                framesAppliedSinceLastGap: 8 + i);
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(80 + i), 2, 3 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
                    streamEpoch: 1));
            await Task.Delay(20);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(8, fakeSource.LastCaptureFrameRateHint);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_PostReceiptContinuityLossStaleBlockers_DoNotBlockPromotion()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 21, 19, 45, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-post-receipt-stale-blockers", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "post receipt stale blockers start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        typeof(TransportScreenShareCoordinator)
            .GetField("senderFreshnessMode", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(coordinator, ScreenShareSenderFreshnessMode.Reduced);

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 600,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryBurstStreamEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 0x51, 0x52, 0x53 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: recoveryBurstStreamEpoch,
            isKeyFrame: true));

        await WaitUntilAsync(
            () => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0,
            TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");

        DeliverMatchingHelperVisibleReceipt(
            coordinator,
            "session-post-receipt-stale-blockers",
            recoveryBurstStreamEpoch,
            recoveryOwnerFrameId);

        SetPrivateFieldValue(coordinator, "recoveryLockActive", true);
        SetPrivateFieldValue(coordinator, "recoveryLockStreamEpoch", recoveryBurstStreamEpoch);
        SetPrivateFieldValue(coordinator, "recoveryLockStartedUtc", clock.UtcNow - TimeSpan.FromMilliseconds(100));

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: recoveryBurstStreamEpoch,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.None,
            ScreenSharePressureProtocol.PressureReasonContinuityLoss,
            observedFrameAgeMs: 0,
            recentStaleDrops: 0,
            sentAtUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 3,
            currentEpochNeedMoreInputCount: 0,
            lastVisibleApplyFrameId: recoveryOwnerFrameId,
            visibleHeadFrameId: recoveryOwnerFrameId,
            appliedHeadFrameId: recoveryOwnerFrameId,
            steadyVisibleProgressActive: true,
            stableVisibleHeadFrameId: recoveryOwnerFrameId,
            framesAppliedSinceLastGap: 3,
            visibleRecoveryFloorFrameId: recoveryOwnerFrameId,
            currentEpochRecoveryKeyframeApplyCount: 1);

        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(96 + i), 2, 3 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
                    streamEpoch: recoveryBurstStreamEpoch));
            await Task.Delay(20);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.True(GetPrivateFieldValue<long>(coordinator, "postReceiptBlockerSuppressedCount") > 0);
        Assert.Contains(
            "helper_pressure",
            GetPrivateFieldValue<string>(coordinator, "lastPostReceiptBlockerSuppressedSet"),
            StringComparison.Ordinal);
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "promotionBlockerHelperPressureTicks"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "promotionBlockerHelperApplyCountTicks"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "promotionBlockerRecoveryLockTicks"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_PostReceiptSuppression_RequiresCurrentHelperEvidence()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 21, 19, 46, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-post-receipt-suppression-needs-evidence", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "post receipt suppression needs evidence start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        typeof(TransportScreenShareCoordinator)
            .GetField("senderFreshnessMode", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(coordinator, ScreenShareSenderFreshnessMode.Reduced);

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 700,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryBurstStreamEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 0x61, 0x62, 0x63 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: recoveryBurstStreamEpoch,
            isKeyFrame: true));

        await WaitUntilAsync(
            () => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0,
            TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");

        DeliverMatchingHelperVisibleReceipt(
            coordinator,
            "session-post-receipt-suppression-needs-evidence",
            recoveryBurstStreamEpoch,
            recoveryOwnerFrameId);

        SetPrivateFieldValue(coordinator, "helperCurrentEpochApplyCount", 0);
        SetPrivateFieldValue(coordinator, "helperFramesAppliedSinceLastGap", 0L);
        SetPrivateFieldValue(coordinator, "helperLastVisibleApplyFrameId", -1L);
        SetPrivateFieldValue(coordinator, "helperVisibleHeadFrameId", -1L);
        SetPrivateFieldValue(coordinator, "helperVisibleRecoveryFloorFrameId", -1L);
        SetPrivateFieldValue(coordinator, "recoveryLockActive", true);
        SetPrivateFieldValue(coordinator, "recoveryLockStreamEpoch", recoveryBurstStreamEpoch);

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: recoveryBurstStreamEpoch,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.None,
            ScreenSharePressureProtocol.PressureReasonContinuityLoss,
            observedFrameAgeMs: 0,
            recentStaleDrops: 0,
            sentAtUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 0,
            currentEpochNeedMoreInputCount: 0,
            lastVisibleApplyFrameId: null,
            visibleHeadFrameId: null,
            appliedHeadFrameId: null,
            steadyVisibleProgressActive: null,
            stableVisibleHeadFrameId: null,
            framesAppliedSinceLastGap: null,
            visibleRecoveryFloorFrameId: null,
            currentEpochRecoveryKeyframeApplyCount: null);

        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(112 + i), 2, 3 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
                    streamEpoch: recoveryBurstStreamEpoch));
            await Task.Delay(20);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "postReceiptBlockerSuppressedCount"));
        Assert.True(GetPrivateFieldValue<long>(coordinator, "promotionBlockerHelperPressureTicks") > 0);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_HealthyMessagesWithoutTransientProof_DoNotEraseStickyStableProgress()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 10, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-sticky-proof", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "sticky proof start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 60,
            CurrentStreamEpoch: 1));

        SendHealthyRemotePressure(
            coordinator,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 2,
            currentEpochNeedMoreInputCount: 0,
            steadyVisibleProgressActive: true,
            stableVisibleHeadFrameId: 12,
            framesAppliedSinceLastGap: 8);

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "helperSteadyVisibleProgressActive"));
        Assert.Equal(12L, GetPrivateFieldValue<long>(coordinator, "helperStableVisibleHeadFrameId"));
        Assert.Equal(8L, GetPrivateFieldValue<long>(coordinator, "helperFramesAppliedSinceLastGap"));

        SendHealthyRemotePressure(
            coordinator,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 2,
            currentEpochNeedMoreInputCount: 0);

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "helperSteadyVisibleProgressActive"));
        Assert.Equal(12L, GetPrivateFieldValue<long>(coordinator, "helperStableVisibleHeadFrameId"));
        Assert.Equal(8L, GetPrivateFieldValue<long>(coordinator, "helperFramesAppliedSinceLastGap"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_NonHealthyPressure_WithProvenFacts_DoesNotRetainLegacySenderSideHelperProof()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 15, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-sticky-proof-reset", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "sticky proof reset start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 60,
            CurrentStreamEpoch: 1));

        SendHealthyRemotePressure(
            coordinator,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 2,
            currentEpochNeedMoreInputCount: 0,
            steadyVisibleProgressActive: true,
            stableVisibleHeadFrameId: 15,
            framesAppliedSinceLastGap: 9);

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.ReduceFps,
            ScreenSharePressureProtocol.PressureReasonHighFrameAge,
            observedFrameAgeMs: 650,
            recentStaleDrops: 0,
            sentAtUtcMs: 0,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 2,
            currentEpochNeedMoreInputCount: 0,
            steadyVisibleProgressActive: null,
            stableVisibleHeadFrameId: null,
            framesAppliedSinceLastGap: null);

        Assert.Equal(ScreenShareRemotePressureMode.ReduceFps, GetPrivateFieldValue<ScreenShareRemotePressureMode>(coordinator, "remotePressureMode"));
        Assert.Equal(ScreenSharePressureProtocol.PressureReasonHighFrameAge, GetPrivateFieldValue<string>(coordinator, "remotePressureReason"));
        Assert.False(GetPrivateFieldValue<bool>(coordinator, "remoteHelperFactHealthyActive"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ContinuityLossPressure_WithFramesAppliedProof_ActivatesSenderSideHelperFactHealth()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 21, 7, 55, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-continuity-fact-health", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "continuity fact health start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 60,
            CurrentStreamEpoch: 1));

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.None,
            ScreenSharePressureProtocol.PressureReasonContinuityLoss,
            observedFrameAgeMs: 0,
            recentStaleDrops: 0,
            sentAtUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            currentEpochWarmupActive: true,
            currentEpochApplyCount: 4,
            currentEpochNeedMoreInputCount: 0,
            lastVisibleApplyFrameId: 7,
            appliedHeadFrameId: 7,
            steadyVisibleProgressActive: false,
            stableVisibleHeadFrameId: 7,
            framesAppliedSinceLastGap: 4);

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "helperSteadyVisibleProgressActive"));
        Assert.False(GetPrivateFieldValue<bool>(coordinator, "helperCurrentEpochWarmupActive"));
        Assert.Equal(4L, GetPrivateFieldValue<long>(coordinator, "helperFramesAppliedSinceLastGap"));
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "remoteHelperFactHealthyActive"));
        Assert.Equal("frames_applied_since_last_gap", GetPrivateFieldValue<string>(coordinator, "remoteHelperFactHealthySource"));
        Assert.Equal(7L, GetPrivateFieldValue<long>(coordinator, "remoteHelperFactProofFrameId"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RemoteHelperFactHealth_ClearsOnNewEpoch()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 21, 8, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-remote-helper-fact-epoch-reset", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "remote helper fact epoch reset start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 60,
            CurrentStreamEpoch: 1));

        SendHealthyRemotePressure(
            coordinator,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 2,
            currentEpochNeedMoreInputCount: 0,
            steadyVisibleProgressActive: true,
            stableVisibleHeadFrameId: 15,
            framesAppliedSinceLastGap: 9);

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 60,
            CurrentStreamEpoch: 2));

        SendHealthyRemotePressure(
            coordinator,
            currentEpochWarmupActive: true,
            currentEpochApplyCount: 0,
            currentEpochNeedMoreInputCount: 0);

        Assert.False(GetPrivateFieldValue<bool>(coordinator, "remoteHelperFactHealthyActive"));
        Assert.Equal("new_stream_epoch", GetPrivateFieldValue<string>(coordinator, "remoteHelperFactHealthyClearReason"));
        Assert.True(GetPrivateFieldValue<long>(coordinator, "remoteHelperFactHealthyClearCount") >= 0);
        Assert.False(GetPrivateFieldValue<bool>(coordinator, "helperSteadyVisibleProgressActive"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "helperFramesAppliedSinceLastGap"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RemoteHelperFactHealth_ClearsAfterNoProgressStall()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 21, 8, 5, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-remote-helper-fact-stall-clear", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "remote helper fact stall clear start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 60,
            CurrentStreamEpoch: 1));

        SendHealthyRemotePressure(
            coordinator,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 2,
            currentEpochNeedMoreInputCount: 0,
            lastVisibleApplyFrameId: 15,
            appliedHeadFrameId: 15,
            steadyVisibleProgressActive: true,
            stableVisibleHeadFrameId: 15,
            framesAppliedSinceLastGap: 9);

        clock.Advance(TimeSpan.FromMilliseconds(1600));
        SendHealthyRemotePressure(
            coordinator,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 2,
            currentEpochNeedMoreInputCount: 0);

        Assert.False(GetPrivateFieldValue<bool>(coordinator, "remoteHelperFactHealthyActive"));
        Assert.Equal("no_progress_stall", GetPrivateFieldValue<string>(coordinator, "remoteHelperFactHealthyClearReason"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "remoteHelperFactHealthyClearCount"));
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "helperSteadyVisibleProgressActive"));
        Assert.Equal(9L, GetPrivateFieldValue<long>(coordinator, "helperFramesAppliedSinceLastGap"));
        Assert.Equal(15L, GetPrivateFieldValue<long>(coordinator, "acknowledgedHelperHeadFrameId"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "satisfiedRecoveryFloorFrameId"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RemoteHelperFactHealth_RefreshMessagesRemainAdvisoryOnly()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 21, 8, 10, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-remote-helper-fact-refresh-hold", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "remote helper fact refresh hold start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 60,
            CurrentStreamEpoch: 1));

        SendHealthyRemotePressure(
            coordinator,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 2,
            currentEpochNeedMoreInputCount: 0,
            steadyVisibleProgressActive: true,
            stableVisibleHeadFrameId: 15,
            framesAppliedSinceLastGap: 9);

        clock.Advance(TimeSpan.FromMilliseconds(900));
        SendHealthyRemotePressure(
            coordinator,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 2,
            currentEpochNeedMoreInputCount: 0,
            steadyVisibleProgressActive: true,
            stableVisibleHeadFrameId: 15,
            framesAppliedSinceLastGap: 9);

        Assert.False(GetPrivateFieldValue<bool>(coordinator, "remoteHelperFactHealthyActive"));

        clock.Advance(TimeSpan.FromMilliseconds(900));
        SendHealthyRemotePressure(
            coordinator,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 2,
            currentEpochNeedMoreInputCount: 0,
            steadyVisibleProgressActive: true,
            stableVisibleHeadFrameId: 15,
            framesAppliedSinceLastGap: 9);

        Assert.False(GetPrivateFieldValue<bool>(coordinator, "remoteHelperFactHealthyActive"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ReducedMode_OverBudgetEncode_DoesNotPromote()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 13, 21, 5, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-reduced-promotion-over-budget", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "reduced promotion over-budget start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 22,
            LastEncodeTotalDurationMs: 80,
            CurrentStreamEpoch: 1));

        for (var i = 0; i < 4; i++)
        {
            SendHealthyRemotePressure(coordinator);
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(60 + i), 4, 5 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
                    streamEpoch: 1));
            await Task.Delay(20);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);
        var overBudgetMetrics = coordinator.GetMetricsSnapshot();
        Assert.True(overBudgetMetrics.PromotionBlockerEncodeBudgetTicks >= 1);
        Assert.True(GetPrivateFieldValue<long>(coordinator, "promotionEncodeSoftSpikeCount") >= 1);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ReducedMode_StableVisibleProgressProof_DoesNotBypassEncodeBudget()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 5, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-reduced-promotion-stable-progress-over-budget", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "reduced promotion stable-progress over-budget start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 22,
            LastEncodeTotalDurationMs: 80,
            CurrentStreamEpoch: 1));

        for (var i = 0; i < 4; i++)
        {
            SendHealthyRemotePressure(
                coordinator,
                currentEpochWarmupActive: false,
                currentEpochApplyCount: 2,
                currentEpochNeedMoreInputCount: 0,
                steadyVisibleProgressActive: true,
                stableVisibleHeadFrameId: 20 + i,
                framesAppliedSinceLastGap: 8 + i);
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(96 + i), 4, 5 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
                    streamEpoch: 1));
            await Task.Delay(20);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
        Assert.True(coordinator.GetMetricsSnapshot().PromotionBlockerEncodeBudgetTicks >= 1);
        Assert.True(GetPrivateFieldValue<long>(coordinator, "promotionBlockedByEncodeBudgetAloneCount") >= 1);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ReducedMode_SingleModerateEncodeSpike_DoesNotResetHealthyTicks()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 10, 30, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-reduced-promotion-soft-encode-spike", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "reduced promotion soft encode-spike start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);

        var encodeTotals = new[] { 80, 60, 60 };
        for (var i = 0; i < encodeTotals.Length; i++)
        {
            fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
                LastEncodeDurationMs: 18,
                LastEncodeTotalDurationMs: encodeTotals[i],
                CurrentStreamEpoch: 1));
            SendHealthyRemotePressure(
                coordinator,
                currentEpochWarmupActive: false,
                currentEpochApplyCount: 2,
                currentEpochNeedMoreInputCount: 0,
                steadyVisibleProgressActive: true,
                stableVisibleHeadFrameId: 20 + i,
                framesAppliedSinceLastGap: 8 + i);
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(112 + i), 7, 8 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
                    streamEpoch: 1));
            await Task.Delay(20);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(8, fakeSource.LastCaptureFrameRateHint);
        Assert.True(GetPrivateFieldValue<long>(coordinator, "promotionEncodeSoftSpikeCount") >= 1);
        Assert.True(GetPrivateFieldValue<long>(coordinator, "promotionEncodeSoftSpikeResetSuppressedCount") >= 1);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ReducedMode_PacedCaptureAgeBudget_AllowsPromotionAt180Ms()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 14, 18, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-reduced-promotion-capture-age-allow", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "reduced promotion capture-age allow start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 60,
            CurrentStreamEpoch: 1));

        for (var i = 0; i < 3; i++)
        {
            SetLastCaptureToSendAgeMs(coordinator, 180);
            SendHealthyRemotePressure(coordinator);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(8, fakeSource.LastCaptureFrameRateHint);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ReducedMode_PacedCaptureAgeBudget_BlocksPromotionAt260Ms()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 14, 18, 5, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-reduced-promotion-capture-age-block", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "reduced promotion capture-age block start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 60,
            CurrentStreamEpoch: 1));

        for (var i = 0; i < 4; i++)
        {
            SetLastCaptureToSendAgeMs(coordinator, 260);
            SendHealthyRemotePressure(coordinator);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_HealthyPressureWithoutExplicitApplyCount_DoesNotAdvanceHelperApplyCount()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 17, 10, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-helper-apply-count-authoritative", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "helper apply count authoritative start");

        DisableStartupWarmupForCoordinatorOnly(coordinator);
        SetPrivateFieldValue(coordinator, "helperCurrentEpochApplyCount", 1);

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.None,
            ScreenSharePressureProtocol.PressureReasonHealthy,
            observedFrameAgeMs: 220,
            recentStaleDrops: 0,
            sentAtUtcMs: 0,
            currentEpochWarmupActive: true,
            currentEpochApplyCount: null,
            currentEpochNeedMoreInputCount: 0);

        Assert.Equal(1, GetPrivateFieldValue<int>(coordinator, "helperCurrentEpochApplyCount"));
        Assert.Equal(1, GetPrivateFieldValue<int>(coordinator, "helperCurrentEpochHealthySignalCount"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ReducedMode_RawRemoteAgeSpike_DoesNotResetHealthyPromotion()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 13, 21, 10, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-reduced-raw-age-spike", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "reduced raw-age spike start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 60,
            CurrentStreamEpoch: 1));

        SendHealthyRemotePressure(coordinator);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(1280, 720, new byte[] { 70, 2, 3 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: 1));
        await Task.Delay(20);
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

        SendHealthyRemotePressure(coordinator, observedFrameAgeMs: 700);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(1280, 720, new byte[] { 71, 2, 3 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: 1));
        await Task.Delay(20);
        autoTuneTick.Invoke(coordinator, Array.Empty<object>());

        SendHealthyRemotePressure(coordinator);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(1280, 720, new byte[] { 72, 2, 3 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: 1));
        await Task.Delay(20);
        autoTuneTick.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ReducedMode_BlocksPromotionUntilHelperWarmupCompletes()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 13, 21, 15, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-helper-warmup-block", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "helper warmup promotion block start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 60,
            CurrentStreamEpoch: 1));

        for (var i = 0; i < 4; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(80 + i), 6, 7 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
                    streamEpoch: 1));
            await Task.Delay(20);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Contains("helper_warmup", LocalOperationalLog.GetRecentLogText(), StringComparison.Ordinal);
        var helperWarmupMetrics = coordinator.GetMetricsSnapshot();
        Assert.True(helperWarmupMetrics.PromotionBlockerHelperWarmupTicks >= 1);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ReducedMode_SlowApplyCadencePressure_DoesNotPromote()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 14, 12, 10, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-reduced-slow-cadence-block", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "reduced slow cadence block start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 60,
            CurrentStreamEpoch: 1));

        for (var i = 0; i < 4; i++)
        {
            coordinator.SetRemotePressureState(
                ScreenShareRemotePressureMode.None,
                ScreenSharePressureProtocol.PressureReasonSlowApplyCadence,
                observedFrameAgeMs: 180,
                recentStaleDrops: 0);
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(90 + i), 2, 3 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
                    streamEpoch: 1));
            await Task.Delay(20);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Contains("helper_pressure", LocalOperationalLog.GetRecentLogText(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_NormalMode_UsesProfileRelativeEncodeBudgetForDemotion()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 13, 21, 20, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-normal-demotion-budget", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "normal demotion budget start");

        DisableStartupWarmupForCoordinatorOnly(coordinator);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 20,
            LastEncodeTotalDurationMs: 95,
            CurrentStreamEpoch: 1));

        for (var i = 0; i < 2; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(90 + i), 8, 9 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
                    streamEpoch: 1));
            await Task.Delay(20);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 24,
            LastEncodeTotalDurationMs: 105,
            CurrentStreamEpoch: 1));

        for (var i = 0; i < 2; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(100 + i), 10, 11 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
                    streamEpoch: 1));
            await Task.Delay(20);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ReducedToNormalProfileTransition_StartsGrace_PurgesBacklog_AndRequestsSingleKeyframe()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 13, 20, 15, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-transition-grace-start", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "transition grace start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            PendingRawFrameCount: 2,
            OldestPendingRawFrameAgeMs: 25,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 30,
            CurrentStreamEpoch: 1));

        for (var i = 0; i < 3; i++)
        {
            SendHealthyRemotePressure(coordinator);
            var chunksSentBefore = coordinator.GetMetricsSnapshot().ChunksSent;
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(150 + i), 4, 5 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
                    streamEpoch: fakeSource.GetFreshnessMetricsSnapshot().CurrentStreamEpoch));
            await WaitUntilAsync(
                () =>
                {
                    var metrics = coordinator.GetMetricsSnapshot();
                    return metrics.ChunksSent > chunksSentBefore && metrics.LastCaptureToSendAgeMs >= 0;
                },
                TimeSpan.FromSeconds(2));
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(8, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(ScreenShareTransportTuningLevel.Normal, fakeSource.LastTransportTuningLevel);
        Assert.Equal(1, fakeSource.PurgePendingRawFramesCallCount);
        Assert.Equal(
            1,
            fakeSource.KeyFrameRequestReasons.Count(static reason => string.Equals(reason, "sender_profile_transition", StringComparison.Ordinal)));
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "transitionActive"));
        Assert.True(GetPrivateFieldValue<long>(coordinator, "transitionStreamEpoch") > 0);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ProfileTransitionGrace_BlocksRemoteAgeOnlyDemotion()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 13, 20, 25, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-transition-grace-block", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "transition grace block start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 30,
            CurrentStreamEpoch: 1));

        for (var i = 0; i < 3; i++)
        {
            SendHealthyRemotePressure(coordinator);
            var chunksSentBefore = coordinator.GetMetricsSnapshot().ChunksSent;
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(170 + i), 2, 3 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
                    streamEpoch: fakeSource.GetFreshnessMetricsSnapshot().CurrentStreamEpoch));
            await WaitUntilAsync(
                () =>
                {
                    var metrics = coordinator.GetMetricsSnapshot();
                    return metrics.ChunksSent > chunksSentBefore && metrics.LastCaptureToSendAgeMs >= 0;
                },
                TimeSpan.FromSeconds(2));
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "transitionActive"));

        for (var i = 0; i < 2; i++)
        {
            SendHealthyRemotePressure(coordinator, observedFrameAgeMs: 700);
            autoTuneTick.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(8, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(ScreenShareTransportTuningLevel.Normal, fakeSource.LastTransportTuningLevel);
        Assert.Equal(
            1,
            fakeSource.KeyFrameRequestReasons.Count(static reason => string.Equals(reason, "sender_profile_transition", StringComparison.Ordinal)));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ProfileTransitionGrace_AllowsSevereCongestionDemotion()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var backpressureProbe = new FakeScreenShareBackpressureProbe();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 13, 20, 35, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock,
            transportBackpressureProbeResolver: () => backpressureProbe);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-transition-grace-severe", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "transition grace severe start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 30,
            CurrentStreamEpoch: 1));

        for (var i = 0; i < 3; i++)
        {
            SendHealthyRemotePressure(coordinator);
            var chunksSentBefore = coordinator.GetMetricsSnapshot().ChunksSent;
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(190 + i), 8, 9 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
                    streamEpoch: fakeSource.GetFreshnessMetricsSnapshot().CurrentStreamEpoch));
            await WaitUntilAsync(
                () =>
                {
                    var metrics = coordinator.GetMetricsSnapshot();
                    return metrics.ChunksSent > chunksSentBefore && metrics.LastCaptureToSendAgeMs >= 0;
                },
                TimeSpan.FromSeconds(2));
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "transitionActive"));

        backpressureProbe.IsSeverelyCongested = true;
        backpressureProbe.QueueDepth = 18;
        backpressureProbe.QueuedBytes = 256 * 1024;
        backpressureProbe.RecentDropCount = 2;
        autoTuneTick.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal("catch_up", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(3, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(ScreenShareTransportTuningLevel.BandwidthReduced, fakeSource.LastTransportTuningLevel);
        Assert.Equal(
            2,
            fakeSource.KeyFrameRequestReasons.Count(static reason => string.Equals(reason, "sender_profile_transition", StringComparison.Ordinal)));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RemotePressureState_OverridesWarmupAndRestoresOnHealthyState()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: new FakeScreenShareClock(new DateTimeOffset(2026, 3, 8, 12, 10, 0, TimeSpan.Zero)));

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-remote-pressure", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "remote pressure start");

        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.CatchUpOnly,
            "high_frame_age",
            observedFrameAgeMs: 1800,
            recentStaleDrops: 4);

        Assert.Equal(3, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(ScreenShareTransportTuningLevel.BandwidthReduced, fakeSource.LastTransportTuningLevel);

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.None,
            "healthy",
            observedFrameAgeMs: 0,
            recentStaleDrops: 0);

        var remotePressureMode = (ScreenShareRemotePressureMode)typeof(TransportScreenShareCoordinator)
            .GetField("remotePressureMode", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(coordinator)!;
        Assert.Equal(ScreenShareRemotePressureMode.None, remotePressureMode);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_LocalAdvisoryBridgeHealth_DoesNotIgnoreHealthyRemotePressureState()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var backpressureProbe = new FakeScreenShareBackpressureProbe
        {
            RecentHealthIssueCount = 2,
        };
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: new FakeScreenShareClock(new DateTimeOffset(2026, 4, 13, 20, 20, 0, TimeSpan.Zero)),
            transportBackpressureProbeResolver: () => backpressureProbe);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-advisory-health-remote-none", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "advisory bridge health remote-none start");

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.CatchUpOnly,
            "high_frame_age",
            observedFrameAgeMs: 1800,
            recentStaleDrops: 4);

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.None,
            "healthy",
            observedFrameAgeMs: 0,
            recentStaleDrops: 0);

        var remotePressureMode = (ScreenShareRemotePressureMode)typeof(TransportScreenShareCoordinator)
            .GetField("remotePressureMode", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(coordinator)!;
        Assert.Equal(ScreenShareRemotePressureMode.None, remotePressureMode);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_LocalLaneCongestion_ReducesFpsBeforeRemotePressure()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var backpressureProbe = new FakeScreenShareBackpressureProbe
        {
            IsCongested = true,
            QueueDepth = 14,
            QueuedBytes = 320 * 1024,
        };
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 8, 12, 20, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock,
            transportBackpressureProbeResolver: () => backpressureProbe);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-local-lane-congestion", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "local lane congestion start");
        DisableStartupWarmupForCoordinatorOnly(coordinator);

        fakeSource.RaiseFrame(
            CreateTransportFrameEventArgs(
                1280,
                720,
                new byte[] { 1, 2, 3 },
                capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));

        await Task.Delay(50);
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(ScreenShareTransportTuningLevel.BandwidthReduced, fakeSource.LastTransportTuningLevel);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_SevereLocalLaneCongestion_EntersDegradedModeImmediately()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var backpressureProbe = new FakeScreenShareBackpressureProbe
        {
            IsCongested = true,
            IsSeverelyCongested = true,
            QueueDepth = 22,
            QueuedBytes = 512 * 1024,
            RecentDropCount = 2,
        };
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 8, 12, 25, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock,
            transportBackpressureProbeResolver: () => backpressureProbe);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-severe-local-lane-congestion", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "severe local lane congestion start");
        DisableStartupWarmupForCoordinatorOnly(coordinator);

        fakeSource.RaiseFrame(
            CreateTransportFrameEventArgs(
                1280,
                720,
                new byte[] { 1, 2, 3 },
                capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));

        await Task.Delay(50);
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal(3, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(ScreenShareTransportTuningLevel.BandwidthReduced, fakeSource.LastTransportTuningLevel);
        Assert.Equal("catch_up", coordinator.GetMetricsSnapshot().FreshnessMode);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RemoteReduceFpsWithRepeatedStaleDrops_StaysInDegradedMode()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 2, 12, 40, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-remote-stale-burst", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "remote stale burst start");
        DisableStartupWarmupForCoordinatorOnly(coordinator);

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.ReduceFps,
            ScreenSharePressureProtocol.PressureReasonRepeatedStaleDrops,
            observedFrameAgeMs: 320,
            recentStaleDrops: 12);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

        Assert.InRange(fakeSource.LastCaptureFrameRateHint, 5, 8);
        Assert.Equal(ScreenShareTransportTuningLevel.Normal, fakeSource.LastTransportTuningLevel);
        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RemoteReduceFpsHighFrameAgeWithHelperEvidence_EntersCatchUpAfterTwoTicks()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 22, 9, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-remote-high-frame-age-catchup", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "remote high frame age catch-up start");
        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 8);

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 21,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.ReduceFps,
            ScreenSharePressureProtocol.PressureReasonHighFrameAge,
            observedFrameAgeMs: 450,
            recentStaleDrops: 0,
            sentAtUtcMs: 0,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 5,
            currentEpochNeedMoreInputCount: 0,
            lastVisibleApplyFrameId: 4,
            visibleHeadFrameId: 4,
            appliedHeadFrameId: 4,
            steadyVisibleProgressActive: true,
            stableVisibleHeadFrameId: 4,
            framesAppliedSinceLastGap: 5);

        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
        typeof(TransportScreenShareCoordinator)
            .GetField("remoteHighFrameAgeCatchUpEntryConsecutiveTicks", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(coordinator, 0);

        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "senderCatchUpEnteredDueToRemoteHighFrameAgeCount"));
        Assert.Equal(1, GetPrivateFieldValue<int>(coordinator, "remoteHighFrameAgeCatchUpEntryConsecutiveTicks"));

        clock.Advance(TimeSpan.FromSeconds(1));
        autoTuneTick.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal("catch_up", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(3, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "senderCatchUpEnteredDueToRemoteHighFrameAgeCount"));
        Assert.InRange(GetPrivateFieldValue<int>(coordinator, "remoteHighFrameAgeCatchUpEntryConsecutiveTicks"), 2, int.MaxValue);
        Assert.DoesNotContain("catch_up_mode_enter", fakeSource.KeyFrameRequestReasons);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RemoteReduceFpsHighFrameAgeWithoutHelperEvidence_DoesNotEnterCatchUp()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 22, 9, 5, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-remote-high-frame-age-no-proof", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "remote high frame age without helper evidence start");
        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 8);

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 22,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.ReduceFps,
            ScreenSharePressureProtocol.PressureReasonHighFrameAge,
            observedFrameAgeMs: 450,
            recentStaleDrops: 0,
            sentAtUtcMs: 0,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 0,
            currentEpochNeedMoreInputCount: 0,
            lastVisibleApplyFrameId: null,
            visibleHeadFrameId: null,
            appliedHeadFrameId: null,
            steadyVisibleProgressActive: false,
            stableVisibleHeadFrameId: null,
            framesAppliedSinceLastGap: 0);

        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        clock.Advance(TimeSpan.FromSeconds(1));
        autoTuneTick.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "senderCatchUpEnteredDueToRemoteHighFrameAgeCount"));
        Assert.Equal(0, GetPrivateFieldValue<int>(coordinator, "remoteHighFrameAgeCatchUpEntryConsecutiveTicks"));
        Assert.True(GetPrivateFieldValue<long>(coordinator, "remoteHighFrameAgeCatchUpSuppressedDueToMissingHelperEvidenceCount") > 0);
        Assert.Equal("missing_helper_evidence", GetPrivateFieldValue<string>(coordinator, "lastRemoteHighFrameAgeCatchUpSuppressionReason"));

        var logText = File.ReadAllText(LocalOperationalLog.LogFilePath);
        Assert.Contains("event=screenshare_sender_auto_tune_decision", logText, StringComparison.Ordinal);
        Assert.Contains("session_id=session-remote-high-frame-age-no-proof", logText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RemoteReduceFpsHighFrameAgeDuringActiveRecovery_DoesNotEnterCatchUp()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 22, 9, 10, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-remote-high-frame-age-active-recovery", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "remote high frame age during active recovery start");
        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 8);

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 24,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 1, 2, 3 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: recoveryEpoch,
            isKeyFrame: true));

        await WaitUntilAsync(
            () => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0,
            TimeSpan.FromSeconds(2));

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.ReduceFps,
            ScreenSharePressureProtocol.PressureReasonHighFrameAge,
            observedFrameAgeMs: 450,
            recentStaleDrops: 0,
            sentAtUtcMs: 0,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 5,
            currentEpochNeedMoreInputCount: 0,
            lastVisibleApplyFrameId: 4,
            visibleHeadFrameId: 4,
            appliedHeadFrameId: 4,
            steadyVisibleProgressActive: true,
            stableVisibleHeadFrameId: 4,
            framesAppliedSinceLastGap: 5);
        SetPrivateFieldValue(coordinator, "remoteHighFrameAgeCatchUpEntryConsecutiveTicks", 0);

        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        clock.Advance(TimeSpan.FromSeconds(1));
        autoTuneTick.Invoke(coordinator, Array.Empty<object>());

        Assert.NotEqual("catch_up", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "senderCatchUpEnteredDueToRemoteHighFrameAgeCount"));
        Assert.Equal(0, GetPrivateFieldValue<int>(coordinator, "remoteHighFrameAgeCatchUpEntryConsecutiveTicks"));

    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RemoteHighFrameAgeCatchUp_ExitsViaExistingLowPressurePath()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 22, 9, 15, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-remote-high-frame-age-recover", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "remote high frame age exit start");
        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 8);

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 25,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.ReduceFps,
            ScreenSharePressureProtocol.PressureReasonHighFrameAge,
            observedFrameAgeMs: 450,
            recentStaleDrops: 0,
            sentAtUtcMs: 0,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 5,
            currentEpochNeedMoreInputCount: 0,
            lastVisibleApplyFrameId: 4,
            visibleHeadFrameId: 4,
            appliedHeadFrameId: 4,
            steadyVisibleProgressActive: true,
            stableVisibleHeadFrameId: 4,
            framesAppliedSinceLastGap: 5);

        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        clock.Advance(TimeSpan.FromSeconds(1));
        autoTuneTick.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal("catch_up", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(3, fakeSource.LastCaptureFrameRateHint);

        for (var i = 0; i < 3; i++)
        {
            coordinator.SetRemotePressureState(
                ScreenShareRemotePressureMode.ReduceFps,
                ScreenSharePressureProtocol.PressureReasonHighFrameAge,
                observedFrameAgeMs: 300 - (i * 10),
                recentStaleDrops: 0,
                sentAtUtcMs: 0,
                currentEpochWarmupActive: false,
                currentEpochApplyCount: 6 + i,
                currentEpochNeedMoreInputCount: 0,
                lastVisibleApplyFrameId: 6 + i,
                visibleHeadFrameId: 6 + i,
                appliedHeadFrameId: 6 + i,
                steadyVisibleProgressActive: true,
                stableVisibleHeadFrameId: 6 + i,
                framesAppliedSinceLastGap: 6 + i);
            fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
                CurrentStreamEpoch: 25,
                LastEncodeDurationMs: 18,
                LastEncodeTotalDurationMs: 32));
            clock.Advance(TimeSpan.FromMilliseconds(250));
            fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
                640,
                360,
                new[] { (byte)(30 + i) },
                capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));
            await Task.Delay(20);
            autoTuneTick.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("catch_up", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(3, fakeSource.LastCaptureFrameRateHint);
        Assert.True(GetPrivateFieldValue<long>(coordinator, "catchUpRecoverySuppressedDueToRemoteHighFrameAgeCount") > 0);
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "catchUpExitWhileRemoteHighFrameAgePressureCount"));

        for (var i = 0; i < 3; i++)
        {
            SendHealthyRemotePressure(
                coordinator,
                observedFrameAgeMs: 150,
                recentStaleDrops: 0,
                currentEpochWarmupActive: false,
                currentEpochApplyCount: 9 + i,
                currentEpochNeedMoreInputCount: 0,
                lastVisibleApplyFrameId: 9 + i,
                visibleHeadFrameId: 9 + i,
                appliedHeadFrameId: 9 + i,
                steadyVisibleProgressActive: true,
                stableVisibleHeadFrameId: 9 + i,
                framesAppliedSinceLastGap: 9 + i);
            fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
                CurrentStreamEpoch: 25,
                LastEncodeDurationMs: 18,
                LastEncodeTotalDurationMs: 32));
            clock.Advance(TimeSpan.FromMilliseconds(250));
            fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
                640,
                360,
                new[] { (byte)(40 + i) },
                capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));
            await Task.Delay(20);
            autoTuneTick.Invoke(coordinator, Array.Empty<object>());

            if (string.Equals(coordinator.GetMetricsSnapshot().FreshnessMode, "reduced", StringComparison.Ordinal))
            {
                break;
            }
        }

        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "senderCatchUpEnteredDueToRemoteHighFrameAgeCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "catchUpExitWhileRemoteHighFrameAgePressureCount"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RemoteHighFrameAgeWithRecoveryLock_AllowsCatchUpWithinReducedBand()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 22, 9, 20, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-lock-catchup-allowed", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery lock catch-up allowed start");
        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 26,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        SetPrivateFieldValue(coordinator, "recoveryLockActive", true);
        SetPrivateFieldValue(coordinator, "recoveryLockStreamEpoch", 26L);
        SetPrivateFieldValue(coordinator, "recoveryLockStartedUtc", clock.UtcNow);
        SetPrivateFieldValue(coordinator, "recoveryLockReason", ScreenSharePressureProtocol.PressureReasonContinuityLoss);

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.ReduceFps,
            ScreenSharePressureProtocol.PressureReasonHighFrameAge,
            observedFrameAgeMs: 450,
            recentStaleDrops: 0,
            sentAtUtcMs: 0,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 5,
            currentEpochNeedMoreInputCount: 0,
            lastVisibleApplyFrameId: 4,
            visibleHeadFrameId: 4,
            appliedHeadFrameId: 4,
            steadyVisibleProgressActive: true,
            stableVisibleHeadFrameId: 4,
            framesAppliedSinceLastGap: 5);

        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        if (!string.Equals(coordinator.GetMetricsSnapshot().FreshnessMode, "catch_up", StringComparison.Ordinal))
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            autoTuneTick.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("catch_up", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(3, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "senderCatchUpEnteredDueToRemoteHighFrameAgeCount"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryLockAllowedSameTuningModeChangeCount"));
        Assert.Equal("reduced->catch_up", GetPrivateFieldValue<string>(coordinator, "lastRecoveryLockAllowedSameTuningModeChange"));
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryLockActive"));

        var logText = File.ReadAllText(LocalOperationalLog.LogFilePath);
        Assert.Contains("event=screenshare_recovery_lock_allowed_same_tuning_mode_change", logText, StringComparison.Ordinal);
        Assert.Contains("session_id=session-recovery-lock-catchup-allowed", logText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_CatchUpRecoveryWithRecoveryLock_AllowsReturnToReducedWithinReducedBand()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 22, 9, 25, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-lock-catchup-exit", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery lock catch-up exit start");
        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);

        SetPrivateFieldValue(coordinator, "senderFreshnessMode", ScreenShareSenderFreshnessMode.CatchUp);
        SetPrivateFieldValue(coordinator, "captureFpsHint", 3);
        SetPrivateFieldValue(coordinator, "transportTuningLevel", ScreenShareTransportTuningLevel.BandwidthReduced);
        SetPrivateFieldValue(coordinator, "recoveryLockActive", true);
        SetPrivateFieldValue(coordinator, "recoveryLockStreamEpoch", 27L);
        SetPrivateFieldValue(coordinator, "recoveryLockStartedUtc", clock.UtcNow);
        SetPrivateFieldValue(coordinator, "recoveryLockReason", ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        fakeSource.SetCaptureFrameRateHint(3);
        if (typeof(TransportScreenShareCoordinator)
                .GetField("sendPipeline", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(coordinator) is ScreenShareFrameSendPipeline sendPipeline)
        {
            sendPipeline.SetMaxFramesPerSecond(3);
        }

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 27,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        for (var i = 0; i < 3; i++)
        {
            SendHealthyRemotePressure(
                coordinator,
                observedFrameAgeMs: 150,
                recentStaleDrops: 0,
                currentEpochWarmupActive: false,
                currentEpochApplyCount: 6 + i,
                currentEpochNeedMoreInputCount: 0,
                lastVisibleApplyFrameId: 6 + i,
                visibleHeadFrameId: 6 + i,
                appliedHeadFrameId: 6 + i,
                steadyVisibleProgressActive: true,
                stableVisibleHeadFrameId: 6 + i,
                framesAppliedSinceLastGap: 6 + i);
            clock.Advance(TimeSpan.FromMilliseconds(250));
            fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
                640,
                360,
                new[] { (byte)(60 + i) },
                capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
                streamEpoch: 27));
            await Task.Delay(20);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryLockAllowedSameTuningModeChangeCount"));
        Assert.Equal("catch_up->reduced", GetPrivateFieldValue<string>(coordinator, "lastRecoveryLockAllowedSameTuningModeChange"));
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryLockActive"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryLock_StillBlocksReducedToNormalProfileChange()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 22, 9, 30, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-lock-still-blocks-normal", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery lock still blocks normal start");
        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 28,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 60));

        SetPrivateFieldValue(coordinator, "recoveryLockActive", true);
        SetPrivateFieldValue(coordinator, "recoveryLockStreamEpoch", 28L);
        SetPrivateFieldValue(coordinator, "recoveryLockStartedUtc", clock.UtcNow);
        SetPrivateFieldValue(coordinator, "recoveryLockReason", ScreenSharePressureProtocol.PressureReasonContinuityLoss);

        for (var i = 0; i < 3; i++)
        {
            SendHealthyRemotePressure(
                coordinator,
                observedFrameAgeMs: 0,
                recentStaleDrops: 0,
                currentEpochWarmupActive: false,
                currentEpochApplyCount: 3 + i,
                currentEpochNeedMoreInputCount: 0,
                lastVisibleApplyFrameId: 3 + i,
                visibleHeadFrameId: 3 + i,
                appliedHeadFrameId: 3 + i,
                steadyVisibleProgressActive: true,
                stableVisibleHeadFrameId: 3 + i,
                framesAppliedSinceLastGap: 3 + i);
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
                1280,
                720,
                new byte[] { (byte)(40 + i), 2, 3 },
                capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
                streamEpoch: 28));
            await Task.Delay(20);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryLockAllowedSameTuningModeChangeCount"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RemoteCatchUpOnly_RequestsKeyframeAndPurgesQueuedBacklog()
    {
        var fakeSource = new FakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 12, 12, 0, 0, TimeSpan.Zero));
        var probe = new ScreenShareSendProbe(recentPayloadCapacity: 4, maxInFlight: 1, startBlocked: true);
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: probe.SendReadOnlyPayloadAsync,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-remote-catchup", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "remote catch-up start");
        DisableStartupWarmupForCoordinatorOnly(coordinator);

        RaiseTransportFrame(fakeSource, 640, 360, new byte[] { 1 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds());
        await AwaitCompletesAsync(
            probe.FirstSendStarted,
            TimeSpan.FromSeconds(2),
            "remote catch-up blocked first send");

        for (byte marker = 2; marker <= 5; marker++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(300));
            RaiseTransportFrame(fakeSource, 640, 360, new[] { marker }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds());
        }

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.CatchUpOnly,
            ScreenSharePressureProtocol.PressureReasonHighFrameAge,
            observedFrameAgeMs: 1600,
            recentStaleDrops: 3);
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        clock.Advance(TimeSpan.FromMilliseconds(600));
        RaiseTransportFrame(fakeSource, 640, 360, new byte[] { 6 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds());

        probe.ReleaseBlockedSends();
        await AwaitCompletesAsync(
            probe.WaitForPayloadCountAsync(2, TimeSpan.FromSeconds(2)),
            TimeSpan.FromSeconds(2),
            "remote catch-up freshest payloads");

        var payloads = probe.GetRecentPayloadsSnapshot();
        Assert.Equal(2, payloads.Length);
        var firstFragment = Assert.Single(ExpandFragmentsFromPayload(payloads[0]));
        var secondFragment = Assert.Single(ExpandFragmentsFromPayload(payloads[1]));
        Assert.Equal(new byte[] { 1 }, firstFragment.Data);
        Assert.Equal(new byte[] { 6 }, secondFragment.Data);
        Assert.Contains("remote_catch_up_only", fakeSource.KeyFrameRequestReasons);
        Assert.Equal("catch_up", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.True(coordinator.GetMetricsSnapshot().FramesDropped >= 3);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ContinuityRecoveryLock_BlocksNormalToReducedTransition()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 14, 8, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-lock-block", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery lock block start");

        DisableStartupWarmupForCoordinatorOnly(coordinator);
        fakeSource.SetTransportTuningLevel(ScreenShareTransportTuningLevel.Normal);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 7,
            LastEncodeDurationMs: 22,
            LastEncodeTotalDurationMs: 140));

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.None,
            ScreenSharePressureProtocol.PressureReasonContinuityLoss,
            observedFrameAgeMs: 0,
            recentStaleDrops: 0);

        clock.Advance(TimeSpan.FromMilliseconds(500));
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        clock.Advance(TimeSpan.FromMilliseconds(500));
        autoTuneTick.Invoke(coordinator, Array.Empty<object>());

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryLockActive"));
        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(ScreenShareTransportTuningLevel.Normal, fakeSource.LastTransportTuningLevel);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryLock_IgnoresOutOfOrderNonRecoveryPressure()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 14, 8, 2, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-lock-out-of-order", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery lock out-of-order start");

        DisableStartupWarmupForCoordinatorOnly(coordinator);
        fakeSource.SetTransportTuningLevel(ScreenShareTransportTuningLevel.Normal);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 9,
            LastEncodeDurationMs: 20,
            LastEncodeTotalDurationMs: 36));

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.None,
            ScreenSharePressureProtocol.PressureReasonContinuityLoss,
            observedFrameAgeMs: 0,
            recentStaleDrops: 0,
            sentAtUtcMs: 2_000);
        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.CatchUpOnly,
            ScreenSharePressureProtocol.PressureReasonSlowApplyCadence,
            observedFrameAgeMs: 280,
            recentStaleDrops: 0,
            sentAtUtcMs: 1_500);

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryLockActive"));
        Assert.Equal(ScreenShareRemotePressureMode.None, GetPrivateFieldValue<ScreenShareRemotePressureMode>(coordinator, "remotePressureMode"));
        Assert.Equal(
            ScreenSharePressureProtocol.PressureReasonContinuityLoss,
            GetPrivateFieldValue<string>(coordinator, "remotePressureReason"));
        Assert.DoesNotContain("remote_catch_up_only", fakeSource.KeyFrameRequestReasons);
        Assert.Contains("ignore_reason=stale_recovery_message", LocalOperationalLog.GetRecentLogText(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryLock_ClearsForNewerNonRecoveryPressure()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 14, 8, 3, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-lock-clear", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery lock clear start");

        DisableStartupWarmupForCoordinatorOnly(coordinator);
        fakeSource.SetTransportTuningLevel(ScreenShareTransportTuningLevel.Normal);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 10,
            LastEncodeDurationMs: 20,
            LastEncodeTotalDurationMs: 36));

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.None,
            ScreenSharePressureProtocol.PressureReasonContinuityLoss,
            observedFrameAgeMs: 0,
            recentStaleDrops: 0,
            sentAtUtcMs: 2_000);
        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.ReduceFps,
            ScreenSharePressureProtocol.PressureReasonSlowApplyCadence,
            observedFrameAgeMs: 260,
            recentStaleDrops: 0,
            sentAtUtcMs: 2_500);

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryLockActive"));
        Assert.Equal(ScreenShareRemotePressureMode.ReduceFps, GetPrivateFieldValue<ScreenShareRemotePressureMode>(coordinator, "remotePressureMode"));
        Assert.Equal(
            ScreenSharePressureProtocol.PressureReasonSlowApplyCadence,
            GetPrivateFieldValue<string>(coordinator, "remotePressureReason"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_AcknowledgedHelperProof_DoesNotClearRecoveryLockWithoutReceipt()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 21, 9, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-lock-acknowledged-proof", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery lock acknowledged proof start");

        DisableStartupWarmupForCoordinatorOnly(coordinator);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 40,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.None,
            ScreenSharePressureProtocol.PressureReasonContinuityLoss,
            observedFrameAgeMs: 0,
            recentStaleDrops: 0,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 0,
            currentEpochNeedMoreInputCount: 1);

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryLockActive"));
        Assert.Equal(recoveryEpoch, GetPrivateFieldValue<long>(coordinator, "recoveryLockStreamEpoch"));

        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 0x01, 0x02, 0x03 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: recoveryEpoch,
            isKeyFrame: true));

        await WaitUntilAsync(
            () => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0,
            TimeSpan.FromSeconds(2));

        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");
        clock.Advance(TimeSpan.FromMilliseconds(140));
        var acknowledgedReleaseFloorFrameId = recoveryOwnerFrameId + 98;
        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.None,
            ScreenSharePressureProtocol.PressureReasonContinuityLoss,
            observedFrameAgeMs: 0,
            recentStaleDrops: 0,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 1,
            currentEpochNeedMoreInputCount: 0,
            lastVisibleApplyFrameId: acknowledgedReleaseFloorFrameId,
            visibleHeadFrameId: recoveryOwnerFrameId + 3,
            appliedHeadFrameId: acknowledgedReleaseFloorFrameId,
            steadyVisibleProgressActive: true,
            stableVisibleHeadFrameId: acknowledgedReleaseFloorFrameId,
            framesAppliedSinceLastGap: 1);

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryLockActive"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryLockClearedByVisibleProofCount"));
        Assert.Equal(recoveryOwnerFrameId + 3, GetPrivateFieldValue<long>(coordinator, "acknowledgedVisibleHelperHeadFrameId"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "satisfiedRecoveryFloorFrameId"));
        Assert.Equal(string.Empty, GetPrivateFieldValue<string>(coordinator, "satisfiedRecoveryFloorSource"));

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.None,
            ScreenSharePressureProtocol.PressureReasonContinuityLoss,
            observedFrameAgeMs: 0,
            recentStaleDrops: 0,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 1,
            currentEpochNeedMoreInputCount: 0,
            lastVisibleApplyFrameId: acknowledgedReleaseFloorFrameId,
            visibleHeadFrameId: acknowledgedReleaseFloorFrameId,
            appliedHeadFrameId: acknowledgedReleaseFloorFrameId,
            steadyVisibleProgressActive: true,
            stableVisibleHeadFrameId: acknowledgedReleaseFloorFrameId,
            framesAppliedSinceLastGap: 1,
            visibleRecoveryFloorFrameId: acknowledgedReleaseFloorFrameId,
            currentEpochRecoveryKeyframeApplyCount: 1);

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryLockActive"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryLockClearedByAcknowledgedProofCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryLockClearedByVisibleProofCount"));

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.None,
            ScreenSharePressureProtocol.PressureReasonContinuityLoss,
            observedFrameAgeMs: 0,
            recentStaleDrops: 0,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 1,
            currentEpochNeedMoreInputCount: 0,
            lastVisibleApplyFrameId: acknowledgedReleaseFloorFrameId,
            visibleHeadFrameId: acknowledgedReleaseFloorFrameId,
            appliedHeadFrameId: acknowledgedReleaseFloorFrameId,
            steadyVisibleProgressActive: true,
            stableVisibleHeadFrameId: acknowledgedReleaseFloorFrameId,
            framesAppliedSinceLastGap: 1,
            visibleRecoveryFloorFrameId: acknowledgedReleaseFloorFrameId,
            currentEpochRecoveryKeyframeApplyCount: 1);

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryLockActive"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_NoProgressStall_DoesNotRevokeSatisfiedRecoveryFloor()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 21, 9, 5, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-satisfied-floor-stall", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "satisfied floor stall start");

        DisableStartupWarmupForCoordinatorOnly(coordinator);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 50,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 0x01, 0x02, 0x03 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: recoveryEpoch,
            isKeyFrame: true));

        await WaitUntilAsync(
            () => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0,
            TimeSpan.FromSeconds(2));

        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");
        clock.Advance(TimeSpan.FromMilliseconds(140));
        SendHealthyRemotePressure(
            coordinator,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 1,
            currentEpochNeedMoreInputCount: 0,
            lastVisibleApplyFrameId: recoveryOwnerFrameId,
            appliedHeadFrameId: recoveryOwnerFrameId,
            steadyVisibleProgressActive: true,
            stableVisibleHeadFrameId: recoveryOwnerFrameId,
            framesAppliedSinceLastGap: 1);

        Assert.InRange(GetPrivateFieldValue<long>(coordinator, "satisfiedRecoveryFloorFrameId"), -1L, recoveryOwnerFrameId);
        Assert.InRange(GetPrivateFieldValue<long>(coordinator, "acknowledgedHelperHeadFrameId"), -1L, recoveryOwnerFrameId);

        clock.Advance(TimeSpan.FromMilliseconds(1600));
        SendHealthyRemotePressure(
            coordinator,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 1,
            currentEpochNeedMoreInputCount: 0);

        Assert.InRange(GetPrivateFieldValue<long>(coordinator, "acknowledgedHelperHeadFrameId"), -1L, recoveryOwnerFrameId);
        Assert.InRange(GetPrivateFieldValue<long>(coordinator, "satisfiedRecoveryFloorFrameId"), -1L, recoveryOwnerFrameId);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ContinuityRecoveryTimeout_TriggersExactlyOneHardReset()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var flushedTransportQueueCount = 0;
        string? flushedTransportQueueReason = null;
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 14, 8, 5, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock,
            flushTransportQueue: reason =>
            {
                flushedTransportQueueCount++;
                flushedTransportQueueReason = reason;
            });

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-timeout-reset", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery timeout reset start");

        DisableStartupWarmupForCoordinatorOnly(coordinator);
        fakeSource.SetTransportTuningLevel(ScreenShareTransportTuningLevel.Normal);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 11,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.None,
            ScreenSharePressureProtocol.PressureReasonContinuityLoss,
            observedFrameAgeMs: 0,
            recentStaleDrops: 0);
        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.CatchUpOnly,
            ScreenSharePressureProtocol.PressureReasonContinuityLoss,
            observedFrameAgeMs: 0,
            recentStaleDrops: 0);
        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.CatchUpOnly,
            ScreenSharePressureProtocol.PressureReasonContinuityLoss,
            observedFrameAgeMs: 0,
            recentStaleDrops: 0);

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryLockActive"));
        Assert.Equal(0, GetPrivateFieldValue<int>(coordinator, "recoveryTimeoutResetCount"));
        Assert.True(GetPrivateFieldValue<long>(coordinator, "recoveryLockStreamEpoch") >= 11);
        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(ScreenShareTransportTuningLevel.Normal, fakeSource.LastTransportTuningLevel);
        Assert.Equal(0, flushedTransportQueueCount);
        Assert.True(string.IsNullOrEmpty(flushedTransportQueueReason));
        Assert.Equal(
            0,
            fakeSource.KeyFrameRequestReasons.Count(static reason => string.Equals(reason, "recovery_timeout_reset", StringComparison.Ordinal)));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ContinuityLossKeyframeRequest_StartsRecoveryBurstAndRecordsGapToRequestLatency()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-burst-start", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery burst start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 5,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.None,
            ScreenSharePressureProtocol.PressureReasonContinuityLoss,
            observedFrameAgeMs: 0,
            recentStaleDrops: 0);
        clock.Advance(TimeSpan.FromMilliseconds(120));
        var expectedTransportTuningLevel = GetPrivateFieldValue<ScreenShareTransportTuningLevel>(coordinator, "transportTuningLevel");

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.True(GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch") > 5L);
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryGapCount"));
        Assert.True(GetPrivateFieldValue<long>(coordinator, "recoveryGapToKeyframeRequestMs") >= 120L);
        Assert.Contains(ScreenSharePressureProtocol.PressureReasonContinuityLoss, fakeSource.KeyFrameRequestReasons);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurstStart_FlushesTransportQueue_AndPurgesPendingRawFrames()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var flushedTransportQueueCount = 0;
        string? flushedTransportQueueReason = null;
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 0, 30, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock,
            flushTransportQueue: reason =>
            {
                flushedTransportQueueCount++;
                flushedTransportQueueReason = reason;
            });

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-burst-start-flush", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery burst start flush");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 55,
            PendingRawFrameCount: 2,
            OldestPendingRawFrameAgeMs: 180,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal(1, flushedTransportQueueCount);
        Assert.Equal("recovery_burst_start", flushedTransportQueueReason);
        Assert.Equal(1, fakeSource.PurgePendingRawFramesCallCount);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_FrameGapReassemblerKeyframeRequest_StartsRecoveryBurst()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 1, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-burst-frame-gap", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery burst frame-gap start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 6,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.None,
            ScreenSharePressureProtocol.PressureReasonContinuityLoss,
            observedFrameAgeMs: 0,
            recentStaleDrops: 0);

        coordinator.RequestKeyFrame("frame_gap_reassembler");

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.True(GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch") > 6L);
        Assert.Contains("frame_gap_reassembler", fakeSource.KeyFrameRequestReasons);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_SimplifiedRecovery_StartsOnResetEpochImmediately()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 20, 9, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-simplified-recovery-start", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "simplified recovery start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 5,
            PendingRawFrameCount: 1,
            OldestPendingRawFrameAgeMs: 180,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.None,
            ScreenSharePressureProtocol.PressureReasonContinuityLoss,
            observedFrameAgeMs: 0,
            recentStaleDrops: 0);
        clock.Advance(TimeSpan.FromMilliseconds(120));
        var expectedTransportTuningLevel = GetPrivateFieldValue<ScreenShareTransportTuningLevel>(coordinator, "transportTuningLevel");

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal(6L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch"));
        Assert.Equal("OwnerPending", GetPrivateFieldValue<object>(coordinator, "recoveryBurstPhase")?.ToString());
        Assert.Equal(6L, fakeSource.GetFreshnessMetricsSnapshot().CurrentStreamEpoch);
        Assert.Equal(expectedTransportTuningLevel, fakeSource.LastTransportTuningLevel);
        Assert.Equal(1, fakeSource.PurgePendingRawFramesCallCount);
        Assert.Contains(ScreenSharePressureProtocol.PressureReasonContinuityLoss, fakeSource.KeyFrameRequestReasons);
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerPendingForcedResetCount"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_SimplifiedRecovery_HelperVisibleReceipt_CompletesBurstAndStartsPostAckHold()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 20, 9, 5, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-simplified-recovery-ack", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "simplified recovery ack start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 70,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        Assert.Equal(71L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch"));

        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 0x01, 0x02, 0x03 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: 71,
            isKeyFrame: true));

        await WaitUntilAsync(
            () => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0,
            TimeSpan.FromSeconds(2));

        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");
        clock.Advance(TimeSpan.FromMilliseconds(140));
        coordinator.SetRemoteRecoveryReceipt(
            new ScreenShareRecoveryReceiptV1
            {
                SessionId = "session-simplified-recovery-ack",
                StreamEpoch = 71,
                OwnerFrameId = recoveryOwnerFrameId,
                VisibleRecoveryFrameId = recoveryOwnerFrameId,
                VisibleHeadFrameId = recoveryOwnerFrameId,
                ReceiptKind = ScreenShareRecoveryReceiptCodec.RecoveryKeyframeVisibleReceiptKind,
            });

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal("PostAckHold", GetPrivateFieldValue<object>(coordinator, "recoveryBurstPhase")?.ToString());
        Assert.Equal("helper_ack", GetPrivateFieldValue<string>(coordinator, "lastCompletedRecoveryCompletionKind"));
        Assert.Equal(recoveryOwnerFrameId, GetPrivateFieldValue<long>(coordinator, "lastCompletedRecoveryOwnerFrameId"));
        Assert.Equal(recoveryOwnerFrameId, GetPrivateFieldValue<long>(coordinator, "lastCompletedRecoveryAckFrameId"));
        Assert.Equal("helper_visible_receipt", GetPrivateFieldValue<string>(coordinator, "lastCompletedRecoveryAckSource"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryPostAckHoldStartedCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryPostAckHoldExpiredCount"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByHelperAckCount"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByHelperVisibleReceiptCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByTimeoutCount"));
        Assert.Equal(recoveryOwnerFrameId, GetPrivateFieldValue<long>(coordinator, "lastRemoteRecoveryReceiptOwnerFrameId"));
        Assert.Equal(recoveryOwnerFrameId, GetPrivateFieldValue<long>(coordinator, "lastRemoteRecoveryReceiptVisibleRecoveryFrameId"));
        Assert.False(GetPrivateFieldValue<bool>(coordinator, "recoveryLockActive"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_HelperVisibleReceipt_WrongOwner_IsRejectedWithDiagnostics()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 20, 9, 5, 20, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-receipt-wrong-owner", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "receipt wrong owner start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 80,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        Assert.Equal(81L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch"));

        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 0x01, 0x02, 0x03 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: 81,
            isKeyFrame: true));

        await WaitUntilAsync(
            () => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0,
            TimeSpan.FromSeconds(2));

        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");

        coordinator.SetRemoteRecoveryReceipt(
            new ScreenShareRecoveryReceiptV1
            {
                SessionId = "session-receipt-wrong-owner",
                StreamEpoch = 81,
                OwnerFrameId = recoveryOwnerFrameId + 1,
                VisibleRecoveryFrameId = recoveryOwnerFrameId + 1,
                VisibleHeadFrameId = recoveryOwnerFrameId + 1,
                ReceiptKind = ScreenShareRecoveryReceiptCodec.RecoveryKeyframeVisibleReceiptKind,
            });

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal("OwnerEmittedAwaitingHelperAck", GetPrivateFieldValue<object>(coordinator, "recoveryBurstPhase")?.ToString());
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByHelperVisibleReceiptCount"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "remoteRecoveryReceiptRejectedCount"));
        Assert.Equal("wrong_owner_frame", GetPrivateFieldValue<string>(coordinator, "lastRemoteRecoveryReceiptRejectReason"));
        Assert.Equal(81L, GetPrivateFieldValue<long>(coordinator, "lastRemoteRecoveryReceiptRejectActiveStreamEpoch"));
        Assert.Equal(recoveryOwnerFrameId, GetPrivateFieldValue<long>(coordinator, "lastRemoteRecoveryReceiptRejectActiveOwnerFrameId"));
        Assert.Equal("owner_emitted_awaiting_helper_ack", GetPrivateFieldValue<string>(coordinator, "lastRemoteRecoveryReceiptRejectActivePhase"));
        Assert.Equal(recoveryOwnerFrameId + 1, GetPrivateFieldValue<long>(coordinator, "lastRemoteRecoveryReceiptOwnerFrameId"));
        Assert.Equal(string.Empty, GetPrivateFieldValue<string>(coordinator, "recoveryAckSource"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_DoesNotCompleteOnStableHeadAlone()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 5, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-burst-complete", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery burst complete start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 7,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryEpochForStableHeadOnly = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(new ScreenCaptureFrameEventArgs(
            640,
            360,
            new byte[] { 0x01, 0x02, 0x03 },
            "h264",
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            isKeyFrame: true,
            streamEpoch: recoveryEpochForStableHeadOnly));

        await WaitUntilAsync(
            () => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0,
            TimeSpan.FromSeconds(2));

        clock.Advance(TimeSpan.FromMilliseconds(140));
        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.None,
            ScreenSharePressureProtocol.PressureReasonHealthy,
            observedFrameAgeMs: 0,
            recentStaleDrops: 0,
            appliedHeadFrameId: 1,
            steadyVisibleProgressActive: true,
            stableVisibleHeadFrameId: 1,
            framesAppliedSinceLastGap: 4);

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal("OwnerEmittedAwaitingHelperAck", GetPrivateFieldValue<object>(coordinator, "recoveryBurstPhase")?.ToString());
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryPostAckHoldStartedCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedCount"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerAckFrameId"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerEmitToFirstVisibleApplyMs"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_DoesNotCompleteOnAppliedHeadAlone()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 5, 15, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-burst-applied-ack", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery burst applied ack start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 70,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryEpochForAppliedOnly = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 0x01, 0x02, 0x03 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: recoveryEpochForAppliedOnly,
            isKeyFrame: true));

        await WaitUntilAsync(
            () => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0,
            TimeSpan.FromSeconds(2));

        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");

        clock.Advance(TimeSpan.FromMilliseconds(140));
        coordinator.SetRemotePressureState(
            mode: ScreenShareRemotePressureMode.None,
            reason: ScreenSharePressureProtocol.PressureReasonHealthy,
            observedFrameAgeMs: 0,
            recentStaleDrops: 0,
            lastVisibleApplyFrameId: null,
            appliedHeadFrameId: recoveryOwnerFrameId,
            steadyVisibleProgressActive: true,
            stableVisibleHeadFrameId: recoveryOwnerFrameId,
            framesAppliedSinceLastGap: 4);

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal("OwnerEmittedAwaitingHelperAck", GetPrivateFieldValue<object>(coordinator, "recoveryBurstPhase")?.ToString());
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryPostAckHoldStartedCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByAppliedHeadAckCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByVisibleRecoveryFloorCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByVisibleApplyFallbackCount"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerAckFrameId"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_DoesNotCompleteOnVisibleApplyFallbackAfterRecoveryKeyframeApply()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 5, 20, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-burst-last-visible-ack", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery burst last visible ack start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 701,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryEpochForVisibleFallback = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 0x01, 0x02, 0x03 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: recoveryEpochForVisibleFallback,
            isKeyFrame: true));

        await WaitUntilAsync(
            () => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0,
            TimeSpan.FromSeconds(2));

        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");

        clock.Advance(TimeSpan.FromMilliseconds(140));
        coordinator.SetRemotePressureState(
            mode: ScreenShareRemotePressureMode.None,
            reason: ScreenSharePressureProtocol.PressureReasonHealthy,
            observedFrameAgeMs: 0,
            recentStaleDrops: 0,
            lastVisibleApplyFrameId: recoveryOwnerFrameId,
            appliedHeadFrameId: recoveryOwnerFrameId > 0 ? recoveryOwnerFrameId - 1 : null,
            steadyVisibleProgressActive: true,
            stableVisibleHeadFrameId: recoveryOwnerFrameId,
            framesAppliedSinceLastGap: 4,
            currentEpochRecoveryKeyframeApplyCount: 1);

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal("OwnerEmittedAwaitingHelperAck", GetPrivateFieldValue<object>(coordinator, "recoveryBurstPhase")?.ToString());
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryPostAckHoldStartedCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByVisibleApplyFallbackCount"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerAckFrameId"));
        Assert.Equal(string.Empty, GetPrivateFieldValue<string>(coordinator, "recoveryAckSource"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerEmitToAckMs"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_DoesNotCompleteOnVisibleApplyFallbackWhileRemotePressureStillHighFrameAge()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 5, 22, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-burst-applied-ack-high-age", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery burst applied ack high-age start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 705,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryEpochForHighAge = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 0x01, 0x02, 0x03 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: recoveryEpochForHighAge,
            isKeyFrame: true));

        await WaitUntilAsync(
            () => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0,
            TimeSpan.FromSeconds(2));

        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");

        clock.Advance(TimeSpan.FromMilliseconds(140));
        coordinator.SetRemotePressureState(
            mode: ScreenShareRemotePressureMode.ReduceFps,
            reason: ScreenSharePressureProtocol.PressureReasonHighFrameAge,
            observedFrameAgeMs: 1400,
            recentStaleDrops: 0,
            lastVisibleApplyFrameId: recoveryOwnerFrameId,
            appliedHeadFrameId: recoveryOwnerFrameId,
            steadyVisibleProgressActive: false,
            stableVisibleHeadFrameId: null,
            framesAppliedSinceLastGap: 0,
            currentEpochRecoveryKeyframeApplyCount: 1);

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal("OwnerEmittedAwaitingHelperAck", GetPrivateFieldValue<object>(coordinator, "recoveryBurstPhase")?.ToString());
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryPostAckHoldStartedCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByVisibleApplyFallbackCount"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerAckFrameId"));
        Assert.Equal(string.Empty, GetPrivateFieldValue<string>(coordinator, "recoveryAckSource"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerEmitToAckMs"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_DoesNotCompleteOnVisibleApplyFallbackWhileRemotePressureStillContinuityLoss()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 5, 23, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-burst-applied-ack-continuity", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery burst applied ack continuity start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 706,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryEpochForContinuityLoss = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 0x21, 0x22, 0x23 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: recoveryEpochForContinuityLoss,
            isKeyFrame: true));

        await WaitUntilAsync(
            () => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0,
            TimeSpan.FromSeconds(2));

        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");

        clock.Advance(TimeSpan.FromMilliseconds(140));
        coordinator.SetRemotePressureState(
            mode: ScreenShareRemotePressureMode.None,
            reason: ScreenSharePressureProtocol.PressureReasonContinuityLoss,
            observedFrameAgeMs: 0,
            recentStaleDrops: 0,
            lastVisibleApplyFrameId: recoveryOwnerFrameId,
            appliedHeadFrameId: recoveryOwnerFrameId,
            steadyVisibleProgressActive: false,
            stableVisibleHeadFrameId: recoveryOwnerFrameId,
            framesAppliedSinceLastGap: 0,
            currentEpochRecoveryKeyframeApplyCount: 1);

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal("OwnerEmittedAwaitingHelperAck", GetPrivateFieldValue<object>(coordinator, "recoveryBurstPhase")?.ToString());
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryPostAckHoldStartedCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByVisibleApplyFallbackCount"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerAckFrameId"));
        Assert.Equal(string.Empty, GetPrivateFieldValue<string>(coordinator, "recoveryAckSource"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_LastCompletedHelperAckPersistsAcrossLaterRestart()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 5, 25, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-burst-last-completed-persist", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery burst last completed persist start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 702,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryEpochForPersistedAck = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 0x01, 0x02, 0x03 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: recoveryEpochForPersistedAck,
            isKeyFrame: true));

        await WaitUntilAsync(
            () => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0,
            TimeSpan.FromSeconds(2));

        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");
        clock.Advance(TimeSpan.FromMilliseconds(140));
        DeliverMatchingHelperVisibleReceipt(
            coordinator,
            "session-recovery-burst-last-completed-persist",
            recoveryEpochForPersistedAck,
            recoveryOwnerFrameId);

        Assert.Equal(recoveryEpochForPersistedAck, GetPrivateFieldValue<long>(coordinator, "lastCompletedRecoveryEpoch"));
        Assert.Equal(recoveryOwnerFrameId, GetPrivateFieldValue<long>(coordinator, "lastCompletedRecoveryOwnerFrameId"));
        Assert.Equal(recoveryOwnerFrameId, GetPrivateFieldValue<long>(coordinator, "lastCompletedRecoveryAckFrameId"));
        Assert.Equal("helper_visible_receipt", GetPrivateFieldValue<string>(coordinator, "lastCompletedRecoveryAckSource"));
        Assert.Equal("helper_ack", GetPrivateFieldValue<string>(coordinator, "lastCompletedRecoveryCompletionKind"));

        clock.Advance(TimeSpan.FromMilliseconds(450));
        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

        coordinator.RequestKeyFrame("frame_gap_reassembler");

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.True(GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch") > recoveryEpochForPersistedAck);
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "lastCompletedRecoveryEpoch"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "lastCompletedRecoveryOwnerFrameId"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "lastCompletedRecoveryAckFrameId"));
        Assert.Equal(string.Empty, GetPrivateFieldValue<string>(coordinator, "lastCompletedRecoveryAckSource"));
        Assert.Equal(string.Empty, GetPrivateFieldValue<string>(coordinator, "lastCompletedRecoveryCompletionKind"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerAckFrameId"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerEmitToAckMs"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerAckWindowMs"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerEmitToFirstVisibleApplyMs"));
        Assert.Equal(string.Empty, GetPrivateFieldValue<string>(coordinator, "recoveryAckSource"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "helperAckAfterFactSendMs"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_SameEpochFrameGapAfterHelperAck_IsSuppressed()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 5, 30, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-burst-stale-suppress", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery burst stale suppress start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 71,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryBurstStreamEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 0x01, 0x02, 0x03 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: recoveryBurstStreamEpoch,
            isKeyFrame: true));

        await WaitUntilAsync(
            () => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0,
            TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");

        clock.Advance(TimeSpan.FromMilliseconds(140));
        DeliverMatchingHelperVisibleReceipt(
            coordinator,
            "session-recovery-burst-stale-suppress",
            recoveryBurstStreamEpoch,
            recoveryOwnerFrameId);

        coordinator.RequestKeyFrame("frame_gap_reassembler");

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal("PostAckHold", GetPrivateFieldValue<object>(coordinator, "recoveryBurstPhase")?.ToString());
        Assert.Equal(1, fakeSource.KeyFrameRequestReasons.Count);
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstStaleRequestSuppressedCount"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstRequestSuppressedDueToHelperAckCount"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryPostAckHoldSuppressedReopenCount"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_SameEpochFrameGapAfterHelperAckAndContinuitySignal_IsStillSuppressed()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 5, 35, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-burst-stale-suppress-continuity", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery burst stale suppress continuity start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 710,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryBurstStreamEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 0x11, 0x12, 0x13 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: recoveryBurstStreamEpoch,
            isKeyFrame: true));

        await WaitUntilAsync(
            () => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0,
            TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");

        clock.Advance(TimeSpan.FromMilliseconds(140));
        DeliverMatchingHelperVisibleReceipt(
            coordinator,
            "session-recovery-burst-stale-suppress-continuity",
            recoveryBurstStreamEpoch,
            recoveryOwnerFrameId);

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.None,
            ScreenSharePressureProtocol.PressureReasonContinuityLoss,
            observedFrameAgeMs: 0,
            recentStaleDrops: 0);

        coordinator.RequestKeyFrame("frame_gap_reassembler");

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal("PostAckHold", GetPrivateFieldValue<object>(coordinator, "recoveryBurstPhase")?.ToString());
        Assert.Equal(1, fakeSource.KeyFrameRequestReasons.Count);
        Assert.Equal(recoveryOwnerFrameId, GetPrivateFieldValue<long>(coordinator, "helperVisibleHeadFrameId"));
        Assert.Equal(recoveryOwnerFrameId, GetPrivateFieldValue<long>(coordinator, "helperLastVisibleApplyFrameId"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstStaleRequestSuppressedCount"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstRequestSuppressedDueToHelperAckCount"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryPostAckHoldSuppressedReopenCount"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_StreamConfigMissing_RequestsKeyframeWithoutStartingRecoveryBurst()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 5, 33, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-stream-config-missing-no-burst", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "stream config missing no burst start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 7021,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame("stream_config_missing");

        Assert.Single(fakeSource.KeyFrameRequestReasons);
        Assert.Equal("stream_config_missing", fakeSource.KeyFrameRequestReasons[0]);
        Assert.False(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_PreOwnerSameEpochNonKeyFrames_AreHeldUntilOwnerExists()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var sentPayloadCount = 0;
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 5, 35, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) =>
            {
                Interlocked.Increment(ref sentPayloadCount);
                return Task.CompletedTask;
            },
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-burst-pre-owner-hold", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery burst pre-owner hold start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 703,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryBurstStreamEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 0x11, 0x12, 0x13 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: recoveryBurstStreamEpoch,
            isKeyFrame: false));
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 0x21, 0x22, 0x23 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: recoveryBurstStreamEpoch,
            isKeyFrame: false));

        await Task.Delay(100);

        Assert.Equal(0, Volatile.Read(ref sentPayloadCount));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerPendingNonKeyHeldCount"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerPendingNonKeyReplacedCount"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId"));

        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 0x31, 0x32, 0x33 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: recoveryBurstStreamEpoch,
            isKeyFrame: true));

        await WaitUntilAsync(
            () => Volatile.Read(ref sentPayloadCount) > 0,
            TimeSpan.FromSeconds(2));

        Assert.False(GetPrivateFieldValue<bool>(coordinator, "recoveryOwnerPendingNonKeyHeldActive"));
        Assert.True(GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_PostOwnerSameEpochNonKeyFrames_BeyondFirstTwo_AreHeldUntilAck()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var sentPayloadCount = 0;
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 5, 40, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) =>
            {
                Interlocked.Increment(ref sentPayloadCount);
                return Task.CompletedTask;
            },
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-burst-post-owner-hold", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery burst post-owner hold start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 704,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryBurstStreamEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 0x41, 0x42, 0x43 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: recoveryBurstStreamEpoch,
            isKeyFrame: true));

        await WaitUntilAsync(
            () => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0,
            TimeSpan.FromSeconds(2));

        for (var i = 0; i < 4; i++)
        {
            fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
                640,
                360,
                new byte[] { (byte)(0x50 + i), (byte)(0x60 + i), (byte)(0x70 + i) },
                capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
                streamEpoch: recoveryBurstStreamEpoch,
                isKeyFrame: false));
        }

        await WaitUntilAsync(
            () => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerUnackedNonKeyHeldCount") >= 1 &&
                  GetPrivateFieldValue<long>(coordinator, "recoveryOwnerUnackedNonKeyReplacedCount") >= 1,
            TimeSpan.FromSeconds(2));
        await Task.Delay(100);

        Assert.InRange(Volatile.Read(ref sentPayloadCount), 1, 3);
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerUnackedNonKeyHeldCount"));
        Assert.Equal(3L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerUnackedNonKeyReplacedCount"));
        Assert.InRange(GetPrivateFieldValue<int>(coordinator, "recoveryProtectedFollowerCount"), 0, 2);
    }

    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_PostOwnerSameEpochKeyframe_IsSuppressedUntilAck()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var sentPayloadCount = 0;
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 5, 50, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) =>
            {
                Interlocked.Increment(ref sentPayloadCount);
                return Task.CompletedTask;
            },
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-burst-post-owner-keyframe-suppressed", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery burst post-owner keyframe suppressed start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 705,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 0x81, 0x82, 0x83 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: 705,
            isKeyFrame: true));

        await WaitUntilAsync(
            () => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0,
            TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");

        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 0x91, 0x92, 0x93 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: 705,
            isKeyFrame: true));

        await Task.Delay(100);

        Assert.Equal(1, Volatile.Read(ref sentPayloadCount));
        Assert.Equal(recoveryOwnerFrameId, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoverySameEpochKeyframeSuppressedWhileOwnerUnackedCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerReplacedBeforeAckCount"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_HelperAckStartsSettleHold_AndFollowerProgressResumesFreshFrames()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var sentPayloadCount = 0;
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 6, 10, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) =>
            {
                Interlocked.Increment(ref sentPayloadCount);
                return Task.CompletedTask;
            },
            clock: clock);
        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-burst-post-owner-resume", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery burst post-owner resume start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 706,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryBurstStreamEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 0xA1, 0xA2, 0xA3 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: recoveryBurstStreamEpoch,
            isKeyFrame: true));

        await WaitUntilAsync(
            () => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0,
            TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");

        for (var i = 0; i < 4; i++)
        {
            fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
                640,
                360,
                new byte[] { (byte)(0xB0 + i), (byte)(0xC0 + i), (byte)(0xD0 + i) },
                capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
                streamEpoch: recoveryBurstStreamEpoch,
                isKeyFrame: false));
        }

        await WaitUntilAsync(
            () => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerUnackedNonKeyHeldCount") >= 1,
            TimeSpan.FromSeconds(2));

        DeliverMatchingHelperVisibleReceipt(
            coordinator,
            "session-recovery-burst-post-owner-resume",
            recoveryBurstStreamEpoch,
            recoveryOwnerFrameId);

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal("PostAckHold", GetPrivateFieldValue<object>(coordinator, "recoveryBurstPhase")?.ToString());
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryPostAckHoldStartedCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryPostAckHoldExpiredCount"));
        var sentPayloadCountBeforeResume = Volatile.Read(ref sentPayloadCount);

        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 0xE1, 0xE2, 0xE3 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: 706,
            isKeyFrame: false));

        await Task.Delay(100);
        Assert.Equal(sentPayloadCountBeforeResume, Volatile.Read(ref sentPayloadCount));

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.None,
            ScreenSharePressureProtocol.PressureReasonHealthy,
            observedFrameAgeMs: 0,
            recentStaleDrops: 0,
            lastVisibleApplyFrameId: recoveryOwnerFrameId + 1,
            visibleHeadFrameId: recoveryOwnerFrameId + 1,
            appliedHeadFrameId: recoveryOwnerFrameId + 1,
            steadyVisibleProgressActive: true,
            stableVisibleHeadFrameId: recoveryOwnerFrameId + 1,
            framesAppliedSinceLastGap: 5);

        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 0xF1, 0xF2, 0xF3 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: 706,
            isKeyFrame: false));

        await Task.Delay(100);
        Assert.True(Volatile.Read(ref sentPayloadCount) <= sentPayloadCountBeforeResume + 1);

        clock.Advance(TimeSpan.FromMilliseconds(450));
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

        await WaitUntilAsync(
            () => Volatile.Read(ref sentPayloadCount) > sentPayloadCountBeforeResume,
            TimeSpan.FromSeconds(2));
        await Task.Delay(100);

        Assert.True(Volatile.Read(ref sentPayloadCount) > sentPayloadCountBeforeResume);
        Assert.False(GetPrivateFieldValue<bool>(coordinator, "recoveryOwnerUnackedNonKeyHeldActive"));
        Assert.False(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryPostAckHoldExpiredCount"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_SettleTimeoutClearsHoldAndLease()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 19, 8, 30, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-burst-settle-timeout", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery burst settle timeout start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 7061,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryBurstStreamEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 0x01, 0x02, 0x03 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: recoveryBurstStreamEpoch,
            isKeyFrame: true));

        await WaitUntilAsync(
            () => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0,
            TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");

        DeliverMatchingHelperVisibleReceipt(
            coordinator,
            "session-recovery-burst-settle-timeout",
            recoveryBurstStreamEpoch,
            recoveryOwnerFrameId);

        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryPostAckHoldStartedCount"));
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));

        clock.Advance(TimeSpan.FromMilliseconds(450));
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryPostAckHoldExpiredCount"));
        Assert.False(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_HighFrameAgeDuringOwnerAckWithFreshProgress_IsSuppressed()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var sentPayloadCount = 0;
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 6, 25, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) =>
            {
                Interlocked.Increment(ref sentPayloadCount);
                return Task.CompletedTask;
            },
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-burst-owner-ack-age", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery burst owner ack age start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 707,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(180));
            fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
                640,
                360,
                new byte[] { (byte)(0x10 + i), (byte)(0x20 + i), (byte)(0x30 + i) },
                capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
                streamEpoch: 707,
                isKeyFrame: false));
            await Task.Delay(75);
        }

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryBurstStreamEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        clock.Advance(TimeSpan.FromMilliseconds(180));
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 0xF1, 0xF2, 0xF3 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: recoveryBurstStreamEpoch,
            isKeyFrame: true));

        await WaitUntilAsync(
            () => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0,
            TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.None,
            ScreenSharePressureProtocol.PressureReasonHealthy,
            observedFrameAgeMs: 0,
            recentStaleDrops: 0,
            lastVisibleApplyFrameId: recoveryOwnerFrameId,
            visibleHeadFrameId: recoveryOwnerFrameId,
            appliedHeadFrameId: recoveryOwnerFrameId,
            steadyVisibleProgressActive: true,
            stableVisibleHeadFrameId: recoveryOwnerFrameId,
            framesAppliedSinceLastGap: 4);

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.ReduceFps,
            ScreenSharePressureProtocol.PressureReasonHighFrameAge,
            observedFrameAgeMs: 900,
            recentStaleDrops: 0,
            lastVisibleApplyFrameId: recoveryOwnerFrameId,
            visibleHeadFrameId: recoveryOwnerFrameId,
            appliedHeadFrameId: recoveryOwnerFrameId,
            steadyVisibleProgressActive: true,
            stableVisibleHeadFrameId: recoveryOwnerFrameId,
            framesAppliedSinceLastGap: 4);

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal(ScreenShareRemotePressureMode.None, GetPrivateFieldValue<ScreenShareRemotePressureMode>(coordinator, "remotePressureMode"));
        Assert.Equal(ScreenSharePressureProtocol.PressureReasonHealthy, GetPrivateFieldValue<string>(coordinator, "remotePressureReason"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "highFrameAgeSuppressedDuringOwnerAckCount"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_StaleHelperProofAllowsSameEpochRestart()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 5, 45, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-burst-stale-expiry", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery burst stale expiry start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 72,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryBurstStreamEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 0x01, 0x02, 0x03 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: recoveryBurstStreamEpoch,
            isKeyFrame: true));

        await WaitUntilAsync(
            () => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0,
            TimeSpan.FromSeconds(2));

        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");
        clock.Advance(TimeSpan.FromMilliseconds(140));
        DeliverMatchingHelperVisibleReceipt(
            coordinator,
            "session-recovery-burst-stale-expiry",
            recoveryBurstStreamEpoch,
            recoveryOwnerFrameId);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        clock.Advance(TimeSpan.FromMilliseconds(450));
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        coordinator.RequestKeyFrame("frame_gap_reassembler");

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal(2, fakeSource.KeyFrameRequestReasons.Count);
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstStaleRequestSuppressedCount"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_DuplicateRequestsRemainSingleOwner()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 6, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-burst-single-owner", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery burst single owner start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 8,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        coordinator.RequestKeyFrame("frame_gap_reassembler");

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal(1, fakeSource.KeyFrameRequestReasons.Count);
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstRestartSuppressedCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstEncoderRerequestCount"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_HigherEpochTakeoverStillAllowedBeforeOwnerEmit()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 21, 18, 55, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-burst-pre-owner-takeover", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery burst pre-owner takeover start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 30,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var initialBurstEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        Assert.True(initialBurstEpoch >= 30L);

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: initialBurstEpoch + 1,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame("frame_gap_reassembler");

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.True(GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch") > initialBurstEpoch);
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstProfileTransitionTakeoverCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryEpochTakeoverSuppressedAfterOwnerEmitCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerReplacedBeforeAckCount"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_HigherEpochRequestAfterOwnerEmit_DoesNotReplaceBurst_AndReceiptCompletesOriginal()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 21, 18, 56, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);
        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-burst-freeze-after-owner-emit", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery burst freeze after owner emit start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 40,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var originalBurstEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 0x11, 0x12, 0x13 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: originalBurstEpoch,
            isKeyFrame: true));

        await WaitUntilAsync(
            () => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0,
            TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: originalBurstEpoch + 1,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame("frame_gap_reassembler");

        Assert.Equal(originalBurstEpoch, GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch"));
        Assert.Equal(recoveryOwnerFrameId, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryEpochTakeoverSuppressedAfterOwnerEmitCount"));
        Assert.Equal(originalBurstEpoch, GetPrivateFieldValue<long>(coordinator, "lastRecoveryEpochTakeoverSuppressedFromEpoch"));
        Assert.Equal(originalBurstEpoch + 1, GetPrivateFieldValue<long>(coordinator, "lastRecoveryEpochTakeoverSuppressedToEpoch"));
        Assert.Equal("owner_emitted_awaiting_helper_ack", GetPrivateFieldValue<string>(coordinator, "lastRecoveryEpochTakeoverSuppressedPhase"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstProfileTransitionTakeoverCount"));

        DeliverMatchingHelperVisibleReceipt(
            coordinator,
            "session-recovery-burst-freeze-after-owner-emit",
            originalBurstEpoch,
            recoveryOwnerFrameId);

        await WaitUntilAsync(
            () => string.Equals(
                GetPrivateFieldValue<string>(coordinator, "recoveryAckSource"),
                "helper_visible_receipt",
                StringComparison.Ordinal),
            TimeSpan.FromSeconds(2));
        Assert.Equal("PostAckHold", GetPrivateFieldValue<object>(coordinator, "recoveryBurstPhase")?.ToString());

        clock.Advance(TimeSpan.FromMilliseconds(450));
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

        Assert.False(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByHelperVisibleReceiptCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByTimeoutCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "remoteRecoveryReceiptRejectedCount"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_HelperEpochResetAfterOwnerEmit_DoesNotClearBurst()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 21, 18, 57, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);
        var refreshHelperCurrentEpochState = typeof(TransportScreenShareCoordinator).GetMethod(
            "RefreshHelperCurrentEpochState",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(refreshHelperCurrentEpochState);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-burst-helper-epoch-reset", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery burst helper epoch reset start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 50,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var originalBurstEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 0x21, 0x22, 0x23 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: originalBurstEpoch,
            isKeyFrame: true));

        await WaitUntilAsync(
            () => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0,
            TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");

        refreshHelperCurrentEpochState!.Invoke(coordinator, new object?[] { originalBurstEpoch + 1 });

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal(originalBurstEpoch, GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch"));
        Assert.Equal(recoveryOwnerFrameId, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryEpochTakeoverSuppressedAfterOwnerEmitCount"));
        Assert.Equal(originalBurstEpoch, GetPrivateFieldValue<long>(coordinator, "lastRecoveryEpochTakeoverSuppressedFromEpoch"));
        Assert.Equal(originalBurstEpoch + 1, GetPrivateFieldValue<long>(coordinator, "lastRecoveryEpochTakeoverSuppressedToEpoch"));
        Assert.Equal("owner_emitted_awaiting_helper_ack", GetPrivateFieldValue<string>(coordinator, "lastRecoveryEpochTakeoverSuppressedPhase"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerReplacedBeforeAckCount"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_PendingOwnerForcesResetOnlyOnce()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 7, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);
        var ownerPendingTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnRecoveryOwnerPendingTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(ownerPendingTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-burst-forced-reset", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery burst forced reset start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 9,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryBurstStreamEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        clock.Advance(TimeSpan.FromMilliseconds(260));
        ownerPendingTick!.Invoke(coordinator, Array.Empty<object>());

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.True(recoveryBurstStreamEpoch > 9);
        Assert.Single(fakeSource.KeyFrameRequestReasons);
        Assert.Equal(ScreenSharePressureProtocol.PressureReasonContinuityLoss, fakeSource.KeyFrameRequestReasons[0]);
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerPendingForcedResetCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstEncoderRerequestCount"));
        Assert.Equal(
            GetPrivateFieldValue<ScreenShareTransportTuningLevel>(coordinator, "transportTuningLevel"),
            fakeSource.LastTransportTuningLevel);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_ForcedResetOwnerAckDoesNotTimeout()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 7, 30, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);
        var ownerPendingTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnRecoveryOwnerPendingTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(ownerPendingTick);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-burst-forced-reset-ack", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery burst forced reset ack start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 12,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);

        var recoveryBurstStreamEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        Assert.True(recoveryBurstStreamEpoch > 12);

        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 1, 2, 3 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: recoveryBurstStreamEpoch,
            isKeyFrame: true));

        await WaitUntilAsync(
            () => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0,
            TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");

        DeliverMatchingHelperVisibleReceipt(
            coordinator,
            "session-recovery-burst-forced-reset-ack",
            recoveryBurstStreamEpoch,
            recoveryOwnerFrameId);

        clock.Advance(TimeSpan.FromMilliseconds(260));
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerPendingForcedResetCount"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryKeyframeEmittedAfterForcedResetCount"));

        clock.Advance(TimeSpan.FromMilliseconds(850));
        autoTuneTick.Invoke(coordinator, Array.Empty<object>());

        Assert.False(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByHelperAckCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByTimeoutCount"));
        Assert.Equal("helper_ack", GetPrivateFieldValue<string>(coordinator, "lastCompletedRecoveryCompletionKind"));
        Assert.Equal("helper_visible_receipt", GetPrivateFieldValue<string>(coordinator, "lastCompletedRecoveryAckSource"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_ProtectedFramesDoNotCompleteBurst()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 8, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-burst-protected-frames", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery burst protected frames start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 10,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryBurstStreamEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 1, 2, 3 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryBurstStreamEpoch, isKeyFrame: true));
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 4, 5, 6 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryBurstStreamEpoch, isKeyFrame: false));
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 7, 8, 9 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryBurstStreamEpoch, isKeyFrame: false));

        await WaitUntilAsync(
            () => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0,
            TimeSpan.FromSeconds(2));

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByProtectedFramesCount"));
        Assert.InRange(GetPrivateFieldValue<int>(coordinator, "recoveryProtectedFollowerCount"), 0, 2);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_TimesOutWithoutHelperAdvance()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 9, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-burst-timeout", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery burst timeout start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 11,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 1, 2, 3 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: 11, isKeyFrame: true));
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 4, 5, 6 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: 11, isKeyFrame: false));
        await WaitUntilAsync(
            () => GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive") &&
                  GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch") > 0,
            TimeSpan.FromSeconds(2));

        clock.Advance(TimeSpan.FromMilliseconds(850));
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

        Assert.False(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedCount"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByTimeoutCount"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "satisfiedRecoveryFloorFrameId"));
        Assert.Equal(string.Empty, GetPrivateFieldValue<string>(coordinator, "satisfiedRecoveryFloorSource"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_TimeoutWithAppliedOnlyProgress_RemainsTimeout()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 10, 30, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-burst-timeout-helper-progress", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery burst timeout helper progress start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 13,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryEpochForTimeout = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 1, 2, 3 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: recoveryEpochForTimeout,
            isKeyFrame: true));

        await WaitUntilAsync(
            () => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") == 0,
            TimeSpan.FromSeconds(2));

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.ReduceFps,
            ScreenSharePressureProtocol.PressureReasonHighFrameAge,
            observedFrameAgeMs: 650,
            recentStaleDrops: 0,
            sentAtUtcMs: 0,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 2,
            currentEpochNeedMoreInputCount: 0,
            lastVisibleApplyFrameId: null,
            appliedHeadFrameId: 0,
            steadyVisibleProgressActive: null,
            stableVisibleHeadFrameId: 0,
            framesAppliedSinceLastGap: null);

        clock.Advance(TimeSpan.FromMilliseconds(850));
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

        Assert.False(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByHelperAckCount"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByTimeoutCount"));
        Assert.Equal(string.Empty, GetPrivateFieldValue<string>(coordinator, "lastCompletedRecoveryAckSource"));
        Assert.Equal("timeout", GetPrivateFieldValue<string>(coordinator, "lastCompletedRecoveryCompletionKind"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByVisibleRecoveryFloorCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByVisibleApplyFallbackCount"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerAckFrameId"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerEmitToAckMs"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerAckWindowMs"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerEmitToFirstVisibleApplyMs"));
        Assert.Equal(string.Empty, GetPrivateFieldValue<string>(coordinator, "recoveryAckSource"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "helperAckAfterFactSendMs"));

        var maybeLogFreshnessSummary = typeof(TransportScreenShareCoordinator).GetMethod(
            "MaybeLogFreshnessSummary",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(maybeLogFreshnessSummary);

        maybeLogFreshnessSummary!.Invoke(
            coordinator,
            new object[]
            {
                "session-recovery-burst-timeout-helper-progress",
                coordinator.GetMetricsSnapshot(),
                fakeSource.GetFreshnessMetricsSnapshot(),
                0,
                0,
                0L,
                0L,
                "none",
                0L,
                250,
                70,
                100
            });

        Assert.Equal("timeout", GetPrivateFieldValue<string>(coordinator, "lastCompletedRecoveryCompletionKind"));
        Assert.Equal(string.Empty, GetPrivateFieldValue<string>(coordinator, "lastCompletedRecoveryAckSource"));

        var logText = File.ReadAllText(LocalOperationalLog.LogFilePath);
        Assert.Contains("event=screenshare_freshness_summary", logText, StringComparison.Ordinal);
        Assert.Contains("recovery_completion_accounting_mismatch=0", logText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_BootstrapGrace_SuppressesAgeOnlyCatchUpBeforeFirstVisibleApply()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 20, 8, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-bootstrap-grace", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "bootstrap grace start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 81,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.ReduceFps,
            ScreenSharePressureProtocol.PressureReasonHighFrameAge,
            observedFrameAgeMs: 1500,
            recentStaleDrops: 0,
            sentAtUtcMs: 0,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 0,
            currentEpochNeedMoreInputCount: 0,
            lastVisibleApplyFrameId: null,
            appliedHeadFrameId: null,
            steadyVisibleProgressActive: false,
            stableVisibleHeadFrameId: null,
            framesAppliedSinceLastGap: 0);

        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        autoTuneTick.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.True(GetPrivateFieldValue<long>(coordinator, "bootstrapGraceSuppressedCatchUpCount") > 0);
        Assert.Equal("bootstrap_grace", GetPrivateFieldValue<string>(coordinator, "lastRemoteHighFrameAgeCatchUpSuppressionReason"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_ProtectedFollowersArePreservedBeforeHelperAck()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 19, 8, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-burst-corridor-ack", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery burst corridor ack start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 721,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryBurstStreamEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 0x11, 0x12, 0x13 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: recoveryBurstStreamEpoch,
            isKeyFrame: true));

        await WaitUntilAsync(
            () => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0,
            TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");

        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 0x21, 0x22, 0x23 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: recoveryBurstStreamEpoch,
            isKeyFrame: false));
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 0x31, 0x32, 0x33 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: recoveryBurstStreamEpoch,
            isKeyFrame: false));

        await Task.Delay(100);
        Assert.InRange(GetPrivateFieldValue<int>(coordinator, "recoveryProtectedFollowerCount"), 0, 2);

        DeliverMatchingHelperVisibleReceipt(
            coordinator,
            "session-recovery-burst-corridor-ack",
            recoveryBurstStreamEpoch,
            recoveryOwnerFrameId,
            visibleRecoveryFrameId: recoveryOwnerFrameId + 1,
            visibleHeadFrameId: recoveryOwnerFrameId + 1);

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal("PostAckHold", GetPrivateFieldValue<object>(coordinator, "recoveryBurstPhase")?.ToString());
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByHelperAckCount"));
        Assert.Equal(recoveryOwnerFrameId + 1, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerAckFrameId"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ContinuityLossPressure_WithHelperHeadAdvance_DoesNotCompleteBurstButClearsWarmup()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 20, 9, 30, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-helper-progress-continuity-loss", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "helper progress continuity loss start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 912,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryBurstStreamEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            640,
            360,
            new byte[] { 0x41, 0x42, 0x43 },
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: recoveryBurstStreamEpoch,
            isKeyFrame: true));

        await WaitUntilAsync(
            () => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0,
            TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.None,
            ScreenSharePressureProtocol.PressureReasonContinuityLoss,
            observedFrameAgeMs: 0,
            recentStaleDrops: 0,
            sentAtUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            currentEpochWarmupActive: true,
            currentEpochApplyCount: 1,
            currentEpochNeedMoreInputCount: 0,
            lastVisibleApplyFrameId: recoveryOwnerFrameId,
            appliedHeadFrameId: recoveryOwnerFrameId,
            steadyVisibleProgressActive: true,
            stableVisibleHeadFrameId: recoveryOwnerFrameId,
            framesAppliedSinceLastGap: 1);

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal("OwnerEmittedAwaitingHelperAck", GetPrivateFieldValue<object>(coordinator, "recoveryBurstPhase")?.ToString());
        Assert.False(GetPrivateFieldValue<bool>(coordinator, "helperCurrentEpochWarmupActive"));
        Assert.Equal(1, GetPrivateFieldValue<int>(coordinator, "helperCurrentEpochApplyCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByHelperAckCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByTimeoutCount"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "senderReceivedHelperProgressDuringContinuityLossCount"));
        Assert.Equal(string.Empty, GetPrivateFieldValue<string>(coordinator, "lastCompletedRecoveryCompletionKind"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ActiveRecoveryBurst_DefersProfileTransition()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 10, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-burst-profile-defer", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery burst profile defer start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 12,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);

        var applyModeMethod = typeof(TransportScreenShareCoordinator).GetMethod(
            "ApplySenderFreshnessMode",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applyModeMethod);

        var sendPipelineField = typeof(TransportScreenShareCoordinator).GetField(
            "sendPipeline",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(sendPipelineField);

        var currentPipeline = sendPipelineField!.GetValue(coordinator);
        applyModeMethod!.Invoke(
            coordinator,
            new object?[]
            {
                currentPipeline,
                fakeSource,
                "session-recovery-burst-profile-defer",
                ScreenShareSenderFreshnessMode.Normal,
                "test_profile_transition",
                8,
                false,
            });

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(0, fakeSource.KeyFrameRequestReasons.Count(static reason => string.Equals(reason, "sender_profile_transition", StringComparison.Ordinal)));
        Assert.True(GetPrivateFieldValue<long>(coordinator, "recoveryBurstProfileTransitionDeferredCount") >= 1);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryLock_DefersProfileTransitionBeforeEpochTakeover()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 16, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-lock-profile-defer", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery lock profile defer start");

        DisableStartupWarmupForCoordinatorOnly(coordinator);
        fakeSource.SetTransportTuningLevel(ScreenShareTransportTuningLevel.Normal);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 18,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.None,
            ScreenSharePressureProtocol.PressureReasonContinuityLoss,
            observedFrameAgeMs: 0,
            recentStaleDrops: 0);

        var applyModeMethod = typeof(TransportScreenShareCoordinator).GetMethod(
            "ApplySenderFreshnessMode",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applyModeMethod);

        var sendPipelineField = typeof(TransportScreenShareCoordinator).GetField(
            "sendPipeline",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(sendPipelineField);

        var currentPipeline = sendPipelineField!.GetValue(coordinator);
        applyModeMethod!.Invoke(
            coordinator,
            new object?[]
            {
                currentPipeline,
                fakeSource,
                "session-recovery-lock-profile-defer",
                ScreenShareSenderFreshnessMode.Reduced,
                "test_profile_transition",
                8,
                false,
            });

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryLockActive"));
        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(ScreenShareTransportTuningLevel.Normal, fakeSource.LastTransportTuningLevel);
        Assert.Equal(0, fakeSource.KeyFrameRequestReasons.Count(static reason => string.Equals(reason, "sender_profile_transition", StringComparison.Ordinal)));
        Assert.True(GetPrivateFieldValue<long>(coordinator, "recoveryBurstProfileTransitionDeferredCount") >= 1);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_HelperNeedMoreInputWithFreshProgress_DefersProfileTransition()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 22, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-helper-progress-profile-defer", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "helper progress profile defer start");

        DisableStartupWarmupForCoordinatorOnly(coordinator);
        fakeSource.SetTransportTuningLevel(ScreenShareTransportTuningLevel.Normal);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 19,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 34));

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.ReduceFps,
            ScreenSharePressureProtocol.PressureReasonHighFrameAge,
            observedFrameAgeMs: 720,
            recentStaleDrops: 0,
            sentAtUtcMs: 0,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 1,
            currentEpochNeedMoreInputCount: 2,
            lastVisibleApplyFrameId: 34,
            appliedHeadFrameId: 34,
            steadyVisibleProgressActive: false,
            stableVisibleHeadFrameId: null,
            framesAppliedSinceLastGap: 1);

        var applyModeMethod = typeof(TransportScreenShareCoordinator).GetMethod(
            "ApplySenderFreshnessMode",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applyModeMethod);

        var sendPipelineField = typeof(TransportScreenShareCoordinator).GetField(
            "sendPipeline",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(sendPipelineField);

        var currentPipeline = sendPipelineField!.GetValue(coordinator);
        applyModeMethod!.Invoke(
            coordinator,
            new object?[]
            {
                currentPipeline,
                fakeSource,
                "session-helper-progress-profile-defer",
                ScreenShareSenderFreshnessMode.Reduced,
                "test_profile_transition",
                8,
                false,
            });

        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(ScreenShareTransportTuningLevel.Normal, fakeSource.LastTransportTuningLevel);
        Assert.Equal(0, fakeSource.KeyFrameRequestReasons.Count(static reason => string.Equals(reason, "sender_profile_transition", StringComparison.Ordinal)));
        Assert.True(GetPrivateFieldValue<long>(coordinator, "recoveryBurstProfileTransitionDeferredCount") >= 1);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_SevereQueueCongestion_CanBreakRecoveryLock()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var backpressureProbe = new FakeScreenShareBackpressureProbe
        {
            IsSeverelyCongested = true,
            QueueDepth = 2,
        };
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 14, 8, 10, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock,
            transportBackpressureProbeResolver: () => backpressureProbe);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-lock-override", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery lock override start");

        DisableStartupWarmupForCoordinatorOnly(coordinator);
        fakeSource.SetTransportTuningLevel(ScreenShareTransportTuningLevel.Normal);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 13,
            LastEncodeDurationMs: 24,
            LastEncodeTotalDurationMs: 150));

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.None,
            ScreenSharePressureProtocol.PressureReasonContinuityLoss,
            observedFrameAgeMs: 0,
            recentStaleDrops: 0);

        clock.Advance(TimeSpan.FromMilliseconds(500));
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryLockActive"));
        Assert.NotEqual("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(ScreenShareTransportTuningLevel.BandwidthReduced, fakeSource.LastTransportTuningLevel);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_SenderLocalFreshnessPressure_EntersReducedModeEvenWhenTransportQueueIsEmpty()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            PendingRawFrameCount: 1,
            OldestPendingRawFrameAgeMs: 1350,
            LastCaptureToEncodeStartAgeMs: 1125,
            LastEncodeDurationMs: 320,
            SupersededPendingRawFrameCount: 3));

        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 13, 18, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-source-freshness", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "sender freshness start");
        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource);

        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(ScreenShareTransportTuningLevel.BandwidthReduced, fakeSource.LastTransportTuningLevel);
        Assert.Equal(0, fakeSource.PurgePendingRawFramesCallCount);
        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);

        var logText = LocalOperationalLog.GetRecentLogText();
        Assert.Contains("event=screenshare_freshness_summary", logText, StringComparison.Ordinal);
        Assert.Contains("source_pending_raw_frames=", logText, StringComparison.Ordinal);
        Assert.Contains("capture_to_encode_start_age_ms=", logText, StringComparison.Ordinal);
        Assert.Contains("last_preprocess_duration_ms=", logText, StringComparison.Ordinal);
        Assert.Contains("last_transform_encode_duration_ms=", logText, StringComparison.Ordinal);
        Assert.Contains("last_encode_total_duration_ms=", logText, StringComparison.Ordinal);
        Assert.Contains("encoder_path=", logText, StringComparison.Ordinal);
        Assert.Contains("encoder_profile=", logText, StringComparison.Ordinal);
        Assert.Contains("source_superseded_pending_frames=", logText, StringComparison.Ordinal);
        Assert.Contains("raw_frames_deferred_to_encode_slot=", logText, StringComparison.Ordinal);
        Assert.Contains("raw_frames_replaced_before_encode_slot=", logText, StringComparison.Ordinal);
        Assert.Contains("raw_encode_slot_empty_count=", logText, StringComparison.Ordinal);
        Assert.Contains("raw_slot_coalescing_active=", logText, StringComparison.Ordinal);
        Assert.Contains("promotion_capture_to_send_budget_ms=", logText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RepeatedSevereSenderPressure_DoesNotSpamKeyframesAfterCatchUpEntry()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 13, 18, 10, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-sender-rate-limit", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "sender rate-limit start");
        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource);

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            PendingRawFrameCount: 1,
            OldestPendingRawFrameAgeMs: 1500,
            LastCaptureToEncodeStartAgeMs: 1400,
            LastEncodeDurationMs: 340,
            SupersededPendingRawFrameCount: 2));
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            PendingRawFrameCount: 1,
            OldestPendingRawFrameAgeMs: 1600,
            LastCaptureToEncodeStartAgeMs: 1450,
            LastEncodeDurationMs: 360,
            SupersededPendingRawFrameCount: 3));
        clock.Advance(TimeSpan.FromSeconds(1));
        autoTuneTick.Invoke(coordinator, Array.Empty<object>());

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            PendingRawFrameCount: 1,
            OldestPendingRawFrameAgeMs: 1700,
            LastCaptureToEncodeStartAgeMs: 1500,
            LastEncodeDurationMs: 370,
            SupersededPendingRawFrameCount: 4));
        clock.Advance(TimeSpan.FromSeconds(1));
        autoTuneTick.Invoke(coordinator, Array.Empty<object>());

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            PendingRawFrameCount: 1,
            OldestPendingRawFrameAgeMs: 1800,
            LastCaptureToEncodeStartAgeMs: 1550,
            LastEncodeDurationMs: 380,
            SupersededPendingRawFrameCount: 5));
        clock.Advance(TimeSpan.FromMilliseconds(2100));
        autoTuneTick.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal(
            0,
            fakeSource.KeyFrameRequestReasons.Count(static reason => string.Equals(reason, "sender_profile_changed", StringComparison.Ordinal)));
        Assert.Equal(
            1,
            fakeSource.KeyFrameRequestReasons.Count(static reason => string.Equals(reason, "catch_up_mode_enter", StringComparison.Ordinal)));
        Assert.Equal(1, fakeSource.PurgePendingRawFramesCallCount);
        Assert.Equal("catch_up", coordinator.GetMetricsSnapshot().FreshnessMode);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_SevereBridgeHealthDegradation_EntersDegradedModeImmediately()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var backpressureProbe = new FakeScreenShareBackpressureProbe
        {
            RecentHealthIssueCount = 3,
            IsHealthSeverelyDegraded = true,
            IsCongested = true,
            QueueDepth = 10,
        };
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 2, 12, 30, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock,
            transportBackpressureProbeResolver: () => backpressureProbe);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-severe-bridge-health", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "severe bridge health start");
        DisableStartupWarmupForCoordinatorOnly(coordinator);

        fakeSource.RaiseFrame(
            CreateTransportFrameEventArgs(
                1280,
                720,
                new byte[] { 1, 2, 3 },
                capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));

        await Task.Delay(50);
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(ScreenShareTransportTuningLevel.BandwidthReduced, fakeSource.LastTransportTuningLevel);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_UnstablePressure_DropsQueuedFrames_ToKeepFreshestOnly()
    {
        using var autoTuneFlag = new EnvironmentOverride("NLINK_FEATURE_SCREENCAP_TRANSPORT_AUTOTUNE", "1");
        var fakeSource = new FakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 8, 12, 45, 0, TimeSpan.Zero));
        var probe = new ScreenShareSendProbe(recentPayloadCapacity: 4, maxInFlight: 1, startBlocked: true);
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: probe.SendReadOnlyPayloadAsync,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-freshest-only", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "freshest-only start");

        RaiseTransportFrame(fakeSource, 640, 360, new byte[] { 1 });
        await AwaitCompletesAsync(
            probe.FirstSendStarted,
            TimeSpan.FromSeconds(2),
            "freshest-only blocked first send");

        for (byte marker = 2; marker <= 4; marker++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            RaiseTransportFrame(fakeSource, 640, 360, new[] { marker });
        }

        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

        clock.Advance(TimeSpan.FromMilliseconds(500));
        RaiseTransportFrame(fakeSource, 640, 360, new byte[] { 5 });

        probe.ReleaseBlockedSends();
        await AwaitCompletesAsync(
            probe.WaitForPayloadCountAsync(2, TimeSpan.FromSeconds(2)),
            TimeSpan.FromSeconds(2),
            "freshest-only final payloads");
        await Task.Delay(100);

        var payloads = probe.GetRecentPayloadsSnapshot();
        Assert.Equal(2, probe.PayloadsSent);
        Assert.Equal(2, payloads.Length);

        var firstFragment = Assert.Single(ExpandFragmentsFromPayload(payloads[0]));
        var secondFragment = Assert.Single(ExpandFragmentsFromPayload(payloads[1]));
        Assert.Equal(new byte[] { 1 }, firstFragment.Data);
        Assert.Equal(new byte[] { 5 }, secondFragment.Data);

        var senderMetrics = coordinator.GetMetricsSnapshot();
        Assert.True(senderMetrics.FramesDropped >= 3, $"Expected unstable freshest-only mode to drop stale queued frames. Metrics={senderMetrics}");
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_FileTransferDegradedHint_DropsBacklogAndReducesSenderFps()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 13, 12, 0, 0, TimeSpan.Zero));
        var probe = new ScreenShareSendProbe(recentPayloadCapacity: 4, maxInFlight: 1, startBlocked: true);
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: probe.SendReadOnlyPayloadAsync,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-filetransfer-pressure", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "file-transfer degraded screenshare start");

        coordinator.SetFileTransferDegradedHint(true);
        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);

        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 1 }));
        await AwaitCompletesAsync(
            probe.FirstSendStarted,
            TimeSpan.FromSeconds(2),
            "degraded sender blocked first send");

        for (byte marker = 2; marker <= 5; marker++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new[] { marker }));
        }

        probe.ReleaseBlockedSends();
        await AwaitCompletesAsync(
            probe.WaitForPayloadCountAsync(2, TimeSpan.FromSeconds(2)),
            TimeSpan.FromSeconds(2),
            "degraded sender freshest payloads");

        var payloads = probe.GetRecentPayloadsSnapshot();
        Assert.Equal(2, probe.PayloadsSent);
        Assert.Equal(2, payloads.Length);
        var firstFragment = Assert.Single(ExpandFragmentsFromPayload(payloads[0]));
        var secondFragment = Assert.Single(ExpandFragmentsFromPayload(payloads[1]));
        Assert.Equal(new byte[] { 1 }, firstFragment.Data);
        Assert.Equal(new byte[] { 5 }, secondFragment.Data);

        var senderMetrics = coordinator.GetMetricsSnapshot();
        Assert.Equal("reduced", senderMetrics.FreshnessMode);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_FileTransferDegradedHint_StaysStickyBeforeExiting()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: static (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-sticky-degraded", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "sticky degraded start");

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        coordinator.SetFileTransferDegradedHint(true);
        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);

        coordinator.SetFileTransferDegradedHint(false);
        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);

        DisableStartupWarmupForCoordinatorOnly(coordinator, targetFps: 5);

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 30));
        for (var i = 0; i < 4; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new[] { (byte)(10 + i) }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));
            await WaitUntilAsync(
                () =>
                {
                    var metrics = coordinator.GetMetricsSnapshot();
                    return metrics.ChunksSent >= i + 1 && metrics.LastCaptureToSendAgeMs >= 0;
                },
                TimeSpan.FromSeconds(2));
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal(8, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_FileTransferCatchUpOnlyHint_KeepsSenderDegradedUntilReleased()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 15, 11, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: static (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-catchup-pressure", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "catch-up pressure start");

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        coordinator.SetFileTransferCatchUpOnlyHint(true);
        Assert.Equal(3, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal("catch_up", coordinator.GetMetricsSnapshot().FreshnessMode);

        clock.Advance(TimeSpan.FromSeconds(3));
        coordinator.SetFileTransferCatchUpOnlyHint(true);
        Assert.Equal(3, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal("catch_up", coordinator.GetMetricsSnapshot().FreshnessMode);

        coordinator.SetFileTransferCatchUpOnlyHint(false);
        Assert.Equal(3, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal("catch_up", coordinator.GetMetricsSnapshot().FreshnessMode);

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 30));

        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(250));
            fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new[] { (byte)(20 + i) }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));
            await Task.Delay(20);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);

        for (var i = 0; i < 5; i++)
        {
            SendHealthyRemotePressure(coordinator);
            clock.Advance(TimeSpan.FromMilliseconds(250));
            fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new[] { (byte)(30 + i) }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));
            await Task.Delay(20);
            autoTuneTick.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal(8, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_Metrics_TrackRawSerializedAndBridgeBytes()
    {
        var fakeSource = new FakeScreenCaptureSource();
        var probe = new ScreenShareSendProbe(recentPayloadCapacity: 4);

        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: probe.SendReadOnlyPayloadAsync,
            estimateBridgeBytes: payload => NknBridgePayloadAccounting.MeasureSendFrameBytes(
                destination: "peer.test",
                payload.Span));

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-byte-metrics", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "byte metrics start");

        var frameBytes = Enumerable
            .Range(0, ScreenShareVideoFragmenter.MaxFragmentRawBytes + 137)
            .Select(i => (byte)(i % 251))
            .ToArray();

        RaiseTransportFrame(fakeSource, 1280, 720, frameBytes);

        await AwaitCompletesAsync(
            probe.WaitForPayloadCountAsync(1, TimeSpan.FromSeconds(2)),
            TimeSpan.FromSeconds(2),
            "byte metrics payload send");

        var payloads = probe.GetRecentPayloadsSnapshot();
        var metrics = coordinator.GetMetricsSnapshot();

        Assert.Equal(frameBytes.Length, metrics.RawFrameBytesSent);
        Assert.Equal(payloads.Sum(static payload => payload.Length), metrics.SerializedChunkBytesSent);
        Assert.Equal(
            payloads.Sum(payload => NknBridgePayloadAccounting.MeasureSendFrameBytes("peer.test", payload)),
            metrics.BridgeBytesSent);
        Assert.Equal(1, metrics.TransportPayloadsSent);
        Assert.Equal(1, metrics.BatchedPayloadsSent);
        Assert.Equal(0, metrics.LegacyFragmentPayloadsSent);
        Assert.Equal(0, metrics.OrdinaryNonKeyBatchedPayloadsSent);
        Assert.Equal(0, metrics.OrdinaryNonKeyLegacyPayloadsSent);
        Assert.Equal(1, metrics.KeyframeOrRecoveryBatchedPayloadsSent);
        Assert.True(metrics.AverageFragmentsPerFrame > 1d);
        Assert.True(metrics.AverageTransportPayloadsPerFrame <= 1d);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_OrdinaryNonKeyMultiFragment_BatchesWhenPayloadFits()
    {
        var fakeSource = new FakeScreenCaptureSource();
        var probe = new ScreenShareSendProbe(recentPayloadCapacity: 8);

        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: probe.SendReadOnlyPayloadAsync,
            estimateBridgeBytes: payload => NknBridgePayloadAccounting.MeasureSendFrameBytes(
                destination: "peer.test",
                payload.Span));

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-ordinary-non-key-legacy", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "ordinary non-key legacy start");

        var frameBytes = Enumerable
            .Range(0, ScreenShareVideoFragmenter.MaxFragmentRawBytes + 137)
            .Select(i => (byte)(i % 251))
            .ToArray();
        var expectedFragments = ScreenShareVideoFragmenter.FragmentAccessUnit(
            "session-ordinary-non-key-legacy",
            1,
            0,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            1280,
            720,
            "h264",
            false,
            frameBytes).Count;

        RaiseTransportFrame(fakeSource, 1280, 720, frameBytes, isKeyFrame: false);

        await AwaitCompletesAsync(
            probe.WaitForPayloadCountAsync(1, TimeSpan.FromSeconds(2)),
            TimeSpan.FromSeconds(2),
            "ordinary non-key batched payload send");

        var payloads = probe.GetRecentPayloadsSnapshot();
        var metrics = coordinator.GetMetricsSnapshot();

        Assert.Single(payloads);
        Assert.Equal(expectedFragments, ExpandFragmentsFromPayload(payloads[0]).Length);
        Assert.Equal(1, metrics.TransportPayloadsSent);
        Assert.Equal(1, metrics.BatchedPayloadsSent);
        Assert.Equal(0, metrics.LegacyFragmentPayloadsSent);
        Assert.Equal(1, metrics.OrdinaryNonKeyBatchedPayloadsSent);
        Assert.Equal(0, metrics.OrdinaryNonKeyLegacyPayloadsSent);
        Assert.Equal(0, metrics.KeyframeOrRecoveryBatchedPayloadsSent);
        Assert.True(metrics.AverageTransportPayloadsPerFrame <= 1d);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_OversizedFrame_SplitsIntoMultipleBatches()
    {
        var fakeSource = new FakeScreenCaptureSource();
        var probe = new ScreenShareSendProbe(recentPayloadCapacity: 8);

        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: probe.SendReadOnlyPayloadAsync,
            estimateBridgeBytes: payload => NknBridgePayloadAccounting.MeasureSendFrameBytes(
                destination: "peer.test",
                payload.Span));

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-batch-split", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "batch split start");

        var frameBytes = Enumerable
            .Range(0, (ScreenShareVideoFragmenter.MaxFragmentRawBytes * 6) + 257)
            .Select(i => (byte)(i % 251))
            .ToArray();

        RaiseTransportFrame(fakeSource, 1280, 720, frameBytes);

        await AwaitCompletesAsync(
            probe.WaitForPayloadCountAsync(2, TimeSpan.FromSeconds(2)),
            TimeSpan.FromSeconds(2),
            "batch split payload send");

        var payloads = probe.GetRecentPayloadsSnapshot();
        var expandedFragments = payloads
            .SelectMany(ExpandFragmentsFromPayload)
            .ToArray();
        var metrics = coordinator.GetMetricsSnapshot();

        Assert.True(payloads.Length >= 2, $"Expected multiple batched payloads for the oversized frame. Payloads={payloads.Length}.");
        Assert.All(payloads, payload => Assert.True(ScreenShareVideoPayloadCodec.TryDeserializeFragmentEnvelope(payload, out _, out _)));
        Assert.Equal(
            ScreenShareVideoFragmenter.FragmentAccessUnit(
                "session-batch-split",
                1,
                0,
                expandedFragments[0].CapturedTsUtcMs,
                1280,
                720,
                "h264",
                true,
                frameBytes).Count,
            expandedFragments.Length);
        Assert.True(metrics.TransportPayloadsSent >= 2);
        Assert.Equal(metrics.TransportPayloadsSent, metrics.BatchedPayloadsSent);
        Assert.Equal(0, metrics.LegacyFragmentPayloadsSent);
        Assert.True(metrics.AverageTransportPayloadsPerFrame > 1d);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryOwnerKeyframe_StillBatchesWhenFit()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 23, 10, 30, 0, TimeSpan.Zero));
        var sentPayloads = new ConcurrentQueue<(byte[] Payload, string? Role, long BurstToken)>();

        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock,
            estimateBridgeBytes: payload => NknBridgePayloadAccounting.MeasureSendFrameBytes(
                destination: "peer.test",
                payload.Span),
            sendPayloadWithRecoveryMetadataAsync: (payload, recoverySendRole, recoveryBurstTransportFallbackToken, _) =>
            {
                sentPayloads.Enqueue((payload.ToArray(), recoverySendRole, recoveryBurstTransportFallbackToken));
                return Task.CompletedTask;
            });

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-recovery-owner-batch", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "recovery owner batch start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 12,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryBurstStreamEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        var frameBytes = Enumerable
            .Range(0, ScreenShareVideoFragmenter.MaxFragmentRawBytes + 137)
            .Select(i => (byte)(i % 251))
            .ToArray();
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
            1280,
            720,
            frameBytes,
            capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
            streamEpoch: recoveryBurstStreamEpoch,
            isKeyFrame: true));

        await WaitUntilAsync(
            () => sentPayloads.Any(payload => string.Equals(payload.Role, "owner", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(2));

        var sentPayloadSnapshot = sentPayloads.ToArray();
        var metrics = coordinator.GetMetricsSnapshot();

        Assert.Contains(sentPayloadSnapshot, payload => string.Equals(payload.Role, "owner", StringComparison.Ordinal));
        Assert.Contains(sentPayloadSnapshot, payload => ExpandFragmentsFromPayload(payload.Payload).Length > 1);
        Assert.True(metrics.BatchedPayloadsSent > 0);
        Assert.True(metrics.KeyframeOrRecoveryBatchedPayloadsSent > 0);
        Assert.Equal(0, metrics.OrdinaryNonKeyBatchedPayloadsSent);
        Assert.Equal(0, metrics.OrdinaryNonKeyLegacyPayloadsSent);
    }

    [Fact]
    [Trait("Category", "LocalStress")]
    public async Task TransportScreenShareCoordinator_RapidFrames_AreThrottledForTransport_StressLoop()
    {
        for (var iteration = 1; iteration <= 50; iteration++)
        {
            await RunRapidFramesThrottledScenarioAsync($"iteration {iteration}");
        }
    }

    [Fact]
    [Trait("Category", "LocalStress")]
    public async Task ScreenShare_RapidFrames_Stop_IsStable_Repeated()
    {
        const int iterations = 20;

        using var globalTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        for (var iteration = 1; iteration <= iterations; iteration++)
        {
            await AwaitCompletesAsync(
                RunRapidFramesThrottledScenarioAsync($"repeat {iteration}"),
                TimeSpan.FromSeconds(2),
                $"repeat {iteration}: rapid frames stop scenario");

            Assert.False(
                globalTimeoutCts.IsCancellationRequested,
                $"Timed out after iteration {iteration} of {iterations} in repeated rapid-frames stop stability test.");
        }
    }

    private static async Task RunRapidFramesThrottledScenarioAsync(string iterationLabel)
    {
        var fakeSource = new FakeScreenCaptureSource();
        var probe = new ScreenShareSendProbe(recentPayloadCapacity: 4);
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 3, 18, 0, 0, TimeSpan.Zero));

        var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: probe.SendReadOnlyPayloadAsync,
            clock: clock);

        try
        {
            await AwaitCompletesAsync(
                coordinator.StartAsync("session-live", CancellationToken.None),
                TimeSpan.FromSeconds(2),
                $"{iterationLabel}: transport start");
            DisableStartupWarmupForCoordinatorOnly(coordinator);

            for (var i = 0; i < 5; i++)
            {
                RaiseTransportFrame(fakeSource, 1, 1, new byte[] { (byte)(i + 1) });
                // Keep frame cadence well above the transport min interval (8 FPS -> 125 ms)
                // so this scenario remains deterministic even if the default transport cap changes.
                clock.Advance(TimeSpan.FromMilliseconds(40));
            }

            await AwaitCompletesAsync(
                probe.WaitForPayloadCountAsync(1, TimeSpan.FromSeconds(2)),
                TimeSpan.FromSeconds(2),
                $"{iterationLabel}: first throttled payload send");
            await Task.Delay(350);
            await AwaitCompletesAsync(
                coordinator.StopAsync(sendStopMessage: false, reason: null, CancellationToken.None),
                TimeSpan.FromSeconds(2),
                $"{iterationLabel}: throttled stop");

            var sentPayloads = probe.GetRecentPayloadsSnapshot();
            Assert.InRange(probe.PayloadsSent, 1, 2);
            Assert.InRange(sentPayloads.Length, 1, 2);
            var firstChunk = Assert.Single(ExpandFragmentsFromPayload(sentPayloads[0]));
            Assert.Equal("session-live", firstChunk.SessionId);
            Assert.Equal(0, firstChunk.FrameId);
            if (sentPayloads.Length > 1)
            {
                var secondChunk = Assert.Single(ExpandFragmentsFromPayload(sentPayloads[1]));
                Assert.Equal("session-live", secondChunk.SessionId);
                Assert.Equal(1, secondChunk.FrameId);
            }
        }
        finally
        {
            await AwaitCompletesAsync(
                coordinator.DisposeAsync().AsTask(),
                TimeSpan.FromSeconds(2),
                $"{iterationLabel}: transport dispose");
        }
    }

    private static void DriveTransportFrames(
        FakeScreenCaptureSource fakeSource,
        FakeScreenShareClock clock,
        int count,
        TimeSpan advancePerFrame)
    {
        for (var i = 0; i < count; i++)
        {
            RaiseTransportFrame(fakeSource, 1, 1, new byte[] { (byte)((i % 250) + 1) });
            clock.Advance(advancePerFrame);
        }
    }

    private static async Task RunStopOrDisconnectUnderLoadScenarioAsync(
        string scenarioLabel,
        Func<TransportScreenShareCoordinator, Task> stopAsync)
    {
        using var unobserved = new UnobservedTaskExceptionRecorder();
        var fakeSource = new FakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 4, 12, 0, 0, TimeSpan.Zero));
        var sendEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: async (_, ct) =>
            {
                sendEntered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            },
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-load", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            $"{scenarioLabel}: start");
        DisableStartupWarmupForCoordinatorOnly(coordinator);

        RaiseTransportFrame(fakeSource, 640, 360, new byte[] { 0, 1, 2 });
        await AwaitCompletesAsync(
            sendEntered.Task,
            TimeSpan.FromSeconds(2),
            $"{scenarioLabel}: blocked send entry");

        for (var frameIndex = 1; frameIndex <= 24; frameIndex++)
        {
            RaiseTransportFrame(fakeSource, 640, 360, new byte[] { (byte)frameIndex, 7, 9 });
            clock.Advance(TimeSpan.FromMilliseconds(500));
        }

        await AwaitCompletesAsync(
            stopAsync(coordinator),
            TimeSpan.FromSeconds(2),
            $"{scenarioLabel}: stop/disconnect");

        Assert.False(coordinator.IsActive);
        Assert.False(fakeSource.IsStarted);

        ForceFullCollection();
        Assert.Empty(unobserved.Exceptions);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_StartStopCycles_RemainStable_AndReleaseCaptureSubscription()
    {
        const int cycleCount = 100;
        const int framesPerCycle = 3;

        ForceFullCollection();
        var memoryBeforeBytes = GC.GetTotalMemory(forceFullCollection: true);
        var fakeSource = new FakeScreenCaptureSource();
        var probe = new ScreenShareSendProbe(recentPayloadCapacity: 8);
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 4, 9, 0, 0, TimeSpan.Zero));

        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: probe.SendReadOnlyPayloadAsync,
            clock: clock);

        for (var cycle = 1; cycle <= cycleCount; cycle++)
        {
            await AwaitCompletesAsync(
                coordinator.StartAsync("session-cycle", CancellationToken.None),
                TimeSpan.FromSeconds(2),
                $"cycle {cycle}: start");

            Assert.True(fakeSource.IsStarted, $"Expected capture source to start in cycle {cycle}.");
            Assert.Equal(1, fakeSource.FrameSubscriberCount);

            for (var frameIndex = 0; frameIndex < framesPerCycle; frameIndex++)
            {
                var expectedPayloadCount = ((cycle - 1) * framesPerCycle) + frameIndex + 1;
                RaiseTransportFrame(fakeSource, 640, 360, new byte[] { (byte)cycle, (byte)frameIndex, 42 });
                await AwaitCompletesAsync(
                    probe.WaitForPayloadCountAsync(expectedPayloadCount, TimeSpan.FromSeconds(2)),
                    TimeSpan.FromSeconds(2),
                    $"cycle {cycle}: payload {frameIndex + 1}");
                clock.Advance(TimeSpan.FromMilliseconds(500));
            }

            if ((cycle % 2) == 0)
            {
                await AwaitCompletesAsync(
                    coordinator.HandleDisconnectedAsync(),
                    TimeSpan.FromSeconds(2),
                    $"cycle {cycle}: disconnect stop");
            }
            else
            {
                await AwaitCompletesAsync(
                    coordinator.StopAsync(sendStopMessage: false, reason: null, CancellationToken.None),
                    TimeSpan.FromSeconds(2),
                    $"cycle {cycle}: explicit stop");
            }

            Assert.False(fakeSource.IsStarted, $"Expected capture source to stop in cycle {cycle}.");
            Assert.Equal(0, fakeSource.FrameSubscriberCount);
            Assert.False(coordinator.IsActive, $"Coordinator should be inactive after cycle {cycle} stop.");
        }

        Assert.Equal(cycleCount, fakeSource.StartCallCount);
        Assert.Equal(cycleCount, fakeSource.StopCallCount);
        Assert.Equal(cycleCount, fakeSource.DisposeCallCount);
        Assert.Equal(cycleCount * framesPerCycle, probe.PayloadsSent);

        ForceFullCollection();
        var memoryAfterBytes = GC.GetTotalMemory(forceFullCollection: true);
        var memoryDeltaBytes = memoryAfterBytes - memoryBeforeBytes;
        Assert.True(
            memoryDeltaBytes <= 4 * 1024 * 1024,
            $"Expected bounded memory growth after coordinator cycles. DeltaBytes={memoryDeltaBytes}, Starts={fakeSource.StartCallCount}, Stops={fakeSource.StopCallCount}, Disposals={fakeSource.DisposeCallCount}, Payloads={probe.PayloadsSent}.");
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeePageViewModel_ScreenShareStartFailure_ShowsViewerError_AndCleansUp()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var view = new ScreenSharePlaceholderView
            {
                DataContext = new ScreenShareViewerErrorContext(),
            };
            var window = new Window { Width = 640, Height = 420, Content = view };
            window.Show();

            try
            {
                await WaitUntilAsync(
                    () => FindViewerMessageText(window) is { IsVisible: true, Text: "Screen sharing failed to start" },
                    TimeSpan.FromSeconds(2));
            }
            finally
            {
                window.Close();
            }

            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeeScreenShareCoordinator_InvalidFrame_DoesNotKillDecodeLoop_AndSubsequentFrameApplies()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var fakeSource = new FakeScreenCaptureSource();
            var active = false;
            Bitmap? currentFrame = null;
            var decodeCalls = 0;

            var coordinator = new HelpeeScreenShareCoordinator(
                isDisposed: static () => false,
                canShowScreenShareAction: static () => true,
                isPreviewActive: () => active,
                captureSourceFactory: new FixedCaptureSourceFactory(fakeSource),
                setPreviewActive: value => active = value,
                setStatus: _ => { },
                getPreviewFrame: () => currentFrame,
                setPreviewFrame: value => currentFrame = value,
                decodeFrame: bytes =>
                {
                    var next = Interlocked.Increment(ref decodeCalls);
                    if (next == 1)
                    {
                        throw new InvalidDataException("invalid preview frame");
                    }

                    return CreateBitmap(bytes[0], 1);
                });

            try
            {
                coordinator.Toggle();
                await WaitUntilAsync(() => active, TimeSpan.FromSeconds(2));

                fakeSource.RaiseFrame(1, 1, new byte[] { 1 }, "jpeg");
                await WaitUntilAsync(() => coordinator.DecodeTasksActive == 0, TimeSpan.FromSeconds(2));
                Assert.Null(currentFrame);

                fakeSource.RaiseFrame(1, 1, new byte[] { 9 }, "jpeg");
                await WaitUntilAsync(
                    () => currentFrame is Bitmap frame && frame.PixelSize.Width == 9,
                    TimeSpan.FromSeconds(2));
                await WaitUntilAsync(
                    () => coordinator.FramesDecoded == 1 && coordinator.DecodeTasksActive == 0,
                    TimeSpan.FromSeconds(2));

                Assert.Equal(2, decodeCalls);
                Assert.Equal(1, coordinator.FramesDecoded);
                Assert.Equal(0, coordinator.DecodeTasksActive);

                await coordinator.StopAsync();
            }
            finally
            {
                currentFrame?.Dispose();
            }

            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_InvalidFrame_DoesNotRaiseUnobservedException_AndLaterFrameStillSends()
    {
        using var unobserved = new UnobservedTaskExceptionRecorder();
        var fakeSource = new FakeScreenCaptureSource();
        var probe = new ScreenShareSendProbe(recentPayloadCapacity: 2);

        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: probe.SendReadOnlyPayloadAsync);

        await coordinator.StartAsync("session-live", CancellationToken.None);

        RaiseTransportFrame(fakeSource, 1, 1, Array.Empty<byte>());
        await Task.Yield();
        ForceFullCollection();
        Assert.Empty(unobserved.Exceptions);

        RaiseTransportFrame(fakeSource, 1, 1, new byte[] { 1, 2, 3 });
        await AwaitCompletesAsync(
            probe.WaitForPayloadCountAsync(1, TimeSpan.FromSeconds(2)),
            TimeSpan.FromSeconds(2),
            "payload after invalid frame");

        Assert.Equal(1, probe.PayloadsSent);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenSharePlaceholderView_States_RenderExpectedSurfaceAndCenteredErrorOverlay()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var remoteFrame = CreateBitmap(640, 360);
            using var previewFrame = CreateBitmap(360, 640);

            var blankView = new ScreenSharePlaceholderView
            {
                DataContext = new ScreenSharePlaceholderContext(),
            };
            var blankWindow = new Window { Width = 1200, Height = 800, Content = blankView };
            blankWindow.Show();

            try
            {
                await WaitUntilAsync(
                    () => blankView.GetVisualDescendants().OfType<Grid>().Any(),
                    TimeSpan.FromSeconds(2));

                var blankImages = blankView.GetVisualDescendants().OfType<Image>().ToArray();
                Assert.Equal(2, blankImages.Length);
                Assert.All(blankImages, image => Assert.False(image.IsVisible));

                var blankRoot = blankView.GetVisualDescendants().OfType<Grid>().First();
                Assert.True(blankRoot.ClipToBounds);
            }
            finally
            {
                blankWindow.Close();
            }

            var remoteView = new ScreenSharePlaceholderView
            {
                DataContext = new ScreenSharePlaceholderContext
                {
                    ShowRemoteScreenShareFrame = true,
                    RemoteFrame = remoteFrame,
                },
            };
            var remoteWindow = new Window { Width = 1280, Height = 900, Content = remoteView };
            remoteWindow.Show();

            try
            {
                await WaitUntilAsync(
                    () => remoteView.GetVisualDescendants().OfType<Image>().Any(image => image.IsVisible),
                    TimeSpan.FromSeconds(2));

                var remoteImages = remoteView.GetVisualDescendants().OfType<Image>().ToArray();
                Assert.True(remoteImages[0].IsVisible);
                Assert.False(remoteImages[1].IsVisible);
                Assert.Same(remoteFrame, remoteImages[0].Source);
            }
            finally
            {
                remoteWindow.Close();
            }

            var previewView = new ScreenSharePlaceholderView
            {
                DataContext = new ScreenSharePlaceholderContext
                {
                    ShowScreenSharePreviewFrame = true,
                    PreviewFrame = previewFrame,
                },
            };
            var previewWindow = new Window { Width = 900, Height = 1200, Content = previewView };
            previewWindow.Show();

            try
            {
                await WaitUntilAsync(
                    () => previewView.GetVisualDescendants().OfType<Image>().Any(image => image.IsVisible),
                    TimeSpan.FromSeconds(2));

                var previewImages = previewView.GetVisualDescendants().OfType<Image>().ToArray();
                Assert.False(previewImages[0].IsVisible);
                Assert.True(previewImages[1].IsVisible);
                Assert.Same(previewFrame, previewImages[1].Source);
            }
            finally
            {
                previewWindow.Close();
            }

            var errorView = new ScreenSharePlaceholderView
            {
                DataContext = new ScreenSharePlaceholderContext
                {
                    ShowScreenShareViewerError = true,
                    ScreenShareViewerMessage = "Screen sharing failed to start",
                },
            };
            var errorWindow = new Window { Width = 1400, Height = 900, Content = errorView };
            errorWindow.Show();

            try
            {
                await WaitUntilAsync(
                    () => FindViewerMessageText(errorWindow) is { IsVisible: true },
                    TimeSpan.FromSeconds(2));

                var overlay = errorView.GetVisualDescendants()
                    .OfType<Border>()
                    .First(border => border.Child is TextBlock);
                var message = Assert.IsType<TextBlock>(overlay.Child);

                Assert.Equal(HorizontalAlignment.Center, overlay.HorizontalAlignment);
                Assert.Equal(VerticalAlignment.Center, overlay.VerticalAlignment);
                Assert.Equal(new Thickness(16), overlay.Margin);
                Assert.Equal(360d, message.MaxWidth);
                Assert.True(overlay.IsVisible);
                Assert.Equal("Screen sharing failed to start", message.Text);
            }
            finally
            {
                errorWindow.Close();
            }

            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenSharePlaceholderView_WhenWindowIsLarger_SurfaceBoundsIncreaseWithoutDecodeChanges()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var remoteFrame = CreateBitmap(640, 360);

            var smallView = new ScreenSharePlaceholderView
            {
                DataContext = new ScreenSharePlaceholderContext
                {
                    ShowRemoteScreenShareFrame = true,
                    RemoteFrame = remoteFrame,
                },
            };
            var smallWindow = new Window { Width = 760, Height = 560, Content = smallView };
            smallWindow.Show();

            Rect smallBounds;
            try
            {
                await WaitUntilAsync(() => smallView.Bounds.Width > 0 && smallView.Bounds.Height > 0, TimeSpan.FromSeconds(2));
                smallBounds = smallView.Bounds;
            }
            finally
            {
                smallWindow.Close();
            }

            var largeView = new ScreenSharePlaceholderView
            {
                DataContext = new ScreenSharePlaceholderContext
                {
                    ShowRemoteScreenShareFrame = true,
                    RemoteFrame = remoteFrame,
                },
            };
            var largeWindow = new Window { Width = 1280, Height = 860, Content = largeView };
            largeWindow.Show();

            try
            {
                await WaitUntilAsync(() => largeView.Bounds.Width > 0 && largeView.Bounds.Height > 0, TimeSpan.FromSeconds(2));
                var largeBounds = largeView.Bounds;

                Assert.True(
                    largeBounds.Width > smallBounds.Width,
                    $"Expected screenshare surface width to grow with a larger window. Before={smallBounds.Width:N1}, After={largeBounds.Width:N1}.");
                Assert.True(
                    largeBounds.Height > smallBounds.Height,
                    $"Expected screenshare surface height to grow with a larger window. Before={smallBounds.Height:N1}, After={largeBounds.Height:N1}.");
            }
            finally
            {
                largeWindow.Close();
            }

            return true;
        }, default);
    }

    private static void RaiseTransportFrame(
        FakeScreenCaptureSource fakeSource,
        int width,
        int height,
        byte[] encodedFrameBytes,
        long capturedTsUtcMs = 0,
        long streamEpoch = 1,
        bool isKeyFrame = true)
    {
        fakeSource.RaiseFrame(
            CreateTransportFrameEventArgs(
                width,
                height,
                encodedFrameBytes,
                capturedTsUtcMs,
                streamEpoch,
                isKeyFrame));
    }

    private static ScreenCaptureFrameEventArgs CreateTransportFrameEventArgs(
        int width,
        int height,
        byte[] encodedFrameBytes,
        long capturedTsUtcMs = 0,
        long streamEpoch = 1,
        bool isKeyFrame = true)
    {
        return new ScreenCaptureFrameEventArgs(
            width,
            height,
            encodedFrameBytes,
            "h264",
            capturedTsUtcMs <= 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : capturedTsUtcMs,
            isKeyFrame,
            streamEpoch,
            new ScreenShareVideoStreamConfigV1
            {
                SessionId = "session-live",
                StreamEpoch = streamEpoch,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            });
    }

    private static Bitmap CreateTinyBitmap()
    {
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/a5kAAAAASUVORK5CYII=");
        using var stream = new MemoryStream(bytes, writable: false);
        return new Bitmap(stream);
    }

    private static ScreenShareVideoFragmentV1[] ExpandFragmentsFromPayload(byte[] payload)
    {
        Assert.True(
            ScreenShareVideoPayloadCodec.TryDeserializeFragmentEnvelope(payload, out var fragments, out _),
            "Expected payload to deserialize as either a legacy fragment or a fragment batch.");
        return fragments;
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

    private static void DeliverMatchingHelperVisibleReceipt(
        TransportScreenShareCoordinator coordinator,
        string sessionId,
        long streamEpoch,
        long ownerFrameId,
        long? visibleRecoveryFrameId = null,
        long? visibleHeadFrameId = null)
    {
        var effectiveVisibleRecoveryFrameId = visibleRecoveryFrameId ?? ownerFrameId;
        var effectiveVisibleHeadFrameId = Math.Max(
            effectiveVisibleRecoveryFrameId,
            visibleHeadFrameId ?? effectiveVisibleRecoveryFrameId);
        coordinator.SetRemoteRecoveryReceipt(
            new ScreenShareRecoveryReceiptV1
            {
                SessionId = sessionId,
                StreamEpoch = streamEpoch,
                OwnerFrameId = ownerFrameId,
                VisibleRecoveryFrameId = effectiveVisibleRecoveryFrameId,
                VisibleHeadFrameId = effectiveVisibleHeadFrameId,
                ReceiptKind = effectiveVisibleRecoveryFrameId == ownerFrameId
                    ? ScreenShareRecoveryReceiptCodec.RecoveryKeyframeVisibleReceiptKind
                    : ScreenShareRecoveryReceiptCodec.VisibleProgressAfterRecoveryKeyframeReceiptKind,
            });
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

            await TryPumpUiThreadOnceAsync().ConfigureAwait(false);
            await Task.Delay(10).ConfigureAwait(false);
        }

        Assert.True(predicate(), $"Condition not met within {timeout.TotalSeconds:N1}s.");
    }

    private static async Task TryPumpUiThreadOnceAsync()
    {
        try
        {
            var pumpTask = Dispatcher.UIThread
                .InvokeAsync(static () => { }, DispatcherPriority.Background)
                .GetTask();
            var completed = await Task.WhenAny(pumpTask, Task.Delay(25)).ConfigureAwait(false);
            if (ReferenceEquals(completed, pumpTask))
            {
                await pumpTask.ConfigureAwait(false);
            }
        }
        catch
        {
            // Best-effort UI pump for tests. If dispatcher is unavailable/stalled, continue polling.
        }
    }

    private static void WaitForSignal(Task signal, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!signal.IsCompleted && DateTime.UtcNow < deadline)
        {
            Thread.Yield();
        }

        Assert.True(signal.IsCompleted, $"Signal was not completed within {timeout.TotalSeconds:N1}s.");
        Assert.False(signal.IsCanceled, "Signal was canceled unexpectedly.");
        Assert.False(signal.IsFaulted, $"Signal faulted unexpectedly: {signal.Exception}");
    }

    private static void ForceFullCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static async Task AwaitCompletesAsync(Task operation, TimeSpan timeout, string phase)
    {
        using var timeoutCts = new CancellationTokenSource();
        var timeoutTask = Task.Delay(timeout, timeoutCts.Token);
        var completed = await Task.WhenAny(operation, timeoutTask);
        if (!ReferenceEquals(completed, operation))
        {
            Assert.Fail($"Timed out waiting for {phase} after {timeout.TotalSeconds:N1}s.");
        }

        timeoutCts.Cancel();
        await operation;
    }

    private static void DisableStartupWarmupForAutoTuneTests(
        TransportScreenShareCoordinator coordinator,
        AdaptiveFakeScreenCaptureSource fakeSource,
        int? initialFpsHint = null)
    {
        if (typeof(TransportScreenShareCoordinator)
                .GetField("autoTuneTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(coordinator) is Timer autoTuneTimer)
        {
            autoTuneTimer.Dispose();
            typeof(TransportScreenShareCoordinator)
                .GetField("autoTuneTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(coordinator, null);
        }

        var targetFps = initialFpsHint ?? FeatureFlags.ScreenShareTransportMaxFps;
        SetPrivateFieldValue(coordinator, "startupWarmupUntilUtc", default(DateTimeOffset));
        SetPrivateFieldValue(coordinator, "captureFpsHint", targetFps);
        SetPrivateFieldValue(coordinator, "captureToSendCatchUpPressureTicks", 0);
        SetPrivateFieldValue(coordinator, "remoteObservedCatchUpPressureTicks", 0);
        SetPrivateFieldValue(coordinator, "normalToReducedPressureTicks", 0);
        SetPrivateFieldValue(coordinator, "catchUpRecoveryLowPressureTicks", 0);
        SetPrivateFieldValue(coordinator, "reducedRecoveryLowPressureTicks", 0);
        SetPrivateFieldValue(coordinator, "remoteHighFrameAgeCatchUpEntryConsecutiveTicks", 0);
        SetPrivateFieldValue(coordinator, "senderCatchUpEnteredDueToRemoteHighFrameAgeCount", 0L);
        SetPrivateFieldValue(coordinator, "transitionActive", false);
        SetPrivateFieldValue(coordinator, "transitionStreamEpoch", 0L);
        SetPrivateFieldValue(coordinator, "transitionStartedUtc", default(DateTimeOffset));
        SetPrivateFieldValue(coordinator, "transitionFirstRemoteApplySeen", false);
        SetPrivateFieldValue(coordinator, "transitionRemoteApplyCount", 0);
        SetPrivateFieldValue(coordinator, "recoveryLockActive", false);
        SetPrivateFieldValue(coordinator, "recoveryLockStreamEpoch", 0L);
        SetPrivateFieldValue(coordinator, "recoveryLockStartedUtc", default(DateTimeOffset));
        SetPrivateFieldValue(coordinator, "recoveryLockReason", string.Empty);
        SetPrivateFieldValue(coordinator, "recoveryTimeoutResetIssued", false);
        SetPrivateFieldValue(coordinator, "recoveryTimeoutResetCount", 0);
        SetPrivateFieldValue(coordinator, "helperCurrentEpochStateStreamEpoch", 0L);
        SetPrivateFieldValue(coordinator, "helperCurrentEpochWarmupActive", true);
        SetPrivateFieldValue(coordinator, "helperCurrentEpochApplyCount", 0);
        SetPrivateFieldValue(coordinator, "helperCurrentEpochNeedMoreInputCount", 0L);
        SetPrivateFieldValue(coordinator, "helperCurrentEpochHealthySignalCount", 0);
        SetPrivateFieldValue(coordinator, "helperCurrentEpochStaleDrops", 0L);
        fakeSource.SetCaptureFrameRateHint(targetFps);

        var sendPipeline = typeof(TransportScreenShareCoordinator)
            .GetField("sendPipeline", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(coordinator) as ScreenShareFrameSendPipeline;
        sendPipeline?.SetMaxFramesPerSecond(targetFps);
    }

    private static void DisableStartupWarmupForCoordinatorOnly(
        TransportScreenShareCoordinator coordinator,
        int targetFps = 8)
    {
        if (typeof(TransportScreenShareCoordinator)
                .GetField("autoTuneTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(coordinator) is Timer autoTuneTimer)
        {
            autoTuneTimer.Dispose();
            typeof(TransportScreenShareCoordinator)
                .GetField("autoTuneTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(coordinator, null);
        }

        SetPrivateFieldValue(coordinator, "startupWarmupUntilUtc", default(DateTimeOffset));
        SetPrivateFieldValue(coordinator, "captureFpsHint", targetFps);
        SetPrivateFieldValue(coordinator, "senderFreshnessMode", ScreenShareSenderFreshnessMode.Normal);
        SetPrivateFieldValue(coordinator, "transportTuningLevel", ScreenShareTransportTuningLevel.Normal);
        SetPrivateFieldValue(coordinator, "preferFreshestPendingFrameOnly", 0);
        SetPrivateFieldValue(coordinator, "captureToSendCatchUpPressureTicks", 0);
        SetPrivateFieldValue(coordinator, "remoteObservedCatchUpPressureTicks", 0);
        SetPrivateFieldValue(coordinator, "normalToReducedPressureTicks", 0);
        SetPrivateFieldValue(coordinator, "catchUpRecoveryLowPressureTicks", 0);
        SetPrivateFieldValue(coordinator, "reducedRecoveryLowPressureTicks", 0);
        SetPrivateFieldValue(coordinator, "remoteHighFrameAgeCatchUpEntryConsecutiveTicks", 0);
        SetPrivateFieldValue(coordinator, "senderCatchUpEnteredDueToRemoteHighFrameAgeCount", 0L);
        SetPrivateFieldValue(coordinator, "transitionActive", false);
        SetPrivateFieldValue(coordinator, "transitionStreamEpoch", 0L);
        SetPrivateFieldValue(coordinator, "transitionStartedUtc", default(DateTimeOffset));
        SetPrivateFieldValue(coordinator, "transitionFirstRemoteApplySeen", false);
        SetPrivateFieldValue(coordinator, "transitionRemoteApplyCount", 0);
        SetPrivateFieldValue(coordinator, "recoveryLockActive", false);
        SetPrivateFieldValue(coordinator, "recoveryLockStreamEpoch", 0L);
        SetPrivateFieldValue(coordinator, "recoveryLockStartedUtc", default(DateTimeOffset));
        SetPrivateFieldValue(coordinator, "recoveryLockReason", string.Empty);
        SetPrivateFieldValue(coordinator, "recoveryTimeoutResetIssued", false);
        SetPrivateFieldValue(coordinator, "recoveryTimeoutResetCount", 0);
        SetPrivateFieldValue(coordinator, "helperCurrentEpochStateStreamEpoch", 0L);
        SetPrivateFieldValue(coordinator, "helperCurrentEpochWarmupActive", true);
        SetPrivateFieldValue(coordinator, "helperCurrentEpochApplyCount", 0);
        SetPrivateFieldValue(coordinator, "helperCurrentEpochNeedMoreInputCount", 0L);
        SetPrivateFieldValue(coordinator, "helperCurrentEpochHealthySignalCount", 0);
        SetPrivateFieldValue(coordinator, "helperCurrentEpochStaleDrops", 0L);

        var sendPipeline = typeof(TransportScreenShareCoordinator)
            .GetField("sendPipeline", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(coordinator) as ScreenShareFrameSendPipeline;
        sendPipeline?.SetMaxFramesPerSecond(targetFps);

        var captureSource = typeof(TransportScreenShareCoordinator)
            .GetField("captureSource", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(coordinator);
        if (captureSource is IScreenCaptureAdaptiveTuning adaptiveCaptureSource)
        {
            adaptiveCaptureSource.SetCaptureFrameRateHint(targetFps);
            adaptiveCaptureSource.SetTransportTuningLevel(ScreenShareTransportTuningLevel.Normal);
        }
    }

    private static void SetLastCaptureToSendAgeMs(TransportScreenShareCoordinator coordinator, long captureToSendAgeMs)
    {
        var sendPipeline = typeof(TransportScreenShareCoordinator)
            .GetField("sendPipeline", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(coordinator) as ScreenShareFrameSendPipeline;
        Assert.NotNull(sendPipeline);

        typeof(ScreenShareFrameSendPipeline)
            .GetField("lastCaptureToSendAgeMs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(sendPipeline, captureToSendAgeMs);
    }

    private static T GetPrivateFieldValue<T>(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is not null)
        {
            var value = field.GetValue(target);
            if (typeof(T) == typeof(object))
            {
                return (T)value!;
            }

            return Assert.IsType<T>(value);
        }

        var property = target.GetType().GetProperty(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (property is not null)
        {
            var value = property.GetValue(target);
            if (typeof(T) == typeof(object))
            {
                return (T)value!;
            }

            return Assert.IsType<T>(value);
        }

        if (TryGetLegacyRecoveryFieldValue(target, fieldName, out var remappedValue))
        {
            if (typeof(T) == typeof(object))
            {
                return (T)remappedValue!;
            }

            return Assert.IsType<T>(remappedValue);
        }

        Assert.NotNull(field);
        return Assert.IsType<T>(field!.GetValue(target));
    }

    private static void SetPrivateFieldValue(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is not null)
        {
            field.SetValue(target, value);
            return;
        }

        var property = target.GetType().GetProperty(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (property is not null)
        {
            property.SetValue(target, value);
            return;
        }

        Assert.NotNull(field);
    }

    private static bool TryGetLegacyRecoveryFieldValue(object target, string fieldName, out object? value)
    {
        value = null;
        if (target is not TransportScreenShareCoordinator)
        {
            return false;
        }

        var activeRecoveryBurst = GetNestedPrivateFieldValue(target, "activeRecoveryBurst");
        var lastCompletedRecovery = GetNestedPrivateFieldValue(target, "lastCompletedRecovery");
        value = fieldName switch
        {
            "recoveryBurstActive" => activeRecoveryBurst is not null,
            "recoveryBurstStreamEpoch" => GetNestedPrivateFieldValue(activeRecoveryBurst, "StreamEpoch") ?? 0L,
            "recoveryOwnerFrameId" => GetNestedPrivateFieldValue(activeRecoveryBurst, "OwnerFrameId") ?? -1L,
            "recoveryBurstPhase" => GetNestedPrivateFieldValue(activeRecoveryBurst, "Phase") ?? RecoveryBurstPhase.Idle,
            "lastCompletedRecoveryEpoch" => GetNestedPrivateFieldValue(lastCompletedRecovery, "StreamEpoch") ?? 0L,
            "lastCompletedRecoveryOwnerFrameId" => GetNestedPrivateFieldValue(lastCompletedRecovery, "OwnerFrameId") ?? -1L,
            "lastCompletedRecoveryAckFrameId" => GetNestedPrivateFieldValue(lastCompletedRecovery, "AckFrameId") ?? -1L,
            "lastCompletedRecoveryAckSource" => GetNestedPrivateFieldValue(lastCompletedRecovery, "AckSource") ?? string.Empty,
            "lastCompletedRecoveryOwnerEmitToAckMs" => GetNestedPrivateFieldValue(lastCompletedRecovery, "OwnerEmitToAckMs") ?? -1L,
            "lastCompletedRecoveryCompletionKind" => GetNestedPrivateFieldValue(lastCompletedRecovery, "CompletionKind") ?? string.Empty,
            _ => null,
        };

        return value is not null;
    }

    private static object? GetNestedPrivateFieldValue(object? target, string fieldName)
    {
        if (target is null)
        {
            return null;
        }

        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (field is not null)
        {
            return field.GetValue(target);
        }

        var property = target.GetType().GetProperty(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        return property?.GetValue(target);
    }

    private static void SendHealthyRemotePressure(
        TransportScreenShareCoordinator coordinator,
        long observedFrameAgeMs = 0,
        long recentStaleDrops = 0,
        bool? currentEpochWarmupActive = false,
        int? currentEpochApplyCount = 3,
        long? currentEpochNeedMoreInputCount = 0,
        long? lastVisibleApplyFrameId = null,
        long? visibleHeadFrameId = null,
        long? appliedHeadFrameId = null,
        bool? steadyVisibleProgressActive = null,
        long? stableVisibleHeadFrameId = null,
        long? framesAppliedSinceLastGap = null,
        long? visibleRecoveryFloorFrameId = null,
        long? currentEpochRecoveryKeyframeApplyCount = null)
    {
        coordinator.SetRemotePressureState(
            mode: ScreenShareRemotePressureMode.None,
            reason: ScreenSharePressureProtocol.PressureReasonHealthy,
            observedFrameAgeMs: observedFrameAgeMs,
            recentStaleDrops: recentStaleDrops,
            sentAtUtcMs: 0,
            currentEpochWarmupActive: currentEpochWarmupActive,
            currentEpochApplyCount: currentEpochApplyCount,
            currentEpochNeedMoreInputCount: currentEpochNeedMoreInputCount,
            lastVisibleApplyFrameId: lastVisibleApplyFrameId,
            visibleHeadFrameId: visibleHeadFrameId,
            appliedHeadFrameId: appliedHeadFrameId,
            steadyVisibleProgressActive: steadyVisibleProgressActive,
            stableVisibleHeadFrameId: stableVisibleHeadFrameId,
            framesAppliedSinceLastGap: framesAppliedSinceLastGap,
            visibleRecoveryFloorFrameId: visibleRecoveryFloorFrameId,
            currentEpochRecoveryKeyframeApplyCount: currentEpochRecoveryKeyframeApplyCount);
    }

    private static TextBlock? FindViewerMessageText(Window window)
    {
        return window.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(x =>
                string.Equals(
                    AutomationProperties.GetAutomationId(x),
                    "ScreenShare.ViewerMessage",
                    StringComparison.Ordinal));
    }

    private sealed class FixedCaptureSourceFactory : IScreenCaptureSourceFactory
    {
        private readonly IScreenCaptureSource source;

        public FixedCaptureSourceFactory(IScreenCaptureSource source)
        {
            this.source = source;
        }

        public IScreenCaptureSource Create() => source;
    }

    private sealed class SequenceCaptureSourceFactory : IScreenCaptureSourceFactory
    {
        private readonly Queue<IScreenCaptureSource> sources;

        public SequenceCaptureSourceFactory(params IScreenCaptureSource[] sources)
        {
            this.sources = new Queue<IScreenCaptureSource>(sources);
        }

        public IScreenCaptureSource Create()
        {
            if (sources.Count == 0)
            {
                throw new InvalidOperationException("No capture sources remain in the test factory.");
            }

            return sources.Dequeue();
        }
    }

    private sealed class AdaptiveFakeScreenCaptureSource :
        IScreenCaptureSource,
        IScreenCaptureMetadataSource,
        IScreenCaptureAdaptiveTuning,
        IScreenCaptureKeyFrameRequestSource,
        IScreenCaptureTransportRecoveryResetSource,
        IScreenCaptureFreshnessMetricsSource,
        IAsyncDisposable
    {
        private EventHandler<ScreenCaptureFrameEventArgs>? frameArrived;
        private readonly List<int> captureFrameRateHints = new();
        private readonly List<ScreenShareTransportTuningLevel> transportTuningLevels = new();
        private readonly List<string> keyFrameRequestReasons = new();
        private ScreenCaptureFreshnessMetrics freshnessMetrics = new();

        public bool IsSupported => true;

        public bool IsStarted { get; private set; }

        public int LastCaptureFrameRateHint { get; private set; }

        public IReadOnlyList<int> CaptureFrameRateHints => captureFrameRateHints;

        public ScreenShareTransportTuningLevel LastTransportTuningLevel { get; private set; }

        public IReadOnlyList<ScreenShareTransportTuningLevel> TransportTuningLevels => transportTuningLevels;

        public int PurgePendingRawFramesCallCount { get; private set; }

        public IReadOnlyList<string> KeyFrameRequestReasons => keyFrameRequestReasons;

        public ScreenCaptureMetadata? CaptureMetadata { get; set; }

        public event EventHandler<ScreenCaptureFrameEventArgs>? FrameArrived
        {
            add => frameArrived += value;
            remove => frameArrived -= value;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsStarted = true;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            IsStarted = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsStarted = false;
            frameArrived = null;
            keyFrameRequestReasons.Clear();
            freshnessMetrics = new ScreenCaptureFreshnessMetrics();
            return ValueTask.CompletedTask;
        }

        public bool TryGetCaptureMetadata(out ScreenCaptureMetadata metadata)
        {
            if (CaptureMetadata.HasValue)
            {
                metadata = CaptureMetadata.Value;
                return true;
            }

            metadata = default;
            return false;
        }

        public void SetCaptureFrameRateHint(int maxFramesPerSecond)
        {
            LastCaptureFrameRateHint = maxFramesPerSecond;
            captureFrameRateHints.Add(maxFramesPerSecond);
        }

        public void SetTransportTuningLevel(ScreenShareTransportTuningLevel level)
        {
            if (LastTransportTuningLevel != level)
            {
                var nextEpoch = freshnessMetrics.CurrentStreamEpoch > 0
                    ? freshnessMetrics.CurrentStreamEpoch + 1
                    : 1;
                freshnessMetrics = freshnessMetrics with
                {
                    CurrentStreamEpoch = nextEpoch,
                };
            }

            LastTransportTuningLevel = level;
            transportTuningLevels.Add(level);
        }

        public void RequestKeyFrame(string reason)
        {
            keyFrameRequestReasons.Add(string.IsNullOrWhiteSpace(reason) ? "(none)" : reason.Trim());
        }

        public long ForceTransportRecoveryReset(ScreenShareTransportTuningLevel level)
        {
            LastTransportTuningLevel = level;
            transportTuningLevels.Add(level);
            var nextEpoch = freshnessMetrics.CurrentStreamEpoch > 0
                ? freshnessMetrics.CurrentStreamEpoch + 1
                : 1;
            freshnessMetrics = freshnessMetrics with
            {
                CurrentStreamEpoch = nextEpoch,
            };
            return nextEpoch;
        }

        public ScreenCaptureFreshnessMetrics GetFreshnessMetricsSnapshot()
        {
            return freshnessMetrics;
        }

        public int PurgePendingRawFrames()
        {
            if (freshnessMetrics.PendingRawFrameCount <= 0)
            {
                return 0;
            }

            PurgePendingRawFramesCallCount++;
            freshnessMetrics = freshnessMetrics with
            {
                PendingRawFrameCount = 0,
                OldestPendingRawFrameAgeMs = 0,
            };
            return 1;
        }

        public void SetFreshnessMetrics(ScreenCaptureFreshnessMetrics metrics)
        {
            freshnessMetrics = metrics;
        }

        public void RaiseFrame(ScreenCaptureFrameEventArgs frame)
        {
            frameArrived?.Invoke(this, frame);
        }
    }

    private sealed class ScreenShareViewerErrorContext
    {
        public bool ShowDefaultScreenSharePlaceholder => false;

        public bool ShowScreenShareViewerError => true;

        public string ScreenShareViewerMessage => "Screen sharing failed to start";

        public bool ShowRemoteScreenShareFrame => false;

        public bool ShowScreenSharePreviewFrame => false;
    }

    private sealed class ScreenSharePlaceholderContext
    {
        public bool ShowDefaultScreenSharePlaceholder => !ShowRemoteScreenShareFrame &&
                                                         !ShowScreenSharePreviewFrame &&
                                                         !ShowScreenShareViewerError;

        public bool ShowScreenShareViewerError { get; init; }

        public string ScreenShareViewerMessage { get; init; } = string.Empty;

        public bool ShowRemoteScreenShareFrame { get; init; }

        public Bitmap? RemoteFrame { get; init; }

        public ScreenShareViewerProxy ScreenShareViewer => new(RemoteFrame);

        public bool ShowScreenSharePreviewFrame { get; init; }

        public Bitmap? PreviewFrame { get; init; }

        public Bitmap? ScreenSharePreviewFrame => PreviewFrame;
    }

    private sealed class ScreenShareViewerProxy
    {
        public ScreenShareViewerProxy(Bitmap? currentFrame)
        {
            CurrentFrame = currentFrame;
        }

        public Bitmap? CurrentFrame { get; }
    }

    private sealed class FakeScreenShareClock : IScreenShareClock
    {
        private DateTimeOffset utcNow;

        public FakeScreenShareClock(DateTimeOffset initialUtcNow)
        {
            utcNow = initialUtcNow;
        }

        public DateTimeOffset UtcNow => utcNow;

        public void Advance(TimeSpan by)
        {
            utcNow = utcNow.Add(by);
        }
    }

    private sealed class FakePreviewH264BitmapDecoder : IWindowsH264BitmapDecoder
    {
        public bool IsSupported => true;

        public int NeedMoreInputBeforeSuccessCount { get; set; }

        public int ConfigureCallCount { get; private set; }

        public int DecodeCallCount { get; private set; }

        public int ResetCallCount { get; private set; }

        public long LastConfiguredEpoch { get; private set; }

        public void ConfigureStream(ScreenShareVideoStreamConfigV1 config)
        {
            ConfigureCallCount++;
            LastConfiguredEpoch = config.StreamEpoch;
        }

        public void Reset()
        {
            ResetCallCount++;
        }

        public Bitmap Decode(EncodedFrameDecodeRequest request)
        {
            DecodeCallCount++;
            if (NeedMoreInputBeforeSuccessCount > 0)
            {
                NeedMoreInputBeforeSuccessCount--;
                throw new H264DecoderNeedsMoreInputException("more input required");
            }

            return CreateBitmap(request.EncodedFrameBytes.Span[0], 1);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeScreenShareBackpressureProbe : IScreenShareTransportBackpressureProbe
    {
        public bool IsCongested { get; set; }

        public bool IsSeverelyCongested { get; set; }

        public int QueueDepth { get; set; }

        public int QueuedBytes { get; set; }

        public long OldestQueuedAgeMs { get; set; }

        public long RecentDropCount { get; set; }

        public long RecentHealthIssueCount { get; set; }

        public bool IsHealthSeverelyDegraded { get; set; }

        public bool IsScreenShareTransportCongested => IsCongested;

        public bool IsScreenShareTransportSeverelyCongested => IsSeverelyCongested;

        public int ScreenShareTransportQueueDepth => QueueDepth;

        public int ScreenShareTransportQueuedBytes => QueuedBytes;

        public long ScreenShareTransportOldestQueuedAgeMs => OldestQueuedAgeMs;

        public long ScreenShareTransportRecentDropCount => RecentDropCount;

        public long ScreenShareTransportRecentHealthIssueCount => RecentHealthIssueCount;

        public bool IsScreenShareTransportHealthSeverelyDegraded => IsHealthSeverelyDegraded;
    }

    private sealed class UnobservedTaskExceptionRecorder : IDisposable
    {
        private readonly ConcurrentQueue<Exception> exceptions = new();

        public UnobservedTaskExceptionRecorder()
        {
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        public Exception[] Exceptions => exceptions.ToArray();

        public void Dispose()
        {
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            exceptions.Enqueue(e.Exception);
            e.SetObserved();
        }
    }
}

public sealed class ScreenShareCoordinatorFixture : IDisposable
{
    public ScreenShareCoordinatorFixture()
    {
        Session = Avalonia.Headless.HeadlessUnitTestSession.StartNew(typeof(AvaloniaHeadlessUiAppBootstrap));
    }

    public Avalonia.Headless.HeadlessUnitTestSession Session { get; }

    public void Dispose()
    {
        Session.Dispose();
    }
}
