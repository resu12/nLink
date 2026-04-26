using System;
using System.Threading;
using System.Threading.Tasks;

namespace NLink.App.Services.ScreenCapture;

internal interface IWindowsRawCaptureSource : IAsyncDisposable
{
    bool IsSupported { get; }

    event EventHandler<WindowsRawCaptureFrameEventArgs>? FrameArrived;
    event EventHandler<WindowsRawCaptureFailureEventArgs>? CaptureFailed;

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync();

    bool TryGetCaptureMetadata(out ScreenCaptureMetadata metadata);
}

internal interface IWindowsRawCaptureCadenceControl
{
    void SetRawCaptureCadence(int targetFramesPerSecond, string reason);

    void ForceNextRawCapture(string reason);

    WindowsRawCaptureRuntimeMetrics GetRawCaptureRuntimeMetricsSnapshot();
}

internal interface IWindowsRawCaptureOutputControl
{
    void SetRawCaptureOutputSizeHint(int targetWidth, int targetHeight, string reason);
}

internal readonly record struct WindowsRawCaptureRuntimeMetrics(
    long FrameArrivedCount = 0,
    long FramesSkippedBeforeReadback = 0,
    long FramesReadbackCount = 0,
    double ReadbackFps = 0,
    long LastReadbackDurationMs = -1,
    double AverageReadbackDurationMs = -1,
    int CadenceTargetFps = 0,
    long UrgentBypassCount = 0,
    int OutputWidth = 0,
    int OutputHeight = 0,
    bool GpuScaleEnabled = false,
    string GpuScaleFallbackReason = "",
    bool CaptureActive = false,
    bool BorderRequiredControlSupported = false,
    bool BorderRequiredDesired = true,
    bool BorderRequired = true,
    string BorderRequiredApplyStatus = "",
    string BorderRequiredFallbackReason = "",
    long LastStopDurationMs = -1,
    string LastStopReason = "",
    long ActiveSessionLeaseCount = 0,
    string LastSessionCloseStatus = "",
    string LastSessionCloseMethod = "",
    string LastSessionCloseHResult = "",
    long ForceCloseCount = 0,
    long SessionCloseAnomalyCount = 0,
    int SessionOwnerThreadId = 0,
    int LastSessionCloseThreadId = 0,
    bool LastSessionCloseOnOwnerThread = false,
    bool OwnerDispatcherActive = false,
    long OwnerThreadCloseTimeoutCount = 0);
