using System.Globalization;
using System.Text;

namespace NLink.Core.Logging;

public sealed class RollingFileLogger
{
    public const int DefaultRetainedFileCount = 20;

    private readonly object gate = new();
    private readonly string logFilePath;
    private readonly long maxFileBytes;
    private readonly int retainedFileCount;

    public RollingFileLogger(
        string logFilePath,
        long maxFileBytes = 2 * 1024 * 1024,
        int retainedFileCount = DefaultRetainedFileCount)
    {
        this.logFilePath = string.IsNullOrWhiteSpace(logFilePath)
            ? throw new ArgumentException("Log file path is required.", nameof(logFilePath))
            : Path.GetFullPath(logFilePath);
        this.maxFileBytes = maxFileBytes > 0 ? maxFileBytes : throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
        this.retainedFileCount = retainedFileCount > 0
            ? retainedFileCount
            : throw new ArgumentOutOfRangeException(nameof(retainedFileCount));
    }

    public string LogFilePath => logFilePath;

    public string LogsDirectoryPath => Path.GetDirectoryName(logFilePath) ?? Environment.CurrentDirectory;

    public long MaxFileBytes => maxFileBytes;

    public int RetainedFileCount => retainedFileCount;

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

        if (retainedFileCount <= 1)
        {
            File.Delete(logFilePath);
            return;
        }

        var oldestRotatedIndex = retainedFileCount - 1;
        var oldestRotatedPath = GetRotatedPath(oldestRotatedIndex);

        if (File.Exists(oldestRotatedPath))
        {
            File.Delete(oldestRotatedPath);
        }

        for (var index = oldestRotatedIndex - 1; index >= 1; index--)
        {
            var currentPath = GetRotatedPath(index);
            if (File.Exists(currentPath))
            {
                File.Move(currentPath, GetRotatedPath(index + 1));
            }
        }

        File.Move(logFilePath, GetRotatedPath(1));
    }

    private string GetRotatedPath(int index)
    {
        var dir = LogsDirectoryPath;
        var fileName = Path.GetFileNameWithoutExtension(logFilePath);
        var ext = Path.GetExtension(logFilePath);
        return Path.Combine(dir, string.Create(CultureInfo.InvariantCulture, $"{fileName}.{index}{ext}"));
    }
}
