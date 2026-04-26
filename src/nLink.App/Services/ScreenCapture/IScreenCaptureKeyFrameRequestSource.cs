namespace NLink.App.Services.ScreenCapture;

internal interface IScreenCaptureKeyFrameRequestSource
{
    void RequestKeyFrame(string reason);
}
