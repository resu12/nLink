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
[Trait("Area", "ScreenShare")]
public sealed class TransportScreenShareCoordinatorLifecycleTests : ScreenShareCoordinatorTestBase, IClassFixture<ScreenShareCoordinatorFixture>
{
    public TransportScreenShareCoordinatorLifecycleTests(ScreenShareCoordinatorFixture fixture) : base(fixture)
    {
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

}
