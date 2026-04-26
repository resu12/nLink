using System;
using System.IO;
using System.Linq;
using FFmpeg.AutoGen;

namespace NLink.App.Services.ScreenCapture;

internal static class WindowsFfmpegRuntime
{
    private static readonly object NativeInitSync = new();
    private static bool initializationAttempted;
    private static bool initializationSucceeded;
    private static string initializationFailure = "unchecked";
    private static string searchPaths = "(none)";
    private static string librariesPath = "(none)";

    internal static string DebugNativeLibrariesPath => librariesPath;

    internal static string DebugNativeInitializationFailure => initializationFailure;

    internal static string DebugNativeSearchPaths => searchPaths;

    public static bool TryInitialize()
    {
        lock (NativeInitSync)
        {
            if (initializationAttempted)
            {
                return initializationSucceeded;
            }

            initializationAttempted = true;
            return TryInitializeCore(GetLibrarySearchPaths(AppContext.BaseDirectory));
        }
    }

    internal static FfmpegRuntimeDiagnostics DebugProbeInitialization(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        lock (NativeInitSync)
        {
            var originalAttempted = initializationAttempted;
            var originalSucceeded = initializationSucceeded;
            var originalFailure = initializationFailure;
            var originalSearchPaths = searchPaths;
            var originalLibrariesPath = librariesPath;
            var originalRootPath = ffmpeg.RootPath;
            try
            {
                initializationAttempted = true;
                _ = TryInitializeCore(GetLibrarySearchPaths(baseDirectory));
                return new FfmpegRuntimeDiagnostics(initializationSucceeded, initializationFailure, searchPaths, librariesPath);
            }
            finally
            {
                initializationAttempted = originalAttempted;
                initializationSucceeded = originalSucceeded;
                initializationFailure = originalFailure;
                searchPaths = originalSearchPaths;
                librariesPath = originalLibrariesPath;
                ffmpeg.RootPath = originalRootPath;
            }
        }
    }

    private static bool TryInitializeCore(string[] candidates)
    {
        searchPaths = FormatSearchPaths(candidates);
        librariesPath = "(none)";

        try
        {
            foreach (var candidate in candidates)
            {
                if (!Directory.Exists(candidate))
                {
                    continue;
                }

                if (!Directory.EnumerateFiles(candidate, "avcodec*.dll", SearchOption.TopDirectoryOnly).Any())
                {
                    continue;
                }

                ffmpeg.RootPath = candidate;
                _ = ffmpeg.avcodec_version();
                initializationSucceeded = true;
                librariesPath = candidate;
                initializationFailure = "none";
                return true;
            }

            initializationSucceeded = false;
            initializationFailure = "ffmpeg_dlls_not_found";
            return false;
        }
        catch (Exception ex)
        {
            initializationSucceeded = false;
            initializationFailure = $"{ex.GetType().Name}:0x{ex.HResult:X8}";
            return false;
        }
    }

    private static string[] GetLibrarySearchPaths(string baseDirectory)
    {
        return
        [
            baseDirectory,
            Path.Combine(baseDirectory, "runtimes", "win-x64", "native"),
            Path.Combine(baseDirectory, "ffmpeg"),
        ];
    }

    private static string FormatSearchPaths(string[] paths)
    {
        var normalized = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return normalized.Length == 0 ? "(none)" : string.Join("|", normalized);
    }
}

internal readonly record struct FfmpegRuntimeDiagnostics(
    bool InitializationSucceeded,
    string InitializationFailure,
    string SearchPaths,
    string SelectedLibraryPath);
