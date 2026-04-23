using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using NLink.App.Configuration;
using NLink.App.Services;
using NLink.App.Services.ScreenCapture;
using NLink.App.ViewModels;
using NLink.Infra.DevLocal;
using NLink.SmokeTests.Fakes;

namespace NLink.SmokeTests;

[Collection(AvaloniaHeadlessUiCollection.Name)]
public sealed class ScreenSharePreviewIntegrationTests : IClassFixture<ScreenSharePreviewFixture>
{
    private readonly ScreenSharePreviewFixture fixture;

    public ScreenSharePreviewIntegrationTests(ScreenSharePreviewFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task HelpeePreview_ToggleOnOff_Repeatedly_DoesNotCrash_AndCleansUp()
    {
        if (!FeatureFlags.EnableScreenShareScaffold ||
            !FeatureFlags.EnableScreenShareCapture ||
            !FeatureFlags.EnableScreenSharePreview)
        {
            return;
        }

        await fixture.Session.Dispatch(async () =>
        {
            var transportConfig = CreateDevLocalTestConfig();
            var fakeSource = new FakeScreenCaptureSource();
            using var runtime = new SessionRuntime(() => new DevLocalTransport());
            using var helpee = new HelpeePageViewModel(
                cancelAction: static () => { },
                transportConfig,
                runtime,
                screenCaptureSourceFactory: new FixedCaptureSourceFactory(fakeSource));

            try
            {
                Assert.True(helpee.CanShowScreenShareAction);

                for (var i = 0; i < 2; i++)
                {
                    var startedSignal = CreateConditionSignal(
                        helpee,
                        () => helpee.IsScreenSharingPreviewActive,
                        nameof(HelpeePageViewModel.IsScreenSharingPreviewActive));
                    var frameAppliedSignal = CreateConditionSignal(
                        helpee,
                        () => helpee.ShowScreenSharePreviewFrame && helpee.ScreenSharePreviewFrame is not null,
                        nameof(HelpeePageViewModel.ShowScreenSharePreviewFrame),
                        nameof(HelpeePageViewModel.ScreenSharePreviewFrame));
                    helpee.ToggleScreenSharePreviewCommand.Execute(null);

                    await WaitForSignalAsync(
                        startedSignal,
                        TimeSpan.FromSeconds(10),
                        () => BuildState(helpee));

                    fakeSource.RaiseFrame(1, 1, new byte[] { (byte)(i + 1) }, "jpeg");
                    await WaitForSignalAsync(
                        frameAppliedSignal,
                        TimeSpan.FromSeconds(10),
                        () => BuildState(helpee));

                    var stoppedSignal = CreateConditionSignal(
                        helpee,
                        () => !helpee.IsScreenSharingPreviewActive &&
                              helpee.ScreenSharePreviewFrame is null &&
                              helpee.ScreenSharePreviewStatus.State == ScreenShareState.Off,
                        nameof(HelpeePageViewModel.IsScreenSharingPreviewActive),
                        nameof(HelpeePageViewModel.ScreenSharePreviewFrame),
                        nameof(HelpeePageViewModel.ScreenSharePreviewStatus));
                    helpee.ToggleScreenSharePreviewCommand.Execute(null);

                    await WaitForSignalAsync(
                        stoppedSignal,
                        TimeSpan.FromSeconds(10),
                        () => BuildState(helpee));
                }

                Assert.Equal(2, fakeSource.StartCallCount);
                Assert.Equal(2, fakeSource.StopCallCount);
                Assert.Equal(3, fakeSource.DisposeCallCount);
                Assert.Equal(0, fakeSource.FrameSubscriberCount);
            }
            finally
            {
            }

            return true;
        }, default);
    }

    [Fact]
    public async Task HelpeePreview_ScreenSharePreviewFrame_Progresses_UnderRapidFrames_AndSlowDecode()
    {
        if (!FeatureFlags.EnableScreenShareScaffold ||
            !FeatureFlags.EnableScreenShareCapture ||
            !FeatureFlags.EnableScreenSharePreview)
        {
            return;
        }

        await fixture.Session.Dispatch(async () =>
        {
            var transportConfig = CreateDevLocalTestConfig();
            var fakeSource = new FakeScreenCaptureSource();
            using var runtime = new SessionRuntime(() => new DevLocalTransport());
            using var decodeGate = new SemaphoreSlim(0, 50);
            var appliedFrames = 0;

            using var helpee = new HelpeePageViewModel(
                cancelAction: static () => { },
                transportConfig,
                runtime,
                openDiagnosticsAction: null,
                clipboardService: null,
                shareMessageConfig: null,
                statusPresenter: null,
                incomingRequestTimeout: null,
                uiStateStore: null,
                backAction: null,
                screenCaptureSourceFactory: new FixedCaptureSourceFactory(fakeSource),
                decodeFrame: bytes =>
                {
                    Assert.True(
                        decodeGate.Wait(TimeSpan.FromSeconds(2)),
                        "Timed out waiting to release preview decode.");
                    return CreateBitmap(bytes[0], 1);
                });

            helpee.PropertyChanged += (_, e) =>
            {
                if (!string.Equals(e.PropertyName, nameof(HelpeePageViewModel.ScreenSharePreviewFrame), StringComparison.Ordinal))
                {
                    return;
                }

                if (helpee.ScreenSharePreviewFrame is null)
                {
                    return;
                }
                Interlocked.Increment(ref appliedFrames);
            };

            var startedSignal = CreateConditionSignal(
                helpee,
                () => helpee.IsScreenSharingPreviewActive,
                nameof(HelpeePageViewModel.IsScreenSharingPreviewActive));
            helpee.ToggleScreenSharePreviewCommand.Execute(null);
            await WaitForSignalAsync(
                startedSignal,
                TimeSpan.FromSeconds(5),
                () => BuildState(helpee));

            for (byte i = 1; i <= 20; i++)
            {
                fakeSource.RaiseFrame(1, 1, new byte[] { i }, "jpeg");
            }

            decodeGate.Release(2);
            await WaitUntilAsync(
                () => helpee.ScreenSharePreviewFrame is Bitmap first && first.PixelSize.Width == 20,
                TimeSpan.FromSeconds(5));

            for (byte i = 21; i <= 35; i++)
            {
                fakeSource.RaiseFrame(1, 1, new byte[] { i }, "jpeg");
            }

            decodeGate.Release(2);
            await WaitUntilAsync(
                () => helpee.ScreenSharePreviewFrame is Bitmap second && second.PixelSize.Width == 35,
                TimeSpan.FromSeconds(5));

            for (byte i = 36; i <= 50; i++)
            {
                fakeSource.RaiseFrame(1, 1, new byte[] { i }, "jpeg");
            }

            decodeGate.Release(2);
            await WaitUntilAsync(
                () => helpee.ScreenSharePreviewFrame is Bitmap third && third.PixelSize.Width == 50,
                TimeSpan.FromSeconds(5));

            Assert.True(appliedFrames >= 3, $"Expected at least 3 preview frame updates, but saw {appliedFrames}.");
            Assert.True(helpee.IsScreenSharingPreviewActive);
            Assert.True(helpee.ShowScreenSharePreviewFrame);
            Assert.NotNull(helpee.ScreenSharePreviewFrame);
            Assert.Equal(ScreenShareState.Active, helpee.ScreenSharePreviewStatus.State);

            return true;
        }, default);
    }

    [Fact]
    public async Task HelpeePreview_ScreenSharePreviewFrame_AppliesLatestFrame_WhenDecodeSlowerThanArrival()
    {
        if (!FeatureFlags.EnableScreenShareScaffold ||
            !FeatureFlags.EnableScreenShareCapture ||
            !FeatureFlags.EnableScreenSharePreview)
        {
            return;
        }

        await fixture.Session.Dispatch(async () =>
        {
            var transportConfig = CreateDevLocalTestConfig();
            var fakeSource = new FakeScreenCaptureSource();
            using var runtime = new SessionRuntime(() => new DevLocalTransport());
            using var decodeGate = new SemaphoreSlim(0, 2);
            var firstDecodeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var decodeCalls = 0;
            var lastDecodedMarker = 0;

            using var helpee = new HelpeePageViewModel(
                cancelAction: static () => { },
                transportConfig,
                runtime,
                openDiagnosticsAction: null,
                clipboardService: null,
                shareMessageConfig: null,
                statusPresenter: null,
                incomingRequestTimeout: null,
                uiStateStore: null,
                backAction: null,
                screenCaptureSourceFactory: new FixedCaptureSourceFactory(fakeSource),
                decodeFrame: bytes =>
                {
                    var call = Interlocked.Increment(ref decodeCalls);
                    Volatile.Write(ref lastDecodedMarker, bytes[0]);
                    if (call == 1)
                    {
                        firstDecodeStarted.TrySetResult(true);
                    }

                    Assert.True(
                        decodeGate.Wait(TimeSpan.FromSeconds(2)),
                        $"Timed out waiting to release preview decode {call}.");
                    return CreateBitmap(bytes[0], 1);
                });

            var startedSignal = CreateConditionSignal(
                helpee,
                () => helpee.IsScreenSharingPreviewActive,
                nameof(HelpeePageViewModel.IsScreenSharingPreviewActive));
            helpee.ToggleScreenSharePreviewCommand.Execute(null);
            await WaitForSignalAsync(
                startedSignal,
                TimeSpan.FromSeconds(5),
                () => BuildState(helpee));

            fakeSource.RaiseFrame(1, 1, new byte[] { 1 }, "jpeg");
            await WaitForSignalAsync(
                firstDecodeStarted.Task,
                TimeSpan.FromSeconds(2),
                () => BuildState(helpee));

            for (byte i = 2; i <= 20; i++)
            {
                fakeSource.RaiseFrame(1, 1, new byte[] { i }, "jpeg");
            }

            decodeGate.Release(2);

            await WaitUntilAsync(
                () => helpee.ScreenSharePreviewFrame is Bitmap latest && latest.PixelSize.Width == 20,
                TimeSpan.FromSeconds(5));

            Assert.Equal(20, Volatile.Read(ref lastDecodedMarker));
            Assert.InRange(decodeCalls, 2, 3);
            Assert.True(helpee.IsScreenSharingPreviewActive);
            Assert.True(helpee.ShowScreenSharePreviewFrame);
            Assert.NotNull(helpee.ScreenSharePreviewFrame);
            Assert.Equal(ScreenShareState.Active, helpee.ScreenSharePreviewStatus.State);

            return true;
        }, default);
    }

    [Fact]
    public async Task HelpeePreview_Stop_PreventsFurtherPreviewApplies()
    {
        if (!FeatureFlags.EnableScreenShareScaffold ||
            !FeatureFlags.EnableScreenShareCapture ||
            !FeatureFlags.EnableScreenSharePreview)
        {
            return;
        }

        await fixture.Session.Dispatch(async () =>
        {
            var transportConfig = CreateDevLocalTestConfig();
            var fakeSource = new FakeScreenCaptureSource();
            using var runtime = new SessionRuntime(() => new DevLocalTransport());
            var applyCount = 0;
            var firstApplyObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var postStopApplyObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var observePostStopApplies = 0;

            using var helpee = new HelpeePageViewModel(
                cancelAction: static () => { },
                transportConfig,
                runtime,
                openDiagnosticsAction: null,
                clipboardService: null,
                shareMessageConfig: null,
                statusPresenter: null,
                incomingRequestTimeout: null,
                uiStateStore: null,
                backAction: null,
                screenCaptureSourceFactory: new FixedCaptureSourceFactory(fakeSource),
                decodeFrame: bytes => CreateBitmap(bytes[0], 1));

            helpee.PropertyChanged += (_, e) =>
            {
                if (!string.Equals(e.PropertyName, nameof(HelpeePageViewModel.ScreenSharePreviewFrame), StringComparison.Ordinal))
                {
                    return;
                }

                if (helpee.ScreenSharePreviewFrame is null)
                {
                    return;
                }

                if (Interlocked.Increment(ref applyCount) == 1)
                {
                    firstApplyObserved.TrySetResult(true);
                }

                if (Volatile.Read(ref observePostStopApplies) == 1)
                {
                    postStopApplyObserved.TrySetResult(true);
                }
            };

            var startedSignal = CreateConditionSignal(
                helpee,
                () => helpee.IsScreenSharingPreviewActive,
                nameof(HelpeePageViewModel.IsScreenSharingPreviewActive));
            helpee.ToggleScreenSharePreviewCommand.Execute(null);
            await WaitForSignalAsync(
                startedSignal,
                TimeSpan.FromSeconds(5),
                () => BuildState(helpee));

            fakeSource.RaiseFrame(1, 1, new byte[] { 1 }, "jpeg");
            await WaitForSignalAsync(
                firstApplyObserved.Task,
                TimeSpan.FromSeconds(5),
                () => BuildState(helpee));

            var stoppedSignal = CreateConditionSignal(
                helpee,
                () => !helpee.IsScreenSharingPreviewActive &&
                      helpee.ScreenSharePreviewFrame is null &&
                      helpee.ScreenSharePreviewStatus.State == ScreenShareState.Off,
                nameof(HelpeePageViewModel.IsScreenSharingPreviewActive),
                nameof(HelpeePageViewModel.ScreenSharePreviewFrame),
                nameof(HelpeePageViewModel.ScreenSharePreviewStatus));
            helpee.ToggleScreenSharePreviewCommand.Execute(null);
            await WaitForSignalAsync(
                stoppedSignal,
                TimeSpan.FromSeconds(5),
                () => BuildState(helpee));

            var applyCountAfterStop = Volatile.Read(ref applyCount);
            Volatile.Write(ref observePostStopApplies, 1);

            for (var i = 0; i < 20; i++)
            {
                fakeSource.RaiseFrame(1, 1, new byte[] { (byte)(i + 2) }, "jpeg");
            }

            var completed = await Task.WhenAny(
                postStopApplyObserved.Task,
                Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None));

            Assert.False(
                ReferenceEquals(completed, postStopApplyObserved.Task),
                $"Observed a preview frame apply after stop. {BuildState(helpee)} ApplyCountAfterStop={applyCountAfterStop} CurrentApplyCount={Volatile.Read(ref applyCount)}");
            Assert.Equal(applyCountAfterStop, Volatile.Read(ref applyCount));
            Assert.False(helpee.IsScreenSharingPreviewActive);
            Assert.False(helpee.ShowScreenSharePreviewFrame);
            Assert.Null(helpee.ScreenSharePreviewFrame);
            Assert.Equal(ScreenShareState.Off, helpee.ScreenSharePreviewStatus.State);

            return true;
        }, default);
    }

