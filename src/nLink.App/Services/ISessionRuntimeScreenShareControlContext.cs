using System;
using System.Threading.Tasks;
using NLink.App.Services.ScreenCapture;
using NLink.Core.ScreenShare;
using NLink.Core.SessionSecurity;
namespace NLink.App.Services;

internal interface ISessionRuntimeScreenShareControlContext
{
    bool IsDisposed { get; }

    SessionRuntimeRole Role { get; }

    SessionRuntimeState RuntimeState { get; }

    bool IsFromCurrentTransport(object? sender);

    bool TryValidateScreenShareSession(string sessionId, string stage, string kind);

    bool RequireCapability(SessionCapability capability, string stage);

    string GetTransportNameForLog(object? sender);

    void LogScreenShareTransportInfo(string message);

    void ApplyRemotePressureState(ScreenSharePressureStateV1 message);

    void ApplyRemoteRecoveryReceipt(ScreenShareRecoveryReceiptV1 message);

    void RequestRemoteKeyFrame(string reason);

    void RunScreenShareBackgroundTask(Func<Task> work, bool countAsTransportTask = false);

    string SanitizeTransportExceptionMessage(string? message);

    void ReportHelperRemoteScreenShareFrameApplied(
        long ageMs,
        long streamEpoch,
        long frameId,
        long visibleHeadFrameId,
        long stableVisibleHeadFrameId,
        long framesAppliedSinceLastGap);

    void ReportHelperRemoteScreenShareFrameApplied(
        long ageMs,
        long streamEpoch,
        long frameId,
        long visibleHeadFrameId,
        long stableVisibleHeadFrameId,
        long framesAppliedSinceLastGap,
        HelperRemoteSessionSnapshot sessionSnapshot);

    void ReportHelperRemoteScreenShareSessionSnapshot(HelperRemoteSessionSnapshot snapshot);

    void ReportHelperRemoteScreenShareDecodeNeedsMoreInput(long streamEpoch);

    void ReportHelperRemoteScreenShareContinuityLost(
        long streamEpoch,
        string reason,
        bool shouldRequestRecoveryKeyframe,
        long currentEpochNeedMoreInputCount,
        long expectedNextFrameId,
        long receivedFrameId,
        long lastCleanFrameId);

    void ReportHelperRemoteScreenShareRecoveryKeyframeApplied(long ageMs, long streamEpoch);

    void ReportHelperRemoteScreenShareRecoveryWindowStateChanged(
        long streamEpoch,
        long recoveryFrameId,
        long lastContiguousFrameId,
        int contiguousFollowerApplyCount,
        string status,
        string? abortReason);

    void ReportHelperRemoteScreenShareStaleFrameDropped(long renderedAgeMs, long streamEpoch);

    void TrackHelperRemoteScreenShareAcceptedFrame(ScreenShareFrameCompletedEventArgs e);

    void NotifyRemoteScreenShareStopped(string reason, object? sender, bool localStop);

    SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
}
