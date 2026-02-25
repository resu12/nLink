using System;
using System.Diagnostics;
using NLink.Core.Logging;

namespace NLink.App.Configuration;

internal static class AppLog
{
    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    private static void Write(string level, string message)
    {
        var safeMessage = SensitiveDataRedactor.Redact(message);
        var line = $"[{DateTime.Now:HH:mm:ss}] [nLink] [{level}] {safeMessage}";
        Console.WriteLine(line);
        Debug.WriteLine(line);

        if (string.Equals(level, "WARN", StringComparison.Ordinal))
        {
            LocalOperationalLog.Warn("App", safeMessage);
        }
        else
        {
            LocalOperationalLog.Info("App", safeMessage);
        }
    }
}
