using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using NLink.Core.Logging;

namespace NLink.App.Services.ScreenCapture;

[SupportedOSPlatform("windows")]
internal sealed class WindowsGraphicsCaptureRawSource : IWindowsRawCaptureSource, IWindowsRawCaptureBackendDescriptor
{
    private const uint D3D11SdkVersion = 7;
    private const uint D3D11CreateDeviceBgraSupport = 0x20;
    private const uint D3D11UsageStaging = 3;
    private const uint D3D11CpuAccessRead = 0x20000;
    private const uint D3D11MapRead = 1;
    private const int FramePoolBufferCount = 2;
    private static readonly TimeSpan RestartWindow = TimeSpan.FromSeconds(2);
    private readonly object sync = new();

    private GraphicsCaptureItem? captureItem;
    private Direct3D11CaptureFramePool? framePool;
    private GraphicsCaptureSession? captureSession;
    private IDirect3DDevice? direct3DDevice;
    private CancellationTokenSource? captureCts;
    private IntPtr nativeD3DDevice;
    private IntPtr nativeImmediateContext;
    private IntPtr stagingTexture;
    private D3D11Texture2DDesc stagingTextureDesc;
    private bool started;
    private bool disposed;
    private bool hasDeliveredFrame;
    private int frameProcessing;
    private int restartInProgress;
    private SizeInt32 currentContentSize;
    private long currentBootTimeUnixMs;
    private DateTimeOffset lastRestartAttemptUtc;

    public WindowsGraphicsCaptureRawSource(ScreenCaptureTargetSelection captureTarget)
    {
        CaptureTarget = captureTarget;
    }

    public ScreenCaptureTargetSelection CaptureTarget { get; }

    public WindowsRawCaptureBackendKind BackendKind => WindowsRawCaptureBackendKind.WindowsGraphicsCapture;

    public bool IsSupported => IsSupportedSelection(CaptureTarget) && IsRuntimeSupported();

    public event EventHandler<WindowsRawCaptureFrameEventArgs>? FrameArrived;
    public event EventHandler<WindowsRawCaptureFailureEventArgs>? CaptureFailed;

