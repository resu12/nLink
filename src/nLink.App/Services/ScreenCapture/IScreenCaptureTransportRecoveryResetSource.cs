namespace NLink.App.Services.ScreenCapture;

internal interface IScreenCaptureTransportRecoveryResetSource
{
    long ForceTransportRecoveryReset(ScreenShareTransportTuningLevel level);
}
