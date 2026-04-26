using System.Threading;
using NLink.App.Services.ScreenCapture;
using NLink.Core.Logging;
using NLink.Core.ScreenShare;

namespace NLink.App.ViewModels;

public sealed partial class ScreenShareViewerViewModel : IHelperRemoteScreenShareSessionContext
{
    long IHelperRemoteScreenShareSessionContext.FramesApplied => Interlocked.Read(ref framesApplied);

    long IHelperRemoteScreenShareSessionContext.ForcedHelperRemoteRecoveryAfterApplies => forcedHelperRemoteRecoveryAfterApplies;

    bool IHelperRemoteScreenShareSessionContext.ForcedHelperRemoteRecoveryTriggered
    {
        get => forcedHelperRemoteRecoveryTriggered;
        set => forcedHelperRemoteRecoveryTriggered = value;
    }

    string IHelperRemoteScreenShareSessionContext.LogRole => logRole;

    bool IHelperRemoteScreenShareSessionContext.IsHelperRemoteH264(string encoding) => IsHelperRemoteH264(encoding);

    string? IHelperRemoteScreenShareSessionContext.ResolveHelperRemotePreDecodeRejectionReason(
        string? sessionId,
        long streamEpoch,
        long frameId,
        bool isKeyFrame,
        ScreenShareRecoveryDeliveryClass recoveryDeliveryClass)
        => ResolveHelperRemotePreDecodeRejectionReason(sessionId, streamEpoch, frameId, isKeyFrame, recoveryDeliveryClass);

    void IHelperRemoteScreenShareSessionContext.IncrementFramesDroppedWaitingForRecoveryKeyframe()
        => Interlocked.Increment(ref framesDroppedWaitingForRecoveryKeyframe);

    void IHelperRemoteScreenShareSessionContext.IncrementPreCandidateGapTailEmittedToViewerCount()
        => Interlocked.Increment(ref preCandidateGapTailEmittedToViewerCount);

    void IHelperRemoteScreenShareSessionContext.IncrementFramesDroppedForFrameGap()
        => Interlocked.Increment(ref framesDroppedForFrameGap);

    void IHelperRemoteScreenShareSessionContext.ObserveViewerRejectedBeforeEnqueue(
        string sessionId,
        string encoding,
        long streamEpoch,
        long frameId,
        bool isKeyFrame,
        string reason)
        => ObserveViewerRejectedBeforeEnqueue(sessionId, encoding, streamEpoch, frameId, isKeyFrame, reason);

    string IHelperRemoteScreenShareSessionContext.GetEffectiveHelperRemoteSessionId(string? sessionId)
    {
        var effectiveSessionId = ResolveFrameSessionId(sessionId, streamConfig: null);
        if (string.IsNullOrWhiteSpace(effectiveSessionId))
        {
            effectiveSessionId = helperRemoteRecoveryState.SessionId;
        }

        return effectiveSessionId;
    }

    void IHelperRemoteScreenShareSessionContext.LogScreenShareInfo(string message)
        => LocalOperationalLog.Info("ScreenShare", message);
}