    private static async Task WaitForSignalAsync(
        Task signal,
        TimeSpan timeout,
        Func<string> describeState)
    {
        try
        {
            await signal.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"Timed out waiting for preview condition. {describeState()}");
        }
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

    private static Task CreateConditionSignal(
        INotifyPropertyChanged source,
        Func<bool> condition,
        params string[] propertyNames)
    {
        if (condition())
        {
            return Task.CompletedTask;
        }

        var watchedProperties = propertyNames.Length == 0
            ? null
            : new HashSet<string>(propertyNames, StringComparer.Ordinal);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        PropertyChangedEventHandler? handler = null;
        handler = (_, e) =>
        {
            if (watchedProperties is not null &&
                !string.IsNullOrWhiteSpace(e.PropertyName) &&
                !watchedProperties.Contains(e.PropertyName))
            {
                return;
            }

            if (!condition())
            {
                return;
            }

            source.PropertyChanged -= handler;
            completion.TrySetResult(true);
        };

        source.PropertyChanged += handler;

        if (condition())
        {
            source.PropertyChanged -= handler;
            return Task.CompletedTask;
        }

        return completion.Task;
    }

    private static string BuildState(HelpeePageViewModel helpee)
    {
        return $"CanShow={helpee.CanShowScreenShareAction}, Active={helpee.IsScreenSharingPreviewActive}, HasFrame={helpee.ScreenSharePreviewFrame is not null}, ShowFrame={helpee.ShowScreenSharePreviewFrame}, Status={helpee.ScreenSharePreviewStatus.State}";
    }

