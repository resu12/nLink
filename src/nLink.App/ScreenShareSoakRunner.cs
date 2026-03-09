using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Logging;
using Avalonia.Threading;
using NLink.App.Services.ScreenCapture;
using NLink.App.ViewModels;
using NLink.Core.ScreenShare;
using NLink.Infra.Nkn;

namespace NLink.App;

internal static class ScreenShareSoakRunner
{
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultSampleInterval = TimeSpan.FromSeconds(5);

    internal sealed record ScreenShareSoakRunnerOptions(
        TimeSpan Duration,
        TimeSpan SampleInterval);

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            await error.WriteLineAsync("FAIL: --screenshare-soak is only supported on Windows.");
            return 1;
        }

        if (!TryParseOptions(args, out var options, out var parseError))
        {
            await error.WriteLineAsync($"FAIL: {parseError}");
            return 1;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(options!.Duration + TimeSpan.FromSeconds(30));
        EnsureViewerDecodePlatformInitialized();

        var reassembler = new ScreenShareFrameReassembler();
        long framesSent = 0;
        long enqueueFailures = 0;
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: static () => new WindowsScreenCaptureSource(),
            sendPayloadAsync: (payload, _) =>
            {
                if (ScreenSharePayloadCodec.TryDeserialize(payload.Span, out var chunk))
                {
                    reassembler.OnChunk(chunk);
                    if (chunk.ChunkIndex == chunk.ChunkCount - 1)
                    {
                        Interlocked.Increment(ref framesSent);
                    }
                }

                return Task.CompletedTask;
            },
            sendDisplayInfoAsync: (_, _, _) => Task.CompletedTask,
            estimateBridgeBytes: payload => NknBridgePayloadAccounting.MeasureSendCommandJsonlBytes(
                destination: "screenshare-soak",
                payload.Span,
                commandId: "1"));
        using var viewer = new ScreenShareViewerViewModel(
            postToUiAsync: action =>
            {
                action();
                return Task.CompletedTask;
            });

        reassembler.FrameReady += (_, frame) => viewer.OnJpegFrame(
            frame.EncodedFrameBytes,
            frame.TimestampUnixMilliseconds);

        try
        {
            await output.WriteLineAsync("ScreenShare soak runner");
            await output.WriteLineAsync($"  Duration: {options.Duration}");
            await output.WriteLineAsync($"  Sample interval: {options.SampleInterval}");

            await coordinator.StartAsync("screenshare-soak", linkedCts.Token).ConfigureAwait(false);
            var startedAt = DateTimeOffset.UtcNow;
            var nextSampleAt = startedAt;

            while (DateTimeOffset.UtcNow - startedAt < options.Duration)
            {
                linkedCts.Token.ThrowIfCancellationRequested();
                await TryFlushUiAsync(linkedCts.Token).ConfigureAwait(false);

                var now = DateTimeOffset.UtcNow;
                if (now >= nextSampleAt)
                {
                    await output.WriteLineAsync(BuildMetricsLine(
                        elapsed: now - startedAt,
                        framesSent: Interlocked.Read(ref framesSent),
                        senderMetrics: coordinator.GetMetricsSnapshot(),
                        receiverMetrics: reassembler.GetMetricsSnapshot(),
                        viewerMetrics: viewer.GetMetricsSnapshot(),
                        enqueueFailures: Interlocked.Read(ref enqueueFailures)));
                    nextSampleAt = now + options.SampleInterval;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), linkedCts.Token).ConfigureAwait(false);
            }

            await coordinator.StopAsync(sendStopMessage: false, reason: "soak_complete", linkedCts.Token).ConfigureAwait(false);
            await TryFlushUiAsync(linkedCts.Token).ConfigureAwait(false);
            await WaitUntilAsync(
                condition: () => viewer.IsIdleForDiagnostics,
                timeout: TimeSpan.FromSeconds(5),
                pollInterval: TimeSpan.FromMilliseconds(50),
                failureMessage: "Viewer did not become idle after screenshare stop.",
                ct: linkedCts.Token).ConfigureAwait(false);

            var stableSnapshot = await WaitForStableMetricsAsync(
                getSnapshot: () => CreateStopSnapshot(coordinator, reassembler, viewer, framesSent, enqueueFailures),
                timeout: TimeSpan.FromSeconds(5),
                pollInterval: TimeSpan.FromMilliseconds(50),
                stablePolls: 5,
                ct: linkedCts.Token).ConfigureAwait(false);

            viewer.Clear();

            await output.WriteLineAsync("Final metrics");
            await output.WriteLineAsync(BuildMetricsLine(
                elapsed: options.Duration,
                framesSent: stableSnapshot.FramesSent,
                senderMetrics: stableSnapshot.SenderMetrics,
                receiverMetrics: stableSnapshot.ReceiverMetrics,
                viewerMetrics: stableSnapshot.ViewerMetrics,
                enqueueFailures: stableSnapshot.EnqueueFailures));
            await output.WriteLineAsync("Screenshare soak completed cleanly.");
            return 0;
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            await error.WriteLineAsync("FAIL: screenshare soak timed out.");
            return 1;
        }
        catch (Exception ex)
        {
            await error.WriteLineAsync($"FAIL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            await coordinator.StopAsync(sendStopMessage: false, reason: "soak_finalize", CancellationToken.None).ConfigureAwait(false);
        }
    }

    internal static bool TryParseOptionsForTests(string[] args, out ScreenShareSoakRunnerOptions? options, out string error)
        => TryParseOptions(args, out options, out error);

    private static void EnsureViewerDecodePlatformInitialized()
    {
        if (Application.Current is not null)
        {
            return;
        }

        AppBuilder.Configure<ScreenShareSoakApplication>()
            .UsePlatformDetect()
            .LogToTrace(LogEventLevel.Warning)
            .SetupWithoutStarting();
    }

    private static bool TryParseOptions(string[] args, out ScreenShareSoakRunnerOptions? options, out string error)
    {
        options = null;
        error = string.Empty;

        var duration = DefaultDuration;
        var sampleInterval = DefaultSampleInterval;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            string key;
            string? value = null;
            var eq = arg.IndexOf('=');
            if (eq > 0)
            {
                key = arg[..eq];
                value = arg[(eq + 1)..];
            }
            else
            {
                key = arg;
                if (key is "--screenshare-soak")
                {
                    continue;
                }

                if (i + 1 < args.Length)
                {
                    value = args[++i];
                }
            }

            switch (key.ToLowerInvariant())
            {
                case "--screenshare-soak":
                    break;
                case "--seconds":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSeconds) || parsedSeconds <= 0)
                    {
                        error = "Invalid --seconds value.";
                        return false;
                    }

                    duration = TimeSpan.FromSeconds(parsedSeconds);
                    break;
                case "--sample-interval-seconds":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedIntervalSeconds) || parsedIntervalSeconds <= 0)
                    {
                        error = "Invalid --sample-interval-seconds value.";
                        return false;
                    }

                    sampleInterval = TimeSpan.FromSeconds(parsedIntervalSeconds);
                    break;
            }
        }

        if (sampleInterval > duration)
        {
            sampleInterval = duration;
        }

        options = new ScreenShareSoakRunnerOptions(duration, sampleInterval);
        return true;
    }

    private static string BuildMetricsLine(
        TimeSpan elapsed,
        long framesSent,
        ScreenShareMetrics senderMetrics,
        ScreenShareMetrics receiverMetrics,
        ScreenShareMetrics viewerMetrics,
        long enqueueFailures)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "[{0:mm\\:ss}] FramesCaptured={1} FramesSent={2} FramesDropped={3} FramesDroppedByRateGate={4} " +
            "FramesDroppedByQueueEvict={5} FramesCompleted={6} DecodeErrors={7} EnqueueFailures={8} " +
            "DisplayInfoSends={9} AvgCaptureToEnqueueMs={10:F1} AvgEnqueueToSendMs={11:F1} AvgCaptureToSendMs={12:F1} LastCaptureToSendAgeMs={13} " +
            "AvgRawFrameBytes={14:F1} AvgSerializedChunkBytes={15:F1} AvgBridgeBytes={16:F1} " +
            "AvgRenderIntervalMs={17:F1} AvgCaptureToRenderMs={18:F1} StaleFrameRenders={19}",
            elapsed,
            senderMetrics.FramesCaptured,
            framesSent,
            senderMetrics.FramesDropped,
            senderMetrics.FramesDroppedByRateGate,
            senderMetrics.FramesDroppedByQueueEvict,
            receiverMetrics.FramesCompleted,
            viewerMetrics.DecodeErrors,
            enqueueFailures,
            senderMetrics.DisplayInfoSendCount,
            senderMetrics.AverageCaptureToEnqueueMs,
            senderMetrics.AverageEnqueueToSendMs,
            senderMetrics.AverageCaptureToSendMs,
            senderMetrics.LastCaptureToSendAgeMs,
            framesSent > 0 ? senderMetrics.RawFrameBytesSent / (double)framesSent : 0d,
            framesSent > 0 ? senderMetrics.SerializedChunkBytesSent / (double)framesSent : 0d,
            framesSent > 0 ? senderMetrics.BridgeBytesSent / (double)framesSent : 0d,
            viewerMetrics.AverageRenderIntervalMs,
            viewerMetrics.AverageCaptureToRenderMs,
            viewerMetrics.StaleFrameRenders);
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        TimeSpan pollInterval,
        string failureMessage,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await TryFlushUiAsync(ct).ConfigureAwait(false);
            if (condition())
            {
                return;
            }

            await Task.Delay(pollInterval, ct).ConfigureAwait(false);
        }

        await TryFlushUiAsync(ct).ConfigureAwait(false);
        if (!condition())
        {
            throw new TimeoutException(failureMessage);
        }
    }

    private static async Task<StopSnapshot> WaitForStableMetricsAsync(
        Func<StopSnapshot> getSnapshot,
        TimeSpan timeout,
        TimeSpan pollInterval,
        int stablePolls,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var stableCount = 0;
        StopSnapshot? previous = null;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await TryFlushUiAsync(ct).ConfigureAwait(false);
            var current = getSnapshot();
            if (previous is not null && current.Equals(previous))
            {
                stableCount++;
                if (stableCount >= stablePolls)
                {
                    return current;
                }
            }
            else
            {
                stableCount = 1;
            }

            previous = current;
            await Task.Delay(pollInterval, ct).ConfigureAwait(false);
        }

        throw new TimeoutException("Screenshare metrics did not stabilize after stop.");
    }

    private static Task TryFlushUiAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Best-effort only. The soak runner should keep polling even if the dispatcher is unavailable.
        }

        return Task.CompletedTask;
    }

    private static StopSnapshot CreateStopSnapshot(
        TransportScreenShareCoordinator coordinator,
        ScreenShareFrameReassembler reassembler,
        ScreenShareViewerViewModel viewer,
        long framesSent,
        long enqueueFailures)
    {
        return new StopSnapshot(
            SenderMetrics: coordinator.GetMetricsSnapshot(),
            ReceiverMetrics: reassembler.GetMetricsSnapshot(),
            ViewerMetrics: viewer.GetMetricsSnapshot(),
            FramesSent: Interlocked.Read(ref framesSent),
            EnqueueFailures: Interlocked.Read(ref enqueueFailures));
    }

    private sealed record StopSnapshot(
        ScreenShareMetrics SenderMetrics,
        ScreenShareMetrics ReceiverMetrics,
        ScreenShareMetrics ViewerMetrics,
        long FramesSent,
        long EnqueueFailures);

    private sealed class ScreenShareSoakApplication : Application
    {
        public override void Initialize()
        {
        }
    }
}
