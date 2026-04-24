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

            Assert.False(FeatureFlags.RemoteControlSeqGateEnabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_FEATURE_REMOTE_CONTROL_SEQ_GATE", previousSeqGate);
            Environment.SetEnvironmentVariable(FeatureFlags.AllowInsecureRemoteControlSeqGateOverrideEnvVar, previousOptIn);
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
        Assert.Contains("public release artifacts must be Authenticode-signed before publish", runbook, StringComparison.Ordinal);
        Assert.Contains("Authenticode status is `Valid`", runbook, StringComparison.Ordinal);
        Assert.Contains("security_relevant_overrides", runbook, StringComparison.Ordinal);
        Assert.Contains("high_priority_control_queue_overflows:", runbook, StringComparison.Ordinal);
        Assert.Contains("high_priority_control_rejected:", runbook, StringComparison.Ordinal);
        Assert.Contains("high_priority_control_coalesced:", runbook, StringComparison.Ordinal);
        Assert.Contains("high_priority_control_dropped_for_stop:", runbook, StringComparison.Ordinal);
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

}
