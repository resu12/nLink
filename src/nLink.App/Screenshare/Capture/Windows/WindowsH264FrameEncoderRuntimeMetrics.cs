namespace NLink.App.Services.ScreenCapture;

internal readonly record struct WindowsH264FrameEncoderRuntimeMetrics(
    string EncoderPath = "",
    long EmittedDisplayableFrames = 0,
    long EmittedNonDisplayableUnits = 0,
    double DisplayableFrameRatio = 0,
    long IdrFramesEmitted = 0,
    long PFramesEmitted = 0,
    long DroppedBFrames = 0,
    long DroppedMultiPictureUnits = 0,
    double IdrFrameRatio = 0,
    double AverageEncodedFrameBytes = 0,
    bool TransportIpOnlyMode = false,
    string LastAccessUnitKind = "",
    string LowDelayConfigApplied = "",
    bool SenderContinuityRecoveryActive = false,
    long SenderContinuityLossCount = 0,
    long FramesDroppedWaitingForRecoveryKeyframe = 0,
    string LastSenderContinuityLossReason = "",
    long LastPreprocessDurationMs = -1,
    long LastTransformEncodeDurationMs = -1,
    long LastEncodeTotalDurationMs = -1);
