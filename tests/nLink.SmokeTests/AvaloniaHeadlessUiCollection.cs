using Avalonia;
using Avalonia.Headless;

namespace NLink.SmokeTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AvaloniaHeadlessUiCollection
{
    public const string Name = "Avalonia Headless UI";
}

public static class AvaloniaHeadlessUiAppBootstrap
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<global::NLink.App.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .WithInterFont()
            .LogToTrace();
}
