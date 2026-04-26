using Avalonia;
using Avalonia.Headless;

namespace NLink.SmokeTests;

public static class AvaloniaHeadlessUiAppBootstrap
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<global::NLink.App.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .WithInterFont()
            .LogToTrace();
}
