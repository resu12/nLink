using System.Globalization;
using System.Text;

namespace NLink.Core.Logging;

public sealed class RollingFileLogger
{
    private readonly object gate = new();
    private readonly string logFilePath;
    private readonly long maxFileBytes;

    public RollingFileLogger(string logFilePath, long maxFileBytes = 2 * 1024 * 1024)
    {
        this.logFilePath = string.IsNullOrWhiteSpace(logFilePath)
            ? throw new ArgumentException("Log file path is required.", nameof(logFilePath))
            : Path.GetFullPath(logFilePath);
        this.maxFileBytes = maxFileBytes > 0 ? maxFileBytes : throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
    }

    public string LogFilePath => logFilePath;

    public string LogsDirectoryPath => Path.GetDirectoryName(logFilePath) ?? Environment.CurrentDirectory;

    public long MaxFileBytes => maxFileBytes;

    public void WriteLine(string line)
    {
        var text = line ?? string.Empty;
        try
        {
            lock (gate)
            {
                EnsureDirectoryExists();
                RotateIfNeededCore();
                File.AppendAllText(logFilePath, text + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Best-effort support logging must never throw.
        }
    }

    public void RotateIfNeeded()
    {
        try
        {
            lock (gate)
            {
                EnsureDirectoryExists();
                RotateIfNeededCore();
            }
        }
        catch
        {
            // Best-effort support logging must never throw.
        }
    }

    private void EnsureDirectoryExists()
    {
        Directory.CreateDirectory(LogsDirectoryPath);
    }

    private void RotateIfNeededCore()
    {
        if (!File.Exists(logFilePath))
        {
            return;
        }

        var info = new FileInfo(logFilePath);
        if (info.Length < maxFileBytes)
        {
            return;
        }

        var log2 = GetRotatedPath(2);
        var log1 = GetRotatedPath(1);

        if (File.Exists(log2))
        {
            File.Delete(log2);
        }

        if (File.Exists(log1))
        {
            File.Move(log1, log2);
        }

        File.Move(logFilePath, log1);
    }

    private string GetRotatedPath(int index)
    {
        var dir = LogsDirectoryPath;
        var fileName = Path.GetFileNameWithoutExtension(logFilePath);
        var ext = Path.GetExtension(logFilePath);
        return Path.Combine(dir, string.Create(CultureInfo.InvariantCulture, $"{fileName}.{index}{ext}"));
    }
}
