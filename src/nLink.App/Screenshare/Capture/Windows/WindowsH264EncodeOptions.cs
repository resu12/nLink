namespace NLink.App.Services.ScreenCapture;

internal readonly record struct WindowsH264EncodeOptions(
    int TargetFramesPerSecond,
    ScreenShareTransportTuningLevel TuningLevel,
    bool ForceKeyFrame,
    long StreamEpoch);
