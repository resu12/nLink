using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NLink.App.Services.ScreenCapture;
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
                captureSourceFactory: () => fakeSource,
                setPreviewActive: value => active = value,
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
                captureSourceFactory: () => fakeSource,
                setPreviewActive: value => active = value,
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
