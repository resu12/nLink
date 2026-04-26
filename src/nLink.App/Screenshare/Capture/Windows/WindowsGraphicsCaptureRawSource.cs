using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
internal sealed class WindowsGraphicsCaptureRawSource : IWindowsRawCaptureSource, IWindowsRawCaptureBackendDescriptor, IScreenCaptureCursorCaptureControl, IWindowsRawCaptureCadenceControl, IWindowsRawCaptureOutputControl
{
    private const uint D3D11SdkVersion = 7;
    private const uint D3D11CreateDeviceBgraSupport = 0x20;
    private const uint D3D11UsageDefault = 0;
    private const uint D3D11UsageStaging = 3;
    private const uint D3D11CpuAccessRead = 0x20000;
    private const uint D3D11BindShaderResource = 0x8;
    private const uint D3D11BindRenderTarget = 0x20;
    private const uint D3D11MapRead = 1;
    private const int FramePoolBufferCount = 2;
    private static readonly TimeSpan OwnerThreadCloseTimeout = TimeSpan.FromSeconds(2);
    private static readonly Guid IClosableGuid = new("30D5A829-7FA4-4026-83BB-D75BAE4EA99E");
    private static readonly Guid GraphicsCaptureSession2Guid = new("2C39AE40-7D2E-5044-804E-8B6799D4CF9E");
    private static readonly Guid GraphicsCaptureSession3Guid = new("F2CDD966-22AE-5EA1-9596-3A289344C3BE");
    private static readonly TimeSpan RestartWindow = TimeSpan.FromSeconds(2);
    private static readonly object sessionLeaseSync = new();
    private static readonly Dictionary<long, WgcSessionLease> sessionLeases = new();
    private static long nextSessionLeaseId;
    private static long forceCloseAllCount;
    private static long sessionCloseAnomalyCount;
    private static string lastSessionCloseStatus = string.Empty;
    private static string lastSessionCloseMethod = string.Empty;
    private static string lastSessionCloseHResult = string.Empty;
    private static int lastSessionOwnerThreadId;
    private static int lastSessionCloseThreadId;
    private static int lastSessionCloseOnOwnerThread;
    private static long ownerThreadCloseTimeoutCount;
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
    private IntPtr gpuScaleVideoDevice;
    private IntPtr gpuScaleVideoContext;
    private IntPtr gpuScaleEnumerator;
    private IntPtr gpuScaleProcessor;
    private IntPtr gpuScaleInputTexture;
    private IntPtr gpuScaleInputView;
    private IntPtr gpuScaleOutputTexture;
    private IntPtr gpuScaleOutputView;
    private IntPtr gpuScaleStagingTexture;
    private D3D11Texture2DDesc gpuScaleInputTextureDesc;
    private D3D11Texture2DDesc gpuScaleOutputTextureDesc;
    private D3D11Texture2DDesc gpuScaleStagingTextureDesc;
    private bool started;
    private bool disposed;
    private bool hasDeliveredFrame;
    private int frameProcessing;
    private int restartInProgress;
    private SizeInt32 currentContentSize;
    private SizeInt32 currentFramePoolSize;
    private long currentBootTimeUnixMs;
    private DateTimeOffset lastRestartAttemptUtc;
    private bool desiredCursorCaptureEnabled = true;
    private bool cursorCaptureEnabled = true;
    private bool desiredBorderRequired = true;
    private bool borderRequired = true;
    private bool borderRequiredControlSupported;
    private string borderRequiredApplyStatus = string.Empty;
    private string borderRequiredFallbackReason = string.Empty;
    private readonly WindowsRawCaptureCadenceGate cadenceGate = new();
    private int targetOutputWidth;
    private int targetOutputHeight;
    private int outputSizeHintRevision;
    private int appliedOutputSizeHintRevision;
    private int gpuScaleReadbackDisabled;
    private int gpuScaleFallbackLogged;
    private string gpuScaleFallbackReason = string.Empty;
    private long lifecycleGeneration;
    private long activeSessionLeaseId;
    private long lastStopDurationMs = -1;
    private string lastStopReason = string.Empty;
    private WgcOwnerDispatcher? ownerDispatcher;

    public WindowsGraphicsCaptureRawSource(ScreenCaptureTargetSelection captureTarget, string sourceRole = "unknown")
    {
        CaptureTarget = captureTarget;
        SourceRole = string.IsNullOrWhiteSpace(sourceRole) ? "unknown" : sourceRole.Trim().ToLowerInvariant();
    }

    public ScreenCaptureTargetSelection CaptureTarget { get; }

    public string SourceRole { get; }

    public WindowsRawCaptureBackendKind BackendKind => WindowsRawCaptureBackendKind.WindowsGraphicsCapture;

    public bool IsSupported => IsSupportedSelection(CaptureTarget) && IsRuntimeSupported();

    public bool IsCursorCaptureControlSupported => IsCursorCaptureControlRuntimeSupported();

    public bool IsCursorCaptureEnabled
    {
        get
        {
            lock (sync)
            {
                return cursorCaptureEnabled;
            }
        }
    }

    public event EventHandler<WindowsRawCaptureFrameEventArgs>? FrameArrived;
    public event EventHandler<WindowsRawCaptureFailureEventArgs>? CaptureFailed;

    public void SetRawCaptureCadence(int targetFramesPerSecond, string reason)
    {
        cadenceGate.SetCadence(targetFramesPerSecond);
    }

    public void ForceNextRawCapture(string reason)
    {
        cadenceGate.ForceNext();
    }

    public WindowsRawCaptureRuntimeMetrics GetRawCaptureRuntimeMetricsSnapshot()
    {
        var snapshot = cadenceGate.GetSnapshot();
        lock (sync)
        {
            return snapshot with
            {
                CaptureActive = started,
                BorderRequiredControlSupported = borderRequiredControlSupported,
                BorderRequiredDesired = desiredBorderRequired,
                BorderRequired = borderRequired,
                BorderRequiredApplyStatus = borderRequiredApplyStatus,
                BorderRequiredFallbackReason = borderRequiredFallbackReason,
                LastStopDurationMs = Interlocked.Read(ref lastStopDurationMs),
                LastStopReason = lastStopReason,
                ActiveSessionLeaseCount = GetActiveSessionLeaseCount(),
                LastSessionCloseStatus = Volatile.Read(ref lastSessionCloseStatus),
                LastSessionCloseMethod = Volatile.Read(ref lastSessionCloseMethod),
                LastSessionCloseHResult = Volatile.Read(ref lastSessionCloseHResult),
                ForceCloseCount = Interlocked.Read(ref forceCloseAllCount),
                SessionCloseAnomalyCount = Interlocked.Read(ref sessionCloseAnomalyCount),
                SessionOwnerThreadId = Volatile.Read(ref lastSessionOwnerThreadId),
                LastSessionCloseThreadId = Volatile.Read(ref lastSessionCloseThreadId),
                LastSessionCloseOnOwnerThread = Volatile.Read(ref lastSessionCloseOnOwnerThread) != 0,
                OwnerDispatcherActive = ownerDispatcher?.IsAcceptingWork == true,
                OwnerThreadCloseTimeoutCount = Interlocked.Read(ref ownerThreadCloseTimeoutCount),
            };
        }
    }

    public void SetRawCaptureOutputSizeHint(int targetWidth, int targetHeight, string reason)
    {
        var width = Math.Max(0, targetWidth);
        var height = Math.Max(0, targetHeight);
        var previousWidth = Volatile.Read(ref targetOutputWidth);
        var previousHeight = Volatile.Read(ref targetOutputHeight);
        Volatile.Write(ref targetOutputWidth, width);
        Volatile.Write(ref targetOutputHeight, height);
        if (width != previousWidth || height != previousHeight)
        {
            Interlocked.Increment(ref outputSizeHintRevision);
            Interlocked.Exchange(ref gpuScaleFallbackLogged, 0);
            if (width > 0 && height > 0)
            {
                Interlocked.Exchange(ref gpuScaleReadbackDisabled, 0);
                Volatile.Write(ref gpuScaleFallbackReason, string.Empty);
            }
        }
    }

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

    private WgcOwnerDispatcher EnsureOwnerDispatcher()
    {
        lock (sync)
        {
            if (ownerDispatcher?.IsAcceptingWork == true)
            {
                return ownerDispatcher;
            }

            ownerDispatcher = new WgcOwnerDispatcher(CaptureTarget.Describe(), SourceRole);
            return ownerDispatcher;
        }
    }

    private void DisposeOwnerDispatcher(WgcOwnerDispatcher? dispatcher)
    {
        if (dispatcher is null)
        {
            return;
        }

        lock (sync)
        {
            if (ReferenceEquals(ownerDispatcher, dispatcher))
            {
                ownerDispatcher = null;
            }
        }

        dispatcher.Dispose();
    }

    private void InvokeOnOwnerThread(Action action)
    {
        WgcOwnerDispatcher? dispatcher;
        lock (sync)
        {
            dispatcher = ownerDispatcher;
        }

        InvokeOnOwnerThread(dispatcher, action);
    }

    private static void InvokeOnOwnerThread(WgcOwnerDispatcher? dispatcher, Action action)
    {
        if (dispatcher?.IsAcceptingWork == true)
        {
            dispatcher.InvokeAsync(action).GetAwaiter().GetResult();
            return;
        }

        action();
    }

    private static T InvokeOnOwnerThread<T>(WgcOwnerDispatcher? dispatcher, Func<T> action)
    {
        if (dispatcher?.IsAcceptingWork == true)
        {
            return dispatcher.InvokeAsync(action).GetAwaiter().GetResult();
        }

        return action();
    }

    private static Task InvokeOnOwnerThreadAsync(WgcOwnerDispatcher? dispatcher, Action action)
    {
        if (dispatcher?.IsAcceptingWork == true)
        {
            return dispatcher.InvokeAsync(action);
        }

        action();
        return Task.CompletedTask;
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

        var dispatcher = EnsureOwnerDispatcher();
        return StartOnOwnerThreadAsync(dispatcher, monitor, cancellationToken);
    }

    private async Task StartOnOwnerThreadAsync(
        WgcOwnerDispatcher dispatcher,
        IntPtr monitor,
        CancellationToken cancellationToken)
    {
        try
        {
            await dispatcher.InvokeAsync(() => StartOnOwnerThread(monitor, cancellationToken)).ConfigureAwait(false);
        }
        catch
        {
            DisposeOwnerDispatcher(dispatcher);
            throw;
        }
    }

