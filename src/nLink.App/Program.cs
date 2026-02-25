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

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
