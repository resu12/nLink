using System;
using System.Threading;
using System.Threading.Tasks;
using NLink.App.Services.ScreenCapture;
using NLink.Core.Logging;
using NLink.Core.ScreenShare;
using NLink.Core.SessionSecurity;

namespace NLink.App.Services;

public sealed partial class SessionRuntime
{
    bool ISessionRuntimeScreenShareControlContext.IsDisposed => disposed;

    SessionRuntimeRole ISessionRuntimeScreenShareControlContext.Role => role;

    SessionRuntimeState ISessionRuntimeScreenShareControlContext.RuntimeState => state;

    bool ISessionRuntimeScreenShareControlContext.IsFromCurrentTransport(object? sender) => IsFromCurrentTransport(sender);

    bool ISessionRuntimeScreenShareControlContext.TryValidateScreenShareSession(string sessionId, string stage, string kind)
        => TryValidateScreenShareSession(sessionId, stage, kind);

    bool ISessionRuntimeScreenShareControlContext.RequireCapability(SessionCapability capability, string stage)
        => RequireCapability(capability, stage);

    string ISessionRuntimeScreenShareControlContext.GetTransportNameForLog(object? sender) => GetTransportNameForLog(sender);

    void ISessionRuntimeScreenShareControlContext.LogScreenShareTransportInfo(string message)
        => LocalOperationalLog.Info("ScreenShareTransport", message);

    void ISessionRuntimeScreenShareControlContext.ApplyRemotePressureState(ScreenSharePressureStateV1 message)
        => transportScreenShareCoordinator.SetRemotePressureState(
            message.Mode switch
            {
                ScreenSharePressureMode.ReduceFps => ScreenShareRemotePressureMode.ReduceFps,
                ScreenSharePressureMode.CatchUpOnly => ScreenShareRemotePressureMode.CatchUpOnly,
                _ => ScreenShareRemotePressureMode.None,
            },
            message.Reason,
            message.ObservedFrameAgeMs,
            message.RecentStaleFrameDrops,
            message.SentAtUtcMs,
            message.CurrentEpochWarmupActive,
            message.CurrentEpochApplyCount,
            message.CurrentEpochNeedMoreInputCount,
            message.LastVisibleApplyFrameId,
            message.VisibleHeadFrameId,
            message.AppliedHeadFrameId,
            message.SteadyVisibleProgressActive,
            message.StableVisibleHeadFrameId,
            message.FramesAppliedSinceLastGap,
            message.VisibleRecoveryFloorFrameId,
            message.CurrentEpochRecoveryKeyframeApplyCount);

    void ISessionRuntimeScreenShareControlContext.ApplyRemoteRecoveryReceipt(ScreenShareRecoveryReceiptV1 message)
        => transportScreenShareCoordinator.SetRemoteRecoveryReceipt(message);

    void ISessionRuntimeScreenShareControlContext.RequestRemoteKeyFrame(string reason)
        => transportScreenShareCoordinator.RequestKeyFrame(reason);

    void ISessionRuntimeScreenShareControlContext.RunScreenShareBackgroundTask(Func<Task> work, bool countAsTransportTask)
        => RunCountedBackgroundTask(work, countAsTransportTask);

    string ISessionRuntimeScreenShareControlContext.SanitizeTransportExceptionMessage(string? message)
        => SanitizeDispatchExceptionMessage(message);

    void ISessionRuntimeScreenShareControlContext.ReportHelperRemoteScreenShareFrameApplied(
        long ageMs,
        long streamEpoch,
        long frameId,
        long visibleHeadFrameId,
        long stableVisibleHeadFrameId,
        long framesAppliedSinceLastGap)
        => ReportHelperRemoteScreenShareFrameAppliedCore(
            ageMs,
            streamEpoch,
            frameId,
            visibleHeadFrameId,
            stableVisibleHeadFrameId,
            framesAppliedSinceLastGap);

    void ISessionRuntimeScreenShareControlContext.ReportHelperRemoteScreenShareFrameApplied(
        long ageMs,
        long streamEpoch,
        long frameId,
        long visibleHeadFrameId,
        long stableVisibleHeadFrameId,
        long framesAppliedSinceLastGap,
        HelperRemoteSessionSnapshot sessionSnapshot)
        => ReportHelperRemoteScreenShareFrameAppliedCore(
            ageMs,
            streamEpoch,
            frameId,
            visibleHeadFrameId,
            stableVisibleHeadFrameId,
            framesAppliedSinceLastGap,
            sessionSnapshot);

    void ISessionRuntimeScreenShareControlContext.ReportHelperRemoteScreenShareSessionSnapshot(HelperRemoteSessionSnapshot snapshot)
        => ReportHelperRemoteScreenShareSessionSnapshotCore(snapshot);

    void ISessionRuntimeScreenShareControlContext.ReportHelperRemoteScreenShareDecodeNeedsMoreInput(long streamEpoch)
        => ReportHelperRemoteScreenShareDecodeNeedsMoreInputCore(streamEpoch);

    void ISessionRuntimeScreenShareControlContext.ReportHelperRemoteScreenShareContinuityLost(
        long streamEpoch,
        string reason,
        bool shouldRequestRecoveryKeyframe,
        long currentEpochNeedMoreInputCount,
        long expectedNextFrameId,
        long receivedFrameId,
        long lastCleanFrameId)
        => ReportHelperRemoteScreenShareContinuityLostCore(
            streamEpoch,
            reason,
            shouldRequestRecoveryKeyframe,
            currentEpochNeedMoreInputCount,
            expectedNextFrameId,
            receivedFrameId,
            lastCleanFrameId);

    void ISessionRuntimeScreenShareControlContext.ReportHelperRemoteScreenShareRecoveryKeyframeApplied(long ageMs, long streamEpoch)
        => ReportHelperRemoteScreenShareRecoveryKeyframeAppliedCore(ageMs, streamEpoch);

    void ISessionRuntimeScreenShareControlContext.ReportHelperRemoteScreenShareRecoveryWindowStateChanged(
        long streamEpoch,
        long recoveryFrameId,
        long lastContiguousFrameId,
        int contiguousFollowerApplyCount,
        string status,
        string? abortReason)
        => ReportHelperRemoteScreenShareRecoveryWindowStateChangedCore(
            streamEpoch,
            recoveryFrameId,
            lastContiguousFrameId,
            contiguousFollowerApplyCount,
            status,
            abortReason);

    void ISessionRuntimeScreenShareControlContext.ReportHelperRemoteScreenShareStaleFrameDropped(
        long renderedAgeMs,
        long streamEpoch,
        bool referenceContinuityPreserved)
        => ReportHelperRemoteScreenShareStaleFrameDroppedCore(
            renderedAgeMs,
            streamEpoch,
            referenceContinuityPreserved);

    void ISessionRuntimeScreenShareControlContext.TrackHelperRemoteScreenShareAcceptedFrame(ScreenShareFrameCompletedEventArgs e)
        => TrackHelperRemoteScreenShareAcceptedFrameCore(e);

    void ISessionRuntimeScreenShareControlContext.NotifyRemoteScreenShareStopped(string reason, object? sender, bool localStop)
        => NotifyRemoteScreenShareStoppedCore(reason, sender, localStop);

    SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot ISessionRuntimeScreenShareControlContext.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests()
        => GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTestsCore();
}