    private void StartOnOwnerThread(IntPtr monitor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var startStage = "create_device";
        IntPtr nextNativeDevice = IntPtr.Zero;
        IntPtr nextNativeImmediateContext = IntPtr.Zero;
        GraphicsCaptureItem? nextItem = null;
        Direct3D11CaptureFramePool? nextFramePool = null;
        GraphicsCaptureSession? nextSession = null;
        CancellationTokenSource? nextCts = null;
        long nextSessionLeaseId = 0;

        try
        {
            var nextDevice = CreateDirect3DDevice(out nextNativeDevice, out nextNativeImmediateContext);
            startStage = "create_capture_item";
            nextItem = CreateCaptureItemForMonitor(monitor);
            var nextSize = nextItem.Size;

            startStage = "create_session";
            nextFramePool = CreateFullSizeFramePool(nextDevice, nextSize);
            nextSession = nextFramePool.CreateCaptureSession(nextItem);
            nextSessionLeaseId = RegisterSessionLease(nextSession, Interlocked.Read(ref lifecycleGeneration) + 1, "start_create_session");
            nextCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var bootTimeUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Environment.TickCount64;

            nextFramePool.FrameArrived += OnFrameArrived;
            nextItem.Closed += OnCaptureItemClosed;
            TryApplyCursorCapturePreference(nextSession, desiredCursorCaptureEnabled, "start");
            TryApplyBorderRequiredPreference(nextSession, required: true, "start");

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
                currentFramePoolSize = nextSize;
                appliedOutputSizeHintRevision = Volatile.Read(ref outputSizeHintRevision);
                currentBootTimeUnixMs = bootTimeUnixMs;
                activeSessionLeaseId = nextSessionLeaseId;
                hasDeliveredFrame = false;
                lastRestartAttemptUtc = default;
                started = true;
                lifecycleGeneration = checked(lifecycleGeneration + 1);
            }

            nextItem = null;
            nextFramePool = null;
            nextSession = null;
            nextCts = null;
            nextNativeDevice = IntPtr.Zero;
            nextNativeImmediateContext = IntPtr.Zero;

            LogLifecycle(
                "screenshare_wgc_started",
                $"target={CaptureTarget.Describe()}; width={nextSize.Width}; height={nextSize.Height}; cursor_capture_enabled={(cursorCaptureEnabled ? 1 : 0)}; cursor_control_supported={(IsCursorCaptureControlSupported ? 1 : 0)}; border_required={(borderRequired ? 1 : 0)}; border_control_supported={(borderRequiredControlSupported ? 1 : 0)}; lifecycle_generation={Interlocked.Read(ref lifecycleGeneration)}");
            return;
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
                CloseGraphicsCaptureSession(
                    nextSession,
                    nextSessionLeaseId,
                    "start_failed",
                    CaptureTarget.Describe(),
                    SourceRole,
                    0,
                    removeLeaseAfterAttempt: true);
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

    public bool TrySetCursorCaptureEnabled(bool enabled, string reason)
    {
        GraphicsCaptureSession? currentSession;
        WgcOwnerDispatcher? dispatcher;
        lock (sync)
        {
            desiredCursorCaptureEnabled = enabled;
            currentSession = captureSession;
            dispatcher = ownerDispatcher;
            if (currentSession is null)
            {
                cursorCaptureEnabled = enabled;
                LogCursorCaptureMode("cursor_preference_queued", desiredEnabled: enabled, actualEnabled: enabled, "queued_before_start", reason);
                return IsCursorCaptureControlSupported;
            }
        }

        return InvokeOnOwnerThread(dispatcher, () => TryApplyCursorCapturePreference(currentSession, enabled, reason));
    }

    private static bool IsCursorCaptureControlRuntimeSupported()
    {
        try
        {
            return Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent(
                "Windows.Graphics.Capture.GraphicsCaptureSession",
                "IsCursorCaptureEnabled");
        }
        catch (Exception ex)
        {
            LogLifecycle("screenshare_wgc_cursor_support_probe_failed", $"reason={ex.GetType().Name}");
            return false;
        }
    }

    private bool TryApplyCursorCapturePreference(GraphicsCaptureSession session, bool enabled, string reason)
    {
        if (!IsCursorCaptureControlSupported)
        {
            lock (sync)
            {
                cursorCaptureEnabled = true;
            }

            LogCursorCaptureMode("cursor_capture_fallback", desiredEnabled: enabled, actualEnabled: true, "unsupported", reason);
            return false;
        }

        try
        {
            var actualEnabled = TrySetGraphicsCaptureSessionCursorCaptureEnabled(session, enabled);
            lock (sync)
            {
                cursorCaptureEnabled = actualEnabled;
            }

            LogCursorCaptureMode("cursor_capture_applied", desiredEnabled: enabled, actualEnabled, "applied", reason);
            return true;
        }
        catch (Exception ex)
        {
            lock (sync)
            {
                cursorCaptureEnabled = true;
            }

            LogCursorCaptureMode("cursor_capture_fallback", desiredEnabled: enabled, actualEnabled: true, ex.GetType().Name, reason);
            return false;
        }
    }

    private static bool TrySetGraphicsCaptureSessionCursorCaptureEnabled(GraphicsCaptureSession session, bool enabled)
    {
        using var sessionReference = MarshalInspectable.CreateMarshaler(session, false);
        var sessionAbi = MarshalInspectable.GetAbi(sessionReference);
        if (sessionAbi == IntPtr.Zero)
        {
            throw new InvalidOperationException("GraphicsCaptureSession ABI pointer was not available.");
        }

        var session2Guid = GraphicsCaptureSession2Guid;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(sessionAbi, ref session2Guid, out var session2));
        try
        {
            var vtable = Marshal.ReadIntPtr(session2);
            var getCursorCaptureEnabledPtr = Marshal.ReadIntPtr(vtable, IntPtr.Size * 6);
            var putCursorCaptureEnabledPtr = Marshal.ReadIntPtr(vtable, IntPtr.Size * 7);
            var putCursorCaptureEnabled = Marshal.GetDelegateForFunctionPointer<GraphicsCaptureSession2PutCursorCaptureEnabledDelegate>(putCursorCaptureEnabledPtr);
            var getCursorCaptureEnabled = Marshal.GetDelegateForFunctionPointer<GraphicsCaptureSession2GetCursorCaptureEnabledDelegate>(getCursorCaptureEnabledPtr);

            Marshal.ThrowExceptionForHR(putCursorCaptureEnabled(session2, enabled ? (byte)1 : (byte)0));
            Marshal.ThrowExceptionForHR(getCursorCaptureEnabled(session2, out var actualEnabled));
            return actualEnabled != 0;
        }
        finally
        {
            if (session2 != IntPtr.Zero)
            {
                Marshal.Release(session2);
            }
        }
    }

    private static void LogCursorCaptureMode(string eventName, bool desiredEnabled, bool actualEnabled, string status, string reason)
    {
        LogLifecycle(
            "screenshare_wgc_" + eventName,
            $"cursor_capture_desired_enabled={(desiredEnabled ? 1 : 0)}; cursor_capture_enabled={(actualEnabled ? 1 : 0)}; cursor_control_supported={(IsCursorCaptureControlRuntimeSupported() ? 1 : 0)}; status={Sanitize(status)}; reason={Sanitize(string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason)}");
    }

    private static bool IsBorderRequiredControlRuntimeSupported()
    {
        try
        {
            return Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent(
                "Windows.Graphics.Capture.GraphicsCaptureSession",
                "IsBorderRequired");
        }
        catch (Exception ex)
        {
            LogLifecycle("screenshare_wgc_border_support_probe_failed", $"reason={ex.GetType().Name}");
            return false;
        }
    }

    private bool TryApplyBorderRequiredPreference(GraphicsCaptureSession session, bool required, string reason)
    {
        desiredBorderRequired = required;
        if (!IsBorderRequiredControlRuntimeSupported())
        {
            borderRequiredControlSupported = false;
            borderRequired = true;
            borderRequiredApplyStatus = "unsupported";
            borderRequiredFallbackReason = "unsupported";
            LogBorderRequiredMode(required, actualRequired: true, "unsupported", reason, applied: false);
            return false;
        }

        borderRequiredControlSupported = true;
        try
        {
            var actualRequired = TrySetGraphicsCaptureSessionBorderRequired(session, required);
            borderRequired = actualRequired;
            borderRequiredApplyStatus = actualRequired == required ? "applied" : "ignored";
            borderRequiredFallbackReason = actualRequired == required ? string.Empty : "ignored_by_os";
            LogBorderRequiredMode(required, actualRequired, borderRequiredApplyStatus, reason, actualRequired == required);
            return actualRequired == required;
        }
        catch (Exception ex)
        {
            borderRequired = true;
            borderRequiredApplyStatus = ex.GetType().Name;
            borderRequiredFallbackReason = ex.GetType().Name;
            LogBorderRequiredMode(required, actualRequired: true, ex.GetType().Name, reason, applied: false);
            return false;
        }
    }

