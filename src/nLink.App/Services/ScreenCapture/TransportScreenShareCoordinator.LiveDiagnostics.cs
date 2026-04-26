namespace NLink.App.Services.ScreenCapture;

internal sealed partial class TransportScreenShareCoordinator
{
    internal ScreenShareLiveDiagnosticsSnapshot GetLiveDiagnosticsSnapshot()
    {
        var metrics = GetMetricsSnapshot();

        ScreenCaptureFreshnessMetrics sourceFreshnessMetrics;
        bool transportCaptureActive;
        string cursorDeliveryMode;
        lock (gate)
        {
            sourceFreshnessMetrics = GetCaptureFreshnessMetricsSnapshot(captureSource);
            transportCaptureActive = captureSource is not null && sendPipeline is not null;
            cursorDeliveryMode = string.IsNullOrWhiteSpace(cursorOverlayDeliveryMode)
                ? "captured_video"
                : cursorOverlayDeliveryMode;
        }

        return new ScreenShareLiveDiagnosticsSnapshot(
            TransportCaptureActive: transportCaptureActive,
            RemoteViewerActive: false,
            SenderMode: FormatValue(metrics.FreshnessMode, "unknown"),
            SenderOperatingState: FormatValue(metrics.SenderOperatingState, "unknown"),
            SenderGuardState: FormatValue(metrics.SenderGuardState, "unknown"),
            DominantPressureBlocker: FormatValue(metrics.DominantPressureBlocker, "unknown"),
            ActiveTargetWidth: sourceFreshnessMetrics.ActiveTargetWidth,
            ActiveTargetHeight: sourceFreshnessMetrics.ActiveTargetHeight,
            ActiveTargetFramesPerSecond: sourceFreshnessMetrics.ActiveTargetFramesPerSecond,
            ActiveTargetBitrate: sourceFreshnessMetrics.ActiveTargetBitrate,
            ActualEncodedDisplayableFps: sourceFreshnessMetrics.ActualEncodedDisplayableFps,
            RawSourceReadbackFps: sourceFreshnessMetrics.RawSourceReadbackFps,
            SenderProcessCpuPercent: sourceFreshnessMetrics.SenderProcessCpuPercent,
            LastPreprocessDurationMs: sourceFreshnessMetrics.LastPreprocessDurationMs,
            LastPreprocessResizeDurationMs: sourceFreshnessMetrics.LastPreprocessResizeDurationMs,
            LastPreprocessColorConvertDurationMs: sourceFreshnessMetrics.LastPreprocessColorConvertDurationMs,
            PreprocessResizePath: FormatValue(sourceFreshnessMetrics.PreprocessResizePath, "(none)"),
            RawSourceGpuScaleEnabled: sourceFreshnessMetrics.RawSourceGpuScaleEnabled,
            RawSourceGpuScaleFallbackReason: FormatValue(sourceFreshnessMetrics.RawSourceGpuScaleFallbackReason, "(none)"),
            RawSourceOutputWidth: sourceFreshnessMetrics.RawSourceOutputWidth,
            RawSourceOutputHeight: sourceFreshnessMetrics.RawSourceOutputHeight,
            RawSourceCaptureActive: sourceFreshnessMetrics.RawSourceCaptureActive,
            RawSourceBorderRequiredControlSupported: sourceFreshnessMetrics.RawSourceBorderRequiredControlSupported,
            RawSourceBorderRequiredDesired: sourceFreshnessMetrics.RawSourceBorderRequiredDesired,
            RawSourceBorderRequired: sourceFreshnessMetrics.RawSourceBorderRequired,
            RawSourceBorderRequiredApplyStatus: FormatValue(sourceFreshnessMetrics.RawSourceBorderRequiredApplyStatus, "(none)"),
            RawSourceBorderRequiredFallbackReason: FormatValue(sourceFreshnessMetrics.RawSourceBorderRequiredFallbackReason, "(none)"),
            RawSourceLastStopDurationMs: sourceFreshnessMetrics.RawSourceLastStopDurationMs,
            RawSourceLastStopReason: FormatValue(sourceFreshnessMetrics.RawSourceLastStopReason, "(none)"),
            RawSourceActiveSessionLeaseCount: sourceFreshnessMetrics.RawSourceActiveSessionLeaseCount,
            RawSourceLastSessionCloseStatus: FormatValue(sourceFreshnessMetrics.RawSourceLastSessionCloseStatus, "(none)"),
            RawSourceLastSessionCloseMethod: FormatValue(sourceFreshnessMetrics.RawSourceLastSessionCloseMethod, "(none)"),
            RawSourceLastSessionCloseHResult: FormatValue(sourceFreshnessMetrics.RawSourceLastSessionCloseHResult, "(none)"),
            RawSourceForceCloseCount: sourceFreshnessMetrics.RawSourceForceCloseCount,
            RawSourceSessionCloseAnomalyCount: sourceFreshnessMetrics.RawSourceSessionCloseAnomalyCount,
            RawSourceSessionOwnerThreadId: sourceFreshnessMetrics.RawSourceSessionOwnerThreadId,
            RawSourceLastSessionCloseThreadId: sourceFreshnessMetrics.RawSourceLastSessionCloseThreadId,
            RawSourceLastSessionCloseOnOwnerThread: sourceFreshnessMetrics.RawSourceLastSessionCloseOnOwnerThread,
            RawSourceOwnerDispatcherActive: sourceFreshnessMetrics.RawSourceOwnerDispatcherActive,
            RawSourceOwnerThreadCloseTimeoutCount: sourceFreshnessMetrics.RawSourceOwnerThreadCloseTimeoutCount,
            CursorDeliveryMode: cursorDeliveryMode,
            CursorCaptureDesiredEnabled: sourceFreshnessMetrics.CursorCaptureDesiredEnabled,
            CursorCaptureEnabled: sourceFreshnessMetrics.CursorCaptureEnabled,
            CursorCaptureControlSupported: sourceFreshnessMetrics.CursorCaptureControlSupported,
            CursorCaptureApplyStatus: FormatValue(sourceFreshnessMetrics.CursorCaptureApplyStatus, "(none)"),
            CursorCaptureFallbackReason: FormatValue(sourceFreshnessMetrics.CursorCaptureFallbackReason, "(none)"),
            PreCandidateGapTailEmittedToViewerCount: metrics.PreCandidateGapTailEmittedToViewerCount,
            ActionableLateFragmentCount: metrics.ActionableLateFragmentCount,
            H264ReferenceTaintActive: metrics.H264ReferenceTaintActive,
            H264ReferenceQuarantineActive: metrics.H264ReferenceQuarantineActive,
            H264ReferenceTaintEnterCount: metrics.H264ReferenceTaintEnterCount,
            H264ReferenceTaintReleaseCount: metrics.H264ReferenceTaintReleaseCount,
            H264ReferenceTaintLastReason: FormatValue(metrics.H264ReferenceTaintLastReason, "(none)"),
            H264ReferenceQuarantineLastBlocker: FormatValue(metrics.H264ReferenceQuarantineLastBlocker, "(none)"));
    }

    private static string FormatValue(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
