using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

[Trait("Area", "BridgeManual")]
public sealed partial class TunaSidecarLiveManualTests : CoreSmokeTestsBase
{
    private const string TunaTestRequireProviderReadyEnv = "NLINK_TUNA_TEST_REQUIRE_PROVIDER_READY";
    private const string TunaTestProviderReadyAttemptsEnv = "NLINK_TUNA_TEST_PROVIDER_READY_ATTEMPTS";
    private const string TunaTestDegradedProviderGraceSecondsEnv = "NLINK_TUNA_TEST_DEGRADED_PROVIDER_GRACE_SECONDS";

    [Trait("Category", "Manual")]
    [ManualBridgeFact]
    public async Task TunaSidecar_TwoRealNknTransports_NegotiatesScreenAndFileThenFallsBack()
    {
        if (!IsEnabled("NLINK_RUN_LIVE_TUNA_TWO_APP"))
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var sidecarExe = Path.Combine(repoRoot, "artifacts", "tuna-sidecar", "nlink-tuna-sidecar.exe");
        var walletPath = Path.Combine(repoRoot, "artifacts", "tuna-poc", "wallet-test-nkn.json");
        var bridgeDir = TryFindBridgeBundleDirectory();
        Assert.True(File.Exists(sidecarExe), $"Missing Tuna sidecar: {sidecarExe}");
        Assert.True(File.Exists(walletPath), $"Missing Tuna test wallet: {walletPath}");
        Assert.True(bridgeDir is not null, "Bridge runtime not found. Build artifacts/bridge/win-x64 first.");

        var walletPassword = Environment.GetEnvironmentVariable("NLINK_TUNA_TEST_WALLET_PASSWORD");
        Assert.False(string.IsNullOrWhiteSpace(walletPassword), "Set NLINK_TUNA_TEST_WALLET_PASSWORD for this local opt-in live test.");

        var artifactDir = Path.Combine(
            repoRoot,
            "artifacts",
            "tuna-sidecar",
            "live-two-app-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'"));
        Directory.CreateDirectory(artifactDir);
        var identityDir = Path.Combine(Path.GetTempPath(), "nlink-tuna-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(identityDir);

        var previousNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var previousBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var previousManualBridge = Environment.GetEnvironmentVariable("NLINK_RUN_MANUAL_BRIDGE");
        var logStart = GetOperationalLogLength();
        Process? listenerProcess = null;
        var listenerLines = new ConcurrentQueue<string>();
        var listenerErrLines = new ConcurrentQueue<string>();
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", Path.Combine(bridgeDir!, "node.exe"));
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", Path.Combine(bridgeDir!, "index.js"));
            Environment.SetEnvironmentVariable("NLINK_RUN_MANUAL_BRIDGE", "1");

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var hostKey = Path.Combine(identityDir, "helpee-identity.json");
            var helperKey = Path.Combine(identityDir, "helper-identity.json");
            var nknOptionsHost = LoadNknOptionsWithOverrides(hostKey, "nlink-tuna-live-helpee-" + Guid.NewGuid().ToString("N")[..8]);
            var nknOptionsHelper = LoadNknOptionsWithOverrides(helperKey, "nlink-tuna-live-helper-" + Guid.NewGuid().ToString("N")[..8]);
            var hostIdentity = NknIdentityStore.LoadOrCreate(nknOptionsHost);
            var helperIdentity = NknIdentityStore.LoadOrCreate(nknOptionsHelper);
            var helperSeedBase64 = NknIdentityStore.ReadSeedBase64ForConnect(nknOptionsHelper.KeyPath);
            Assert.False(string.IsNullOrWhiteSpace(helperSeedBase64), "Helper identity seed is required for deterministic Tuna dialer identity.");
            var helperSidecarAddress = await ResolveSidecarAddressAsync(sidecarExe, helperSeedBase64!, cts.Token);

            var listenerReady = await StartListenerSidecarAsync(
                sidecarExe,
                walletPath,
                walletPassword!,
                helperSidecarAddress,
                listenerLines,
                listenerErrLines,
                cts.Token);
            listenerProcess = Process.GetProcessById(listenerReady.ProcessId);

            var hostOptions = CreateTunaOptionsForLiveTest(listenerReady.LocalIpc, sidecarExePath: null);
            var helperOptions = CreateTunaOptionsForLiveTest(listenerEndpoint: null, sidecarExe, helperSeedBase64);

            using var hostClient = new RealNknClientAdapter(hostIdentity, nknOptionsHost);
            using var helperClient = new RealNknClientAdapter(helperIdentity, nknOptionsHelper);
            using var host = new NknSignalingTransport(hostClient, nknOptionsHost, hostIdentity, hostOptions, new NknTunaAccelerationLane(hostOptions));
            using var helper = new NknSignalingTransport(helperClient, nknOptionsHelper, helperIdentity, helperOptions, new NknTunaAccelerationLane(helperOptions));

            var sessionId = await ApproveLiveSessionAsync(
                host,
                helper,
                InviteCapabilities.Chat | InviteCapabilities.ScreenShare | InviteCapabilities.FileTransfer,
                cts.Token);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(120));

