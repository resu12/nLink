using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NLink.App.Configuration;
using NLink.App.Services;
using NLink.App.ViewModels;
using NLink.Core;
using NLink.Core.Chat;
using NLink.Core.Logging;
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
    public void ShareMessageBuilder_HelperInstallMessage_IncludesConfiguredUrl_AndTrailingNewline()
    {
        var text = ShareMessageBuilder.BuildHelperInstallMessage("https://example.com/releases");

        Assert.Equal(
            "Install nLink and open it." + Environment.NewLine +
            "Download: https://example.com/releases" + Environment.NewLine,
            text);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void ShareMessageBuilder_HelperInstallMessage_DoesNotIncludeInternalDiagnosticsText()
    {
        var text = ShareMessageBuilder.BuildHelperInstallMessage("https://example.com/releases");

        Assert.DoesNotContain("Bridge PID", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NKN", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("last_error", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("identifier", text, StringComparison.OrdinalIgnoreCase);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void UserErrorMapper_KeyMessages_AreShortAndUserFriendly()
    {
        Assert.Equal("No one found with that code.", UserErrorMapper.HelperDiscoveryTimeout());
        Assert.Equal("No response yet.", UserErrorMapper.HelperApprovalTimeout());
        Assert.Equal("Connection lost.", UserErrorMapper.HelperDisconnected());
        Assert.Equal("Connection lost.", UserErrorMapper.HelperGenericConnectFailure());
        Assert.Equal("Please reinstall.", UserErrorMapper.NknStartFailedReinstall());
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void AppAssembly_InformationalVersion_Matches_VERSION_File()
    {
        var assembly = typeof(DiagnosticsPageViewModel).Assembly;
        var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Assert.False(string.IsNullOrWhiteSpace(infoVersion));

        var versionPath = FindFileUpwards("VERSION");
        Assert.True(versionPath is not null, "VERSION file not found when walking parent directories from test output.");

        var expected = File.ReadAllText(versionPath!).Trim();
        Assert.Equal(expected, infoVersion);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void Program_Parses_SelfTest_Argument()
    {
        var programType = typeof(NLink.App.App).Assembly.GetType("NLink.App.Program");
        Assert.NotNull(programType);

        var method = programType!.GetMethod("HasSelfTestArgument", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var hasSelfTest = (bool)method!.Invoke(null, new object[] { new[] { "--self-test" } })!;
        var noSelfTest = (bool)method.Invoke(null, new object[] { new[] { "--something-else" } })!;

        Assert.True(hasSelfTest);
        Assert.False(noSelfTest);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void Diagnostics_CopyExport_IncludesRuntimeBasics_AndNoPayloadOrChatHistory()
    {
        var previousTransport = Environment.GetEnvironmentVariable("NLINK_TRANSPORT");
        try
        {
            SessionTimeline.Clear();
            SessionTimeline.Record("Started");
            SessionTimeline.Record("Disconnected", "timeout");
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", "DEVLOCAL");
            var config = TransportRuntimeConfig.Select();
            var vm = new DiagnosticsPageViewModel(static () => { }, config);

            string? copied = null;
            vm.CopyReliabilityLogRequested += (_, text) => copied = text;

            vm.CopyReliabilityLogCommand.Execute(null);

            Assert.False(string.IsNullOrWhiteSpace(copied));
            Assert.Contains("App version:", copied!, StringComparison.Ordinal);
            Assert.Contains("OS:", copied!, StringComparison.Ordinal);
            Assert.Contains("Process architecture:", copied!, StringComparison.Ordinal);
            Assert.Contains("OS architecture:", copied!, StringComparison.Ordinal);
            Assert.Contains("Bridge RID:", copied!, StringComparison.Ordinal);
            Assert.Contains("Transport:", copied!, StringComparison.Ordinal);
            Assert.Contains("Forced by environment:", copied!, StringComparison.Ordinal);
            Assert.Contains("Session timeline (last 30)", copied!, StringComparison.Ordinal);
            Assert.Contains("Started", copied!, StringComparison.Ordinal);
            Assert.Contains("Disconnected | timeout", copied!, StringComparison.Ordinal);

            Assert.DoesNotContain("payloadBase64", copied!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("hello from helper", copied!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sharedKey", copied!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SessionTimeline.Clear();
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", previousTransport);
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void Diagnostics_And_OperationalLog_Redact_Sensitive_Content()
    {
        var previousTransport = Environment.GetEnvironmentVariable("NLINK_TRANSPORT");
        var uniqueChatText = "hello-from-helper-" + Guid.NewGuid().ToString("N");
        var sensitive = string.Join(' ', new[]
        {
            "payloadBase64=ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/==",
            "sharedKey=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "seedBase64=QkFTRTY0U0VFRA==",
            "seed=supersecretseedvalue",
            "identifier=nlink-private-identifier",
            $"chat={uniqueChatText}"
        });

        try
        {
            SessionTimeline.Clear();
            SessionTimeline.Record("ChatReceived", sensitive);
            NknRuntimeDiagnostics.SetLastDisconnectReason(sensitive);
            NknRuntimeDiagnostics.SetLastError("NKN_START_FAILED: " + sensitive);

            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", "DEVLOCAL");
            var config = TransportRuntimeConfig.Select();
            var vm = new DiagnosticsPageViewModel(static () => { }, config);
            string? diagnostics = null;
            vm.CopyReliabilityLogRequested += (_, text) => diagnostics = text;
            vm.CopyReliabilityLogCommand.Execute(null);

            Assert.NotNull(diagnostics);
            Assert.DoesNotContain("payloadBase64", diagnostics!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sharedKey", diagnostics!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("seedBase64", diagnostics!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("identifier=", diagnostics!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(uniqueChatText, diagnostics!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("[redacted]", diagnostics!, StringComparison.OrdinalIgnoreCase);

            var source = "UnitTestPrivacy" + Guid.NewGuid().ToString("N")[..8];
            LocalOperationalLog.Info(source, sensitive);

            var logText = File.ReadAllText(LocalOperationalLog.LogFilePath);
            var matchingLine = logText
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault(line => line.Contains($"[{source}]", StringComparison.Ordinal));

            Assert.False(string.IsNullOrWhiteSpace(matchingLine));
            Assert.DoesNotContain("payloadBase64", matchingLine!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sharedKey", matchingLine!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("seedBase64", matchingLine!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("identifier=", matchingLine!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(uniqueChatText, matchingLine!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("[redacted]", matchingLine!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SessionTimeline.Clear();
            NknRuntimeDiagnostics.SetLastDisconnectReason("(none)");
            NknRuntimeDiagnostics.SetLastError("(none)");
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", previousTransport);
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionTimeline_IsCappedAt30_AndDiagnosticsExportUsesLatestEntries()
    {
        var previousTransport = Environment.GetEnvironmentVariable("NLINK_TRANSPORT");
        try
        {
            SessionTimeline.Clear();
            for (var i = 0; i < 35; i++)
            {
                SessionTimeline.Record("Event" + i.ToString("D2"));
            }

            var snapshot = SessionTimeline.SnapshotRecent(100);
            Assert.Equal(30, snapshot.Count);
            Assert.Equal("Event05", snapshot[0].EventName);
            Assert.Equal("Event34", snapshot[^1].EventName);

            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", "DEVLOCAL");
            var config = TransportRuntimeConfig.Select();
            var vm = new DiagnosticsPageViewModel(static () => { }, config);
            string? export = null;
            vm.CopyReliabilityLogRequested += (_, text) => export = text;
            vm.CopyReliabilityLogCommand.Execute(null);

            Assert.NotNull(export);
            Assert.Contains("Event34", export!, StringComparison.Ordinal);
            Assert.DoesNotContain("Event00", export!, StringComparison.Ordinal);
            Assert.DoesNotContain("Event01", export!, StringComparison.Ordinal);
            Assert.DoesNotContain("Event02", export!, StringComparison.Ordinal);
            Assert.DoesNotContain("Event03", export!, StringComparison.Ordinal);
            Assert.DoesNotContain("Event04", export!, StringComparison.Ordinal);
        }
        finally
        {
            SessionTimeline.Clear();
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", previousTransport);
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void TransportRuntimeConfig_ReleaseDefault_SelectsNkn_WhenBridgeBundled()
    {
        var previousTransport = Environment.GetEnvironmentVariable("NLINK_TRANSPORT");
        var bridgeRid = GetCurrentBridgeRidForTests();
        var bridgeRoot = Path.Combine(AppContext.BaseDirectory, "bridge", bridgeRid);

        try
        {
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", null);
            PrepareFakeBridgeBundle(bridgeRoot);

            var config = TransportRuntimeConfig.Select();

            Assert.Equal("NKN", config.Key);
            Assert.True(config.AutoSelected);
            Assert.False(config.ForcedByEnvironment);
            Assert.False(config.HasStartupWarning);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", previousTransport);
            CleanupDirectoryIfExists(bridgeRoot);
            CleanupDirectoryIfExists(Path.Combine(AppContext.BaseDirectory, "bridge"));
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void TransportRuntimeConfig_ReleaseDefault_SelectsDevLocal_WithWarning_WhenBridgeMissing()
    {
        var previousTransport = Environment.GetEnvironmentVariable("NLINK_TRANSPORT");
        var bridgeRid = GetCurrentBridgeRidForTests();
        var bridgeRoot = Path.Combine(AppContext.BaseDirectory, "bridge", bridgeRid);

        try
        {
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", null);
            CleanupDirectoryIfExists(Path.Combine(AppContext.BaseDirectory, "bridge"));

            var config = TransportRuntimeConfig.Select();

            Assert.Equal("DevLocal", config.Key);
            Assert.False(config.AutoSelected);
            Assert.False(config.ForcedByEnvironment);
            Assert.True(config.HasStartupWarning);
            Assert.Contains("missing the bridge runtime", config.StartupWarningText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", previousTransport);
            CleanupDirectoryIfExists(bridgeRoot);
            CleanupDirectoryIfExists(Path.Combine(AppContext.BaseDirectory, "bridge"));
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void TransportRuntimeConfig_EnvNkn_SelectsNkn_AndHelperFailsLoudlyBeforeConnect_WhenBridgeMissing()
    {
        var previousTransport = Environment.GetEnvironmentVariable("NLINK_TRANSPORT");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", "NKN");
            CleanupDirectoryIfExists(Path.Combine(AppContext.BaseDirectory, "bridge"));

            var config = TransportRuntimeConfig.Select();
            Assert.Equal("NKN", config.Key);
            Assert.True(config.ForcedByEnvironment);

            using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport(
                onJoinAsync: static (_, __) => throw new InvalidOperationException("bridge missing")));
            using var helper = new HelperPageViewModel(
                cancelAction: static () => { },
                config,
                runtime,
                openDiagnosticsAction: static () => { },
                approvalTimeout: TimeSpan.FromMilliseconds(100),
                connectFailureCooldown: TimeSpan.Zero);

            Assert.True(helper.IsStartupBlocked);
            Assert.Equal("Please reinstall.", helper.StatusText);
            Assert.False(helper.ConnectCommand.CanExecute(null));
            Assert.True(helper.ShowOpenDiagnosticsLink);
            Assert.Equal(SessionRuntimeState.Idle, runtime.State);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", previousTransport);
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void TransportRuntimeConfig_EnvDevLocal_SelectsDevLocal()
    {
        var previousTransport = Environment.GetEnvironmentVariable("NLINK_TRANSPORT");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", "DEVLOCAL");
            var config = TransportRuntimeConfig.Select();
            Assert.Equal("DevLocal", config.Key);
            Assert.True(config.ForcedByEnvironment);
            Assert.False(config.AutoSelected);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", previousTransport);
        }
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
    public void RollingFileLogger_CreatesLogFile_AndContainsAppStart()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-smoke-logs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var logPath = Path.Combine(tempDir, "nlink.log");

        try
        {
            var logger = new RollingFileLogger(logPath, maxFileBytes: 1024 * 1024);
            logger.WriteLine("app start | version=0.1.0-alpha.test");

            Assert.True(File.Exists(logPath));
            var text = File.ReadAllText(logPath);
            Assert.Contains("app start", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CleanupDirectoryIfExists(tempDir);
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void RollingFileLogger_Rotates_WhenSizeLimitExceeded()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-smoke-logs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var logPath = Path.Combine(tempDir, "nlink.log");

        try
        {
            var logger = new RollingFileLogger(logPath, maxFileBytes: 80);
            logger.WriteLine(new string('A', 120));
            logger.WriteLine("second line");

            Assert.True(File.Exists(logPath));
            Assert.True(File.Exists(Path.Combine(tempDir, "nlink.1.log")));
            var current = File.ReadAllText(logPath);
            var rotated = File.ReadAllText(Path.Combine(tempDir, "nlink.1.log"));
            Assert.Contains("second line", current, StringComparison.Ordinal);
            Assert.Contains("AAA", rotated, StringComparison.Ordinal);
        }
        finally
        {
            CleanupDirectoryIfExists(tempDir);
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void RollingFileLogger_Rotation_And_Write_NeverThrow_WhenFileLocked()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-smoke-logs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var logPath = Path.Combine(tempDir, "nlink.log");

        try
        {
            File.WriteAllText(logPath, new string('X', 256));
            var logger = new RollingFileLogger(logPath, maxFileBytes: 32);

            using var lockStream = new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var rotateEx = Record.Exception(() => logger.RotateIfNeeded());
            var writeEx = Record.Exception(() => logger.WriteLine("line while locked"));

            Assert.Null(rotateEx);
            Assert.Null(writeEx);
        }
        finally
        {
            CleanupDirectoryIfExists(tempDir);
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
        using var helpeeRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());

        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, transportConfig, helpeeRuntime);
        using var helper = new HelperPageViewModel(cancelAction: static () => { }, transportConfig, helperRuntime);

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
    public async Task SessionRuntime_RepeatCycle_ResetAndRetry_FiveIterations_ReturnsToIdle()
    {
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-" + Guid.NewGuid().ToString("N")));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var helperChatReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var helpeeChatReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        for (var i = 0; i < 5; i++)
        {
            helperChatReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            helpeeChatReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnHelperChat(object? _, ChatMessageEventArgs e) => helperChatReceived.TrySetResult(e.Message.Text);
            void OnHelpeeChat(object? _, ChatMessageEventArgs e) => helpeeChatReceived.TrySetResult(e.Message.Text);

            helperRuntime.ChatMessageReceived += OnHelperChat;
            helpeeRuntime.ChatMessageReceived += OnHelpeeChat;

            var code = new SessionCode((100000 + i).ToString("D6"));

            await helpeeRuntime.StartHelpeeAsync(code, cts.Token);
            await helperRuntime.StartHelperAsync(code, cts.Token);

            await WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.IncomingJoinRequest, TimeSpan.FromSeconds(1));

            await helpeeRuntime.ApproveAsync(cts.Token);

            await WaitUntilAsync(
                () => helpeeRuntime.State == SessionRuntimeState.Connected &&
                      helperRuntime.State == SessionRuntimeState.Connected &&
                      helpeeRuntime.HasSessionKey &&
                      helperRuntime.HasSessionKey,
                TimeSpan.FromSeconds(1));

            var helperText = $"hello-{i}";
            var helpeeText = $"reply-{i}";

            var helperSent = await helperRuntime.TrySendChatTextAsync(helperText, cts.Token);
            Assert.NotNull(helperSent);
            Assert.Equal(helperText, await helpeeChatReceived.Task.WaitAsync(TimeSpan.FromSeconds(1), cts.Token));

            helpeeChatReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            helperRuntime.ChatMessageReceived -= OnHelperChat;
            helpeeRuntime.ChatMessageReceived -= OnHelpeeChat;
            helperRuntime.ChatMessageReceived += OnHelperChat;
            helpeeRuntime.ChatMessageReceived += OnHelpeeChat;

            var helpeeSent = await helpeeRuntime.TrySendChatTextAsync(helpeeText, cts.Token);
            Assert.NotNull(helpeeSent);
            Assert.Equal(helpeeText, await helperChatReceived.Task.WaitAsync(TimeSpan.FromSeconds(1), cts.Token));

            helperRuntime.ChatMessageReceived -= OnHelperChat;
            helpeeRuntime.ChatMessageReceived -= OnHelpeeChat;

            await helperRuntime.ResetAsync();
            await helpeeRuntime.ResetAsync();

            Assert.Equal(SessionRuntimeState.Idle, helperRuntime.State);
            Assert.Equal(SessionRuntimeState.Idle, helpeeRuntime.State);
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task Alpha3ScenarioA_HappyPath_HeadlessSessionRuntime_CompletesConnectAndChat()
    {
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-a-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-a-" + Guid.NewGuid().ToString("N")));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var helperReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var helpeeReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        helperRuntime.ChatMessageReceived += (_, e) => helperReceived.TrySetResult(e.Message.Text);
        helpeeRuntime.ChatMessageReceived += (_, e) => helpeeReceived.TrySetResult(e.Message.Text);

        var code = new SessionCode("321654");

        await helpeeRuntime.StartHelpeeAsync(code, cts.Token);
        await helperRuntime.StartHelperAsync(code, cts.Token);
        await WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.IncomingJoinRequest, TimeSpan.FromSeconds(1));

        await helpeeRuntime.ApproveAsync(cts.Token);

        await WaitUntilAsync(
            () => helpeeRuntime.State == SessionRuntimeState.Connected &&
                  helperRuntime.State == SessionRuntimeState.Connected &&
                  helpeeRuntime.HasSessionKey &&
                  helperRuntime.HasSessionKey,
            TimeSpan.FromSeconds(1));

        Assert.NotNull(await helperRuntime.TrySendChatTextAsync("hello-a", cts.Token));
        Assert.Equal("hello-a", await helpeeReceived.Task.WaitAsync(TimeSpan.FromSeconds(1), cts.Token));

        helperReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        helperRuntime.ChatMessageReceived += (_, e) => helperReceived.TrySetResult(e.Message.Text);
        Assert.NotNull(await helpeeRuntime.TrySendChatTextAsync("reply-a", cts.Token));
        Assert.Equal("reply-a", await helperReceived.Task.WaitAsync(TimeSpan.FromSeconds(1), cts.Token));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_NknRemoteSessionEnd_ShowsFriendlyMessage_AndCanReset()
    {
        FakeNknClient.ResetNetwork();

        try
        {
            var options = NknTransportOptions.Load();
            using var helpeeTransport = new NknSignalingTransport(
                new FakeNknClient("helpee.addr." + Guid.NewGuid().ToString("N")),
                options,
                new NknIdentity("helpee-test", "helpee.test.fake"));
            using var helperTransport = new NknSignalingTransport(
                new FakeNknClient("helper.addr." + Guid.NewGuid().ToString("N")),
                options,
                new NknIdentity("helper-test", "helper.test.fake"));
            using var helpeeRuntime = new SessionRuntime(() => helpeeTransport);
            using var helperRuntime = new SessionRuntime(() => helperTransport);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var code = new SessionCode("345678");

            await helpeeRuntime.StartHelpeeAsync(code, cts.Token);
            await helperRuntime.StartHelperAsync(code, cts.Token);

            await WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.IncomingJoinRequest, TimeSpan.FromSeconds(2));
            await helpeeRuntime.ApproveAsync(cts.Token);

            await WaitUntilAsync(
                () => helpeeRuntime.State == SessionRuntimeState.Connected &&
                      helperRuntime.State == SessionRuntimeState.Connected,
                TimeSpan.FromSeconds(2));

            await helperRuntime.DisconnectAsync();

            await WaitUntilAsync(
                () => helpeeRuntime.State == SessionRuntimeState.Failed &&
                      string.Equals(helpeeRuntime.StatusText, "The helper ended the session.", StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));

            Assert.Equal(SessionRuntimeState.Idle, helperRuntime.State);
            Assert.Equal("The helper ended the session.", helpeeRuntime.StatusText);

            await helpeeRuntime.ResetAsync();
            Assert.Equal(SessionRuntimeState.Idle, helpeeRuntime.State);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task Alpha3ScenarioC_SessionEnd_HeadlessRemoteEnd_ShowsFriendlyMessage()
    {
        FakeNknClient.ResetNetwork();

        try
        {
            var options = NknTransportOptions.Load();
            using var helpeeTransport = new NknSignalingTransport(
                new FakeNknClient("helpee.c.addr." + Guid.NewGuid().ToString("N")),
                options,
                new NknIdentity("helpee-c", "helpee.c.fake"));
            using var helperTransport = new NknSignalingTransport(
                new FakeNknClient("helper.c.addr." + Guid.NewGuid().ToString("N")),
                options,
                new NknIdentity("helper-c", "helper.c.fake"));
            using var helpeeRuntime = new SessionRuntime(() => helpeeTransport);
            using var helperRuntime = new SessionRuntime(() => helperTransport);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

            var code = new SessionCode("456789");
            await helpeeRuntime.StartHelpeeAsync(code, cts.Token);
            await helperRuntime.StartHelperAsync(code, cts.Token);
            await WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.IncomingJoinRequest, TimeSpan.FromSeconds(2));
            await helpeeRuntime.ApproveAsync(cts.Token);
            await WaitUntilAsync(
                () => helpeeRuntime.State == SessionRuntimeState.Connected &&
                      helperRuntime.State == SessionRuntimeState.Connected,
                TimeSpan.FromSeconds(2));

            await helperRuntime.DisconnectAsync();

            await WaitUntilAsync(
                () => helpeeRuntime.State == SessionRuntimeState.Failed &&
                      string.Equals(helpeeRuntime.StatusText, "The helper ended the session.", StringComparison.Ordinal),
                TimeSpan.FromSeconds(2));
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
        using var runtime = new SessionRuntime(() => new FakeSignalingTransport());

        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            transportConfig,
            runtime,
            clipboardService: fakeClipboard,
            shareMessageConfig: shareConfig);

        await helper.CopyInstallMessageCommand.ExecuteAsync(null);

        Assert.Equal(
            "Install nLink and open it." + Environment.NewLine +
            "Download: https://example.com/nlink" + Environment.NewLine,
            fakeClipboard.LastText);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelperViewModel_WrongCode_TransitionsToFailed_WithMappedMessage_AndReconnectEnabled()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport(
            onJoinAsync: static (_, __) => throw new TimeoutException("Could not find session for code")));
        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            transportConfig,
            runtime,
            approvalTimeout: TimeSpan.FromMilliseconds(100),
            connectFailureCooldown: TimeSpan.Zero);

        helper.CodeInput = "123456";
        await helper.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionRuntimeState.Failed, runtime.State);
        Assert.Equal("No one found with that code.", helper.StatusText);
        Assert.True(helper.ConnectCommand.CanExecute(null));
        Assert.Equal("Failed", helper.ConnectionState);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task Alpha3ScenarioB_WrongCodeTimeout_HeadlessHelperVm_ShowsFriendlyFailure_AndReconnect()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport(
            onJoinAsync: static (_, __) => throw new TimeoutException("Could not find session for code")));
        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            transportConfig,
            runtime,
            approvalTimeout: TimeSpan.FromMilliseconds(100),
            connectFailureCooldown: TimeSpan.Zero);

        helper.CodeInput = "654321";
        await helper.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionRuntimeState.Failed, runtime.State);
        Assert.Equal("No one found with that code.", helper.StatusText);
        Assert.True(helper.ConnectCommand.CanExecute(null));
        Assert.Equal("Failed", helper.ConnectionState);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelperViewModel_ApprovalTimeout_TransitionsToFailed_WithMappedMessage()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport(
            onJoinAsync: static (_, __) => Task.CompletedTask));
        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            transportConfig,
            runtime,
            approvalTimeout: TimeSpan.FromMilliseconds(100),
            connectFailureCooldown: TimeSpan.Zero);

        helper.CodeInput = "123456";
        await helper.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionRuntimeState.Failed, runtime.State);
        Assert.Equal("No response yet.", helper.StatusText);
        Assert.True(helper.ConnectCommand.CanExecute(null));
        Assert.Equal("Failed", helper.ConnectionState);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelperViewModel_Cooldown_PreventsRapidSecondConnectAttempt()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var factory = new CountingTransportFactory(() => new ScriptedSignalingTransport(
            onJoinAsync: static (_, __) => throw new TimeoutException("Could not find session for code")));
        using var runtime = new SessionRuntime(factory.Create);
        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            transportConfig,
            runtime,
            approvalTimeout: TimeSpan.FromMilliseconds(100),
            connectFailureCooldown: TimeSpan.FromSeconds(2));

        helper.CodeInput = "123456";

        await helper.ConnectCommand.ExecuteAsync(null);
        await helper.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(1, factory.CreateCount);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_TransportDisconnect_TransitionsToFailed_WithConnectionLost()
    {
        var scripted = new ScriptedSignalingTransport(onJoinAsync: static (_, __) => Task.CompletedTask);
        using var runtime = new SessionRuntime(() => scripted);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        await runtime.StartHelperAsync(new SessionCode("123456"), cts.Token);
        scripted.RaiseDisconnected();

        await WaitUntilAsync(
            () => runtime.State == SessionRuntimeState.Failed &&
                  string.Equals(runtime.StatusText, "Connection lost.", StringComparison.Ordinal),
            TimeSpan.FromSeconds(2));

        Assert.Equal("Connection lost.", runtime.StatusText);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelperViewModel_NknMissing_ShowsFriendlyError_AndDiagnosticsLink()
    {
        var previousTransport = Environment.GetEnvironmentVariable("NLINK_TRANSPORT");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", "NKN");
            CleanupDirectoryIfExists(Path.Combine(AppContext.BaseDirectory, "bridge"));

            var config = TransportRuntimeConfig.Select();
            using var runtime = new SessionRuntime(() => new FakeSignalingTransport());
            using var helper = new HelperPageViewModel(
                cancelAction: static () => { },
                config,
                runtime,
                openDiagnosticsAction: static () => { });

            Assert.True(helper.IsStartupBlocked);
            Assert.Equal("Please reinstall.", helper.StatusText);
            Assert.True(helper.ShowOpenDiagnosticsLink);
            Assert.False(helper.ShowConnectAction);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", previousTransport);
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelperViewModel_Disconnect_ShowsRetry_AndRetryReturnsToIdle()
    {
        var scripted = new ScriptedSignalingTransport(onJoinAsync: static (_, __) => Task.CompletedTask);
        var transportConfig = CreateDevLocalTestConfig();
        using var runtime = new SessionRuntime(() => scripted);
        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            transportConfig,
            runtime,
            connectFailureCooldown: TimeSpan.Zero);

        helper.CodeInput = "123456";
        var connectTask = helper.ConnectCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => runtime.State == SessionRuntimeState.Connecting, TimeSpan.FromSeconds(1));
        scripted.RaiseDisconnected();
        await connectTask;

        await WaitUntilAsync(
            () => runtime.State == SessionRuntimeState.Failed &&
                  helper.ConnectionState == "Failed" &&
                  helper.ShowRetryAction &&
                  string.Equals(helper.StatusText, "Connection lost.", StringComparison.Ordinal),
            TimeSpan.FromSeconds(2));

        await helper.RetryCommand.ExecuteAsync(null);

        await WaitUntilAsync(
            () => runtime.State == SessionRuntimeState.Idle &&
                  helper.ConnectionState == "Idle" &&
                  !helper.ShowRetryAction &&
                  helper.ConnectCommand.CanExecute(null),
            TimeSpan.FromSeconds(2));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task Alpha3ScenarioD_DisconnectAndRetry_HeadlessHelperVm_ReturnsToIdle()
    {
        var scripted = new ScriptedSignalingTransport(onJoinAsync: static (_, __) => Task.CompletedTask);
        var transportConfig = CreateDevLocalTestConfig();
        using var runtime = new SessionRuntime(() => scripted);
        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            transportConfig,
            runtime,
            connectFailureCooldown: TimeSpan.Zero);

        helper.CodeInput = "123456";
        var connectTask = helper.ConnectCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => runtime.State == SessionRuntimeState.Connecting, TimeSpan.FromSeconds(1));
        scripted.RaiseDisconnected();
        await connectTask;

        await WaitUntilAsync(
            () => runtime.State == SessionRuntimeState.Failed &&
                  string.Equals(helper.StatusText, "Connection lost.", StringComparison.Ordinal) &&
                  helper.ShowRetryAction,
            TimeSpan.FromSeconds(2));

        await helper.RetryCommand.ExecuteAsync(null);
        await WaitUntilAsync(
            () => runtime.State == SessionRuntimeState.Idle &&
                  helper.ConnectionState == "Idle" &&
                  helper.ConnectCommand.CanExecute(null),
            TimeSpan.FromSeconds(2));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelpeeViewModel_IncomingJoinRequest_SwitchesToApprovalPanel()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-ui-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-ui-" + Guid.NewGuid().ToString("N")));
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, transportConfig, helpeeRuntime);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        Assert.False(helpee.IsIncomingRequestView);
        Assert.True(helpee.ShowWaitingPanel);

        Assert.True(SessionCode.TryParse(helpee.ShareCode, out var code));
        await helperRuntime.StartHelperAsync(code!, cts.Token);

        await WaitUntilAsync(
            () => helpee.IsIncomingRequestView &&
                  helpee.ShowIncomingRequestPanel &&
                  !helpee.ShowWaitingPanel &&
                  string.Equals(helpee.PageTitle, "Someone wants to connect", StringComparison.Ordinal),
            TimeSpan.FromSeconds(2));
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

    private static string? FindFileUpwards(string fileName)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && current is not null; i++, current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string GetCurrentBridgeRidForTests()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            RuntimeInformation.OSArchitecture == Architecture.X64)
        {
            return "win-x64";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            RuntimeInformation.OSArchitecture == Architecture.X64)
        {
            return "linux-x64";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "osx-x64",
                Architecture.Arm64 => "osx-arm64",
                _ => throw new NotSupportedException("Unsupported macOS architecture for bridge RID test.")
            };
        }

        throw new NotSupportedException("Unsupported platform for bridge RID test.");
    }

    private static void PrepareFakeBridgeBundle(string bridgeRoot)
    {
        CleanupDirectoryIfExists(bridgeRoot);
        Directory.CreateDirectory(bridgeRoot);

        var nodeFileName = OperatingSystem.IsWindows() ? "node.exe" : "node";
        File.WriteAllText(Path.Combine(bridgeRoot, "index.js"), "// fake");
        File.WriteAllText(Path.Combine(bridgeRoot, nodeFileName), "fake");
        Directory.CreateDirectory(Path.Combine(bridgeRoot, "node_modules"));
    }

    private static void CleanupDirectoryIfExists(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        Directory.Delete(path, recursive: true);
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

    private sealed class CountingTransportFactory
    {
        private readonly Func<ISignalingTransport> factory;

        public CountingTransportFactory(Func<ISignalingTransport> factory)
        {
            this.factory = factory;
        }

        public int CreateCount { get; private set; }

        public ISignalingTransport Create()
        {
            CreateCount++;
            return factory();
        }
    }

    private sealed class ScriptedSignalingTransport : ISignalingTransport
    {
        private readonly Func<SessionCode, CancellationToken, Task> onJoinAsync;
        private readonly Func<SessionCode, CancellationToken, Task> onHostAsync;
        private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task> onSendChatAsync;

        public ScriptedSignalingTransport(
            Func<SessionCode, CancellationToken, Task>? onJoinAsync = null,
            Func<SessionCode, CancellationToken, Task>? onHostAsync = null,
            Func<ReadOnlyMemory<byte>, CancellationToken, Task>? onSendChatAsync = null)
        {
            this.onJoinAsync = onJoinAsync ?? ((_, ct) => Task.Delay(Timeout.Infinite, ct));
            this.onHostAsync = onHostAsync ?? ((_, ct) => Task.Delay(Timeout.Infinite, ct));
            this.onSendChatAsync = onSendChatAsync ?? ((_, _) => Task.CompletedTask);
        }

        public event EventHandler<IncomingJoinRequestEventArgs>? IncomingJoinRequest;
        public event EventHandler<TransportSessionKeyReadyEventArgs>? SessionKeyReady;
        public event EventHandler<TransportChatMessageEventArgs>? ChatMessageReceived;
        public event EventHandler? Approved;
        public event EventHandler? Rejected;
        public event EventHandler? Disconnected;

        public void Dispose()
        {
        }

        public Task HostAsync(SessionCode code, CancellationToken ct) => onHostAsync(code, ct);

        public Task JoinAsync(SessionCode code, CancellationToken ct) => onJoinAsync(code, ct);

        public Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => onSendChatAsync(payload, ct);

        public void RaiseDisconnected()
        {
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FakeSessionTransportNetwork
    {
        private readonly object gate = new();
        private readonly Dictionary<string, FakeSessionTransport> hostsByCode = new(StringComparer.Ordinal);

        public FakeSessionTransport CreateTransport(string address)
        {
            return new FakeSessionTransport(this, address);
        }

        public void RegisterHost(string code, FakeSessionTransport host)
        {
            lock (gate)
            {
                hostsByCode[code] = host;
            }
        }

        public void UnregisterHost(FakeSessionTransport transport)
        {
            lock (gate)
            {
                foreach (var pair in hostsByCode.ToArray())
                {
                    if (ReferenceEquals(pair.Value, transport))
                    {
                        hostsByCode.Remove(pair.Key);
                    }
                }
            }
        }

        public FakeSessionTransport? TryFindHost(string code)
        {
            lock (gate)
            {
                return hostsByCode.TryGetValue(code, out var host) ? host : null;
            }
        }
    }

    private sealed class FakeSessionTransport : ISignalingTransport
    {
        private readonly FakeSessionTransportNetwork network;
        private readonly byte[] sharedKey = SmokeTests.SHA256LikeDeterministicBytes("session-runtime-repeat-key", 32);
        private FakeSessionTransport? peer;
        private bool disposed;
        private string? hostedCode;

        public FakeSessionTransport(FakeSessionTransportNetwork network, string address)
        {
            this.network = network;
            Address = address;
        }

        public string Address { get; }

        public event EventHandler<IncomingJoinRequestEventArgs>? IncomingJoinRequest;
        public event EventHandler<TransportSessionKeyReadyEventArgs>? SessionKeyReady;
        public event EventHandler<TransportChatMessageEventArgs>? ChatMessageReceived;
        public event EventHandler? Approved;
        public event EventHandler? Rejected;
        public event EventHandler? Disconnected;

        public Task HostAsync(SessionCode code, CancellationToken ct)
        {
            ThrowIfDisposed();
            hostedCode = code.Digits;
            network.RegisterHost(code.Digits, this);
            return Task.Delay(Timeout.Infinite, ct);
        }

        public Task JoinAsync(SessionCode code, CancellationToken ct)
        {
            ThrowIfDisposed();
            var host = network.TryFindHost(code.Digits) ?? throw new TimeoutException("Host not found.");
            peer = host;
            host.peer = this;

            var joinRequest = new IncomingJoinRequestEventArgs(
                approveAsync: _ =>
                {
                    host.SessionKeyReady?.Invoke(host, new TransportSessionKeyReadyEventArgs(host.sharedKey));
                    SessionKeyReady?.Invoke(this, new TransportSessionKeyReadyEventArgs(sharedKey));
                    host.Approved?.Invoke(host, EventArgs.Empty);
                    Approved?.Invoke(this, EventArgs.Empty);
                    return Task.CompletedTask;
                },
                rejectAsync: _ =>
                {
                    Rejected?.Invoke(this, EventArgs.Empty);
                    return Task.CompletedTask;
                });

            host.IncomingJoinRequest?.Invoke(host, joinRequest);
            return Task.CompletedTask;
        }

        public Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            ThrowIfDisposed();
            var target = peer ?? throw new InvalidOperationException("No peer connected.");
            target.ChatMessageReceived?.Invoke(target, new TransportChatMessageEventArgs(payload.ToArray()));
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            network.UnregisterHost(this);

            if (peer is { } target)
            {
                peer = null;
                target.peer = null;
                target.Disconnected?.Invoke(target, EventArgs.Empty);
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(FakeSessionTransport));
            }
        }
    }
}
