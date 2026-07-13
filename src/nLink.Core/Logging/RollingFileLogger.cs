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

    internal Action<int>? RetainedSliceReadBarrierForTests { get; set; }

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

    internal bool TryReadRetainedSlice(string startToken, string endToken, out string slice)
    {
        if (string.IsNullOrEmpty(startToken) || string.IsNullOrEmpty(endToken))
        {
            slice = string.Empty;
            return false;
        }

        try
        {
            lock (gate)
            {
                var paths = GetRetainedPathsCore();
                for (var startFileIndex = paths.Count - 1; startFileIndex >= 0; startFileIndex--)
                {
                    if (!File.Exists(paths[startFileIndex]))
                    {
                        continue;
                    }

                    var startFileText = ReadSharedTextCore(paths[startFileIndex]);
                    var startTokenIndex = startFileText.IndexOf(startToken, StringComparison.Ordinal);
                    if (startTokenIndex < 0)
                    {
                        continue;
                    }

                    var builder = new StringBuilder();
                    for (var fileIndex = startFileIndex; fileIndex < paths.Count; fileIndex++)
                    {
                        if (!File.Exists(paths[fileIndex]))
                        {
                            slice = string.Empty;
                            return false;
                        }

                        if (fileIndex > startFileIndex)
                        {
                            RetainedSliceReadBarrierForTests?.Invoke(fileIndex);
                        }

                        var text = fileIndex == startFileIndex
                            ? startFileText
                            : ReadSharedTextCore(paths[fileIndex]);
                        var segmentStart = fileIndex == startFileIndex
                            ? GetLineEnd(text, startTokenIndex)
                            : 0;
                        var endTokenIndex = text.IndexOf(endToken, segmentStart, StringComparison.Ordinal);
                        if (endTokenIndex >= 0)
                        {
                            var segmentEnd = GetLineStart(text, endTokenIndex);
                            if (segmentEnd > segmentStart)
                            {
                                builder.Append(text.AsSpan(segmentStart, segmentEnd - segmentStart));
                            }

                            slice = builder.ToString();
                            return true;
                        }

                        if (segmentStart < text.Length)
                        {
                            builder.Append(text.AsSpan(segmentStart));
                        }
                    }

                    slice = string.Empty;
                    return false;
                }
            }
        }
        catch
        {
            // Retained diagnostics are best effort and must never affect runtime behavior.
        }

        slice = string.Empty;
        return false;
    }

    internal bool TryReadRetainedTail(string startToken, out string tail)
    {
        if (string.IsNullOrEmpty(startToken))
        {
            tail = string.Empty;
            return false;
        }

        try
        {
            lock (gate)
            {
                var paths = GetRetainedPathsCore();
                for (var startFileIndex = paths.Count - 1; startFileIndex >= 0; startFileIndex--)
                {
                    if (!File.Exists(paths[startFileIndex]))
                    {
                        continue;
                    }

                    var startFileText = ReadSharedTextCore(paths[startFileIndex]);
                    var startTokenIndex = startFileText.LastIndexOf(startToken, StringComparison.Ordinal);
                    if (startTokenIndex < 0)
                    {
                        continue;
                    }

                    var builder = new StringBuilder();
                    for (var fileIndex = startFileIndex; fileIndex < paths.Count; fileIndex++)
                    {
                        if (!File.Exists(paths[fileIndex]))
                        {
                            tail = string.Empty;
                            return false;
                        }

                        if (fileIndex > startFileIndex)
                        {
                            RetainedSliceReadBarrierForTests?.Invoke(fileIndex);
                        }

                        var text = fileIndex == startFileIndex
                            ? startFileText
                            : ReadSharedTextCore(paths[fileIndex]);
                        var segmentStart = fileIndex == startFileIndex
                            ? GetLineEnd(text, startTokenIndex)
                            : 0;
                        if (segmentStart < text.Length)
                        {
                            builder.Append(text.AsSpan(segmentStart));
                        }
                    }

                    tail = builder.ToString();
                    return true;
                }
            }
        }
        catch
        {
            // Retained diagnostics are best effort and must never affect runtime behavior.
        }

        tail = string.Empty;
        return false;
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

    private List<string> GetRetainedPathsCore()
    {
        var paths = new List<string>(retainedFileCount);
        for (var index = retainedFileCount - 1; index >= 1; index--)
        {
            paths.Add(GetRotatedPath(index));
        }

        paths.Add(logFilePath);

        return paths;
    }

    private static string ReadSharedTextCore(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static int GetLineEnd(string text, int index)
    {
        var lineFeed = text.IndexOf('\n', index);
        return lineFeed < 0 ? text.Length : lineFeed + 1;
    }

    private static int GetLineStart(string text, int index)
    {
        if (index <= 0)
        {
            return 0;
        }

        var lineFeed = text.LastIndexOf('\n', index - 1);
        return lineFeed < 0 ? 0 : lineFeed + 1;
    }
}
