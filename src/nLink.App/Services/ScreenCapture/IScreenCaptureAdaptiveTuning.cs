namespace NLink.App.Services.ScreenCapture;

internal enum ScreenShareTransportTuningLevel
{
    Normal = 0,
    QualityProtected = 1,
    BandwidthReduced = 2,
}

internal interface IScreenCaptureAdaptiveTuning
{
    void SetCaptureFrameRateHint(int maxFramesPerSecond);

    void SetTransportTuningLevel(ScreenShareTransportTuningLevel level);
}
