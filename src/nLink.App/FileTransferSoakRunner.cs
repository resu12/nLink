using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NLink.App.Services;
using NLink.App.Services.ScreenCapture;
using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.DevLocal;

namespace NLink.App;

internal static class FileTransferSoakRunner
{
    private const string LocalFastMode = "local-fast";
    private const string LocalImpairedMode = "local-impaired";
    private const string LocalMixedMode = "local-mixed";
    private const string DefaultMode = LocalFastMode;
    private const int DefaultSeed = 1_313_625_684;
    private const int DefaultCycleTimeoutSeconds = 120;
    private static readonly TimeSpan MixedScreenShareWarmup = TimeSpan.FromSeconds(3);
    private static readonly long[] DefaultPayloadSizes = [1L * 1024 * 1024, 16L * 1024 * 1024, 64L * 1024 * 1024];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    internal sealed record FileTransferSoakRunnerOptions(
        string Mode,
        long[] PayloadSizes,
        int Cycles,
        string Direction,
        int Seed,
        string ImpairmentProfile,
        string PayloadEfficiencyProfile,
        string? ArtifactDir,
        int CycleTimeoutSeconds,
        bool KeepReceivedFiles,
        bool FailOnGate);

    private sealed record FileTransferSoakCycleResult(
        int CycleIndex,
        string Direction,
        string TransferId,
        string FileName,
        long PayloadSizeBytes,
        string ExpectedSha256Base64,
        string? ActualSha256Base64,
        long SenderBytesTransferred,
        long ReceiverBytesTransferred,
        int SenderChunksTransferred,
        int ReceiverChunksTransferred,
        string SenderTerminalState,
        string ReceiverTerminalState,
        string SenderErrorCode,
        string ReceiverErrorCode,
        string? SavedPath,
        long SavedFileSizeBytes,
        double DurationMs,
        double GoodputBytesPerSecond,
        bool IntegrityOk,
        bool Completed,
        string FailureReason,
        string ImpairmentProfile,
        string PayloadEfficiencyProfile,
        long ImpairmentDelayCount,
        long ImpairmentDropCount,
        long ImpairmentReorderCount,
        long ScreenShareFramesEmitted,
        long ScreenShareMediaFramesDelayed,
        long ScreenShareMediaFramesDropped);

    private sealed record FileTransferSoakAggregate(
        string Mode,
        int Seed,
        int CyclesRequested,
        int CyclesCompleted,
        long TotalPayloadBytes,
        double AverageGoodputBytesPerSecond,
        double MinimumGoodputBytesPerSecond,
        int DataProtocolVersion,
        int V4ChunkBatchFrameCount,
        int V4MixedEnabledCount,
        int ReorderEventCount,
        int RequestTimeoutCount,
        int RetryRequestedCount,
        int PayloadRejectedCount,
        int DecodeFailureCount,
        int MessageRejectedCount,
        int BridgeBulkSendFailureCount,
        int BridgeBulkQueueClearCount,
        string ImpairmentProfile,
        string PayloadEfficiencyProfile,
        long ImpairmentDelayCount,
        long ImpairmentDropCount,
        long ImpairmentReorderCount,
        long ScreenShareFramesEmitted,
        long ScreenShareMediaFramesDelayed,
        long ScreenShareMediaFramesDropped,
        string Verdict,
        string FailureReason);

    private sealed record FileTransferSoakOutput(
        string Version,
        DateTimeOffset StartedUtc,
        DateTimeOffset CompletedUtc,
        FileTransferSoakRunnerOptions Options,
        FileTransferSoakAggregate Aggregate,
        FileTransferSoakCycleResult[] Cycles);

    private readonly record struct LogSliceSnapshot(string Path, long Length);

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (!TryParseOptions(args, out var options, out var parseError))
        {
            await error.WriteLineAsync($"FAIL: {parseError}").ConfigureAwait(false);
            return 1;
        }

        if (!IsSupportedMode(options!.Mode))
        {
            await error.WriteLineAsync($"FAIL: unsupported file-transfer soak mode '{options.Mode}'.").ConfigureAwait(false);
            return 1;
        }

        var startedUtc = DateTimeOffset.UtcNow;
        var artifactDir = ResolveArtifactDir(options.ArtifactDir, startedUtc);
        Directory.CreateDirectory(artifactDir);

        var runId = $"filetransfer_soak_run_{startedUtc:yyyyMMddHHmmss}_{Environment.ProcessId}";
        var initialLogSnapshot = CaptureLogSnapshot();
        var cycleResults = new List<FileTransferSoakCycleResult>(options.Cycles);
        var runFailure = string.Empty;

        await output.WriteLineAsync("File-transfer soak runner").ConfigureAwait(false);
        await output.WriteLineAsync($"  Mode: {options.Mode}").ConfigureAwait(false);
        await output.WriteLineAsync($"  Cycles: {options.Cycles}").ConfigureAwait(false);
        await output.WriteLineAsync($"  Payload sizes: {string.Join(",", options.PayloadSizes.Select(FormatPayloadSize))}").ConfigureAwait(false);
        await output.WriteLineAsync($"  Direction: {options.Direction}").ConfigureAwait(false);
        await output.WriteLineAsync($"  Seed: {options.Seed}").ConfigureAwait(false);
        await output.WriteLineAsync($"  Impairment profile: {options.ImpairmentProfile}").ConfigureAwait(false);
        await output.WriteLineAsync($"  Payload efficiency profile: {options.PayloadEfficiencyProfile}").ConfigureAwait(false);
        await output.WriteLineAsync($"  Artifact dir: {artifactDir}").ConfigureAwait(false);
        LocalOperationalLog.Info(
            "FileTransferSoak",
            $"event=filetransfer_local_soak_started; run_id={runId}; mode={options.Mode}; cycles={options.Cycles}; payload_sizes={string.Join(",", options.PayloadSizes)}; direction={options.Direction}; seed={options.Seed}; impairment_profile={options.ImpairmentProfile}; payload_efficiency_profile={options.PayloadEfficiencyProfile}; artifact_dir={artifactDir}");

