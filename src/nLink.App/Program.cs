using System;
using System.Linq;
using System.Threading;
using Avalonia;

namespace NLink.App;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (HasBenchmarkArgument(args))
        {
            var exitCode = BenchmarkRunner.RunAsync(args, Console.Out, Console.Error, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            Environment.ExitCode = exitCode;
            return;
        }

        if (HasSoakArgument(args))
        {
            var exitCode = SoakRunner.RunAsync(args, Console.Out, Console.Error, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            Environment.ExitCode = exitCode;
            return;
        }

        if (HasScreenShareSoakArgument(args))
        {
            var exitCode = ScreenShareSoakRunner.RunAsync(args, Console.Out, Console.Error, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            Environment.ExitCode = exitCode;
            return;
        }

        if (HasSelfTestArgument(args))
        {
            var exitCode = BridgeSelfTestRunner.RunAsync(Console.Out, Console.Error, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            Environment.ExitCode = exitCode;
            return;
        }

        if (HasResourceRunnerArgument(args))
        {
            var exitCode = ResourceBenchmarkRunner.RunAsync(args, Console.Out, Console.Error, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            Environment.ExitCode = exitCode;
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    internal static bool HasSelfTestArgument(string[] args)
    {
        return args.Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool HasBenchmarkArgument(string[] args)
    {
        return args.Any(a => string.Equals(a, "--bench", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool HasSoakArgument(string[] args)
    {
        return args.Any(a => string.Equals(a, "--soak", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool HasScreenShareSoakArgument(string[] args)
    {
        return args.Any(a => string.Equals(a, "--screenshare-soak", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool HasResourceRunnerArgument(string[] args)
    {
        return args.Any(a =>
            string.Equals(a, "--resource-bench", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "--leak-check", StringComparison.OrdinalIgnoreCase));
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