            var streamConfigReceived = new TaskCompletionSource<ScreenShareVideoStreamConfigReceivedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var screenReceived = new TaskCompletionSource<ScreenShareFrameCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.ScreenShareVideoStreamConfigReceived += (_, e) => streamConfigReceived.TrySetResult(e);
            host.ScreenShareFrameCompleted += (_, e) => screenReceived.TrySetResult(e);
            await helper.SendScreenShareVideoStreamConfigAsync(CreateScreenShareVideoStreamConfig(sessionId, streamEpoch: 1), cts.Token);
            var receivedConfig = await streamConfigReceived.Task.WaitAsync(TimeSpan.FromSeconds(30), cts.Token);
            Assert.Equal(sessionId, receivedConfig.Message.SessionId);
            Assert.Equal(1, receivedConfig.Message.StreamEpoch);

            await helper.SendScreenSharePayloadAsync(
                ScreenShareVideoPayloadCodec.SerializeFragment(
                    new ScreenShareVideoFragmentV1
                    {
                        SessionId = sessionId,
                        StreamEpoch = 1,
                        FrameId = 0,
                        Width = 640,
                        Height = 360,
                        CapturedTsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        Encoding = "h264",
                        IsKeyFrame = true,
                        FragmentIndex = 0,
                        FragmentCount = 1,
                        Data = new byte[] { 1, 2, 3, 4, 5 },
                    }),
                cts.Token);
            var receivedScreen = await screenReceived.Task.WaitAsync(TimeSpan.FromSeconds(30), cts.Token);
            Assert.Equal(sessionId, receivedScreen.SessionId);

            const string transferId = "tuna-live-transfer";
            var offerReceived = new TaskCompletionSource<FileTransferOfferV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            var acceptReceived = new TaskCompletionSource<FileTransferAcceptV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sessionOpenReceived = new TaskCompletionSource<FileTransferSessionOpenV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.FileTransferOfferReceived += (_, e) => offerReceived.TrySetResult(e.Message);
            helper.FileTransferAcceptReceived += (_, e) => acceptReceived.TrySetResult(e.Message);
            host.FileTransferSessionOpenReceived += (_, e) => sessionOpenReceived.TrySetResult(e.Message);

