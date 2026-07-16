using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NLink.Core.Logging;

public sealed class RollingFileLogger
{
    public const int DefaultRetainedFileCount = 20;
    private const int CrossProcessMutexTimeoutMilliseconds = 250;
    private static readonly ConcurrentDictionary<string, Mutex> CrossProcessMutexes =
        new(StringComparer.Ordinal);

    private readonly object gate = new();
    private readonly string logFilePath;
    private readonly string crossProcessMutexName;
    private readonly Mutex? crossProcessMutex;
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
        crossProcessMutexName = GetCrossProcessMutexName(this.logFilePath);
        try
        {
            crossProcessMutex = CrossProcessMutexes.GetOrAdd(
                crossProcessMutexName,
                static name => new Mutex(initiallyOwned: false, name));
        }
        catch
        {
            // Operational logging remains best effort even when the host cannot
            // create the cross-process synchronization primitive.
            crossProcessMutex = null;
        }
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

    internal string CrossProcessMutexNameForTests => crossProcessMutexName;

    public void WriteLine(string line)
        => _ = TryWriteLine(line);

    internal RollingFileWriteResult TryWriteLine(string line)
    {
        var text = line ?? string.Empty;
        try
        {
            lock (gate)
            {
                if (!TryAcquireCrossProcessMutex(out var mutexHeld))
                {
                    return RollingFileWriteResult.Failed("mutex_timeout");
                }

                try
                {
                    EnsureDirectoryExists();
                    RotateIfNeededCore();
                    File.AppendAllText(logFilePath, text + Environment.NewLine, Encoding.UTF8);
                    return RollingFileWriteResult.Succeeded;
                }
                finally
                {
                    ReleaseCrossProcessMutex(mutexHeld);
                }
            }
        }
        catch (Exception ex)
        {
            // Best-effort support logging must never throw.
            return RollingFileWriteResult.Failed(NormalizeFailureCode(ex));
        }
    }

    public void RotateIfNeeded()
    {
        try
        {
            lock (gate)
            {
                if (!TryAcquireCrossProcessMutex(out var mutexHeld))
                {
                    return;
                }

                try
                {
                    EnsureDirectoryExists();
                    RotateIfNeededCore();
                }
                finally
                {
                    ReleaseCrossProcessMutex(mutexHeld);
                }
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
                using var snapshot = TryOpenRetainedSnapshotCore();
                if (snapshot is null)
                {
                    slice = string.Empty;
                    return false;
                }

                for (var startFileIndex = snapshot.Count - 1; startFileIndex >= 0; startFileIndex--)
                {
                    if (snapshot[startFileIndex] is null)
                    {
                        continue;
                    }

                    var startFileText = ReadSharedTextCore(snapshot[startFileIndex]!);
                    var startTokenIndex = startFileText.IndexOf(startToken, StringComparison.Ordinal);
                    if (startTokenIndex < 0)
                    {
                        continue;
                    }

                    var builder = new StringBuilder();
                    for (var fileIndex = startFileIndex; fileIndex < snapshot.Count; fileIndex++)
                    {
                        if (snapshot[fileIndex] is null)
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
                            : ReadSharedTextCore(snapshot[fileIndex]!);
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
                using var snapshot = TryOpenRetainedSnapshotCore();
                if (snapshot is null)
                {
                    tail = string.Empty;
                    return false;
                }

                for (var startFileIndex = snapshot.Count - 1; startFileIndex >= 0; startFileIndex--)
                {
                    if (snapshot[startFileIndex] is null)
                    {
                        continue;
                    }

                    var startFileText = ReadSharedTextCore(snapshot[startFileIndex]!);
                    var startTokenIndex = startFileText.LastIndexOf(startToken, StringComparison.Ordinal);
                    if (startTokenIndex < 0)
                    {
                        continue;
                    }

                    var builder = new StringBuilder();
                    for (var fileIndex = startFileIndex; fileIndex < snapshot.Count; fileIndex++)
                    {
                        if (snapshot[fileIndex] is null)
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
                            : ReadSharedTextCore(snapshot[fileIndex]!);
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

    private RetainedLogSnapshot? TryOpenRetainedSnapshotCore()
    {
        if (!TryAcquireCrossProcessMutex(out var mutexHeld))
        {
            return null;
        }

        var streams = new List<FileStream?>(retainedFileCount);
        try
        {
            foreach (var path in GetRetainedPathsCore())
            {
                streams.Add(File.Exists(path)
                    ? new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete)
                    : null);
            }

            return new RetainedLogSnapshot(streams);
        }
        catch
        {
            foreach (var stream in streams)
            {
                stream?.Dispose();
            }

            throw;
        }
        finally
        {
            ReleaseCrossProcessMutex(mutexHeld);
        }
    }

    private bool TryAcquireCrossProcessMutex(out bool mutexHeld)
    {
        mutexHeld = false;
        if (crossProcessMutex is null)
        {
            return false;
        }

        try
        {
            mutexHeld = crossProcessMutex.WaitOne(CrossProcessMutexTimeoutMilliseconds);
            return mutexHeld;
        }
        catch (AbandonedMutexException)
        {
            mutexHeld = true;
            return true;
        }
    }

    private void ReleaseCrossProcessMutex(bool mutexHeld)
    {
        if (mutexHeld && crossProcessMutex is not null)
        {
            crossProcessMutex.ReleaseMutex();
        }
    }

    private static string GetCrossProcessMutexName(string path)
    {
        var normalizedPath = Path.GetFullPath(path)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (OperatingSystem.IsWindows())
        {
            normalizedPath = normalizedPath.ToUpperInvariant();
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
        return OperatingSystem.IsWindows()
            ? $@"Local\nLink.RollingFileLogger.{hash}"
            : $"nLink.RollingFileLogger.{hash}";
    }

    private static string NormalizeFailureCode(Exception exception)
        => exception switch
        {
            UnauthorizedAccessException => "access_denied",
            DirectoryNotFoundException => "directory_not_found",
            PathTooLongException => "path_too_long",
            IOException => "io_error",
            ArgumentException => "invalid_path",
            NotSupportedException => "unsupported_path",
            _ => "write_failed",
        };

    private static string ReadSharedTextCore(FileStream stream)
    {
        stream.Position = 0;
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);
        return reader.ReadToEnd();
    }

    private sealed class RetainedLogSnapshot(List<FileStream?> streams) : IDisposable
    {
        public int Count => streams.Count;

        public FileStream? this[int index] => streams[index];

        public void Dispose()
        {
            foreach (var stream in streams)
            {
                stream?.Dispose();
            }
        }
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

internal readonly record struct RollingFileWriteResult(bool Success, string FailureCode)
{
    public static RollingFileWriteResult Succeeded { get; } = new(true, "none");

    public static RollingFileWriteResult Failed(string failureCode)
        => new(false, failureCode);
}
