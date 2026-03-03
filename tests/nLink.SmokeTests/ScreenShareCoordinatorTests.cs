using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using NLink.App.Services.ScreenCapture;
using NLink.App.Views;
using NLink.Core.ScreenShare;
using NLink.Infra.Nkn;
using NLink.SmokeTests.Fakes;

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
    public async Task HelpeeScreenShareCoordinator_SingleDecodeInFlight_DropsLaterFrames_AndDiscardsStaleGeneration()
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
                decodeFrame: _ =>
                {
                    decodeStarted.TrySetResult(true);
                    releaseDecode.Task.GetAwaiter().GetResult();
                    decodeCount++;
                    return CreateTinyBitmap();
                });

            try
            {
                coordinator.Toggle();
                await WaitUntilAsync(() => active, TimeSpan.FromSeconds(2));

                fakeSource.RaiseFrame(1, 1, new byte[] { 1 }, "jpeg");
                await decodeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

                fakeSource.RaiseFrame(1, 1, new byte[] { 2 }, "jpeg");
                await coordinator.StopAsync();

                releaseDecode.TrySetResult(true);
                await WaitUntilAsync(() => !active, TimeSpan.FromSeconds(2));
                await WaitUntilAsync(() => decodeCount == 1, TimeSpan.FromSeconds(2));
                await WaitUntilAsync(() => currentFrame is null, TimeSpan.FromSeconds(2));

                Assert.Equal(1, decodeCount);
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
    public async Task HelperScreenShareCoordinator_SingleDecodeInFlight_DropsLaterFrames_AndDiscardsStaleGeneration()
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
                decodeFrame: _ =>
                {
                    decodeStarted.TrySetResult(true);
                    releaseDecode.Task.GetAwaiter().GetResult();
                    decodeCount++;
                    return CreateTinyBitmap();
                });

            try
            {
                coordinator.OnFrameCompleted(new ScreenShareFrameCompletedEventArgs(1, 1, 1, "jpeg", new byte[] { 1 }));
                await decodeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

                coordinator.OnFrameCompleted(new ScreenShareFrameCompletedEventArgs(2, 1, 1, "jpeg", new byte[] { 2 }));
                coordinator.Clear();

                releaseDecode.TrySetResult(true);

                await WaitUntilAsync(() => decodeCount == 1, TimeSpan.FromSeconds(2));
                await WaitUntilAsync(() => currentFrame is null, TimeSpan.FromSeconds(2));

                Assert.Equal(1, decodeCount);
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

                coordinator.OnFrameCompleted(new ScreenShareFrameCompletedEventArgs(1, 1, 1, "jpeg", new byte[] { 1 }));

                await WaitUntilAsync(() => currentFrame is null, TimeSpan.FromSeconds(1));
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
    public async Task TransportScreenShareCoordinator_Disconnected_StopsCapture_AndPreventsFurtherSends()
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
        Assert.True(fakeSource.IsStarted);

        fakeSource.RaiseFrame(1, 1, new byte[] { 1, 2, 3 }, "jpeg");
        await firstPayloadSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.HandleDisconnectedAsync();

        Assert.False(fakeSource.IsStarted);
        Assert.Equal(1, fakeSource.StartCallCount);
        Assert.Equal(1, fakeSource.StopCallCount);

        fakeSource.RaiseFrame(1, 1, new byte[] { 4, 5, 6 }, "jpeg");
        await WaitUntilAsync(
            () =>
            {
                lock (sentPayloads)
                {
                    return sentPayloads.Count == 1;
                }
            },
            TimeSpan.FromSeconds(1));

        lock (sentPayloads)
        {
            Assert.Single(sentPayloads);
            Assert.True(ScreenSharePayloadCodec.TryDeserialize(sentPayloads[0], out var chunk));
            Assert.Equal("session-live", chunk.SessionId);
        }
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
        await firstPayloadSent.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await coordinator.StopAsync(sendStopMessage: false, reason: null, CancellationToken.None);
        await coordinator.StopAsync(sendStopMessage: false, reason: null, CancellationToken.None);

        Assert.False(coordinator.IsActive);
        Assert.False(fakeSource.IsStarted);
        Assert.Equal(1, fakeSource.StopCallCount);

        fakeSource.RaiseFrame(1, 1, new byte[] { 4, 5, 6 }, "jpeg");
        await WaitUntilAsync(
            () =>
            {
                lock (sentPayloads)
                {
                    return sentPayloads.Count == 1;
                }
            },
            TimeSpan.FromSeconds(1));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RapidFrames_AreThrottledForTransport()
    {
        var fakeSource = new FakeScreenCaptureSource();
        var sentPayloads = new List<byte[]>();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 3, 18, 0, 0, TimeSpan.Zero));

        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (payload, _) =>
            {
                lock (sentPayloads)
                {
                    sentPayloads.Add(payload.ToArray());
                }

                return Task.CompletedTask;
            },
            clock: clock);

        await coordinator.StartAsync("session-live", CancellationToken.None);

        for (var i = 0; i < 5; i++)
        {
            fakeSource.RaiseFrame(1, 1, new byte[] { (byte)(i + 1) }, "jpeg");
            clock.Advance(TimeSpan.FromMilliseconds(125));
        }

        await WaitUntilAsync(
            () =>
            {
                lock (sentPayloads)
                {
                    return sentPayloads.Count >= 1;
                }
            },
            TimeSpan.FromSeconds(2));

        await coordinator.StopAsync(sendStopMessage: false, reason: null, CancellationToken.None);

        lock (sentPayloads)
        {
            Assert.Equal(2, sentPayloads.Count);
            Assert.True(ScreenSharePayloadCodec.TryDeserialize(sentPayloads[0], out var firstChunk));
            Assert.Equal("session-live", firstChunk.SessionId);
            Assert.Equal(0, firstChunk.FrameId);
            Assert.True(ScreenSharePayloadCodec.TryDeserialize(sentPayloads[1], out var secondChunk));
            Assert.Equal("session-live", secondChunk.SessionId);
            Assert.Equal(1, secondChunk.FrameId);
        }
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

    private static Bitmap CreateTinyBitmap()
    {
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/a5kAAAAASUVORK5CYII=");
        using var stream = new MemoryStream(bytes, writable: false);
        return new Bitmap(stream);
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

    private sealed class ScreenShareViewerErrorContext
    {
        public bool ShowDefaultScreenSharePlaceholder => false;

        public bool ShowScreenShareViewerError => true;

        public string ScreenShareViewerMessage => "Screen sharing failed to start";

        public bool ShowRemoteScreenShareFrame => false;

        public bool ShowScreenSharePreviewFrame => false;
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