            await helper.SendFileTransferOfferAsync(
                new FileTransferOfferV2
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    FileName = "tuna-live-transfer.bin",
                    FileSizeBytes = 32 * 1024,
                    PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                },
                cts.Token);
            await offerReceived.Task.WaitAsync(TimeSpan.FromSeconds(30), cts.Token);
            await host.SendFileTransferAcceptAsync(
                new FileTransferAcceptV1
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                },
                cts.Token);
            await acceptReceived.Task.WaitAsync(TimeSpan.FromSeconds(30), cts.Token);
            await helper.SendFileTransferSessionOpenAsync(
                new FileTransferSessionOpenV2
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    ProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                    SessionRole = FileTransferProtocol.SessionRoleSender,
                    ChunkSizeBytes = 16 * 1024,
                    InitialPipelineDepth = 8,
                },
                cts.Token);
            await sessionOpenReceived.Task.WaitAsync(TimeSpan.FromSeconds(30), cts.Token);

            using var receiverSession = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            using var senderSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            await senderSession.SendAsync(CreateChunkFrame(sessionId, transferId, chunkIndex: 0, fill: 0x41), cts.Token);
            var receivedFrame = await receiverSession.ReceiveAsync(cts.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(30), cts.Token);
            Assert.Equal(0, ((FileTransferChunkBatchFrame)receivedFrame).StartChunkIndex);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=tuna_sidecar_frame_written; channel=bulk", StringComparison.Ordinal),
                TimeSpan.FromSeconds(20));
            var listenerForwardedMediaBeforeKill = ListenerForwardedLane(listenerLines, "media");
            var listenerForwardedBulkBeforeKill = await WaitUntilOrFalseAsync(
                () => ListenerForwardedLane(listenerLines, "bulk"),
                TimeSpan.FromSeconds(8));

            listenerProcess = Process.GetProcessById(listenerReady.ProcessId);
            TryKill(listenerProcess);
            listenerProcess = null;
            await WaitUntilAsync(
                () => !host.IsAccelerationAvailableForTests && !helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(30));

            await senderSession.SendAsync(CreateChunkFrame(sessionId, transferId, chunkIndex: 1, fill: 0x42), cts.Token);
            var fallbackFrame = await receiverSession.ReceiveAsync(cts.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(45), cts.Token);
            Assert.Equal(1, ((FileTransferChunkBatchFrame)fallbackFrame).StartChunkIndex);

            var logTail = ReadOperationalLogTail(logStart);
            var listenerSnapshot = listenerLines.ToArray();
            File.WriteAllText(Path.Combine(artifactDir, "app-log-tail.redacted.log"), logTail);
            File.WriteAllText(
                Path.Combine(artifactDir, "summary.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        eventName = "tuna_live_two_app_summary",
                        listener = new
                        {
                            transport = "tuna",
                            localIpc = listenerReady.LocalIpc,
                            addressLength = listenerReady.Address.Length,
                            walletFile = Path.GetFileName(walletPath),
                            allowMode = "strict_exact_sidecar_address_seed_stdin_low_balance_capped_dev_test",
                            allowedRemoteAddressLength = helperSidecarAddress.Length,
                        },
                        sessionId,
                        screenFrameReceived = receivedScreen.FrameId,
                        fileChunkReceived = ((FileTransferChunkBatchFrame)receivedFrame).StartChunkIndex,
                        fallbackChunkReceived = ((FileTransferChunkBatchFrame)fallbackFrame).StartChunkIndex,
                        acceleratedSendEvents = CountOccurrences(logTail, "event=tuna_accelerated_envelope_sent"),
                        acceleratedMediaSendEvents = CountOccurrences(logTail, "event=tuna_accelerated_envelope_sent; message_type=ScreenShareFrame; channel=media"),
                        acceleratedBulkSendEvents = CountOccurrences(logTail, "event=tuna_accelerated_envelope_sent; message_type=file_transfer_data_frame; channel=bulk"),
                        sidecarFrameWrittenEvents = CountOccurrences(logTail, "event=tuna_sidecar_frame_written"),
                        sidecarMediaFrameWrittenEvents = CountOccurrences(logTail, "event=tuna_sidecar_frame_written; channel=media"),
                        sidecarBulkFrameWrittenEvents = CountOccurrences(logTail, "event=tuna_sidecar_frame_written; channel=bulk"),
                        sidecarFrameReceivedEvents = CountOccurrences(logTail, "event=tuna_sidecar_frame_received"),
                        sidecarMediaFrameReceivedEvents = CountOccurrences(logTail, "event=tuna_sidecar_frame_received; channel=media"),
                        sidecarBulkFrameReceivedEvents = CountOccurrences(logTail, "event=tuna_sidecar_frame_received; channel=bulk"),
                        listenerBridgeFrameForwardedEvents = CountListenerOccurrences(listenerSnapshot, "\"event\":\"bridge_frame_forwarded\""),
                        listenerMediaFrameForwardedEvents = CountListenerForwardedLane(listenerSnapshot, "media"),
                        listenerBulkFrameForwardedEvents = CountListenerForwardedLane(listenerSnapshot, "bulk"),
                        listenerForwardedMediaBeforeKill,
                        listenerForwardedBulkBeforeKill,
                        sidecarReadFailures = CountOccurrences(logTail, "event=tuna_sidecar_read_failed"),
                        sidecarWriteFailures = CountOccurrences(logTail, "event=tuna_sidecar_write_failed"),
                        accelerationDownNotifyQueuedEvents = CountOccurrences(logTail, "event=tuna_acceleration_down_notify_queued"),
                        accelerationDownNotifyRejectedEvents = CountOccurrences(logTail, "event=tuna_acceleration_down_notify_rejected"),
                        accelerationRemoteDownEvents = CountOccurrences(logTail, "event=tuna_acceleration_remote_down"),
                        dialerTerminalSidecarEvents =
                            CountOccurrences(logTail, "sidecar_event=bridge_direction_stopped") +
                            CountOccurrences(logTail, "sidecar_event=error"),
                        fileFrameShadowEvents = CountOccurrences(logTail, "event=tuna_accelerated_file_frame_shadowed"),
                        negotiated = logTail.Contains("event=tuna_acceleration_negotiated", StringComparison.Ordinal),
                        stateChangedDown = logTail.Contains("event=tuna_acceleration_state_changed; available=0", StringComparison.Ordinal),
                    },
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            if (listenerProcess is not null)
            {
                TryKill(listenerProcess);
            }

            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", previousNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", previousBridgePath);
            Environment.SetEnvironmentVariable("NLINK_RUN_MANUAL_BRIDGE", previousManualBridge);
            try { Directory.Delete(identityDir, recursive: true); } catch { }

            await File.WriteAllLinesAsync(Path.Combine(artifactDir, "listener.stdout.jsonl"), listenerLines);
            await File.WriteAllLinesAsync(Path.Combine(artifactDir, "listener.stderr.redacted.log"), listenerErrLines.Select(line => $"line_len={line.Length}"));
        }
    }

    private static async Task<ListenerReady> StartListenerSidecarAsync(
        string sidecarExe,
        string walletPath,
        string password,
        string allowRemoteAddress,
        ConcurrentQueue<string> stdoutLines,
        ConcurrentQueue<string> stderrLines,
        CancellationToken ct,
        int maxTotalMiB = 32,
        int maxDurationSec = 240,
        int acceptTimeoutSec = 180,
        string maxPriceNknPerMb = "0.0002",
        string? identifier = null)
    {
        var ready = new TaskCompletionSource<ListenerReady>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = sidecarExe,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };
        process.StartInfo.ArgumentList.Add("listen");
        if (!string.IsNullOrWhiteSpace(identifier))
        {
            process.StartInfo.ArgumentList.Add("--identifier");
            process.StartInfo.ArgumentList.Add(identifier);
        }

        process.StartInfo.ArgumentList.Add("--wallet");
        process.StartInfo.ArgumentList.Add(walletPath);
        process.StartInfo.ArgumentList.Add("--password-stdin");
        process.StartInfo.ArgumentList.Add("--allow-remote");
        process.StartInfo.ArgumentList.Add(allowRemoteAddress);
        process.StartInfo.ArgumentList.Add("--max-price-nkn-per-mb");
        process.StartInfo.ArgumentList.Add(maxPriceNknPerMb);
        process.StartInfo.ArgumentList.Add("--max-total-mib");
        process.StartInfo.ArgumentList.Add(maxTotalMiB.ToString(CultureInfo.InvariantCulture));
        process.StartInfo.ArgumentList.Add("--max-duration-sec");
        process.StartInfo.ArgumentList.Add(maxDurationSec.ToString(CultureInfo.InvariantCulture));
        process.StartInfo.ArgumentList.Add("--accept-timeout-sec");
        process.StartInfo.ArgumentList.Add(acceptTimeoutSec.ToString(CultureInfo.InvariantCulture));
        process.StartInfo.ArgumentList.Add("--local-ipc");
        process.StartInfo.ArgumentList.Add("127.0.0.1:0");
        if (IsEnabled(TunaTestRequireProviderReadyEnv))
        {
            process.StartInfo.ArgumentList.Add("--require-provider-ready");
        }

        var providerReadyAttempts = ReadInt(TunaTestProviderReadyAttemptsEnv, fallback: 1, min: 1, max: 5);
        if (providerReadyAttempts > 1)
        {
            process.StartInfo.ArgumentList.Add("--provider-ready-attempts");
            process.StartInfo.ArgumentList.Add(providerReadyAttempts.ToString(CultureInfo.InvariantCulture));
        }

        var degradedProviderGraceSeconds = ReadInt(TunaTestDegradedProviderGraceSecondsEnv, fallback: 0, min: 0, max: 300);
        if (degradedProviderGraceSeconds > 0)
        {
            process.StartInfo.ArgumentList.Add("--degraded-provider-grace-sec");
            process.StartInfo.ArgumentList.Add(degradedProviderGraceSeconds.ToString(CultureInfo.InvariantCulture));
        }

        process.StartInfo.ArgumentList.Add("--jsonl");

        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }

            stdoutLines.Enqueue(e.Data);
            TryParseReady(e.Data, process.Id, ready);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                stderrLines.Enqueue(e.Data);
            }
        };
        process.Exited += (_, _) =>
        {
            if (!ready.Task.IsCompleted)
            {
                ready.TrySetException(new InvalidOperationException($"Tuna listener exited before ready with code {process.ExitCode}."));
            }
        };

        var started = false;
        try
        {
            Assert.True(process.Start(), "Failed to start Tuna listener sidecar.");
            started = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.StandardInput.WriteLineAsync(password.AsMemory(), ct);
            process.StandardInput.Close();
            return await ready.Task.WaitAsync(TimeSpan.FromSeconds(90), ct);
        }
        catch
        {
            if (started)
            {
                TryKill(process);
            }
            else
            {
                process.Dispose();
            }

            throw;
        }
    }

    private static async Task<string> ResolveSidecarAddressAsync(string sidecarExe, string seedBase64, CancellationToken ct)
    {
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = sidecarExe,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };
        process.StartInfo.ArgumentList.Add("address");
        process.StartInfo.ArgumentList.Add("--seed-stdin");
        process.StartInfo.ArgumentList.Add("--jsonl");

        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }

            TryParseAddress(e.Data, ready);
        };
        process.Exited += (_, _) =>
        {
            if (!ready.Task.IsCompleted)
            {
                ready.TrySetException(new InvalidOperationException($"Tuna sidecar address probe exited before ready with code {process.ExitCode}."));
            }
        };

        var started = false;
        try
        {
            Assert.True(process.Start(), "Failed to start Tuna sidecar address probe.");
            started = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await WriteSeedHexAsync(process, seedBase64, ct);
            var address = await ready.Task.WaitAsync(TimeSpan.FromSeconds(45), ct);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            return address;
        }
        catch
        {
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }
    }

    private static async Task WriteSeedHexAsync(Process process, string seedBase64, CancellationToken ct)
    {
        var seedBytes = Convert.FromBase64String(seedBase64.Trim());
        try
        {
            Assert.Equal(32, seedBytes.Length);
            var seedHex = Convert.ToHexString(seedBytes).ToLowerInvariant();
            await process.StandardInput.WriteLineAsync(seedHex.AsMemory(), ct);
            process.StandardInput.Close();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seedBytes);
        }
    }

    private static async Task<string> ApproveLiveSessionAsync(
        NknSignalingTransport host,
        NknSignalingTransport helper,
        InviteCapabilities capabilities,
        CancellationToken ct)
    {
        var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
        host.Approved += (_, _) => hostApproved.TrySetResult();
        helper.Approved += (_, _) => helperApproved.TrySetResult();

        await host.HostByAddressAsync(ct);
        var invite = CreateValidatedInviteForTarget(
            new PeerAddress(host.LocalPeerAddress),
            out var rawToken,
            capabilities,
            boundHelperAddress: null);
        await helper.JoinByInviteAsync(rawToken, invite, ct);

        var pendingJoin = await joinRequestRaised.Task.WaitAsync(TimeSpan.FromSeconds(90), ct);
        await pendingJoin.ApproveAsync(pendingJoin.CreateApprovalDecision(), ct);
        await hostApproved.Task.WaitAsync(TimeSpan.FromSeconds(90), ct);
        await helperApproved.Task.WaitAsync(TimeSpan.FromSeconds(90), ct);
        return host.CurrentSessionSecurityState.SessionId!.Value.Value;
    }

    private static FileTransferChunkBatchFrameV6 CreateChunkFrame(string sessionId, string transferId, int chunkIndex, byte fill)
        => new()
        {
            SessionId = sessionId,
            TransferId = transferId,
            StartChunkIndex = chunkIndex,
            ChunkCount = 1,
            DataSegments = new[] { Enumerable.Repeat(fill, 16 * 1024).ToArray() },
            BatchProfile = "live_tuna_sidecar",
        };

    private static NknTunaAccelerationOptions CreateTunaOptionsForLiveTest(
        string? listenerEndpoint,
        string? sidecarExePath,
        string? dialerSeedBase64 = null)
    {
        var options = (NknTunaAccelerationOptions)Activator.CreateInstance(typeof(NknTunaAccelerationOptions), nonPublic: true)!;
        SetOption(options, "Enabled", true);
        SetOption(options, "ListenerEndpoint", listenerEndpoint);
        SetOption(options, "SidecarExePath", sidecarExePath);
        SetOption(options, "Lanes", NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen);
        SetOption(options, "CanOfferListener", !string.IsNullOrWhiteSpace(listenerEndpoint));
        SetOption(options, "ConnectTimeoutMs", 10_000);
        SetOption(options, "DialerReadyTimeoutMs", 120_000);
        SetOption(options, "TunaDialTimeoutMs", 60_000);
        SetOption(options, "DialerSeedBase64", dialerSeedBase64);
        return options;
    }

    private static void SetOption(NknTunaAccelerationOptions options, string propertyName, object? value)
        => typeof(NknTunaAccelerationOptions)
            .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(options, value);

    private static void TryParseReady(string line, int processId, TaskCompletionSource<ListenerReady> ready)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("event", out var eventProperty) ||
                !string.Equals(eventProperty.GetString(), "ready", StringComparison.OrdinalIgnoreCase) ||
                !root.TryGetProperty("localIpc", out var localIpcProperty) ||
                !root.TryGetProperty("address", out var addressProperty))
            {
                return;
            }

            ready.TrySetResult(new ListenerReady(
                addressProperty.GetString() ?? string.Empty,
                localIpcProperty.GetString() ?? string.Empty,
                processId));
        }
        catch (JsonException)
        {
        }
    }

    private static void TryParseAddress(string line, TaskCompletionSource<string> ready)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("event", out var eventProperty) ||
                !string.Equals(eventProperty.GetString(), "ready", StringComparison.OrdinalIgnoreCase) ||
                !root.TryGetProperty("address", out var addressProperty) ||
                addressProperty.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var address = addressProperty.GetString();
            if (!string.IsNullOrWhiteSpace(address))
            {
                ready.TrySetResult(address.Trim());
            }
        }
        catch (JsonException)
        {
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private static bool IsEnabled(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static int ReadInt(string name, int fallback, int min, int max)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, min, max)
            : fallback;
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string ExtractLastLogToken(string text, string prefix)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(prefix))
        {
            return string.Empty;
        }

        var index = text.LastIndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return string.Empty;
        }

        var start = index + prefix.Length;
        var end = start;
        while (end < text.Length &&
               !char.IsWhiteSpace(text[end]) &&
               text[end] != ';' &&
               text[end] != ',' &&
               text[end] != '"' &&
               text[end] != '}')
        {
            end++;
        }

        return end <= start ? string.Empty : text[start..end].Trim();
    }

    private static async Task<bool> WaitUntilOrFalseAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return condition();
    }

    private static bool ListenerForwardedLane(ConcurrentQueue<string> lines, string lane)
        => lines.Any(line =>
            line.Contains("\"event\":\"bridge_frame_forwarded\"", StringComparison.Ordinal) &&
            line.Contains($"\"frameLane\":\"{lane}\"", StringComparison.Ordinal));

    private static int CountListenerForwardedLane(IEnumerable<string> lines, string lane)
        => lines.Count(line =>
            line.Contains("\"event\":\"bridge_frame_forwarded\"", StringComparison.Ordinal) &&
            line.Contains($"\"frameLane\":\"{lane}\"", StringComparison.Ordinal));

    private static int CountListenerOccurrences(IEnumerable<string> lines, string needle)
        => lines.Count(line => line.Contains(needle, StringComparison.Ordinal));

    private sealed record ListenerReady(string Address, string LocalIpc, int ProcessId);
}
