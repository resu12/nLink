using NLink.Core.ScreenShare;
using NLink.App.Services.ScreenCapture;

namespace NLink.App.ViewModels;

internal interface IHelperRemoteScreenShareSessionContext
{
    bool IsHelperRemoteH264(string encoding);

    string? ResolveHelperRemotePreDecodeRejectionReason(
        string? sessionId,
        long streamEpoch,
        long frameId,
        bool isKeyFrame,
        ScreenShareRecoveryDeliveryClass recoveryDeliveryClass);

    void IncrementFramesDroppedWaitingForRecoveryKeyframe();

    void IncrementPreCandidateGapTailEmittedToViewerCount();

    void IncrementFramesDroppedForFrameGap();

    void ObserveViewerRejectedBeforeEnqueue(
        string sessionId,
        string encoding,
        long streamEpoch,
        long frameId,
        bool isKeyFrame,
        string reason);

    string GetEffectiveHelperRemoteSessionId(string? sessionId);

    long FramesApplied { get; }

    long ForcedHelperRemoteRecoveryAfterApplies { get; }

    bool ForcedHelperRemoteRecoveryTriggered { get; set; }

    string LogRole { get; }

    void LogScreenShareInfo(string message);
}