    private static bool TrySetGraphicsCaptureSessionBorderRequired(GraphicsCaptureSession session, bool required)
    {
        using var sessionReference = MarshalInspectable.CreateMarshaler(session, false);
        var sessionAbi = MarshalInspectable.GetAbi(sessionReference);
        if (sessionAbi == IntPtr.Zero)
        {
            throw new InvalidOperationException("GraphicsCaptureSession ABI pointer was not available.");
        }

        var session3Guid = GraphicsCaptureSession3Guid;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(sessionAbi, ref session3Guid, out var session3));
        try
        {
            var vtable = Marshal.ReadIntPtr(session3);
            var getBorderRequiredPtr = Marshal.ReadIntPtr(vtable, IntPtr.Size * 6);
            var putBorderRequiredPtr = Marshal.ReadIntPtr(vtable, IntPtr.Size * 7);
            var putBorderRequired = Marshal.GetDelegateForFunctionPointer<GraphicsCaptureSession3PutBorderRequiredDelegate>(putBorderRequiredPtr);
            var getBorderRequired = Marshal.GetDelegateForFunctionPointer<GraphicsCaptureSession3GetBorderRequiredDelegate>(getBorderRequiredPtr);

            Marshal.ThrowExceptionForHR(putBorderRequired(session3, required ? (byte)1 : (byte)0));
            Marshal.ThrowExceptionForHR(getBorderRequired(session3, out var actualRequired));
            return actualRequired != 0;
        }
        finally
        {
            if (session3 != IntPtr.Zero)
            {
                Marshal.Release(session3);
            }
        }
    }

    private static void LogBorderRequiredMode(
        bool desiredRequired,
        bool actualRequired,
        string status,
        string reason,
        bool applied)
    {
        LogLifecycle(
            "screenshare_wgc_border_required_mode",
            $"border_required_desired={(desiredRequired ? 1 : 0)}; border_required={(actualRequired ? 1 : 0)}; border_control_supported={(IsBorderRequiredControlRuntimeSupported() ? 1 : 0)}; applied={(applied ? 1 : 0)}; status={Sanitize(status)}; reason={Sanitize(string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason)}");
    }

    public async Task StopAsync()
    {
        var stopStartedAt = Stopwatch.GetTimestamp();
        const string stopReason = "stop_async";
        GraphicsCaptureItem? oldItem;
        Direct3D11CaptureFramePool? oldFramePool;
        GraphicsCaptureSession? oldSession;
        CancellationTokenSource? oldCts;
        IntPtr oldNativeDevice;
        IntPtr oldNativeImmediateContext;
        IntPtr oldStagingTexture;
        long stopGeneration;
        long oldSessionLeaseId;
        WgcOwnerDispatcher? oldOwnerDispatcher;

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
            oldSessionLeaseId = activeSessionLeaseId;
            oldOwnerDispatcher = ownerDispatcher;
            captureCts = null;
            captureItem = null;
            framePool = null;
            captureSession = null;
            direct3DDevice = null;
            activeSessionLeaseId = 0;
            lifecycleGeneration = checked(lifecycleGeneration + 1);
            stopGeneration = lifecycleGeneration;
        }

        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_wgc_stop_requested; target={CaptureTarget.Describe()}; reason={stopReason}; lifecycle_generation={stopGeneration}");

        oldCts?.Cancel();

        var sessionCloseResult = await CloseSessionOnOwnerThreadAsync(
            oldOwnerDispatcher,
            oldSession,
            oldSessionLeaseId,
            oldFramePool,
            oldItem,
            stopReason,
            stopGeneration,
            removeLeaseAfterAttempt: false).ConfigureAwait(false);
        var sessionDisposed = sessionCloseResult.Closed;
        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_wgc_session_disposed; target={CaptureTarget.Describe()}; disposed={(sessionDisposed ? 1 : 0)}; close_method={sessionCloseResult.Method}; close_status={sessionCloseResult.Status}; close_hresult={sessionCloseResult.HResult}; lifecycle_generation={stopGeneration}");

        await WaitForFrameDrainAsync().ConfigureAwait(false);

        lock (sync)
        {
            nativeD3DDevice = IntPtr.Zero;
            nativeImmediateContext = IntPtr.Zero;
            stagingTexture = IntPtr.Zero;
            stagingTextureDesc = default;
            currentContentSize = default;
            currentFramePoolSize = default;
            currentBootTimeUnixMs = 0;
            hasDeliveredFrame = false;
            lastRestartAttemptUtc = default;
            Interlocked.Exchange(ref restartInProgress, 0);
        }

        var framePoolDisposed = await DisposeFramePoolOnOwnerThreadAsync(
            oldOwnerDispatcher,
            oldFramePool,
            oldItem).ConfigureAwait(false);
        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_wgc_framepool_disposed; target={CaptureTarget.Describe()}; disposed={(framePoolDisposed ? 1 : 0)}; lifecycle_generation={stopGeneration}");

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

        ReleaseGpuScaleResources();
        ReleaseNativeResources(oldNativeDevice, oldNativeImmediateContext);
        var stopElapsedMs = Math.Max(0, (long)Stopwatch.GetElapsedTime(stopStartedAt).TotalMilliseconds);
        lock (sync)
        {
            lastStopReason = stopReason;
        }

        Interlocked.Exchange(ref lastStopDurationMs, stopElapsedMs);
        LogLifecycle(
            "screenshare_wgc_stop_completed",
            $"target={CaptureTarget.Describe()}; reason={stopReason}; elapsed_ms={stopElapsedMs}; session_disposed={(sessionDisposed ? 1 : 0)}; framepool_disposed={(framePoolDisposed ? 1 : 0)}; lifecycle_generation={stopGeneration}");
        LogLifecycle("screenshare_wgc_stopped", $"target={CaptureTarget.Describe()}");

        if (sessionDisposed && framePoolDisposed)
        {
            DisposeOwnerDispatcher(oldOwnerDispatcher);
        }
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
        const string frameStageGpuScale = "gpu_scale";
        const string frameStageReadStaging = "read_staging";
        const string frameStageRecreatePool = "recreate_frame_pool";
        var frameStage = frameStageAcquire;
        var callbackGeneration = Interlocked.Read(ref lifecycleGeneration);

        cadenceGate.RecordFrameArrived();
        if (Volatile.Read(ref restartInProgress) != 0)
        {
            TryDrainSkippedFrame(sender);
            cadenceGate.RecordSkippedBeforeReadback();
            return;
        }

        if (Interlocked.Exchange(ref frameProcessing, 1) == 1)
        {
            TryDrainSkippedFrame(sender);
            cadenceGate.RecordSkippedBeforeReadback();
            return;
        }

        try
        {
            if (!IsStartedForGeneration(callbackGeneration))
            {
                TryDrainSkippedFrame(sender);
                cadenceGate.RecordSkippedBeforeReadback();
                return;
            }

            var recreateFramePool = false;
            var recreateContentSize = default(SizeInt32);

            {
                using var frame = sender.TryGetNextFrame();
                if (frame is null)
                {
                    return;
                }

                var nextContentSize = ResolveCurrentCaptureItemSize(frame.ContentSize);
                if (nextContentSize.Width <= 0 || nextContentSize.Height <= 0)
                {
                    return;
                }

                recreateFramePool = true;
                recreateContentSize = nextContentSize;

                if (!cadenceGate.ShouldSkipBeforeReadback(DateTimeOffset.UtcNow, hasDeliveredFrame))
                {
                    frameStage = frameStageQuerySurface;
                    var sourceTexture = GetD3D11TextureFromSurface(frame.Surface);
                    try
                    {
                        var sourceDesc = GetTextureDesc(sourceTexture);
                        if (sourceDesc.Width == 0 || sourceDesc.Height == 0)
                        {
                            throw new InvalidOperationException("Windows Graphics Capture source texture had invalid dimensions.");
                        }

                        frameStage = frameStageEnsureStagingTexture;
                        var readbackTexture = IntPtr.Zero;
                        var readbackDesc = default(D3D11Texture2DDesc);
                        var gpuScaleEnabled = false;
                        frameStage = frameStageGpuScale;
                        if (TryGpuScaleToReadbackTexture(sourceTexture, sourceDesc, out readbackTexture, out readbackDesc))
                        {
                            gpuScaleEnabled = true;
                        }
                        else
                        {
                            frameStage = frameStageEnsureStagingTexture;
                            EnsureStagingTextureForSourceDesc(sourceDesc);

                            frameStage = frameStageCopySurface;
                            InvokeD3D11CopyResource(nativeImmediateContext, stagingTexture, sourceTexture);
                            readbackTexture = stagingTexture;
                            readbackDesc = stagingTextureDesc;
                        }

                        frameStage = frameStageReadStaging;
                        var readbackStartedAt = Stopwatch.GetTimestamp();
                        var deliveredBitmap = ReadStagingTexture(readbackTexture, readbackDesc);
                        if (!IsStartedForGeneration(callbackGeneration))
                        {
                            deliveredBitmap.Dispose();
                            return;
                        }

                        UpdateOutputDiagnostics(deliveredBitmap.Width, deliveredBitmap.Height, gpuScaleEnabled);
                        cadenceGate.RecordReadback(Stopwatch.GetElapsedTime(readbackStartedAt), DateTimeOffset.UtcNow);
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
                }
            }

            if (recreateFramePool)
            {
                frameStage = frameStageRecreatePool;
                if (IsStartedForGeneration(callbackGeneration))
                {
                    InvokeOnOwnerThread(() => RecreateFramePoolIfNeeded(recreateContentSize));
                }
            }
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

    private void UpdateOutputDiagnostics(int outputWidth, int outputHeight, bool gpuScaleEnabled)
    {
        var desiredWidth = Volatile.Read(ref targetOutputWidth);
        var desiredHeight = Volatile.Read(ref targetOutputHeight);
        SizeInt32 contentSize;
        lock (sync)
        {
            contentSize = currentContentSize;
        }

        var target = ResolveFramePoolTarget(
            contentSize.Width,
            contentSize.Height,
            desiredWidth,
            desiredHeight,
            Volatile.Read(ref gpuScaleReadbackDisabled) != 0);
        gpuScaleEnabled = gpuScaleEnabled &&
            target.UsesGpuScaleReadback &&
            outputWidth == target.Width &&
            outputHeight == target.Height;
        var fallbackReason = ResolveGpuScaleOutputFallbackReason(target, outputWidth, outputHeight);
        cadenceGate.SetOutputDiagnostics(
            outputWidth,
            outputHeight,
            gpuScaleEnabled,
            fallbackReason);

        if (target.GpuScaleRequested && !gpuScaleEnabled && Interlocked.Exchange(ref gpuScaleFallbackLogged, 1) == 0)
        {
            LogLifecycle(
                "screenshare_wgc_gpu_scale_fallback",
                $"target={CaptureTarget.Describe()}; source_width={contentSize.Width}; source_height={contentSize.Height}; desired_width={desiredWidth}; desired_height={desiredHeight}; output_width={outputWidth}; output_height={outputHeight}; reason={fallbackReason}");
        }
    }

    private bool IsStartedForGeneration(long generation)
    {
        lock (sync)
        {
            return started && lifecycleGeneration == generation;
        }
    }

    private SizeInt32 ResolveCurrentCaptureItemSize(SizeInt32 fallbackSize)
    {
        try
        {
            GraphicsCaptureItem? currentItem;
            lock (sync)
            {
                currentItem = captureItem;
            }

            var itemSize = currentItem?.Size ?? fallbackSize;
            return itemSize.Width > 0 && itemSize.Height > 0 ? itemSize : fallbackSize;
        }
        catch
        {
            return fallbackSize;
        }
    }

    private static Direct3D11CaptureFramePool CreateFullSizeFramePool(
        IDirect3DDevice device,
        SizeInt32 contentSize)
    {
        return Direct3D11CaptureFramePool.CreateFreeThreaded(
            device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            FramePoolBufferCount,
            contentSize);
    }

    private FramePoolTarget RecreateFullSizeFramePool(
        Direct3D11CaptureFramePool pool,
        IDirect3DDevice device,
        SizeInt32 contentSize,
        SizeInt32 currentPoolSize)
    {
        var fallbackTarget = ResolveFramePoolFallbackTarget(
            contentSize,
            gpuScaleRequested: false,
            fallbackReason: "(none)");
        if (CanReuseExistingFullSizeFramePool(contentSize, currentPoolSize))
        {
            return fallbackTarget;
        }

        pool.Recreate(
            device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            FramePoolBufferCount,
            contentSize);
        return fallbackTarget;
    }

    private FramePoolTarget ResolveRequestedFramePoolTarget(SizeInt32 contentSize)
        => ResolveFramePoolTarget(
            contentSize.Width,
            contentSize.Height,
            Volatile.Read(ref targetOutputWidth),
            Volatile.Read(ref targetOutputHeight),
            Volatile.Read(ref gpuScaleReadbackDisabled) != 0);

    internal static FramePoolTarget ResolveFramePoolTargetForTesting(
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        bool disabledAfterFailure)
        => ResolveFramePoolTarget(sourceWidth, sourceHeight, targetWidth, targetHeight, disabledAfterFailure);

    internal static FramePoolTarget ResolveFramePoolTargetAfterGpuScaleFallbackForTesting(
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        string fallbackReason)
    {
        var target = ResolveFramePoolTarget(sourceWidth, sourceHeight, targetWidth, targetHeight, disabledAfterFailure: false);
        return ResolveFramePoolFallbackTarget(
            new SizeInt32 { Width = Math.Max(0, sourceWidth), Height = Math.Max(0, sourceHeight) },
            target.GpuScaleRequested,
            fallbackReason);
    }

    internal static bool CanReuseExistingFullSizeFramePoolForTesting(
        int contentWidth,
        int contentHeight,
        int currentPoolWidth,
        int currentPoolHeight)
        => CanReuseExistingFullSizeFramePool(
            new SizeInt32 { Width = contentWidth, Height = contentHeight },
            new SizeInt32 { Width = currentPoolWidth, Height = currentPoolHeight });

    internal static WgcOwnerDispatcherDiagnostics RunOwnerDispatcherRoundTripForTesting()
    {
        using var dispatcher = new WgcOwnerDispatcher("test", "test");
        var callerThreadId = Environment.CurrentManagedThreadId;
        var workThreadId = dispatcher.InvokeAsync(() => Environment.CurrentManagedThreadId).GetAwaiter().GetResult();
        return new WgcOwnerDispatcherDiagnostics(
            callerThreadId,
            dispatcher.OwnerThreadId,
            workThreadId,
            dispatcher.OwnerThreadId == workThreadId,
            dispatcher.OwnerThreadId != callerThreadId);
    }

    private static FramePoolTarget ResolveFramePoolTarget(
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        bool disabledAfterFailure)
    {
        sourceWidth = Math.Max(0, sourceWidth);
        sourceHeight = Math.Max(0, sourceHeight);
        targetWidth = Math.Max(0, targetWidth);
        targetHeight = Math.Max(0, targetHeight);
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return new FramePoolTarget(0, 0, false, "source_size_unavailable");
        }

        if (targetWidth <= 0 || targetHeight <= 0 ||
            (targetWidth == sourceWidth && targetHeight == sourceHeight))
        {
            return new FramePoolTarget(sourceWidth, sourceHeight, false, "(none)");
        }

        if (targetWidth > sourceWidth || targetHeight > sourceHeight)
        {
            return new FramePoolTarget(sourceWidth, sourceHeight, true, "target_not_smaller");
        }

        if (disabledAfterFailure)
        {
            return new FramePoolTarget(sourceWidth, sourceHeight, true, "disabled_after_failure");
        }

        return new FramePoolTarget(targetWidth, targetHeight, true, "(none)");
    }

    private static FramePoolTarget ResolveFramePoolFallbackTarget(
        SizeInt32 contentSize,
        bool gpuScaleRequested,
        string? fallbackReason)
        => new(
            Math.Max(0, contentSize.Width),
            Math.Max(0, contentSize.Height),
            gpuScaleRequested,
            gpuScaleRequested ? ResolveGpuScaleFallbackReason(fallbackReason) : "(none)");

    private static bool CanReuseExistingFullSizeFramePool(SizeInt32 contentSize, SizeInt32 currentPoolSize)
        => contentSize.Width > 0 &&
           contentSize.Height > 0 &&
           currentPoolSize.Width == contentSize.Width &&
           currentPoolSize.Height == contentSize.Height;

    private string ResolveGpuScaleOutputFallbackReason(FramePoolTarget target, int outputWidth, int outputHeight)
    {
        if (!target.GpuScaleRequested || outputWidth == 0 || outputHeight == 0)
        {
            return "(none)";
        }

        if (target.UsesGpuScaleReadback && outputWidth == target.Width && outputHeight == target.Height)
        {
            return "(none)";
        }

        if (!string.Equals(target.FallbackReason, "(none)", StringComparison.Ordinal))
        {
            return ResolveGpuScaleFallbackReason(target.FallbackReason);
        }

        var recordedReason = Volatile.Read(ref gpuScaleFallbackReason);
        if (!string.IsNullOrWhiteSpace(recordedReason))
        {
            return ResolveGpuScaleFallbackReason(recordedReason);
        }

        return "output_mismatch";
    }

    private void RecordGpuScaleFallback(string reason)
    {
        Interlocked.Exchange(ref gpuScaleReadbackDisabled, 1);
        Volatile.Write(ref gpuScaleFallbackReason, ResolveGpuScaleFallbackReason(reason));
    }

    private static string ResolveGpuScaleFallbackReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || string.Equals(reason, "(none)", StringComparison.Ordinal))
        {
            return "(none)";
        }

        return reason.Trim() switch
        {
            "source_size_unavailable" => "source_size_unavailable",
            "target_not_smaller" => "target_not_smaller",
            "disabled_after_failure" => "disabled_after_failure",
            "framepool_create_failed" => "framepool_create_failed",
            "framepool_recreate_failed" => "framepool_recreate_failed",
            "video_processor_unsupported" => "video_processor_unsupported",
            "video_processor_failed" => "video_processor_failed",
            "scale_readback_texture_create_failed" => "scale_readback_texture_create_failed",
            "output_mismatch" => "output_mismatch",
            _ => "scale_pipeline_failed",
        };
    }

    private void LogGpuScaleFramePoolEnabled(string operation, SizeInt32 contentSize, FramePoolTarget target)
    {
        LogLifecycle(
            "screenshare_wgc_gpu_scale_framepool_enabled",
            $"target={CaptureTarget.Describe()}; operation={operation}; source_width={contentSize.Width}; source_height={contentSize.Height}; framepool_width={target.Width}; framepool_height={target.Height}");
    }

    private void LogGpuScaleFramePoolFallback(
        string operation,
        SizeInt32 contentSize,
        FramePoolTarget target,
        string reason,
        Exception ex)
    {
        LogLifecycle(
            "screenshare_wgc_gpu_scale_fallback",
            $"target={CaptureTarget.Describe()}; operation={operation}; source_width={contentSize.Width}; source_height={contentSize.Height}; desired_width={target.Width}; desired_height={target.Height}; reason={ResolveGpuScaleFallbackReason(reason)}; nonfatal=1; hresult=0x{ex.HResult:X8}; exception={ex.GetType().Name}");
    }

    private void LogGpuScaleFallbackPoolReused(
        string operation,
        SizeInt32 contentSize,
        SizeInt32 currentPoolSize,
        FramePoolTarget requestedTarget,
        FramePoolTarget fallbackTarget)
    {
        LogLifecycle(
            "screenshare_wgc_framepool_recreate_skipped",
            $"target={CaptureTarget.Describe()}; operation={operation}; fallback_pool_reused=1; source_width={contentSize.Width}; source_height={contentSize.Height}; current_framepool_width={currentPoolSize.Width}; current_framepool_height={currentPoolSize.Height}; requested_width={requestedTarget.Width}; requested_height={requestedTarget.Height}; framepool_width={fallbackTarget.Width}; framepool_height={fallbackTarget.Height}; gpu_scale_requested={(fallbackTarget.GpuScaleRequested ? 1 : 0)}; gpu_scale_enabled=0; fallback_reason={fallbackTarget.FallbackReason}");
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
        long oldSessionLeaseId = 0;
        GraphicsCaptureItem? nextItem = null;
        Direct3D11CaptureFramePool? nextFramePool = null;
        GraphicsCaptureSession? nextSession = null;
        long nextSessionLeaseId = 0;
        WgcOwnerDispatcher? dispatcher = null;

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
                oldSessionLeaseId = activeSessionLeaseId;
                activeSessionLeaseId = 0;
                currentDevice = direct3DDevice;
                dispatcher = ownerDispatcher;
                restartToken = captureCts?.Token ?? CancellationToken.None;
            }

            await InvokeOnOwnerThreadAsync(
                dispatcher,
                () =>
                {
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
                        CloseGraphicsCaptureSession(
                            oldSession,
                            oldSessionLeaseId,
                            "restart_old_session",
                            CaptureTarget.Describe(),
                            SourceRole,
                            Interlocked.Read(ref lifecycleGeneration),
                            removeLeaseAfterAttempt: false,
                            expectedOwnerThreadId: dispatcher?.OwnerThreadId ?? 0);
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
                    nextFramePool = CreateFullSizeFramePool(currentDevice!, nextSize);
                    nextSession = nextFramePool.CreateCaptureSession(nextItem);
                    nextSessionLeaseId = RegisterSessionLease(nextSession, Interlocked.Read(ref lifecycleGeneration), "restart_create_session");
                    nextFramePool.FrameArrived += OnFrameArrived;
                    nextItem.Closed += OnCaptureItemClosed;
                    TryApplyCursorCapturePreference(nextSession, desiredCursorCaptureEnabled, "restart");
                    TryApplyBorderRequiredPreference(nextSession, required: true, "restart");
                    nextSession.StartCapture();
                }).ConfigureAwait(false);

            var nextSize = nextItem?.Size ?? default;

            lock (sync)
            {
                if (!started || disposed)
                {
                    return;
                }

                captureItem = nextItem;
                framePool = nextFramePool;
                captureSession = nextSession;
                activeSessionLeaseId = nextSessionLeaseId;
                currentContentSize = nextSize;
                currentFramePoolSize = nextSize;
                appliedOutputSizeHintRevision = Volatile.Read(ref outputSizeHintRevision);
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
            await InvokeOnOwnerThreadAsync(
                dispatcher,
                () =>
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
                        CloseGraphicsCaptureSession(
                            nextSession,
                            nextSessionLeaseId,
                            "restart_cleanup",
                            CaptureTarget.Describe(),
                            SourceRole,
                            Interlocked.Read(ref lifecycleGeneration),
                            removeLeaseAfterAttempt: true,
                            expectedOwnerThreadId: dispatcher?.OwnerThreadId ?? 0);
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
                }).ConfigureAwait(false);

            Interlocked.Exchange(ref restartInProgress, 0);
        }
    }

    private void RecreateFramePoolIfNeeded(SizeInt32 nextContentSize)
    {
        Direct3D11CaptureFramePool? currentFramePool;
        IDirect3DDevice? currentDevice;
        SizeInt32 currentPoolSize;

        lock (sync)
        {
            currentFramePool = framePool;
            currentDevice = direct3DDevice;
            currentPoolSize = currentFramePoolSize;
            var sizeChanged = currentContentSize.Width != nextContentSize.Width ||
                currentContentSize.Height != nextContentSize.Height;
            var poolSizeChanged = currentFramePoolSize.Width != nextContentSize.Width ||
                currentFramePoolSize.Height != nextContentSize.Height;
            if (!started || (!sizeChanged && !poolSizeChanged))
            {
                return;
            }
        }

        if (currentFramePool is null || currentDevice is null)
        {
            return;
        }

        var appliedTarget = RecreateFullSizeFramePool(
            currentFramePool,
            currentDevice,
            nextContentSize,
            currentPoolSize);

        lock (sync)
        {
            if (!started)
            {
                return;
            }

            currentContentSize = nextContentSize;
            currentFramePoolSize = appliedTarget.ToSize();
        }

        LogLifecycle(
            "screenshare_wgc_framepool_recreated",
            $"target={CaptureTarget.Describe()}; content_width={nextContentSize.Width}; content_height={nextContentSize.Height}; framepool_width={appliedTarget.Width}; framepool_height={appliedTarget.Height}; gpu_scale_requested=0; gpu_scale_enabled=0; fallback_reason={appliedTarget.FallbackReason}");
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

    private bool TryGpuScaleToReadbackTexture(
        IntPtr sourceTexture,
        D3D11Texture2DDesc sourceDesc,
        out IntPtr readbackTexture,
        out D3D11Texture2DDesc readbackDesc)
    {
        readbackTexture = IntPtr.Zero;
        readbackDesc = default;

        var target = ResolveRequestedFramePoolTarget(new SizeInt32
        {
            Width = checked((int)sourceDesc.Width),
            Height = checked((int)sourceDesc.Height),
        });
        if (!target.UsesGpuScaleReadback)
        {
            return false;
        }

        try
        {
            EnsureGpuScaleResources(sourceDesc, target);
            InvokeD3D11CopyResource(nativeImmediateContext, gpuScaleInputTexture, sourceTexture);
            InvokeVideoProcessorScale(sourceDesc, target);
            InvokeD3D11CopyResource(nativeImmediateContext, gpuScaleStagingTexture, gpuScaleOutputTexture);
            readbackTexture = gpuScaleStagingTexture;
            readbackDesc = gpuScaleStagingTextureDesc;
            return true;
        }
        catch (Exception ex)
        {
            RecordGpuScaleFallback(ResolveGpuScaleFailureReason(ex));
            ReleaseGpuScaleResources();
            LogGpuScaleReadbackFallback(sourceDesc, target, ResolveGpuScaleFailureReason(ex), ex);
            return false;
        }
    }

    private void EnsureGpuScaleResources(D3D11Texture2DDesc sourceDesc, FramePoolTarget target)
    {
        if (gpuScaleInputTexture != IntPtr.Zero &&
            gpuScaleInputTextureDesc.Width == sourceDesc.Width &&
            gpuScaleInputTextureDesc.Height == sourceDesc.Height &&
            gpuScaleInputTextureDesc.Format == sourceDesc.Format &&
            gpuScaleOutputTextureDesc.Width == (uint)target.Width &&
            gpuScaleOutputTextureDesc.Height == (uint)target.Height &&
            gpuScaleStagingTextureDesc.Width == (uint)target.Width &&
            gpuScaleStagingTextureDesc.Height == (uint)target.Height &&
            gpuScaleEnumerator != IntPtr.Zero &&
            gpuScaleProcessor != IntPtr.Zero &&
            gpuScaleInputView != IntPtr.Zero &&
            gpuScaleOutputView != IntPtr.Zero)
        {
            return;
        }

        ReleaseGpuScaleResources();
        QueryGpuScaleVideoInterfaces();
        CreateGpuScaleTextures(sourceDesc, target);
        CreateGpuScaleVideoProcessor(sourceDesc, target);
        LogLifecycle(
            "screenshare_wgc_gpu_scale_readback_enabled",
            $"target={CaptureTarget.Describe()}; source_width={sourceDesc.Width}; source_height={sourceDesc.Height}; scaled_width={target.Width}; scaled_height={target.Height}; path=d3d11_video_processor");
    }

    private void QueryGpuScaleVideoInterfaces()
    {
        if (gpuScaleVideoDevice == IntPtr.Zero)
        {
            var videoDeviceGuid = IID_ID3D11VideoDevice;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(nativeD3DDevice, ref videoDeviceGuid, out gpuScaleVideoDevice));
        }

        if (gpuScaleVideoContext == IntPtr.Zero)
        {
            var videoContextGuid = IID_ID3D11VideoContext;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(nativeImmediateContext, ref videoContextGuid, out gpuScaleVideoContext));
        }
    }

    private void CreateGpuScaleTextures(D3D11Texture2DDesc sourceDesc, FramePoolTarget target)
    {
        var inputDesc = sourceDesc;
        inputDesc.Usage = D3D11UsageDefault;
        inputDesc.BindFlags = D3D11BindRenderTarget | D3D11BindShaderResource;
        inputDesc.CPUAccessFlags = 0;
        inputDesc.MiscFlags = 0;
        inputDesc.ArraySize = 1;
        inputDesc.MipLevels = 1;
        inputDesc.SampleDesc = new DXGISampleDesc { Count = 1, Quality = 0 };
        Marshal.ThrowExceptionForHR(InvokeD3D11CreateTexture2D(nativeD3DDevice, ref inputDesc, IntPtr.Zero, out gpuScaleInputTexture));
        gpuScaleInputTextureDesc = inputDesc;

        var outputDesc = inputDesc;
        outputDesc.Width = checked((uint)target.Width);
        outputDesc.Height = checked((uint)target.Height);
        outputDesc.BindFlags = D3D11BindRenderTarget;
        Marshal.ThrowExceptionForHR(InvokeD3D11CreateTexture2D(nativeD3DDevice, ref outputDesc, IntPtr.Zero, out gpuScaleOutputTexture));
        gpuScaleOutputTextureDesc = outputDesc;

        var stagingDesc = outputDesc;
        stagingDesc.BindFlags = 0;
        stagingDesc.Usage = D3D11UsageStaging;
        stagingDesc.CPUAccessFlags = D3D11CpuAccessRead;
        Marshal.ThrowExceptionForHR(InvokeD3D11CreateTexture2D(nativeD3DDevice, ref stagingDesc, IntPtr.Zero, out gpuScaleStagingTexture));
        gpuScaleStagingTextureDesc = stagingDesc;
    }

    private void CreateGpuScaleVideoProcessor(D3D11Texture2DDesc sourceDesc, FramePoolTarget target)
    {
        var contentDesc = new D3D11VideoProcessorContentDesc
        {
            InputFrameFormat = D3D11VideoFrameFormatProgressive,
            InputFrameRate = new DXGIRational { Numerator = 8, Denominator = 1 },
            InputWidth = sourceDesc.Width,
            InputHeight = sourceDesc.Height,
            OutputFrameRate = new DXGIRational { Numerator = 8, Denominator = 1 },
            OutputWidth = checked((uint)target.Width),
            OutputHeight = checked((uint)target.Height),
            Usage = D3D11VideoUsageOptimalQuality,
        };

        Marshal.ThrowExceptionForHR(InvokeCreateVideoProcessorEnumerator(gpuScaleVideoDevice, ref contentDesc, out gpuScaleEnumerator));
        Marshal.ThrowExceptionForHR(InvokeCreateVideoProcessor(gpuScaleVideoDevice, gpuScaleEnumerator, 0, out gpuScaleProcessor));

        var inputViewDesc = new D3D11VideoProcessorInputViewDesc
        {
            FourCC = 0,
            ViewDimension = D3D11VpivDimensionTexture2D,
            MipSlice = 0,
            ArraySlice = 0,
        };
        Marshal.ThrowExceptionForHR(InvokeCreateVideoProcessorInputView(gpuScaleVideoDevice, gpuScaleInputTexture, gpuScaleEnumerator, ref inputViewDesc, out gpuScaleInputView));

        var outputViewDesc = new D3D11VideoProcessorOutputViewDesc
        {
            ViewDimension = D3D11VpovDimensionTexture2D,
            MipSlice = 0,
            FirstArraySlice = 0,
            ArraySize = 0,
        };
        Marshal.ThrowExceptionForHR(InvokeCreateVideoProcessorOutputView(gpuScaleVideoDevice, gpuScaleOutputTexture, gpuScaleEnumerator, ref outputViewDesc, out gpuScaleOutputView));
    }

    private void InvokeVideoProcessorScale(D3D11Texture2DDesc sourceDesc, FramePoolTarget target)
    {
        var sourceRect = new RectStruct
        {
            Left = 0,
            Top = 0,
            Right = checked((int)sourceDesc.Width),
            Bottom = checked((int)sourceDesc.Height),
        };
        var outputRect = new RectStruct
        {
            Left = 0,
            Top = 0,
            Right = target.Width,
            Bottom = target.Height,
        };

        InvokeVideoProcessorSetOutputTargetRect(gpuScaleVideoContext, gpuScaleProcessor, 1, ref outputRect);
        InvokeVideoProcessorSetStreamFrameFormat(gpuScaleVideoContext, gpuScaleProcessor, 0, D3D11VideoFrameFormatProgressive);
        InvokeVideoProcessorSetStreamSourceRect(gpuScaleVideoContext, gpuScaleProcessor, 0, 1, ref sourceRect);
        InvokeVideoProcessorSetStreamDestRect(gpuScaleVideoContext, gpuScaleProcessor, 0, 1, ref outputRect);
        InvokeVideoProcessorSetStreamAutoProcessingMode(gpuScaleVideoContext, gpuScaleProcessor, 0, 0);

        var stream = new D3D11VideoProcessorStream
        {
            Enable = 1,
            OutputIndex = 0,
            InputFrameOrField = 0,
            PastFrames = 0,
            FutureFrames = 0,
            ppPastSurfaces = IntPtr.Zero,
            pInputSurface = gpuScaleInputView,
            ppFutureSurfaces = IntPtr.Zero,
            ppPastSurfacesRight = IntPtr.Zero,
            pInputSurfaceRight = IntPtr.Zero,
            ppFutureSurfacesRight = IntPtr.Zero,
        };
        Marshal.ThrowExceptionForHR(InvokeVideoProcessorBlt(gpuScaleVideoContext, gpuScaleProcessor, gpuScaleOutputView, 0, 1, ref stream));
    }

    private static string ResolveGpuScaleFailureReason(Exception ex)
    {
        return ex switch
        {
            COMException { HResult: unchecked((int)0x80004002) } => "video_processor_unsupported",
            COMException => "video_processor_failed",
            _ => "scale_pipeline_failed",
        };
    }

    private void LogGpuScaleReadbackFallback(
        D3D11Texture2DDesc sourceDesc,
        FramePoolTarget target,
        string reason,
        Exception ex)
    {
        if (Interlocked.Exchange(ref gpuScaleFallbackLogged, 1) != 0)
        {
            return;
        }

        LogLifecycle(
            "screenshare_wgc_gpu_scale_fallback",
            $"target={CaptureTarget.Describe()}; operation=scale_readback; source_width={sourceDesc.Width}; source_height={sourceDesc.Height}; desired_width={target.Width}; desired_height={target.Height}; reason={ResolveGpuScaleFallbackReason(reason)}; nonfatal=1; hresult=0x{ex.HResult:X8}; exception={ex.GetType().Name}");
    }

    private void EnsureStagingTextureForSourceDesc(D3D11Texture2DDesc sourceDesc)
    {
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

    private Bitmap ReadStagingTexture(IntPtr readbackTexture, D3D11Texture2DDesc desc)
    {
        if (readbackTexture == IntPtr.Zero)
        {
            throw new InvalidOperationException("Windows Graphics Capture staging texture is not available.");
        }

        var bitmap = new Bitmap((int)desc.Width, (int)desc.Height, PixelFormat.Format32bppPArgb);
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var bitmapData = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            Marshal.ThrowExceptionForHR(InvokeD3D11Map(nativeImmediateContext, readbackTexture, 0, D3D11MapRead, 0, out var mapped));
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
                InvokeD3D11Unmap(nativeImmediateContext, readbackTexture, 0);
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

    private Task<WgcSessionCloseResult> CloseSessionOnOwnerThreadAsync(
        WgcOwnerDispatcher? dispatcher,
        GraphicsCaptureSession? session,
        long leaseId,
        Direct3D11CaptureFramePool? pool,
        GraphicsCaptureItem? item,
        string reason,
        long generation,
        bool removeLeaseAfterAttempt)
    {
        var ownerThreadId = dispatcher?.OwnerThreadId ?? 0;
        var closeTask = InvokeSessionCloseOnOwnerThreadAsync(
            dispatcher,
            () =>
            {
                if (pool is not null)
                {
                    pool.FrameArrived -= OnFrameArrived;
                }

                if (item is not null)
                {
                    item.Closed -= OnCaptureItemClosed;
                }

                if (session is not null)
                {
                    TryApplyBorderRequiredPreference(session, required: false, $"stop:{reason}");
                }

                return CloseGraphicsCaptureSession(
                    session,
                    leaseId,
                    reason,
                    CaptureTarget.Describe(),
                    SourceRole,
                    generation,
                    removeLeaseAfterAttempt,
                    ownerThreadId);
            });

        return closeTask;
    }

    private static async Task<WgcSessionCloseResult> InvokeSessionCloseOnOwnerThreadAsync(
        WgcOwnerDispatcher? dispatcher,
        Func<WgcSessionCloseResult> action)
    {
        var task = dispatcher?.IsAcceptingWork == true
            ? dispatcher.InvokeAsync(action)
            : Task.FromResult(action());
        try
        {
            return await task.WaitAsync(OwnerThreadCloseTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Interlocked.Increment(ref ownerThreadCloseTimeoutCount);
            Interlocked.Increment(ref sessionCloseAnomalyCount);
            LogLifecycle(
                "screenshare_wgc_owner_thread_close_timeout",
                $"owner_thread_id={dispatcher?.OwnerThreadId ?? 0}; timeout_ms={(long)OwnerThreadCloseTimeout.TotalMilliseconds}");
            return new WgcSessionCloseResult(true, false, "owner_dispatcher", "owner_thread_timeout", "(none)", "(none)", 0, Environment.CurrentManagedThreadId, dispatcher?.OwnerThreadId ?? 0, false);
        }
    }

    private static async Task<bool> DisposeFramePoolOnOwnerThreadAsync(
        WgcOwnerDispatcher? dispatcher,
        Direct3D11CaptureFramePool? pool,
        GraphicsCaptureItem? item)
    {
        return await InvokeOnOwnerThreadAsync(
            dispatcher,
            () =>
            {
                return DisposeFramePool(pool);
            }).ConfigureAwait(false);
    }

    private static Task<T> InvokeOnOwnerThreadAsync<T>(WgcOwnerDispatcher? dispatcher, Func<T> action)
    {
        if (dispatcher?.IsAcceptingWork == true)
        {
            return dispatcher.InvokeAsync(action);
        }

        return Task.FromResult(action());
    }

    private long RegisterSessionLease(GraphicsCaptureSession session, long generation, string reason)
    {
        var leaseId = Interlocked.Increment(ref nextSessionLeaseId);
        WgcOwnerDispatcher? dispatcher;
        lock (sync)
        {
            dispatcher = ownerDispatcher;
        }

        var ownerThreadId = dispatcher?.OwnerThreadId ?? Environment.CurrentManagedThreadId;
        Volatile.Write(ref lastSessionOwnerThreadId, ownerThreadId);
        lock (sessionLeaseSync)
        {
            sessionLeases[leaseId] = new WgcSessionLease(
                leaseId,
                session,
                CaptureTarget.Describe(),
                SourceRole,
                generation,
                DateTimeOffset.UtcNow,
                dispatcher,
                ownerThreadId);
        }

        LogLifecycle(
            "screenshare_wgc_session_lease_registered",
            $"lease_id={leaseId}; target={CaptureTarget.Describe()}; source_role={Sanitize(SourceRole)}; lifecycle_generation={generation}; reason={Sanitize(reason)}; owner_thread_id={ownerThreadId}; active_session_lease_count={GetActiveSessionLeaseCount()}");
        return leaseId;
    }

    public static int ForceCloseAllScreenShareLeases(string reason)
    {
        List<WgcSessionLease> leases;
        lock (sessionLeaseSync)
        {
            leases = new List<WgcSessionLease>(sessionLeases.Values);
        }

        var forceCloseId = Interlocked.Increment(ref forceCloseAllCount);
        LogLifecycle(
            "screenshare_wgc_force_close_all_requested",
            $"reason={Sanitize(string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason)}; force_close_id={forceCloseId}; active_session_lease_count={leases.Count}");

        var closedCount = 0;
        foreach (var lease in leases)
        {
            var result = CloseSessionLease(
                lease,
                $"force_close:{reason}",
                removeLeaseAfterAttempt: true);
            if (result.Closed)
            {
                closedCount++;
            }
        }

        LogLifecycle(
            "screenshare_wgc_force_close_all_completed",
            $"reason={Sanitize(string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason)}; force_close_id={forceCloseId}; lease_count={leases.Count}; closed_count={closedCount}; active_session_lease_count={GetActiveSessionLeaseCount()}");
        return closedCount;
    }

    internal static WgcSessionLeaseDiagnostics GetSessionLeaseDiagnosticsForTesting()
        => new(
            GetActiveSessionLeaseCount(),
            Volatile.Read(ref lastSessionCloseStatus),
            Volatile.Read(ref lastSessionCloseMethod),
            Volatile.Read(ref lastSessionCloseHResult),
            Interlocked.Read(ref forceCloseAllCount),
            Interlocked.Read(ref sessionCloseAnomalyCount),
            Volatile.Read(ref lastSessionOwnerThreadId),
            Volatile.Read(ref lastSessionCloseThreadId),
            Volatile.Read(ref lastSessionCloseOnOwnerThread) != 0,
            Interlocked.Read(ref ownerThreadCloseTimeoutCount));

    private static long GetActiveSessionLeaseCount()
    {
        lock (sessionLeaseSync)
        {
            return sessionLeases.Count;
        }
    }

    private static WgcSessionCloseResult CloseSessionLease(
        WgcSessionLease lease,
        string reason,
        bool removeLeaseAfterAttempt)
    {
        var dispatcher = lease.OwnerDispatcher;
        if (dispatcher?.IsAcceptingWork == true)
        {
            try
            {
                var closeTask = dispatcher.InvokeAsync(() => CloseGraphicsCaptureSession(
                    lease.Session,
                    lease.LeaseId,
                    reason,
                    lease.TargetDescription,
                    lease.SourceRole,
                    lease.LifecycleGeneration,
                    removeLeaseAfterAttempt,
                    lease.OwnerThreadId));
                return closeTask.WaitAsync(OwnerThreadCloseTimeout).GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                Interlocked.Increment(ref ownerThreadCloseTimeoutCount);
                Interlocked.Increment(ref sessionCloseAnomalyCount);
                LogLifecycle(
                    "screenshare_wgc_owner_thread_close_timeout",
                    $"lease_id={lease.LeaseId}; target={lease.TargetDescription}; source_role={Sanitize(lease.SourceRole)}; reason={Sanitize(reason)}; owner_thread_id={lease.OwnerThreadId}; timeout_ms={(long)OwnerThreadCloseTimeout.TotalMilliseconds}");
                return new WgcSessionCloseResult(true, false, "owner_dispatcher", "owner_thread_timeout", "(none)", "(none)", 0, Environment.CurrentManagedThreadId, lease.OwnerThreadId, false);
            }
        }

        return CloseGraphicsCaptureSession(
            lease.Session,
            lease.LeaseId,
            reason,
            lease.TargetDescription,
            lease.SourceRole,
            lease.LifecycleGeneration,
            removeLeaseAfterAttempt,
            lease.OwnerThreadId);
    }

    private static WgcSessionCloseResult CloseGraphicsCaptureSession(
        GraphicsCaptureSession? session,
        long leaseId,
        string reason,
        string targetDescription,
        string sourceRole,
        long lifecycleGenerationValue,
        bool removeLeaseAfterAttempt,
        int expectedOwnerThreadId = 0)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var sanitizedReason = Sanitize(string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason);
        var sessionPresent = session is not null;
        var closeThreadId = Environment.CurrentManagedThreadId;
        var closeOnOwnerThread = expectedOwnerThreadId <= 0 || closeThreadId == expectedOwnerThreadId;
        LogLifecycle(
            "screenshare_wgc_session_close_requested",
            $"lease_id={leaseId}; target={targetDescription}; source_role={Sanitize(sourceRole)}; reason={sanitizedReason}; session_present={(sessionPresent ? 1 : 0)}; owner_thread_id={expectedOwnerThreadId}; close_thread_id={closeThreadId}; close_on_owner_thread={(closeOnOwnerThread ? 1 : 0)}; lifecycle_generation={lifecycleGenerationValue}");

        var method = "none";
        var status = sessionPresent ? "not_attempted" : "missing_session";
        var hresultText = "(none)";
        var disposeStatus = "(none)";
        var closed = false;

        if (session is not null)
        {
            var closeSucceeded = TryCloseGraphicsCaptureSessionViaIClosable(session, out var closeHResult, out var closeStatus);
            hresultText = closeHResult;
            method = closeStatus == "query_interface_failed" ? "dispose" : "iclosable_close";
            status = closeSucceeded ? "closed" : closeStatus;

            try
            {
                session.Dispose();
                disposeStatus = "disposed";
                closed = true;
            }
            catch (Exception ex)
            {
                disposeStatus = ex.GetType().Name;
                closed = closeSucceeded;
            }

            if (closeSucceeded)
            {
                closed = true;
                method = "iclosable_close+dispose";
                status = disposeStatus == "disposed" ? "closed" : "closed_dispose_fallback_failed";
            }
            else if (closed)
            {
                status = "disposed";
            }
        }

        if (leaseId > 0 && (closed || removeLeaseAfterAttempt))
        {
            UnregisterSessionLease(leaseId);
        }

        var elapsedMs = Math.Max(0, (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        Volatile.Write(ref lastSessionCloseStatus, status);
        Volatile.Write(ref lastSessionCloseMethod, method);
        Volatile.Write(ref lastSessionCloseHResult, hresultText);
        Volatile.Write(ref lastSessionOwnerThreadId, expectedOwnerThreadId);
        Volatile.Write(ref lastSessionCloseThreadId, closeThreadId);
        Volatile.Write(ref lastSessionCloseOnOwnerThread, closeOnOwnerThread ? 1 : 0);

        LogLifecycle(
            "screenshare_wgc_session_close_result",
            $"lease_id={leaseId}; target={targetDescription}; source_role={Sanitize(sourceRole)}; reason={sanitizedReason}; session_present={(sessionPresent ? 1 : 0)}; closed={(closed ? 1 : 0)}; method={method}; status={status}; hresult={hresultText}; dispose_status={disposeStatus}; elapsed_ms={elapsedMs}; owner_thread_id={expectedOwnerThreadId}; close_thread_id={closeThreadId}; close_on_owner_thread={(closeOnOwnerThread ? 1 : 0)}; lifecycle_generation={lifecycleGenerationValue}; active_session_lease_count={GetActiveSessionLeaseCount()}");

        var wrongThreadClose = string.Equals(hresultText, "0x8001010E", StringComparison.OrdinalIgnoreCase);
        if (!closed || !closeOnOwnerThread || wrongThreadClose)
        {
            Interlocked.Increment(ref sessionCloseAnomalyCount);
            LogLifecycle(
                "screenshare_wgc_session_close_anomaly",
                $"lease_id={leaseId}; target={targetDescription}; source_role={Sanitize(sourceRole)}; reason={sanitizedReason}; status={status}; hresult={hresultText}; dispose_status={disposeStatus}; session_present={(sessionPresent ? 1 : 0)}; owner_thread_id={expectedOwnerThreadId}; close_thread_id={closeThreadId}; close_on_owner_thread={(closeOnOwnerThread ? 1 : 0)}; wrong_thread_hresult={(wrongThreadClose ? 1 : 0)}; active_session_lease_count={GetActiveSessionLeaseCount()}");
        }

        return new WgcSessionCloseResult(sessionPresent, closed, method, status, hresultText, disposeStatus, elapsedMs, closeThreadId, expectedOwnerThreadId, closeOnOwnerThread);
    }

    private static void UnregisterSessionLease(long leaseId)
    {
        lock (sessionLeaseSync)
        {
            sessionLeases.Remove(leaseId);
        }
    }

    private static bool TryCloseGraphicsCaptureSessionViaIClosable(
        GraphicsCaptureSession session,
        out string hresultText,
        out string status)
    {
        hresultText = "(none)";
        status = "not_attempted";
        IntPtr closable = IntPtr.Zero;
        try
        {
            using var sessionReference = MarshalInspectable.CreateMarshaler(session, false);
            var sessionAbi = MarshalInspectable.GetAbi(sessionReference);
            if (sessionAbi == IntPtr.Zero)
            {
                status = "abi_unavailable";
                return false;
            }

            var closableGuid = IClosableGuid;
            var queryHr = Marshal.QueryInterface(sessionAbi, ref closableGuid, out closable);
            if (queryHr < 0 || closable == IntPtr.Zero)
            {
                hresultText = FormatHResult(queryHr);
                status = "query_interface_failed";
                return false;
            }

            var vtable = Marshal.ReadIntPtr(closable);
            var closePtr = Marshal.ReadIntPtr(vtable, IntPtr.Size * 6);
            var close = Marshal.GetDelegateForFunctionPointer<IClosableCloseDelegate>(closePtr);
            var closeHr = close(closable);
            hresultText = FormatHResult(closeHr);
            status = closeHr >= 0 ? "closed" : "close_failed";
            return closeHr >= 0;
        }
        catch (Exception ex)
        {
            status = ex.GetType().Name;
            return false;
        }
        finally
        {
            if (closable != IntPtr.Zero)
            {
                Marshal.Release(closable);
            }
        }
    }

    private static string FormatHResult(int hresult)
        => hresult == 0 ? "0x00000000" : $"0x{unchecked((uint)hresult):X8}";

    private static bool DisposeFramePool(Direct3D11CaptureFramePool? pool)
    {
        if (pool is null)
        {
            return false;
        }

        try
        {
            pool.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ReleaseGpuScaleResources()
    {
        ReleaseComPointer(ref gpuScaleOutputView);
        ReleaseComPointer(ref gpuScaleInputView);
        ReleaseComPointer(ref gpuScaleProcessor);
        ReleaseComPointer(ref gpuScaleEnumerator);
        ReleaseComPointer(ref gpuScaleStagingTexture);
        ReleaseComPointer(ref gpuScaleOutputTexture);
        ReleaseComPointer(ref gpuScaleInputTexture);
        ReleaseComPointer(ref gpuScaleVideoContext);
        ReleaseComPointer(ref gpuScaleVideoDevice);
        gpuScaleInputTextureDesc = default;
        gpuScaleOutputTextureDesc = default;
        gpuScaleStagingTextureDesc = default;
    }

    private static void ReleaseComPointer(ref IntPtr value)
    {
        var current = value;
        if (current == IntPtr.Zero)
        {
            return;
        }

        value = IntPtr.Zero;
        Marshal.Release(current);
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

    private static int InvokeCreateVideoProcessorEnumerator(
        IntPtr videoDevicePtr,
        ref D3D11VideoProcessorContentDesc contentDesc,
        out IntPtr enumerator)
    {
        return GetVtableDelegate<D3D11VideoDeviceCreateVideoProcessorEnumeratorDelegate>(videoDevicePtr, 10)(videoDevicePtr, ref contentDesc, out enumerator);
    }

    private static int InvokeCreateVideoProcessor(
        IntPtr videoDevicePtr,
        IntPtr enumerator,
        uint rateConversionIndex,
        out IntPtr processor)
    {
        return GetVtableDelegate<D3D11VideoDeviceCreateVideoProcessorDelegate>(videoDevicePtr, 4)(videoDevicePtr, enumerator, rateConversionIndex, out processor);
    }

    private static int InvokeCreateVideoProcessorInputView(
        IntPtr videoDevicePtr,
        IntPtr resource,
        IntPtr enumerator,
        ref D3D11VideoProcessorInputViewDesc desc,
        out IntPtr inputView)
    {
        return GetVtableDelegate<D3D11VideoDeviceCreateVideoProcessorInputViewDelegate>(videoDevicePtr, 8)(videoDevicePtr, resource, enumerator, ref desc, out inputView);
    }

    private static int InvokeCreateVideoProcessorOutputView(
        IntPtr videoDevicePtr,
        IntPtr resource,
        IntPtr enumerator,
        ref D3D11VideoProcessorOutputViewDesc desc,
        out IntPtr outputView)
    {
        return GetVtableDelegate<D3D11VideoDeviceCreateVideoProcessorOutputViewDelegate>(videoDevicePtr, 9)(videoDevicePtr, resource, enumerator, ref desc, out outputView);
    }

    private static void InvokeVideoProcessorSetOutputTargetRect(
        IntPtr videoContextPtr,
        IntPtr processor,
        int enable,
        ref RectStruct rect)
    {
        GetVtableDelegate<D3D11VideoContextSetOutputTargetRectDelegate>(videoContextPtr, 13)(videoContextPtr, processor, enable, ref rect);
    }

    private static void InvokeVideoProcessorSetStreamFrameFormat(
        IntPtr videoContextPtr,
        IntPtr processor,
        uint streamIndex,
        uint frameFormat)
    {
        GetVtableDelegate<D3D11VideoContextSetStreamFrameFormatDelegate>(videoContextPtr, 27)(videoContextPtr, processor, streamIndex, frameFormat);
    }

    private static void InvokeVideoProcessorSetStreamSourceRect(
        IntPtr videoContextPtr,
        IntPtr processor,
        uint streamIndex,
        int enable,
        ref RectStruct rect)
    {
        GetVtableDelegate<D3D11VideoContextSetStreamSourceRectDelegate>(videoContextPtr, 30)(videoContextPtr, processor, streamIndex, enable, ref rect);
    }

    private static void InvokeVideoProcessorSetStreamDestRect(
        IntPtr videoContextPtr,
        IntPtr processor,
        uint streamIndex,
        int enable,
        ref RectStruct rect)
    {
        GetVtableDelegate<D3D11VideoContextSetStreamDestRectDelegate>(videoContextPtr, 31)(videoContextPtr, processor, streamIndex, enable, ref rect);
    }

    private static void InvokeVideoProcessorSetStreamAutoProcessingMode(
        IntPtr videoContextPtr,
        IntPtr processor,
        uint streamIndex,
        int enable)
    {
        GetVtableDelegate<D3D11VideoContextSetStreamAutoProcessingModeDelegate>(videoContextPtr, 37)(videoContextPtr, processor, streamIndex, enable);
    }

    private static int InvokeVideoProcessorBlt(
        IntPtr videoContextPtr,
        IntPtr processor,
        IntPtr outputView,
        uint outputFrame,
        uint streamCount,
        ref D3D11VideoProcessorStream stream)
    {
        return GetVtableDelegate<D3D11VideoContextVideoProcessorBltDelegate>(videoContextPtr, 53)(videoContextPtr, processor, outputView, outputFrame, streamCount, ref stream);
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
    private const uint D3D11VideoFrameFormatProgressive = 0;
    private const uint D3D11VideoUsageOptimalQuality = 2;
    private const uint D3D11VpivDimensionTexture2D = 1;
    private const uint D3D11VpovDimensionTexture2D = 1;
    private static readonly Guid IID_IDXGIDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    private static readonly Guid IID_IInspectable = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");
    private static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    private static readonly Guid IID_ID3D11VideoDevice = new("10EC4D5B-975A-4689-B9E4-D0AAC30FE333");
    private static readonly Guid IID_ID3D11VideoContext = new("61F21C45-3C0E-4A74-9CEA-67100D9AD5E4");

    private enum D3DDriverType : uint
    {
        Hardware = 1,
        Warp = 5,
    }

    private enum D3DFeatureLevel : uint
    {
    }

    internal readonly record struct FramePoolTarget(
        int Width,
        int Height,
        bool GpuScaleRequested,
        string FallbackReason)
    {
        public bool UsesGpuScaleReadback =>
            GpuScaleRequested &&
            Width > 0 &&
            Height > 0 &&
            string.Equals(FallbackReason, "(none)", StringComparison.Ordinal);

        public bool UsesTargetSizedFramePool => false;

        public SizeInt32 ToSize() => new() { Width = Width, Height = Height };
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

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGIRational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11VideoProcessorContentDesc
    {
        public uint InputFrameFormat;
        public DXGIRational InputFrameRate;
        public uint InputWidth;
        public uint InputHeight;
        public DXGIRational OutputFrameRate;
        public uint OutputWidth;
        public uint OutputHeight;
        public uint Usage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11VideoProcessorInputViewDesc
    {
        public uint FourCC;
        public uint ViewDimension;
        public uint MipSlice;
        public uint ArraySlice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11VideoProcessorOutputViewDesc
    {
        public uint ViewDimension;
        public uint MipSlice;
        public uint FirstArraySlice;
        public uint ArraySize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectStruct
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11VideoProcessorStream
    {
        public int Enable;
        public uint OutputIndex;
        public uint InputFrameOrField;
        public uint PastFrames;
        public uint FutureFrames;
        public IntPtr ppPastSurfaces;
        public IntPtr pInputSurface;
        public IntPtr ppFutureSurfaces;
        public IntPtr ppPastSurfacesRight;
        public IntPtr pInputSurfaceRight;
        public IntPtr ppFutureSurfacesRight;
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

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int D3D11VideoDeviceCreateVideoProcessorEnumeratorDelegate(
        IntPtr @this,
        ref D3D11VideoProcessorContentDesc contentDesc,
        out IntPtr enumerator);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int D3D11VideoDeviceCreateVideoProcessorDelegate(
        IntPtr @this,
        IntPtr enumerator,
        uint rateConversionIndex,
        out IntPtr processor);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int D3D11VideoDeviceCreateVideoProcessorInputViewDelegate(
        IntPtr @this,
        IntPtr resource,
        IntPtr enumerator,
        ref D3D11VideoProcessorInputViewDesc desc,
        out IntPtr inputView);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int D3D11VideoDeviceCreateVideoProcessorOutputViewDelegate(
        IntPtr @this,
        IntPtr resource,
        IntPtr enumerator,
        ref D3D11VideoProcessorOutputViewDesc desc,
        out IntPtr outputView);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void D3D11VideoContextSetOutputTargetRectDelegate(
        IntPtr @this,
        IntPtr processor,
        int enable,
        ref RectStruct rect);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void D3D11VideoContextSetStreamFrameFormatDelegate(
        IntPtr @this,
        IntPtr processor,
        uint streamIndex,
        uint frameFormat);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void D3D11VideoContextSetStreamSourceRectDelegate(
        IntPtr @this,
        IntPtr processor,
        uint streamIndex,
        int enable,
        ref RectStruct rect);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void D3D11VideoContextSetStreamDestRectDelegate(
        IntPtr @this,
        IntPtr processor,
        uint streamIndex,
        int enable,
        ref RectStruct rect);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void D3D11VideoContextSetStreamAutoProcessingModeDelegate(
        IntPtr @this,
        IntPtr processor,
        uint streamIndex,
        int enable);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int D3D11VideoContextVideoProcessorBltDelegate(
        IntPtr @this,
        IntPtr processor,
        IntPtr outputView,
        uint outputFrame,
        uint streamCount,
        ref D3D11VideoProcessorStream streams);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int IClosableCloseDelegate(IntPtr @this);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GraphicsCaptureSession2GetCursorCaptureEnabledDelegate(
        IntPtr @this,
        out byte enabled);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GraphicsCaptureSession2PutCursorCaptureEnabledDelegate(
        IntPtr @this,
        byte enabled);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GraphicsCaptureSession3GetBorderRequiredDelegate(
        IntPtr @this,
        out byte required);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GraphicsCaptureSession3PutBorderRequiredDelegate(
        IntPtr @this,
        byte required);

    private sealed class WgcOwnerDispatcher : IDisposable
    {
        private readonly BlockingCollection<Action> workQueue = new();
        private readonly Thread thread;
        private int acceptingWork = 1;
        private int disposed;
        private int ownerThreadId;

        public WgcOwnerDispatcher(string targetDescription, string sourceRole)
        {
            thread = new Thread(Run)
            {
                IsBackground = true,
                Name = $"nLink WGC {sourceRole} {targetDescription}",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public int OwnerThreadId => Volatile.Read(ref ownerThreadId);

        public bool IsAcceptingWork => Volatile.Read(ref acceptingWork) != 0 && Volatile.Read(ref disposed) == 0;

        private bool IsOwnerThread => Environment.CurrentManagedThreadId == OwnerThreadId;

        public Task InvokeAsync(Action action)
        {
            if (IsOwnerThread)
            {
                try
                {
                    action();
                    return Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    return Task.FromException(ex);
                }
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Enqueue(() =>
            {
                try
                {
                    action();
                    completion.SetResult();
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            }, completion);
            return completion.Task;
        }

        public Task<T> InvokeAsync<T>(Func<T> action)
        {
            if (IsOwnerThread)
            {
                try
                {
                    return Task.FromResult(action());
                }
                catch (Exception ex)
                {
                    return Task.FromException<T>(ex);
                }
            }

            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Enqueue(() =>
            {
                try
                {
                    completion.SetResult(action());
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            }, completion);
            return completion.Task;
        }

        private void Enqueue(Action action, TaskCompletionSource completion)
        {
            if (!IsAcceptingWork)
            {
                completion.SetException(new ObjectDisposedException(nameof(WgcOwnerDispatcher)));
                return;
            }

            try
            {
                workQueue.Add(action);
            }
            catch (InvalidOperationException ex)
            {
                completion.SetException(ex);
            }
        }

        private void Enqueue<T>(Action action, TaskCompletionSource<T> completion)
        {
            if (!IsAcceptingWork)
            {
                completion.SetException(new ObjectDisposedException(nameof(WgcOwnerDispatcher)));
                return;
            }

            try
            {
                workQueue.Add(action);
            }
            catch (InvalidOperationException ex)
            {
                completion.SetException(ex);
            }
        }

        private void Run()
        {
            Volatile.Write(ref ownerThreadId, Environment.CurrentManagedThreadId);
            foreach (var workItem in workQueue.GetConsumingEnumerable())
            {
                workItem();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            Volatile.Write(ref acceptingWork, 0);
            workQueue.CompleteAdding();
            if (!IsOwnerThread)
            {
                thread.Join((int)OwnerThreadCloseTimeout.TotalMilliseconds);
            }

            workQueue.Dispose();
        }
    }

    internal readonly record struct WgcSessionLeaseDiagnostics(
        long ActiveSessionLeaseCount,
        string LastSessionCloseStatus,
        string LastSessionCloseMethod,
        string LastSessionCloseHResult,
        long ForceCloseCount,
        long SessionCloseAnomalyCount,
        int LastSessionOwnerThreadId,
        int LastSessionCloseThreadId,
        bool LastSessionCloseOnOwnerThread,
        long OwnerThreadCloseTimeoutCount);

    internal readonly record struct WgcOwnerDispatcherDiagnostics(
        int CallerThreadId,
        int OwnerThreadId,
        int WorkThreadId,
        bool WorkRanOnOwnerThread,
        bool OwnerThreadIsDedicated);

    private sealed record WgcSessionLease(
        long LeaseId,
        GraphicsCaptureSession Session,
        string TargetDescription,
        string SourceRole,
        long LifecycleGeneration,
        DateTimeOffset CreatedUtc,
        WgcOwnerDispatcher? OwnerDispatcher,
        int OwnerThreadId);

    private readonly record struct WgcSessionCloseResult(
        bool SessionPresent,
        bool Closed,
        string Method,
        string Status,
        string HResult,
        string DisposeStatus,
        long ElapsedMs,
        int CloseThreadId,
        int OwnerThreadId,
        bool CloseOnOwnerThread);

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
