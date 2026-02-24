using System;
using System.Diagnostics;

namespace NLink.App.Configuration;

internal static class AppLog
{
    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    private static void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] [nLink] [{level}] {message}";
        Console.WriteLine(line);
        Debug.WriteLine(line);
    }
}

