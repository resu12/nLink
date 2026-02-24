using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NLink.App.Configuration;
using NLink.App.Services;
using NLink.App.ViewModels;
using NLink.Core;
using NLink.Core.Chat;
using NLink.Infra.DevLocal;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public class SmokeTests
{
    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionCode_FormatsToSixDigits_AndRejectsNonDigits()
    {
        var code = new SessionCode("001234");

        Assert.Equal("001234", code.Digits);
        Assert.Equal("001 234", code.DisplayText);
        Assert.Equal("123456", SessionCode.NormalizeDigits("12a3-45 6"));
        Assert.True(SessionCode.TryParse("123 456", out var parsed));
        Assert.Equal("123456", parsed.Digits);
        Assert.False(SessionCode.TryParse("12a45", out _));
        Assert.Throws<ArgumentException>(() => new SessionCode("12A456"));
        Assert.Throws<ArgumentException>(() => new SessionCode("12345"));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void ShareMessageBuilder_WithCode_NoUrl()
    {
        var text = ShareMessageBuilder.BuildInstallMessage("123456", null);
        Assert.Equal("Install nLink and enter code 123456", text);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void ShareMessageBuilder_WithCode_AndUrl()
    {
        var text = ShareMessageBuilder.BuildInstallMessage("123456", "https://example.com/nlink");
        Assert.Equal(
            "Install nLink and enter code 123456" + Environment.NewLine + "https://example.com/nlink",
            text);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void ShareMessageBuilder_WithoutCode_WithUrl()
    {
        var text = ShareMessageBuilder.BuildInstallMessage(null, "https://example.com/nlink");
        Assert.Equal(
            "Install nLink" + Environment.NewLine + "https://example.com/nlink",
            text);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void ReliabilityLog_RingBuffer_CapsAt50()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var logPath = Path.Combine(tempDir, "reliability.jsonl");

        SessionReliabilityLog.SetStoragePathOverrideForTests(logPath);
        SessionReliabilityLog.ResetForTests();

        try
        {
            for (var i = 0; i < 60; i++)
            {
                SessionReliabilityLog.RecordStandalone(
                    "Helper",
                    "NKN",
                    SessionReliabilityStage.Disconnected,
                    errorCode: "e" + i.ToString("D2"),
                    errorHint: null);
            }

            var snapshot = SessionReliabilityLog.SnapshotRecent(100);
            Assert.Equal(50, snapshot.Count);
            Assert.Equal("e10", snapshot[0].ErrorCode);
            Assert.Equal("e59", snapshot[^1].ErrorCode);
        }
        finally
        {
            SessionReliabilityLog.ResetForTests();
            SessionReliabilityLog.SetStoragePathOverrideForTests(null);
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void ReliabilityLog_Persists_JsonlLines()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var logPath = Path.Combine(tempDir, "reliability.jsonl");

        SessionReliabilityLog.SetStoragePathOverrideForTests(logPath);
        SessionReliabilityLog.ResetForTests();

        try
        {
            var attempt = SessionReliabilityLog.StartAttempt("Helpee", "DevLocal");
            SessionReliabilityLog.RecordStage(attempt, SessionReliabilityStage.CodeGenerated);
            SessionReliabilityLog.RecordStage(attempt, SessionReliabilityStage.Completed);

            Assert.True(File.Exists(logPath));
            var lines = File.ReadAllLines(logPath);
            Assert.True(lines.Length >= 3); // Started + CodeGenerated + Completed
            Assert.Contains("\"Stage\":\"Completed\"", lines[^1]);
            Assert.Contains("\"Mode\":\"Helpee\"", string.Join(Environment.NewLine, lines));
        }
        finally
        {
            SessionReliabilityLog.ResetForTests();
            SessionReliabilityLog.SetStoragePathOverrideForTests(null);
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void ReliabilityLog_Redacts_SecretLikeTokens()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var logPath = Path.Combine(tempDir, "reliability.jsonl");

        SessionReliabilityLog.SetStoragePathOverrideForTests(logPath);
        SessionReliabilityLog.ResetForTests();

        const string fakePayload = "payloadBase64=ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/==";
        const string fakeKey = "sharedKey=0123456789abcdef0123456789abcdef0123456789abcdef";

        try
        {
            SessionReliabilityLog.RecordStandalone(
                "Helper",
                "NKN",
                SessionReliabilityStage.Disconnected,
                errorCode: "bridge_ping_timeout",
                errorHint: $"{fakePayload} {fakeKey}");

            var line = File.ReadAllText(logPath);
            Assert.DoesNotContain("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/==", line);
            Assert.DoesNotContain("0123456789abcdef0123456789abcdef0123456789abcdef", line);
            Assert.Contains("[redacted]", line);
        }
        finally
        {
            SessionReliabilityLog.ResetForTests();
            SessionReliabilityLog.SetStoragePathOverrideForTests(null);
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void ChatKeyAgreement_ProducesSameSessionKey_OnBothSides()
    {
        using var a = ChatKeyAgreement.CreateKeyPair();
        using var b = ChatKeyAgreement.CreateKeyPair();

        var aKey = a.DeriveSharedKey(b.PublicKey);
        var bKey = b.DeriveSharedKey(a.PublicKey);

        Assert.Equal(32, aKey.Length);
        Assert.Equal(aKey, bKey);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void ChatAesGcm_EncryptDecrypt_RoundTrip()
    {
        var key = SHA256LikeDeterministicBytes("test-key", 32);
        var nonce = SHA256LikeDeterministicBytes("test-nonce", ChatAesGcmCrypto.NonceSize);
        var plaintext = Encoding.UTF8.GetBytes("hello chat");

        var encrypted = ChatAesGcmCrypto.EncryptWithNonce(key, plaintext, nonce);
        var roundTrip = ChatAesGcmCrypto.Decrypt(key, encrypted.Nonce, encrypted.Tag, encrypted.Ciphertext);

        Assert.Equal(plaintext, roundTrip);
        Assert.Equal(ChatAesGcmCrypto.TagSize, encrypted.Tag.Length);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void ChatEnvelope_SerializeDeserialize_IsStableAndVersioned()
    {
        var envelope = new ChatEnvelope
        {
            Version = ChatProtocol.Version,
            Type = ChatProtocol.ChatMessageType,
            NonceBase64 = "AQIDBAUGBwgJCgsM",
            TagBase64 = "AAAAAAAAAAAAAAAAAAAAAA==",
            CiphertextBase64 = "SGVsbG8=",
        };

        var bytes = ChatEnvelopeCodec.SerializeEnvelope(envelope);
        var json = Encoding.UTF8.GetString(bytes);

        Assert.Equal(
            "{\"v\":1,\"t\":\"chat.message\",\"n\":\"AQIDBAUGBwgJCgsM\",\"g\":\"AAAAAAAAAAAAAAAAAAAAAA==\",\"c\":\"SGVsbG8=\"}",
            json);

        var parsed = ChatEnvelopeCodec.DeserializeEnvelope(bytes);
        Assert.Equal(ChatProtocol.Version, parsed.Version);
        Assert.Equal(ChatProtocol.ChatMessageType, parsed.Type);
        Assert.Equal(envelope.NonceBase64, parsed.NonceBase64);
        Assert.Equal(envelope.TagBase64, parsed.TagBase64);
        Assert.Equal(envelope.CiphertextBase64, parsed.CiphertextBase64);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task DevLocalTransport_HostJoin_RaisesJoinRequestApproveAndRejectEvents()
    {
        await VerifyHandshakeAsync(approve: true);
        await VerifyHandshakeAsync(approve: false);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task DevLocalTransport_Chat_HelperToHelpee_And_HelpeeToHelper_RoundTrip()
    {
        ChatRuntimeCounters.ResetForTests();

        var code = CreateTestCode();
        using var hostTransport = new DevLocalTransport();
        using var helperTransport = new DevLocalTransport();
        using var helpeeChat = new SessionChatService(() => new DateTimeOffset(2026, 2, 23, 18, 0, 0, TimeSpan.Zero));
        using var helperChat = new SessionChatService(() => new DateTimeOffset(2026, 2, 23, 18, 0, 5, TimeSpan.Zero));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        helpeeChat.AttachTransport(hostTransport);
        helperChat.AttachTransport(helperTransport);

        IncomingJoinRequestEventArgs? pendingJoin = null;
        var joinRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hostTransport.IncomingJoinRequest += (_, e) =>
        {
            pendingJoin = e;
            joinRaised.TrySetResult();
        };

        var helpeeMessageTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var helperMessageTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var preApprovalNoticeRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        helpeeChat.MessageReceived += (_, e) => helpeeMessageTcs.TrySetResult(e.Message.Text);
        helpeeChat.MessageReceivedBeforeApproved += (_, _) => preApprovalNoticeRaised.TrySetResult();
        helperChat.MessageReceived += (_, e) => helperMessageTcs.TrySetResult(e.Message.Text);

        _ = hostTransport.HostAsync(code, cts.Token);
        await Task.Delay(75, cts.Token);
        await helperTransport.JoinAsync(code, cts.Token).WaitAsync(TimeSpan.FromSeconds(3));

        await joinRaised.Task.WaitAsync(cts.Token);
        await WaitUntilAsync(() => helpeeChat.HasSessionKey && helperChat.HasSessionKey, TimeSpan.FromSeconds(3));

        var helperSent = await helperChat.TrySendTextAsync("Hi, it's me", cts.Token);
        Assert.NotNull(helperSent);
        var helpeeReceived = await helpeeMessageTcs.Task.WaitAsync(cts.Token);
        await preApprovalNoticeRaised.Task.WaitAsync(cts.Token);

        await pendingJoin!.ApproveAsync(cts.Token);
        await WaitUntilAsync(() => helperChat.IsApproved && helpeeChat.IsApproved, TimeSpan.FromSeconds(3));

        var helpeeSent = await helpeeChat.TrySendTextAsync("I can see your message", cts.Token);
        Assert.NotNull(helpeeSent);
        var helperReceived = await helperMessageTcs.Task.WaitAsync(cts.Token);

        Assert.Equal("Hi, it's me", helpeeReceived);
        Assert.Equal("I can see your message", helperReceived);

        var counters = ChatRuntimeCounters.Snapshot();
        Assert.True(counters.ChatSent >= 2);
        Assert.True(counters.ChatReceived >= 2);
        Assert.Equal(0, counters.ChatDecryptFailed);

        helperTransport.Dispose();
        hostTransport.Dispose();
        cts.Cancel();
        await Task.Delay(50, CancellationToken.None);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionChatService_ValidReceivedPayload_IncrementsChatReceived()
    {
        ChatRuntimeCounters.ResetForTests();

        using var transport = new FakeSignalingTransport();
        using var chat = new SessionChatService(() => new DateTimeOffset(2026, 2, 23, 19, 0, 0, TimeSpan.Zero));

        var receivedText = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        chat.MessageReceived += (_, e) => receivedText.TrySetResult(e.Message.Text);

        chat.AttachTransport(transport);

        var key = SHA256LikeDeterministicBytes("chat-key-valid", 32);
        transport.RaiseSessionKeyReady(key);

        var payloadBytes = CreateEncryptedChatEnvelopeBytes(
            key,
            messageId: "msg-valid-1",
            text: "hello from helper",
            timestampUnixMs: new DateTimeOffset(2026, 2, 23, 19, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            nonceSeed: "nonce-valid-1");

        transport.RaiseChatMessage(payloadBytes);

        var text = await receivedText.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("hello from helper", text);

        var counters = ChatRuntimeCounters.Snapshot();
        Assert.Equal(1, counters.ChatReceived);
        Assert.Equal(0, counters.ChatDecryptFailed);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionChatService_InvalidEncryptedPayload_IncrementsDecryptFailed()
    {
        ChatRuntimeCounters.ResetForTests();

        using var transport = new FakeSignalingTransport();
        using var chat = new SessionChatService(() => new DateTimeOffset(2026, 2, 23, 19, 5, 0, TimeSpan.Zero));

        chat.AttachTransport(transport);

        var key = SHA256LikeDeterministicBytes("chat-key-invalid", 32);
        transport.RaiseSessionKeyReady(key);

        var payloadBytes = CreateEncryptedChatEnvelopeBytes(
            key,
            messageId: "msg-invalid-1",
            text: "hello",
            timestampUnixMs: new DateTimeOffset(2026, 2, 23, 19, 5, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            nonceSeed: "nonce-invalid-1");

        var envelope = ChatEnvelopeCodec.DeserializeEnvelope(payloadBytes);
        var tagBytes = Convert.FromBase64String(envelope.TagBase64);
        tagBytes[0] ^= 0xFF;

        var tamperedBytes = ChatEnvelopeCodec.SerializeEnvelope(
            new ChatEnvelope
            {
                Version = envelope.Version,
                Type = envelope.Type,
                NonceBase64 = envelope.NonceBase64,
                TagBase64 = Convert.ToBase64String(tagBytes),
                CiphertextBase64 = envelope.CiphertextBase64,
            });

        transport.RaiseChatMessage(tamperedBytes);

        var counters = ChatRuntimeCounters.Snapshot();
        Assert.Equal(1, counters.ChatReceived);
        Assert.Equal(1, counters.ChatDecryptFailed);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task ViewModelFlow_HelpeeApproves_HelperAndHelpeeReachConnectedState()
    {
        var transportConfig = CreateDevLocalTestConfig();

        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, transportConfig);
        using var helper = new HelperPageViewModel(cancelAction: static () => { }, transportConfig);

        helper.CodeInput = helpee.ShareCode;

        var connectTask = helper.ConnectCommand.ExecuteAsync(null);

        await WaitUntilAsync(
            () => helpee.HasIncomingRequest && helpee.ConnectionState == "IncomingRequest",
            TimeSpan.FromSeconds(5));

        helpee.AllowCommand.Execute(null);

        await connectTask;

        await WaitUntilAsync(
            () => helpee.ConnectionState == "Connected" && helper.ConnectionState == "Connected",
            TimeSpan.FromSeconds(5));

        Assert.Equal("Connected", helpee.ConnectionState);
        Assert.Equal("Connected", helper.ConnectionState);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_FakeClient_HostJoinApproveAndChat_RoundTrip()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.fake.address");
            var helperClient = new FakeNknClient("helper.fake.address");
            var hostIdentity = new NknIdentity("host-id", "host.fake.address");
            var helperIdentity = new NknIdentity("helper-id", "helper.fake.address");

            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);

            var code = new SessionCode("123456");

            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostKeyReady = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var helperKeyReady = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostChatReceived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.SessionKeyReady += (_, e) => hostKeyReady.TrySetResult(e.SharedKey);
            helper.SessionKeyReady += (_, e) => helperKeyReady.TrySetResult(e.SharedKey);
            host.Approved += (_, _) => hostApproved.TrySetResult();
            helper.Approved += (_, _) => helperApproved.TrySetResult();
            host.ChatMessageReceived += (_, e) => hostChatReceived.TrySetResult(e.Payload);

            await host.HostAsync(code, cts.Token);
            await helper.JoinAsync(code, cts.Token);

            var pendingJoin = await joinRequestRaised.Task.WaitAsync(TimeSpan.FromSeconds(6), cts.Token);
            await pendingJoin.ApproveAsync(cts.Token);

            var hostKey = await hostKeyReady.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            var helperKey = await helperKeyReady.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await hostApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await helperApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            Assert.Equal(hostKey, helperKey);
            Assert.Equal(32, hostKey.Length);

            var chatPayload = Encoding.UTF8.GetBytes("opaque-encrypted-payload");
            await helper.SendChatMessageAsync(chatPayload, cts.Token);
            var received = await hostChatReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            Assert.Equal(chatPayload, received);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelperViewModel_CopyInstallMessageCommand_UsesClipboardService()
    {
        var fakeClipboard = new FakeClipboardService();
        var transportConfig = CreateDevLocalTestConfig();
        var shareConfig = new ShareMessageConfig("https://example.com/nlink");

        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            transportConfig,
            fakeClipboard,
            shareConfig);

        await helper.CopyInstallMessageCommand.ExecuteAsync(null);

        Assert.Equal(
            "Install nLink" + Environment.NewLine + "https://example.com/nlink",
            fakeClipboard.LastText);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task Bridge_Startup_HealthCheck()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        Assert.True(bundleDir is not null,
            "Bridge runtime not found. Build artifacts/bridge/win-x64 first (run installer/Build-BridgeBundle.ps1).");

        var nodePath = Path.Combine(bundleDir!, "node.exe");
        var bridgePath = Path.Combine(bundleDir!, "index.js");
        Assert.True(File.Exists(nodePath),
            $"Bridge runtime not found. Expected bundled node at '{nodePath}'. Run installer/Build-BridgeBundle.ps1.");
        Assert.True(File.Exists(bridgePath),
            $"Bridge runtime not found. Expected bridge script at '{bridgePath}'. Run installer/Build-BridgeBundle.ps1.");

        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);

            var options = NknTransportOptions.Load();
            var identity = new NknIdentity("smoke-bridge", "smoke-bridge.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await adapter.StartBridgeAsync(cts.Token);
            Assert.True(adapter.IsBridgeProcessRunning);

            await adapter.PingBridgeAsync(cts.Token);

            var snapshotAfterPing = NknRuntimeDiagnostics.Snapshot();
            Assert.True(snapshotAfterPing.BridgePid > 0);
            Assert.False(string.IsNullOrWhiteSpace(snapshotAfterPing.NodeVersion));
            Assert.True(snapshotAfterPing.BridgeLastPongUtcTicks > 0);

            await adapter.DisconnectAsync();

            await WaitUntilAsync(() => !adapter.IsBridgeProcessRunning, TimeSpan.FromSeconds(2));

            var snapshotAfterShutdown = NknRuntimeDiagnostics.Snapshot();
            Assert.True(snapshotAfterShutdown.BridgeLastExitCode >= 0 || snapshotAfterShutdown.BridgeLastExitReason != "(none)");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Bridge_ProcessKill_RestartsAndUpdatesDiagnostics()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        Assert.True(bundleDir is not null,
            "Bridge runtime not found. Build artifacts/bridge/win-x64 first (run installer/Build-BridgeBundle.ps1).");

        var nodePath = Path.Combine(bundleDir!, "node.exe");
        var bridgePath = Path.Combine(bundleDir!, "index.js");
        Assert.True(File.Exists(nodePath), $"Missing bundled node runtime: {nodePath}");
        Assert.True(File.Exists(bridgePath), $"Missing bundled bridge script: {bridgePath}");

        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);

            var options = NknTransportOptions.Load();
            var identity = new NknIdentity("manual-restart", "manual-restart.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            await adapter.StartBridgeAsync(cts.Token);
            await adapter.PingBridgeAsync(cts.Token);

            var before = NknRuntimeDiagnostics.Snapshot();
            Assert.True(before.BridgePid > 0, "Bridge PID was not recorded after hello/ping.");

            using (var bridgeProcess = Process.GetProcessById(before.BridgePid))
            {
                bridgeProcess.Kill(entireProcessTree: true);
            }

            await WaitUntilAsync(() =>
            {
                var snap = NknRuntimeDiagnostics.Snapshot();
                return snap.BridgeRestartCount > before.BridgeRestartCount &&
                       snap.BridgePid > 0 &&
                       snap.BridgePid != before.BridgePid;
            }, TimeSpan.FromSeconds(10));

            var after = NknRuntimeDiagnostics.Snapshot();
            Assert.True(after.BridgeRestartCount > before.BridgeRestartCount, "Bridge restart count did not increment.");
            Assert.NotEqual(before.BridgePid, after.BridgePid);
            Assert.Equal("process exited", after.BridgeLastExitReason);

            await adapter.DisconnectAsync();
            await WaitUntilAsync(() => !adapter.IsBridgeProcessRunning, TimeSpan.FromSeconds(3));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task NknTransport_RealBridge_SingleMachine_HostJoinApproveAndChat_RoundTrip()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        Assert.True(bundleDir is not null,
            "Bridge runtime not found. Build artifacts/bridge/win-x64 first (run installer/Build-BridgeBundle.ps1).");

        var nodePath = Path.Combine(bundleDir!, "node.exe");
        var bridgePath = Path.Combine(bundleDir!, "index.js");
        Assert.True(File.Exists(nodePath), $"Missing bundled node runtime: {nodePath}");
        Assert.True(File.Exists(bridgePath), $"Missing bundled bridge script: {bridgePath}");

        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-real-nkn-manual", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);

            var hostKeyPath = Path.Combine(tempDir, "host-identity.json");
            var helperKeyPath = Path.Combine(tempDir, "helper-identity.json");

            var hostOptions = LoadNknOptionsWithOverrides(hostKeyPath, "manual-host-" + Guid.NewGuid().ToString("N")[..8]);
            var helperOptions = LoadNknOptionsWithOverrides(helperKeyPath, "manual-helper-" + Guid.NewGuid().ToString("N")[..8]);

            var hostIdentity = NknIdentityStore.LoadOrCreate(hostOptions);
            var helperIdentity = NknIdentityStore.LoadOrCreate(helperOptions);

            using var hostClient = new RealNknClientAdapter(hostIdentity, hostOptions);
            using var helperClient = new RealNknClientAdapter(helperIdentity, helperOptions);
            using var host = new NknSignalingTransport(hostClient, hostOptions, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, helperOptions, helperIdentity);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

            var code = new SessionCode("482631");

            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostKeyReady = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var helperKeyReady = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostChatReceived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.SessionKeyReady += (_, e) => hostKeyReady.TrySetResult(e.SharedKey);
            helper.SessionKeyReady += (_, e) => helperKeyReady.TrySetResult(e.SharedKey);
            helper.Approved += (_, _) => helperApproved.TrySetResult();
            host.ChatMessageReceived += (_, e) => hostChatReceived.TrySetResult(e.Payload);

            await host.HostAsync(code, cts.Token);
            await helper.JoinAsync(code, cts.Token);

            var pendingJoin = await joinRequestRaised.Task.WaitAsync(TimeSpan.FromSeconds(45), cts.Token);
            await pendingJoin.ApproveAsync(cts.Token);

            var hostKey = await hostKeyReady.Task.WaitAsync(TimeSpan.FromSeconds(20), cts.Token);
            var helperKey = await helperKeyReady.Task.WaitAsync(TimeSpan.FromSeconds(20), cts.Token);
            await helperApproved.Task.WaitAsync(TimeSpan.FromSeconds(20), cts.Token);

            Assert.Equal(hostKey, helperKey);
            Assert.Equal(32, hostKey.Length);

            var chatPayload = Encoding.UTF8.GetBytes("manual-real-nkn-chat-payload");
            await helper.SendChatMessageAsync(chatPayload, cts.Token);
            var received = await hostChatReceived.Task.WaitAsync(TimeSpan.FromSeconds(20), cts.Token);
            Assert.Equal(chatPayload, received);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);

            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    private static async Task VerifyHandshakeAsync(bool approve)
    {
        var code = CreateTestCode();
        using var host = new DevLocalTransport();
        using var joiner = new DevLocalTransport();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var joinRequestRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var approvedRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rejectedRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnectedRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IncomingJoinRequestEventArgs? pendingJoinRequest = null;

        host.IncomingJoinRequest += (_, e) =>
        {
            pendingJoinRequest = e;
            joinRequestRaised.TrySetResult();
        };

        joiner.Approved += (_, _) => approvedRaised.TrySetResult();
        joiner.Rejected += (_, _) => rejectedRaised.TrySetResult();
        joiner.Disconnected += (_, _) => disconnectedRaised.TrySetResult();

        _ = host.HostAsync(code, cts.Token);
        await Task.Delay(75, cts.Token);

        await WaitStepAsync("joiner join", joiner.JoinAsync(code, cts.Token), TimeSpan.FromSeconds(3));
        await WaitStepAsync("join request raised", joinRequestRaised.Task, TimeSpan.FromSeconds(3));
        Assert.NotNull(pendingJoinRequest);

        if (approve)
        {
            await WaitStepAsync("approve request", pendingJoinRequest!.ApproveAsync(CancellationToken.None), TimeSpan.FromSeconds(3));
        }
        else
        {
            await WaitStepAsync("reject request", pendingJoinRequest!.RejectAsync(CancellationToken.None), TimeSpan.FromSeconds(3));
        }

        if (approve)
        {
            await WaitStepAsync("approved event", approvedRaised.Task, TimeSpan.FromSeconds(3));
            Assert.False(rejectedRaised.Task.IsCompleted);
        }
        else
        {
            await WaitStepAsync("rejected event", rejectedRaised.Task, TimeSpan.FromSeconds(3));
            Assert.False(approvedRaised.Task.IsCompleted);
        }

        // Reject path may close immediately. Approve path should keep the session alive.
        if (approve)
        {
            Assert.False(disconnectedRaised.Task.IsCompleted);
        }

        joiner.Dispose();
        host.Dispose();
        cts.Cancel();
        await Task.Delay(50, CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("Condition was not met before timeout.");
    }

    private static async Task WaitStepAsync(string stepName, Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync(timeout);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"Timed out while waiting for step: {stepName}", ex);
        }
    }

    private static SessionCode CreateTestCode()
    {
        var value = Math.Abs(HashCode.Combine(Environment.ProcessId, Environment.TickCount64)) % 1_000_000;
        return new SessionCode(value.ToString("D6"));
    }

    private static TransportRuntimeConfig CreateDevLocalTestConfig()
    {
        var previous = Environment.GetEnvironmentVariable("FRH_TRANSPORT");

        try
        {
            Environment.SetEnvironmentVariable("FRH_TRANSPORT", null);
            return TransportRuntimeConfig.Select();
        }
        finally
        {
            Environment.SetEnvironmentVariable("FRH_TRANSPORT", previous);
        }
    }

    private static byte[] SHA256LikeDeterministicBytes(string input, int length)
    {
        var source = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(input));
        if (length == source.Length)
        {
            return source;
        }

        var buffer = new byte[length];
        Array.Copy(source, buffer, length);
        return buffer;
    }

    private static byte[] CreateEncryptedChatEnvelopeBytes(
        byte[] key,
        string messageId,
        string text,
        long timestampUnixMs,
        string nonceSeed)
    {
        var payload = new ChatMessagePayload
        {
            MessageId = messageId,
            Text = text,
            TimestampUnixMilliseconds = timestampUnixMs,
        };

        var payloadBytes = ChatEnvelopeCodec.SerializePayload(payload);
        var nonce = SHA256LikeDeterministicBytes(nonceSeed, ChatAesGcmCrypto.NonceSize);
        var encrypted = ChatAesGcmCrypto.EncryptWithNonce(key, payloadBytes, nonce);

        var envelope = new ChatEnvelope
        {
            Version = ChatProtocol.Version,
            Type = ChatProtocol.ChatMessageType,
            NonceBase64 = Convert.ToBase64String(encrypted.Nonce),
            TagBase64 = Convert.ToBase64String(encrypted.Tag),
            CiphertextBase64 = Convert.ToBase64String(encrypted.Ciphertext),
        };

        return ChatEnvelopeCodec.SerializeEnvelope(envelope);
    }

    private static string? TryFindBridgeBundleDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && current is not null; i++, current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "artifacts", "bridge", "win-x64");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static NknTransportOptions LoadNknOptionsWithOverrides(string keyPath, string identifier)
    {
        var prevKeyPath = Environment.GetEnvironmentVariable("NLINK_NKN_KEY_PATH");
        var prevIdentifier = Environment.GetEnvironmentVariable("NLINK_NKN_IDENTIFIER");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_KEY_PATH", keyPath);
            Environment.SetEnvironmentVariable("NLINK_NKN_IDENTIFIER", identifier);
            return NknTransportOptions.Load();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_KEY_PATH", prevKeyPath);
            Environment.SetEnvironmentVariable("NLINK_NKN_IDENTIFIER", prevIdentifier);
        }
    }

#pragma warning disable CS0067
    private sealed class FakeSignalingTransport : ISignalingTransport
    {
        public event EventHandler<IncomingJoinRequestEventArgs>? IncomingJoinRequest;
        public event EventHandler<TransportSessionKeyReadyEventArgs>? SessionKeyReady;
        public event EventHandler<TransportChatMessageEventArgs>? ChatMessageReceived;
        public event EventHandler? Approved;
        public event EventHandler? Rejected;
        public event EventHandler? Disconnected;

        public void Dispose()
        {
        }

        public Task HostAsync(SessionCode code, CancellationToken ct) => Task.CompletedTask;

        public Task JoinAsync(SessionCode code, CancellationToken ct) => Task.CompletedTask;

        public Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => Task.CompletedTask;

        public void RaiseSessionKeyReady(byte[] sharedKey)
        {
            SessionKeyReady?.Invoke(this, new TransportSessionKeyReadyEventArgs(sharedKey));
        }

        public void RaiseChatMessage(byte[] payload)
        {
            ChatMessageReceived?.Invoke(this, new TransportChatMessageEventArgs(payload));
        }
    }
#pragma warning restore CS0067

    private sealed class FakeClipboardService : IClipboardService
    {
        public string LastText { get; private set; } = string.Empty;

        public Task SetTextAsync(string text)
        {
            LastText = text;
            return Task.CompletedTask;
        }
    }
}
