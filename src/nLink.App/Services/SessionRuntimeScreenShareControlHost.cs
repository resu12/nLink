using System;
using System.Threading;
using System.Threading.Tasks;
using NLink.App.Services.ScreenCapture;
using NLink.Core.ScreenShare;
using NLink.Core.SessionSecurity;

namespace NLink.App.Services;

internal sealed class SessionRuntimeScreenShareControlHost
{
    private static readonly TimeSpan HelperRemoteScreenSharePressureReevaluationInterval = TimeSpan.FromSeconds(1);
    private static readonly IHelperRemoteScreenSharePressurePublishTarget NoopPressurePublishTarget = new NoopHelperRemoteScreenSharePressurePublishTarget();
    private readonly ISessionRuntimeScreenShareControlContext context;
    private readonly HelperRemoteScreenSharePressurePublisher pressurePublisher;
    private readonly object helperRemoteScreenSharePressureTimerGate = new();
    private bool remoteScreenShareActive;
    private Timer? helperRemoteScreenSharePressureTimer;
    private int helperRemoteScreenSharePressureTimerTickQueued;
    private long remoteScreenShareRecoveryReceiptReceivedCount;
    private long remoteScreenShareLastRecoveryReceiptStreamEpoch;
    private long remoteScreenShareLastRecoveryReceiptOwnerFrameId = -1;
    private long remoteScreenShareLastRecoveryReceiptVisibleRecoveryFrameId = -1;
    private long remoteScreenShareLastRecoveryReceiptVisibleHeadFrameId = -1;
    private string remoteScreenShareLastRecoveryReceiptKind = string.Empty;

    public SessionRuntimeScreenShareControlHost(
        ISessionRuntimeScreenShareControlContext context,
        HelperRemoteScreenSharePressurePublisher? pressurePublisher = null)
    {
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        this.pressurePublisher = pressurePublisher ?? new HelperRemoteScreenSharePressurePublisher(NoopPressurePublishTarget);
    }

    public bool RemoteScreenShareActive => remoteScreenShareActive;

    public long RemoteScreenShareRecoveryReceiptReceivedCount => remoteScreenShareRecoveryReceiptReceivedCount;

    public long RemoteScreenShareLastRecoveryReceiptStreamEpoch => remoteScreenShareLastRecoveryReceiptStreamEpoch;

    public long RemoteScreenShareLastRecoveryReceiptOwnerFrameId => remoteScreenShareLastRecoveryReceiptOwnerFrameId;

    public long RemoteScreenShareLastRecoveryReceiptVisibleRecoveryFrameId => remoteScreenShareLastRecoveryReceiptVisibleRecoveryFrameId;

    public long RemoteScreenShareLastRecoveryReceiptVisibleHeadFrameId => remoteScreenShareLastRecoveryReceiptVisibleHeadFrameId;

    public string RemoteScreenShareLastRecoveryReceiptKind => remoteScreenShareLastRecoveryReceiptKind;

    public void HandleTransportScreenSharePressureStateReceived(object? sender, ScreenSharePressureStateReceivedEventArgs e)
    {
        if (!context.IsFromCurrentTransport(sender) ||
            context.IsDisposed ||
            context.Role != SessionRuntimeRole.Helpee ||
            context.RuntimeState != SessionRuntimeState.Connected)
        {
            return;
        }

        if (!context.TryValidateScreenShareSession(e.Message.SessionId, "screen_share_pressure_dispatch", "pressure") ||
            !context.RequireCapability(SessionCapability.ScreenShare, "screen_share_pressure_dispatch"))
        {
            return;
        }

        context.ApplyRemotePressureState(e.Message);
    }

