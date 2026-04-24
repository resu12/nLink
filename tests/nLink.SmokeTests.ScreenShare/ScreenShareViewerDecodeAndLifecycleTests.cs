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
public sealed class ScreenShareViewerDecodeAndLifecycleTests : ScreenShareViewerViewModelTestBase, IClassFixture<ScreenShareCoordinatorFixture>
{
    public ScreenShareViewerDecodeAndLifecycleTests(ScreenShareCoordinatorFixture fixture) : base(fixture)
    {
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

}
