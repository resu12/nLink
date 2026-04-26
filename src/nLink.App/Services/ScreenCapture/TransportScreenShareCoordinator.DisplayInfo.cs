using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NLink.App.Services;
using NLink.Core.Logging;
using NLink.Core.RemoteControl;

namespace NLink.App.Services.ScreenCapture;

internal sealed partial class TransportScreenShareCoordinator
{
    private void TryPublishDisplayInfo(IScreenCaptureSource currentCaptureSource, int frameWidth, int frameHeight)
    {
        if (sendDisplayInfoAsync is null)
        {
            return;
        }

        if (!displayInfoProvider.TryGetSnapshot(currentCaptureSource, frameWidth, frameHeight, out var snapshot, out var reason))
        {
            lock (gate)
            {
                if (!string.Equals(lastDisplayInfoIssue, reason, StringComparison.Ordinal))
                {
                    lastDisplayInfoIssue = reason;
                    LogDebug($"Display info skipped ({reason}).");
                }
            }

            return;
        }

        ControlDisplayInfoMessageV1 message;
        string publishSessionId;
        long publishLifecycleGeneration;
        long revision;
        ScreenShareDisplayInfoSnapshot sentSnapshot;
        DisplayInfoMappingKey sentMapping;
        var flushedQueuedFrames = 0;
        lock (gate)
        {
            if (lastSentDisplayInfo.HasValue &&
                lastSentDisplayInfo.Value.Equals(snapshot))
            {
                return;
            }

            var mapping = CreateMappingKey(snapshot);
            if (lastSentDisplayInfoMapping.HasValue &&
                lastSentDisplayInfoMapping.Value.Equals(mapping))
            {
                // Frame-level changes (e.g. adaptive encoder size) are not mapping changes.
                // Keep latest diagnostics fields but do not bump revision/send updates.
                lastSentDisplayInfo = snapshot;
                ClearPendingDisplayInfoUnsafe();
                lastDisplayInfoIssue = string.Empty;
                return;
            }

            var now = clock.UtcNow;
            if (lastSentDisplayInfoMapping.HasValue)
            {
                if (!pendingDisplayInfoMapping.HasValue ||
                    !pendingDisplayInfoMapping.Value.Equals(mapping))
                {
                    pendingDisplayInfoMapping = mapping;
                    pendingDisplayInfo = snapshot;
                    pendingDisplayInfoNotBeforeUtc = now + DisplayInfoMappingChangeDebounce;
                    lastDisplayInfoIssue = string.Empty;
                    return;
                }

                pendingDisplayInfo = snapshot;
                if (now < pendingDisplayInfoNotBeforeUtc)
                {
                    return;
                }

                snapshot = pendingDisplayInfo.Value;
                mapping = pendingDisplayInfoMapping.Value;
                if (sendPipeline is not null)
                {
                    flushedQueuedFrames = sendPipeline.FlushPendingFrames();
                }

                ClearPendingDisplayInfoUnsafe();
            }

            if (string.IsNullOrWhiteSpace(sessionId) ||
                captureSource is null ||
                sendPipeline is null)
            {
                return;
            }

            revision = checked(lastSentDisplayInfoRevision + 1);
            lastSentDisplayInfoRevision = revision;
            lastSentDisplayInfo = snapshot;
            lastSentDisplayInfoMapping = mapping;
            lastDisplayInfoIssue = string.Empty;
            sentSnapshot = snapshot;
            sentMapping = mapping;
            message = new ControlDisplayInfoMessageV1
            {
                DisplayId = snapshot.DisplayId,
                VirtualDesktopX = snapshot.VirtualDesktopX,
                VirtualDesktopY = snapshot.VirtualDesktopY,
                VirtualDesktopWidth = snapshot.VirtualDesktopWidth,
                VirtualDesktopHeight = snapshot.VirtualDesktopHeight,
                CaptureRegionX = snapshot.CaptureRegionX,
                CaptureRegionY = snapshot.CaptureRegionY,
                CaptureRegionWidth = snapshot.CaptureRegionWidth,
                CaptureRegionHeight = snapshot.CaptureRegionHeight,
                FrameWidth = snapshot.FrameWidth,
                FrameHeight = snapshot.FrameHeight,
                DpiScale = snapshot.DpiScale,
                Revision = revision,
                TsUtcMs = clock.UtcNow.ToUnixTimeMilliseconds(),
            };
            publishSessionId = sessionId;
            publishLifecycleGeneration = lifecycleGeneration;
        }

        if (flushedQueuedFrames > 0)
        {
            LogDebug(
                $"Dropped {flushedQueuedFrames} queued frame(s) before display info send " +
                $"(display_id={message.DisplayId}, revision={message.Revision}, frame={message.FrameWidth}x{message.FrameHeight}).");
        }

        _ = BackgroundTaskRunner.Run(
            async () =>
            {
                if (!ShouldSendDisplayInfo(
                        publishSessionId,
                        publishLifecycleGeneration,
                        sentSnapshot,
                        sentMapping,
                        revision))
                {
                    LogDisplayInfoSendSuppressed(message);
                    LogDebug($"Display info suppressed because ownership changed before send (display_id={message.DisplayId}, revision={message.Revision}).");
                    return;
                }

                try
                {
                    await sendDisplayInfoAsync(publishSessionId, message, CancellationToken.None).ConfigureAwait(false);
                    Interlocked.Increment(ref displayInfoSendCount);
                    LogDebug($"Display info sent (display_id={message.DisplayId}, revision={message.Revision}, frame={message.FrameWidth}x{message.FrameHeight}).");
                }
                catch
                {
                    ResetDisplayInfoRetryStateIfCurrent(
                        publishSessionId,
                        publishLifecycleGeneration,
                        sentSnapshot,
                        sentMapping,
                        revision);

                    throw;
                }
            },
            source: "ScreenShareTransport",
            operationName: "send_display_info",
            contextProvider: () => $"revision={revision}; frame={message.FrameWidth}x{message.FrameHeight}");
    }