    public void HandleTransportScreenShareRecoveryReceiptReceived(object? sender, ScreenShareRecoveryReceiptReceivedEventArgs e)
    {
        if (!context.IsFromCurrentTransport(sender) ||
            context.IsDisposed ||
            context.Role != SessionRuntimeRole.Helpee ||
            context.RuntimeState != SessionRuntimeState.Connected)
        {
            return;
        }

        if (!context.TryValidateScreenShareSession(e.Message.SessionId, "screen_share_recovery_receipt_dispatch", "recovery_receipt") ||
            !context.RequireCapability(SessionCapability.ScreenShare, "screen_share_recovery_receipt_dispatch"))
        {
            return;
        }

        remoteScreenShareRecoveryReceiptReceivedCount++;
        remoteScreenShareLastRecoveryReceiptStreamEpoch = e.Message.StreamEpoch;
        remoteScreenShareLastRecoveryReceiptOwnerFrameId = e.Message.OwnerFrameId;
        remoteScreenShareLastRecoveryReceiptVisibleRecoveryFrameId = e.Message.VisibleRecoveryFrameId;
        remoteScreenShareLastRecoveryReceiptVisibleHeadFrameId = e.Message.VisibleHeadFrameId;
        remoteScreenShareLastRecoveryReceiptKind = e.Message.ReceiptKind;

        context.LogScreenShareTransportInfo(
            $"event=screenshare_recovery_receipt_received_runtime; session_id={e.Message.SessionId}; stream_epoch={e.Message.StreamEpoch}; owner_frame_id={e.Message.OwnerFrameId}; visible_recovery_frame_id={e.Message.VisibleRecoveryFrameId}; visible_head_frame_id={e.Message.VisibleHeadFrameId}; receipt_kind={e.Message.ReceiptKind}; peer_id={(string.IsNullOrWhiteSpace(e.PeerId) ? "(none)" : e.PeerId)}; transport={context.GetTransportNameForLog(sender)}");
        context.ApplyRemoteRecoveryReceipt(e.Message);
    }

    public void HandleTransportScreenShareVideoKeyframeRequestReceived(object? sender, ScreenShareVideoKeyframeRequestReceivedEventArgs e)
    {
        if (!context.IsFromCurrentTransport(sender) ||
            context.IsDisposed ||
            context.Role != SessionRuntimeRole.Helpee ||
            context.RuntimeState != SessionRuntimeState.Connected)
        {
            return;
        }

        if (!context.TryValidateScreenShareSession(e.Message.SessionId, "screen_share_keyframe_request_dispatch", "keyframe_request") ||
            !context.RequireCapability(SessionCapability.ScreenShare, "screen_share_keyframe_request_dispatch"))
        {
            return;
        }

        context.RequestRemoteKeyFrame(e.Message.Reason);
    }

    public void EnsureHelperRemoteScreenSharePressureTimerStarted()
    {
        if (context.IsDisposed ||
            context.Role != SessionRuntimeRole.Helper ||
            context.RuntimeState != SessionRuntimeState.Connected ||
            !remoteScreenShareActive)
        {
            return;
        }

        lock (helperRemoteScreenSharePressureTimerGate)
        {
            helperRemoteScreenSharePressureTimer ??= new Timer(
                static state => ((SessionRuntimeScreenShareControlHost)state!).OnHelperRemoteScreenSharePressureTimerTick(),
                this,
                HelperRemoteScreenSharePressureReevaluationInterval,
                HelperRemoteScreenSharePressureReevaluationInterval);
        }
    }

