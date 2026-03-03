namespace NLink.App.Services.ScreenCapture;

public sealed class DefaultScreenCaptureSourceFactory : IScreenCaptureSourceFactory
{
    public IScreenCaptureSource Create() => ScreenCaptureFactory.CreateDefault();
}
