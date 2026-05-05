using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Media.Imaging;
using NLink.App;
using NLink.App.Configuration;
using NLink.App.Services;
using NLink.App.Services.RemoteControl;
using NLink.App.Services.ScreenCapture;
using NLink.App.ViewModels;
using NLink.App.Views;
using NLink.Core;
using NLink.Core.Chat;
using NLink.Core.Configuration;
using NLink.Core.Diagnostics;
using NLink.Core.FileTransfer;
using NLink.Core.Metrics;
using NLink.Core.RemoteControl;
using NLink.Core.Resources;
using NLink.Core.Retry;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Core.Logging;
using NLink.Infra.DevLocal;
using NLink.Infra.Nkn;
using NLink.SmokeTests.Fakes;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class ConfigurationAndRunbookTests : CoreSmokeTestsBase
{
[Trait("Category", "LegacySmoke")]
    [Fact]
    public void FeatureFlags_DefaultsMatchScreenShareReleaseRollout_WhenNoEnvironmentOverridesArePresent()
    {
        Assert.True(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NLINK_FEATURE_CHAT_HARDENING")));
        Assert.True(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD")));
        Assert.True(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_CAPTURE")));
        Assert.True(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_PREVIEW")));
        Assert.True(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_TRANSPORT")));
        Assert.True(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NLINK_FEATURE_RESPONSIVE_LAYOUT")));
        Assert.True(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NLINK_FEATURE_SESSION_HEADER")));

        Assert.True(FeatureFlags.EnableChatHardening);
        Assert.True(FeatureFlags.EnableResponsiveLayout);
        Assert.True(FeatureFlags.EnableScreenShareScaffold);
        Assert.True(FeatureFlags.EnableScreenShareCapture);
        Assert.True(FeatureFlags.EnableScreenSharePreview);
        Assert.True(FeatureFlags.EnableScreenShareTransport);
        Assert.True(FeatureFlags.EnableSessionHeader);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void FeatureFlags_RemoteControlSeqGate_FailsClosedInRelease_WithoutExplicitInsecureOptIn()
    {
        var previousSeqGate = Environment.GetEnvironmentVariable("NLINK_FEATURE_REMOTE_CONTROL_SEQ_GATE");
        var previousOptIn = Environment.GetEnvironmentVariable(FeatureFlags.AllowInsecureRemoteControlSeqGateOverrideEnvVar);

        try
        {
            Environment.SetEnvironmentVariable("NLINK_FEATURE_REMOTE_CONTROL_SEQ_GATE", "0");
            Environment.SetEnvironmentVariable(FeatureFlags.AllowInsecureRemoteControlSeqGateOverrideEnvVar, null);

#if DEBUG
            Assert.False(FeatureFlags.RemoteControlSeqGateEnabled);
#else
            Assert.True(FeatureFlags.RemoteControlSeqGateEnabled);
#endif

            Environment.SetEnvironmentVariable(FeatureFlags.AllowInsecureRemoteControlSeqGateOverrideEnvVar, "1");

#if DEBUG
            Assert.False(FeatureFlags.RemoteControlSeqGateEnabled);
#else
            Assert.True(FeatureFlags.RemoteControlSeqGateEnabled);
            using (EnableUnsafeDeveloperModeForTests())
            {
                Assert.False(FeatureFlags.RemoteControlSeqGateEnabled);
            }
#endif
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_FEATURE_REMOTE_CONTROL_SEQ_GATE", previousSeqGate);
            Environment.SetEnvironmentVariable(FeatureFlags.AllowInsecureRemoteControlSeqGateOverrideEnvVar, previousOptIn);
            ReleaseOverridePolicy.ResetSuppressedOverridesForTests();
        }
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void ReleaseRunbook_IncludesReleaseHardeningChecklistMarkers()
    {
        var runbookPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "ReleaseRunbook.md");
        var runbook = File.ReadAllText(Path.GetFullPath(runbookPath));

        Assert.Contains("Transport abuse-resistance limit matrix:", runbook, StringComparison.Ordinal);
        Assert.Contains("NknSignalingTransport` high-priority control queue: `256`", runbook, StringComparison.Ordinal);
        Assert.Contains("NLINK_UNSAFE_DEVELOPER_MODE", runbook, StringComparison.Ordinal);
        Assert.Contains("file-transfer data-session queue: `512` frames and `32 MiB`", runbook, StringComparison.Ordinal);
        Assert.Contains("filetransfer_data_session_overflow", runbook, StringComparison.Ordinal);
        Assert.Contains("ReceiverBufferExhausted", runbook, StringComparison.Ordinal);
        Assert.Contains("negotiated remote bulk endpoint", runbook, StringComparison.Ordinal);
        Assert.Contains("`64 KiB` payload cap", runbook, StringComparison.Ordinal);
        Assert.Contains("`196,606` body bytes before allocation", runbook, StringComparison.Ordinal);
        Assert.Contains("live NKN file-transfer soak", runbook, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("preferred public release artifacts are Authenticode-signed before publish", runbook, StringComparison.Ordinal);
        Assert.Contains("unsigned public Windows artifacts are an accepted release exception", runbook, StringComparison.Ordinal);
        Assert.Contains("Authenticode status is `Valid`", runbook, StringComparison.Ordinal);
        Assert.Contains("security_relevant_overrides", runbook, StringComparison.Ordinal);
        Assert.Contains("high_priority_control_queue_overflows:", runbook, StringComparison.Ordinal);
        Assert.Contains("high_priority_control_rejected:", runbook, StringComparison.Ordinal);
        Assert.Contains("high_priority_control_coalesced:", runbook, StringComparison.Ordinal);
        Assert.Contains("high_priority_control_dropped_for_stop:", runbook, StringComparison.Ordinal);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void ReleaseDocs_IncludeUnsafeOverrideAndFileTransferSecurityGates()
    {
        var repoRoot = FindRepoRoot();
        var checklist = File.ReadAllText(Path.Combine(repoRoot, "docs", "release", "rc-validation-checklist.md"));
        var releaseNotes = File.ReadAllText(Path.Combine(repoRoot, "docs", "releases", "0.7.0.md"));
        var githubNotes = File.ReadAllText(Path.Combine(repoRoot, "docs", "releases", "0.7.0-github.md"));
        var readme = File.ReadAllText(Path.Combine(repoRoot, "README.md"));

        Assert.Contains("NLINK_UNSAFE_DEVELOPER_MODE", checklist, StringComparison.Ordinal);
        Assert.Contains("512` frames / `32 MiB", checklist, StringComparison.Ordinal);
        Assert.Contains("64 KiB", checklist, StringComparison.Ordinal);
        Assert.Contains("live NKN file-transfer soak", checklist, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bundled `bridge/win-x64/node.exe`", checklist, StringComparison.Ordinal);
        Assert.Contains("package-lock.json", checklist, StringComparison.Ordinal);
        Assert.Contains("bridge-dependencies.json", checklist, StringComparison.Ordinal);
        Assert.Contains("no shipped `node_modules`", checklist, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Node archive SHA-256", checklist, StringComparison.Ordinal);
        Assert.Contains("Unsigned public Windows artifacts are recorded as an accepted release exception", checklist, StringComparison.Ordinal);

        Assert.Contains("V4-only", releaseNotes, StringComparison.Ordinal);
        Assert.Contains("explicit accept/decline", releaseNotes, StringComparison.Ordinal);
        Assert.Contains("session envelope", releaseNotes, StringComparison.Ordinal);
        Assert.Contains("source/session validation", releaseNotes, StringComparison.Ordinal);
        Assert.Contains("NKN transport alone", releaseNotes, StringComparison.Ordinal);
        Assert.Contains("Release exception: Windows artifacts for this release are unsigned", releaseNotes, StringComparison.Ordinal);
        Assert.Contains("High quality", releaseNotes, StringComparison.Ordinal);

        Assert.Contains("V4-only", githubNotes, StringComparison.Ordinal);
        Assert.Contains("explicit accept/decline", githubNotes, StringComparison.Ordinal);
        Assert.Contains("session envelope", githubNotes, StringComparison.Ordinal);
        Assert.Contains("Release exception: Windows artifacts for this release are unsigned", githubNotes, StringComparison.Ordinal);

        Assert.Contains("NLINK_UNSAFE_DEVELOPER_MODE=1", readme, StringComparison.Ordinal);
        Assert.Contains("session envelope", readme, StringComparison.Ordinal);
        Assert.Contains("source/session validation", readme, StringComparison.Ordinal);
        Assert.Contains("Release exception: Windows artifacts for `0.7.0` are unsigned", readme, StringComparison.Ordinal);
        Assert.Contains("no shipped `node_modules`", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("package-lock.json", readme, StringComparison.Ordinal);
        Assert.Contains("bridge-dependencies.json", readme, StringComparison.Ordinal);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void SecurityAuditReport_TreatsFileTransferAsShippedAndResolved()
    {
        var report = File.ReadAllText(Path.Combine(FindRepoRoot(), "SECURITY_AUDIT_REPORT.md"));
        var normalizedReport = report.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("NLINK-SEC-001", report, StringComparison.Ordinal);
        Assert.Contains("NLINK-SEC-002", report, StringComparison.Ordinal);
        Assert.Contains("NLINK-SEC-003", report, StringComparison.Ordinal);
        Assert.Contains("NLINK_UNSAFE_DEVELOPER_MODE=1", report, StringComparison.Ordinal);
        Assert.Contains("file transfer uses the post-handshake session envelope", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("V4-only, single-file, explicit accept/decline", report, StringComparison.Ordinal);
        Assert.Contains("Public Windows installer and portable artifacts are unsigned for this release", report, StringComparison.Ordinal);
        Assert.Contains("remote clipboard", report, StringComparison.Ordinal);
        Assert.Contains("## Out Of Scope Features For This Release", normalizedReport, StringComparison.Ordinal);
        Assert.Contains("\n- remote clipboard\n", normalizedReport, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch("(?is)out of scope[^#]*file transfer", normalizedReport);
        Assert.DoesNotContain("file transfer looks partially wired", report, StringComparison.OrdinalIgnoreCase);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void BridgeSupplyChain_DocsAndScriptsRequireCleanLockedBundle()
    {
        var repoRoot = FindRepoRoot();
        var bridgeScript = File.ReadAllText(Path.Combine(repoRoot, "installer", "Build-BridgeBundle.ps1"));
        var readme = File.ReadAllText(Path.Combine(repoRoot, "README.md"));
        var report = File.ReadAllText(Path.Combine(repoRoot, "SECURITY_AUDIT_REPORT.md"));

        Assert.Contains("ci --ignore-scripts --no-audit --no-fund", bridgeScript, StringComparison.Ordinal);
        Assert.DoesNotContain("npm install", bridgeScript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PinnedNodeWinX64ArchiveSha256", bridgeScript, StringComparison.Ordinal);
        Assert.Contains("nodeModulesShipped = $false", bridgeScript, StringComparison.Ordinal);
        Assert.Contains("bridge-dependencies.json", bridgeScript, StringComparison.Ordinal);

        Assert.DoesNotContain("artifacts/bridge/win-x64/node_modules", readme, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bridge/win-x64/node_modules", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("clean `npm ci`", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no shipped `node_modules`", readme, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("nodeModulesShipped=false", report, StringComparison.Ordinal);
        Assert.Contains("nkn-sdk` `1.3.6", report, StringComparison.Ordinal);
        Assert.DoesNotContain("committed `tools/nkn-bridge/node_modules`", report, StringComparison.OrdinalIgnoreCase);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void BridgeSupplyChain_NoGeneratedNodeRuntimeOrBridgeModulesAreTracked()
    {
        var repoRoot = FindRepoRoot();
        var tracked = RunGitLsFiles(repoRoot, "tools/node", "tools/nkn-bridge/node_modules", "tools/nkn-bridge/.nlink-bundle");

        Assert.Empty(tracked);
    }

    [Trait("Category", "LegacySmoke")]
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

    [Trait("Category", "LegacySmoke")]
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

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void Program_Parses_Benchmark_Argument()
    {
        var programType = typeof(NLink.App.App).Assembly.GetType("NLink.App.Program");
        Assert.NotNull(programType);

        var method = programType!.GetMethod("HasBenchmarkArgument", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var hasBench = (bool)method!.Invoke(null, new object[] { new[] { "--bench" } })!;
        var noBench = (bool)method.Invoke(null, new object[] { new[] { "--something-else" } })!;

        Assert.True(hasBench);
        Assert.False(noBench);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void Program_Parses_Soak_Argument()
    {
        var programType = typeof(NLink.App.App).Assembly.GetType("NLink.App.Program");
        Assert.NotNull(programType);

        var method = programType!.GetMethod("HasSoakArgument", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var hasSoak = (bool)method!.Invoke(null, new object[] { new[] { "--soak" } })!;
        var noSoak = (bool)method.Invoke(null, new object[] { new[] { "--something-else" } })!;

        Assert.True(hasSoak);
        Assert.False(noSoak);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void Program_Parses_ScreenShareSoak_Argument()
    {
        var programType = typeof(NLink.App.App).Assembly.GetType("NLink.App.Program");
        Assert.NotNull(programType);

        var method = programType!.GetMethod("HasScreenShareSoakArgument", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var hasSoak = (bool)method!.Invoke(null, new object[] { new[] { "--screenshare-soak" } })!;
        var noSoak = (bool)method.Invoke(null, new object[] { new[] { "--something-else" } })!;

        Assert.True(hasSoak);
        Assert.False(noSoak);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void BenchmarkRunner_Parses_Defaults_And_Overrides()
    {
        Assert.True(BenchmarkRunner.TryParseOptionsForTests(new[] { "--bench" }, out var defaults, out var defaultError));
        Assert.NotNull(defaults);
        Assert.Equal(string.Empty, defaultError);
        Assert.Equal(50, defaults!.Cycles);
        Assert.Equal(0, defaults.DelayMs);
        Assert.Equal("devlocal", defaults.Transport);
        Assert.Equal(BridgeReuseMode.PerSession, defaults.BridgeReuseMode);
        Assert.False(defaults.MemoryCheck);
        Assert.Equal(5d, defaults.MemoryTolerancePercent);

        Assert.True(BenchmarkRunner.TryParseOptionsForTests(
            new[] { "--bench", "--cycles", "3", "--delay-ms", "25", "--transport", "nkn", "--bridge-reuse-mode", "keepalive", "--memory-check", "--memory-tolerance-percent", "7.5" },
            out var custom,
            out var customError));
        Assert.NotNull(custom);
        Assert.Equal(string.Empty, customError);
        Assert.Equal(3, custom!.Cycles);
        Assert.Equal(25, custom.DelayMs);
        Assert.Equal("nkn", custom.Transport);
        Assert.Equal(BridgeReuseMode.KeepAlive, custom.BridgeReuseMode);
        Assert.True(custom.MemoryCheck);
        Assert.Equal(7.5d, custom.MemoryTolerancePercent);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void BenchmarkRunner_DevLocalInvite_BindsDeterministicHelperIdentity()
    {
        var targetAddress = new PeerAddress(BenchmarkRunner.BuildDevLocalBenchmarkPeerAddressForTests("helpee", 7));
        var helperAddress = new PeerAddress(BenchmarkRunner.BuildDevLocalBenchmarkPeerAddressForTests("helper", 7));

        var (_, invite) = BenchmarkRunner.CreateInviteForTargetForTests(targetAddress, helperAddress);

        Assert.Equal(targetAddress, invite.TargetAddress);
        Assert.Equal(helperAddress, invite.BoundHelperAddress);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void SoakRunner_Parses_And_Maps_To_BenchmarkArgs()
    {
        Assert.True(SoakRunner.TryParseOptionsForTests(new[] { "--soak" }, out var defaults, out var defaultError));
        Assert.NotNull(defaults);
        Assert.Equal(string.Empty, defaultError);
        Assert.False(defaults!.FailOnGate);

        var defaultBenchArgs = SoakRunner.BuildBenchmarkArgsForTests(defaults);
        Assert.Contains("--bench", defaultBenchArgs);
        Assert.DoesNotContain("--reliability-gate", defaultBenchArgs);

        Assert.True(SoakRunner.TryParseOptionsForTests(
            new[] { "--soak", "--cycles", "10", "--delay-ms", "5", "--transport", "devlocal", "--bridge-reuse-mode", "persession", "--fail-on-gate" },
            out var custom,
            out var customError));
        Assert.NotNull(custom);
        Assert.Equal(string.Empty, customError);
        Assert.True(custom!.FailOnGate);

        var mappedArgs = SoakRunner.BuildBenchmarkArgsForTests(custom);
        Assert.Contains("--bench", mappedArgs);
        Assert.Contains("--cycles", mappedArgs);
        Assert.Contains("10", mappedArgs);
        Assert.Contains("--delay-ms", mappedArgs);
        Assert.Contains("5", mappedArgs);
        Assert.Contains("--transport", mappedArgs);
        Assert.Contains("devlocal", mappedArgs);
        Assert.Contains("--bridge-reuse-mode", mappedArgs);
        Assert.Contains("persession", mappedArgs);
        Assert.Contains("--reliability-gate", mappedArgs);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void ScreenShareSoakRunner_Parses_Defaults_And_Overrides()
    {
        Assert.True(ScreenShareSoakRunner.TryParseOptionsForTests(new[] { "--screenshare-soak" }, out var defaults, out var defaultError));
        Assert.NotNull(defaults);
        Assert.Equal(string.Empty, defaultError);
        Assert.Equal(TimeSpan.FromMinutes(5), defaults!.Duration);
        Assert.Equal(TimeSpan.FromSeconds(5), defaults.SampleInterval);

        Assert.True(ScreenShareSoakRunner.TryParseOptionsForTests(
            new[] { "--screenshare-soak", "--seconds", "90", "--sample-interval-seconds", "15" },
            out var custom,
            out var customError));
        Assert.NotNull(custom);
        Assert.Equal(string.Empty, customError);
        Assert.Equal(TimeSpan.FromSeconds(90), custom!.Duration);
        Assert.Equal(TimeSpan.FromSeconds(15), custom.SampleInterval);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void TransportRuntimeConfig_ReleaseDefault_SelectsNkn_WhenBridgeBundled()
    {
#if DEBUG
        return;
#endif
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

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void TransportRuntimeConfig_ReleaseDefault_SelectsDevLocal_WithWarning_WhenBridgeMissing()
    {
#if DEBUG
        return;
#endif
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

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void TransportRuntimeConfig_EnvNkn_SelectsNkn_AndHelperFailsLoudlyBeforeConnect_WhenBridgeMissing()
    {
        var previousTransport = Environment.GetEnvironmentVariable("NLINK_TRANSPORT");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", "NKN");
            CleanupDirectoryIfExists(Path.Combine(AppContext.BaseDirectory, "bridge"));

            var selected = TransportRuntimeConfig.Select();
            Assert.Equal("NKN", selected.Key);
            Assert.True(selected.ForcedByEnvironment);

            var config = CreateStartupBlockedNknTestConfig();
            using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport());
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
            Assert.Equal(SessionRuntimeState.Failed, runtime.State);

            using var scripted = new ScriptedSignalingTransport();
            SetPrivateField(runtime, "transport", scripted);
            InvokePrivateMethod(runtime, "OnTransportDisconnected", scripted, EventArgs.Empty);

            Assert.Equal("Please reinstall.", runtime.StatusText);
            Assert.Equal("Please reinstall.", helper.StatusText);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", previousTransport);
        }
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void TransportRuntimeConfig_EnvDevLocal_SelectsDevLocal()
    {
        var previousTransport = Environment.GetEnvironmentVariable("NLINK_TRANSPORT");
        using var unsafeDeveloperMode = EnableUnsafeDeveloperModeForTests();
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

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void TransportRuntimeConfig_ReleaseEnvDevLocal_IsIgnoredWithoutUnsafeDeveloperMode()
    {
#if DEBUG
        return;
#endif
        var previousTransport = Environment.GetEnvironmentVariable("NLINK_TRANSPORT");
        var bridgeRid = GetCurrentBridgeRidForTests();
        var bridgeRoot = Path.Combine(AppContext.BaseDirectory, "bridge", bridgeRid);

        using var safeReleaseMode = DisableUnsafeDeveloperModeForTests();
        try
        {
            ReleaseOverridePolicy.ResetSuppressedOverridesForTests();
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", "DEVLOCAL");
            PrepareFakeBridgeBundle(bridgeRoot);

            var config = TransportRuntimeConfig.Select();

            Assert.Equal("NKN", config.Key);
            Assert.False(config.ForcedByEnvironment);
            Assert.Contains("NLINK_TRANSPORT:env:transport_devlocal=suppressed", ReleaseOverridePolicy.GetSuppressedOverrideSummaries());
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", previousTransport);
            ReleaseOverridePolicy.ResetSuppressedOverridesForTests();
            CleanupDirectoryIfExists(bridgeRoot);
            CleanupDirectoryIfExists(Path.Combine(AppContext.BaseDirectory, "bridge"));
        }
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void NknTransportOptions_ReleaseUnsafeOverrides_AreIgnoredWithoutUnsafeDeveloperMode()
    {
#if DEBUG
        return;
#endif
        var previousKeyPath = Environment.GetEnvironmentVariable("NLINK_NKN_KEY_PATH");
        var previousIdentifier = Environment.GetEnvironmentVariable("NLINK_NKN_IDENTIFIER");
        var previousPreflight = Environment.GetEnvironmentVariable("NLINK_NKN_PREFLIGHT_RPC_ENABLED");
        var previousBulkClients = Environment.GetEnvironmentVariable("NLINK_NKN_BULK_NUM_SUBCLIENTS");
        var previousFastRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY");

        using var safeReleaseMode = DisableUnsafeDeveloperModeForTests();
        try
        {
            ReleaseOverridePolicy.ResetSuppressedOverridesForTests();
            Environment.SetEnvironmentVariable("NLINK_NKN_KEY_PATH", Path.Combine(Path.GetTempPath(), "unsafe-ignored-identity.json"));
            Environment.SetEnvironmentVariable("NLINK_NKN_IDENTIFIER", "unsafe-ignored-id");
            Environment.SetEnvironmentVariable("NLINK_NKN_PREFLIGHT_RPC_ENABLED", "true");
            Environment.SetEnvironmentVariable("NLINK_NKN_BULK_NUM_SUBCLIENTS", "9");
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", "false");

            var options = NknTransportOptions.Load();

            Assert.DoesNotContain("unsafe-ignored-identity.json", options.KeyPath, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual("unsafe-ignored-id", options.Identifier);
            Assert.False(options.PreflightRpcEnabled);
            Assert.Equal(4, options.BulkNumSubClients);
            Assert.True(options.ReceiveStallFileTransferFastRecoveryEnabled);
            Assert.Contains("NLINK_NKN_KEY_PATH:env:nkn_identity=suppressed", ReleaseOverridePolicy.GetSuppressedOverrideSummaries());
            Assert.Contains("NLINK_NKN_PREFLIGHT_RPC_ENABLED:env:nkn_tuning=suppressed", ReleaseOverridePolicy.GetSuppressedOverrideSummaries());
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_KEY_PATH", previousKeyPath);
            Environment.SetEnvironmentVariable("NLINK_NKN_IDENTIFIER", previousIdentifier);
            Environment.SetEnvironmentVariable("NLINK_NKN_PREFLIGHT_RPC_ENABLED", previousPreflight);
            Environment.SetEnvironmentVariable("NLINK_NKN_BULK_NUM_SUBCLIENTS", previousBulkClients);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", previousFastRecovery);
            ReleaseOverridePolicy.ResetSuppressedOverridesForTests();
        }
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void NknTransportOptions_ReleaseUnsafeOverrides_AreHonoredWithUnsafeDeveloperMode()
    {
        var previousKeyPath = Environment.GetEnvironmentVariable("NLINK_NKN_KEY_PATH");
        var previousIdentifier = Environment.GetEnvironmentVariable("NLINK_NKN_IDENTIFIER");
        var previousPreflight = Environment.GetEnvironmentVariable("NLINK_NKN_PREFLIGHT_RPC_ENABLED");
        var previousBulkClients = Environment.GetEnvironmentVariable("NLINK_NKN_BULK_NUM_SUBCLIENTS");
        var previousFastRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY");

        using var unsafeDeveloperMode = EnableUnsafeDeveloperModeForTests();
        try
        {
            var keyPath = Path.Combine(Path.GetTempPath(), "unsafe-honored-identity.json");
            Environment.SetEnvironmentVariable("NLINK_NKN_KEY_PATH", keyPath);
            Environment.SetEnvironmentVariable("NLINK_NKN_IDENTIFIER", "unsafe-honored-id");
            Environment.SetEnvironmentVariable("NLINK_NKN_PREFLIGHT_RPC_ENABLED", "true");
            Environment.SetEnvironmentVariable("NLINK_NKN_BULK_NUM_SUBCLIENTS", "9");
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", "false");

            var options = NknTransportOptions.Load();

            Assert.Equal(Path.GetFullPath(keyPath), options.KeyPath);
            Assert.Equal("unsafe-honored-id", options.Identifier);
            Assert.True(options.PreflightRpcEnabled);
            Assert.Equal(9, options.BulkNumSubClients);
            Assert.False(options.ReceiveStallFileTransferFastRecoveryEnabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_KEY_PATH", previousKeyPath);
            Environment.SetEnvironmentVariable("NLINK_NKN_IDENTIFIER", previousIdentifier);
            Environment.SetEnvironmentVariable("NLINK_NKN_PREFLIGHT_RPC_ENABLED", previousPreflight);
            Environment.SetEnvironmentVariable("NLINK_NKN_BULK_NUM_SUBCLIENTS", previousBulkClients);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", previousFastRecovery);
        }
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void FileTransferPayloadEfficiencyProfile_ReleaseOverrideRequiresUnsafeDeveloperMode()
    {
        var previousProfile = Environment.GetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName);

        try
        {
            Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, nameof(FileTransferPayloadEfficiencyProfileKind.LargeSingle48KiB));

            using (DisableUnsafeDeveloperModeForTests())
            {
                var safeProfile = FileTransferPayloadEfficiencyProfile.ResolveRequestedFromEnvironment(out var safeReason);
#if DEBUG
                Assert.Equal(FileTransferPayloadEfficiencyProfileKind.LargeSingle48KiB, safeProfile.Kind);
#else
                Assert.Equal(FileTransferPayloadEfficiencyProfileKind.Current, safeProfile.Kind);
                Assert.Equal("current_default", safeReason);
#endif
            }

            using (EnableUnsafeDeveloperModeForTests())
            {
                var unsafeProfile = FileTransferPayloadEfficiencyProfile.ResolveRequestedFromEnvironment(out var unsafeReason);
                Assert.Equal(FileTransferPayloadEfficiencyProfileKind.LargeSingle48KiB, unsafeProfile.Kind);
                Assert.Equal("env_profile", unsafeReason);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, previousProfile);
            ReleaseOverridePolicy.ResetSuppressedOverridesForTests();
        }
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void ReleaseOverridePolicy_BridgePathOverridesRequireUnsafeDeveloperMode()
    {
        var previousBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", Path.Combine(Path.GetTempPath(), "unsafe-bridge.js"));

            using (DisableUnsafeDeveloperModeForTests())
            {
                ReleaseOverridePolicy.ResetSuppressedOverridesForTests();
                var safeValue = ReleaseOverridePolicy.ReadUnsafeEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", category: "bridge_runtime_path");
#if DEBUG
                Assert.EndsWith("unsafe-bridge.js", safeValue, StringComparison.OrdinalIgnoreCase);
#else
                Assert.Null(safeValue);
                Assert.Contains("NLINK_NKN_BRIDGE_PATH:env:bridge_runtime_path=suppressed", ReleaseOverridePolicy.GetSuppressedOverrideSummaries());
#endif
            }

            using (EnableUnsafeDeveloperModeForTests())
            {
                var unsafeValue = ReleaseOverridePolicy.ReadUnsafeEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", category: "bridge_runtime_path");
                Assert.EndsWith("unsafe-bridge.js", unsafeValue, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", previousBridgePath);
            ReleaseOverridePolicy.ResetSuppressedOverridesForTests();
        }
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public void InviteLegacyMode_ReleaseOverrideRequiresUnsafeDeveloperMode()
    {
        var previousMode = Environment.GetEnvironmentVariable(InviteTokenServiceFactory.InviteModeEnvVar);
        var previousOptIn = Environment.GetEnvironmentVariable(InviteTokenServiceFactory.AllowInsecureLegacyInviteModeEnvVar);

        try
        {
            Environment.SetEnvironmentVariable(InviteTokenServiceFactory.InviteModeEnvVar, InviteTokenServiceFactory.InviteModeLegacySigned);
            Environment.SetEnvironmentVariable(InviteTokenServiceFactory.AllowInsecureLegacyInviteModeEnvVar, "1");

            using (DisableUnsafeDeveloperModeForTests())
            {
#if DEBUG
                Assert.Equal(InviteTokenServiceFactory.InviteModeLegacySigned, InviteTokenServiceFactory.GetInviteMode());
#else
                Assert.Equal(InviteTokenServiceFactory.InviteModeIssuedSecret, InviteTokenServiceFactory.GetInviteMode());
                Assert.False(InviteTokenServiceFactory.IsLegacyInviteModeExplicitlyAllowed());
#endif
            }

            using (EnableUnsafeDeveloperModeForTests())
            {
                Assert.Equal(InviteTokenServiceFactory.InviteModeLegacySigned, InviteTokenServiceFactory.GetInviteMode());
                Assert.True(InviteTokenServiceFactory.IsLegacyInviteModeExplicitlyAllowed());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(InviteTokenServiceFactory.InviteModeEnvVar, previousMode);
            Environment.SetEnvironmentVariable(InviteTokenServiceFactory.AllowInsecureLegacyInviteModeEnvVar, previousOptIn);
            ReleaseOverridePolicy.ResetSuppressedOverridesForTests();
        }
    }

    private static string[] RunGitLsFiles(string repoRoot, params string[] paths)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("ls-files");
        foreach (var path in paths)
        {
            startInfo.ArgumentList.Add(path);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start git.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git ls-files failed with exit {process.ExitCode}: {error}");
        }

        return output
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0)
            .ToArray();
    }

}
