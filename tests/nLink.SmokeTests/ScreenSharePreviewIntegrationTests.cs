using System.Diagnostics;
using Avalonia.Headless;
using NLink.App.Configuration;
using NLink.App.Services;
using NLink.App.ViewModels;
using NLink.Infra.DevLocal;

namespace NLink.SmokeTests;

[Collection(AvaloniaHeadlessUiCollection.Name)]
public sealed class ScreenSharePreviewIntegrationTests : IClassFixture<ScreenSharePreviewFixture>
{
    private readonly ScreenSharePreviewFixture fixture;

    public ScreenSharePreviewIntegrationTests(ScreenSharePreviewFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task HelpeePreview_ToggleOnOff_Repeatedly_DoesNotCrash_AndCleansUp()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (!FeatureFlags.EnableScreenShareScaffold ||
            !FeatureFlags.EnableScreenShareCapture ||
            !FeatureFlags.EnableScreenSharePreview)
        {
            return;
        }

        await fixture.Session.Dispatch(async () =>
        {
            var transportConfig = CreateDevLocalTestConfig();
            using var runtime = new SessionRuntime(() => new DevLocalTransport());
            using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, transportConfig, runtime);
            using var logWriter = new StringWriter();
            using var traceListener = new TextWriterTraceListener(logWriter);
            Trace.Listeners.Add(traceListener);

            try
            {
                Assert.True(helpee.CanShowScreenShareAction);

                for (var i = 0; i < 2; i++)
                {
                    helpee.ToggleScreenSharePreviewCommand.Execute(null);

                    await WaitUntilAsync(
                        () => helpee.IsScreenSharingPreviewActive,
                        TimeSpan.FromSeconds(5),
                        () => BuildState(helpee, logWriter));

                    await WaitUntilAsync(
                        () => helpee.ScreenSharePreviewFrame is not null,
                        TimeSpan.FromSeconds(10),
                        () => BuildState(helpee, logWriter));

                    helpee.ToggleScreenSharePreviewCommand.Execute(null);

                    await WaitUntilAsync(
                        () => !helpee.IsScreenSharingPreviewActive && helpee.ScreenSharePreviewFrame is null,
                        TimeSpan.FromSeconds(10),
                        () => BuildState(helpee, logWriter));
                }
            }
            finally
            {
                Trace.Listeners.Remove(traceListener);
            }

            return true;
        }, default);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, Func<string> describeState)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.True(condition(), $"Timed out waiting for preview condition. {describeState()}");
    }

    private static string BuildState(HelpeePageViewModel helpee, StringWriter logWriter)
    {
        return $"CanShow={helpee.CanShowScreenShareAction}, Active={helpee.IsScreenSharingPreviewActive}, HasFrame={helpee.ScreenSharePreviewFrame is not null}, Logs={logWriter}";
    }

    private static TransportRuntimeConfig CreateDevLocalTestConfig()
    {
        var previous = Environment.GetEnvironmentVariable("FRH_TRANSPORT");

        try
        {
            Environment.SetEnvironmentVariable("FRH_TRANSPORT", null);
            return TransportRuntimeConfig.Select();
        }
        finally
        {
            Environment.SetEnvironmentVariable("FRH_TRANSPORT", previous);
        }
    }
}

public sealed class ScreenSharePreviewFixture : IDisposable
{
    public ScreenSharePreviewFixture()
    {
        Session = HeadlessUnitTestSession.StartNew(typeof(AvaloniaHeadlessUiAppBootstrap));
    }

    public HeadlessUnitTestSession Session { get; }

    public void Dispose()
    {
        Session.Dispose();
    }
}