    private static Bitmap CreateBitmap(int width, int height)
    {
        var writeable = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using var locked = writeable.Lock();
        var totalBytes = width * height * 4;
        var pixels = new byte[totalBytes];
        Marshal.Copy(pixels, 0, locked.Address, totalBytes);
        return writeable;
    }

    private static TransportRuntimeConfig CreateDevLocalTestConfig()
    {
        var previous = Environment.GetEnvironmentVariable("FRH_TRANSPORT");

        try
        {
            Environment.SetEnvironmentVariable("FRH_TRANSPORT", null);
            return TransportRuntimeConfig.Select();
        }
        finally
        {
            Environment.SetEnvironmentVariable("FRH_TRANSPORT", previous);
        }
    }
}

public sealed class ScreenSharePreviewFixture : IDisposable
{
    public ScreenSharePreviewFixture()
    {
        Session = HeadlessUnitTestSession.StartNew(typeof(AvaloniaHeadlessUiAppBootstrap));
    }

    public HeadlessUnitTestSession Session { get; }

    public void Dispose()
    {
        Session.Dispose();
    }
}

internal sealed class FixedCaptureSourceFactory : IScreenCaptureSourceFactory
{
    private readonly IScreenCaptureSource source;

    public FixedCaptureSourceFactory(IScreenCaptureSource source)
    {
        this.source = source;
    }

    public IScreenCaptureSource Create() => source;
}
