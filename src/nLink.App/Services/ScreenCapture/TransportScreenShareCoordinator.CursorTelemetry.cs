using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using NLink.Core.Logging;
using NLink.Core.ScreenShare;

namespace NLink.App.Services.ScreenCapture;

internal sealed partial class TransportScreenShareCoordinator
{
    private static readonly TimeSpan CursorTelemetrySampleInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan CursorTelemetrySendInterval = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan CursorTelemetryKeepAliveInterval = TimeSpan.FromMilliseconds(500);
    private const double CursorTelemetryCoordinateEpsilon = 0.001d;

    private void StartCursorTelemetryTimer_NoLock()
    {
        if (cursorTelemetryTimer is not null || sendCursorStateAsync is null)
        {
            return;
        }

        cursorTelemetryTimer = new Timer(
            static state => ((TransportScreenShareCoordinator)state!).OnCursorTelemetryTimerTick(),
            this,
            CursorTelemetrySampleInterval,
            CursorTelemetrySampleInterval);
    }

    private void StopCursorTelemetryTimer_NoLock()
    {
        cursorTelemetryTimer?.Dispose();
        cursorTelemetryTimer = null;
        cursorTelemetryTickInFlight = 0;
    }

    private void OnCursorTelemetryTimerTick()
    {
        if (Interlocked.Exchange(ref cursorTelemetryTickInFlight, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await PublishCursorTelemetryTickAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LocalOperationalLog.Info(
                    "ScreenShareTransport",
                    $"event=screenshare_cursor_overlay_tick_failed; ex={ex.GetType().Name}");
            }
            finally
            {
                Interlocked.Exchange(ref cursorTelemetryTickInFlight, 0);
            }
        });
    }

    private async Task PublishCursorTelemetryTickAsync()
    {
        if (sendCursorStateAsync is null)
        {
            return;
        }

        var overlayAllowed = cursorOverlayEnabledResolver?.Invoke() == true;
        CursorTelemetryContext context;
        lock (gate)
        {
            if (disposed || captureSource is null || sendPipeline is null || !overlayAllowed)
            {
                UpdateCursorDeliveryMode_NoLock("captured_video", "overlay_not_available");
                return;
            }

            if (lastSentDisplayInfo is not { } displayInfo ||
                lastSentDisplayInfoRevision <= 0)
            {
                UpdateCursorDeliveryMode_NoLock("captured_video", "display_info_unavailable");
                return;
            }

            context = new CursorTelemetryContext(
                sessionId,
                displayInfo.DisplayId,
                lastSentDisplayInfoRevision,
                displayInfo.CaptureRegionX,
                displayInfo.CaptureRegionY,
                displayInfo.CaptureRegionWidth,
                displayInfo.CaptureRegionHeight,
                captureSource);
        }

        if (context.CaptureRegionWidth <= 0 || context.CaptureRegionHeight <= 0)
        {
            return;
        }

        if (!cursorPositionSource.TryGetCursorPosition(out var cursor))
        {
            Interlocked.Increment(ref cursorOverlayMappingFailureCount);
            UpdateCursorDeliveryMode("fallback_captured", "cursor_position_unavailable");
            return;
        }

        var inside =
            cursor.X >= context.CaptureRegionX &&
            cursor.Y >= context.CaptureRegionY &&
            cursor.X < context.CaptureRegionX + context.CaptureRegionWidth &&
            cursor.Y < context.CaptureRegionY + context.CaptureRegionHeight;
        var nx = Math.Clamp((cursor.X - context.CaptureRegionX) / (double)context.CaptureRegionWidth, 0d, 1d);
        var ny = Math.Clamp((cursor.Y - context.CaptureRegionY) / (double)context.CaptureRegionHeight, 0d, 1d);
        var visible = cursor.Visible && inside;

        var now = clock.UtcNow;
        var nowMs = now.ToUnixTimeMilliseconds();
        var sourceMetrics = TryGetCursorCaptureMetrics(context.CaptureSource);
        var capturedCursorEnabled = sourceMetrics?.CursorCaptureEnabled ?? true;
        var cursorControlSupported = sourceMetrics?.CursorCaptureControlSupported ?? false;
        var cursorStatus = capturedCursorEnabled
            ? "captured_cursor_enabled"
            : "captured_cursor_disabled";
        var deliveryMode = !capturedCursorEnabled && cursorControlSupported
            ? "helper_overlay"
            : "fallback_captured";
        UpdateCursorDeliveryMode(deliveryMode, cursorStatus);

        var shouldSend = ShouldSendCursorState(now, nx, ny, visible, capturedCursorEnabled, context.DisplayInfoRevision);
        if (!shouldSend)
        {
            return;
        }

        var next = new ScreenShareCursorStateV1
        {
            SessionId = context.SessionId,
            Seq = Interlocked.Increment(ref cursorOverlayStateSeq),
            TsUtcMs = nowMs,
            DisplayId = context.DisplayId,
            DisplayInfoRevision = context.DisplayInfoRevision,
            Nx = nx,
            Ny = ny,
            Visible = visible,
            Source = "os_cursor",
            Status = cursorStatus,
            CapturedCursorEnabled = capturedCursorEnabled,
            CursorCaptureControlSupported = cursorControlSupported,
        };

        lastCursorStateSent = next;
        lastCursorStateSentUtc = now;
        try
        {
            await sendCursorStateAsync(context.SessionId, next, CancellationToken.None).ConfigureAwait(false);
            Interlocked.Increment(ref cursorOverlayUpdatesSentCount);
            LogCursorOverlaySnapshot("sent", next, deliveryMode);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref cursorOverlaySendFailureCount);
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_cursor_overlay_send_failed; session_id={context.SessionId}; seq={next.Seq}; ex={ex.GetType().Name}");
        }
    }

    private bool ShouldSendCursorState(
        DateTimeOffset now,
        double nx,
        double ny,
        bool visible,
        bool capturedCursorEnabled,
        long displayInfoRevision)
    {
        if (lastCursorStateSent is not { } previous)
        {
            return true;
        }

        if (now - lastCursorStateSentUtc >= CursorTelemetryKeepAliveInterval)
        {
            return true;
        }

        if (now - lastCursorStateSentUtc < CursorTelemetrySendInterval)
        {
            return false;
        }

        return previous.Visible != visible ||
               previous.CapturedCursorEnabled != capturedCursorEnabled ||
               previous.DisplayInfoRevision != displayInfoRevision ||
               Math.Abs(previous.Nx - nx) >= CursorTelemetryCoordinateEpsilon ||
               Math.Abs(previous.Ny - ny) >= CursorTelemetryCoordinateEpsilon;
    }

    private ScreenCaptureFreshnessMetrics? TryGetCursorCaptureMetrics(IScreenCaptureSource source)
        => source is IScreenCaptureFreshnessMetricsSource freshnessSource
            ? freshnessSource.GetFreshnessMetricsSnapshot()
            : null;

    private void UpdateCursorDeliveryMode(string mode, string reason)
    {
        lock (gate)
        {
            UpdateCursorDeliveryMode_NoLock(mode, reason);
        }
    }

    private void UpdateCursorDeliveryMode_NoLock(string mode, string reason)
    {
        var normalizedMode = string.IsNullOrWhiteSpace(mode) ? "captured_video" : mode.Trim();
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason.Trim();
        cursorOverlayDeliveryMode = normalizedMode;
        cursorOverlayLastStatus = normalizedReason;
    }

    private void LogCursorOverlaySnapshot(string stage, ScreenShareCursorStateV1 state, string deliveryMode)
    {
        if (Interlocked.Read(ref cursorOverlayUpdatesSentCount) % 30 != 1)
        {
            return;
        }

        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_cursor_overlay_{stage}; session_id={state.SessionId}; seq={state.Seq}; display_id={state.DisplayId}; display_rev={state.DisplayInfoRevision}; nx={state.Nx.ToString("0.###", CultureInfo.InvariantCulture)}; ny={state.Ny.ToString("0.###", CultureInfo.InvariantCulture)}; visible={(state.Visible ? 1 : 0)}; cursor_delivery_mode={deliveryMode}; cursor_capture_enabled={(state.CapturedCursorEnabled ? 1 : 0)}; cursor_capture_control_supported={(state.CursorCaptureControlSupported ? 1 : 0)}; status={state.Status}");
    }

    private readonly record struct CursorTelemetryContext(
        string SessionId,
        string DisplayId,
        long DisplayInfoRevision,
        int CaptureRegionX,
        int CaptureRegionY,
        int CaptureRegionWidth,
        int CaptureRegionHeight,
        IScreenCaptureSource CaptureSource);
}
