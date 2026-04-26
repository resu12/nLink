namespace NLink.SmokeTests;

public sealed class ScreenShareCoordinatorFixture : IDisposable
{
    public ScreenShareCoordinatorFixture()
    {
        Session = Avalonia.Headless.HeadlessUnitTestSession.StartNew(typeof(AvaloniaHeadlessUiAppBootstrap));
    }

    public Avalonia.Headless.HeadlessUnitTestSession Session { get; }

    public void Dispose()
    {
        Session.Dispose();
    }
}
