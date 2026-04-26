using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using NLink.Core.Logging;

namespace NLink.App.Services.ScreenCapture;

[SupportedOSPlatform("windows")]
internal static class WindowsH264BitmapDecoderFactory
{
    private static string debugLastSelectedBackend = "unknown";
    private static string debugLastFallbackReason = "(none)";
    private static string debugLastFfmpegInitializationFailure = "unchecked";
    private static string debugLastFfmpegSearchPaths = "(none)";
    private static string debugLastFfmpegSelectedLibraryPath = "(none)";

    internal static string DebugLastSelectedBackend => debugLastSelectedBackend;

    internal static string DebugLastFallbackReason => debugLastFallbackReason;

    internal static string DebugLastFfmpegInitializationFailure => debugLastFfmpegInitializationFailure;

    internal static string DebugLastFfmpegSearchPaths => debugLastFfmpegSearchPaths;

    internal static string DebugLastFfmpegSelectedLibraryPath => debugLastFfmpegSelectedLibraryPath;

    internal static void ResetDebugState()
    {
        debugLastSelectedBackend = "unknown";
        debugLastFallbackReason = "(none)";
        debugLastFfmpegInitializationFailure = "unchecked";
        debugLastFfmpegSearchPaths = "(none)";
        debugLastFfmpegSelectedLibraryPath = "(none)";
    }

    public static IWindowsH264BitmapDecoder? TryCreate(string logRole = "viewer")
    {
        return TryCreate(logRole, FfmpegH264BitmapDecoder.TryCreate, MediaFoundationH264BitmapDecoder.TryCreate);
    }

    internal static IWindowsH264BitmapDecoder? TryCreate(
        string logRole,
        Func<string, IWindowsH264BitmapDecoder?> ffmpegFactory,
        Func<string, IWindowsH264BitmapDecoder?> mediaFoundationFactory,
        Func<FfmpegRuntimeDiagnostics>? ffmpegDiagnosticsProvider = null)
    {
        ArgumentNullException.ThrowIfNull(ffmpegFactory);
        ArgumentNullException.ThrowIfNull(mediaFoundationFactory);

        var ffmpegDecoder = ffmpegFactory(logRole);
        var ffmpegDiagnostics = ffmpegDiagnosticsProvider?.Invoke() ?? GetFfmpegRuntimeDiagnostics();
        UpdateFfmpegDiagnostics(ffmpegDiagnostics);
        if (ffmpegDecoder is not null && ffmpegDecoder.IsSupported)
        {
            debugLastSelectedBackend = "ffmpeg_software";
            debugLastFallbackReason = "(none)";
            Log("screenshare_h264_decoder_backend_selected", logRole, BuildBackendDetails("ffmpeg_software", "(none)", ffmpegDiagnostics));
            return ffmpegDecoder;
        }

        ffmpegDecoder?.Dispose();

        var fallbackReason = "ffmpeg_backend_unavailable";
        debugLastFallbackReason = fallbackReason;
        Log("screenshare_h264_decoder_backend_fallback", logRole, BuildBackendDetails("media_foundation", fallbackReason, ffmpegDiagnostics));

        var mediaFoundationDecoder = mediaFoundationFactory(logRole);
        if (mediaFoundationDecoder is not null && mediaFoundationDecoder.IsSupported)
        {
            debugLastSelectedBackend = "media_foundation";
            Log("screenshare_h264_decoder_backend_selected", logRole, BuildBackendDetails("media_foundation", fallbackReason, ffmpegDiagnostics));
            return mediaFoundationDecoder;
        }

        mediaFoundationDecoder?.Dispose();
        debugLastSelectedBackend = "unknown";
        Log("screenshare_h264_decoder_backend_unavailable", logRole, BuildBackendDetails("unknown", fallbackReason, ffmpegDiagnostics));
        return null;
    }

    private static FfmpegRuntimeDiagnostics GetFfmpegRuntimeDiagnostics()
    {
        return new FfmpegRuntimeDiagnostics(
            InitializationSucceeded: string.Equals(FfmpegH264BitmapDecoder.DebugNativeInitializationFailure, "none", StringComparison.Ordinal),
            InitializationFailure: FfmpegH264BitmapDecoder.DebugNativeInitializationFailure,
            SearchPaths: FfmpegH264BitmapDecoder.DebugNativeSearchPaths,
            SelectedLibraryPath: FfmpegH264BitmapDecoder.DebugNativeLibrariesPath);
    }

    private static void UpdateFfmpegDiagnostics(FfmpegRuntimeDiagnostics diagnostics)
    {
        debugLastFfmpegInitializationFailure = diagnostics.InitializationFailure;
        debugLastFfmpegSearchPaths = diagnostics.SearchPaths;
        debugLastFfmpegSelectedLibraryPath = diagnostics.SelectedLibraryPath;
    }

    private static string BuildBackendDetails(string backend, string fallbackReason, FfmpegRuntimeDiagnostics diagnostics)
    {
        return
            $"backend={Sanitize(backend)}; fallback_reason={Sanitize(fallbackReason)}; " +
            $"ffmpeg_init_failure={Sanitize(diagnostics.InitializationFailure)}; " +
            $"ffmpeg_search_paths={Sanitize(diagnostics.SearchPaths)}; " +
            $"ffmpeg_selected_library_path={Sanitize(diagnostics.SelectedLibraryPath)}";
    }

    private static void Log(string eventName, string role, string details)
    {
        LocalOperationalLog.Info("ScreenShareTransport", $"event={eventName}; role={Sanitize(role)}; {details}");
        WriteDebugTrace($"[WindowsH264BitmapDecoderFactory] {eventName}: role={role} {details}");
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(none)";
        }

        return value
            .Replace(';', ',')
            .Replace(Environment.NewLine, "|")
            .Replace("\r", "|")
            .Replace("\n", "|")
            .Trim();
    }

    [Conditional("DEBUG")]
    private static void WriteDebugTrace(string message)
    {
        Trace.WriteLine(message);
    }
}