        var payloadEfficiencyRestore = SetPayloadEfficiencyProfileEnvironment(options);
        try
        {
            for (var cycleIndex = 0; cycleIndex < options.Cycles; cycleIndex++)
            {
                ct.ThrowIfCancellationRequested();
                var payloadSize = options.PayloadSizes[cycleIndex % options.PayloadSizes.Length];
                var direction = ResolveCycleDirection(options.Direction, cycleIndex);
                var cycleResult = await RunCycleAsync(options, runId, cycleIndex, payloadSize, direction, output, ct).ConfigureAwait(false);
                cycleResults.Add(cycleResult);
                await output.WriteLineAsync(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "  Cycle {0}: direction={1} size={2} completed={3} goodput_bps={4:F0} integrity={5}",
                        cycleIndex,
                        direction,
                        payloadSize,
                        cycleResult.Completed ? 1 : 0,
                        cycleResult.GoodputBytesPerSecond,
                        cycleResult.IntegrityOk ? 1 : 0)).ConfigureAwait(false);

                if (!cycleResult.Completed || !cycleResult.IntegrityOk)
                {
                    runFailure = cycleResult.FailureReason;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            runFailure = "cycle timeout";
            LocalOperationalLog.Warn("FileTransferSoak", $"event=filetransfer_local_soak_failed; run_id={runId}; reason=cycle_timeout");
        }
        catch (Exception ex)
        {
            runFailure = $"{ex.GetType().Name}: {ex.Message}";
            LocalOperationalLog.Warn(
                "FileTransferSoak",
                $"event=filetransfer_local_soak_failed; run_id={runId}; reason={ex.GetType().Name}; message={ex.Message}");
        }
        finally
        {
            RestorePayloadEfficiencyProfileEnvironment(payloadEfficiencyRestore);
        }

        var logSlice = ReadLogSlice(initialLogSnapshot);
        var logSlicePath = Path.Combine(artifactDir, "filetransfer-retained-log-slice.log");
        await File.WriteAllTextAsync(logSlicePath, logSlice, Encoding.UTF8, CancellationToken.None).ConfigureAwait(false);

        var logMetrics = ExtractLogMetrics(logSlice);
        var aggregate = BuildAggregate(options, cycleResults, logMetrics, runFailure);
        var completedUtc = DateTimeOffset.UtcNow;
        var payload = new FileTransferSoakOutput(
            Version: ResolveVersion(),
            StartedUtc: startedUtc,
            CompletedUtc: completedUtc,
            Options: options with { ArtifactDir = artifactDir },
            Aggregate: aggregate,
            Cycles: cycleResults.ToArray());

        await WriteArtifactsAsync(artifactDir, payload, logSlicePath, CancellationToken.None).ConfigureAwait(false);

        await output.WriteLineAsync("").ConfigureAwait(false);
        await output.WriteLineAsync("File-transfer soak summary").ConfigureAwait(false);
        await output.WriteLineAsync($"  Verdict: {aggregate.Verdict}").ConfigureAwait(false);
        await output.WriteLineAsync($"  Cycles completed: {aggregate.CyclesCompleted}/{aggregate.CyclesRequested}").ConfigureAwait(false);
        await output.WriteLineAsync($"  Average goodput (bytes/s): {aggregate.AverageGoodputBytesPerSecond:F0}").ConfigureAwait(false);
        await output.WriteLineAsync($"  Artifact dir: {artifactDir}").ConfigureAwait(false);
        LocalOperationalLog.Info(
            "FileTransferSoak",
            $"event=filetransfer_local_soak_completed; run_id={runId}; verdict={aggregate.Verdict}; cycles_completed={aggregate.CyclesCompleted}; cycles_requested={aggregate.CyclesRequested}; average_goodput_bytes_per_second={aggregate.AverageGoodputBytesPerSecond.ToString("F3", CultureInfo.InvariantCulture)}; artifact_dir={artifactDir}");

        return aggregate.Verdict == "PASS" ? 0 : 1;
    }

    internal static bool TryParseOptionsForTests(string[] args, out FileTransferSoakRunnerOptions? options, out string error)
        => TryParseOptions(args, out options, out error);

    internal static Stream CreatePayloadStreamForTests(long sizeBytes, int seed, int cycleIndex)
        => new DeterministicPayloadStream(sizeBytes, seed, cycleIndex);

    internal static async Task<string> ComputeSha256Base64ForTestsAsync(long sizeBytes, int seed, int cycleIndex, CancellationToken ct = default)
        => await ComputeSha256Base64Async(() => new DeterministicPayloadStream(sizeBytes, seed, cycleIndex), ct).ConfigureAwait(false);

    private static bool TryParseOptions(string[] args, out FileTransferSoakRunnerOptions? options, out string error)
    {
        options = null;
        error = string.Empty;

        var mode = DefaultMode;
        var payloadSizes = DefaultPayloadSizes.ToArray();
        int? cycles = null;
        var direction = "alternate";
        var seed = DefaultSeed;
        string? impairmentProfile = null;
        var payloadEfficiencyProfile = FileTransferPayloadEfficiencyProfile.Current.Name;
        string? artifactDir = null;
        var cycleTimeoutSeconds = DefaultCycleTimeoutSeconds;
        var keepReceivedFiles = false;
        var failOnGate = false;
        var consumedBareMode = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (!consumedBareMode && IsSupportedMode(arg))
                {
                    mode = arg.Trim().ToLowerInvariant();
                    consumedBareMode = true;
                    continue;
                }

                error = $"Unexpected argument '{arg}'.";
                return false;
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
                if (key is "--filetransfer-soak" or "--keep-received-files" or "--fail-on-gate")
                {
                    value = null;
                }
                else if (i + 1 < args.Length)
                {
                    value = args[++i];
                }
            }

