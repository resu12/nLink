using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using NLink.Core;
using NLink.Core.Configuration;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class TunaSidecarLiveManualTests
{
    private const string PaidPostTunaRepairOptInEnv = "NLINK_RUN_TUNA_PAID_POST_REPAIR_TEST";
    private const int PaidPostTunaRepairPayloadMiB = 64;
    private const int PaidPostTunaRepairStopAfterMiB = 8;

    [Trait("Category", "Manual")]
    [ManualBridgeFact]
    public async Task TunaSidecar_PaidPostTunaRepair_RealV4TransferCompletesAfterFallbackFrontierRepair()
    {
        if (!IsEnabled(PaidPostTunaRepairOptInEnv))
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var sidecarExe = Path.Combine(repoRoot, "artifacts", "tuna-sidecar", "nlink-tuna-sidecar.exe");
        var walletPath = Path.Combine(repoRoot, "artifacts", "tuna-poc", "wallet-test-nkn.json");
        var bridgeDir = TryFindBridgeBundleDirectory();
        var walletPassword = Environment.GetEnvironmentVariable(Phase3WalletPasswordEnv);
        var options = Phase3BenchmarkOptions.Load();
        var prerequisite = ValidatePhase3TunaPrerequisites(Phase3TransportMode.Tuna, sidecarExe, walletPath, walletPassword, options);

        Assert.True(File.Exists(sidecarExe), $"Missing Tuna sidecar: {sidecarExe}");
        Assert.True(File.Exists(walletPath), $"Missing Tuna test wallet: {Path.GetFileName(walletPath)}");
        Assert.True(bridgeDir is not null, "Bridge runtime not found. Build artifacts/bridge/win-x64 first.");
        Assert.True(prerequisite.IsValid, $"Paid post-Tuna repair prerequisites failed: {prerequisite.Reason}");

        var artifactDir = Path.Combine(
            repoRoot,
            "artifacts",
            "tuna-sidecar",
            "post-tuna-repair-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(artifactDir);
        var runsPath = Path.Combine(artifactDir, "runs.jsonl");
        var listenerStdout = new ConcurrentQueue<string>();
        var listenerStderr = new ConcurrentQueue<string>();
        var previousDeveloperMode = Environment.GetEnvironmentVariable(ReleaseOverridePolicy.UnsafeDeveloperModeEnvVar);
        var previousNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var previousBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var previousManualBridge = Environment.GetEnvironmentVariable("NLINK_RUN_MANUAL_BRIDGE");
        var logStart = GetOperationalLogLength();
        var startedAtUtc = DateTimeOffset.UtcNow;

        try
        {
            Environment.SetEnvironmentVariable(ReleaseOverridePolicy.UnsafeDeveloperModeEnvVar, "1");
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", Path.Combine(bridgeDir!, "node.exe"));
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", Path.Combine(bridgeDir!, "index.js"));
            Environment.SetEnvironmentVariable("NLINK_RUN_MANUAL_BRIDGE", "1");

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            await AppendPhase3EventAsync(
                runsPath,
                new
                {
                    @event = "paid_post_tuna_repair_start",
                    payloadMiB = PaidPostTunaRepairPayloadMiB,
                    stopAfterMiB = PaidPostTunaRepairStopAfterMiB,
                    walletFile = Path.GetFileName(walletPath),
                    bridgeRuntime = Path.GetFileName(bridgeDir!),
                    startedAtUtc,
                },
                cts.Token);

            using var context = await CreatePhase3LiveRunContextWithRetryAsync(
                Phase3TransportMode.Tuna,
                repeat: 1,
                options,
                sidecarExe,
                walletPath,
                walletPassword!,
                runsPath,
                listenerStdout,
                listenerStderr,
                cts.Token);

            var dropPolicy = new PostFallbackFrontierDropPolicy();
            using var senderTransport = new FaultInjectingFileTransferTransport(context.Helper, dropPolicy);
            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(context.Host);

            var transferId = "paid-post-tuna-repair-" + Guid.NewGuid().ToString("N")[..8];
            var payload = CreateDeterministicPayload(PaidPostTunaRepairPayloadMiB * 1024 * 1024);
            using var destination = new MemoryStream(capacity: payload.Length);
            var stopTask = StopTunaAndArmRepairDropAfterProgressAsync(
                context,
                sender,
                dropPolicy,
                PaidPostTunaRepairStopAfterMiB * 1024L * 1024L,
                runsPath,
                cts.Token);

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("paid-post-tuna-repair.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                cts.Token);
            await WaitUntilAsync(
                () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision,
                TimeSpan.FromSeconds(30));
            await receiver.AcceptIncomingTransferAsync(
                transferId,
                (_, _) => Task.FromResult<Stream>(destination),
                cts.Token);

            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                      receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
                TimeSpan.FromMinutes(4));
            await stopTask.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

            var logTail = ReadTunaSoakOperationalLogSlice(logStart, startedAtUtc);
            var destinationBytes = destination.ToArray();
            Assert.Equal(payload, destinationBytes[..payload.Length]);
            Assert.Equal(1, dropPolicy.DropCount);
            Assert.Contains("event=tuna_fallback_started", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_fallback_nkn_frame_sent; message_type=file_transfer_data_frame", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_fallback_nkn_frame_received; message_type=file_transfer_data_frame", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v4_frontier_repair_batch_received", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v4_frontier_repair_batch_applied", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v4_frontier_repair_frontier_advanced", logTail, StringComparison.Ordinal);

            await AppendPhase3EventAsync(
                runsPath,
                new
                {
                    @event = "paid_post_tuna_repair_summary",
                    completed = true,
                    transferId,
                    payloadBytes = payload.Length,
                    receivedBytes = destinationBytes.Length,
                    droppedStartChunkIndex = dropPolicy.DroppedStartChunkIndex,
                    droppedChunkCount = dropPolicy.DroppedChunkCount,
                    fallbackStarted = CountOccurrences(logTail, "event=tuna_fallback_started"),
                    fallbackFileSent = CountOccurrences(logTail, "event=tuna_fallback_nkn_frame_sent; message_type=file_transfer_data_frame"),
                    fallbackFileReceived = CountOccurrences(logTail, "event=tuna_fallback_nkn_frame_received; message_type=file_transfer_data_frame"),
                    repairBatchesReceived = CountOccurrences(logTail, "event=filetransfer_v4_frontier_repair_batch_received"),
                    repairBatchesApplied = CountOccurrences(logTail, "event=filetransfer_v4_frontier_repair_batch_applied"),
                    repairFrontierAdvanced = CountOccurrences(logTail, "event=filetransfer_v4_frontier_repair_frontier_advanced"),
                    endedAtUtc = DateTimeOffset.UtcNow,
                },
                CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ReleaseOverridePolicy.UnsafeDeveloperModeEnvVar, previousDeveloperMode);
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", previousNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", previousBridgePath);
            Environment.SetEnvironmentVariable("NLINK_RUN_MANUAL_BRIDGE", previousManualBridge);
            var logTail = ReadTunaSoakOperationalLogSlice(logStart, startedAtUtc);
            await File.WriteAllTextAsync(
                Path.Combine(artifactDir, "app-log-tail.redacted.log"),
                RedactPhase3ArtifactText(logTail, walletPath, walletPassword),
                CancellationToken.None);
            await File.WriteAllLinesAsync(
                Path.Combine(artifactDir, "listener.stdout.redacted.jsonl"),
                listenerStdout.Select(line => RedactPhase3ArtifactText(line, walletPath, walletPassword)),
                CancellationToken.None);
            await File.WriteAllLinesAsync(
                Path.Combine(artifactDir, "listener.stderr.redacted.log"),
                listenerStderr.Select(line => RedactPhase3ArtifactText(line, walletPath, walletPassword)),
                CancellationToken.None);
        }
    }

    private static async Task StopTunaAndArmRepairDropAfterProgressAsync(
        Phase3LiveRunContext context,
        SessionFileTransferService sender,
        PostFallbackFrontierDropPolicy dropPolicy,
        long stopAfterAcceptedBytes,
        string runsPath,
        CancellationToken ct)
    {
        await WaitUntilAsync(
            () => (sender.Snapshot.Outbound?.BytesAcceptedForTransport ?? 0) >= stopAfterAcceptedBytes,
            TimeSpan.FromSeconds(90));
        await AppendPhase3EventAsync(
            runsPath,
            new
            {
                @event = "paid_post_tuna_repair_stop_tuna",
                acceptedBytes = sender.Snapshot.Outbound?.BytesAcceptedForTransport ?? 0,
                atUtc = DateTimeOffset.UtcNow,
            },
            ct);
        await ((ITransportAccelerationControl)context.Host).StopAccelerationAsync("paid_post_tuna_repair", ct);
        dropPolicy.Arm();
    }

    private static byte[] CreateDeterministicPayload(int length)
    {
        var payload = new byte[length];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)((index * 31 + 17) % 251);
        }

        return payload;
    }

    private sealed class PostFallbackFrontierDropPolicy
    {
        private int armed;
        private int dropped;

        public int DropCount => Volatile.Read(ref dropped);

        public int DroppedStartChunkIndex { get; private set; } = -1;

        public int DroppedChunkCount { get; private set; }

        public void Arm()
            => Volatile.Write(ref armed, 1);

        public bool TryDrop(FileTransferDataFrame frame)
        {
            if (Volatile.Read(ref armed) == 0 ||
                frame is not FileTransferChunkBatchFrameV4 batch ||
                batch.ChunkCount <= 0 ||
                Interlocked.CompareExchange(ref dropped, 1, 0) != 0)
            {
                return false;
            }

            DroppedStartChunkIndex = batch.StartChunkIndex;
            DroppedChunkCount = batch.ChunkCount;
            LocalOperationalLog.Warn(
                "TunaPostRepairPaidTest",
                $"event=paid_post_tuna_repair_frame_dropped; transfer_id={batch.TransferId}; session_id={batch.SessionId}; start_chunk_index={batch.StartChunkIndex}; chunk_count={batch.ChunkCount}; repair_delivery_mode={batch.RepairDeliveryMode}");
            return true;
        }
    }

    private sealed class FaultInjectingFileTransferTransport :
        IFileTransferSignalingTransport,
        IFileTransferProtocolCapabilities,
        IFileTransferChunkBudgetProvider,
        IFileTransferTransportProfileProvider,
        IDisposable
    {
        private readonly IFileTransferSignalingTransport inner;
        private readonly PostFallbackFrontierDropPolicy dropPolicy;

        public FaultInjectingFileTransferTransport(IFileTransferSignalingTransport inner, PostFallbackFrontierDropPolicy dropPolicy)
        {
            this.inner = inner;
            this.dropPolicy = dropPolicy;
        }

        public bool SupportsFileTransferV6Streaming
            => inner is IFileTransferProtocolCapabilities { SupportsFileTransferV6Streaming: true };

        public FileTransferTransportProfileKind FileTransferTransportProfileKind
            => inner is IFileTransferTransportProfileProvider provider
                ? provider.FileTransferTransportProfileKind
                : FileTransferTransportProfileKind.Default;

        public event EventHandler<FileTransferOfferReceivedEventArgs>? FileTransferOfferReceived
        {
            add => inner.FileTransferOfferReceived += value;
            remove => inner.FileTransferOfferReceived -= value;
        }

        public event EventHandler<FileTransferAcceptReceivedEventArgs>? FileTransferAcceptReceived
        {
            add => inner.FileTransferAcceptReceived += value;
            remove => inner.FileTransferAcceptReceived -= value;
        }

        public event EventHandler<FileTransferDeclineReceivedEventArgs>? FileTransferDeclineReceived
        {
            add => inner.FileTransferDeclineReceived += value;
            remove => inner.FileTransferDeclineReceived -= value;
        }

        public event EventHandler<FileTransferSessionOpenReceivedEventArgs>? FileTransferSessionOpenReceived
        {
            add => inner.FileTransferSessionOpenReceived += value;
            remove => inner.FileTransferSessionOpenReceived -= value;
        }

        public event EventHandler<FileTransferCancelReceivedEventArgs>? FileTransferCancelReceived
        {
            add => inner.FileTransferCancelReceived += value;
            remove => inner.FileTransferCancelReceived -= value;
        }

        public event EventHandler<FileTransferErrorReceivedEventArgs>? FileTransferErrorReceived
        {
            add => inner.FileTransferErrorReceived += value;
            remove => inner.FileTransferErrorReceived -= value;
        }

        public event EventHandler<FileTransferCompleteReceivedEventArgs>? FileTransferCompleteReceived
        {
            add => inner.FileTransferCompleteReceived += value;
            remove => inner.FileTransferCompleteReceived -= value;
        }

        public event EventHandler<FileTransferPauseControlReceivedEventArgs>? FileTransferPauseControlReceived
        {
            add => inner.FileTransferPauseControlReceived += value;
            remove => inner.FileTransferPauseControlReceived -= value;
        }

        public event EventHandler<FileTransferHeartbeatReceivedEventArgs>? FileTransferHeartbeatReceived
        {
            add => inner.FileTransferHeartbeatReceived += value;
            remove => inner.FileTransferHeartbeatReceived -= value;
        }

        public event EventHandler<FileTransferTransportEpochReceivedEventArgs>? FileTransferTransportEpochReceived
        {
            add => inner.FileTransferTransportEpochReceived += value;
            remove => inner.FileTransferTransportEpochReceived -= value;
        }

        public event EventHandler<FileTransferTransportProbeReceivedEventArgs>? FileTransferTransportProbeReceived
        {
            add => inner.FileTransferTransportProbeReceived += value;
            remove => inner.FileTransferTransportProbeReceived -= value;
        }

        public event EventHandler<FileTransferRepairProofReceivedEventArgs>? FileTransferRepairProofReceived
        {
            add => inner.FileTransferRepairProofReceived += value;
            remove => inner.FileTransferRepairProofReceived -= value;
        }

        public int ResolveSafeOutboundChunkSize(FileTransferChunkBudgetRequest request)
            => inner is IFileTransferChunkBudgetProvider provider
                ? provider.ResolveSafeOutboundChunkSize(request)
                : FileTransferChunkBudget.ComputeLargestFittingRawChunkSize(
                    request.RequestedChunkSizeBytes,
                    candidate => candidate <= FileTransferChunkBudget.MaxRawChunkBytes,
                    "No valid V4 file-transfer chunk size fits within the payload budget.");

        public Task SendFileTransferOfferAsync(FileTransferOfferV2 message, CancellationToken ct)
            => inner.SendFileTransferOfferAsync(message, ct);

        public Task SendFileTransferAcceptAsync(FileTransferAcceptV1 message, CancellationToken ct)
            => inner.SendFileTransferAcceptAsync(message, ct);

        public Task SendFileTransferDeclineAsync(FileTransferDeclineV1 message, CancellationToken ct)
            => inner.SendFileTransferDeclineAsync(message, ct);

        public Task SendFileTransferSessionOpenAsync(FileTransferSessionOpenV2 message, CancellationToken ct)
            => inner.SendFileTransferSessionOpenAsync(message, ct);

        public Task SendFileTransferCancelAsync(FileTransferCancelV1 message, CancellationToken ct)
            => inner.SendFileTransferCancelAsync(message, ct);

        public Task SendFileTransferErrorAsync(FileTransferErrorV1 message, CancellationToken ct)
            => inner.SendFileTransferErrorAsync(message, ct);

        public Task SendFileTransferCompleteAsync(FileTransferCompleteV1 message, CancellationToken ct)
            => inner.SendFileTransferCompleteAsync(message, ct);

        public Task SendFileTransferPauseControlAsync(FileTransferPauseControlV6 message, CancellationToken ct)
            => inner.SendFileTransferPauseControlAsync(message, ct);

        public Task SendFileTransferHeartbeatAsync(FileTransferHeartbeatV6 message, CancellationToken ct)
            => inner.SendFileTransferHeartbeatAsync(message, ct);

        public Task SendFileTransferTransportEpochAsync(FileTransferTransportEpochV6 message, CancellationToken ct)
            => inner.SendFileTransferTransportEpochAsync(message, ct);

        public Task SendFileTransferTransportProbeAsync(FileTransferTransportProbeV6 message, CancellationToken ct)
            => inner.SendFileTransferTransportProbeAsync(message, ct);

        public Task SendFileTransferRepairProofAsync(FileTransferRepairProofV6 message, CancellationToken ct)
            => inner.SendFileTransferRepairProofAsync(message, ct);

        public async Task<IFileTransferDataSession> OpenFileTransferDataSessionAsync(string sessionId, string transferId, CancellationToken ct)
            => new FaultInjectingFileTransferDataSession(
                await inner.OpenFileTransferDataSessionAsync(sessionId, transferId, ct).ConfigureAwait(false),
                dropPolicy);

        public void Dispose()
        {
        }
    }

    private sealed class FaultInjectingFileTransferDataSession : IFileTransferDataSession
    {
        private readonly IFileTransferDataSession inner;
        private readonly PostFallbackFrontierDropPolicy dropPolicy;

        public FaultInjectingFileTransferDataSession(IFileTransferDataSession inner, PostFallbackFrontierDropPolicy dropPolicy)
        {
            this.inner = inner;
            this.dropPolicy = dropPolicy;
        }

        public string SessionId => inner.SessionId;

        public string TransferId => inner.TransferId;

        public bool IsAvailable => inner.IsAvailable;

        public event EventHandler<FileTransferDataSessionAvailabilityChangedEventArgs>? AvailabilityChanged
        {
            add => inner.AvailabilityChanged += value;
            remove => inner.AvailabilityChanged -= value;
        }

        public ValueTask<FileTransferDataFrame> ReceiveAsync(CancellationToken ct)
            => inner.ReceiveAsync(ct);

        public ValueTask<FileTransferReceivedDataFrame> ReceiveWithMetadataAsync(CancellationToken ct)
            => inner.ReceiveWithMetadataAsync(ct);

        public Task SendAsync(FileTransferDataFrame frame, CancellationToken ct)
            => dropPolicy.TryDrop(frame)
                ? Task.CompletedTask
                : inner.SendAsync(frame, ct);

        public void Dispose()
            => inner.Dispose();
    }
}
