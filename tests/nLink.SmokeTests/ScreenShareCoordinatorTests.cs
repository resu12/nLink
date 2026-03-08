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
using NLink.Core.ScreenShare;
using NLink.Infra.Nkn;
using NLink.SmokeTests.Fakes;
using System.Collections.Concurrent;

namespace NLink.SmokeTests;

[Collection(AvaloniaHeadlessUiCollection.Name)]
public sealed class ScreenShareCoordinatorTests : IClassFixture<ScreenShareCoordinatorFixture>
{
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
    public async Task HelperScreenShareCoordinator_SlowDecode_CoalescesToLatestFrame()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var decodeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDecode = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Bitmap? currentFrame = null;
            var decodeCount = 0;

            var coordinator = new HelperScreenShareCoordinator(
                isDisposed: static () => false,
                isTransportEnabled: static () => true,
                getRemoteFrame: () => currentFrame,
                setRemoteFrame: value => currentFrame = value,
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
                coordinator.OnFrameCompleted(new ScreenShareFrameCompletedEventArgs(1, 1, 1, "jpeg", new byte[] { 1 }));
                await decodeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

                for (byte i = 2; i <= 5; i++)
                {
                    coordinator.OnFrameCompleted(new ScreenShareFrameCompletedEventArgs(i, 1, 1, "jpeg", new byte[] { i }));
                }

                releaseDecode.TrySetResult(true);
                await WaitUntilAsync(
                    () => currentFrame is Bitmap latest && latest.PixelSize.Width == 5,
                    TimeSpan.FromSeconds(2));
                await WaitUntilAsync(
                    () => coordinator.DecodeTasksActive == 0,
                    TimeSpan.FromSeconds(2));

                Assert.NotNull(currentFrame);
                Assert.Equal(2, decodeCount);
                Assert.Equal(2, coordinator.FramesDecoded);
                Assert.Equal(1, coordinator.MaxDecodeTasksActive);
                Assert.Equal(0, coordinator.DecodeTasksActive);

                coordinator.Clear();
                await WaitUntilAsync(() => currentFrame is null, TimeSpan.FromSeconds(2));
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
    public async Task HelperScreenShareCoordinator_StopPreventsLaterFramesFromApplying()
    {
        await fixture.Session.Dispatch(async () =>
        {
            Bitmap? currentFrame = null;

            var coordinator = new HelperScreenShareCoordinator(
                isDisposed: static () => false,
                isTransportEnabled: static () => true,
                getRemoteFrame: () => currentFrame,
                setRemoteFrame: value => currentFrame = value,
                decodeFrame: _ => CreateTinyBitmap());

            try
            {
                await coordinator.StopAsync();
                await WaitUntilAsync(() => coordinator.DecodeTasksActive == 0, TimeSpan.FromSeconds(1));

                coordinator.OnFrameCompleted(new ScreenShareFrameCompletedEventArgs(1, 1, 1, "jpeg", new byte[] { 1 }));

                await WaitUntilAsync(() => currentFrame is null, TimeSpan.FromSeconds(1));
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
    public async Task Helper_Ingress_Clear_Stop_Idempotent_And_NoStaleApply()
    {
        await fixture.Session.Dispatch(async () =>
        {
            Bitmap? currentFrame = null;
            var decodeCalls = 0;

            var coordinator = new HelperScreenShareCoordinator(
                isDisposed: static () => false,
                isTransportEnabled: static () => true,
                getRemoteFrame: () => currentFrame,
                setRemoteFrame: value => currentFrame = value,
                decodeFrame: bytes =>
                {
                    Interlocked.Increment(ref decodeCalls);
                    return CreateBitmap(bytes[0], 1);
                });

            try
            {
                coordinator.OnFrameCompleted(new ScreenShareFrameCompletedEventArgs(1, 1, 1, "jpeg", new byte[] { 1 }));
                await WaitUntilAsync(
                    () => currentFrame is Bitmap frame && frame.PixelSize.Width == 1,
                    TimeSpan.FromSeconds(2));

                coordinator.Clear();
                await WaitUntilAsync(() => currentFrame is null, TimeSpan.FromSeconds(2));

                await AwaitCompletesAsync(
                    coordinator.StopAsync(),
                    TimeSpan.FromSeconds(2),
                    "helper first stop");
                await AwaitCompletesAsync(
                    coordinator.StopAsync(),
                    TimeSpan.FromSeconds(2),
                    "helper second stop");

                Assert.Equal(0, coordinator.DecodeTasksActive);
                Assert.Null(currentFrame);

                coordinator.OnFrameCompleted(new ScreenShareFrameCompletedEventArgs(2, 1, 1, "jpeg", new byte[] { 2 }));
                await WaitUntilAsync(
                    () => coordinator.DecodeTasksActive == 0,
                    TimeSpan.FromSeconds(2));

                Assert.Null(currentFrame);
                Assert.True(decodeCalls >= 1);
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
    public async Task HelperScreenShareCoordinator_StopDuringBlockedDecode_LeavesNoDecodeTaskRunning()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var decodeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDecode = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Bitmap? currentFrame = null;

            var coordinator = new HelperScreenShareCoordinator(
                isDisposed: static () => false,
                isTransportEnabled: static () => true,
                getRemoteFrame: () => currentFrame,
                setRemoteFrame: value => currentFrame = value,
                decodeFrame: _ =>
                {
                    decodeStarted.TrySetResult(true);
                    WaitForSignal(releaseDecode.Task, TimeSpan.FromSeconds(2));
                    return CreateTinyBitmap();
                });

            try
            {
                coordinator.OnFrameCompleted(new ScreenShareFrameCompletedEventArgs(1, 1, 1, "jpeg", new byte[] { 1 }));
                await AwaitCompletesAsync(
                    decodeStarted.Task,
                    TimeSpan.FromSeconds(2),
                    "helper decode start");

                var stopTask = Task.Run(coordinator.StopAsync);
                releaseDecode.TrySetResult(true);

                await AwaitCompletesAsync(
                    stopTask,
                    TimeSpan.FromSeconds(2),
                    "helper stop during blocked decode");

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
    public async Task HelperScreenShareCoordinator_InvalidFrame_DoesNotKillDecodeLoop_AndSubsequentFrameApplies()
    {
        await fixture.Session.Dispatch(async () =>
        {
            Bitmap? currentFrame = null;
            var decodeCalls = 0;

            var coordinator = new HelperScreenShareCoordinator(
                isDisposed: static () => false,
                isTransportEnabled: static () => true,
                getRemoteFrame: () => currentFrame,
                setRemoteFrame: value => currentFrame = value,
                decodeFrame: bytes =>
                {
                    var next = Interlocked.Increment(ref decodeCalls);
                    if (next == 1)
                    {
                        throw new InvalidDataException("invalid remote frame");
                    }

                    return CreateBitmap(bytes[0], 1);
                });

            try
            {
                coordinator.OnFrameCompleted(new ScreenShareFrameCompletedEventArgs(1, 1, 1, "jpeg", new byte[] { 1 }));
                await WaitUntilAsync(() => coordinator.DecodeTasksActive == 0, TimeSpan.FromSeconds(2));
                Assert.Null(currentFrame);

                coordinator.OnFrameCompleted(new ScreenShareFrameCompletedEventArgs(2, 1, 1, "jpeg", new byte[] { 7 }));
                await WaitUntilAsync(
                    () => currentFrame is Bitmap frame && frame.PixelSize.Width == 7,
                    TimeSpan.FromSeconds(2));
                await WaitUntilAsync(
                    () => coordinator.FramesDecoded == 1 && coordinator.DecodeTasksActive == 0,
                    TimeSpan.FromSeconds(2));

                Assert.Equal(2, decodeCalls);
                Assert.Equal(1, coordinator.FramesDecoded);
                Assert.Equal(0, coordinator.DecodeTasksActive);
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
    public async Task TransportScreenShareCoordinator_Disconnected_StopsCapture_AndPreventsFurtherSends()
    {
        var fakeSource = new FakeScreenCaptureSource();
        var probe = new ScreenShareSendProbe(recentPayloadCapacity: 2);

        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: probe.SendReadOnlyPayloadAsync);

        await coordinator.StartAsync("session-live", CancellationToken.None);
        Assert.True(fakeSource.IsStarted);

        fakeSource.RaiseFrame(1, 1, new byte[] { 1, 2, 3 }, "jpeg");
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

        fakeSource.RaiseFrame(1, 1, new byte[] { 4, 5, 6 }, "jpeg");
        Assert.Equal(1, probe.PayloadsSent);

        var sentPayload = Assert.Single(probe.GetRecentPayloadsSnapshot());
        Assert.True(ScreenSharePayloadCodec.TryDeserialize(sentPayload, out var chunk));
        Assert.Equal("session-live", chunk.SessionId);
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
        fakeSource.RaiseFrame(1280, 720, new byte[] { 1 }, "jpeg");
        await WaitUntilAsync(() => sentRevisions.Count == 1, TimeSpan.FromSeconds(2));

        clock.Advance(TimeSpan.FromMilliseconds(300));
        fakeSource.RaiseFrame(960, 540, new byte[] { 2 }, "jpeg");
        clock.Advance(TimeSpan.FromMilliseconds(300));
        fakeSource.RaiseFrame(1280, 720, new byte[] { 3 }, "jpeg");
        await Task.Delay(100);

        Assert.Equal(1, sentRevisions.Count);
        Assert.Equal(new long[] { 1 }, sentRevisions.ToArray());
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
        fakeSource.RaiseFrame(1280, 720, new byte[] { 1 }, "jpeg");
        await WaitUntilAsync(() => sentRevisions.Count == 1, TimeSpan.FromSeconds(2));

        fakeSource.CaptureMetadata = new ScreenCaptureMetadata(
            DisplayId: "primary",
            CaptureRegionPx: new ScreenCapturePixelRect(100, 50, 1720, 980),
            DpiScale: 1.25);
        fakeSource.RaiseFrame(1280, 720, new byte[] { 2 }, "jpeg");
        clock.Advance(TimeSpan.FromMilliseconds(100));
        fakeSource.RaiseFrame(1280, 720, new byte[] { 3 }, "jpeg");
        await Task.Delay(100);
        Assert.Equal(1, sentRevisions.Count);

        clock.Advance(TimeSpan.FromMilliseconds(200));
        fakeSource.RaiseFrame(1280, 720, new byte[] { 4 }, "jpeg");
        await WaitUntilAsync(() => sentRevisions.Count == 2, TimeSpan.FromSeconds(2));
        Assert.Equal(new long[] { 1, 2 }, sentRevisions.ToArray());
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

        fakeSource.RaiseFrame(1280, 720, new byte[] { 1 }, "jpeg");
        await WaitUntilAsync(() => Volatile.Read(ref attempts) >= 1, TimeSpan.FromSeconds(2));
        await Task.Yield();
        ForceFullCollection();
        Assert.Empty(unobserved.Exceptions);

        fakeSource.RaiseFrame(1280, 720, new byte[] { 2 }, "jpeg");
        await WaitUntilAsync(() => sentRevisions.Count == 1, TimeSpan.FromSeconds(2));

        Assert.Equal(2, Volatile.Read(ref attempts));
        Assert.Equal(new long[] { 2 }, sentRevisions.ToArray());
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

        fakeSource.RaiseFrame(1280, 720, new byte[] { 1 }, "jpeg");
        await AwaitCompletesAsync(
            staleSendStarted.Task,
            TimeSpan.FromSeconds(2),
            "stale display-info send start");

        await coordinator.StopAsync(sendStopMessage: false, reason: null, CancellationToken.None);
        await coordinator.StartAsync("session-live", CancellationToken.None);

        fakeSource.RaiseFrame(1280, 720, new byte[] { 2 }, "jpeg");
        await WaitUntilAsync(() => sentRevisions.Count == 1, TimeSpan.FromSeconds(2));

        releaseStaleSend.TrySetResult(true);
        await AwaitCompletesAsync(
            staleFailureObserved.Task,
            TimeSpan.FromSeconds(2),
            "stale display-info send failure");
        await Task.Yield();
        ForceFullCollection();
        Assert.Empty(unobserved.Exceptions);

        fakeSource.RaiseFrame(1280, 720, new byte[] { 3 }, "jpeg");
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
        fakeSource.RaiseFrame(1280, 720, new byte[] { 1 }, "jpeg");
        await WaitUntilAsync(() => sentRevisions.Count == 1, TimeSpan.FromSeconds(2));

        await coordinator.StopAsync(sendStopMessage: false, reason: null, CancellationToken.None);
        await coordinator.StartAsync("session-live", CancellationToken.None);

        fakeSource.RaiseFrame(960, 540, new byte[] { 2 }, "jpeg");
        await WaitUntilAsync(() => sentRevisions.Count == 2, TimeSpan.FromSeconds(2));

        clock.Advance(TimeSpan.FromMilliseconds(300));
        fakeSource.RaiseFrame(1280, 720, new byte[] { 3 }, "jpeg");
        clock.Advance(TimeSpan.FromMilliseconds(300));
        fakeSource.RaiseFrame(800, 450, new byte[] { 4 }, "jpeg");
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
        fakeSource.RaiseFrame(1280, 720, new byte[] { 1 }, "jpeg");
        await WaitUntilAsync(() => sentRevisions.Count == 1, TimeSpan.FromSeconds(2));

        fakeSource.CaptureMetadata = new ScreenCaptureMetadata(
            DisplayId: "primary",
            CaptureRegionPx: new ScreenCapturePixelRect(100, 50, 1720, 980),
            DpiScale: 1.25);
        fakeSource.RaiseFrame(1280, 720, new byte[] { 2 }, "jpeg");
        clock.Advance(TimeSpan.FromMilliseconds(100));
        fakeSource.RaiseFrame(1280, 720, new byte[] { 3 }, "jpeg");
        await Task.Delay(100);
        Assert.Equal(1, sentRevisions.Count);

        await coordinator.StopAsync(sendStopMessage: false, reason: null, CancellationToken.None);
        await coordinator.StartAsync("session-live", CancellationToken.None);

        fakeSource.RaiseFrame(1280, 720, new byte[] { 4 }, "jpeg");
        await WaitUntilAsync(() => sentRevisions.Count == 2, TimeSpan.FromSeconds(2));

        fakeSource.CaptureMetadata = new ScreenCaptureMetadata(
            DisplayId: "primary",
            CaptureRegionPx: new ScreenCapturePixelRect(150, 80, 1600, 900),
            DpiScale: 1.25);
        fakeSource.RaiseFrame(1280, 720, new byte[] { 5 }, "jpeg");
        clock.Advance(TimeSpan.FromMilliseconds(100));
        fakeSource.RaiseFrame(1280, 720, new byte[] { 6 }, "jpeg");
        await Task.Delay(100);
        Assert.Equal(2, sentRevisions.Count);

        clock.Advance(TimeSpan.FromMilliseconds(200));
        fakeSource.RaiseFrame(1280, 720, new byte[] { 7 }, "jpeg");
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

        for (var i = 0; i < 20; i++)
        {
            fakeSource.RaiseFrame(1, 1, new byte[] { (byte)(i + 1) }, "jpeg");
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
            fakeSource.RaiseFrame(1, 1, new byte[] { (byte)((i % 250) + 21) }, "jpeg");
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

        fakeSource.RaiseFrame(1, 1, new byte[] { 1, 2, 3 }, "jpeg");
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

        fakeSource.RaiseFrame(1, 1, new byte[] { 4, 5, 6 }, "jpeg");
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

            DriveTransportFrames(fakeSource, clock, count: 24, advancePerFrame: TimeSpan.FromMilliseconds(500));
            await AwaitCompletesAsync(
                probe.WaitForPayloadCountAsync(2, TimeSpan.FromSeconds(3)),
                TimeSpan.FromSeconds(3),
                "transport lifecycle step 2 payloads for s1");

            await AwaitCompletesAsync(
                coordinator.StopAsync(sendStopMessage: false, reason: "test", CancellationToken.None),
                TimeSpan.FromSeconds(3),
                "transport lifecycle step 3 first stop");
            await AwaitCompletesAsync(
                coordinator.StopAsync(sendStopMessage: false, reason: "test", CancellationToken.None),
                TimeSpan.FromSeconds(3),
                "transport lifecycle step 4 second stop");

            var payloadCountAfterStop = probe.PayloadsSent;

            await AwaitCompletesAsync(
                coordinator.StartAsync("s2", CancellationToken.None),
                TimeSpan.FromSeconds(3),
                "transport lifecycle step 5 restart s2");

            DriveTransportFrames(fakeSource, clock, count: 24, advancePerFrame: TimeSpan.FromMilliseconds(500));
            await AwaitCompletesAsync(
                probe.WaitForPayloadCountAsync(payloadCountAfterStop + 2, TimeSpan.FromSeconds(3)),
                TimeSpan.FromSeconds(3),
                "transport lifecycle step 6 payloads for s2");

            await AwaitCompletesAsync(
                coordinator.HandleDisconnectedAsync(),
                TimeSpan.FromSeconds(3),
                "transport lifecycle step 7 disconnect");

            Assert.False(coordinator.IsActive);
            Assert.False(fakeSource.IsStarted);
            Assert.Equal(0, fakeSource.FrameSubscriberCount);

            var payloadCountAfterDisconnect = probe.PayloadsSent;
            fakeSource.RaiseFrame(1, 1, new byte[] { 201 }, "jpeg");
            fakeSource.RaiseFrame(1, 1, new byte[] { 202 }, "jpeg");
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

            Assert.Equal(FeatureFlags.ScreenShareTransportMaxFps, fakeSource.LastCaptureFrameRateHint);

            fakeSource.RaiseFrame(
                new ScreenCaptureFrameEventArgs(
                    1280,
                    720,
                    new byte[] { 1, 2, 3 },
                    "jpeg",
                    capturedTsUtcMs: clock.UtcNow.AddMilliseconds(-1000).ToUnixTimeMilliseconds()));

            await Task.Delay(50);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

            Assert.Equal(FeatureFlags.ScreenShareTransportMaxFps - 1, fakeSource.LastCaptureFrameRateHint);
            Assert.Contains(FeatureFlags.ScreenShareTransportMaxFps, fakeSource.CaptureFrameRateHints);

            await AwaitCompletesAsync(
                coordinator.StopAsync(sendStopMessage: false, reason: "restart", CancellationToken.None),
                TimeSpan.FromSeconds(2),
                "auto-tune cycle 1 stop");

            await AwaitCompletesAsync(
                coordinator.StartAsync("session-auto-2", CancellationToken.None),
                TimeSpan.FromSeconds(2),
                "auto-tune cycle 2 start");

            Assert.Equal(FeatureFlags.ScreenShareTransportMaxFps, fakeSource.LastCaptureFrameRateHint);

            fakeSource.RaiseFrame(
                new ScreenCaptureFrameEventArgs(
                    1280,
                    720,
                    new byte[] { 4, 5, 6 },
                    "jpeg",
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));

            await Task.Delay(50);
            autoTuneTick.Invoke(coordinator, Array.Empty<object>());

            Assert.Equal(FeatureFlags.ScreenShareTransportMaxFps, fakeSource.LastCaptureFrameRateHint);
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

            for (var i = 0; i < 5; i++)
            {
                fakeSource.RaiseFrame(1, 1, new byte[] { (byte)(i + 1) }, "jpeg");
                // Keep frame cadence well above the transport min interval (8 FPS -> 125 ms)
                // so this scenario remains deterministic even if the default transport cap changes.
                clock.Advance(TimeSpan.FromMilliseconds(40));
            }

            await AwaitCompletesAsync(
                probe.WaitForPayloadCountAsync(2, TimeSpan.FromSeconds(2)),
                TimeSpan.FromSeconds(2),
                $"{iterationLabel}: throttled payload sends");
            await AwaitCompletesAsync(
                coordinator.StopAsync(sendStopMessage: false, reason: null, CancellationToken.None),
                TimeSpan.FromSeconds(2),
                $"{iterationLabel}: throttled stop");

            var sentPayloads = probe.GetRecentPayloadsSnapshot();
            Assert.Equal(2, probe.PayloadsSent);
            Assert.Equal(2, sentPayloads.Length);
            Assert.True(
                ScreenSharePayloadCodec.TryDeserialize(sentPayloads[0], out var firstChunk),
                $"Expected first payload to deserialize during {iterationLabel}.");
            Assert.Equal("session-live", firstChunk.SessionId);
            Assert.Equal(0, firstChunk.FrameId);
            Assert.True(
                ScreenSharePayloadCodec.TryDeserialize(sentPayloads[1], out var secondChunk),
                $"Expected second payload to deserialize during {iterationLabel}.");
            Assert.Equal("session-live", secondChunk.SessionId);
            Assert.Equal(1, secondChunk.FrameId);
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
            fakeSource.RaiseFrame(1, 1, new byte[] { (byte)((i % 250) + 1) }, "jpeg");
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

        fakeSource.RaiseFrame(640, 360, new byte[] { 0, 1, 2 }, "jpeg");
        await AwaitCompletesAsync(
            sendEntered.Task,
            TimeSpan.FromSeconds(2),
            $"{scenarioLabel}: blocked send entry");

        for (var frameIndex = 1; frameIndex <= 24; frameIndex++)
        {
            fakeSource.RaiseFrame(640, 360, new byte[] { (byte)frameIndex, 7, 9 }, "jpeg");
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
                fakeSource.RaiseFrame(640, 360, new byte[] { (byte)cycle, (byte)frameIndex, 42 }, "jpeg");
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

        fakeSource.RaiseFrame(1, 1, Array.Empty<byte>(), "jpeg");
        await Task.Yield();
        ForceFullCollection();
        Assert.Empty(unobserved.Exceptions);

        fakeSource.RaiseFrame(1, 1, new byte[] { 1, 2, 3 }, "jpeg");
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

    private static Bitmap CreateTinyBitmap()
    {
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/a5kAAAAASUVORK5CYII=");
        using var stream = new MemoryStream(bytes, writable: false);
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
        IAsyncDisposable
    {
        private EventHandler<ScreenCaptureFrameEventArgs>? frameArrived;
        private readonly List<int> captureFrameRateHints = new();

        public bool IsSupported => true;

        public bool IsStarted { get; private set; }

        public int LastCaptureFrameRateHint { get; private set; }

        public IReadOnlyList<int> CaptureFrameRateHints => captureFrameRateHints;

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
