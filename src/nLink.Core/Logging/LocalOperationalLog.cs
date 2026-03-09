using System.Reflection;
using NLink.Core.SessionConnect;

namespace NLink.Core.Logging;

public static class LocalOperationalLog
{
    private static readonly object Gate = new();
    private static readonly RollingFileLogger Logger = new(GetDefaultLogPath());

    public static string LogsDirectoryPath => Logger.LogsDirectoryPath;

    public static string LogFilePath => Logger.LogFilePath;

    public static void Info(string source, string message) => Write("INFO", source, message);

    public static void Warn(string source, string message) => Write("WARN", source, message);

    public static void Error(string source, string message) => Write("ERROR", source, message);

    public static void LogAppStart(string? appVersion = null)
    {
        var version = string.IsNullOrWhiteSpace(appVersion) ? ResolveInformationalVersion() : appVersion!;
        Info("App", $"app start | version={version}");
        var inviteSecurity = InviteSecurityDiagnostics.Snapshot();
        Warn(
            "Security",
            $"event=invite_security_status; version={version}; mode={inviteSecurity.Mode}; signing={inviteSecurity.SigningConfiguration}; public_invite_flow={inviteSecurity.PublicInviteFlow}; release_ready={(inviteSecurity.ReleaseReady ? "yes" : "no")}; warning={inviteSecurity.Warning}");
    }

    private static void Write(string level, string source, string message)
    {
        try
        {
            lock (Gate)
            {
                var safeSource = SensitiveDataRedactor.Redact(source);
                var safeMessage = SensitiveDataRedactor.Redact(message);
                Logger.WriteLine($"[{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss'Z'}] [{level}] [{safeSource}] {safeMessage}");
            }
        }
        catch
        {
            // Logging must never break application flow.
        }
    }

    private static string GetDefaultLogPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var logsDir = Path.Combine(localAppData, "nLink", "logs");
        return Path.Combine(logsDir, "nlink.log");
    }

    private static string ResolveInformationalVersion()
    {
        try
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(LocalOperationalLog).Assembly;
            var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                return info!;
            }

            return assembly.GetName().Version?.ToString() ?? "(unknown)";
        }
        catch
        {
            return "(unknown)";
        }
    }
}
