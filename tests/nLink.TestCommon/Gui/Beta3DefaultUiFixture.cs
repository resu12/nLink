using Avalonia.Headless;

namespace NLink.SmokeTests;

public sealed class Beta3DefaultUiFixture : IDisposable
{
    public Beta3DefaultUiFixture()
    {
        Session = HeadlessUnitTestSession.StartNew(typeof(AvaloniaHeadlessUiAppBootstrap));
    }

    public HeadlessUnitTestSession Session { get; }

    public void Dispose()
    {
        Session.Dispose();
    }
}
