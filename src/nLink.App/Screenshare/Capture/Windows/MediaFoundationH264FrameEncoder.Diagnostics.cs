using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using NLink.App.Configuration;
using NLink.Core.Logging;

namespace NLink.App.Services.ScreenCapture;

[SupportedOSPlatform("windows")]
internal sealed partial class MediaFoundationH264FrameEncoder
{
    private static readonly object InputBufferProbeSync = new();
    private static int nextEncoderInstanceId;
    private static int inputBufferProbeExecutionCount;
    private static int preservedDebugMp4Count;
    private static RawInputBufferStrategy selectedInputBufferStrategy = RawInputBufferStrategy.CpuMemoryBufferNv12;
    private static string lastInputBufferProbeSummary = "status=not-run";
    private static bool inputBufferProbeCompleted;
    private static bool inputBufferProbeSucceeded;
    private static string lastInputBufferRootCause = "unknown";

    internal static int DebugInputBufferProbeExecutionCount => Volatile.Read(ref inputBufferProbeExecutionCount);

    internal static string DebugLastInputBufferProbeSummary
    {
        get
        {
            lock (InputBufferProbeSync)
            {
                return lastInputBufferProbeSummary;
            }
        }
    }

    internal static string DebugSelectedInputBufferStrategy
    {
        get
        {
            lock (InputBufferProbeSync)
            {
                return selectedInputBufferStrategy.ToString();
            }
        }
    }

    internal static string DebugLastInputBufferRootCause
    {
        get
        {
            lock (InputBufferProbeSync)
            {
                return lastInputBufferRootCause;
            }
        }
    }

    internal static void ResetDebugInputBufferProbeState()
    {
        lock (InputBufferProbeSync)
        {
            selectedInputBufferStrategy = RawInputBufferStrategy.CpuMemoryBufferNv12;
            lastInputBufferProbeSummary = "status=not-run";
            lastInputBufferRootCause = "unknown";
            inputBufferProbeCompleted = false;
            inputBufferProbeSucceeded = false;
            Volatile.Write(ref inputBufferProbeExecutionCount, 0);
        }
    }

    internal static byte[] DebugExtractAnnexBFromSingleSampleMp4(byte[] containerBytes, out byte[] decoderConfigData)
    {
        return ExtractAnnexBFromSingleSampleMp4(containerBytes, out decoderConfigData);
    }

    internal static bool DebugIsDisplayableAccessUnit(byte[] encodedBytes)
    {
        return AnalyzeAccessUnit(encodedBytes).HasDisplayableVcl;
    }

    internal static bool DebugIsIdrAccessUnit(byte[] encodedBytes)
    {
        return AnalyzeAccessUnit(encodedBytes).HasIdr;
    }

    internal static string DebugClassifyAccessUnitKind(byte[] encodedBytes)
    {
        return AnalyzeAccessUnit(encodedBytes).Kind;
    }

    private static string TryPreserveDebugMp4Container(byte[] containerBytes)
    {
        if (!FeatureFlags.ScreenShareDeepDiagnostics)
        {
            return string.Empty;
        }

        if (containerBytes.Length == 0)
        {
            return string.Empty;
        }

        if (Interlocked.Increment(ref preservedDebugMp4Count) > 2)
        {
            return string.Empty;
        }

        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "nlink-h264-debug");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"sink-writer-sample-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.mp4");
            File.WriteAllBytes(path, containerBytes);
            return path;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(none)";
        }

        return value.Replace(';', ',').Trim();
    }

    private static string HexPrefix(byte[] bytes, int maxBytes)
    {
        if (bytes.Length == 0)
        {
            return "(empty)";
        }

        var count = Math.Min(bytes.Length, Math.Max(0, maxBytes));
        var builder = new StringBuilder(count * 2);
        for (var i = 0; i < count; i++)
        {
            builder.Append(bytes[i].ToString("X2"));
        }

        if (count < bytes.Length)
        {
            builder.Append("...");
        }

        return builder.ToString();
    }

    private string GetLogContext()
    {
        return $"encoder_id={encoderInstanceId}; source_role={sourceRole}";
    }

    private void LogInstanceLifecycle(string eventName, string details)
    {
        LogLifecycle(eventName, $"{GetLogContext()}; {details}");
    }

    private static void LogLifecycle(string eventName, string details)
    {
        if (!ShouldLogLifecycleEvent(eventName))
        {
            return;
        }

        LocalOperationalLog.Info("ScreenShareTransport", $"event={eventName}; {details}");
        WriteDebugTrace($"[MediaFoundationH264FrameEncoder] {eventName}: {details}");
    }

    private static bool ShouldLogLifecycleEvent(string eventName)
    {
        if (FeatureFlags.ScreenShareDeepDiagnostics)
        {
            return true;
        }

        return eventName is "screenshare_h264_encoder_selected"
            or "screenshare_h264_encoder_path_selected"
            or "screenshare_h264_terminal_root_cause";
    }

    [Conditional("DEBUG")]
    private static void WriteDebugTrace(string message)
    {
        Trace.WriteLine(message);
    }
}