    public void StopHelperRemoteScreenSharePressureTimer()
    {
        Timer? timerToDispose = null;
        lock (helperRemoteScreenSharePressureTimerGate)
        {
            timerToDispose = helperRemoteScreenSharePressureTimer;
            helperRemoteScreenSharePressureTimer = null;
        }

        Interlocked.Exchange(ref helperRemoteScreenSharePressureTimerTickQueued, 0);

        if (timerToDispose is null)
        {
            return;
        }

        try
        {
            timerToDispose.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        catch
        {
            // Best-effort shutdown only.
        }

        try
        {
            timerToDispose.Dispose();
        }
        catch
        {
            // Best-effort shutdown only.
        }
    }

    public void OnHelperRemoteScreenSharePressureTimerTick()
    {
        if (context.IsDisposed ||
            context.Role != SessionRuntimeRole.Helper ||
            context.RuntimeState != SessionRuntimeState.Connected ||
            !remoteScreenShareActive)
        {
            return;
        }

        if (Interlocked.Exchange(ref helperRemoteScreenSharePressureTimerTickQueued, 1) != 0)
        {
            return;
        }

        context.RunScreenShareBackgroundTask(
            async () =>
            {
                try
                {
                    pressurePublisher.Publish(timerDriven: true);
                }
                catch (Exception ex)
                {
                    context.LogScreenShareTransportInfo(
                        $"event=screenshare_pressure_timer_tick_failed; reason={ex.GetType().Name}; message={context.SanitizeTransportExceptionMessage(ex.Message)}");
                }
                finally
                {
                    Interlocked.Exchange(ref helperRemoteScreenSharePressureTimerTickQueued, 0);
                }

                await Task.CompletedTask.ConfigureAwait(false);
            },
            countAsTransportTask: false);
    }

    public void MaybeSendScreenSharePressureState()
    {
        pressurePublisher.Publish();
    }

    public void MaybeSendScreenSharePressureState(bool timerDriven)
    {
        pressurePublisher.Publish(timerDriven);
    }

    public void ReportHelperRemoteScreenShareFrameApplied(
        long ageMs,
        long streamEpoch,
        long frameId,
        long visibleHeadFrameId,
        long stableVisibleHeadFrameId,
        long framesAppliedSinceLastGap)
    {
        context.ReportHelperRemoteScreenShareFrameApplied(
            ageMs,
            streamEpoch,
            frameId,
            visibleHeadFrameId,
            stableVisibleHeadFrameId,
            framesAppliedSinceLastGap);
    }

    public void ReportHelperRemoteScreenShareFrameApplied(
        long ageMs,
        long streamEpoch,
        long frameId,
        long visibleHeadFrameId,
        long stableVisibleHeadFrameId,
        long framesAppliedSinceLastGap,
        HelperRemoteSessionSnapshot sessionSnapshot)
    {
        context.ReportHelperRemoteScreenShareFrameApplied(
            ageMs,
            streamEpoch,
            frameId,
            visibleHeadFrameId,
            stableVisibleHeadFrameId,
            framesAppliedSinceLastGap,
            sessionSnapshot);
    }

    public void ReportHelperRemoteScreenShareSessionSnapshot(HelperRemoteSessionSnapshot snapshot)
    {
        context.ReportHelperRemoteScreenShareSessionSnapshot(snapshot);
    }

    public void ReportHelperRemoteScreenShareDecodeNeedsMoreInput(long streamEpoch)
    {
        context.ReportHelperRemoteScreenShareDecodeNeedsMoreInput(streamEpoch);
    }

    public void ReportHelperRemoteScreenShareContinuityLost(
        long streamEpoch,
        string reason,
        bool shouldRequestRecoveryKeyframe,
        long currentEpochNeedMoreInputCount,
        long expectedNextFrameId,
        long receivedFrameId,
        long lastCleanFrameId)
    {
        context.ReportHelperRemoteScreenShareContinuityLost(
            streamEpoch,
            reason,
            shouldRequestRecoveryKeyframe,
            currentEpochNeedMoreInputCount,
            expectedNextFrameId,
            receivedFrameId,
            lastCleanFrameId);
    }

    public void ReportHelperRemoteScreenShareRecoveryKeyframeApplied(long ageMs, long streamEpoch)
    {
        context.ReportHelperRemoteScreenShareRecoveryKeyframeApplied(ageMs, streamEpoch);
    }

    public void ReportHelperRemoteScreenShareRecoveryWindowStateChanged(
        long streamEpoch,
        long recoveryFrameId,
        long lastContiguousFrameId,
        int contiguousFollowerApplyCount,
        string status,
        string? abortReason)
    {
        context.ReportHelperRemoteScreenShareRecoveryWindowStateChanged(
            streamEpoch,
            recoveryFrameId,
            lastContiguousFrameId,
            contiguousFollowerApplyCount,
            status,
            abortReason);
    }

    public void ReportHelperRemoteScreenShareStaleFrameDropped(
        long renderedAgeMs,
        long streamEpoch,
        bool referenceContinuityPreserved)
    {
        context.ReportHelperRemoteScreenShareStaleFrameDropped(
            renderedAgeMs,
            streamEpoch,
            referenceContinuityPreserved);
    }

    public void ObserveAcceptedFrame(ScreenShareFrameCompletedEventArgs e)
    {
        remoteScreenShareActive = true;
        EnsureHelperRemoteScreenSharePressureTimerStarted();
        context.TrackHelperRemoteScreenShareAcceptedFrame(e);
    }

    public void NotifyRemoteScreenShareStopped(string reason, object? sender, bool localStop)
    {
        remoteScreenShareActive = false;
        StopHelperRemoteScreenSharePressureTimer();
        context.NotifyRemoteScreenShareStopped(reason, sender, localStop);
    }

    public void ResetRemoteScreenShareActivity()
    {
        remoteScreenShareActive = false;
    }

    public SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests()
    {
        return context.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
    }

    private sealed class NoopHelperRemoteScreenSharePressurePublishTarget : IHelperRemoteScreenSharePressurePublishTarget
    {
        public void PublishHelperRemoteScreenSharePressureState(bool timerDriven)
        {
            _ = timerDriven;
        }
    }
}