    public static bool IsRuntimeSupported()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return GraphicsCaptureSession.IsSupported();
        }
        catch (Exception ex)
        {
            LogLifecycle("screenshare_wgc_support_probe_failed", $"reason={ex.GetType().Name}");
            return false;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        lock (sync)
        {
            if (started)
            {
                return Task.CompletedTask;
            }
        }

        if (!IsSupported)
        {
            throw new NotSupportedException("Windows Graphics Capture is not supported for the selected target.");
        }

        if (!TryGetCaptureMetadata(out var metadata))
        {
            throw new InvalidOperationException($"Capture target could not be resolved ({CaptureTarget.Describe()}).");
        }

        var monitor = ResolveMonitorHandle(metadata.CaptureRegionPx);
        if (monitor == IntPtr.Zero)
        {
            LogLifecycle("screenshare_wgc_monitor_resolution_failed", $"target={CaptureTarget.Describe()}");
            throw new InvalidOperationException("Display monitor handle could not be resolved for Windows Graphics Capture.");
        }

        var startStage = "create_device";
        IntPtr nextNativeDevice = IntPtr.Zero;
        IntPtr nextNativeImmediateContext = IntPtr.Zero;
        GraphicsCaptureItem? nextItem = null;
        Direct3D11CaptureFramePool? nextFramePool = null;
        GraphicsCaptureSession? nextSession = null;
        CancellationTokenSource? nextCts = null;

        try
        {
            var nextDevice = CreateDirect3DDevice(out nextNativeDevice, out nextNativeImmediateContext);
            startStage = "create_capture_item";
            nextItem = CreateCaptureItemForMonitor(monitor);
            var nextSize = nextItem.Size;

            startStage = "create_session";
            nextFramePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                nextDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                FramePoolBufferCount,
                nextSize);
            nextSession = nextFramePool.CreateCaptureSession(nextItem);
            nextCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var bootTimeUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Environment.TickCount64;

            nextFramePool.FrameArrived += OnFrameArrived;
            nextItem.Closed += OnCaptureItemClosed;

            startStage = "start_capture";
            nextSession.StartCapture();

            lock (sync)
            {
                captureItem = nextItem;
                framePool = nextFramePool;
                captureSession = nextSession;
                direct3DDevice = nextDevice;
                captureCts = nextCts;
                nativeD3DDevice = nextNativeDevice;
                nativeImmediateContext = nextNativeImmediateContext;
                currentContentSize = nextSize;
                currentBootTimeUnixMs = bootTimeUnixMs;
                hasDeliveredFrame = false;
                lastRestartAttemptUtc = default;
                started = true;
            }

            nextItem = null;
            nextFramePool = null;
            nextSession = null;
            nextCts = null;
            nextNativeDevice = IntPtr.Zero;
            nextNativeImmediateContext = IntPtr.Zero;

            LogLifecycle(
                "screenshare_wgc_started",
                $"target={CaptureTarget.Describe()}; width={nextSize.Width}; height={nextSize.Height}");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            LogLifecycle(
                "screenshare_wgc_start_failed",
                $"target={CaptureTarget.Describe()}; stage={startStage}; reason={ex.GetType().Name}; message={Sanitize(ex.Message)}");

            if (nextFramePool is not null)
            {
                nextFramePool.FrameArrived -= OnFrameArrived;
            }

            if (nextItem is not null)
            {
                nextItem.Closed -= OnCaptureItemClosed;
            }

            try
            {
                nextSession?.Dispose();
            }
            catch
            {
            }

            try
            {
                nextFramePool?.Dispose();
            }
            catch
            {
            }

            try
            {
                nextCts?.Dispose();
            }
            catch
            {
            }

            ReleaseNativeResources(nextNativeDevice, nextNativeImmediateContext);
            throw;
        }
    }

    public async Task StopAsync()
    {
        GraphicsCaptureItem? oldItem;
        Direct3D11CaptureFramePool? oldFramePool;
        GraphicsCaptureSession? oldSession;
        CancellationTokenSource? oldCts;
        IntPtr oldNativeDevice;
        IntPtr oldNativeImmediateContext;
        IntPtr oldStagingTexture;

        lock (sync)
        {
            if (!started)
            {
                return;
            }

            started = false;
            oldItem = captureItem;
            oldFramePool = framePool;
            oldSession = captureSession;
            oldCts = captureCts;
            oldNativeDevice = nativeD3DDevice;
            oldNativeImmediateContext = nativeImmediateContext;
            oldStagingTexture = stagingTexture;
            captureCts = null;
        }

        oldCts?.Cancel();

        if (oldFramePool is not null)
        {
            oldFramePool.FrameArrived -= OnFrameArrived;
        }

        if (oldItem is not null)
        {
            oldItem.Closed -= OnCaptureItemClosed;
        }

        await WaitForFrameDrainAsync().ConfigureAwait(false);

        lock (sync)
        {
            if (ReferenceEquals(captureItem, oldItem))
            {
                captureItem = null;
            }

            if (ReferenceEquals(framePool, oldFramePool))
            {
                framePool = null;
            }

            if (ReferenceEquals(captureSession, oldSession))
            {
                captureSession = null;
            }

            direct3DDevice = null;
            nativeD3DDevice = IntPtr.Zero;
            nativeImmediateContext = IntPtr.Zero;
            stagingTexture = IntPtr.Zero;
            stagingTextureDesc = default;
            currentContentSize = default;
            currentBootTimeUnixMs = 0;
            hasDeliveredFrame = false;
            lastRestartAttemptUtc = default;
            Interlocked.Exchange(ref restartInProgress, 0);
        }

        try
        {
            oldSession?.Dispose();
        }
        catch
        {
        }

        try
        {
            oldFramePool?.Dispose();
        }
        catch
        {
        }

        try
        {
            oldCts?.Dispose();
        }
        catch
        {
        }

        if (oldStagingTexture != IntPtr.Zero)
        {
            Marshal.Release(oldStagingTexture);
        }

        ReleaseNativeResources(oldNativeDevice, oldNativeImmediateContext);
        LogLifecycle("screenshare_wgc_stopped", $"target={CaptureTarget.Describe()}");
    }

    public bool TryGetCaptureMetadata(out ScreenCaptureMetadata metadata)
    {
        return WindowsScreenCaptureTargetCatalog.TryResolveTarget(CaptureTarget, fallbackDpiScale: null, out metadata, out _);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await StopAsync().ConfigureAwait(false);
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        const string frameStageAcquire = "acquire_frame";
        const string frameStageQuerySurface = "query_surface";
        const string frameStageEnsureStagingTexture = "ensure_staging_texture";
        const string frameStageCopySurface = "copy_surface";
        const string frameStageReadStaging = "read_staging";
        const string frameStageRecreatePool = "recreate_frame_pool";
        var frameStage = frameStageAcquire;

        if (Volatile.Read(ref restartInProgress) != 0)
        {
            TryDrainSkippedFrame(sender);
            return;
        }

        if (Interlocked.Exchange(ref frameProcessing, 1) == 1)
        {
            TryDrainSkippedFrame(sender);
            return;
        }

        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            var nextContentSize = frame.ContentSize;
            if (nextContentSize.Width <= 0 || nextContentSize.Height <= 0)
            {
                return;
            }

            frameStage = frameStageQuerySurface;
            var sourceTexture = GetD3D11TextureFromSurface(frame.Surface);
            try
            {
                frameStage = frameStageEnsureStagingTexture;
                EnsureStagingTextureForSource(sourceTexture);

                frameStage = frameStageCopySurface;
                InvokeD3D11CopyResource(nativeImmediateContext, stagingTexture, sourceTexture);

                frameStage = frameStageReadStaging;
                var deliveredBitmap = ReadStagingTexture();
                var capturedTsUtcMs = ComputeCapturedTimestampUnixMs(frame.SystemRelativeTime);
                FrameArrived?.Invoke(
                    this,
                    new WindowsRawCaptureFrameEventArgs(new WindowsRawCaptureFrame(deliveredBitmap, capturedTsUtcMs)));
                hasDeliveredFrame = true;
            }
            finally
            {
                if (sourceTexture != IntPtr.Zero)
                {
                    Marshal.Release(sourceTexture);
                }
            }

            frameStage = frameStageRecreatePool;
            RecreateFramePoolIfNeeded(nextContentSize);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (TryScheduleInternalRestart(frameStage, ex))
            {
                return;
            }

            EmitFatalCaptureFailure(frameStage, ex.GetType().Name, ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref frameProcessing, 0);
        }
    }

    private void OnCaptureItemClosed(GraphicsCaptureItem sender, object args)
    {
        LogLifecycle("screenshare_wgc_item_closed", $"target={CaptureTarget.Describe()}");
        CaptureFailed?.Invoke(
            this,
            new WindowsRawCaptureFailureEventArgs(
                "capture_item_closed",
                "CaptureItemClosed",
                $"target={CaptureTarget.Describe()}",
                isFatal: true));
        _ = Task.Run(StopAsync);
    }

    private bool TryScheduleInternalRestart(string frameStage, Exception ex)
    {
        if (!ShouldAttemptInternalRestart(frameStage, ex))
        {
            return false;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        if (Interlocked.CompareExchange(ref restartInProgress, 1, 0) != 0)
        {
            return true;
        }

        lock (sync)
        {
            if (!started || disposed || captureItem is null || framePool is null || captureSession is null || direct3DDevice is null)
            {
                Interlocked.Exchange(ref restartInProgress, 0);
                return false;
            }

            if (!hasDeliveredFrame)
            {
                Interlocked.Exchange(ref restartInProgress, 0);
                return false;
            }

            if (lastRestartAttemptUtc != default && nowUtc - lastRestartAttemptUtc <= RestartWindow)
            {
                Interlocked.Exchange(ref restartInProgress, 0);
                LogLifecycle(
                    "screenshare_wgc_restart_exhausted",
                    $"target={CaptureTarget.Describe()}; failure_stage={frameStage}; reason={ex.GetType().Name}; message={Sanitize(ex.Message)}");
                return false;
            }

            lastRestartAttemptUtc = nowUtc;
        }

        LogLifecycle(
            "screenshare_wgc_restart_requested",
            $"target={CaptureTarget.Describe()}; failure_stage={frameStage}; reason={ex.GetType().Name}; message={Sanitize(ex.Message)}");
        _ = Task.Run(() => RestartCaptureSessionAsync(frameStage, ex.GetType().Name, ex.Message));
        return true;
    }

    private async Task RestartCaptureSessionAsync(string failureStage, string failureReason, string? failureMessage)
    {
        GraphicsCaptureItem? oldItem = null;
        Direct3D11CaptureFramePool? oldFramePool = null;
        GraphicsCaptureSession? oldSession = null;
        GraphicsCaptureItem? nextItem = null;
        Direct3D11CaptureFramePool? nextFramePool = null;
        GraphicsCaptureSession? nextSession = null;

        try
        {
            await WaitForFrameDrainAsync().ConfigureAwait(false);

            if (!TryGetCaptureMetadata(out var metadata))
            {
                throw new InvalidOperationException($"Capture target could not be resolved ({CaptureTarget.Describe()}).");
            }

            var monitor = ResolveMonitorHandle(metadata.CaptureRegionPx);
            if (monitor == IntPtr.Zero)
            {
                throw new InvalidOperationException("Display monitor handle could not be resolved for Windows Graphics Capture.");
            }

            IDirect3DDevice? currentDevice;
            CancellationToken restartToken;

            lock (sync)
            {
                if (!started || disposed || direct3DDevice is null)
                {
                    return;
                }

                oldItem = captureItem;
                oldFramePool = framePool;
                oldSession = captureSession;
                currentDevice = direct3DDevice;
                restartToken = captureCts?.Token ?? CancellationToken.None;
            }

            if (oldFramePool is not null)
            {
                oldFramePool.FrameArrived -= OnFrameArrived;
            }

            if (oldItem is not null)
            {
                oldItem.Closed -= OnCaptureItemClosed;
            }

            ReleaseStagingTexture();

            try
            {
                oldSession?.Dispose();
            }
            catch
            {
            }

            try
            {
                oldFramePool?.Dispose();
            }
            catch
            {
            }

            restartToken.ThrowIfCancellationRequested();

            nextItem = CreateCaptureItemForMonitor(monitor);
            var nextSize = nextItem.Size;
            nextFramePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                currentDevice!,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                FramePoolBufferCount,
                nextSize);
            nextSession = nextFramePool.CreateCaptureSession(nextItem);
            nextFramePool.FrameArrived += OnFrameArrived;
            nextItem.Closed += OnCaptureItemClosed;
            nextSession.StartCapture();

            lock (sync)
            {
                if (!started || disposed)
                {
                    return;
                }

                captureItem = nextItem;
                framePool = nextFramePool;
                captureSession = nextSession;
                currentContentSize = nextSize;
            }

            nextItem = null;
            nextFramePool = null;
            nextSession = null;

            LogLifecycle(
                "screenshare_wgc_restart_succeeded",
                $"target={CaptureTarget.Describe()}; failure_stage={failureStage}; reason={failureReason}; message={Sanitize(failureMessage)}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            var restartStage = ResolveRestartStage(ex, "restart_capture");
            LogLifecycle(
                "screenshare_wgc_restart_exhausted",
                $"target={CaptureTarget.Describe()}; failure_stage={failureStage}; restart_stage={restartStage}; reason={ex.GetType().Name}; message={Sanitize(ex.Message)}");
            EmitFatalCaptureFailure(restartStage, ex.GetType().Name, ex.Message);
        }
        finally
        {
            if (nextFramePool is not null)
            {
                nextFramePool.FrameArrived -= OnFrameArrived;
            }

            if (nextItem is not null)
            {
                nextItem.Closed -= OnCaptureItemClosed;
            }

            try
            {
                nextSession?.Dispose();
            }
            catch
            {
            }

            try
            {
                nextFramePool?.Dispose();
            }
            catch
            {
            }

            Interlocked.Exchange(ref restartInProgress, 0);
        }
    }

    private void RecreateFramePoolIfNeeded(SizeInt32 nextContentSize)
    {
        Direct3D11CaptureFramePool? currentFramePool;
        IDirect3DDevice? currentDevice;
        bool sizeChanged;

        lock (sync)
        {
            currentFramePool = framePool;
            currentDevice = direct3DDevice;
            sizeChanged = started &&
                (currentContentSize.Width != nextContentSize.Width || currentContentSize.Height != nextContentSize.Height);
            if (!sizeChanged)
            {
                return;
            }

            currentContentSize = nextContentSize;
        }

        if (currentFramePool is null || currentDevice is null)
        {
            return;
        }

        currentFramePool.Recreate(
            currentDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            FramePoolBufferCount,
            nextContentSize);
        LogLifecycle(
            "screenshare_wgc_framepool_recreated",
            $"target={CaptureTarget.Describe()}; width={nextContentSize.Width}; height={nextContentSize.Height}");
    }

    private long ComputeCapturedTimestampUnixMs(TimeSpan? systemRelativeTime)
    {
        var bootTimeUnixMs = Volatile.Read(ref currentBootTimeUnixMs);
        if (bootTimeUnixMs <= 0 || systemRelativeTime is null || systemRelativeTime.Value < TimeSpan.Zero)
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        var candidate = bootTimeUnixMs + (long)systemRelativeTime.Value.TotalMilliseconds;
        return candidate > 0 ? candidate : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private void EnsureStagingTextureForSource(IntPtr sourceTexture)
    {
        var sourceDesc = GetTextureDesc(sourceTexture);
        if (sourceDesc.Width == 0 || sourceDesc.Height == 0)
        {
            throw new InvalidOperationException("Windows Graphics Capture source texture had invalid dimensions.");
        }

        if (stagingTexture != IntPtr.Zero &&
            stagingTextureDesc.Width == sourceDesc.Width &&
            stagingTextureDesc.Height == sourceDesc.Height &&
            stagingTextureDesc.Format == sourceDesc.Format)
        {
            return;
        }

        ReleaseStagingTexture();

        var stagingDesc = sourceDesc;
        stagingDesc.BindFlags = 0;
        stagingDesc.MiscFlags = 0;
        stagingDesc.Usage = D3D11UsageStaging;
        stagingDesc.CPUAccessFlags = D3D11CpuAccessRead;
        stagingDesc.ArraySize = 1;
        stagingDesc.MipLevels = 1;
        stagingDesc.SampleDesc = new DXGISampleDesc { Count = 1, Quality = 0 };

        Marshal.ThrowExceptionForHR(InvokeD3D11CreateTexture2D(nativeD3DDevice, ref stagingDesc, IntPtr.Zero, out stagingTexture));
        stagingTextureDesc = stagingDesc;
    }

    private Bitmap ReadStagingTexture()
    {
        if (stagingTexture == IntPtr.Zero)
        {
            throw new InvalidOperationException("Windows Graphics Capture staging texture is not available.");
        }

        var desc = stagingTextureDesc;
        var bitmap = new Bitmap((int)desc.Width, (int)desc.Height, PixelFormat.Format32bppPArgb);
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var bitmapData = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            Marshal.ThrowExceptionForHR(InvokeD3D11Map(nativeImmediateContext, stagingTexture, 0, D3D11MapRead, 0, out var mapped));
            try
            {
                var srcPtr = mapped.pData;
                var dstPtr = bitmapData.Scan0;
                var rowBytes = Math.Min(Math.Abs(bitmapData.Stride), checked((int)desc.Width * 4));
                for (var row = 0; row < bitmap.Height; row++)
                {
                    CopyMemory(dstPtr, srcPtr, checked((nuint)rowBytes));
                    srcPtr = IntPtr.Add(srcPtr, checked((int)mapped.RowPitch));
                    dstPtr = IntPtr.Add(dstPtr, bitmapData.Stride);
                }
            }
            finally
            {
                InvokeD3D11Unmap(nativeImmediateContext, stagingTexture, 0);
            }
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }

        return bitmap;
    }

    private void EmitFatalCaptureFailure(string stage, string reason, string? message)
    {
        LogLifecycle(
            "screenshare_wgc_frame_failed",
            $"target={CaptureTarget.Describe()}; stage={stage}; reason={reason}; message={Sanitize(message)}");
        CaptureFailed?.Invoke(
            this,
            new WindowsRawCaptureFailureEventArgs(
                stage,
                reason,
                Sanitize(message),
                isFatal: true));
    }

    private static bool ShouldAttemptInternalRestart(string frameStage, Exception ex)
    {
        if (ex is not ObjectDisposedException && ex is not COMException)
        {
            return false;
        }

        return string.Equals(frameStage, "query_surface", StringComparison.Ordinal) ||
               string.Equals(frameStage, "ensure_staging_texture", StringComparison.Ordinal) ||
               string.Equals(frameStage, "copy_surface", StringComparison.Ordinal) ||
               string.Equals(frameStage, "read_staging", StringComparison.Ordinal);
    }

    private static void TryDrainSkippedFrame(Direct3D11CaptureFramePool sender)
    {
        try
        {
            using var skippedFrame = sender.TryGetNextFrame();
        }
        catch
        {
        }
    }

    private void ReleaseStagingTexture()
    {
        if (stagingTexture != IntPtr.Zero)
        {
            Marshal.Release(stagingTexture);
            stagingTexture = IntPtr.Zero;
        }

        stagingTextureDesc = default;
    }

    private static D3D11Texture2DDesc GetTextureDesc(IntPtr texture)
    {
        InvokeD3D11Texture2DGetDesc(texture, out var desc);
        return desc;
    }

    private static IntPtr GetD3D11TextureFromSurface(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var texturePtr = access.GetInterface(IID_ID3D11Texture2D);
        if (texturePtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Windows Graphics Capture surface query returned a null D3D11 texture.");
        }

        return texturePtr;
    }

    private static string ResolveRestartStage(Exception ex, string fallbackStage)
    {
        if (ex is null)
        {
            return fallbackStage;
        }

        var stageProperty = ex.GetType().GetProperty("Stage");
        if (stageProperty?.GetValue(ex) is string stage && !string.IsNullOrWhiteSpace(stage))
        {
            return $"restart_{stage.Trim()}";
        }

        return fallbackStage;
    }

    private static bool IsSupportedSelection(ScreenCaptureTargetSelection selection)
    {
        return selection.Mode is ScreenCaptureTargetMode.PrimaryDisplay or ScreenCaptureTargetMode.Display;
    }

    private static IntPtr ResolveMonitorHandle(ScreenCapturePixelRect captureRegion)
    {
        var center = new PointStruct
        {
            X = captureRegion.X + (captureRegion.Width / 2),
            Y = captureRegion.Y + (captureRegion.Height / 2),
        };

        return MonitorFromPoint(center, MonitorDefaultToNearest);
    }

    private static IDirect3DDevice CreateDirect3DDevice(out IntPtr nativeD3DDevice, out IntPtr nativeImmediateContext)
    {
        var hr = D3D11CreateDevice(
            IntPtr.Zero,
            D3DDriverType.Hardware,
            IntPtr.Zero,
            D3D11CreateDeviceBgraSupport,
            IntPtr.Zero,
            0,
            D3D11SdkVersion,
            out nativeD3DDevice,
            out _,
            out nativeImmediateContext);

        if (hr < 0)
        {
            ReleaseNativeResources(nativeD3DDevice, nativeImmediateContext);
            hr = D3D11CreateDevice(
                IntPtr.Zero,
                D3DDriverType.Warp,
                IntPtr.Zero,
                D3D11CreateDeviceBgraSupport,
                IntPtr.Zero,
                0,
                D3D11SdkVersion,
                out nativeD3DDevice,
                out _,
                out nativeImmediateContext);
        }

        Marshal.ThrowExceptionForHR(hr);
        try
        {
            var dxgiDeviceGuid = IID_IDXGIDevice;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(nativeD3DDevice, ref dxgiDeviceGuid, out var dxgiDevice));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var inspectable));
                try
                {
                    return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                }
                finally
                {
                    Marshal.Release(inspectable);
                }
            }
            finally
            {
                Marshal.Release(dxgiDevice);
            }
        }
        catch
        {
            ReleaseNativeResources(nativeD3DDevice, nativeImmediateContext);
            nativeD3DDevice = IntPtr.Zero;
            nativeImmediateContext = IntPtr.Zero;
            throw;
        }
    }

    private static void ReleaseNativeResources(IntPtr nativeD3DDevice, IntPtr nativeImmediateContext)
    {
        if (nativeImmediateContext != IntPtr.Zero)
        {
            Marshal.Release(nativeImmediateContext);
        }

        if (nativeD3DDevice != IntPtr.Zero)
        {
            Marshal.Release(nativeD3DDevice);
        }
    }

    private static GraphicsCaptureItem CreateCaptureItemForMonitor(IntPtr monitor)
    {
        var runtimeClassName = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(runtimeClassName, runtimeClassName.Length, out var className));
        try
        {
            var iid = typeof(IGraphicsCaptureItemInterop).GUID;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(className, ref iid, out var activationFactory));
            try
            {
                using var interop = QueryInterface<IGraphicsCaptureItemInterop>(activationFactory);
                var itemIid = IID_IInspectable;
                Marshal.ThrowExceptionForHR(interop.Value.CreateForMonitor(monitor, ref itemIid, out var itemPtr));
                try
                {
                    return (GraphicsCaptureItem)MarshalInspectable.FromAbi(itemPtr);
                }
                finally
                {
                    Marshal.Release(itemPtr);
                }
            }
            finally
            {
                Marshal.Release(activationFactory);
            }
        }
        finally
        {
            WindowsDeleteString(className);
        }
    }

    private async Task WaitForFrameDrainAsync()
    {
        for (var i = 0; i < 100 && Volatile.Read(ref frameProcessing) != 0; i++)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private static ComReleaser<T> QueryInterface<T>(IntPtr sourceUnknown)
    {
        var iid = typeof(T).GUID;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(sourceUnknown, ref iid, out var interfacePtr));
        try
        {
            return new ComReleaser<T>((T)Marshal.GetObjectForIUnknown(interfacePtr), interfacePtr);
        }
        catch
        {
            Marshal.Release(interfacePtr);
            throw;
        }
    }

    private static int InvokeD3D11CreateTexture2D(IntPtr devicePtr, ref D3D11Texture2DDesc desc, IntPtr initialData, out IntPtr texture2D)
    {
        return GetVtableDelegate<D3D11DeviceCreateTexture2DDelegate>(devicePtr, 5)(devicePtr, ref desc, initialData, out texture2D);
    }

    private static void InvokeD3D11Texture2DGetDesc(IntPtr texturePtr, out D3D11Texture2DDesc desc)
    {
        GetVtableDelegate<D3D11Texture2DGetDescDelegate>(texturePtr, 10)(texturePtr, out desc);
    }

    private static void InvokeD3D11CopyResource(IntPtr contextPtr, IntPtr destinationResource, IntPtr sourceResource)
    {
        GetVtableDelegate<D3D11DeviceContextCopyResourceDelegate>(contextPtr, 47)(contextPtr, destinationResource, sourceResource);
    }

    private static int InvokeD3D11Map(IntPtr contextPtr, IntPtr resource, uint subresource, uint mapType, uint mapFlags, out D3D11MappedSubresource mappedResource)
    {
        return GetVtableDelegate<D3D11DeviceContextMapDelegate>(contextPtr, 14)(contextPtr, resource, subresource, mapType, mapFlags, out mappedResource);
    }

    private static void InvokeD3D11Unmap(IntPtr contextPtr, IntPtr resource, uint subresource)
    {
        GetVtableDelegate<D3D11DeviceContextUnmapDelegate>(contextPtr, 15)(contextPtr, resource, subresource);
    }

    private static TDelegate GetVtableDelegate<TDelegate>(IntPtr comInterfacePtr, int slot) where TDelegate : Delegate
    {
        if (comInterfacePtr == IntPtr.Zero)
        {
            throw new ArgumentNullException(nameof(comInterfacePtr));
        }

        var vtablePtr = Marshal.ReadIntPtr(comInterfacePtr);
        var methodPtr = Marshal.ReadIntPtr(vtablePtr, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(methodPtr);
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(none)";
        }

        return value.Replace(';', ',').Trim();
    }

    private static void LogLifecycle(string eventName, string details)
    {
        LocalOperationalLog.Info("ScreenShareTransport", $"event={eventName}; {details}");
        WriteDebugTrace($"[WgcCapture] {eventName}: {details}");
    }

    [Conditional("DEBUG")]
    private static void WriteDebugTrace(string message)
    {
        Trace.WriteLine(message);
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter,
        D3DDriverType driverType,
        IntPtr software,
        uint flags,
        IntPtr pFeatureLevels,
        uint featureLevels,
        uint sdkVersion,
        out IntPtr device,
        out D3DFeatureLevel featureLevel,
        out IntPtr immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(PointStruct point, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory", SetLastError = false)]
    private static extern void CopyMemory(IntPtr destination, IntPtr source, nuint length);

    private const uint MonitorDefaultToNearest = 0x00000002;
    private static readonly Guid IID_IDXGIDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    private static readonly Guid IID_IInspectable = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");
    private static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private enum D3DDriverType : uint
    {
        Hardware = 1,
        Warp = 5,
    }

    private enum D3DFeatureLevel : uint
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointStruct
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public DXGISampleDesc SampleDesc;
        public uint Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGISampleDesc
    {
        public uint Count;
        public uint Quality;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11MappedSubresource
    {
        public IntPtr pData;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int D3D11DeviceCreateTexture2DDelegate(
        IntPtr @this,
        ref D3D11Texture2DDesc desc,
        IntPtr initialData,
        out IntPtr texture2D);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void D3D11Texture2DGetDescDelegate(
        IntPtr @this,
        out D3D11Texture2DDesc desc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int D3D11DeviceContextMapDelegate(
        IntPtr @this,
        IntPtr resource,
        uint subresource,
        uint mapType,
        uint mapFlags,
        out D3D11MappedSubresource mappedResource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void D3D11DeviceContextUnmapDelegate(
        IntPtr @this,
        IntPtr resource,
        uint subresource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void D3D11DeviceContextCopyResourceDelegate(
        IntPtr @this,
        IntPtr destinationResource,
        IntPtr sourceResource);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr result);

        int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr result);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface(in Guid iid);
    }

    private sealed class ComReleaser<T> : IDisposable
    {
        private IntPtr interfacePtr;

        public ComReleaser(T value, IntPtr interfacePtr)
        {
            Value = value;
            this.interfacePtr = interfacePtr;
        }

        public T Value { get; }

        public void Dispose()
        {
            if (interfacePtr != IntPtr.Zero)
            {
                Marshal.Release(interfacePtr);
                interfacePtr = IntPtr.Zero;
            }
        }
    }
}
