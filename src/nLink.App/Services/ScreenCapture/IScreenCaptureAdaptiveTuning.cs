namespace NLink.App.Services.ScreenCapture;

internal interface IScreenCaptureAdaptiveTuning
{
    void SetCaptureFrameRateHint(int maxFramesPerSecond);
}
