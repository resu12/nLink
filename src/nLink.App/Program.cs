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

        if (HasSelfTestArgument(args))
        {
            var exitCode = BridgeSelfTestRunner.RunAsync(Console.Out, Console.Error, CancellationToken.None)
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

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
