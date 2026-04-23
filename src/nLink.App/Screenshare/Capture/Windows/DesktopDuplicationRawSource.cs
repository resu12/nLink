using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NLink.Core.Logging;

namespace NLink.App.Services.ScreenCapture;

[SupportedOSPlatform("windows")]
internal sealed class DesktopDuplicationRawSource : IWindowsRawCaptureSource, IWindowsRawCaptureBackendDescriptor
{
    private const uint D3D11SdkVersion = 7;
    private const uint D3D11CreateDeviceBgraSupport = 0x20;
    private const uint D3D11UsageStaging = 3;
    private const uint D3D11CpuAccessRead = 0x20000;
    private const uint D3D11MapRead = 1;
    private const int AcquireTimeoutMs = 100;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);
    private const int DxgiErrorAccessLost = unchecked((int)0x887A0026);
    private const int EInvalidArg = unchecked((int)0x80070057);
    private readonly object sync = new();

    private IntPtr d3dDevice;
    private IntPtr immediateContext;
    private IDXGIOutputDuplication? outputDuplication;
    private IntPtr stagingTexture;
    private D3D11Texture2DDesc stagingTextureDesc;
    private DXGIOutduplDesc duplicationDesc;
    private D3DFeatureLevel deviceFeatureLevel;
    private uint deviceCreationFlags;
    private Task? captureLoopTask;
    private CancellationTokenSource? captureCts;
    private bool started;
    private bool disposed;
    private int frameProcessing;
    private int fatalFailureRaised;
    private int duplicationBindingLogged;
    private int duplicationIdentityLogged;

    public DesktopDuplicationRawSource(ScreenCaptureTargetSelection captureTarget)
    {
        CaptureTarget = captureTarget;
    }

    public ScreenCaptureTargetSelection CaptureTarget { get; }

    public WindowsRawCaptureBackendKind BackendKind => WindowsRawCaptureBackendKind.DesktopDuplication;

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
            return TryProbeDuplicationSupport();
        }
        catch (Exception ex)
        {
            LogLifecycle("screenshare_duplication_support_probe_failed", $"reason={ex.GetType().Name}");
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
            throw new NotSupportedException("Desktop Duplication is not supported for the selected target.");
        }

        if (!TryGetCaptureMetadata(out var metadata))
        {
            throw new InvalidOperationException($"Capture target could not be resolved ({CaptureTarget.Describe()}).");
        }

        var monitor = ResolveMonitorHandle(metadata.CaptureRegionPx);
        if (monitor == IntPtr.Zero)
        {
            LogLifecycle("screenshare_duplication_monitor_resolution_failed", $"target={CaptureTarget.Describe()}");
            throw new InvalidOperationException("Display monitor handle could not be resolved for Desktop Duplication.");
        }

        try
        {
            outputDuplication = CreateOutputDuplication(monitor);
            captureCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            captureLoopTask = Task.Run(() => CaptureLoopAsync(captureCts.Token), CancellationToken.None);
        }
        catch (Exception ex)
        {
            var failureStage = ex is DesktopDuplicationStartException startException ? startException.Stage : "create_duplication";
            LogLifecycle(
                "screenshare_duplication_start_failed",
                $"target={CaptureTarget.Describe()}; stage={failureStage}; reason={ex.GetType().Name}; hresult=0x{ex.HResult:X8}; message={Sanitize(ex.Message)}");
            CaptureFailed?.Invoke(
                this,
                new WindowsRawCaptureFailureEventArgs(
                    failureStage,
                    ex.GetType().Name,
                    Sanitize(ex.Message),
                    isFatal: true));
            captureLoopTask = null;
            captureCts?.Dispose();
            captureCts = null;
            ReleaseComObject(outputDuplication);
            outputDuplication = null;
            ReleaseStagingTexture();
            ReleaseDevice();
            throw;
        }

        lock (sync)
        {
            started = true;
        }

        LogLifecycle("screenshare_duplication_started", $"target={CaptureTarget.Describe()}");
        TryEmitInitialSnapshot(metadata);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        Task? oldLoopTask;
        CancellationTokenSource? oldCts;
        IDXGIOutputDuplication? oldDuplication;
        IntPtr oldStagingTexture;

        lock (sync)
        {
            if (!started)
            {
                return;
            }

            started = false;
            oldLoopTask = captureLoopTask;
            oldCts = captureCts;
            oldDuplication = outputDuplication;
            oldStagingTexture = stagingTexture;

            captureLoopTask = null;
            captureCts = null;
            outputDuplication = null;
            stagingTexture = IntPtr.Zero;
        }

        oldCts?.Cancel();

        if (oldLoopTask is not null)
        {
            try
            {
                await oldLoopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        try
        {
            oldDuplication?.ReleaseFrame();
        }
        catch
        {
        }

        if (oldStagingTexture != IntPtr.Zero)
        {
            Marshal.Release(oldStagingTexture);
        }
        ReleaseComObject(oldDuplication);
        oldCts?.Dispose();
        ReleaseDevice();
        LogLifecycle("screenshare_duplication_stopped", $"target={CaptureTarget.Describe()}");
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

    private async Task CaptureLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!TryCaptureFrame(out var frame))
                {
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (DesktopDuplicationAccessLostException)
            {
                LogLifecycle("screenshare_duplication_access_lost", $"target={CaptureTarget.Describe()}");
                CaptureFailed?.Invoke(
                    this,
                    new WindowsRawCaptureFailureEventArgs(
                        "capture_loop",
                        nameof(DesktopDuplicationAccessLostException),
                        $"target={CaptureTarget.Describe()}",
                        isFatal: true));
                break;
            }
            catch (DesktopDuplicationFatalException ex)
            {
                LogLifecycle(
                    "screenshare_duplication_fatal",
                    $"target={CaptureTarget.Describe()}; stage={ex.Stage}; reason={ex.InnerException?.GetType().Name ?? ex.GetType().Name}; message={Sanitize(ex.Message)}");
                if (Interlocked.Exchange(ref fatalFailureRaised, 1) == 0)
                {
                    CaptureFailed?.Invoke(
                        this,
                        new WindowsRawCaptureFailureEventArgs(
                            ex.Stage,
                            ex.InnerException?.GetType().Name ?? ex.GetType().Name,
                            Sanitize(ex.Message),
                            isFatal: true));
                }

                break;
            }
            catch (Exception ex)
            {
                LogLifecycle(
                    "screenshare_duplication_frame_failed",
                    $"target={CaptureTarget.Describe()}; reason={ex.GetType().Name}; message={Sanitize(ex.Message)}");
                CaptureFailed?.Invoke(
                    this,
                    new WindowsRawCaptureFailureEventArgs(
                        "capture_loop",
                        ex.GetType().Name,
                        Sanitize(ex.Message)));
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private bool TryCaptureFrame(out WindowsRawCaptureFrame? frame)
    {
        frame = null;
        var stage = "enter";
        if (Interlocked.Exchange(ref frameProcessing, 1) == 1)
        {
            return false;
        }

        IntPtr desktopResource = IntPtr.Zero;
        var frameAcquired = false;
        var textureIdentityValidated = false;
        try
        {
            stage = "validate_duplication";
            if (outputDuplication is null)
            {
                return false;
            }

            stage = "acquire_next_frame";
            var hr = outputDuplication.AcquireNextFrame(AcquireTimeoutMs, out var frameInfo, out desktopResource);
            if (hr == DxgiErrorWaitTimeout)
            {
                return false;
            }

            if (hr == DxgiErrorAccessLost)
            {
                throw new DesktopDuplicationAccessLostException();
            }

            Marshal.ThrowExceptionForHR(hr);
            frameAcquired = true;

            if (desktopResource == IntPtr.Zero)
            {
                return false;
            }

            stage = "query_dxgi_resource";
            using var resource = QueryInterface<IDXGIResource>(desktopResource);
            textureIdentityValidated = true;
            LogDuplicationTextureIdentity(resourceQuerySucceeded: true);
            stage = "ensure_staging_texture";
            EnsureStagingTexture();
            stage = "copy_to_staging";
            CopyToStagingTexture(desktopResource);
            stage = "read_staging";
            frame = ReadStagingTexture(frameInfo.LastPresentTime);
            if (frame is not null)
            {
                FrameArrived?.Invoke(this, new WindowsRawCaptureFrameEventArgs(frame));
                frame = null;
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            LogLifecycle(
                "screenshare_duplication_frame_stage_failed",
                $"target={CaptureTarget.Describe()}; stage={stage}; texture_identity_validated={(textureIdentityValidated ? 1 : 0)}; reason={ex.GetType().Name}; hresult=0x{ex.HResult:X8}; message={Sanitize(ex.Message)}");
            throw;
        }
        finally
        {
            if (frameAcquired)
            {
                try
                {
                    outputDuplication?.ReleaseFrame();
                }
                catch
                {
                }
            }

            if (desktopResource != IntPtr.Zero)
            {
                Marshal.Release(desktopResource);
            }
            Interlocked.Exchange(ref frameProcessing, 0);
        }
    }

    private void EnsureStagingTexture()
    {
        try
        {
            var sourceDesc = GetExpectedSourceTextureDesc();
            ValidateTextureDescOrThrow(sourceDesc, "source_texture_desc");
            if (stagingTexture != IntPtr.Zero)
            {
                if (stagingTextureDesc.Width == sourceDesc.Width &&
                    stagingTextureDesc.Height == sourceDesc.Height &&
                    stagingTextureDesc.Format == sourceDesc.Format)
                {
                    return;
                }

                ReleaseStagingTexture();
            }

            var stagingDesc = sourceDesc;
            stagingDesc.BindFlags = 0;
            stagingDesc.MiscFlags = 0;
            stagingDesc.Usage = D3D11UsageStaging;
            stagingDesc.CPUAccessFlags = D3D11CpuAccessRead;
            stagingDesc.ArraySize = 1;
            stagingDesc.MipLevels = 1;
            ValidateTextureDescOrThrow(stagingDesc, "staging_texture_desc");
            LogStagingTextureAttempt(sourceDesc, stagingDesc);

            using var device = QueryInterface<ID3D11Device>(d3dDevice);
            Marshal.ThrowExceptionForHR(device.Value.CreateTexture2D(ref stagingDesc, IntPtr.Zero, out var texturePtr));
            try
            {
                stagingTexture = texturePtr;
                stagingTextureDesc = stagingDesc;
                texturePtr = IntPtr.Zero;
            }
            finally
            {
                if (texturePtr != IntPtr.Zero)
                {
                    Marshal.Release(texturePtr);
                }
            }
        }
        catch (Exception ex)
        {
            var sourceDesc = GetExpectedSourceTextureDesc();
            LogLifecycle(
                "screenshare_duplication_staging_texture_failed",
                $"target={CaptureTarget.Describe()}; source_width={sourceDesc.Width}; source_height={sourceDesc.Height}; source_format={sourceDesc.Format}; source_usage={sourceDesc.Usage}; source_bind_flags={sourceDesc.BindFlags}; source_cpu_access={sourceDesc.CPUAccessFlags}; source_array_size={sourceDesc.ArraySize}; source_mip_levels={sourceDesc.MipLevels}; reason={ex.GetType().Name}; hresult=0x{ex.HResult:X8}; message={Sanitize(ex.Message)}");

            throw new DesktopDuplicationFatalException("ensure_staging_texture", "Desktop Duplication staging texture creation failed.", ex);
        }
    }

    private void CopyToStagingTexture(IntPtr sourceTexture)
    {
        InvokeD3D11CopyResource(immediateContext, stagingTexture, sourceTexture);
    }

    private WindowsRawCaptureFrame ReadStagingTexture(long lastPresentQpcTicks)
    {
        if (stagingTexture == IntPtr.Zero)
        {
            throw new InvalidOperationException("Desktop duplication staging texture is not available.");
        }

        var desc = stagingTextureDesc;
        var bitmap = new Bitmap((int)desc.Width, (int)desc.Height, PixelFormat.Format32bppPArgb);
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var bitmapData = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            Marshal.ThrowExceptionForHR(InvokeD3D11Map(immediateContext, stagingTexture, 0, D3D11MapRead, 0, out var mapped));
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
                InvokeD3D11Unmap(immediateContext, stagingTexture, 0);
            }
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }

        return new WindowsRawCaptureFrame(bitmap, ComputeCapturedTimestamp(lastPresentQpcTicks));
    }

    private void TryEmitInitialSnapshot(ScreenCaptureMetadata metadata)
    {
        try
        {
            var region = metadata.CaptureRegionPx;
            if (region.Width <= 0 || region.Height <= 0)
            {
                return;
            }

            var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(region.X, region.Y, 0, 0, new Size(region.Width, region.Height), CopyPixelOperation.SourceCopy);
            }

            FrameArrived?.Invoke(
                this,
                new WindowsRawCaptureFrameEventArgs(
                    new WindowsRawCaptureFrame(bitmap, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())));

            LogLifecycle(
                "screenshare_duplication_initial_snapshot_emitted",
                $"target={CaptureTarget.Describe()}; width={region.Width}; height={region.Height}");
        }
        catch (Exception ex)
        {
            LogLifecycle(
                "screenshare_duplication_initial_snapshot_failed",
                $"target={CaptureTarget.Describe()}; reason={ex.GetType().Name}; message={Sanitize(ex.Message)}");
        }
    }

    private long ComputeCapturedTimestamp(long lastPresentQpcTicks)
    {
        if (lastPresentQpcTicks <= 0)
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        var nowTimestamp = Stopwatch.GetTimestamp();
        var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var deltaTicks = nowTimestamp - lastPresentQpcTicks;
        if (deltaTicks <= 0)
        {
            return nowUnixMs;
        }

        var deltaMs = (long)(deltaTicks * 1000d / Stopwatch.Frequency);
        return Math.Max(0, nowUnixMs - deltaMs);
    }

    private void CreateDevice()
    {
        ReleaseDevice();
        var hr = D3D11CreateDevice(
            IntPtr.Zero,
            D3DDriverType.Hardware,
            IntPtr.Zero,
            D3D11CreateDeviceBgraSupport,
            IntPtr.Zero,
            0,
            D3D11SdkVersion,
            out d3dDevice,
            out _,
            out immediateContext);
        if (hr < 0)
        {
            hr = D3D11CreateDevice(
                IntPtr.Zero,
                D3DDriverType.Warp,
                IntPtr.Zero,
                D3D11CreateDeviceBgraSupport,
                IntPtr.Zero,
                0,
                D3D11SdkVersion,
                out d3dDevice,
                out _,
                out immediateContext);
        }

        Marshal.ThrowExceptionForHR(hr);
    }

    private IDXGIOutputDuplication CreateOutputDuplication(IntPtr monitor)
    {
        const string stage = "create_dxgi_factory";
        try
        {
            var factoryGuid = IID_IDXGIFactory1;
            Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref factoryGuid, out var factoryPtr));
            try
            {
                using var factory = QueryInterface<IDXGIFactory1>(factoryPtr);
                for (uint adapterIndex = 0; ; adapterIndex++)
                {
                    var hr = factory.Value.EnumAdapters1(adapterIndex, out var adapterPtr);
                    if (hr == DxgiErrorNotFound)
                    {
                        break;
                    }

                    if (hr < 0)
                    {
                        LogLifecycle(
                            "screenshare_duplication_enum_adapter_failed",
                            $"target={CaptureTarget.Describe()}; stage=enum_adapters; adapter_index={adapterIndex}; hresult=0x{hr:X8}");
                    }

                    Marshal.ThrowExceptionForHR(hr);
                    try
                    {
                    using var adapter = QueryInterface<IDXGIAdapter1>(adapterPtr);
                    var adapterDesc = TryGetAdapterDesc1(adapter.Value, out var resolvedAdapterDesc)
                        ? resolvedAdapterDesc
                        : (DXGIAdapterDesc1?)null;
                    var duplication = TryCreateDuplicationForAdapter(adapter.Value, adapterPtr, adapterIndex, monitor, adapterDesc);
                        if (duplication is not null)
                        {
                            return duplication;
                        }
                    }
                    finally
                    {
                        if (adapterPtr != IntPtr.Zero)
                        {
                            Marshal.Release(adapterPtr);
                        }
                    }
                }
            }
            finally
            {
                Marshal.Release(factoryPtr);
            }

            throw new NotSupportedException("Desktop Duplication could not find a matching monitor output.");
        }
        catch (Exception ex) when (ex is not DesktopDuplicationStartException)
        {
            throw new DesktopDuplicationStartException(stage, "Desktop Duplication factory/output enumeration failed.", ex);
        }
    }

    private IDXGIOutputDuplication? TryCreateDuplicationForAdapter(
        IDXGIAdapter1 adapter,
        IntPtr adapterPtr,
        uint adapterIndex,
        IntPtr monitor,
        DXGIAdapterDesc1? adapterDesc)
    {
        for (uint outputIndex = 0; ; outputIndex++)
        {
            var hr = adapter.EnumOutputs(outputIndex, out var outputPtr);
            if (hr == DxgiErrorNotFound)
            {
                return null;
            }

            if (hr < 0)
            {
                LogLifecycle(
                    "screenshare_duplication_enum_output_failed",
                    $"target={CaptureTarget.Describe()}; stage=enum_outputs; adapter_index={adapterIndex}; output_index={outputIndex}; hresult=0x{hr:X8}");
            }

            Marshal.ThrowExceptionForHR(hr);
            try
            {
                using var output = QueryInterface<IDXGIOutput>(outputPtr);
                output.Value.GetDesc(out var desc);
                if (desc.Monitor != monitor)
                {
                    continue;
                }

                LogDuplicationBinding(adapterIndex, outputIndex, desc, adapterDesc);
                try
                {
                    CreateDeviceForAdapter(adapterPtr, adapterIndex, outputIndex, desc, adapterDesc);
                }
                catch (Exception ex)
                {
                    LogLifecycle(
                        "screenshare_duplication_device_create_failed",
                        $"target={CaptureTarget.Describe()}; stage=create_device; adapter_index={adapterIndex}; output_index={outputIndex}; output_name={Sanitize(desc.DeviceName)}; {DescribeAdapter(adapterDesc)}; hresult=0x{ex.HResult:X8}; reason={ex.GetType().Name}; message={Sanitize(ex.Message)}");
                    throw new DesktopDuplicationStartException("create_device", "Desktop Duplication device creation failed.", ex);
                }
                using var output1 = QueryInterface<IDXGIOutput1>(outputPtr);
                var duplicateHr = output1.Value.DuplicateOutput(d3dDevice, out var duplicationPtr);
                if (duplicateHr < 0)
                {
                    LogLifecycle(
                        "screenshare_duplication_duplicate_output_failed",
                        $"target={CaptureTarget.Describe()}; stage=duplicate_output; adapter_index={adapterIndex}; output_index={outputIndex}; output_name={Sanitize(desc.DeviceName)}; monitor=0x{desc.Monitor.ToInt64():X}; feature_level={FormatFeatureLevel(deviceFeatureLevel)}; creation_flags=0x{deviceCreationFlags:X8}; {DescribeAdapter(adapterDesc)}; hresult=0x{duplicateHr:X8}");
                    if (duplicateHr == EInvalidArg)
                    {
                        LogLifecycle(
                            "screenshare_duplication_unsupported",
                            $"target={CaptureTarget.Describe()}; stage=duplicate_output; adapter_index={adapterIndex}; output_index={outputIndex}; output_name={Sanitize(desc.DeviceName)}; feature_level={FormatFeatureLevel(deviceFeatureLevel)}; creation_flags=0x{deviceCreationFlags:X8}; {DescribeAdapter(adapterDesc)}; hresult=0x{duplicateHr:X8}");
                    }
                }

                Marshal.ThrowExceptionForHR(duplicateHr);
                if (duplicationPtr == IntPtr.Zero)
                {
                    throw new InvalidOperationException("DuplicateOutput returned a null duplication interface.");
                }

                try
                {
                    using var duplication = QueryInterface<IDXGIOutputDuplication>(duplicationPtr);
                    duplication.Value.GetDesc(out duplicationDesc);
                    return duplication.Detach();
                }
                finally
                {
                    Marshal.Release(duplicationPtr);
                }
            }
            catch (Exception ex) when (ex is not DesktopDuplicationStartException)
            {
                throw new DesktopDuplicationStartException("duplicate_output", "Desktop Duplication output duplication setup failed.", ex);
            }
            finally
            {
                if (outputPtr != IntPtr.Zero)
                {
                    Marshal.Release(outputPtr);
                }
            }
        }
    }

    private void CreateDeviceForAdapter(
        IntPtr adapterPtr,
        uint adapterIndex,
        uint outputIndex,
        DXGIOutputDesc outputDesc,
        DXGIAdapterDesc1? adapterDesc)
    {
        ReleaseDevice();
        deviceFeatureLevel = 0;
        deviceCreationFlags = D3D11CreateDeviceBgraSupport;
        var usedFeatureLevelRetry = false;
        var hr = D3D11CreateDevice(
            adapterPtr,
            D3DDriverType.Unknown,
            IntPtr.Zero,
            deviceCreationFlags,
            IntPtr.Zero,
            0,
            D3D11SdkVersion,
            out d3dDevice,
            out deviceFeatureLevel,
            out immediateContext);
        if (hr < 0)
        {
            ReleaseDevice();
            usedFeatureLevelRetry = true;
            LogLifecycle(
                "screenshare_duplication_device_create_retry",
                $"target={CaptureTarget.Describe()}; stage=create_device; adapter_index={adapterIndex}; output_index={outputIndex}; output_name={Sanitize(outputDesc.DeviceName)}; feature_levels={DescribePreferredFeatureLevels()}; creation_flags=0x{deviceCreationFlags:X8}; {DescribeAdapter(adapterDesc)}; initial_hresult=0x{hr:X8}");
            using var featureLevels = CreateFeatureLevelBuffer(PreferredDuplicationFeatureLevels);
            hr = D3D11CreateDevice(
                adapterPtr,
                D3DDriverType.Unknown,
                IntPtr.Zero,
                deviceCreationFlags,
                featureLevels.Pointer,
                featureLevels.Count,
                D3D11SdkVersion,
                out d3dDevice,
                out deviceFeatureLevel,
                out immediateContext);
        }

        if (hr >= 0)
        {
            LogLifecycle(
                "screenshare_duplication_device_created",
                $"target={CaptureTarget.Describe()}; adapter_index={adapterIndex}; output_index={outputIndex}; output_name={Sanitize(outputDesc.DeviceName)}; feature_level={FormatFeatureLevel(deviceFeatureLevel)}; creation_flags=0x{deviceCreationFlags:X8}; retry={(usedFeatureLevelRetry ? 1 : 0)}; {DescribeAdapter(adapterDesc)}");
        }

        Marshal.ThrowExceptionForHR(hr);
    }

    private static bool TryProbeDuplicationSupport()
    {
        IntPtr device = IntPtr.Zero;
        IntPtr context = IntPtr.Zero;
        try
        {
            var hr = D3D11CreateDevice(
                IntPtr.Zero,
                D3DDriverType.Hardware,
                IntPtr.Zero,
                D3D11CreateDeviceBgraSupport,
                IntPtr.Zero,
                0,
                D3D11SdkVersion,
                out device,
                out _,
                out context);
            if (hr < 0)
            {
                hr = D3D11CreateDevice(
                    IntPtr.Zero,
                    D3DDriverType.Warp,
                    IntPtr.Zero,
                    D3D11CreateDeviceBgraSupport,
                    IntPtr.Zero,
                    0,
                    D3D11SdkVersion,
                    out device,
                    out _,
                    out context);
            }

            if (hr < 0)
            {
                return false;
            }

            var factoryGuid = IID_IDXGIFactory1;
            if (CreateDXGIFactory1(ref factoryGuid, out var factoryPtr) < 0)
            {
                return false;
            }

            try
            {
            using var factory = QueryInterface<IDXGIFactory1>(factoryPtr);
            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                var enumAdapterHr = factory.Value.EnumAdapters1(adapterIndex, out var adapterPtr);
                if (enumAdapterHr == DxgiErrorNotFound)
                {
                    break;
                }

                if (enumAdapterHr < 0)
                {
                    return false;
                }

                try
                {
                    using var adapter = QueryInterface<IDXGIAdapter1>(adapterPtr);
                    var enumOutputHr = adapter.Value.EnumOutputs(0, out var outputPtr);
                    if (enumOutputHr == 0 && outputPtr != IntPtr.Zero)
                    {
                        Marshal.Release(outputPtr);
                        return true;
                    }
                }
                finally
                {
                    if (adapterPtr != IntPtr.Zero)
                    {
                        Marshal.Release(adapterPtr);
                    }
                }
            }

            return false;
        }
        finally
        {
                Marshal.Release(factoryPtr);
            }
        }
        finally
        {
            if (context != IntPtr.Zero)
            {
                Marshal.Release(context);
            }

            if (device != IntPtr.Zero)
            {
                Marshal.Release(device);
            }
        }
    }

    private void ReleaseDevice()
    {
        if (immediateContext != IntPtr.Zero)
        {
            Marshal.Release(immediateContext);
            immediateContext = IntPtr.Zero;
        }

        if (d3dDevice != IntPtr.Zero)
        {
            Marshal.Release(d3dDevice);
            d3dDevice = IntPtr.Zero;
        }

        Interlocked.Exchange(ref fatalFailureRaised, 0);
        Interlocked.Exchange(ref duplicationBindingLogged, 0);
        Interlocked.Exchange(ref duplicationIdentityLogged, 0);
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

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.ReleaseComObject(value);
        }
    }

    private static ComReleaser<T> QueryInterface<T>(object comObject)
    {
        var sourceUnknown = Marshal.GetIUnknownForObject(comObject);
        try
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
        finally
        {
            Marshal.Release(sourceUnknown);
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
        WriteDebugTrace($"[DesktopDuplication] {eventName}: {details}");
    }

    [Conditional("DEBUG")]
    private static void WriteDebugTrace(string message)
    {
        Trace.WriteLine(message);
    }

    private void LogDuplicationBinding(uint adapterIndex, uint outputIndex, DXGIOutputDesc desc, DXGIAdapterDesc1? adapterDesc)
    {
        if (Interlocked.Exchange(ref duplicationBindingLogged, 1) == 1)
        {
            return;
        }

        LogLifecycle(
            "screenshare_duplication_output_binding",
            $"target={CaptureTarget.Describe()}; adapter_index={adapterIndex}; output_index={outputIndex}; device_name={Sanitize(desc.DeviceName)}; attached={(desc.AttachedToDesktop ? 1 : 0)}; monitor=0x{desc.Monitor.ToInt64():X}; {DescribeAdapter(adapterDesc)}");
    }

    private void LogDuplicationTextureIdentity(bool resourceQuerySucceeded)
    {
        if (Interlocked.Exchange(ref duplicationIdentityLogged, 1) == 1)
        {
            return;
        }

        var desc = GetExpectedSourceTextureDesc();
        var valid = IsTextureDescPlausible(desc);
        LogLifecycle(
            "screenshare_duplication_texture_identity",
            $"target={CaptureTarget.Describe()}; resource_qi={(resourceQuerySucceeded ? 1 : 0)}; desc_valid={(valid ? 1 : 0)}; width={desc.Width}; height={desc.Height}; format={desc.Format}; usage={desc.Usage}; bind_flags={desc.BindFlags}; cpu_access={desc.CPUAccessFlags}; array_size={desc.ArraySize}; mip_levels={desc.MipLevels}; sample_count={desc.SampleDesc.Count}; sample_quality={desc.SampleDesc.Quality}");
    }

    private void LogStagingTextureAttempt(D3D11Texture2DDesc sourceDesc, D3D11Texture2DDesc stagingDesc)
    {
        LogLifecycle(
            "screenshare_duplication_staging_texture_config",
            $"target={CaptureTarget.Describe()}; source_width={sourceDesc.Width}; source_height={sourceDesc.Height}; source_format={sourceDesc.Format}; source_usage={sourceDesc.Usage}; staging_width={stagingDesc.Width}; staging_height={stagingDesc.Height}; staging_format={stagingDesc.Format}; staging_usage={stagingDesc.Usage}; staging_cpu_access={stagingDesc.CPUAccessFlags}; staging_array_size={stagingDesc.ArraySize}; staging_mip_levels={stagingDesc.MipLevels}");
    }

    private static bool IsTextureDescPlausible(D3D11Texture2DDesc desc)
    {
        return desc.Width > 0 &&
               desc.Width <= 16_384 &&
               desc.Height > 0 &&
               desc.Height <= 16_384 &&
               desc.Format != 0 &&
               desc.ArraySize > 0 &&
               desc.MipLevels > 0 &&
               desc.SampleDesc.Count > 0;
    }

    private static void ValidateTextureDescOrThrow(D3D11Texture2DDesc desc, string stage)
    {
        if (IsTextureDescPlausible(desc))
        {
            return;
        }

        throw new ArgumentException(
            $"Invalid D3D11 texture desc at stage '{stage}' (width={desc.Width}, height={desc.Height}, format={desc.Format}, arraySize={desc.ArraySize}, mipLevels={desc.MipLevels}, sampleCount={desc.SampleDesc.Count}).");
    }

    private D3D11Texture2DDesc GetExpectedSourceTextureDesc()
    {
        return new D3D11Texture2DDesc
        {
            Width = duplicationDesc.ModeDesc.Width,
            Height = duplicationDesc.ModeDesc.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = duplicationDesc.ModeDesc.Format,
            SampleDesc = new DXGISampleDesc { Count = 1, Quality = 0 },
            Usage = 0,
            BindFlags = 0,
            CPUAccessFlags = 0,
            MiscFlags = 0,
        };
    }

    private static FeatureLevelBuffer CreateFeatureLevelBuffer(IReadOnlyList<D3DFeatureLevel> featureLevels)
    {
        var count = featureLevels.Count;
        var pointer = Marshal.AllocHGlobal(sizeof(int) * count);
        try
        {
            var values = new int[count];
            for (var i = 0; i < count; i++)
            {
                values[i] = unchecked((int)featureLevels[i]);
            }

            Marshal.Copy(values, 0, pointer, count);
            return new FeatureLevelBuffer(pointer, (uint)count);
        }
        catch
        {
            Marshal.FreeHGlobal(pointer);
            throw;
        }
    }

    private static bool TryGetAdapterDesc1(IDXGIAdapter1 adapter, out DXGIAdapterDesc1 desc)
    {
        try
        {
            var hr = adapter.GetDesc1(out desc);
            if (hr >= 0)
            {
                return true;
            }
        }
        catch
        {
        }

        desc = default;
        return false;
    }

    private static string DescribeAdapter(DXGIAdapterDesc1? adapterDesc)
    {
        if (adapterDesc is not { } desc)
        {
            return "adapter_desc=unavailable";
        }

        return $"adapter_name={Sanitize(desc.Description)}; vendor_id=0x{desc.VendorId:X8}; device_id=0x{desc.DeviceId:X8}; subsystem_id=0x{desc.SubSysId:X8}; revision={desc.Revision}; luid={FormatLuid(desc.AdapterLuid)}";
    }

    private static string DescribePreferredFeatureLevels()
    {
        var builder = new StringBuilder();
        for (var i = 0; i < PreferredDuplicationFeatureLevels.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(FormatFeatureLevel(PreferredDuplicationFeatureLevels[i]));
        }

        return builder.ToString();
    }

    private static string FormatFeatureLevel(D3DFeatureLevel featureLevel)
    {
        return featureLevel switch
        {
            D3DFeatureLevel.Level11_1 => "11_1",
            D3DFeatureLevel.Level11_0 => "11_0",
            D3DFeatureLevel.Level10_1 => "10_1",
            D3DFeatureLevel.Level10_0 => "10_0",
            _ => $"0x{(uint)featureLevel:X4}",
        };
    }

    private static string FormatLuid(DXGILuid luid)
    {
        var high = unchecked((uint)luid.HighPart);
        return $"0x{high:X8}{luid.LowPart:X8}";
    }

    private readonly record struct FeatureLevelBuffer(IntPtr Pointer, uint Count) : IDisposable
    {
        public void Dispose()
        {
            if (Pointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Pointer);
            }
        }
    }

    private void ReleaseStagingTexture()
    {
        if (stagingTexture != IntPtr.Zero)
        {
            Marshal.Release(stagingTexture);
            stagingTexture = IntPtr.Zero;
        }
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

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(PointStruct point, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory", SetLastError = false)]
    private static extern void CopyMemory(IntPtr destination, IntPtr source, nuint length);

    private static readonly Guid IID_IDXGIFactory1 = new("770AAE78-F26F-4DBA-A829-253C83D1B387");
    private static readonly Guid IID_ID3D11Device = new("DB6F6DDB-AC77-4E88-8253-819DF9BBF140");
    private static readonly D3DFeatureLevel[] PreferredDuplicationFeatureLevels =
    [
        D3DFeatureLevel.Level11_1,
        D3DFeatureLevel.Level11_0,
        D3DFeatureLevel.Level10_1,
        D3DFeatureLevel.Level10_0,
    ];
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);

    private enum D3DDriverType : uint
    {
        Unknown = 0,
        Hardware = 1,
        Warp = 5,
    }

    private enum D3DFeatureLevel : uint
    {
        Level10_0 = 0xA000,
        Level10_1 = 0xA100,
        Level11_0 = 0xB000,
        Level11_1 = 0xB100,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointStruct
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGIOutputDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public RECT DesktopCoordinates;
        [MarshalAs(UnmanagedType.Bool)]
        public bool AttachedToDesktop;
        public DXGIModeRotation Rotation;
        public IntPtr Monitor;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGIAdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public DXGILuid AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGILuid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private enum DXGIModeRotation
    {
        Unspecified = 0,
        Identity = 1,
        Rotate90 = 2,
        Rotate180 = 3,
        Rotate270 = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGIOutduplFrameInfo
    {
        public long LastPresentTime;
        public long LastMouseUpdateTime;
        public uint AccumulatedFrames;
        [MarshalAs(UnmanagedType.Bool)]
        public bool RectsCoalesced;
        [MarshalAs(UnmanagedType.Bool)]
        public bool ProtectedContentMaskedOut;
        public DXGIOutduplPointerPosition PointerPosition;
        public uint TotalMetadataBufferSize;
        public uint PointerShapeBufferSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGIOutduplDesc
    {
        public DXGIModeDesc ModeDesc;
        public DXGIModeRotation Rotation;
        [MarshalAs(UnmanagedType.Bool)]
        public bool DesktopImageInSystemMemory;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGIModeDesc
    {
        public uint Width;
        public uint Height;
        public DXGIRational RefreshRate;
        public uint Format;
        public uint ScanlineOrdering;
        public uint Scaling;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGIRational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGIOutduplPointerPosition
    {
        public POINT Position;
        [MarshalAs(UnmanagedType.Bool)]
        public bool Visible;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
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

    [ComImport]
    [Guid("770AAE78-F26F-4DBA-A829-253C83D1B387")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        int SetPrivateData();
        int SetPrivateDataInterface();
        int GetPrivateData();
        int GetParent();
        int EnumAdapters(uint adapter, out IntPtr ppAdapter);
        int MakeWindowAssociation();
        int GetWindowAssociation();
        int CreateSwapChain();
        int CreateSoftwareAdapter();
        int EnumAdapters1(uint adapter, out IntPtr ppAdapter);
        int IsCurrent();
    }

    [ComImport]
    [Guid("29038F61-3839-4626-91FD-086879011A05")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        int SetPrivateData();
        int SetPrivateDataInterface();
        int GetPrivateData();
        int GetParent();
        int EnumOutputs(uint output, out IntPtr ppOutput);
        int GetDesc();
        int CheckInterfaceSupport();
        int GetDesc1(out DXGIAdapterDesc1 pDesc);
    }

    [ComImport]
    [Guid("AE02EEDB-C735-4690-8D52-5A8DC20213AA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIOutput
    {
        int SetPrivateData();
        int SetPrivateDataInterface();
        int GetPrivateData();
        int GetParent();
        int GetDesc(out DXGIOutputDesc pDesc);
    }

    [ComImport]
    [Guid("00CDDEA8-939B-4B83-A340-A685226666CC")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIOutput1
    {
        int SetPrivateData();
        int SetPrivateDataInterface();
        int GetPrivateData();
        int GetParent();
        int GetDesc(out DXGIOutputDesc pDesc);
        int GetDisplayModeList();
        int FindClosestMatchingMode();
        int WaitForVBlank();
        int TakeOwnership();
        void ReleaseOwnership();
        int GetGammaControlCapabilities();
        int SetGammaControl();
        int GetGammaControl();
        int SetDisplaySurface();
        int GetDisplaySurfaceData();
        int GetFrameStatistics();
        int GetDisplayModeList1();
        int FindClosestMatchingMode1();
        int GetDisplaySurfaceData1();
        int DuplicateOutput(IntPtr pDevice, out IntPtr ppOutputDuplication);
    }

    [ComImport]
    [Guid("191CFAC3-A341-470D-B26E-A864F428319C")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIOutputDuplication
    {
        int SetPrivateData();
        int SetPrivateDataInterface();
        int GetPrivateData();
        int GetParent();
        void GetDesc(out DXGIOutduplDesc desc);
        int AcquireNextFrame(int timeoutInMilliseconds, out DXGIOutduplFrameInfo frameInfo, out IntPtr desktopResource);
        int GetFrameDirtyRects();
        int GetFrameMoveRects();
        int GetFramePointerShape();
        int MapDesktopSurface();
        void UnMapDesktopSurface();
        int ReleaseFrame();
    }

    [ComImport]
    [Guid("1841E5C8-16B0-489B-BCC8-44CFB0D5DEAE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3D11Texture2D : ID3D11Resource
    {
        void GetDesc(out D3D11Texture2DDesc desc);
    }

    [ComImport]
    [Guid("DB6F6DDB-AC77-4E88-8253-819DF9BBF140")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3D11Device
    {
        int CreateBuffer();
        int CreateTexture1D();
        int CreateTexture2D(ref D3D11Texture2DDesc desc, IntPtr initialData, out IntPtr texture2D);
    }

    [ComImport]
    [Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3D11DeviceContext
    {
        int GetDevice(out IntPtr device);
        int GetPrivateData();
        int SetPrivateData();
        int SetPrivateDataInterface();
        int VSSetConstantBuffers();
        int PSSetShaderResources();
        int PSSetShader();
        int PSSetSamplers();
        int VSSetShader();
        int DrawIndexed();
        int Draw();
        int Map(IntPtr resource, uint subresource, uint mapType, uint mapFlags, out D3D11MappedSubresource mappedResource);
        void Unmap(IntPtr resource, uint subresource);
        void PSSetConstantBuffers();
        void IASetInputLayout();
        void IASetVertexBuffers();
        void IASetIndexBuffer();
        void DrawIndexedInstanced();
        void DrawInstanced();
        void GSSetConstantBuffers();
        void GSSetShader();
        void IASetPrimitiveTopology();
        void VSSetShaderResources();
        void VSSetSamplers();
        void Begin();
        void End();
        void GetData();
        void SetPredication();
        void GSSetShaderResources();
        void GSSetSamplers();
        void OMSetRenderTargets();
        void OMSetRenderTargetsAndUnorderedAccessViews();
        void OMSetBlendState();
        void OMSetDepthStencilState();
        void SOSetTargets();
        void DrawAuto();
        void DrawIndexedInstancedIndirect();
        void DrawInstancedIndirect();
        void Dispatch();
        void DispatchIndirect();
        void RSSetState();
        void RSSetViewports();
        void RSSetScissorRects();
        void CopySubresourceRegion();
        void CopyResource(IntPtr destinationResource, IntPtr sourceResource);
    }

    [ComImport]
    [Guid("DC8E63F3-D12B-4952-B47B-5E45026A862D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3D11Resource : ID3D11DeviceChild
    {
        void GetType(out D3D11ResourceDimension resourceDimension);
        void SetEvictionPriority(uint evictionPriority);
        uint GetEvictionPriority();
    }

    [ComImport]
    [Guid("035F3AB4-482E-4E50-B41F-8A7F8BD8960B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIResource
    {
        int SetPrivateData();
        int SetPrivateDataInterface();
        int GetPrivateData();
        int GetParent();
        int GetSharedHandle(out IntPtr pSharedHandle);
        int GetUsage(out uint pUsage);
        int SetEvictionPriority(uint evictionPriority);
        int GetEvictionPriority(out uint evictionPriority);
    }

    [ComImport]
    [Guid("1841E5C6-16B0-489B-BCC8-44CFB0D5DEAE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3D11DeviceChild
    {
        void GetDevice(out IntPtr device);
        int GetPrivateData();
        int SetPrivateData();
        int SetPrivateDataInterface();
    }

    private enum D3D11ResourceDimension : uint
    {
        Unknown = 0,
        Buffer = 1,
        Texture1D = 2,
        Texture2D = 3,
        Texture3D = 4,
    }

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

    private sealed class DesktopDuplicationAccessLostException : Exception
    {
    }

    private sealed class DesktopDuplicationFatalException : Exception
    {
        public DesktopDuplicationFatalException(string stage, string message, Exception innerException)
            : base(message, innerException)
        {
            Stage = stage;
        }

        public string Stage { get; }
    }

    private sealed class DesktopDuplicationStartException : Exception
    {
        public DesktopDuplicationStartException(string stage, string message, Exception innerException)
            : base(message, innerException)
        {
            Stage = stage;
            HResult = innerException.HResult;
        }

        public string Stage { get; }
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

        public T Detach()
        {
            var detached = Value;
            interfacePtr = IntPtr.Zero;
            return detached;
        }

        public static implicit operator T(ComReleaser<T> releaser)
        {
            return releaser.Value;
        }

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