            switch (key.ToLowerInvariant())
            {
                case "--filetransfer-soak":
                    break;
                case "--mode":
                    mode = (value ?? string.Empty).Trim().ToLowerInvariant();
                    if (!IsSupportedMode(mode))
                    {
                        error = "Invalid --mode value. Use local-fast, local-impaired, or local-mixed.";
                        return false;
                    }
                    break;
                case "--impairment-profile":
                    impairmentProfile = (value ?? string.Empty).Trim();
                    if (!TryParseImpairmentProfile(impairmentProfile, out _))
                    {
                        error = "Invalid --impairment-profile value. Use None, DelayJitter, ReorderBurst, LossBurst, or ScreenSharePressure.";
                        return false;
                    }
                    break;
                case "--payload-efficiency-profile":
                    payloadEfficiencyProfile = (value ?? string.Empty).Trim();
                    if (!FileTransferPayloadEfficiencyProfile.TryParse(payloadEfficiencyProfile, out _))
                    {
                        error = "Invalid --payload-efficiency-profile value. Use Current, Packed3x20KiB, Packed3x21KiB, or LargeSingle48KiB.";
                        return false;
                    }
                    break;
                case "--payload-sizes":
                    if (!TryParsePayloadSizes(value, out payloadSizes, out error))
                    {
                        return false;
                    }
                    break;
                case "--cycles":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCycles) || parsedCycles <= 0)
                    {
                        error = "Invalid --cycles value.";
                        return false;
                    }
                    cycles = parsedCycles;
                    break;
                case "--direction":
                    direction = (value ?? string.Empty).Trim().ToLowerInvariant();
                    if (direction is not ("alternate" or "helper-to-helpee" or "helpee-to-helper"))
                    {
                        error = "Invalid --direction value. Use alternate, helper-to-helpee, or helpee-to-helper.";
                        return false;
                    }
                    break;
                case "--seed":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out seed))
                    {
                        error = "Invalid --seed value.";
                        return false;
                    }
                    break;
                case "--artifact-dir":
                    artifactDir = value;
                    break;
                case "--cycle-timeout-seconds":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out cycleTimeoutSeconds) || cycleTimeoutSeconds <= 0)
                    {
                        error = "Invalid --cycle-timeout-seconds value.";
                        return false;
                    }
                    break;
                case "--keep-received-files":
                    keepReceivedFiles = string.IsNullOrWhiteSpace(value) || !bool.TryParse(value, out var keepValue) || keepValue;
                    break;
                case "--fail-on-gate":
                    failOnGate = string.IsNullOrWhiteSpace(value) || !bool.TryParse(value, out var failValue) || failValue;
                    break;
                default:
                    error = $"Unknown file-transfer soak option '{key}'.";
                    return false;
            }
        }

        impairmentProfile = ResolveDefaultImpairmentProfile(mode, impairmentProfile);
        if (string.Equals(mode, LocalFastMode, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(impairmentProfile, nameof(DevLocalImpairmentProfile.None), StringComparison.OrdinalIgnoreCase))
        {
            error = "local-fast only supports --impairment-profile None.";
            return false;
        }

        options = new FileTransferSoakRunnerOptions(
            mode,
            payloadSizes,
            cycles ?? payloadSizes.Length,
            direction,
            seed,
            impairmentProfile,
            FileTransferPayloadEfficiencyProfile.TryParse(payloadEfficiencyProfile, out var parsedPayloadEfficiencyProfile)
                ? parsedPayloadEfficiencyProfile.Name
                : FileTransferPayloadEfficiencyProfile.Current.Name,
            artifactDir,
            cycleTimeoutSeconds,
            keepReceivedFiles,
            failOnGate);
        return true;
    }

    private static bool TryParsePayloadSizes(string? value, out long[] sizes, out string error)
    {
        sizes = [];
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Invalid --payload-sizes value.";
            return false;
        }

        var parsed = new List<long>();
        foreach (var rawPart in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParsePayloadSize(rawPart, out var size) || size <= 0)
            {
                error = $"Invalid payload size '{rawPart}'.";
                return false;
            }

            parsed.Add(size);
        }

        if (parsed.Count == 0)
        {
            error = "Invalid --payload-sizes value.";
            return false;
        }

        sizes = parsed.ToArray();
        return true;
    }

    private static (string? Profile, string? AllowScreenShare) SetPayloadEfficiencyProfileEnvironment(FileTransferSoakRunnerOptions options)
    {
        var previousProfile = Environment.GetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName);
        var previousAllowScreenShare = Environment.GetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.AllowScreenShareEnvironmentVariableName);

        Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, options.PayloadEfficiencyProfile);
        Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.AllowScreenShareEnvironmentVariableName, null);

        return (previousProfile, previousAllowScreenShare);
    }

    private static void RestorePayloadEfficiencyProfileEnvironment((string? Profile, string? AllowScreenShare) restore)
    {
        Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, restore.Profile);
        Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.AllowScreenShareEnvironmentVariableName, restore.AllowScreenShare);
    }

    private static bool TryParsePayloadSize(string value, out long size)
    {
        size = 0;
        var text = value.Trim();
        var multiplier = 1L;
        foreach (var suffix in new[] { "kib", "kb", "mib", "mb", "b" })
        {
            if (!text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var numberText = text[..^suffix.Length];
            multiplier = suffix.ToLowerInvariant() switch
            {
                "kib" or "kb" => 1024L,
                "mib" or "mb" => 1024L * 1024L,
                _ => 1L,
            };
            return long.TryParse(numberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var valuePart) &&
                   TryMultiply(valuePart, multiplier, out size);
        }

        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
    }

    private static bool TryMultiply(long value, long multiplier, out long result)
    {
        try
        {
            result = checked(value * multiplier);
            return true;
        }
        catch (OverflowException)
        {
            result = 0;
            return false;
        }
    }

    private static bool IsSupportedMode(string value)
        => string.Equals(value.Trim(), LocalFastMode, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value.Trim(), LocalImpairedMode, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value.Trim(), LocalMixedMode, StringComparison.OrdinalIgnoreCase);

    private static string ResolveDefaultImpairmentProfile(string mode, string? impairmentProfile)
    {
        if (!string.IsNullOrWhiteSpace(impairmentProfile))
        {
            TryParseImpairmentProfile(impairmentProfile, out var parsed);
            return parsed.ToString();
        }

        return mode switch
        {
            LocalImpairedMode => nameof(DevLocalImpairmentProfile.ReorderBurst),
            LocalMixedMode => nameof(DevLocalImpairmentProfile.ScreenSharePressure),
            _ => nameof(DevLocalImpairmentProfile.None),
        };
    }

    private static bool TryParseImpairmentProfile(string? value, out DevLocalImpairmentProfile profile)
    {
        if (Enum.TryParse(value, ignoreCase: true, out profile) &&
            Enum.IsDefined(typeof(DevLocalImpairmentProfile), profile))
        {
            return true;
        }

        profile = DevLocalImpairmentProfile.None;
        return false;
    }

    private static async Task<FileTransferSoakCycleResult> RunCycleAsync(
        FileTransferSoakRunnerOptions options,
        string runId,
        int cycleIndex,
        long payloadSize,
        string direction,
        TextWriter output,
        CancellationToken ct)
    {
        using var cycleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cycleCts.CancelAfter(TimeSpan.FromSeconds(options.CycleTimeoutSeconds));

        var transferId = $"filetransfer_soak_{options.Seed:x8}_{cycleIndex:D4}_{direction.Replace("-", "_", StringComparison.Ordinal)}";
        var fileName = $"filetransfer-soak-{cycleIndex:D4}.bin";
        LocalOperationalLog.Info(
            "FileTransferSoak",
            $"event=filetransfer_local_soak_cycle_started; run_id={runId}; cycle_index={cycleIndex}; transfer_id={transferId}; payload_bytes={payloadSize}; direction={direction}");

        var helpeeAddress = $"filetransfer.soak.helpee.{options.Seed:x8}.{Environment.ProcessId}.{cycleIndex:D4}";
        var helperAddress = $"filetransfer.soak.helper.{options.Seed:x8}.{Environment.ProcessId}.{cycleIndex:D4}";
        TryParseImpairmentProfile(options.ImpairmentProfile, out var impairmentProfile);
        var impairmentOptions = impairmentProfile == DevLocalImpairmentProfile.None
            ? null
            : new DevLocalImpairmentOptions(impairmentProfile, unchecked(options.Seed + cycleIndex));
        var isMixed = string.Equals(options.Mode, LocalMixedMode, StringComparison.OrdinalIgnoreCase);
        var helpeeTransport = new DevLocalTransport(helpeeAddress, impairmentOptions);
        var helperTransport = new DevLocalTransport(helperAddress, impairmentOptions);
        var syntheticScreenCaptureSource = isMixed
            ? new SyntheticScreenCaptureSource(options.Seed, cycleIndex)
            : null;
        using var helpee = isMixed
            ? new SessionRuntime(
                () => helpeeTransport,
                SessionRuntimeWatchdogOptions.Default,
                transportScreenCaptureSourceFactory: () => syntheticScreenCaptureSource!)
            : new SessionRuntime(() => helpeeTransport);
        using var helper = new SessionRuntime(() => helperTransport);

        await ConnectPairAsync(
                helpee,
                helper,
                helpeeAddress,
                helperAddress,
                cycleIndex,
                isMixed ? CapabilityGrant.Chat | CapabilityGrant.FileTransfer | CapabilityGrant.ScreenShare : CapabilityGrant.Chat | CapabilityGrant.FileTransfer,
                cycleCts.Token)
            .ConfigureAwait(false);

        if (isMixed)
        {
            await helpee.StartTransportScreenShareAsync(cycleCts.Token).ConfigureAwait(false);
            await WaitUntilAsync(
                    () => helpee.IsTransportScreenShareActiveForTests && syntheticScreenCaptureSource?.FramesEmitted > 0,
                    TimeSpan.FromSeconds(10),
                    cycleCts.Token)
                .ConfigureAwait(false);
            LocalOperationalLog.Info(
                "FileTransferSoak",
                $"event=filetransfer_local_mixed_screenshare_started; run_id={runId}; cycle_index={cycleIndex}; transfer_id={transferId}; profile={options.ImpairmentProfile}; warmup_ms={MixedScreenShareWarmup.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)}");
            await Task.Delay(MixedScreenShareWarmup, cycleCts.Token).ConfigureAwait(false);
        }

        var sender = direction == "helper-to-helpee" ? helper : helpee;
        var receiver = direction == "helper-to-helpee" ? helpee : helper;
        var expectedHash = await ComputeSha256Base64Async(
            () => new DeterministicPayloadStream(payloadSize, options.Seed, cycleIndex),
            cycleCts.Token).ConfigureAwait(false);

        var descriptor = new FileTransferSendDescriptor(fileName, payloadSize, transferId);
        var stopwatch = Stopwatch.StartNew();
        await sender.StartSendAsync(
            descriptor,
            _ => Task.FromResult<Stream>(new DeterministicPayloadStream(payloadSize, options.Seed, cycleIndex)),
            cycleCts.Token).ConfigureAwait(false);

        await WaitUntilAsync(
            () => receiver.FileTransferSnapshot.Inbound?.TransferId == transferId &&
                  receiver.FileTransferSnapshot.InboundState == FileTransferTransferState.PendingDecision,
            TimeSpan.FromSeconds(10),
            cycleCts.Token).ConfigureAwait(false);

        await receiver.AcceptIncomingAsync(transferId, cycleCts.Token).ConfigureAwait(false);
        await WaitUntilAsync(
            () => sender.FileTransferSnapshot.Outbound?.TransferId == transferId &&
                  receiver.FileTransferSnapshot.Inbound?.TransferId == transferId &&
                  sender.FileTransferSnapshot.OutboundState is FileTransferTransferState.Completed or FileTransferTransferState.Failed or FileTransferTransferState.Canceled &&
                  receiver.FileTransferSnapshot.InboundState is FileTransferTransferState.Completed or FileTransferTransferState.Failed or FileTransferTransferState.Canceled,
            TimeSpan.FromSeconds(options.CycleTimeoutSeconds),
            cycleCts.Token).ConfigureAwait(false);
        stopwatch.Stop();

        if (isMixed)
        {
            await helpee.StopTransportScreenShareAsync("filetransfer_local_mixed_complete", cycleCts.Token).ConfigureAwait(false);
        }

        var outbound = sender.FileTransferSnapshot.Outbound;
        var inbound = receiver.FileTransferSnapshot.Inbound;
        var savedPath = inbound?.SavedFilePath;
        var savedFileSize = !string.IsNullOrWhiteSpace(savedPath) && File.Exists(savedPath)
            ? new FileInfo(savedPath).Length
            : -1L;
        string? actualHash = null;
        if (!string.IsNullOrWhiteSpace(savedPath) && File.Exists(savedPath))
        {
            await using var savedStream = File.OpenRead(savedPath);
            actualHash = Convert.ToBase64String(await SHA256.HashDataAsync(savedStream, cycleCts.Token).ConfigureAwait(false));
        }
        var completed = outbound?.State == FileTransferTransferState.Completed &&
                        inbound?.State == FileTransferTransferState.Completed;
        var integrityOk = completed &&
                          outbound?.BytesTransferred == payloadSize &&
                          inbound?.BytesTransferred == payloadSize &&
                          savedFileSize == payloadSize &&
                          string.Equals(actualHash, expectedHash, StringComparison.Ordinal);
        var durationMs = Math.Max(1d, stopwatch.Elapsed.TotalMilliseconds);
        var goodput = payloadSize / stopwatch.Elapsed.TotalSeconds;
        var failureReason = BuildCycleFailureReason(completed, integrityOk, payloadSize, outbound, inbound, savedFileSize, expectedHash, actualHash);
        var impairmentMetrics = CombineImpairmentMetrics(
            helpeeTransport.GetImpairmentMetricsSnapshot(),
            helperTransport.GetImpairmentMetricsSnapshot());
        var screenShareFramesEmitted = syntheticScreenCaptureSource?.FramesEmitted ?? 0;

        if (!options.KeepReceivedFiles && !string.IsNullOrWhiteSpace(savedPath))
        {
            TryDeleteTransferDirectory(savedPath);
        }

        if (completed && integrityOk)
        {
            LocalOperationalLog.Info(
                "FileTransferSoak",
                $"event=filetransfer_local_soak_cycle_completed; run_id={runId}; cycle_index={cycleIndex}; transfer_id={transferId}; payload_bytes={payloadSize}; duration_ms={durationMs.ToString("F3", CultureInfo.InvariantCulture)}; goodput_bytes_per_second={goodput.ToString("F3", CultureInfo.InvariantCulture)}");
        }
        else
        {
            LocalOperationalLog.Warn(
                "FileTransferSoak",
                $"event=filetransfer_local_soak_cycle_failed; run_id={runId}; cycle_index={cycleIndex}; transfer_id={transferId}; payload_bytes={payloadSize}; reason={failureReason}");
        }

        LocalOperationalLog.Info(
            "FileTransferSoak",
            $"event=filetransfer_local_impairment_summary; run_id={runId}; cycle_index={cycleIndex}; transfer_id={transferId}; mode={options.Mode}; profile={options.ImpairmentProfile}; ft_observed={impairmentMetrics.FileTransferDataFramesObserved}; ft_delayed={impairmentMetrics.FileTransferDataFramesDelayed}; ft_dropped={impairmentMetrics.FileTransferDataFramesDropped}; ft_reordered={impairmentMetrics.FileTransferDataFramesReordered}; ss_observed={impairmentMetrics.ScreenShareMediaFramesObserved}; ss_delayed={impairmentMetrics.ScreenShareMediaFramesDelayed}; ss_dropped={impairmentMetrics.ScreenShareMediaFramesDropped}; total_delay_ms={impairmentMetrics.TotalDelayMilliseconds}; max_delay_ms={impairmentMetrics.MaxDelayMilliseconds}");
        if (isMixed)
        {
            LocalOperationalLog.Info(
                "FileTransferSoak",
                $"event=filetransfer_local_mixed_screenshare_summary; run_id={runId}; cycle_index={cycleIndex}; transfer_id={transferId}; profile={options.ImpairmentProfile}; frames_emitted={screenShareFramesEmitted}; media_observed={impairmentMetrics.ScreenShareMediaFramesObserved}; media_delayed={impairmentMetrics.ScreenShareMediaFramesDelayed}; media_dropped={impairmentMetrics.ScreenShareMediaFramesDropped}; filetransfer_completed={(completed ? 1 : 0)}; filetransfer_integrity_ok={(integrityOk ? 1 : 0)}");
        }

        await sender.ResetAsync().ConfigureAwait(false);
        await receiver.ResetAsync().ConfigureAwait(false);
        await output.WriteLineAsync($"    transfer_id={transferId} saved_path={(savedPath ?? "(none)")}") .ConfigureAwait(false);

        return new FileTransferSoakCycleResult(
            CycleIndex: cycleIndex,
            Direction: direction,
            TransferId: transferId,
            FileName: fileName,
            PayloadSizeBytes: payloadSize,
            ExpectedSha256Base64: expectedHash,
            ActualSha256Base64: actualHash,
            SenderBytesTransferred: outbound?.BytesTransferred ?? -1L,
            ReceiverBytesTransferred: inbound?.BytesTransferred ?? -1L,
            SenderChunksTransferred: outbound?.ChunksTransferred ?? -1,
            ReceiverChunksTransferred: inbound?.ChunksTransferred ?? -1,
            SenderTerminalState: outbound?.State.ToString() ?? "(none)",
            ReceiverTerminalState: inbound?.State.ToString() ?? "(none)",
            SenderErrorCode: outbound?.ErrorCode ?? "(none)",
            ReceiverErrorCode: inbound?.ErrorCode ?? "(none)",
            SavedPath: savedPath,
            SavedFileSizeBytes: savedFileSize,
            DurationMs: durationMs,
            GoodputBytesPerSecond: goodput,
            IntegrityOk: integrityOk,
            Completed: completed,
            FailureReason: failureReason,
            ImpairmentProfile: options.ImpairmentProfile,
            PayloadEfficiencyProfile: options.PayloadEfficiencyProfile,
            ImpairmentDelayCount: impairmentMetrics.DelayCount,
            ImpairmentDropCount: impairmentMetrics.DropCount,
            ImpairmentReorderCount: impairmentMetrics.ReorderCount,
            ScreenShareFramesEmitted: screenShareFramesEmitted,
            ScreenShareMediaFramesDelayed: impairmentMetrics.ScreenShareMediaFramesDelayed,
            ScreenShareMediaFramesDropped: impairmentMetrics.ScreenShareMediaFramesDropped);
    }

    private static async Task ConnectPairAsync(
        SessionRuntime helpee,
        SessionRuntime helper,
        string helpeeAddress,
        string helperAddress,
        int cycleIndex,
        CapabilityGrant grant,
        CancellationToken ct)
    {
        var incomingJoin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var helpeeConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var helperConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void IncomingJoinHandler(object? _, EventArgs __) => incomingJoin.TrySetResult();
        void HelpeeStateChanged(object? _, SessionRuntimeStateChangedEventArgs e)
        {
            if (e.State == SessionRuntimeState.Connected)
            {
                helpeeConnected.TrySetResult();
            }
        }
        void HelperStateChanged(object? _, SessionRuntimeStateChangedEventArgs e)
        {
            if (e.State == SessionRuntimeState.Connected)
            {
                helperConnected.TrySetResult();
            }
        }

        helpee.IncomingJoinRequestAvailable += IncomingJoinHandler;
        helpee.StateChanged += HelpeeStateChanged;
        helper.StateChanged += HelperStateChanged;
        try
        {
            await helpee.StartHelpeeAsync(ct).ConfigureAwait(false);
            var target = helpee.CurrentLocalPeerAddress ?? new PeerAddress(helpeeAddress);
            var boundHelper = new PeerAddress(helperAddress);
            var (token, invite) = CreateInviteForTarget(target, boundHelper, cycleIndex, grant);
            await helper.StartHelperAsync(token, invite, ct).ConfigureAwait(false);
            await incomingJoin.Task.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            await helpee.ApproveAsync(grant, ct).ConfigureAwait(false);
            await Task.WhenAll(
                helpeeConnected.Task.WaitAsync(TimeSpan.FromSeconds(10), ct),
                helperConnected.Task.WaitAsync(TimeSpan.FromSeconds(10), ct)).ConfigureAwait(false);
        }
        finally
        {
            helpee.IncomingJoinRequestAvailable -= IncomingJoinHandler;
            helpee.StateChanged -= HelpeeStateChanged;
            helper.StateChanged -= HelperStateChanged;
        }
    }

    private static (string Token, ValidatedInviteV1 Invite) CreateInviteForTarget(
        PeerAddress targetAddress,
        PeerAddress boundHelperAddress,
        int cycleIndex,
        CapabilityGrant grant)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var factory = InviteTokenServiceFactory.CreateInviteTokenFactory();
        var create = factory.Create(
            new InviteTokenCreateRequest(
                IssuerAddress: targetAddress,
                TargetAddress: targetAddress,
                SessionId: new SessionId($"sess_filetransfer_soak_{cycleIndex:D4}_{Guid.NewGuid():N}"),
                Capabilities: MapInviteCapabilities(grant),
                Lifetime: TimeSpan.FromMinutes(5),
                BoundHelperAddress: boundHelperAddress),
            nowUtc);
        if (!create.IsSuccess || string.IsNullOrWhiteSpace(create.Token))
        {
            throw new InvalidOperationException(create.Message ?? "Failed to create file-transfer soak invite.");
        }

        var validator = InviteTokenServiceFactory.CreateInviteTokenValidator();
        var validation = validator.Validate(create.Token, nowUtc.AddSeconds(1));
        if (!validation.IsSuccess || validation.Invite is null)
        {
            throw new InvalidOperationException(validation.Message ?? "Failed to validate file-transfer soak invite.");
        }

        return (create.Token, validation.Invite);
    }

    private static InviteCapabilities MapInviteCapabilities(CapabilityGrant grant)
    {
        var capabilities = InviteCapabilities.None;
        if ((grant & CapabilityGrant.Chat) != 0)
        {
            capabilities |= InviteCapabilities.Chat;
        }
        if ((grant & CapabilityGrant.FileTransfer) != 0)
        {
            capabilities |= InviteCapabilities.FileTransfer;
        }
        if ((grant & CapabilityGrant.ScreenShare) != 0)
        {
            capabilities |= InviteCapabilities.ScreenShare;
        }
        if ((grant & CapabilityGrant.RemoteControl) != 0)
        {
            capabilities |= InviteCapabilities.RemoteControl;
        }

        return capabilities;
    }

    private static string ResolveCycleDirection(string direction, int cycleIndex)
        => direction switch
        {
            "helper-to-helpee" => "helper-to-helpee",
            "helpee-to-helper" => "helpee-to-helper",
            _ => cycleIndex % 2 == 0 ? "helper-to-helpee" : "helpee-to-helper",
        };

    private static string BuildCycleFailureReason(
        bool completed,
        bool integrityOk,
        long payloadSize,
        FileTransferTransferSnapshot? outbound,
        FileTransferTransferSnapshot? inbound,
        long savedFileSize,
        string expectedHash,
        string? actualHash)
    {
        if (!completed)
        {
            return $"terminal_not_completed sender={outbound?.State.ToString() ?? "(none)"} receiver={inbound?.State.ToString() ?? "(none)"} sender_error={outbound?.ErrorCode ?? "(none)"} receiver_error={inbound?.ErrorCode ?? "(none)"}";
        }

        if (integrityOk)
        {
            return string.Empty;
        }

        if (outbound?.BytesTransferred != payloadSize || inbound?.BytesTransferred != payloadSize)
        {
            return $"byte_mismatch expected={payloadSize} sender={outbound?.BytesTransferred ?? -1} receiver={inbound?.BytesTransferred ?? -1}";
        }

        if (savedFileSize != payloadSize)
        {
            return $"saved_file_size_mismatch expected={payloadSize} actual={savedFileSize}";
        }

        if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
        {
            return "sha256_mismatch";
        }

        return "integrity_unknown";
    }

    private static DevLocalImpairmentMetricsSnapshot CombineImpairmentMetrics(
        DevLocalImpairmentMetricsSnapshot first,
        DevLocalImpairmentMetricsSnapshot second)
        => new(
            first.Profile != DevLocalImpairmentProfile.None ? first.Profile : second.Profile,
            first.Seed != 0 ? first.Seed : second.Seed,
            first.FileTransferDataFramesObserved + second.FileTransferDataFramesObserved,
            first.FileTransferDataFramesDelayed + second.FileTransferDataFramesDelayed,
            first.FileTransferDataFramesDropped + second.FileTransferDataFramesDropped,
            first.FileTransferDataFramesReordered + second.FileTransferDataFramesReordered,
            first.ScreenShareMediaFramesObserved + second.ScreenShareMediaFramesObserved,
            first.ScreenShareMediaFramesDelayed + second.ScreenShareMediaFramesDelayed,
            first.ScreenShareMediaFramesDropped + second.ScreenShareMediaFramesDropped,
            first.DelayCount + second.DelayCount,
            first.DropCount + second.DropCount,
            first.ReorderCount + second.ReorderCount,
            first.TotalDelayMilliseconds + second.TotalDelayMilliseconds,
            Math.Max(first.MaxDelayMilliseconds, second.MaxDelayMilliseconds));

    private static FileTransferSoakAggregate BuildAggregate(
        FileTransferSoakRunnerOptions options,
        IReadOnlyList<FileTransferSoakCycleResult> cycles,
        LogMetrics logMetrics,
        string runFailure)
    {
        var completedCycles = cycles.Count(c => c.Completed && c.IntegrityOk);
        var totalBytes = cycles.Sum(c => Math.Max(0L, c.PayloadSizeBytes));
        var successfulGoodputs = cycles.Where(c => c.Completed && c.IntegrityOk).Select(c => c.GoodputBytesPerSecond).ToArray();
        var avgGoodput = successfulGoodputs.Length == 0 ? 0d : successfulGoodputs.Average();
        var minGoodput = successfulGoodputs.Length == 0 ? 0d : successfulGoodputs.Min();
        var impairmentDelayCount = cycles.Sum(static c => c.ImpairmentDelayCount);
        var impairmentDropCount = cycles.Sum(static c => c.ImpairmentDropCount);
        var impairmentReorderCount = cycles.Sum(static c => c.ImpairmentReorderCount);
        var screenShareFramesEmitted = cycles.Sum(static c => c.ScreenShareFramesEmitted);
        var screenShareMediaFramesDelayed = cycles.Sum(static c => c.ScreenShareMediaFramesDelayed);
        var screenShareMediaFramesDropped = cycles.Sum(static c => c.ScreenShareMediaFramesDropped);
        var dataProtocolVersion = logMetrics.V4NegotiatedCount > 0 || logMetrics.V4ChunkBatchFrameCount > 0
            ? FileTransferProtocol.ProtocolVersionV4
            : 0;
        var hardLogFailure = logMetrics.PayloadRejectedCount > 0 ||
                             logMetrics.DecodeFailureCount > 0 ||
                             logMetrics.MessageRejectedCount > 0 ||
                             logMetrics.BridgeBulkSendFailureCount > 0 ||
                             logMetrics.BridgeBulkQueueClearCount > 0;
        var verdict = completedCycles == options.Cycles && string.IsNullOrWhiteSpace(runFailure) && !hardLogFailure
            ? "PASS"
            : "FAIL_PROTOCOL_OR_INTEGRITY";
        var failure = !string.IsNullOrWhiteSpace(runFailure)
            ? runFailure
            : hardLogFailure
                ? "hard_failure_event_in_log_slice"
                : string.Empty;

        return new FileTransferSoakAggregate(
            options.Mode,
            options.Seed,
            options.Cycles,
            completedCycles,
            totalBytes,
            avgGoodput,
            minGoodput,
            dataProtocolVersion,
            logMetrics.V4ChunkBatchFrameCount,
            logMetrics.V4MixedEnabledCount,
            logMetrics.ReorderEventCount,
            logMetrics.RequestTimeoutCount,
            logMetrics.RetryRequestedCount,
            logMetrics.PayloadRejectedCount,
            logMetrics.DecodeFailureCount,
            logMetrics.MessageRejectedCount,
            logMetrics.BridgeBulkSendFailureCount,
            logMetrics.BridgeBulkQueueClearCount,
            options.ImpairmentProfile,
            options.PayloadEfficiencyProfile,
            impairmentDelayCount,
            impairmentDropCount,
            impairmentReorderCount,
            screenShareFramesEmitted,
            screenShareMediaFramesDelayed,
            screenShareMediaFramesDropped,
            verdict,
            failure);
    }

    private static async Task WriteArtifactsAsync(
        string artifactDir,
        FileTransferSoakOutput output,
        string logSlicePath,
        CancellationToken ct)
    {
        Directory.CreateDirectory(artifactDir);
        await File.WriteAllTextAsync(
            Path.Combine(artifactDir, "filetransfer-local-soak-summary.json"),
            JsonSerializer.Serialize(output, JsonOptions),
            Encoding.UTF8,
            ct).ConfigureAwait(false);

        await using (var jsonl = new StreamWriter(Path.Combine(artifactDir, "filetransfer-local-soak-cycles.jsonl"), false, Encoding.UTF8))
        {
            foreach (var cycle in output.Cycles)
            {
                await jsonl.WriteLineAsync(JsonSerializer.Serialize(cycle, JsonOptions)).ConfigureAwait(false);
            }
        }

        var lines = BuildLocalSummaryLines(output, logSlicePath);
        await File.WriteAllLinesAsync(Path.Combine(artifactDir, "filetransfer-local-soak-summary.txt"), lines, Encoding.UTF8, ct).ConfigureAwait(false);
        await File.WriteAllLinesAsync(Path.Combine(artifactDir, "filetransfer-impairment-summary.txt"), BuildImpairmentSummaryLines(output), Encoding.UTF8, ct).ConfigureAwait(false);
        await File.WriteAllLinesAsync(Path.Combine(artifactDir, "mixed-screenshare-summary.txt"), BuildMixedScreenShareSummaryLines(output), Encoding.UTF8, ct).ConfigureAwait(false);
        await File.WriteAllLinesAsync(Path.Combine(artifactDir, "baseline-comparison.txt"), ["safe_baseline_available=0", "strong_baseline_available=0"], Encoding.UTF8, ct).ConfigureAwait(false);
        WriteMinimalPhaseOneArtifacts(artifactDir, output, logSlicePath);
    }

    private static string[] BuildLocalSummaryLines(FileTransferSoakOutput output, string logSlicePath)
    {
        var aggregate = output.Aggregate;
        return
        [
            $"verdict={aggregate.Verdict}",
            $"mode={aggregate.Mode}",
            $"seed={aggregate.Seed}",
            $"impairment_profile={aggregate.ImpairmentProfile}",
            $"payload_efficiency_profile={aggregate.PayloadEfficiencyProfile}",
            $"cycles_requested={aggregate.CyclesRequested}",
            $"cycles_completed={aggregate.CyclesCompleted}",
            $"total_payload_bytes={aggregate.TotalPayloadBytes}",
            $"average_goodput_bytes_per_second={aggregate.AverageGoodputBytesPerSecond.ToString("F3", CultureInfo.InvariantCulture)}",
            $"min_goodput_bytes_per_second={aggregate.MinimumGoodputBytesPerSecond.ToString("F3", CultureInfo.InvariantCulture)}",
            $"data_protocol_version={(aggregate.DataProtocolVersion == 0 ? "(unknown)" : aggregate.DataProtocolVersion.ToString(CultureInfo.InvariantCulture))}",
            $"v4_chunk_batch_frame_count={aggregate.V4ChunkBatchFrameCount}",
            $"v4_mixed_enabled_count={aggregate.V4MixedEnabledCount}",
            $"reorder_event_count={aggregate.ReorderEventCount}",
            $"request_timeout_count={aggregate.RequestTimeoutCount}",
            $"retry_requested_count={aggregate.RetryRequestedCount}",
            $"payload_rejected_count={aggregate.PayloadRejectedCount}",
            $"decode_failure_count={aggregate.DecodeFailureCount}",
            $"message_rejected_count={aggregate.MessageRejectedCount}",
            $"bridge_bulk_send_failure_count={aggregate.BridgeBulkSendFailureCount}",
            $"bridge_bulk_queue_clear_count={aggregate.BridgeBulkQueueClearCount}",
            $"impairment_delay_count={aggregate.ImpairmentDelayCount}",
            $"impairment_drop_count={aggregate.ImpairmentDropCount}",
            $"impairment_reorder_count={aggregate.ImpairmentReorderCount}",
            $"screen_share_frames_emitted={aggregate.ScreenShareFramesEmitted}",
            $"screen_share_media_delayed_count={aggregate.ScreenShareMediaFramesDelayed}",
            $"screen_share_media_dropped_count={aggregate.ScreenShareMediaFramesDropped}",
            $"failure_reason={(string.IsNullOrWhiteSpace(aggregate.FailureReason) ? "(none)" : aggregate.FailureReason)}",
            $"retained_log_slice={logSlicePath}",
        ];
    }

    private static string[] BuildImpairmentSummaryLines(FileTransferSoakOutput output)
    {
        var aggregate = output.Aggregate;
        return
        [
            $"mode={aggregate.Mode}",
            $"impairment_profile={aggregate.ImpairmentProfile}",
            $"impairment_delay_count={aggregate.ImpairmentDelayCount}",
            $"impairment_drop_count={aggregate.ImpairmentDropCount}",
            $"impairment_reorder_count={aggregate.ImpairmentReorderCount}",
            $"screen_share_media_delayed_count={aggregate.ScreenShareMediaFramesDelayed}",
            $"screen_share_media_dropped_count={aggregate.ScreenShareMediaFramesDropped}",
            $"verdict={aggregate.Verdict}",
        ];
    }

    private static string[] BuildMixedScreenShareSummaryLines(FileTransferSoakOutput output)
    {
        var aggregate = output.Aggregate;
        var exercised = string.Equals(aggregate.Mode, LocalMixedMode, StringComparison.OrdinalIgnoreCase);
        return
        [
            $"mode={aggregate.Mode}",
            $"mixed_screenshare_exercised={(exercised ? 1 : 0)}",
            $"impairment_profile={aggregate.ImpairmentProfile}",
            $"screen_share_frames_emitted={aggregate.ScreenShareFramesEmitted}",
            $"screen_share_media_delayed_count={aggregate.ScreenShareMediaFramesDelayed}",
            $"screen_share_media_dropped_count={aggregate.ScreenShareMediaFramesDropped}",
            $"data_protocol_version={(aggregate.DataProtocolVersion == 0 ? "(unknown)" : aggregate.DataProtocolVersion.ToString(CultureInfo.InvariantCulture))}",
            $"v4_chunk_batch_frame_count={aggregate.V4ChunkBatchFrameCount}",
            $"v4_mixed_enabled_count={aggregate.V4MixedEnabledCount}",
            $"cycles_completed={aggregate.CyclesCompleted}",
            $"cycles_requested={aggregate.CyclesRequested}",
            $"verdict={aggregate.Verdict}",
        ];
    }

    private static void WriteMinimalPhaseOneArtifacts(string artifactDir, FileTransferSoakOutput output, string logSlicePath)
    {
        var aggregate = output.Aggregate;
        var verdictLines = new[]
        {
            $"verdict={aggregate.Verdict}",
            $"gate_status={(aggregate.Verdict == "PASS" ? "pass" : "fail")}",
            $"transfer_id=({aggregate.Mode})",
            $"next_artifact={(aggregate.Verdict == "PASS" ? "transfer-terminal-summary.txt" : "stability-gates-summary.txt")}",
            $"observed_start_utc={output.StartedUtc:u}",
            $"observed_end_utc={output.CompletedUtc:u}",
            $"analyzed_files={logSlicePath}",
            $"hard_failure_count={(aggregate.Verdict == "PASS" ? 0 : 1)}",
            "warning_count=0",
            "",
            "hard_failures:",
            aggregate.Verdict == "PASS" ? "(none)" : aggregate.FailureReason,
            "",
            "warnings:",
            "(none)",
            "",
            "top_evidence:",
            $"{aggregate.Mode} app runner artifact; PowerShell local soak modes rewrite this with retained-log analysis",
        };

        WriteText(artifactDir, "filetransfer-operator-verdict.txt", verdictLines);
        WriteText(artifactDir, "transfer-terminal-summary.txt", output.Cycles.SelectMany(c => new[]
        {
            $"cycle.{c.CycleIndex}.transfer_id={c.TransferId}",
            $"cycle.{c.CycleIndex}.sender_state={c.SenderTerminalState}",
            $"cycle.{c.CycleIndex}.receiver_state={c.ReceiverTerminalState}",
            $"cycle.{c.CycleIndex}.sender_error_code={c.SenderErrorCode}",
            $"cycle.{c.CycleIndex}.receiver_error_code={c.ReceiverErrorCode}",
        }).ToArray());
        WriteText(artifactDir, "throughput-summary.txt", BuildLocalSummaryLines(output, logSlicePath));
        WriteText(artifactDir, "protocol-shape-summary.txt", BuildLocalSummaryLines(output, logSlicePath));
        WriteText(artifactDir, "repair-reorder-summary.txt", BuildLocalSummaryLines(output, logSlicePath));
        WriteText(artifactDir, "transport-budget-summary.txt", BuildLocalSummaryLines(output, logSlicePath));
        WriteText(artifactDir, "bridge-bulk-summary.txt", BuildLocalSummaryLines(output, logSlicePath));
        WriteText(
            artifactDir,
            "coexistence-summary.txt",
            string.Equals(aggregate.Mode, LocalMixedMode, StringComparison.OrdinalIgnoreCase)
                ? BuildMixedScreenShareSummaryLines(output)
                : ["coexistence_evidence=(not exercised in local soak mode)"]);
        WriteText(artifactDir, "external-transport-health-summary.txt", ["external_transport_evidence=(not exercised in local soak mode)"]);
        WriteText(artifactDir, "stability-gates-summary.txt", verdictLines);
    }

    private static void WriteText(string artifactDir, string fileName, string[] lines)
        => File.WriteAllLines(Path.Combine(artifactDir, fileName), lines, Encoding.UTF8);

    private static string ResolveArtifactDir(string? requested, DateTimeOffset startedUtc)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return Path.GetFullPath(requested);
        }

        return Path.Combine(
            Directory.GetCurrentDirectory(),
            "artifacts",
            "filetransfer-soak",
            startedUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
    }

    private static LogSliceSnapshot CaptureLogSnapshot()
    {
        var path = LocalOperationalLog.LogFilePath;
        if (!File.Exists(path))
        {
            return new LogSliceSnapshot(path, 0);
        }

        return new LogSliceSnapshot(path, new FileInfo(path).Length);
    }

    private static string ReadLogSlice(LogSliceSnapshot snapshot)
    {
        if (!File.Exists(snapshot.Path))
        {
            return string.Empty;
        }

        using var stream = new FileStream(snapshot.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (snapshot.Length > 0 && snapshot.Length < stream.Length)
        {
            stream.Seek(snapshot.Length, SeekOrigin.Begin);
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static LogMetrics ExtractLogMetrics(string logText)
    {
        var metrics = new LogMetrics();
        using var reader = new StringReader(logText);
        while (reader.ReadLine() is { } line)
        {
            if (line.Contains("event=filetransfer_v4_negotiated", StringComparison.Ordinal))
            {
                metrics.V4NegotiatedCount++;
            }
            if (line.Contains("event=filetransfer_v4_chunk_batch_sent", StringComparison.Ordinal))
            {
                metrics.V4ChunkBatchFrameCount++;
            }
            if (line.Contains("event=filetransfer_v4_mixed_enabled", StringComparison.Ordinal))
            {
                metrics.V4MixedEnabledCount++;
            }

            if (line.Contains("event=filetransfer_reorder_pressure", StringComparison.Ordinal))
            {
                metrics.ReorderEventCount++;
            }
            if (line.Contains("event=filetransfer_request_timeout_detected", StringComparison.Ordinal))
            {
                metrics.RequestTimeoutCount++;
            }
            if (line.Contains("event=filetransfer_chunk_retry_requested", StringComparison.Ordinal))
            {
                metrics.RetryRequestedCount++;
            }
            if (line.Contains("event=filetransfer_transport_payload_rejected", StringComparison.Ordinal))
            {
                metrics.PayloadRejectedCount++;
            }
            if (line.Contains("event=filetransfer_data_frame_decode_failed", StringComparison.Ordinal))
            {
                metrics.DecodeFailureCount++;
            }
            if (line.Contains("event=filetransfer_message_rejected", StringComparison.Ordinal))
            {
                metrics.MessageRejectedCount++;
            }
            if (line.Contains("event=nkn_bridge_bulk_send_summary", StringComparison.Ordinal) &&
                !line.Contains("send_failures=0", StringComparison.Ordinal))
            {
                metrics.BridgeBulkSendFailureCount++;
            }
            if ((line.Contains("event=nkn_bridge_bulk_send_summary", StringComparison.Ordinal) &&
                 !line.Contains("queue_clears=0", StringComparison.Ordinal)) ||
                (line.Contains("event=nkn_bridge_bulk_queue_state", StringComparison.Ordinal) &&
                 !line.Contains("cleared_since_last=0", StringComparison.Ordinal)))
            {
                metrics.BridgeBulkQueueClearCount++;
            }
        }

        return metrics;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        while (!condition())
        {
            await Task.Delay(25, timeoutCts.Token).ConfigureAwait(false);
        }
    }

    private static async Task<string> ComputeSha256Base64Async(Func<Stream> openStream, CancellationToken ct)
    {
        await using var stream = openStream();
        return Convert.ToBase64String(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    private static void TryDeleteTransferDirectory(string savedPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(savedPath);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Soak cleanup is best-effort; the artifact records the saved path.
        }
    }

    private static string FormatPayloadSize(long size)
        => size % (1024 * 1024) == 0
            ? $"{size / (1024 * 1024)}MiB"
            : size % 1024 == 0
                ? $"{size / 1024}KiB"
                : $"{size}B";

    private static string ResolveVersion()
    {
        try
        {
            var versionPath = Path.Combine(AppContext.BaseDirectory, "VERSION");
            if (File.Exists(versionPath))
            {
                return File.ReadAllText(versionPath).Trim();
            }
        }
        catch
        {
        }

        return typeof(FileTransferSoakRunner).Assembly.GetName().Version?.ToString() ?? "(unknown)";
    }

    private sealed class LogMetrics
    {
        public int V4NegotiatedCount { get; set; }
        public int V4ChunkBatchFrameCount { get; set; }
        public int V4MixedEnabledCount { get; set; }
        public int ReorderEventCount { get; set; }
        public int RequestTimeoutCount { get; set; }
        public int RetryRequestedCount { get; set; }
        public int PayloadRejectedCount { get; set; }
        public int DecodeFailureCount { get; set; }
        public int MessageRejectedCount { get; set; }
        public int BridgeBulkSendFailureCount { get; set; }
        public int BridgeBulkQueueClearCount { get; set; }
    }

    private sealed class SyntheticScreenCaptureSource : IScreenCaptureSource, IScreenCaptureMetadataSource, IScreenCaptureKeyFrameRequestSource, IScreenCaptureCursorCaptureControl, IAsyncDisposable
    {
        private const int Width = 1280;
        private const int Height = 720;
        private const int PayloadBytes = 8 * 1024;
        private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(125);
        private readonly int seed;
        private readonly int cycleIndex;
        private readonly long streamEpoch;
        private CancellationTokenSource? cts;
        private Task? loopTask;
        private long framesEmitted;
        private int started;
        private int pendingKeyFrameRequest;
        private bool cursorCaptureEnabled = true;

        public SyntheticScreenCaptureSource(int seed, int cycleIndex)
        {
            this.seed = seed;
            this.cycleIndex = cycleIndex;
            streamEpoch = Math.Max(1, Math.Abs((long)seed) + cycleIndex + 1);
        }

        public bool IsSupported => true;

        public bool IsCursorCaptureControlSupported => true;

        public bool IsCursorCaptureEnabled => cursorCaptureEnabled;

        public long FramesEmitted => Interlocked.Read(ref framesEmitted);

        public event EventHandler<ScreenCaptureFrameEventArgs>? FrameArrived;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref started, 1) != 0)
            {
                return Task.CompletedTask;
            }

            cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            loopTask = Task.Run(() => EmitLoopAsync(cts.Token), CancellationToken.None);
            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            if (Interlocked.Exchange(ref started, 0) == 0)
            {
                return;
            }

            try
            {
                cts?.Cancel();
                if (loopTask is not null)
                {
                    await loopTask.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                cts?.Dispose();
                cts = null;
                loopTask = null;
            }
        }

        public void RequestKeyFrame(string reason)
        {
            _ = reason;
            Interlocked.Exchange(ref pendingKeyFrameRequest, 1);
        }

        public bool TrySetCursorCaptureEnabled(bool enabled, string reason)
        {
            _ = reason;
            cursorCaptureEnabled = enabled;
            return true;
        }

        public bool TryGetCaptureMetadata(out ScreenCaptureMetadata metadata)
        {
            metadata = new ScreenCaptureMetadata(
                DisplayId: $"synthetic-filetransfer-soak-{seed:x8}-{cycleIndex:D4}",
                CaptureRegionPx: new ScreenCapturePixelRect(0, 0, Width, Height),
                DpiScale: 1d);
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            FrameArrived = null;
        }

        private async Task EmitLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var frameId = Interlocked.Increment(ref framesEmitted);
                var isKeyFrame = frameId == 1 ||
                                 frameId % 30 == 0 ||
                                 Interlocked.Exchange(ref pendingKeyFrameRequest, 0) != 0;
                var payload = CreateFramePayload(frameId, isKeyFrame);
                var streamConfig = isKeyFrame
                    ? new ScreenShareVideoStreamConfigV1
                    {
                        StreamEpoch = streamEpoch,
                        Encoding = "h264",
                        CodecProfile = "synthetic",
                        DecoderConfigData = [0x01, 0x42, 0x00, 0x1f],
                    }
                    : null;

                FrameArrived?.Invoke(
                    this,
                    new ScreenCaptureFrameEventArgs(
                        Width,
                        Height,
                        payload,
                        "h264",
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        isKeyFrame,
                        streamEpoch,
                        streamConfig));
                await Task.Delay(FrameInterval, ct).ConfigureAwait(false);
            }
        }

        private byte[] CreateFramePayload(long frameId, bool isKeyFrame)
        {
            var payload = new byte[PayloadBytes];
            var state = unchecked((uint)(seed ^ (cycleIndex * 397) ^ (int)frameId));
            for (var index = 0; index < payload.Length; index++)
            {
                state = unchecked((state * 1_664_525u) + 1_013_904_223u);
                payload[index] = (byte)(state >> 24);
            }

            payload[0] = 0x00;
            payload[1] = 0x00;
            payload[2] = 0x00;
            payload[3] = 0x01;
            payload[4] = isKeyFrame ? (byte)0x65 : (byte)0x41;
            return payload;
        }
    }

    private sealed class DeterministicPayloadStream : Stream
    {
        private readonly long length;
        private readonly int seed;
        private readonly int cycleIndex;
        private long position;

        public DeterministicPayloadStream(long length, int seed, int cycleIndex)
        {
            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            this.length = length;
            this.seed = seed;
            this.cycleIndex = cycleIndex;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position
        {
            get => position;
            set
            {
                if (value < 0 || value > length)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            if (position >= length)
            {
                return 0;
            }

            var toRead = (int)Math.Min(buffer.Length, length - position);
            for (var i = 0; i < toRead; i++)
            {
                buffer[i] = ComputeByte(position + i, seed, cycleIndex);
            }

            position += toRead;
            return toRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var next = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => position + offset,
                SeekOrigin.End => length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            Position = next;
            return position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private static byte ComputeByte(long absolutePosition, int seed, int cycleIndex)
        {
            unchecked
            {
                var x = (ulong)absolutePosition;
                x += ((ulong)(uint)seed << 32) ^ (uint)cycleIndex;
                x += 0x9E3779B97F4A7C15UL;
                x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
                x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
                x ^= x >> 31;
                return (byte)x;
            }
        }
    }
}