    private void ClearPendingDisplayInfoUnsafe()
    {
        pendingDisplayInfo = null;
        pendingDisplayInfoMapping = null;
        pendingDisplayInfoNotBeforeUtc = default;
    }

    private bool ShouldSendDisplayInfo(
        string expectedSessionId,
        long expectedLifecycleGeneration,
        ScreenShareDisplayInfoSnapshot expectedSnapshot,
        DisplayInfoMappingKey expectedMapping,
        long expectedRevision)
    {
        lock (gate)
        {
            return captureSource is not null &&
                sendPipeline is not null &&
                string.Equals(sessionId, expectedSessionId, StringComparison.Ordinal) &&
                lifecycleGeneration == expectedLifecycleGeneration &&
                lastSentDisplayInfo.HasValue &&
                lastSentDisplayInfo.Value.Equals(expectedSnapshot) &&
                lastSentDisplayInfoMapping.HasValue &&
                lastSentDisplayInfoMapping.Value.Equals(expectedMapping) &&
                lastSentDisplayInfoRevision == expectedRevision;
        }
    }

    private void LogDisplayInfoSendSuppressed(ControlDisplayInfoMessageV1 message)
    {
        var nowTicks = Environment.TickCount64;
        var windowTicks = (long)Math.Max(1d, TimeSpan.FromSeconds(2).TotalMilliseconds);
        lock (diagnosticRateLimitGate)
        {
            if (nowTicks - lastDisplayInfoSuppressedLogTick < windowTicks)
            {
                return;
            }

            lastDisplayInfoSuppressedLogTick = nowTicks;
        }

        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=display_info_send_suppressed; reason=ownership_changed; display_id={message.DisplayId}; revision={message.Revision}; frame={message.FrameWidth}x{message.FrameHeight}");
    }

    private void ResetDisplayInfoRetryStateIfCurrent(
        string expectedSessionId,
        long expectedLifecycleGeneration,
        ScreenShareDisplayInfoSnapshot expectedSnapshot,
        DisplayInfoMappingKey expectedMapping,
        long expectedRevision)
    {
        lock (gate)
        {
            if (string.Equals(sessionId, expectedSessionId, StringComparison.Ordinal) &&
                lifecycleGeneration == expectedLifecycleGeneration &&
                lastSentDisplayInfo.HasValue &&
                lastSentDisplayInfo.Value.Equals(expectedSnapshot) &&
                lastSentDisplayInfoMapping.HasValue &&
                lastSentDisplayInfoMapping.Value.Equals(expectedMapping) &&
                lastSentDisplayInfoRevision == expectedRevision)
            {
                // Retry on subsequent frames if this send failed while still current.
                lastSentDisplayInfo = null;
                lastSentDisplayInfoMapping = null;
            }
        }
    }

    private static DisplayInfoMappingKey CreateMappingKey(ScreenShareDisplayInfoSnapshot snapshot)
    {
        return new DisplayInfoMappingKey(
            DisplayId: snapshot.DisplayId,
            VirtualDesktopX: snapshot.VirtualDesktopX,
            VirtualDesktopY: snapshot.VirtualDesktopY,
            VirtualDesktopWidth: snapshot.VirtualDesktopWidth,
            VirtualDesktopHeight: snapshot.VirtualDesktopHeight,
            CaptureRegionX: snapshot.CaptureRegionX,
            CaptureRegionY: snapshot.CaptureRegionY,
            CaptureRegionWidth: snapshot.CaptureRegionWidth,
            CaptureRegionHeight: snapshot.CaptureRegionHeight);
    }

    private readonly record struct DisplayInfoMappingKey(
        string DisplayId,
        int VirtualDesktopX,
        int VirtualDesktopY,
        int VirtualDesktopWidth,
        int VirtualDesktopHeight,
        int CaptureRegionX,
        int CaptureRegionY,
        int CaptureRegionWidth,
        int CaptureRegionHeight);
}
