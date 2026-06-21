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
public sealed class BridgeConnectionLifecycleTests : SessionRuntimeConnectionTestBase, IDisposable
{
    private readonly string? previousUnsafeDeveloperMode = Environment.GetEnvironmentVariable(ReleaseOverridePolicy.UnsafeDeveloperModeEnvVar);
    private readonly IDisposable unsafeDeveloperModeOverride = EnableUnsafeDeveloperModeForTests();

    public BridgeConnectionLifecycleTests()
    {
        Environment.SetEnvironmentVariable(ReleaseOverridePolicy.UnsafeDeveloperModeEnvVar, "1");
    }

    public void Dispose()
    {
        unsafeDeveloperModeOverride.Dispose();
        Environment.SetEnvironmentVariable(ReleaseOverridePolicy.UnsafeDeveloperModeEnvVar, previousUnsafeDeveloperMode);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_Startup_HealthCheck()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = ResolveBridgeRuntimeDirectoryForHealthCheck(out var attemptedPath, out var runtimeDirSource);
        Assert.True(bundleDir is not null, $"Bridge runtime not found. Source={runtimeDirSource}, attempted='{attemptedPath}'. Build artifacts/bridge/win-x64 first (run installer/Build-BridgeBundle.ps1).");
        var nodePath = Path.Combine(bundleDir!, "node.exe");
        var bundledBridgePath = Path.Combine(bundleDir!, "index.js");
        var bundledManifestPath = Path.Combine(bundleDir!, "bridge-manifest.json");
        var workspaceBridgePath = FindFileUpwards(Path.Combine("tools", "nkn-bridge", "index.js"));
        var bridgePath = File.Exists(bundledBridgePath) && File.Exists(bundledManifestPath)
            ? bundledBridgePath
            : workspaceBridgePath ?? bundledBridgePath;
        Assert.True(File.Exists(nodePath), $"Bridge runtime not found. Expected bundled node at '{nodePath}'. Run installer/Build-BridgeBundle.ps1.");
        Assert.True(File.Exists(bridgePath), $"Bridge script not found. Expected bundled bridge script at '{bridgePath}' or workspace tools/nkn-bridge/index.js.");
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

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_Startup_WithMockBridge_DelayedPong_EmitsReadyAfterPong()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-delay", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var bridgePath = Path.Combine(tempDir, "mock-bridge-delay.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildMockBridgeScript(delayPongMs: 250, respondToPing: true));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            var options = NknTransportOptions.Load();
            var identity = new NknIdentity("mock-delay", "mock-delay.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            BridgeLifecycleEvent? readyEvent = null;
            adapter.BridgeLifecycle += (_, e) =>
            {
                if (e.Kind == BridgeLifecycleEventKind.Ready)
                {
                    readyEvent = e;
                }
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var sw = Stopwatch.StartNew();
            await adapter.StartBridgeAsync(cts.Token);
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds >= 150, "Bridge start completed before delayed pong should have arrived.");
            Assert.True(adapter.IsBridgeProcessRunning);
            Assert.True(readyEvent.HasValue);
            Assert.Equal(BridgeLifecycleEventKind.Ready, readyEvent.Value.Kind);
            Assert.True(readyEvent.Value.PingRttMs.HasValue);
            Assert.True(readyEvent.Value.PingRttMs.Value >= 150);
            Assert.True(NknRuntimeDiagnostics.Snapshot().BridgeLastPongUtcTicks > 0);
            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Trait("Category", "BridgeStabilityPromotion")]
    [Fact]
    public async Task Bridge_Startup_WithMockBridge_NoPong_FailsAsBridgeUnresponsive()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-nopong", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var bridgePath = Path.Combine(tempDir, "mock-bridge-nopong.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildMockBridgeScript(delayPongMs: 0, respondToPing: false));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            var options = NknTransportOptions.Load();
            var identity = new NknIdentity("mock-nopong", "mock-nopong.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.StartBridgeAsync(cts.Token));
            Assert.Contains("hello failed", ex.Message, StringComparison.OrdinalIgnoreCase);
            var snapshot = NknRuntimeDiagnostics.Snapshot();
            Assert.Contains("NKN_START_FAILED", snapshot.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("bridge_unresponsive", snapshot.LastError, StringComparison.OrdinalIgnoreCase);
            var failure = TransportFailureMapper.FromSignals(snapshot.LastError);
            Assert.Equal(TransportFailureCategory.BridgeUnresponsive, failure.Category);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ConcurrentConnectAsync_SharesSingleConnectAttempt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-concurrent-connect", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-concurrent.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildMockBridgeScriptWithCustomConnect(connectBehaviorJs: $@"
    connectCount++;
    fs.writeFileSync({JsonSerializer.Serialize(countFile)}, String(connectCount));
    emit({{ event:'ok', id: msg.id ?? null, cmd:'connect' }});
    setTimeout(() => emit({{ event:'ready', protocol:2, channels:['control','media','bulk'], address:'mock.concurrent.addr', controlAddress:'mock.concurrent.addr', mediaAddress:'mock.concurrent-media.addr', bulkAddress:'mock.concurrent-bulk.addr', connectId: msg.connectId ?? null }}), 200);
    return;
    "));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            var options = LoadNknOptionsWithOverrides(Path.Combine(tempDir, "id.json"), "mock-concurrent");
            var identity = NknIdentityStore.LoadOrCreate(options);
            using var adapter = new RealNknClientAdapter(identity, options);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var t1 = adapter.ConnectAsync(cts.Token);
            var t2 = adapter.ConnectAsync(cts.Token);
            await Task.WhenAll(t1, t2);
            await adapter.DisconnectAsync();
            var connectCountText = File.Exists(countFile) ? File.ReadAllText(countFile).Trim() : string.Empty;
            Assert.Equal("1", connectCountText);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_SameIdentityAcrossAdapters_WaitsForEarlierAdapterToDisconnect()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-same-identity-lease", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var bridgePath = Path.Combine(tempDir, "mock-bridge-same-identity-lease.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildMockBridgeScriptWithCustomConnect(connectBehaviorJs: $@"
    emit({{ event:'ok', id: msg.id ?? null, cmd:'connect' }});
    setTimeout(() => emit({{ event:'ready', protocol:2, channels:['control','media','bulk'], address:'mock.same.identity.addr', controlAddress:'mock.same.identity.addr', mediaAddress:'mock.same.identity-media.addr', bulkAddress:'mock.same.identity-bulk.addr', connectId: msg.connectId ?? null }}), 80);
    return;
    "));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            var keyPath = Path.Combine(tempDir, "id.json");
            var options = LoadNknOptionsWithOverrides(keyPath, "mock-same-identity");
            var identity = NknIdentityStore.LoadOrCreate(options);
            using var adapter1 = new RealNknClientAdapter(identity, options);
            using var adapter2 = new RealNknClientAdapter(identity, options);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await adapter1.ConnectAsync(cts.Token);
            var secondConnect = adapter2.ConnectAsync(cts.Token);
            await Task.Delay(300, cts.Token);
            Assert.False(secondConnect.IsCompleted, "Second adapter should wait until the first adapter releases the identity lease.");
            await adapter1.DisconnectAsync();
            await secondConnect;
            await adapter2.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_StaleReady_Ignored_UntilMatchingConnectIdArrives()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-stale-ready", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var bridgePath = Path.Combine(tempDir, "mock-bridge-stale-ready.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildMockBridgeScriptWithCustomConnect(connectBehaviorJs: @"
    emit({ event:'ok', id: msg.id ?? null, cmd:'connect' });
    setTimeout(() => emit({ event:'ready', protocol:2, channels:['control','media','bulk'], address:'wrong.addr', controlAddress:'wrong.addr', mediaAddress:'wrong-media.addr', bulkAddress:'wrong-bulk.addr', connectId:'ffffffffffffffffffffffffffffffff' }), 50);
    setTimeout(() => emit({ event:'ready', protocol:2, channels:['control','media','bulk'], address:'correct.addr', controlAddress:'correct.addr', mediaAddress:'correct-media.addr', bulkAddress:'correct-bulk.addr', connectId: msg.connectId ?? null }), 220);
    return;
    "));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            var options = LoadNknOptionsWithOverrides(Path.Combine(tempDir, "id.json"), "mock-stale-ready");
            var identity = NknIdentityStore.LoadOrCreate(options);
            using var adapter = new RealNknClientAdapter(identity, options);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var sw = Stopwatch.StartNew();
            await adapter.ConnectAsync(cts.Token);
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds >= 150, "ConnectAsync completed too early; stale ready may have been accepted.");
            Assert.Equal("correct.addr", adapter.Address);
            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ConnectFailure_ResetsInflight_AndUsesNewConnectIdNextAttempt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-connect-reset", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var idsFile = Path.Combine(tempDir, "connect-ids.json");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-connect-reset.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildMockBridgeScriptWithCustomConnect(connectBehaviorJs: $@"
    connectIds.push(String(msg.connectId || ''));
    fs.writeFileSync({JsonSerializer.Serialize(idsFile)}, JSON.stringify(connectIds));
    emit({{ event:'ok', id: msg.id ?? null, cmd:'connect' }});
    if (connectIds.length >= 2) {{
      setTimeout(() => emit({{ event:'ready', protocol:2, channels:['control','media','bulk'], address:'second-success.addr', controlAddress:'second-success.addr', mediaAddress:'second-success-media.addr', bulkAddress:'second-success-bulk.addr', connectId: msg.connectId ?? null }}), 40);
    }}
    return;
    "));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            var options = LoadNknOptionsWithOverrides(Path.Combine(tempDir, "id.json"), "mock-connect-reset");
            var identity = NknIdentityStore.LoadOrCreate(options);
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.SetConnectReadyTimeoutForTests(TimeSpan.FromMilliseconds(120));
            await Assert.ThrowsAnyAsync<TimeoutException>(() => adapter.ConnectAsync(CancellationToken.None));
            await adapter.ConnectAsync(CancellationToken.None);
            Assert.Equal("second-success.addr", adapter.Address);
            await adapter.DisconnectAsync();
            var idsJson = File.Exists(idsFile) ? File.ReadAllText(idsFile) : "[]";
            using var doc = JsonDocument.Parse(idsJson);
            var ids = doc.RootElement.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray();
            Assert.True(ids.Length >= 2);
            Assert.All(ids, id => Assert.Matches("^[0-9a-f]{32}$", id));
            Assert.NotEqual(ids[0], ids[1]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ReadyMissingBulkChannel_FailsFastWithUpgradeMessage()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-missing-bulk", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var bridgePath = Path.Combine(tempDir, "mock-bridge-missing-bulk.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildMockBridgeScriptWithCustomConnect(connectBehaviorJs: @"
    emit({ event:'ok', id: msg.id ?? null, cmd:'connect' });
    setTimeout(() => emit({
      event:'ready',
      protocol:2,
      channels:['control','media'],
      address:'legacy.addr',
      controlAddress:'legacy.addr',
      mediaAddress:'legacy-media.addr',
      connectId: msg.connectId ?? null
    }), 40);
    return;
    "));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            var options = LoadNknOptionsWithOverrides(Path.Combine(tempDir, "id.json"), "mock-missing-bulk");
            var identity = NknIdentityStore.LoadOrCreate(options);
            using var adapter = new RealNknClientAdapter(identity, options);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ConnectAsync(CancellationToken.None));
            Assert.Contains("bridge_protocol_outdated_bulk_missing", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("reinstall/update nLink package", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_FakeRuntime_BulkSend_DoesNotBlockMediaQueueIngress()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        var bridgePath = FindFileUpwards(Path.Combine("tools", "nkn-bridge", "index.js"));
        if (!File.Exists(nodePath) || bridgePath is null || !File.Exists(bridgePath))
        {
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = nodePath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };
        process.StartInfo.ArgumentList.Add(bridgePath);
        process.StartInfo.Environment["NLINK_BRIDGE_FAKE_NKN_RUNTIME"] = "1";
        process.StartInfo.Environment["NLINK_BRIDGE_FAKE_BULK_SEND_DELAY_MS"] = "1500";
        process.StartInfo.Environment["NLINK_BRIDGE_FAKE_MEDIA_SEND_DELAY_MS"] = "0";

        var stdoutLines = new ConcurrentQueue<string>();
        var stderrLines = new ConcurrentQueue<string>();
        Assert.True(process.Start());
        var stdoutTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cts.Token);
                if (line is null)
                {
                    break;
                }

                stdoutLines.Enqueue(line);
            }
        }, CancellationToken.None);
        var stderrTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cts.Token);
                if (line is null)
                {
                    break;
                }

                stderrLines.Enqueue(line);
            }
        }, CancellationToken.None);

        try
        {
            using var writer = new BridgeStdioWriter(process.StandardInput.BaseStream, leaveOpen: true);
            await writer.WriteJsonLineAsync("{\"cmd\":\"hello\",\"id\":\"hello\",\"protocol\":2}", cts.Token);
            await WaitUntilAsync(() => stdoutLines.Any(line => line.Contains("\"event\":\"hello_ok\"", StringComparison.Ordinal)), TimeSpan.FromSeconds(2));
            await writer.WriteJsonLineAsync("{\"cmd\":\"connect\",\"id\":\"connect\",\"identifier\":\"fake-bridge-hol\"}", cts.Token);
            await WaitUntilAsync(() => stdoutLines.Any(line => line.Contains("\"event\":\"ready\"", StringComparison.Ordinal)), TimeSpan.FromSeconds(2));

            await writer.WriteSendFrameAsync("peer.bulk.fake", new byte[] { 1, 2, 3 }, NknBridgeChannel.Bulk, cts.Token);
            var mediaStart = Stopwatch.StartNew();
            await writer.WriteSendFrameAsync("peer.media.fake", new byte[] { 4, 5, 6 }, NknBridgeChannel.Media, cts.Token);
            await WaitUntilAsync(
                () => stdoutLines.Any(line =>
                    line.Contains("\"event\":\"screen_share_queue_state\"", StringComparison.Ordinal) &&
                    line.Contains("\"queueDepth\":1", StringComparison.Ordinal)),
                TimeSpan.FromMilliseconds(900));

            Assert.True(
                mediaStart.ElapsedMilliseconds < 1200,
                "Media queue ingress was delayed behind the fake slow bulk send. stderr=" + string.Join(" | ", stderrLines.TakeLast(5)));

            await writer.WriteJsonLineAsync("{\"cmd\":\"shutdown\",\"id\":\"shutdown\"}", cts.Token);
            await WaitUntilAsync(() => process.HasExited, TimeSpan.FromSeconds(2));
        }
        finally
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

            await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None));
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_FakeRuntime_BulkSendConcurrency_AllowsParallelBulkSends()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        var bridgePath = FindFileUpwards(Path.Combine("tools", "nkn-bridge", "index.js"));
        if (!File.Exists(nodePath) || bridgePath is null || !File.Exists(bridgePath))
        {
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = nodePath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };
        process.StartInfo.ArgumentList.Add(bridgePath);
        process.StartInfo.Environment["NLINK_BRIDGE_FAKE_NKN_RUNTIME"] = "1";
        process.StartInfo.Environment["NLINK_BRIDGE_FAKE_BULK_SEND_DELAY_MS"] = "1500";

        var stdoutLines = new ConcurrentQueue<string>();
        var stderrLines = new ConcurrentQueue<string>();
        Assert.True(process.Start());
        var stdoutTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cts.Token);
                if (line is null)
                {
                    break;
                }

                stdoutLines.Enqueue(line);
            }
        }, CancellationToken.None);
        var stderrTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cts.Token);
                if (line is null)
                {
                    break;
                }

                stderrLines.Enqueue(line);
            }
        }, CancellationToken.None);

        try
        {
            using var writer = new BridgeStdioWriter(process.StandardInput.BaseStream, leaveOpen: true);
            await writer.WriteJsonLineAsync("{\"cmd\":\"hello\",\"id\":\"hello\",\"protocol\":2}", cts.Token);
            await WaitUntilAsync(() => stdoutLines.Any(line => line.Contains("\"event\":\"hello_ok\"", StringComparison.Ordinal)), TimeSpan.FromSeconds(2));
            await writer.WriteJsonLineAsync("{\"cmd\":\"connect\",\"id\":\"connect\",\"identifier\":\"fake-bridge-bulk-concurrency\",\"bulkSendConcurrency\":4}", cts.Token);
            await WaitUntilAsync(() => stdoutLines.Any(line => line.Contains("\"event\":\"ready\"", StringComparison.Ordinal)), TimeSpan.FromSeconds(2));

            for (var index = 0; index < 4; index++)
            {
                await writer.WriteSendFrameAsync("peer.bulk.fake", new byte[] { (byte)index, 2, 3 }, NknBridgeChannel.Bulk, cts.Token);
            }

            await WaitUntilAsync(
                () => TryGetMaxJsonLong(stdoutLines, "bulk_queue_state", "inFlight") >= 2,
                TimeSpan.FromSeconds(2));
            await WaitUntilAsync(
                () => TryGetMaxJsonLong(stdoutLines, "bridge_bulk_send_summary", "in_flight_max") >= 2,
                TimeSpan.FromSeconds(4));
            Assert.Equal(4, TryGetMaxJsonLong(stdoutLines, "bridge_bulk_send_summary", "effective_concurrency"));

            await writer.WriteJsonLineAsync("{\"cmd\":\"shutdown\",\"id\":\"shutdown\"}", cts.Token);
            await WaitUntilAsync(() => process.HasExited, TimeSpan.FromSeconds(2));
        }
        finally
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

            await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None));
        }

        Assert.DoesNotContain(stderrLines, line => line.Contains("failed", StringComparison.OrdinalIgnoreCase));
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_FakeRuntime_BulkTransientClientNotReady_IsRetried()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        var bridgePath = FindFileUpwards(Path.Combine("tools", "nkn-bridge", "index.js"));
        if (!File.Exists(nodePath) || bridgePath is null || !File.Exists(bridgePath))
        {
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = nodePath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };
        process.StartInfo.ArgumentList.Add(bridgePath);
        process.StartInfo.Environment["NLINK_BRIDGE_FAKE_NKN_RUNTIME"] = "1";
        process.StartInfo.Environment["NLINK_BRIDGE_FAKE_BULK_SEND_CLIENT_NOT_READY_COUNT"] = "12";
        process.StartInfo.Environment["NLINK_NKN_BULK_SEND_MODE"] = "fanout";

        var stdoutLines = new ConcurrentQueue<string>();
        var stderrLines = new ConcurrentQueue<string>();
        Assert.True(process.Start());
        var stdoutTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cts.Token);
                if (line is null)
                {
                    break;
                }

                stdoutLines.Enqueue(line);
            }
        }, CancellationToken.None);
        var stderrTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cts.Token);
                if (line is null)
                {
                    break;
                }

                stderrLines.Enqueue(line);
            }
        }, CancellationToken.None);

        try
        {
            using var writer = new BridgeStdioWriter(process.StandardInput.BaseStream, leaveOpen: true);
            await writer.WriteJsonLineAsync("{\"cmd\":\"hello\",\"id\":\"hello\",\"protocol\":2}", cts.Token);
            await WaitUntilAsync(() => stdoutLines.Any(line => line.Contains("\"event\":\"hello_ok\"", StringComparison.Ordinal)), TimeSpan.FromSeconds(2));
            await writer.WriteJsonLineAsync("{\"cmd\":\"connect\",\"id\":\"connect\",\"identifier\":\"fake-bridge-bulk-transient\"}", cts.Token);
            await WaitUntilAsync(() => stdoutLines.Any(line => line.Contains("\"event\":\"ready\"", StringComparison.Ordinal)), TimeSpan.FromSeconds(2));

            await writer.WriteSendFrameAsync("peer.bulk.fake", new byte[] { 1, 2, 3 }, NknBridgeChannel.Bulk, cts.Token);

            await WaitUntilAsync(
                () => TryGetMaxJsonLong(stdoutLines, "bridge_bulk_send_summary", "frames_sent") >= 1,
                TimeSpan.FromSeconds(15));
            Assert.Equal(0, TryGetMaxJsonLong(stdoutLines, "bridge_bulk_send_summary", "send_failures"));
            Assert.True(
                stderrLines.Count(line => line.Contains("Bulk queue transient not-ready backoff scheduled", StringComparison.Ordinal)) >= 12,
                "Expected bridge bulk transient backoff budget to cover a multi-second client-not-ready window.");
            Assert.True(
                TryGetMaxJsonLong(stdoutLines, "bulk_transient_not_ready", "attempt") >= 1,
                "Expected transient client-not-ready failures to be reported separately from terminal send failures.");

            await writer.WriteJsonLineAsync("{\"cmd\":\"shutdown\",\"id\":\"shutdown\"}", cts.Token);
            await WaitUntilAsync(() => process.HasExited, TimeSpan.FromSeconds(2));
        }
        finally
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

            await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None));
        }

        Assert.DoesNotContain(stderrLines, line => line.Contains("Bulk queue send failed", StringComparison.OrdinalIgnoreCase));
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void Bridge_Source_DefaultBulkSendMode_IsLegacyFanoutPath()
    {
        var bridgePath = FindFileUpwards(Path.Combine("tools", "nkn-bridge", "index.js"));
        Assert.True(bridgePath is not null && File.Exists(bridgePath), "Bridge script not found.");

        AssertLegacyFanoutDefaults(File.ReadAllText(bridgePath), "source bridge");

        var bundledBridgePath = FindFileUpwards(Path.Combine("artifacts", "bridge", "win-x64", "index.js"));
        if (bundledBridgePath is not null && File.Exists(bundledBridgePath))
        {
            AssertLegacyFanoutDefaults(File.ReadAllText(bundledBridgePath), "bundled bridge artifact");
        }

        static void AssertLegacyFanoutDefaults(string bridgeScript, string label)
        {
            Assert.Contains("const DEFAULT_BULK_NUM_SUBCLIENTS = 4;", bridgeScript, StringComparison.Ordinal);
            Assert.Contains("const DEFAULT_BULK_SEND_CONCURRENCY = 4;", bridgeScript, StringComparison.Ordinal);
            Assert.Contains("const DEFAULT_BULK_SEND_MODE = 'fanout';", bridgeScript, StringComparison.Ordinal);
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void Bridge_Source_DefaultRpcCandidates_DoNotUseTlsOnSeedPort30003()
    {
        var bridgePath = FindFileUpwards(Path.Combine("tools", "nkn-bridge", "index.js"));
        Assert.True(bridgePath is not null && File.Exists(bridgePath), "Bridge script not found.");

        var bridgeScript = File.ReadAllText(bridgePath);
        Assert.DoesNotContain("'https://seed.nkn.org:30003'", bridgeScript, StringComparison.Ordinal);
        Assert.Contains("'http://seed.nkn.org:30003'", bridgeScript, StringComparison.Ordinal);
        Assert.Contains("return 'http://seed.nkn.org:30003';", bridgeScript, StringComparison.Ordinal);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_FakeRuntime_BulkRoundRobinSendMode_UsesSingleSubclientPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        var bridgePath = FindFileUpwards(Path.Combine("tools", "nkn-bridge", "index.js"));
        if (!File.Exists(nodePath) || bridgePath is null || !File.Exists(bridgePath))
        {
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = nodePath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };
        process.StartInfo.ArgumentList.Add(bridgePath);
        process.StartInfo.Environment["NLINK_BRIDGE_FAKE_NKN_RUNTIME"] = "1";
        process.StartInfo.Environment["NLINK_NKN_BULK_SEND_MODE"] = "round_robin";

        var stdoutLines = new ConcurrentQueue<string>();
        var stderrLines = new ConcurrentQueue<string>();
        Assert.True(process.Start());
        var stdoutTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cts.Token);
                if (line is null)
                {
                    break;
                }

                stdoutLines.Enqueue(line);
            }
        }, CancellationToken.None);
        var stderrTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cts.Token);
                if (line is null)
                {
                    break;
                }

                stderrLines.Enqueue(line);
            }
        }, CancellationToken.None);

        try
        {
            using var writer = new BridgeStdioWriter(process.StandardInput.BaseStream, leaveOpen: true);
            await writer.WriteJsonLineAsync("{\"cmd\":\"hello\",\"id\":\"hello\",\"protocol\":2}", cts.Token);
            await WaitUntilAsync(() => stdoutLines.Any(line => line.Contains("\"event\":\"hello_ok\"", StringComparison.Ordinal)), TimeSpan.FromSeconds(2));
            await writer.WriteJsonLineAsync("{\"cmd\":\"connect\",\"id\":\"connect\",\"identifier\":\"fake-bridge-bulk-round-robin\",\"bulkSendConcurrency\":4}", cts.Token);
            await WaitUntilAsync(() => stdoutLines.Any(line => line.Contains("\"event\":\"ready\"", StringComparison.Ordinal)), TimeSpan.FromSeconds(2));

            for (var index = 0; index < 4; index++)
            {
                await writer.WriteSendFrameAsync("peer.bulk.fake", new byte[] { (byte)index, 5, 6 }, NknBridgeChannel.Bulk, cts.Token);
            }

            await WaitUntilAsync(
                () => TryGetMaxJsonLong(stdoutLines, "bridge_bulk_send_summary", "send_mode_round_robin_frames") >= 4,
                TimeSpan.FromSeconds(4));
            Assert.Contains(stdoutLines, line => line.Contains("\"send_mode\":\"round_robin\"", StringComparison.Ordinal));
            Assert.Equal(0, TryGetMaxJsonLong(stdoutLines, "bridge_bulk_send_summary", "send_mode_fallback_frames"));

            await writer.WriteJsonLineAsync("{\"cmd\":\"shutdown\",\"id\":\"shutdown\"}", cts.Token);
            await WaitUntilAsync(() => process.HasExited, TimeSpan.FromSeconds(2));
        }
        finally
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

            await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None));
        }

        Assert.DoesNotContain(stderrLines, line => line.Contains("fallback", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(stderrLines, line => line.Contains("failed", StringComparison.OrdinalIgnoreCase));
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task RealNknClientAdapter_BulkQueueSevere_ThrottlesBulkButNotMedia()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-bulk-queue", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var bridgePath = Path.Combine(tempDir, "mock-bridge-bulk-queue.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildMockBridgeScriptWithCustomConnect(connectBehaviorJs: @"
    emit({ event:'ok', id: msg.id ?? null, cmd:'connect' });
    setTimeout(() => emit({ event:'ready', protocol:2, channels:['control','media','bulk'], address:'bulk-queue.addr', controlAddress:'bulk-queue.addr', mediaAddress:'bulk-queue-media.addr', bulkAddress:'bulk-queue-bulk.addr', connectId: msg.connectId ?? null }), 20);
    return;
    "));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            var options = LoadNknOptionsWithOverrides(Path.Combine(tempDir, "id.json"), "mock-bulk-queue");
            var identity = NknIdentityStore.LoadOrCreate(options);
            using var adapter = new RealNknClientAdapter(identity, options);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await adapter.ConnectAsync(cts.Token);

            adapter.HandleStdoutJsonLineForTests("{\"event\":\"bulk_queue_state\",\"queueDepth\":192,\"queuedBytes\":12582912,\"oldestQueuedAgeMs\":1000,\"inFlight\":true,\"clearedSinceLast\":0,\"congested\":true,\"severe\":true}");
            await adapter.SendMediaAsync(adapter.MediaAddress, new byte[] { 9, 8, 7 }, cts.Token).WaitAsync(TimeSpan.FromSeconds(1), cts.Token);

            var bulkSendTask = adapter.SendBulkAsync(adapter.BulkAddress, new byte[] { 1, 2, 3 }, cts.Token);
            await Task.Delay(200, cts.Token);
            Assert.False(bulkSendTask.IsCompleted, "Bulk send should wait while bridge bulk queue is severe.");

            adapter.HandleStdoutJsonLineForTests("{\"event\":\"bulk_queue_state\",\"queueDepth\":0,\"queuedBytes\":0,\"oldestQueuedAgeMs\":0,\"inFlight\":false,\"clearedSinceLast\":0,\"congested\":false,\"severe\":false}");
            await bulkSendTask.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            Assert.True(NknRuntimeDiagnostics.Snapshot().FileTransferLaneWaitCount > 0);

            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void RealNknClientAdapter_RuntimeUnlockBulkQueueProofBlocker_RejectsBackloggedQueue()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-runtime-unlock-bulk-proof", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var options = LoadNknOptionsWithOverrides(Path.Combine(tempDir, "id.json"), "runtime-unlock-bulk-proof");
            var identity = NknIdentityStore.LoadOrCreate(options);
            using var adapter = new RealNknClientAdapter(identity, options);
            var nowTick = Stopwatch.GetTimestamp();

            SetPrivateField(adapter, "lastBridgeTransportHealthSummaryTick", nowTick);
            SetPrivateField(adapter, "lastBridgeTransportHealthReadyEmitted", 1);
            SetPrivateField(adapter, "lastBridgeTransportHealthControlReady", 1);
            SetPrivateField(adapter, "lastBridgeTransportHealthBulkReady", 1);
            SetPrivateField(adapter, "lastBridgeTransportHealthDisconnectSignalCount", 0L);
            SetPrivateField(
                adapter,
                "bulkQueueState",
                new BridgeBulkQueueState(
                    QueueDepth: 8,
                    QueuedBytes: 512 * 1024,
                    OldestQueuedAgeMs: 2_000,
                    InFlight: true,
                    InFlightCount: 4,
                    InFlightBytes: 128 * 1024,
                    ConfiguredConcurrency: 4,
                    EffectiveConcurrency: 4,
                    ClearedSinceLast: 0,
                    IsCongested: true,
                    IsSevere: false));

            Assert.True(adapter.TryGetRuntimeUnlockBulkQueueObservedProofBlocker(out var blockedReason));
            Assert.Equal("bulk_queue_congested", blockedReason);

            SetPrivateField(
                adapter,
                "bulkQueueState",
                new BridgeBulkQueueState(
                    QueueDepth: 0,
                    QueuedBytes: 0,
                    OldestQueuedAgeMs: 0,
                    InFlight: false,
                    InFlightCount: 0,
                    InFlightBytes: 0,
                    ConfiguredConcurrency: 4,
                    EffectiveConcurrency: 4,
                    ClearedSinceLast: 0,
                    IsCongested: false,
                    IsSevere: false));

            Assert.False(adapter.TryGetRuntimeUnlockBulkQueueObservedProofBlocker(out var clearReason));
            Assert.Equal(string.Empty, clearReason);
        }
        finally
        {
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task RealNknClientAdapter_FileTransferBulkAdaptation_PromotesSinglePathWhenBridgeDemandExceedsSentCapacity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-bulk-adaptation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var bridgePath = Path.Combine(tempDir, "mock-bridge-bulk-adaptation.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildMockBridgeScriptWithCustomConnect(connectBehaviorJs: @"
    emit({ event:'ok', id: msg.id ?? null, cmd:'connect' });
    setTimeout(() => emit({ event:'ready', protocol:2, channels:['control','media','bulk'], address:'bulk-adaptation.addr', controlAddress:'bulk-adaptation.addr', mediaAddress:'bulk-adaptation-media.addr', bulkAddress:'bulk-adaptation-bulk.addr', connectId: msg.connectId ?? null }), 20);
    return;
    "));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevAutoBulkAdaptation = Environment.GetEnvironmentVariable("NLINK_NKN_FILE_TRANSFER_AUTO_BULK_ADAPTATION");
        var prevBulkTargetBps = Environment.GetEnvironmentVariable("NLINK_NKN_FILE_TRANSFER_BULK_TARGET_BPS");
        var prevBulkAdaptationCooldown = Environment.GetEnvironmentVariable("NLINK_NKN_FILE_TRANSFER_BULK_ADAPTATION_COOLDOWN_MS");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_FILE_TRANSFER_AUTO_BULK_ADAPTATION", "true");
            Environment.SetEnvironmentVariable("NLINK_NKN_FILE_TRANSFER_BULK_TARGET_BPS", "1500000");
            Environment.SetEnvironmentVariable("NLINK_NKN_FILE_TRANSFER_BULK_ADAPTATION_COOLDOWN_MS", "5000");
            var options = LoadNknOptionsWithOverrides(Path.Combine(tempDir, "id.json"), "mock-bulk-adaptation");
            var identity = NknIdentityStore.LoadOrCreate(options);
            using var adapter = new RealNknClientAdapter(identity, options);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await adapter.ConnectAsync(cts.Token);

            var baselineLength = LocalOperationalLog.GetRecentLogText().Length;
            adapter.RegisterActiveFileTransferDataSession("transfer-bulk-adaptation");
            var lowCapacitySummary =
                "{\"event\":\"bridge_bulk_send_summary\",\"frames_sent\":16,\"frames_enqueued\":24,\"payload_bytes_sent\":800000,\"payload_bytes_per_second\":800000,\"payload_bytes_enqueued\":3000000,\"payload_bytes_enqueued_per_second\":3000000,\"send_failures\":0,\"queue_clears\":0,\"queue_depth\":0,\"queued_bytes\":0,\"oldest_queued_age_ms\":0,\"in_flight\":0,\"in_flight_bytes\":0,\"configured_concurrency\":2,\"effective_concurrency\":2,\"send_mode\":\"single\",\"sample_window_ms\":2000}";
            adapter.HandleStdoutJsonLineForTests(lowCapacitySummary);
            adapter.HandleStdoutJsonLineForTests(lowCapacitySummary);

            await WaitUntilAsync(
                () =>
                {
                    var logText = GetRecentLogTextSince(baselineLength);
                    return logText.Contains("event=nkn_bridge_bulk_send_policy_adaptation_applied", StringComparison.Ordinal) &&
                           logText.Contains("mode=round_robin", StringComparison.Ordinal) &&
                           logText.Contains("concurrency=4", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(3));

            adapter.UnregisterActiveFileTransferDataSession("transfer-bulk-adaptation");
            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_FILE_TRANSFER_AUTO_BULK_ADAPTATION", prevAutoBulkAdaptation);
            Environment.SetEnvironmentVariable("NLINK_NKN_FILE_TRANSFER_BULK_TARGET_BPS", prevBulkTargetBps);
            Environment.SetEnvironmentVariable("NLINK_NKN_FILE_TRANSFER_BULK_ADAPTATION_COOLDOWN_MS", prevBulkAdaptationCooldown);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void NknTransportOptions_ParsesSubClientTopologyOverrides()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-nkn-topology-options", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var prevNumSubClients = Environment.GetEnvironmentVariable("NLINK_NKN_NUM_SUBCLIENTS");
        var prevMediaNumSubClients = Environment.GetEnvironmentVariable("NLINK_NKN_MEDIA_NUM_SUBCLIENTS");
        var prevBulkNumSubClients = Environment.GetEnvironmentVariable("NLINK_NKN_BULK_NUM_SUBCLIENTS");
        var prevBulkSendConcurrency = Environment.GetEnvironmentVariable("NLINK_NKN_BULK_SEND_CONCURRENCY");
        var prevReceiveStallRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");
        var prevReceiveStallFastRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY");
        var prevControlOnlyStallRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY");
        var prevReceiveStallFallbackDelay = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY_FALLBACK_DELAY_MS");
        var prevAutoBulkAdaptation = Environment.GetEnvironmentVariable("NLINK_NKN_FILE_TRANSFER_AUTO_BULK_ADAPTATION");
        var prevBulkTargetBps = Environment.GetEnvironmentVariable("NLINK_NKN_FILE_TRANSFER_BULK_TARGET_BPS");
        var prevBulkAdaptationCooldown = Environment.GetEnvironmentVariable("NLINK_NKN_FILE_TRANSFER_BULK_ADAPTATION_COOLDOWN_MS");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NUM_SUBCLIENTS", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_MEDIA_NUM_SUBCLIENTS", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_BULK_NUM_SUBCLIENTS", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_BULK_SEND_CONCURRENCY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY_FALLBACK_DELAY_MS", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_FILE_TRANSFER_AUTO_BULK_ADAPTATION", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_FILE_TRANSFER_BULK_TARGET_BPS", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_FILE_TRANSFER_BULK_ADAPTATION_COOLDOWN_MS", null);
            var defaults = LoadNknOptionsWithOverrides(Path.Combine(tempDir, "default.json"), "topology-default");
            Assert.Equal(4, defaults.NumSubClients);
            Assert.Equal(8, defaults.MediaNumSubClients);
            Assert.Equal(4, defaults.BulkNumSubClients);
            Assert.Equal(4, defaults.BulkSendConcurrency);
            Assert.True(defaults.FileTransferAutoBulkAdaptationEnabled);
            Assert.Equal(1_500_000, defaults.FileTransferBulkTargetBytesPerSecond);
            Assert.Equal(20_000, defaults.FileTransferBulkAdaptationCooldownMs);
            Assert.True(defaults.ReceiveStallRecoveryEnabled);
            Assert.True(defaults.ReceiveStallFileTransferFastRecoveryEnabled);
            Assert.True(defaults.ReceiveStallControlOnlyRecoveryEnabled);
            Assert.Equal(3000, defaults.ReceiveStallRecoveryFallbackDelayMs);
            Assert.False(defaults.HasSubClientTopologyOverride);
            Assert.False(defaults.ShouldSendSubClientTopology);

            Environment.SetEnvironmentVariable("NLINK_NKN_NUM_SUBCLIENTS", "6");
            Environment.SetEnvironmentVariable("NLINK_NKN_MEDIA_NUM_SUBCLIENTS", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_BULK_NUM_SUBCLIENTS", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_BULK_SEND_CONCURRENCY", null);
            var inheritedMedia = LoadNknOptionsWithOverrides(Path.Combine(tempDir, "inherited.json"), "topology-inherited");
            Assert.Equal(6, inheritedMedia.NumSubClients);
            Assert.Equal(6, inheritedMedia.MediaNumSubClients);
            Assert.Equal(6, inheritedMedia.BulkNumSubClients);
            Assert.Equal(4, inheritedMedia.BulkSendConcurrency);
            Assert.True(inheritedMedia.HasSubClientTopologyOverride);

            Environment.SetEnvironmentVariable("NLINK_NKN_NUM_SUBCLIENTS", "0");
            Environment.SetEnvironmentVariable("NLINK_NKN_MEDIA_NUM_SUBCLIENTS", "99");
            Environment.SetEnvironmentVariable("NLINK_NKN_BULK_NUM_SUBCLIENTS", "99");
            Environment.SetEnvironmentVariable("NLINK_NKN_BULK_SEND_CONCURRENCY", "99");
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", "false");
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", "false");
            Environment.SetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY", "true");
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY_FALLBACK_DELAY_MS", "99");
            Environment.SetEnvironmentVariable("NLINK_NKN_FILE_TRANSFER_AUTO_BULK_ADAPTATION", "false");
            Environment.SetEnvironmentVariable("NLINK_NKN_FILE_TRANSFER_BULK_TARGET_BPS", "128000");
            Environment.SetEnvironmentVariable("NLINK_NKN_FILE_TRANSFER_BULK_ADAPTATION_COOLDOWN_MS", "1");
            var clamped = LoadNknOptionsWithOverrides(Path.Combine(tempDir, "clamped.json"), "topology-clamped");
            Assert.Equal(1, clamped.NumSubClients);
            Assert.Equal(16, clamped.MediaNumSubClients);
            Assert.Equal(16, clamped.BulkNumSubClients);
            Assert.Equal(8, clamped.BulkSendConcurrency);
            Assert.False(clamped.FileTransferAutoBulkAdaptationEnabled);
            Assert.Equal(256_000, clamped.FileTransferBulkTargetBytesPerSecond);
            Assert.Equal(5_000, clamped.FileTransferBulkAdaptationCooldownMs);
            Assert.False(clamped.ReceiveStallRecoveryEnabled);
            Assert.False(clamped.ReceiveStallFileTransferFastRecoveryEnabled);
            Assert.True(clamped.ReceiveStallControlOnlyRecoveryEnabled);
            Assert.Equal(1000, clamped.ReceiveStallRecoveryFallbackDelayMs);
            Assert.True(clamped.HasSubClientTopologyOverride);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NUM_SUBCLIENTS", prevNumSubClients);
            Environment.SetEnvironmentVariable("NLINK_NKN_MEDIA_NUM_SUBCLIENTS", prevMediaNumSubClients);
            Environment.SetEnvironmentVariable("NLINK_NKN_BULK_NUM_SUBCLIENTS", prevBulkNumSubClients);
            Environment.SetEnvironmentVariable("NLINK_NKN_BULK_SEND_CONCURRENCY", prevBulkSendConcurrency);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevReceiveStallRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", prevReceiveStallFastRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY", prevControlOnlyStallRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY_FALLBACK_DELAY_MS", prevReceiveStallFallbackDelay);
            Environment.SetEnvironmentVariable("NLINK_NKN_FILE_TRANSFER_AUTO_BULK_ADAPTATION", prevAutoBulkAdaptation);
            Environment.SetEnvironmentVariable("NLINK_NKN_FILE_TRANSFER_BULK_TARGET_BPS", prevBulkTargetBps);
            Environment.SetEnvironmentVariable("NLINK_NKN_FILE_TRANSFER_BULK_ADAPTATION_COOLDOWN_MS", prevBulkAdaptationCooldown);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ReceiveStallRecovery_ReconnectsAfterConsecutiveReadySendingZeroReceiveWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-receive-stall", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-receive-stall.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildReceiveStallRecoveryMockBridgeScript(countFile));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "receive-stall-recovery");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "receive-stall-recovery");
            var identity = new NknIdentity("receive-stall-recovery", "receive-stall-recovery.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await adapter.ConnectAsync(cts.Token);

            await WaitUntilAsync(
                () => File.Exists(countFile) && int.TryParse(File.ReadAllText(countFile).Trim(), out var count) && count >= 2,
                TimeSpan.FromSeconds(15));

            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ReceiveStallRecovery_DoesNotReconnectForFileTransferBulkSilenceWhenControlFresh()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-bulk-fresh-control", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-bulk-fresh-control.js");
        WriteBridgeScriptWithManifest(
            bridgePath,
            BuildReceiveStallRecoveryMockBridgeScript(
                countFile,
                controlMessagesReceivedSinceLast: 1,
                bulkMessagesReceivedSinceLast: 0,
                totalMessagesReceivedSinceLast: 1,
                controlLastReceivedAgeMs: 100,
                bulkLastReceivedAgeMs: 7000));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");
        var prevFastRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", null);
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "bulk-fresh-control");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "bulk-fresh-control");
            var identity = new NknIdentity("bulk-fresh-control", "bulk-fresh-control.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.RegisterActiveFileTransferDataSession("transfer-bulk-fresh-control");
            var logBaseline = LocalOperationalLog.GetRecentLogText().Length;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await adapter.ConnectAsync(cts.Token);

            await WaitUntilAsync(
                () => GetRecentLogTextSince(logBaseline).Contains("event=filetransfer_v4_receive_liveness_summary", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));
            await Task.Delay(750, cts.Token);

            Assert.True(File.Exists(countFile));
            Assert.True(int.TryParse(File.ReadAllText(countFile).Trim(), out var count));
            Assert.Equal(1, count);
            Assert.DoesNotContain("event=nkn_bridge_receive_stall_detected", GetRecentLogTextSince(logBaseline), StringComparison.Ordinal);

            adapter.UnregisterActiveFileTransferDataSession("transfer-bulk-fresh-control");
            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", prevFastRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ReceiveStallRecovery_SuppressesAutoReconnectAfterFileTransferBulkReceiveStallWhenControlStale()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-bulk-stale-control", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-bulk-stale-control.js");
        WriteBridgeScriptWithManifest(
            bridgePath,
            BuildReceiveStallRecoveryMockBridgeScript(
                countFile,
                controlMessagesReceivedSinceLast: 0,
                bulkMessagesReceivedSinceLast: 0,
                totalMessagesReceivedSinceLast: 1,
                controlLastReceivedAgeMs: 13_000,
                bulkLastReceivedAgeMs: 7_000));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");
        var prevFastRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", null);
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "bulk-stale-control");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "bulk-stale-control");
            var identity = new NknIdentity("bulk-stale-control", "bulk-stale-control.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.RegisterActiveFileTransferDataSession("transfer-bulk-stale-control");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await adapter.ConnectAsync(cts.Token);

            await WaitUntilAsync(
                () => LocalOperationalLog.GetRecentLogText().Contains("reason=filetransfer_protocol_repair_only", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));

            var logText = LocalOperationalLog.GetRecentLogText();
            Assert.Contains("reason=bulk_receive_stalled", logText, StringComparison.Ordinal);
            Assert.Contains("event=nkn_bridge_receive_stall_recovery_suppressed; reason=filetransfer_protocol_repair_only", logText, StringComparison.Ordinal);
            Assert.True(File.Exists(countFile));
            Assert.True(int.TryParse(File.ReadAllText(countFile).Trim(), out var count));
            Assert.Equal(1, count);

            adapter.UnregisterActiveFileTransferDataSession("transfer-bulk-stale-control");
            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", prevFastRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ReceiveStallRecovery_SuppressesAutoReconnectDuringActiveFileTransferRuntimeWhenFeedbackStalls()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-v6-runtime-bulk-stale-control", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-v6-runtime-bulk-stale-control.js");
        WriteBridgeScriptWithManifest(
            bridgePath,
            BuildReceiveStallRecoveryMockBridgeScript(
                countFile,
                controlMessagesReceivedSinceLast: 0,
                bulkMessagesReceivedSinceLast: 0,
                totalMessagesReceivedSinceLast: 1,
                controlLastReceivedAgeMs: 9_000,
                bulkLastReceivedAgeMs: 7_000));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");
        var prevFastRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", null);
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "v6-runtime-bulk-stale-control");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "v6-runtime-bulk-stale-control");
            var identity = new NknIdentity("v6-runtime-bulk-stale-control", "v6-runtime-bulk-stale-control.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.RegisterActiveFileTransferDataSession("transfer-v6-runtime-bulk-stale-control");
            adapter.RegisterActiveFileTransferRuntime("transfer-v6-runtime-bulk-stale-control");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await adapter.ConnectAsync(cts.Token);

            await WaitUntilAsync(
                () =>
                {
                    var text = LocalOperationalLog.GetRecentLogText();
                    return text.Contains("event=nkn_bridge_receive_stall_detected", StringComparison.Ordinal) &&
                           text.Contains("event=nkn_bridge_receive_stall_recovery_suppressed; reason=filetransfer_protocol_repair_only", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(5));

            Assert.True(File.Exists(countFile));
            Assert.True(int.TryParse(File.ReadAllText(countFile).Trim(), out var count));
            Assert.Equal(1, count);

            adapter.UnregisterActiveFileTransferRuntime("transfer-v6-runtime-bulk-stale-control");
            adapter.UnregisterActiveFileTransferDataSession("transfer-v6-runtime-bulk-stale-control");
            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", prevFastRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ReceiveStallRecovery_ActiveRuntimeAllZeroDefersToFileTransferProtocolAfterGraceExpires()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-v6-runtime-protocol-liveness", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-v6-runtime-protocol-liveness.js");
        WriteBridgeScriptWithManifest(
            bridgePath,
            BuildReceiveStallRecoveryMockBridgeScript(
                countFile,
                stallHealthSampleCount: 8));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");
        var prevFastRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY");
        var prevControlOnlyRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY", "0");
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "v6-runtime-protocol-liveness");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "v6-runtime-protocol-liveness");
            var identity = new NknIdentity("v6-runtime-protocol-liveness", "v6-runtime-protocol-liveness.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.RegisterActiveFileTransferDataSession("transfer-v6-runtime-protocol-liveness");
            adapter.RegisterActiveFileTransferRuntime("transfer-v6-runtime-protocol-liveness");
            var logBaseline = GetOperationalLogLength();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await adapter.ConnectAsync(cts.Token);

            await WaitUntilAsync(
                () =>
                {
                    var text = ReadOperationalLogTail(logBaseline) + Environment.NewLine + LocalOperationalLog.GetRecentLogText();
                    if (!text.Contains("event=nkn_bridge_receive_stall_recovery_suppressed; reason=filetransfer_runtime_protocol_liveness", StringComparison.Ordinal) ||
                        !text.Contains("event=nkn_bridge_receive_stall_recovery_protocol_repair_exhausted", StringComparison.Ordinal) ||
                        !text.Contains("event=nkn_bridge_receive_stall_recovery_started", StringComparison.Ordinal))
                    {
                        return false;
                    }

                    return File.Exists(countFile) &&
                           int.TryParse(File.ReadAllText(countFile).Trim(), out var count) &&
                           count > 1;
                },
                TimeSpan.FromSeconds(15));

            var logText = ReadOperationalLogTail(logBaseline) + Environment.NewLine + LocalOperationalLog.GetRecentLogText();
            Assert.Contains("event=nkn_bridge_receive_stall_recovery_suppressed; reason=filetransfer_runtime_protocol_liveness", logText, StringComparison.Ordinal);
            Assert.Contains("event=nkn_bridge_receive_stall_recovery_protocol_repair_exhausted", logText, StringComparison.Ordinal);
            Assert.Contains("event=nkn_bridge_receive_stall_recovery_started", logText, StringComparison.Ordinal);
            Assert.True(File.Exists(countFile));
            Assert.True(int.TryParse(File.ReadAllText(countFile).Trim(), out var connectCount));
            Assert.True(connectCount > 1);

            adapter.UnregisterActiveFileTransferRuntime("transfer-v6-runtime-protocol-liveness");
            adapter.UnregisterActiveFileTransferDataSession("transfer-v6-runtime-protocol-liveness");
            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", prevFastRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY", prevControlOnlyRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ReceiveStallRecovery_ClosingFileTransferResetsSuppressedZeroReceiveWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-v6-runtime-close-reset", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-v6-runtime-close-reset.js");
        WriteBridgeScriptWithManifest(
            bridgePath,
            BuildReceiveStallRecoveryMockBridgeScript(
                countFile,
                stallHealthSampleCount: 4,
                stallHealthSampleSpacingMs: 150));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");
        var prevFastRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY");
        var prevControlOnlyRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY", "0");
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "v6-runtime-close-reset");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "v6-runtime-close-reset");
            var identity = new NknIdentity("v6-runtime-close-reset", "v6-runtime-close-reset.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.RegisterActiveFileTransferDataSession("transfer-v6-runtime-close-reset");
            adapter.RegisterActiveFileTransferRuntime("transfer-v6-runtime-close-reset");
            var logBaseline = LocalOperationalLog.GetRecentLogText().Length;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await adapter.ConnectAsync(cts.Token);

            await WaitUntilAsync(
                () => GetRecentLogTextSince(logBaseline).Contains("event=nkn_bridge_receive_stall_recovery_suppressed; reason=filetransfer_runtime_protocol_liveness", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));

            adapter.UnregisterActiveFileTransferRuntime("transfer-v6-runtime-close-reset");
            adapter.UnregisterActiveFileTransferDataSession("transfer-v6-runtime-close-reset");
            await Task.Delay(350, cts.Token);

            Assert.True(File.Exists(countFile));
            Assert.True(int.TryParse(File.ReadAllText(countFile).Trim(), out var connectCount));
            Assert.Equal(1, connectCount);

            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", prevFastRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY", prevControlOnlyRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ReceiveStallRecovery_SuppressesAutoReconnectForActiveFileTransferBeforeBulkProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-v6-bulk-proof-after-reconnect", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-v6-bulk-proof-after-reconnect.js");
        WriteBridgeScriptWithManifest(
            bridgePath,
            BuildReceiveStallRecoveryMockBridgeScript(
                countFile,
                controlMessagesReceivedSinceLast: 0,
                bulkMessagesReceivedSinceLast: 0,
                totalMessagesReceivedSinceLast: 0,
                controlLastReceivedAgeMs: 9_000,
                bulkLastReceivedAgeMs: 9_000,
                postRecoveryControlMessagesReceivedSinceLast: 0,
                postRecoveryBulkMessagesReceivedSinceLast: 12,
                postRecoveryTotalMessagesReceivedSinceLast: 12,
                postRecoveryControlLastReceivedAgeMs: 24_000,
                postRecoveryBulkLastReceivedAgeMs: 100,
                stallHealthSampleCount: 4,
                rampStallBulkAgeForAllChannelRecovery: true));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");
        var prevFastRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", null);
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "v6-bulk-proof-after-reconnect");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "v6-bulk-proof-after-reconnect");
            var identity = new NknIdentity("v6-bulk-proof-after-reconnect", "v6-bulk-proof-after-reconnect.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.RegisterActiveFileTransferDataSession("transfer-v6-bulk-proof-after-reconnect");
            adapter.RegisterActiveFileTransferRuntime("transfer-v6-bulk-proof-after-reconnect");
            var logBaseline = GetOperationalLogLength();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await adapter.ConnectAsync(cts.Token);

            await WaitUntilAsync(
                () => (ReadOperationalLogTail(logBaseline) + Environment.NewLine + LocalOperationalLog.GetRecentLogText()).Contains("event=nkn_bridge_receive_stall_recovery_suppressed; reason=filetransfer_bulk_probe_window", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));
            await Task.Delay(500, cts.Token);

            var logText = ReadOperationalLogTail(logBaseline) + Environment.NewLine + LocalOperationalLog.GetRecentLogText();
            Assert.Contains("event=nkn_bridge_receive_stall_recovery_suppressed; reason=filetransfer_bulk_probe_window", logText, StringComparison.Ordinal);
            Assert.DoesNotContain("event=nkn_bridge_receive_stall_recovery_filetransfer_bulk_proof_accepted", logText, StringComparison.Ordinal);
            Assert.DoesNotContain("event=nkn_bridge_receive_stall_recovery_receive_resumed", logText, StringComparison.Ordinal);
            Assert.True(File.Exists(countFile));
            Assert.True(int.TryParse(File.ReadAllText(countFile).Trim(), out var count));
            Assert.Equal(1, count);

            adapter.UnregisterActiveFileTransferRuntime("transfer-v6-bulk-proof-after-reconnect");
            adapter.UnregisterActiveFileTransferDataSession("transfer-v6-bulk-proof-after-reconnect");
            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", prevFastRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ReceiveStallRecovery_SuppressesAllChannelRecoveryUntilBulkStaleRepeats()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-all-zero-probe-window", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-all-zero-probe-window.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildReceiveStallRecoveryTransientAllZeroMockBridgeScript(countFile));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");
        var prevFastRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY");
        var prevControlOnlyRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY", "0");
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "all-zero-probe-window");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "all-zero-probe-window");
            var identity = new NknIdentity("all-zero-probe-window", "all-zero-probe-window.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.RegisterActiveFileTransferDataSession("transfer-all-zero-probe-window");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await adapter.ConnectAsync(cts.Token);

            await WaitUntilAsync(
                () => LocalOperationalLog.GetRecentLogText().Contains("reason=filetransfer_bulk_probe_window", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));
            await Task.Delay(750, cts.Token);

            Assert.True(File.Exists(countFile));
            Assert.True(int.TryParse(File.ReadAllText(countFile).Trim(), out var count));
            Assert.Equal(1, count);

            adapter.UnregisterActiveFileTransferDataSession("transfer-all-zero-probe-window");
            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", prevFastRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY", prevControlOnlyRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ReceiveStallRecovery_UnknownBulkLivenessDoesNotSuppressAllChannelRecoveryIndefinitely()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-unknown-bulk-liveness", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-unknown-bulk-liveness.js");
        WriteBridgeScriptWithManifest(
            bridgePath,
            BuildReceiveStallRecoveryMockBridgeScript(
                countFile,
                stallConnectCount: 1,
                bulkLastReceivedAgeMs: -1,
                stallHealthSampleCount: 8));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");
        var prevFastRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY");
        var prevControlOnlyRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY", "0");
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "unknown-bulk-liveness");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "unknown-bulk-liveness");
            var identity = new NknIdentity("unknown-bulk-liveness", "unknown-bulk-liveness.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.RegisterActiveFileTransferDataSession("transfer-unknown-bulk-liveness");
            var logBaseline = LocalOperationalLog.GetRecentLogText().Length;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await adapter.ConnectAsync(cts.Token);

            await WaitUntilAsync(
                () => File.Exists(countFile) && int.TryParse(File.ReadAllText(countFile).Trim(), out var count) && count >= 2,
                TimeSpan.FromSeconds(8));

            var logText = GetRecentLogTextSince(logBaseline);
            Assert.Contains("event=nkn_bridge_receive_stall_recovery_suppressed; reason=filetransfer_protocol_repair_only", logText, StringComparison.Ordinal);
            Assert.Contains("event=nkn_bridge_receive_stall_recovery_started;", logText, StringComparison.Ordinal);
            Assert.Contains("bulk_last_received_age_ms=-1", logText, StringComparison.Ordinal);

            adapter.UnregisterActiveFileTransferDataSession("transfer-unknown-bulk-liveness");
            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", prevFastRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY", prevControlOnlyRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ReceiveStallRecovery_UnknownBulkLivenessWithoutActiveFileTransferDoesNotRequireBulkProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-unknown-bulk-no-filetransfer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-unknown-bulk-no-filetransfer.js");
        WriteBridgeScriptWithManifest(
            bridgePath,
            BuildReceiveStallRecoveryMockBridgeScript(
                countFile,
                stallConnectCount: 1,
                bulkLastReceivedAgeMs: -1,
                stallHealthSampleCount: 4));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");
        var prevFastRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY");
        var prevControlOnlyRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY", "0");
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "unknown-bulk-no-filetransfer");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "unknown-bulk-no-filetransfer");
            var identity = new NknIdentity("unknown-bulk-no-filetransfer", "unknown-bulk-no-filetransfer.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            var logBaseline = LocalOperationalLog.GetRecentLogText().Length;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await adapter.ConnectAsync(cts.Token);

            await WaitUntilAsync(
                () => File.Exists(countFile) && int.TryParse(File.ReadAllText(countFile).Trim(), out var count) && count >= 2,
                TimeSpan.FromSeconds(8));
            await WaitUntilAsync(
                () => GetRecentLogTextSince(logBaseline).Contains("requires_bulk_proof=0", StringComparison.Ordinal),
                TimeSpan.FromSeconds(8));

            var logText = GetRecentLogTextSince(logBaseline);
            Assert.Contains("event=nkn_bridge_receive_stall_recovery_completed;", logText, StringComparison.Ordinal);
            Assert.Contains("requires_bulk_proof=0", logText, StringComparison.Ordinal);
            Assert.Contains("bulk_last_received_age_ms=-1", logText, StringComparison.Ordinal);

            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", prevFastRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY", prevControlOnlyRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_PingTimeoutDuringActiveFileTransferWithoutStallState_ReconnectsWithoutDisconnect()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-ping-timeout-filetransfer-active-only", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-ping-timeout-filetransfer-active-only.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildReceiveStallRecoveryMockBridgeScript(countFile, stallConnectCount: 0));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");
        var prevControlOnlyRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY");
        var disconnectedCount = 0;

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY", "0");
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "ping-timeout-filetransfer-active-only");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "ping-timeout-filetransfer-active-only");
            var identity = new NknIdentity("ping-timeout-filetransfer-active-only", "ping-timeout-filetransfer-active-only.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.Disconnected += (_, _) => Interlocked.Increment(ref disconnectedCount);
            adapter.RegisterActiveFileTransferDataSession("transfer-ping-timeout-active-only");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await adapter.ConnectAsync(cts.Token);

            await WaitUntilAsync(
                () => File.Exists(countFile) && int.TryParse(File.ReadAllText(countFile).Trim(), out var count) && count >= 1,
                TimeSpan.FromSeconds(5));

            var recovered = await adapter.RecoverBridgePingTimeoutForActiveFileTransferForTestsAsync();

            Assert.True(recovered);
            await WaitUntilAsync(
                () => File.Exists(countFile) && int.TryParse(File.ReadAllText(countFile).Trim(), out var count) && count >= 2,
                TimeSpan.FromSeconds(5));
            await Task.Delay(150, cts.Token);
            Assert.Equal(0, Volatile.Read(ref disconnectedCount));
            var logText = LocalOperationalLog.GetRecentLogText();
            Assert.Contains("event=nkn_bridge_ping_timeout_filetransfer_recovery_forced", logText, StringComparison.Ordinal);
            Assert.Contains("event=nkn_bridge_ping_timeout_filetransfer_recovery_started", logText, StringComparison.Ordinal);
            Assert.Contains("event=nkn_bridge_ping_timeout_disconnect_suppressed", logText, StringComparison.Ordinal);

            adapter.UnregisterActiveFileTransferDataSession("transfer-ping-timeout-active-only");
            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY", prevControlOnlyRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_PingTimeoutDuringActiveFileTransferRuntimeWithoutDataSession_ReconnectsWithoutDisconnect()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-ping-timeout-filetransfer-runtime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-ping-timeout-filetransfer-runtime.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildReceiveStallRecoveryMockBridgeScript(countFile, stallConnectCount: 0));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");
        var disconnectedCount = 0;

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "ping-timeout-filetransfer-runtime");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "ping-timeout-filetransfer-runtime");
            var identity = new NknIdentity("ping-timeout-filetransfer-runtime", "ping-timeout-filetransfer-runtime.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.Disconnected += (_, _) => Interlocked.Increment(ref disconnectedCount);
            adapter.RegisterActiveFileTransferRuntime("transfer-ping-timeout-runtime");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await adapter.ConnectAsync(cts.Token);

            await WaitUntilAsync(
                () => File.Exists(countFile) && int.TryParse(File.ReadAllText(countFile).Trim(), out var count) && count >= 1,
                TimeSpan.FromSeconds(5));

            var recovered = await adapter.RecoverBridgePingTimeoutForActiveFileTransferForTestsAsync();

            Assert.True(recovered);
            await WaitUntilAsync(
                () => File.Exists(countFile) && int.TryParse(File.ReadAllText(countFile).Trim(), out var count) && count >= 2,
                TimeSpan.FromSeconds(5));
            await Task.Delay(150, cts.Token);
            Assert.Equal(0, Volatile.Read(ref disconnectedCount));
            var logText = LocalOperationalLog.GetRecentLogText();
            Assert.Contains("event=filetransfer_active_runtime_used", logText, StringComparison.Ordinal);
            Assert.Contains("event=nkn_bridge_ping_timeout_filetransfer_recovery_started", logText, StringComparison.Ordinal);
            Assert.Contains("event=nkn_bridge_ping_timeout_disconnect_suppressed", logText, StringComparison.Ordinal);

            adapter.UnregisterActiveFileTransferRuntime("transfer-ping-timeout-runtime");
            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_PingTimeoutDuringActiveFileTransferRecovery_ReconnectsWithoutDisconnect()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-ping-timeout-filetransfer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-ping-timeout-filetransfer.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildReceiveStallRecoveryMockBridgeScript(countFile));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");
        var disconnectedCount = 0;

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "ping-timeout-filetransfer-recovery");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "ping-timeout-filetransfer-recovery");
            var identity = new NknIdentity("ping-timeout-filetransfer-recovery", "ping-timeout-filetransfer-recovery.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.Disconnected += (_, _) => Interlocked.Increment(ref disconnectedCount);
            adapter.RegisterActiveFileTransferDataSession("transfer-ping-timeout-recovery");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await adapter.ConnectAsync(cts.Token);
            Assert.True(adapter.RequestFileTransferReceiveStallRecovery("test_ping_timeout_recovery"));

            await WaitUntilAsync(
                () => File.Exists(countFile) &&
                      int.TryParse(File.ReadAllText(countFile).Trim(), out var count) &&
                      count >= 2 &&
                      LocalOperationalLog.GetRecentLogText().Contains("event=nkn_bridge_receive_stall_recovery_completed", StringComparison.Ordinal),
                TimeSpan.FromSeconds(6));

            var recovered = await adapter.RecoverBridgePingTimeoutForActiveFileTransferForTestsAsync();

            Assert.True(recovered);
            await WaitUntilAsync(
                () => File.Exists(countFile) && int.TryParse(File.ReadAllText(countFile).Trim(), out var count) && count >= 3,
                TimeSpan.FromSeconds(5));
            await Task.Delay(150, cts.Token);
            Assert.Equal(0, Volatile.Read(ref disconnectedCount));
            var logText = LocalOperationalLog.GetRecentLogText();
            Assert.Contains("event=nkn_bridge_ping_timeout_filetransfer_recovery_started", logText, StringComparison.Ordinal);
            Assert.Contains("event=nkn_bridge_ping_timeout_disconnect_suppressed", logText, StringComparison.Ordinal);

            adapter.UnregisterActiveFileTransferDataSession("transfer-ping-timeout-recovery");
            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_PingTimeoutDuringRecentFileTransferRecoveryTombstone_ReconnectsWithoutDisconnect()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-ping-timeout-filetransfer-tombstone", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-ping-timeout-filetransfer-tombstone.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildReceiveStallRecoveryMockBridgeScript(countFile));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");
        var disconnectedCount = 0;

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "ping-timeout-filetransfer-tombstone");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "ping-timeout-filetransfer-tombstone");
            var identity = new NknIdentity("ping-timeout-filetransfer-tombstone", "ping-timeout-filetransfer-tombstone.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.Disconnected += (_, _) => Interlocked.Increment(ref disconnectedCount);
            adapter.RegisterActiveFileTransferDataSession("transfer-ping-timeout-tombstone");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await adapter.ConnectAsync(cts.Token);
            Assert.True(adapter.RequestFileTransferReceiveStallRecovery("test_ping_timeout_tombstone"));

            await WaitUntilAsync(
                () => File.Exists(countFile) &&
                      int.TryParse(File.ReadAllText(countFile).Trim(), out var count) &&
                      count >= 2 &&
                      LocalOperationalLog.GetRecentLogText().Contains("event=nkn_bridge_receive_stall_recovery_completed", StringComparison.Ordinal),
                TimeSpan.FromSeconds(6));

            adapter.UnregisterActiveFileTransferDataSession("transfer-ping-timeout-tombstone");
            var recovered = await adapter.RecoverBridgePingTimeoutForActiveFileTransferForTestsAsync();

            Assert.True(recovered);
            await WaitUntilAsync(
                () => File.Exists(countFile) && int.TryParse(File.ReadAllText(countFile).Trim(), out var count) && count >= 3,
                TimeSpan.FromSeconds(5));
            await Task.Delay(150, cts.Token);
            Assert.Equal(0, Volatile.Read(ref disconnectedCount));
            var logText = LocalOperationalLog.GetRecentLogText();
            Assert.Contains("event=filetransfer_active_recovery_tombstone_started", logText, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_active_recovery_tombstone_used", logText, StringComparison.Ordinal);
            Assert.Contains("event=nkn_bridge_ping_timeout_filetransfer_recovery_started", logText, StringComparison.Ordinal);
            Assert.Contains("event=nkn_bridge_ping_timeout_disconnect_suppressed", logText, StringComparison.Ordinal);

            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ReceiveStallRecovery_SuppressesControlOnlyReconnectWhenBulkReceiveActive()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-control-degraded", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-control-degraded.js");
        WriteBridgeScriptWithManifest(
            bridgePath,
            BuildReceiveStallRecoveryMockBridgeScript(
                countFile,
                controlMessagesReceivedSinceLast: 0,
                bulkMessagesReceivedSinceLast: 1,
                totalMessagesReceivedSinceLast: 1,
                controlLastReceivedAgeMs: 9000,
                bulkLastReceivedAgeMs: 100));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");
        var prevFastRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY");
        var prevControlOnlyRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY", "0");
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "control-degraded");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "control-degraded");
            var identity = new NknIdentity("control-degraded", "control-degraded.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.RegisterActiveFileTransferDataSession("transfer-control-degraded");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await adapter.ConnectAsync(cts.Token);

            await WaitUntilAsync(
                () =>
                {
                    var text = LocalOperationalLog.GetRecentLogText();
                    return text.Contains("event=nkn_bridge_control_receive_recovery_suppressed", StringComparison.Ordinal) &&
                           text.Contains("reason=filetransfer_bulk_receive_active", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(5));
            var logText = LocalOperationalLog.GetRecentLogText();
            Assert.Contains("event=nkn_bridge_control_receive_degraded", logText, StringComparison.Ordinal);
            Assert.Contains("event=nkn_bridge_control_receive_recovery_suppressed", logText, StringComparison.Ordinal);
            Assert.Contains("reason=filetransfer_bulk_receive_active", logText, StringComparison.Ordinal);
            Assert.True(File.Exists(countFile));
            Assert.True(int.TryParse(File.ReadAllText(countFile).Trim(), out var count));
            Assert.Equal(1, count);

            adapter.UnregisterActiveFileTransferDataSession("transfer-control-degraded");
            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", prevFastRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY", prevControlOnlyRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ReceiveStallRecovery_ControlOnlyOverrideDoesNotReconnectWhenBulkReceiveActive()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-control-recovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-control-recovery.js");
        WriteBridgeScriptWithManifest(
            bridgePath,
            BuildReceiveStallRecoveryMockBridgeScript(
                countFile,
                controlMessagesReceivedSinceLast: 0,
                bulkMessagesReceivedSinceLast: 1,
                totalMessagesReceivedSinceLast: 1,
                controlLastReceivedAgeMs: 9000,
                bulkLastReceivedAgeMs: 100));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");
        var prevFastRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY");
        var prevControlOnlyRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY", "1");
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "control-recovery");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "control-recovery");
            var identity = new NknIdentity("control-recovery", "control-recovery.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.RegisterActiveFileTransferDataSession("transfer-control-recovery");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await adapter.ConnectAsync(cts.Token);

            await WaitUntilAsync(
                () =>
                {
                    var text = LocalOperationalLog.GetRecentLogText();
                    return text.Contains("event=nkn_bridge_control_receive_recovery_suppressed", StringComparison.Ordinal) &&
                           text.Contains("reason=filetransfer_bulk_receive_active", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(5));
            var logText = LocalOperationalLog.GetRecentLogText();
            Assert.Contains("event=nkn_bridge_control_receive_degraded", logText, StringComparison.Ordinal);
            Assert.Contains("event=nkn_bridge_control_receive_recovery_suppressed", logText, StringComparison.Ordinal);
            Assert.Contains("reason=filetransfer_bulk_receive_active", logText, StringComparison.Ordinal);
            Assert.True(File.Exists(countFile));
            Assert.True(int.TryParse(File.ReadAllText(countFile).Trim(), out var count));
            Assert.Equal(1, count);

            adapter.UnregisterActiveFileTransferDataSession("transfer-control-recovery");
            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", prevFastRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY", prevControlOnlyRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ReceiveStallRecovery_ControlOnlyOverrideSuppressesAfterGraceWhenBulkReceiveActive()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-control-recovery-grace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-control-recovery-grace.js");
        WriteBridgeScriptWithManifest(
            bridgePath,
            BuildReceiveStallRecoveryMockBridgeScript(
                countFile,
                controlMessagesReceivedSinceLast: 0,
                bulkMessagesReceivedSinceLast: 1,
                totalMessagesReceivedSinceLast: 1,
                controlLastReceivedAgeMs: 35_000,
                bulkLastReceivedAgeMs: 100,
                stallHealthSampleCount: 6));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");
        var prevFastRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY");
        var prevControlOnlyRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY", "1");
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "control-recovery-grace");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "control-recovery-grace");
            var identity = new NknIdentity("control-recovery-grace", "control-recovery-grace.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.RegisterActiveFileTransferDataSession("transfer-control-recovery-grace");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await adapter.ConnectAsync(cts.Token);

            await WaitUntilAsync(
                () =>
                {
                    var text = LocalOperationalLog.GetRecentLogText();
                    return text.Contains("event=nkn_bridge_control_receive_recovery_suppressed", StringComparison.Ordinal) &&
                           text.Contains("reason=filetransfer_bulk_receive_active", StringComparison.Ordinal) &&
                           text.Contains("protocol_repair_grace_windows=", StringComparison.Ordinal) &&
                           File.Exists(countFile);
                },
                TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromMilliseconds(500), cts.Token);

            var logText = LocalOperationalLog.GetRecentLogText();
            Assert.Contains("event=nkn_bridge_control_receive_degraded", logText, StringComparison.Ordinal);
            Assert.Contains("event=nkn_bridge_control_receive_recovery_suppressed", logText, StringComparison.Ordinal);
            Assert.Contains("reason=filetransfer_bulk_receive_active", logText, StringComparison.Ordinal);
            Assert.True(File.Exists(countFile));
            Assert.True(int.TryParse(File.ReadAllText(countFile).Trim(), out var count));
            Assert.Equal(1, count);

            adapter.UnregisterActiveFileTransferDataSession("transfer-control-recovery-grace");
            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", prevFastRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY", prevControlOnlyRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ReceiveStallRecovery_ControlOnlyOverrideReconnectsWhenRegularV4PressureMarked()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-control-regular-v4-pressure", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-control-regular-v4-pressure.js");
        WriteBridgeScriptWithManifest(
            bridgePath,
            BuildReceiveStallRecoveryMockBridgeScript(
                countFile,
                controlMessagesReceivedSinceLast: 0,
                bulkMessagesReceivedSinceLast: 1,
                totalMessagesReceivedSinceLast: 1,
                controlLastReceivedAgeMs: 35_000,
                bulkLastReceivedAgeMs: 100,
                stallHealthSampleCount: 6));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");
        var prevFastRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY");
        var prevControlOnlyRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY", "1");
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "control-regular-v4-pressure");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "control-regular-v4-pressure");
            var identity = new NknIdentity("control-regular-v4-pressure", "control-regular-v4-pressure.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.RegisterActiveFileTransferDataSession("transfer-control-regular-v4-pressure");
            adapter.ReportRegularV4ControlFeedbackPressure(
                "transfer-control-regular-v4-pressure",
                "test_regular_v4_credit_frontier_pressure",
                creditExhaustedTimeMs: 42_000,
                frontierLagChunks: 512,
                pendingRepairCount: 4);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await adapter.ConnectAsync(cts.Token);

            await WaitUntilAsync(
                () =>
                {
                    var text = LocalOperationalLog.GetRecentLogText();
                    if (!text.Contains("event=nkn_bridge_control_receive_recovery_allowed", StringComparison.Ordinal) ||
                        !text.Contains("reason=regular_v4_control_feedback_pressure", StringComparison.Ordinal) ||
                        !text.Contains("event=nkn_bridge_receive_stall_recovery_started", StringComparison.Ordinal))
                    {
                        return false;
                    }

                    return TryReadIntFileShared(countFile, out var count) &&
                           count > 1;
                },
                TimeSpan.FromSeconds(5));

            var logText = LocalOperationalLog.GetRecentLogText();
            Assert.Contains("event=filetransfer_regular_v4_control_feedback_pressure_marked", logText, StringComparison.Ordinal);
            Assert.Contains("event=nkn_bridge_control_receive_recovery_allowed", logText, StringComparison.Ordinal);
            Assert.Contains("event=nkn_bridge_receive_stall_recovery_started", logText, StringComparison.Ordinal);
            Assert.Contains("stall_reason=control_receive_stalled", logText, StringComparison.Ordinal);
            Assert.True(File.Exists(countFile));
            Assert.True(TryReadIntFileShared(countFile, out var finalCount));
            Assert.True(finalCount > 1);

            adapter.UnregisterActiveFileTransferDataSession("transfer-control-regular-v4-pressure");
            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", prevFastRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY", prevControlOnlyRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ReceiveStallRecovery_ControlOnlyOverrideReconnectsWhenPostTunaFallbackControlPlanePressureMarked()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-control-post-tuna-pressure", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-control-post-tuna-pressure.js");
        WriteBridgeScriptWithManifest(
            bridgePath,
            BuildReceiveStallRecoveryMockBridgeScript(
                countFile,
                controlMessagesReceivedSinceLast: 0,
                bulkMessagesReceivedSinceLast: 1,
                totalMessagesReceivedSinceLast: 1,
                controlLastReceivedAgeMs: 35_000,
                bulkLastReceivedAgeMs: 100,
                stallHealthSampleCount: 6));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");
        var prevFastRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY");
        var prevControlOnlyRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY", "1");
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "control-post-tuna-pressure");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "control-post-tuna-pressure");
            var identity = new NknIdentity("control-post-tuna-pressure", "control-post-tuna-pressure.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            const string transferId = "transfer-control-post-tuna-pressure";
            const string sessionId = "session-control-post-tuna-pressure";
            adapter.RegisterActiveFileTransferDataSession(transferId);
            adapter.MarkActiveFileTransferPostTunaFallbackRuntime(transferId, "test");
            adapter.MarkActiveFileTransferPostTunaFallbackLegAuthority(
                transferId,
                sessionId,
                legGeneration: 4,
                routeToken: "post_tuna_fallback_v6",
                protocolVersion: 6,
                liveRouteEpoch: 3,
                transportEpoch: 7,
                bridgeRecoveryGeneration: 2,
                checkpointRequestId: "checkpoint-7",
                authorityReason: "test_authority");
            adapter.ReportPostTunaFallbackControlPlanePressure(
                sessionId,
                transferId,
                "post_tuna_fallback_v6",
                protocolVersion: 6,
                liveRouteEpoch: 3,
                legGeneration: 4,
                bridgeRecoveryGeneration: 2,
                transportEpoch: 7,
                checkpointRequestId: "checkpoint-7",
                kind: "receiver_state",
                reason: "test_control_plane_pressure");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await adapter.ConnectAsync(cts.Token);

            await WaitUntilAsync(
                () =>
                {
                    var text = LocalOperationalLog.GetRecentLogText();
                    if (!text.Contains("event=nkn_bridge_control_receive_recovery_allowed", StringComparison.Ordinal) ||
                        !text.Contains("reason=post_tuna_fallback_control_plane_pressure", StringComparison.Ordinal) ||
                        !text.Contains("event=nkn_bridge_receive_stall_recovery_started", StringComparison.Ordinal))
                    {
                        return false;
                    }

                    return TryReadIntFileShared(countFile, out var count) &&
                           count > 1;
                },
                TimeSpan.FromSeconds(5));

            var logText = LocalOperationalLog.GetRecentLogText();
            Assert.Contains("event=filetransfer_post_tuna_fallback_control_plane_pressure_marked", logText, StringComparison.Ordinal);
            Assert.Contains("event=nkn_bridge_control_receive_recovery_allowed", logText, StringComparison.Ordinal);
            Assert.Contains("reason=post_tuna_fallback_control_plane_pressure", logText, StringComparison.Ordinal);
            Assert.Contains("event=nkn_bridge_receive_stall_recovery_started", logText, StringComparison.Ordinal);
            Assert.True(File.Exists(countFile));
            Assert.True(TryReadIntFileShared(countFile, out var finalCount));
            Assert.True(finalCount > 1);

            adapter.UnregisterActiveFileTransferDataSession(transferId);
            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", prevFastRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY", prevControlOnlyRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    private static bool TryReadIntFileShared(string path, out int value)
    {
        value = 0;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return int.TryParse(reader.ReadToEnd().Trim(), out value);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ReceiveStallRecovery_SuppressesControlOnlyReconnectWhenBulkReceiveFreshBeyondGrace()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-control-stale", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-control-stale.js");
        WriteBridgeScriptWithManifest(
            bridgePath,
            BuildReceiveStallRecoveryMockBridgeScript(
                countFile,
                controlMessagesReceivedSinceLast: 0,
                bulkMessagesReceivedSinceLast: 0,
                totalMessagesReceivedSinceLast: 0,
                controlLastReceivedAgeMs: 35_000,
                bulkLastReceivedAgeMs: 100,
                stallHealthSampleCount: 6));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");
        var prevFastRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY");
        var prevControlOnlyRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY", "0");
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "control-stale");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "control-stale");
            var identity = new NknIdentity("control-stale", "control-stale.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.RegisterActiveFileTransferDataSession("transfer-control-stale");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await adapter.ConnectAsync(cts.Token);

            await WaitUntilAsync(
                () =>
                {
                    var text = LocalOperationalLog.GetRecentLogText();
                    return text.Contains("event=nkn_bridge_control_receive_recovery_suppressed", StringComparison.Ordinal) &&
                           text.Contains("reason=filetransfer_bulk_receive_fresh", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(5));

            var logText = LocalOperationalLog.GetRecentLogText();
            Assert.Contains("event=nkn_bridge_control_receive_degraded", logText, StringComparison.Ordinal);
            Assert.Contains("event=nkn_bridge_control_receive_recovery_suppressed", logText, StringComparison.Ordinal);
            Assert.Contains("reason=filetransfer_bulk_receive_fresh", logText, StringComparison.Ordinal);
            Assert.True(File.Exists(countFile));
            Assert.True(int.TryParse(File.ReadAllText(countFile).Trim(), out var count));
            Assert.Equal(1, count);

            adapter.UnregisterActiveFileTransferDataSession("transfer-control-stale");
            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", prevFastRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY", prevControlOnlyRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ReceiveStallRecovery_RegularV4FeedbackPressureBypassesProtocolRepairOnly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-regular-v4-feedback-pressure", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-regular-v4-feedback-pressure.js");
        WriteBridgeScriptWithManifest(
            bridgePath,
            BuildReceiveStallRecoveryMockBridgeScript(
                countFile,
                controlMessagesReceivedSinceLast: 0,
                bulkMessagesReceivedSinceLast: 0,
                totalMessagesReceivedSinceLast: 0,
                controlLastReceivedAgeMs: 25_000,
                bulkLastReceivedAgeMs: 25_000,
                stallHealthSampleCount: 4,
                connectKey: "regular-v4-feedback-pressure-key"));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");
        var prevFastRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY");
        var prevControlOnlyRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY", "0");
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "regular-v4-feedback-pressure");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "regular-v4-feedback-pressure");
            var identity = new NknIdentity("regular-v4-feedback-pressure", "regular-v4-feedback-pressure.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.RegisterActiveFileTransferDataSession("transfer-regular-v4-feedback-pressure");
            adapter.RegisterActiveFileTransferRuntime("transfer-regular-v4-feedback-pressure");
            var logBaseline = LocalOperationalLog.GetRecentLogText().Length;
            adapter.ReportRegularV4ControlFeedbackPressure(
                "transfer-regular-v4-feedback-pressure",
                "regular_v4_receiver_frontier_repair_due",
                creditExhaustedTimeMs: 0,
                frontierLagChunks: 128,
                pendingRepairCount: 8);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await adapter.ConnectAsync(cts.Token);

            await WaitUntilAsync(
                () =>
                {
                    var text = GetRecentLogTextSince(logBaseline);
                    return text.Contains("event=nkn_bridge_receive_stall_recovery_protocol_repair_bypassed; reason=regular_v4_control_feedback_pressure", StringComparison.Ordinal) &&
                           text.Contains("stall_reason=regular_v4_control_feedback_pressure", StringComparison.Ordinal) &&
                           text.Contains("event=nkn_bridge_receive_stall_recovery_hard_restart; connect_key=regular-v4-feedback-pressure-key", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(8));

            var logText = GetRecentLogTextSince(logBaseline);
            Assert.Contains("event=nkn_bridge_receive_stall_recovery_protocol_repair_bypassed; reason=regular_v4_control_feedback_pressure", logText, StringComparison.Ordinal);
            Assert.Contains("stall_reason=regular_v4_control_feedback_pressure", logText, StringComparison.Ordinal);
            Assert.Contains("event=nkn_bridge_receive_stall_recovery_hard_restart; connect_key=regular-v4-feedback-pressure-key", logText, StringComparison.Ordinal);
            Assert.DoesNotContain("event=nkn_bridge_receive_stall_recovery_suppressed; reason=filetransfer_protocol_repair_only; connect_key=regular-v4-feedback-pressure-key", logText, StringComparison.Ordinal);
            Assert.True(File.Exists(countFile));
            Assert.True(int.TryParse(File.ReadAllText(countFile).Trim(), out var count));
            Assert.True(count >= 1);

            adapter.UnregisterActiveFileTransferRuntime("transfer-regular-v4-feedback-pressure");
            adapter.UnregisterActiveFileTransferDataSession("transfer-regular-v4-feedback-pressure");
            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", prevFastRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY", prevControlOnlyRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ReceiveStallRecovery_RegularV4FeedbackPressureKeepsProtocolRepairWhenBulkReceiveRecent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-regular-v4-bulk-recent", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-regular-v4-bulk-recent.js");
        WriteBridgeScriptWithManifest(
            bridgePath,
            BuildReceiveStallRecoveryMockBridgeScript(
                countFile,
                controlMessagesReceivedSinceLast: 0,
                bulkMessagesReceivedSinceLast: 0,
                totalMessagesReceivedSinceLast: 0,
                controlLastReceivedAgeMs: 18_000,
                bulkLastReceivedAgeMs: 9_000,
                stallHealthSampleCount: 4,
                connectKey: "regular-v4-bulk-recent-key"));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");
        var prevFastRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY");
        var prevControlOnlyRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY", "0");
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "regular-v4-bulk-recent");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "regular-v4-bulk-recent");
            var identity = new NknIdentity("regular-v4-bulk-recent", "regular-v4-bulk-recent.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.RegisterActiveFileTransferDataSession("transfer-regular-v4-bulk-recent");
            adapter.RegisterActiveFileTransferRuntime("transfer-regular-v4-bulk-recent");
            var logBaseline = LocalOperationalLog.GetRecentLogText().Length;
            adapter.ReportRegularV4ControlFeedbackPressure(
                "transfer-regular-v4-bulk-recent",
                "regular_v4_receiver_frontier_repair_due",
                creditExhaustedTimeMs: 0,
                frontierLagChunks: 128,
                pendingRepairCount: 8);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await adapter.ConnectAsync(cts.Token);

            await WaitUntilAsync(
                () =>
                {
                    var text = GetRecentLogTextSince(logBaseline);
                    return text.Contains("event=nkn_bridge_receive_stall_recovery_suppressed; reason=regular_v4_bulk_recent", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(8));

            var logText = GetRecentLogTextSince(logBaseline);
            Assert.Contains("event=nkn_bridge_receive_stall_recovery_suppressed; reason=regular_v4_bulk_recent", logText, StringComparison.Ordinal);
            Assert.DoesNotContain("event=nkn_bridge_receive_stall_recovery_hard_restart; connect_key=regular-v4-bulk-recent-key", logText, StringComparison.Ordinal);
            Assert.DoesNotContain("event=nkn_bridge_receive_stall_recovery_protocol_repair_bypassed; reason=regular_v4_control_feedback_pressure", logText, StringComparison.Ordinal);
            Assert.True(File.Exists(countFile));
            Assert.True(int.TryParse(File.ReadAllText(countFile).Trim(), out var count));
            Assert.Equal(1, count);

            adapter.UnregisterActiveFileTransferRuntime("transfer-regular-v4-bulk-recent");
            adapter.UnregisterActiveFileTransferDataSession("transfer-regular-v4-bulk-recent");
            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", prevFastRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY", prevControlOnlyRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ReceiveStallRecovery_RetriesBeforeCooldownWhenReconnectDoesNotResumeReceive()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-receive-stall-retry", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-receive-stall-retry.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildReceiveStallRecoveryMockBridgeScript(countFile, stallConnectCount: 2));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "receive-stall-retry");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "receive-stall-retry");
            var identity = new NknIdentity("receive-stall-retry", "receive-stall-retry.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await adapter.ConnectAsync(cts.Token);

            await WaitUntilAsync(
                () => File.Exists(countFile) && int.TryParse(File.ReadAllText(countFile).Trim(), out var count) && count >= 3,
                TimeSpan.FromSeconds(6));

            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ReceiveStallRecovery_ActiveFileTransferSuppressesAutoRetryWhileProtocolRepairs()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-active-filetransfer-cooldown", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-active-filetransfer-cooldown.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildActiveFileTransferCooldownRecoveryMockBridgeScript(countFile));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "active-filetransfer-cooldown");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "active-filetransfer-cooldown");
            var identity = new NknIdentity("active-filetransfer-cooldown", "active-filetransfer-cooldown.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.RegisterActiveFileTransferDataSession("transfer-active-filetransfer-cooldown");
            var deferredLifecycleCount = 0;
            adapter.BridgeLifecycle += (_, e) =>
            {
                if (e.Kind == BridgeLifecycleEventKind.ReceiveStallRecoveryDeferred)
                {
                    Interlocked.Increment(ref deferredLifecycleCount);
                }
            };
            var logBaseline = LocalOperationalLog.GetRecentLogText().Length;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await adapter.ConnectAsync(cts.Token);

            await WaitUntilAsync(
                () => GetRecentLogTextSince(logBaseline).Contains("event=nkn_bridge_receive_stall_recovery_suppressed; reason=filetransfer_protocol_repair_only", StringComparison.Ordinal),
                TimeSpan.FromSeconds(14));

            var logText = GetRecentLogTextSince(logBaseline);
            Assert.Contains("event=nkn_bridge_receive_stall_recovery_suppressed; reason=filetransfer_protocol_repair_only", logText, StringComparison.Ordinal);
            Assert.True(Volatile.Read(ref deferredLifecycleCount) > 0);
            Assert.DoesNotContain("event=nkn_bridge_receive_stall_recovery_failed; reason=previous_recovery_unproven_cooldown", logText, StringComparison.Ordinal);
            var connectCount = int.Parse(File.ReadAllText(countFile).Trim());
            Assert.Equal(1, connectCount);

            adapter.UnregisterActiveFileTransferDataSession("transfer-active-filetransfer-cooldown");
            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ReceiveStallRecovery_PostTunaFallbackUnprovenPeerSilenceBypassesProtocolRepairOnly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-post-tuna-peer-silence-escalation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-post-tuna-peer-silence-escalation.js");
        WriteBridgeScriptWithManifest(
            bridgePath,
            BuildReceiveStallRecoveryMockBridgeScript(
                countFile,
                controlMessagesReceivedSinceLast: 0,
                bulkMessagesReceivedSinceLast: 0,
                totalMessagesReceivedSinceLast: 1,
                controlLastReceivedAgeMs: 21_000,
                bulkLastReceivedAgeMs: 21_000,
                connectKey: "post-tuna-peer-silence-escalation-key"));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");
        var prevFastRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", null);
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "post-tuna-peer-silence-escalation");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "post-tuna-peer-silence-escalation");
            var identity = new NknIdentity("post-tuna-peer-silence-escalation", "post-tuna-peer-silence-escalation.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.RegisterActiveFileTransferDataSession("transfer-post-tuna-peer-silence-escalation");
            adapter.RegisterActiveFileTransferRuntime("transfer-post-tuna-peer-silence-escalation");
            adapter.MarkActiveFileTransferPostTunaFallbackRuntime("transfer-post-tuna-peer-silence-escalation", "test_route_hint");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await adapter.ConnectAsync(cts.Token);
            var nowTick = Stopwatch.GetTimestamp();
            SetPrivateField(adapter, "receiveStallRecoveryCount", 4);
            SetPrivateField(adapter, "receiveStallLastRecoveryStartedTick", nowTick);
            SetPrivateField(adapter, "receiveStallLastRecoveryCompletedTick", nowTick);
            SetPrivateField(adapter, "receiveStallRecoveryInProgress", 1);
            SetPrivateField(adapter, "receiveStallRecoveryAwaitingReceiveProof", 0);
            SetPrivateField(adapter, "receiveStallRecoveryRequiresControlProof", 1);
            SetPrivateField(adapter, "receiveStallRecoveryRequiresBulkProof", 1);
            var logBaseline = GetOperationalLogLength();

            await WaitUntilAsync(
                () =>
                {
                    var text = ReadOperationalLogTail(logBaseline) + Environment.NewLine + LocalOperationalLog.GetRecentLogText();
                    return text.Contains("event=nkn_bridge_receive_stall_recovery_post_tuna_fallback_unproven_escalation_allowed", StringComparison.Ordinal) &&
                           text.Contains("connect_key=post-tuna-peer-silence-escalation-key", StringComparison.Ordinal) &&
                           text.Contains("trigger=filetransfer_protocol_repair_only", StringComparison.Ordinal) &&
                           text.Contains("event=nkn_bridge_receive_stall_recovery_hard_restart; connect_key=post-tuna-peer-silence-escalation-key", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(8));

            var logText = ReadOperationalLogTail(logBaseline) + Environment.NewLine + LocalOperationalLog.GetRecentLogText();
            Assert.Contains(
                "event=nkn_bridge_receive_stall_recovery_post_tuna_fallback_unproven_escalation_auto_armed; trigger=filetransfer_protocol_repair_only; requested_reason=post_tuna_fallback_peer_silence; connect_key=post-tuna-peer-silence-escalation-key",
                logText,
                StringComparison.Ordinal);
            Assert.Contains("stall_reason=post_tuna_fallback_unproven_recovery_escalation", logText, StringComparison.Ordinal);
            Assert.Contains(
                "event=nkn_bridge_receive_stall_recovery_cooldown_bypassed; reason=post_tuna_fallback_unproven_escalation; connect_key=post-tuna-peer-silence-escalation-key",
                logText,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "event=nkn_bridge_receive_stall_recovery_suppressed; reason=filetransfer_protocol_repair_only; connect_key=post-tuna-peer-silence-escalation-key",
                logText,
                StringComparison.Ordinal);

            adapter.UnregisterActiveFileTransferRuntime("transfer-post-tuna-peer-silence-escalation");
            adapter.UnregisterActiveFileTransferDataSession("transfer-post-tuna-peer-silence-escalation");
            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", prevFastRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ReceiveStallRecovery_RegularV4UnprovenFeedbackPressureBypassesCooldownOnce()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-regular-v4-unproven-escalation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-regular-v4-unproven-escalation.js");
        WriteBridgeScriptWithManifest(
            bridgePath,
            BuildReceiveStallRecoveryMockBridgeScript(
                countFile,
                controlMessagesReceivedSinceLast: 0,
                bulkMessagesReceivedSinceLast: 0,
                totalMessagesReceivedSinceLast: 1,
                controlLastReceivedAgeMs: 21_000,
                bulkLastReceivedAgeMs: 21_000));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");
        var prevFastRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", null);
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "regular-v4-unproven-escalation");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "regular-v4-unproven-escalation");
            var identity = new NknIdentity("regular-v4-unproven-escalation", "regular-v4-unproven-escalation.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.RegisterActiveFileTransferDataSession("transfer-regular-v4-unproven-escalation");
            adapter.RegisterActiveFileTransferRuntime("transfer-regular-v4-unproven-escalation");
            adapter.ReportRegularV4ControlFeedbackPressure(
                "transfer-regular-v4-unproven-escalation",
                "regular_v4_receiver_frontier_repair_due",
                creditExhaustedTimeMs: 0,
                frontierLagChunks: 12,
                pendingRepairCount: 12);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await adapter.ConnectAsync(cts.Token);
            var nowTick = Stopwatch.GetTimestamp();
            SetPrivateField(adapter, "receiveStallRecoveryCount", 4);
            SetPrivateField(adapter, "receiveStallLastRecoveryStartedTick", nowTick);
            SetPrivateField(adapter, "receiveStallLastRecoveryCompletedTick", nowTick);
            SetPrivateField(adapter, "receiveStallRecoveryAwaitingReceiveProof", 1);
            SetPrivateField(adapter, "receiveStallRecoveryRequiresControlProof", 1);
            SetPrivateField(adapter, "receiveStallRecoveryRequiresBulkProof", 1);
            var logBaseline = LocalOperationalLog.GetRecentLogText().Length;

            await WaitUntilAsync(
                () =>
                {
                    var text = GetRecentLogTextSince(logBaseline);
                    return text.Contains("event=nkn_bridge_receive_stall_recovery_regular_v4_unproven_escalation_allowed", StringComparison.Ordinal) &&
                           text.Contains("stall_reason=regular_v4_unproven_recovery_escalation", StringComparison.Ordinal) &&
                           text.Contains("event=nkn_bridge_receive_stall_recovery_hard_restart", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(8));

            var logText = GetRecentLogTextSince(logBaseline);
            Assert.Contains("event=nkn_bridge_receive_stall_recovery_cooldown_bypassed; reason=regular_v4_unproven_escalation", logText, StringComparison.Ordinal);
            Assert.Contains("event=nkn_bridge_receive_stall_recovery_budget_extended", logText, StringComparison.Ordinal);
            Assert.DoesNotContain("event=nkn_bridge_receive_stall_recovery_failed; reason=previous_recovery_unproven_cooldown", logText, StringComparison.Ordinal);
            Assert.DoesNotContain("event=nkn_bridge_receive_stall_recovery_failed; reason=active_filetransfer_unproven_cooldown", logText, StringComparison.Ordinal);
            Assert.DoesNotContain("post_tuna_fallback_unproven_recovery_escalation", logText, StringComparison.Ordinal);

            adapter.UnregisterActiveFileTransferRuntime("transfer-regular-v4-unproven-escalation");
            adapter.UnregisterActiveFileTransferDataSession("transfer-regular-v4-unproven-escalation");
            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", prevFastRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ReceiveStallRecovery_RuntimeUnlockReplayTimeoutBypassesStaleRegularV4Gate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-runtime-unlock-replay-escalation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-runtime-unlock-replay-escalation.js");
        WriteBridgeScriptWithManifest(
            bridgePath,
            BuildReceiveStallRecoveryMockBridgeScript(countFile));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");
        var prevFastRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", null);
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "runtime-unlock-replay-escalation");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "runtime-unlock-replay-escalation");
            var identity = new NknIdentity("runtime-unlock-replay-escalation", "runtime-unlock-replay-escalation.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.RegisterActiveFileTransferRuntime("transfer-runtime-unlock-replay-escalation");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await adapter.ConnectAsync(cts.Token);
            var nowTick = Stopwatch.GetTimestamp();
            SetPrivateField(adapter, "receiveStallRecoveryCount", 2);
            SetPrivateField(adapter, "receiveStallLastRecoveryStartedTick", nowTick);
            SetPrivateField(adapter, "receiveStallLastRecoveryCompletedTick", nowTick);
            SetPrivateField(adapter, "receiveStallRecoveryAwaitingReceiveProof", 1);
            SetPrivateField(adapter, "receiveStallRecoveryRequiresControlProof", 1);
            SetPrivateField(adapter, "receiveStallRecoveryRequiresBulkProof", 1);
            var logBaseline = GetOperationalLogLength();

            Assert.True(adapter.RequestFileTransferReceiveStallRecovery("tuna_activation_offer_replay_send_timeout"));

            await WaitUntilAsync(
                () =>
                {
                    var text = ReadOperationalLogTail(logBaseline) + Environment.NewLine + LocalOperationalLog.GetRecentLogText();
                    return text.Contains("requested_reason=tuna_activation_offer_replay_send_timeout", StringComparison.Ordinal) &&
                           text.Contains("event=nkn_bridge_receive_stall_recovery_regular_v4_unproven_escalation_allowed", StringComparison.Ordinal) &&
                           text.Contains("stall_reason=regular_v4_unproven_recovery_escalation", StringComparison.Ordinal) &&
                           text.Contains("event=nkn_bridge_receive_stall_recovery_hard_restart", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(8));

            var logText = ReadOperationalLogTail(logBaseline) + Environment.NewLine + LocalOperationalLog.GetRecentLogText();
            Assert.Contains("event=nkn_bridge_receive_stall_recovery_cooldown_bypassed; reason=regular_v4_unproven_escalation", logText, StringComparison.Ordinal);
            Assert.DoesNotContain("event=nkn_bridge_receive_stall_recovery_request_ignored; reason=recovery_already_in_progress; requested_reason=tuna_activation_offer_replay_send_timeout", logText, StringComparison.Ordinal);

            adapter.UnregisterActiveFileTransferRuntime("transfer-runtime-unlock-replay-escalation");
            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", prevFastRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
                // best effort
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ReceiveStallRecovery_CoreFileTransferRequestUsesHardRestart()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-core-filetransfer-recovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-core-filetransfer-recovery.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildReceiveStallRecoveryMockBridgeScript(countFile, stallConnectCount: 0));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "core-filetransfer-recovery");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "core-filetransfer-recovery");
            var identity = new NknIdentity("core-filetransfer-recovery", "core-filetransfer-recovery.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.RegisterActiveFileTransferDataSession("transfer-core-filetransfer-recovery");
            var logBaseline = LocalOperationalLog.GetRecentLogText().Length;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await adapter.ConnectAsync(cts.Token);

            Assert.True(adapter.RequestFileTransferReceiveStallRecovery("sender_request_feedback_stalled"));

            await WaitUntilAsync(
                () => File.Exists(countFile) &&
                      int.TryParse(File.ReadAllText(countFile).Trim(), out var count) &&
                      count >= 2 &&
                      GetRecentLogTextSince(logBaseline).Contains("event=nkn_bridge_receive_stall_recovery_hard_restart", StringComparison.Ordinal),
                TimeSpan.FromSeconds(10));

            var logText = GetRecentLogTextSince(logBaseline);
            Assert.Contains("event=nkn_bridge_receive_stall_recovery_hard_restart", logText, StringComparison.Ordinal);
            Assert.Contains("connect_key=core_filetransfer_request", logText, StringComparison.Ordinal);
            Assert.Contains("core_requested=1", logText, StringComparison.Ordinal);
            var connectCount = int.Parse(File.ReadAllText(countFile).Trim());
            Assert.True(connectCount >= 2);

            adapter.UnregisterActiveFileTransferDataSession("transfer-core-filetransfer-recovery");
            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ReceiveStallRecovery_PostTunaFallbackStateRefreshUsesSoftRecovery()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-post-tuna-refresh-recovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-post-tuna-refresh-recovery.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildReceiveStallRecoveryMockBridgeScript(countFile, stallConnectCount: 0));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "post-tuna-refresh-recovery");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "post-tuna-refresh-recovery");
            var identity = new NknIdentity("post-tuna-refresh-recovery", "post-tuna-refresh-recovery.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.RegisterActiveFileTransferDataSession("transfer-post-tuna-refresh-recovery");
            var logBaseline = LocalOperationalLog.GetRecentLogText().Length;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await adapter.ConnectAsync(cts.Token);

            var recoveryCountField = typeof(RealNknClientAdapter).GetField("receiveStallRecoveryCount", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(recoveryCountField);
            recoveryCountField.SetValue(adapter, 1);

            Assert.True(adapter.RequestFileTransferReceiveStallRecovery("post_tuna_fallback_state_refresh_failed"));

            await WaitUntilAsync(
                () => GetRecentLogTextSince(logBaseline).Contains(
                    "event=nkn_bridge_receive_stall_recovery_soft_for_filetransfer",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(10));

            var logText = GetRecentLogTextSince(logBaseline);
            Assert.Contains("event=nkn_bridge_receive_stall_recovery_soft_for_filetransfer", logText, StringComparison.Ordinal);
            Assert.Contains("connect_key=core_filetransfer_request", logText, StringComparison.Ordinal);
            Assert.Contains("stall_reason=post_tuna_fallback_state_refresh_failed", logText, StringComparison.Ordinal);
            Assert.Contains("attempt=2", logText, StringComparison.Ordinal);
            Assert.DoesNotContain("event=nkn_bridge_receive_stall_recovery_hard_restart", logText, StringComparison.Ordinal);

            adapter.UnregisterActiveFileTransferDataSession("transfer-post-tuna-refresh-recovery");
            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ReceiveStallRecovery_SessionLivenessPendingUsesSoftRecovery()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-session-liveness-recovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-session-liveness-recovery.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildReceiveStallRecoveryMockBridgeScript(countFile, stallConnectCount: 0));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "session-liveness-recovery");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "session-liveness-recovery");
            var identity = new NknIdentity("session-liveness-recovery", "session-liveness-recovery.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.RegisterActiveFileTransferDataSession("transfer-session-liveness-recovery");
            var logBaseline = LocalOperationalLog.GetRecentLogText().Length;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await adapter.ConnectAsync(cts.Token);

            Assert.True(adapter.RequestFileTransferReceiveStallRecovery("session_liveness_timeout_pending"));

            await WaitUntilAsync(
                () => GetRecentLogTextSince(logBaseline).Contains(
                    "event=nkn_bridge_receive_stall_recovery_soft_for_filetransfer",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(10));

            var logText = GetRecentLogTextSince(logBaseline);
            Assert.Contains("event=nkn_bridge_receive_stall_recovery_soft_for_filetransfer", logText, StringComparison.Ordinal);
            Assert.Contains("connect_key=core_filetransfer_request", logText, StringComparison.Ordinal);
            Assert.Contains("stall_reason=session_liveness_timeout_pending", logText, StringComparison.Ordinal);
            Assert.DoesNotContain("event=nkn_bridge_receive_stall_recovery_hard_restart", logText, StringComparison.Ordinal);

            adapter.UnregisterActiveFileTransferDataSession("transfer-session-liveness-recovery");
            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Theory]
    [InlineData("tuna_activation_offer_send_timeout", false)]
    [InlineData("runtime_unlock_retry_authority_offer_blocked", false)]
    [InlineData("session_liveness_timeout_pending", true)]
    public async Task Bridge_ReceiveStallRecovery_RuntimeUnlockOfferRecoveryEscalatesUnprovenRegularV4Recovery(
        string requestedReason,
        bool staleInProgress)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var reasonName = requestedReason.Replace('_', '-');
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-runtime-unlock-unproven-escalation", reasonName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, $"mock-bridge-runtime-unlock-unproven-escalation-{reasonName}.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildReceiveStallRecoveryMockBridgeScript(countFile, stallConnectCount: 0));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, $"runtime-unlock-unproven-escalation-{reasonName}");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, $"runtime-unlock-unproven-escalation-{reasonName}");
            var identity = new NknIdentity($"runtime-unlock-unproven-escalation-{reasonName}", $"runtime-unlock-unproven-escalation-{reasonName}.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.RegisterActiveFileTransferDataSession($"transfer-runtime-unlock-unproven-escalation-{reasonName}");
            adapter.RegisterActiveFileTransferRuntime($"transfer-runtime-unlock-unproven-escalation-{reasonName}");
            var logBaseline = LocalOperationalLog.GetRecentLogText().Length;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await adapter.ConnectAsync(cts.Token);

            var nowTick = Stopwatch.GetTimestamp();
            var startedTick = staleInProgress
                ? nowTick - Stopwatch.Frequency * 25
                : nowTick;
            var completedTick = staleInProgress ? 0 : nowTick;
            SetPrivateField(adapter, "receiveStallRecoveryInProgress", 1);
            SetPrivateField(adapter, "receiveStallRecoveryCount", 1);
            SetPrivateField(adapter, "receiveStallLastRecoveryStartedTick", startedTick);
            SetPrivateField(adapter, "receiveStallLastRecoveryCompletedTick", completedTick);
            SetPrivateField(adapter, "receiveStallRecoveryAwaitingReceiveProof", staleInProgress ? 0 : 1);
            SetPrivateField(adapter, "receiveStallRecoveryRequiresControlProof", 1);
            SetPrivateField(adapter, "receiveStallRecoveryRequiresBulkProof", 1);

            Assert.True(
                adapter.RequestFileTransferReceiveStallRecovery(requestedReason),
                GetRecentLogTextSince(logBaseline));
            var expectedStaleGateReason = staleInProgress
                ? "reason=regular_v4_stale_in_progress_recovery"
                : "reason=regular_v4_unproven_recovery";
            var proofSeen = false;
            var proofDeadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 10);
            while (Stopwatch.GetTimestamp() < proofDeadline)
            {
                var text = GetRecentLogTextSince(logBaseline);
                proofSeen =
                    text.Contains("event=nkn_bridge_receive_stall_recovery_stale_gate_cleared", StringComparison.Ordinal) &&
                    text.Contains(expectedStaleGateReason, StringComparison.Ordinal) &&
                    text.Contains("event=nkn_bridge_receive_stall_recovery_regular_v4_unproven_escalation_allowed", StringComparison.Ordinal) &&
                    text.Contains("event=nkn_bridge_receive_stall_recovery_hard_restart", StringComparison.Ordinal) &&
                    text.Contains("stall_reason=regular_v4_unproven_recovery_escalation", StringComparison.Ordinal);
                if (proofSeen)
                {
                    break;
                }

                await Task.Delay(50, CancellationToken.None);
            }

            Assert.True(proofSeen, GetRecentLogTextSince(logBaseline));

            var logText = GetRecentLogTextSince(logBaseline);
            if (staleInProgress)
            {
                Assert.Contains("reason=regular_v4_stale_in_progress_recovery", logText, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("event=nkn_bridge_receive_stall_recovery_cooldown_bypassed; reason=regular_v4_unproven_escalation", logText, StringComparison.Ordinal);
            }

            Assert.Contains($"requested_reason={requestedReason}", logText, StringComparison.Ordinal);
            Assert.DoesNotContain($"event=nkn_bridge_receive_stall_recovery_request_ignored; reason=recovery_already_in_progress; requested_reason={requestedReason}", logText, StringComparison.Ordinal);

            adapter.UnregisterActiveFileTransferRuntime($"transfer-runtime-unlock-unproven-escalation-{reasonName}");
            adapter.UnregisterActiveFileTransferDataSession($"transfer-runtime-unlock-unproven-escalation-{reasonName}");
            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ReceiveStallRecovery_PostTunaFallbackUnprovenSecondRefreshEscalatesToHardRestart()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-post-tuna-unproven-escalation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-post-tuna-unproven-escalation.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildReceiveStallRecoveryMockBridgeScript(countFile, stallConnectCount: 0));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "post-tuna-unproven-escalation");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "post-tuna-unproven-escalation");
            var identity = new NknIdentity("post-tuna-unproven-escalation", "post-tuna-unproven-escalation.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.RegisterActiveFileTransferDataSession("transfer-post-tuna-unproven-escalation");
            adapter.RegisterActiveFileTransferRuntime("transfer-post-tuna-unproven-escalation");
            var logBaseline = LocalOperationalLog.GetRecentLogText().Length;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await adapter.ConnectAsync(cts.Token);

            Assert.True(adapter.RequestFileTransferReceiveStallRecovery("post_tuna_fallback_state_refresh_failed"));
            await WaitUntilAsync(
                () =>
                {
                    var text = GetRecentLogTextSince(logBaseline);
                    return text.Contains("event=nkn_bridge_receive_stall_recovery_completed", StringComparison.Ordinal) &&
                           text.Contains("stall_reason=post_tuna_fallback_state_refresh_failed", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(10));

            Assert.True(adapter.RequestFileTransferReceiveStallRecovery("post_tuna_fallback_state_refresh_failed"));
            await WaitUntilAsync(
                () =>
                {
                    var text = GetRecentLogTextSince(logBaseline);
                    return text.Contains("event=nkn_bridge_receive_stall_recovery_post_tuna_fallback_unproven_escalation_allowed", StringComparison.Ordinal) &&
                           text.Contains("event=nkn_bridge_receive_stall_recovery_hard_restart", StringComparison.Ordinal) &&
                           text.Contains("stall_reason=post_tuna_fallback_unproven_recovery_escalation", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(10));

            var logText = GetRecentLogTextSince(logBaseline);
            Assert.Contains("event=nkn_bridge_receive_stall_recovery_cooldown_bypassed; reason=post_tuna_fallback_unproven_escalation", logText, StringComparison.Ordinal);
            Assert.Contains("requested_reason=post_tuna_fallback_state_refresh_failed", logText, StringComparison.Ordinal);
            Assert.DoesNotContain("event=nkn_bridge_receive_stall_recovery_request_ignored; reason=active_filetransfer_cooldown", logText, StringComparison.Ordinal);
            var connectCount = int.Parse(File.ReadAllText(countFile).Trim());
            Assert.True(connectCount >= 2);

            adapter.UnregisterActiveFileTransferRuntime("transfer-post-tuna-unproven-escalation");
            adapter.UnregisterActiveFileTransferDataSession("transfer-post-tuna-unproven-escalation");
            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void Bridge_ReceiveStallRecovery_PostTunaFallbackProofWindowRequiresFallbackRuntime()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-post-tuna-proof-window-route-scope", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "post-tuna-proof-window-route-scope");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "post-tuna-proof-window-route-scope");
            var identity = new NknIdentity("post-tuna-proof-window-route-scope", "post-tuna-proof-window-route-scope.fake");
            using var adapter = new RealNknClientAdapter(identity, options);

            adapter.RegisterActiveFileTransferDataSession("transfer-regular-v4-proof-window");
            adapter.RegisterActiveFileTransferRuntime("transfer-regular-v4-proof-window");
            var logBaseline = GetOperationalLogLength();

            adapter.ArmPostTunaFallbackProofSendWindow(
                "post_tuna_fallback_state_refresh_failed",
                "unit_test_regular_v4_only",
                "session-regular-v4-proof-window");

            var logText = ReadOperationalLogTail(logBaseline);
            Assert.Contains(
                "event=nkn_bridge_post_tuna_fallback_proof_send_window_skipped; reason=no_active_post_tuna_fallback_runtime",
                logText,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "event=nkn_bridge_post_tuna_fallback_proof_send_window_armed",
                logText,
                StringComparison.Ordinal);

            adapter.UnregisterActiveFileTransferRuntime("transfer-regular-v4-proof-window");
            adapter.UnregisterActiveFileTransferDataSession("transfer-regular-v4-proof-window");
        }
        finally
        {
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void Bridge_ReceiveStallRecovery_PostTunaFallbackProofWindowDoesNotSlideWhileAwaitingProof()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-post-tuna-proof-window-preserved", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "post-tuna-proof-window-preserved");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "post-tuna-proof-window-preserved");
            var identity = new NknIdentity("post-tuna-proof-window-preserved", "post-tuna-proof-window-preserved.fake");
            using var adapter = new RealNknClientAdapter(identity, options);

            adapter.RegisterActiveFileTransferDataSession("transfer-post-tuna-proof-window-preserved");
            adapter.RegisterActiveFileTransferRuntime("transfer-post-tuna-proof-window-preserved");
            adapter.MarkActiveFileTransferPostTunaFallbackRuntime("transfer-post-tuna-proof-window-preserved", "test_route_hint");
            SetPrivateField(adapter, "receiveStallRecoveryAwaitingReceiveProof", 1);
            var logBaseline = GetOperationalLogLength();

            adapter.ArmPostTunaFallbackProofSendWindow(
                "post_tuna_fallback_state_refresh_failed",
                "unit_test_first_state_refresh",
                "session-post-tuna-proof-window-preserved");
            var firstExpiresTick = Assert.IsType<long>(GetPrivateField(adapter, "postTunaFallbackProofSendWindowExpiresTick"));

            adapter.ArmPostTunaFallbackProofSendWindow(
                "post_tuna_fallback_state_refresh_failed",
                "unit_test_second_state_refresh",
                "session-post-tuna-proof-window-preserved");
            var secondExpiresTick = Assert.IsType<long>(GetPrivateField(adapter, "postTunaFallbackProofSendWindowExpiresTick"));

            Assert.Equal(firstExpiresTick, secondExpiresTick);
            var logText = ReadOperationalLogTail(logBaseline);
            Assert.Contains(
                "event=nkn_bridge_post_tuna_fallback_proof_send_window_armed",
                logText,
                StringComparison.Ordinal);
            Assert.Contains(
                "event=nkn_bridge_post_tuna_fallback_proof_send_window_preserved",
                logText,
                StringComparison.Ordinal);
            Assert.Contains("requested_trigger=unit_test_second_state_refresh", logText, StringComparison.Ordinal);

            adapter.UnregisterActiveFileTransferRuntime("transfer-post-tuna-proof-window-preserved");
            adapter.UnregisterActiveFileTransferDataSession("transfer-post-tuna-proof-window-preserved");
        }
        finally
        {
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ReceiveStallRecovery_PostTunaFallbackProofWindowSuppressesAutomaticHardRestart()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-post-tuna-proof-window", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-post-tuna-proof-window.js");
        WriteBridgeScriptWithManifest(
            bridgePath,
            BuildReceiveStallRecoveryMockBridgeScript(
                countFile,
                controlMessagesReceivedSinceLast: 0,
                bulkMessagesReceivedSinceLast: 0,
                totalMessagesReceivedSinceLast: 1,
                controlLastReceivedAgeMs: 21_000,
                bulkLastReceivedAgeMs: 21_000));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");
        var prevFastRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", null);
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "post-tuna-proof-window");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "post-tuna-proof-window");
            var identity = new NknIdentity("post-tuna-proof-window", "post-tuna-proof-window.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.RegisterActiveFileTransferDataSession("transfer-post-tuna-proof-window");
            adapter.RegisterActiveFileTransferRuntime("transfer-post-tuna-proof-window");
            adapter.MarkActiveFileTransferPostTunaFallbackRuntime("transfer-post-tuna-proof-window", "test_route_hint");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await adapter.ConnectAsync(cts.Token);

            var nowTick = Stopwatch.GetTimestamp();
            SetPrivateField(adapter, "receiveStallRecoveryCount", 4);
            SetPrivateField(adapter, "receiveStallLastRecoveryStartedTick", nowTick);
            SetPrivateField(adapter, "receiveStallLastRecoveryCompletedTick", nowTick);
            SetPrivateField(adapter, "receiveStallRecoveryAwaitingReceiveProof", 1);
            SetPrivateField(adapter, "receiveStallRecoveryRequiresControlProof", 1);
            SetPrivateField(adapter, "receiveStallRecoveryRequiresBulkProof", 1);
            adapter.ArmPostTunaFallbackProofSendWindow(
                "post_tuna_fallback_state_refresh_failed",
                "unit_test_state_refresh_queued",
                "session-post-tuna-proof-window");
            var logBaseline = GetOperationalLogLength();

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logBaseline).Contains(
                    "event=nkn_bridge_receive_stall_recovery_suppressed; reason=post_tuna_fallback_proof_send_window",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(8));

            var logText = ReadOperationalLogTail(logBaseline);
            Assert.Contains("proof_window_trigger=unit_test_state_refresh_queued", logText, StringComparison.Ordinal);
            Assert.DoesNotContain("event=nkn_bridge_receive_stall_recovery_post_tuna_fallback_unproven_escalation_allowed", logText, StringComparison.Ordinal);
            Assert.DoesNotContain("event=nkn_bridge_receive_stall_recovery_hard_restart", logText, StringComparison.Ordinal);

            adapter.UnregisterActiveFileTransferRuntime("transfer-post-tuna-proof-window");
            adapter.UnregisterActiveFileTransferDataSession("transfer-post-tuna-proof-window");
            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", prevFastRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ReceiveStallRecovery_PostTunaFallbackSoftFailureFallsBackToHardRestart()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-post-tuna-soft-fallback", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-post-tuna-soft-fallback.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildReceiveStallRecoverySoftFailureThenHardRestartMockBridgeScript(countFile));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "post-tuna-soft-fallback");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "post-tuna-soft-fallback");
            var identity = new NknIdentity("post-tuna-soft-fallback", "post-tuna-soft-fallback.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.SetConnectReadyTimeoutForTests(TimeSpan.FromMilliseconds(150));
            adapter.RegisterActiveFileTransferDataSession("transfer-post-tuna-soft-fallback");
            var logBaseline = LocalOperationalLog.GetRecentLogText().Length;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await adapter.ConnectAsync(cts.Token);

            var recoveryCountField = typeof(RealNknClientAdapter).GetField("receiveStallRecoveryCount", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(recoveryCountField);
            recoveryCountField.SetValue(adapter, 1);

            Assert.True(adapter.RequestFileTransferReceiveStallRecovery("post_tuna_fallback_state_refresh_failed"));

            await WaitUntilAsync(
                () =>
                    File.Exists(countFile) &&
                    int.TryParse(File.ReadAllText(countFile).Trim(), out var count) &&
                    count >= 3 &&
                    GetRecentLogTextSince(logBaseline).Contains(
                        "event=nkn_bridge_receive_stall_recovery_soft_failed_hard_restart",
                        StringComparison.Ordinal) &&
                    GetRecentLogTextSince(logBaseline).Contains(
                        "event=nkn_bridge_receive_stall_recovery_completed",
                        StringComparison.Ordinal),
                TimeSpan.FromSeconds(10));

            var logText = GetRecentLogTextSince(logBaseline);
            Assert.Contains("event=nkn_bridge_receive_stall_recovery_soft_for_filetransfer", logText, StringComparison.Ordinal);
            Assert.Contains("event=nkn_bridge_receive_stall_recovery_soft_failed_hard_restart", logText, StringComparison.Ordinal);
            Assert.Contains("event=nkn_bridge_receive_stall_recovery_hard_restart", logText, StringComparison.Ordinal);
            Assert.Contains("trigger=soft_failed", logText, StringComparison.Ordinal);
            Assert.Contains("connect_key=core_filetransfer_request", logText, StringComparison.Ordinal);
            Assert.Contains("stall_reason=post_tuna_fallback_state_refresh_failed", logText, StringComparison.Ordinal);
            Assert.DoesNotContain("event=nkn_bridge_receive_stall_recovery_failed", logText, StringComparison.Ordinal);
            var connectCount = int.Parse(File.ReadAllText(countFile).Trim());
            Assert.True(connectCount >= 3);

            adapter.UnregisterActiveFileTransferDataSession("transfer-post-tuna-soft-fallback");
            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ReceiveStallRecovery_DisabledDoesNotReconnect()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-receive-stall-disabled", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var countFile = Path.Combine(tempDir, "connect-count.txt");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-receive-stall-disabled.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildReceiveStallRecoveryMockBridgeScript(countFile));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevRecovery = Environment.GetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", "0");
            var keyPath = Path.Combine(tempDir, "identity.json");
            WriteIdentityFile(keyPath, "receive-stall-disabled");
            var seedBackend = new FakeProtectedSeedBackend();
            seedBackend.SaveSeed(keyPath, RandomNumberGenerator.GetBytes(32));
            using var seedBackendOverride = NknSecretStore.OverrideBackendForTests(seedBackend);
            var options = LoadNknOptionsWithOverrides(keyPath, "receive-stall-disabled");
            var identity = new NknIdentity("receive-stall-disabled", "receive-stall-disabled.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await adapter.ConnectAsync(cts.Token);
            await Task.Delay(750, cts.Token);

            var count = File.Exists(countFile) && int.TryParse(File.ReadAllText(countFile).Trim(), out var parsed)
                ? parsed
                : 0;
            Assert.Equal(1, count);

            await adapter.DisconnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", prevRecovery);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ConnectPayload_RespectsPreflightOptions()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-preflight-payload", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var payloadFile = Path.Combine(tempDir, "payload.json");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-preflight-payload.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildMockBridgeScriptWithCustomConnect(connectBehaviorJs: $@"
    fs.writeFileSync({JsonSerializer.Serialize(payloadFile)}, JSON.stringify(msg));
    emit({{ event:'ok', id: msg.id ?? null, cmd:'connect' }});
    setTimeout(() => emit({{ event:'ready', protocol:2, channels:['control','media','bulk'], address:'payload-test.addr', controlAddress:'payload-test.addr', mediaAddress:'payload-test-media.addr', bulkAddress:'payload-test-bulk.addr', connectId: msg.connectId ?? null }}), 20);
    return;
    "));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevEnabled = Environment.GetEnvironmentVariable("NLINK_NKN_PREFLIGHT_RPC_ENABLED");
        var prevTimeout = Environment.GetEnvironmentVariable("NLINK_NKN_PREFLIGHT_TIMEOUT_MS");
        var prevConc = Environment.GetEnvironmentVariable("NLINK_NKN_PREFLIGHT_CONCURRENCY");
        var prevTtl = Environment.GetEnvironmentVariable("NLINK_NKN_PREFLIGHT_CACHE_TTL_MS");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            // Disabled by default -> no preflight fields.
            Environment.SetEnvironmentVariable("NLINK_NKN_PREFLIGHT_RPC_ENABLED", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_PREFLIGHT_TIMEOUT_MS", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_PREFLIGHT_CONCURRENCY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_PREFLIGHT_CACHE_TTL_MS", null);
            var disabledOptions = LoadNknOptionsWithOverrides(Path.Combine(tempDir, "id-disabled.json"), "mock-preflight-disabled");
            var disabledIdentity = NknIdentityStore.LoadOrCreate(disabledOptions);
            using (var adapterDisabled = new RealNknClientAdapter(disabledIdentity, disabledOptions))
            {
                await adapterDisabled.ConnectAsync(CancellationToken.None);
                await adapterDisabled.DisconnectAsync();
            }

            using (var payloadDoc = JsonDocument.Parse(File.ReadAllText(payloadFile)))
            {
                var root = payloadDoc.RootElement;
                Assert.True(root.TryGetProperty("connectId", out _));
                Assert.True(root.TryGetProperty("seedBase64", out var disabledSeedProp));
                Assert.False(string.IsNullOrWhiteSpace(disabledSeedProp.GetString()));
                Assert.False(root.TryGetProperty("preflightRpcEnabled", out _));
                Assert.False(root.TryGetProperty("preflightTimeoutMs", out _));
                Assert.False(root.TryGetProperty("preflightConcurrency", out _));
                Assert.False(root.TryGetProperty("preflightCacheTtlMs", out _));
            }

            // Enabled -> fields present.
            Environment.SetEnvironmentVariable("NLINK_NKN_PREFLIGHT_RPC_ENABLED", "true");
            Environment.SetEnvironmentVariable("NLINK_NKN_PREFLIGHT_TIMEOUT_MS", "701");
            Environment.SetEnvironmentVariable("NLINK_NKN_PREFLIGHT_CONCURRENCY", "9");
            Environment.SetEnvironmentVariable("NLINK_NKN_PREFLIGHT_CACHE_TTL_MS", "600001");
            var enabledOptions = LoadNknOptionsWithOverrides(Path.Combine(tempDir, "id-enabled.json"), "mock-preflight-enabled");
            var enabledIdentity = NknIdentityStore.LoadOrCreate(enabledOptions);
            using (var adapterEnabled = new RealNknClientAdapter(enabledIdentity, enabledOptions))
            {
                await adapterEnabled.ConnectAsync(CancellationToken.None);
                await adapterEnabled.DisconnectAsync();
            }

            using (var payloadDoc = JsonDocument.Parse(File.ReadAllText(payloadFile)))
            {
                var root = payloadDoc.RootElement;
                Assert.True(root.TryGetProperty("seedBase64", out var enabledSeedProp));
                Assert.False(string.IsNullOrWhiteSpace(enabledSeedProp.GetString()));
                Assert.True(root.TryGetProperty("preflightRpcEnabled", out var enabledProp) && enabledProp.ValueKind is JsonValueKind.True);
                Assert.Equal(701, root.GetProperty("preflightTimeoutMs").GetInt32());
                Assert.Equal(9, root.GetProperty("preflightConcurrency").GetInt32());
                Assert.Equal(600001, root.GetProperty("preflightCacheTtlMs").GetInt32());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_PREFLIGHT_RPC_ENABLED", prevEnabled);
            Environment.SetEnvironmentVariable("NLINK_NKN_PREFLIGHT_TIMEOUT_MS", prevTimeout);
            Environment.SetEnvironmentVariable("NLINK_NKN_PREFLIGHT_CONCURRENCY", prevConc);
            Environment.SetEnvironmentVariable("NLINK_NKN_PREFLIGHT_CACHE_TTL_MS", prevTtl);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ConnectPayload_RespectsSubClientTopologyOptions()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-topology-payload", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var payloadFile = Path.Combine(tempDir, "payload.json");
        var bridgePath = Path.Combine(tempDir, "mock-bridge-topology-payload.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildMockBridgeScriptWithCustomConnect(connectBehaviorJs: $@"
    fs.writeFileSync({JsonSerializer.Serialize(payloadFile)}, JSON.stringify(msg));
    emit({{ event:'ok', id: msg.id ?? null, cmd:'connect' }});
    setTimeout(() => emit({{ event:'ready', protocol:2, channels:['control','media','bulk'], address:'payload-test.addr', controlAddress:'payload-test.addr', mediaAddress:'payload-test-media.addr', bulkAddress:'payload-test-bulk.addr', connectId: msg.connectId ?? null }}), 20);
    return;
    "));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevNumSubClients = Environment.GetEnvironmentVariable("NLINK_NKN_NUM_SUBCLIENTS");
        var prevMediaNumSubClients = Environment.GetEnvironmentVariable("NLINK_NKN_MEDIA_NUM_SUBCLIENTS");
        var prevBulkNumSubClients = Environment.GetEnvironmentVariable("NLINK_NKN_BULK_NUM_SUBCLIENTS");
        var prevBulkSendConcurrency = Environment.GetEnvironmentVariable("NLINK_NKN_BULK_SEND_CONCURRENCY");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_NUM_SUBCLIENTS", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_MEDIA_NUM_SUBCLIENTS", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_BULK_NUM_SUBCLIENTS", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_BULK_SEND_CONCURRENCY", null);

            var defaultOptions = LoadNknOptionsWithOverrides(Path.Combine(tempDir, "id-default.json"), "mock-topology-default");
            var defaultIdentity = NknIdentityStore.LoadOrCreate(defaultOptions);
            using (var adapterDefault = new RealNknClientAdapter(defaultIdentity, defaultOptions))
            {
                await adapterDefault.ConnectAsync(CancellationToken.None);
                await adapterDefault.DisconnectAsync();
            }

            using (var payloadDoc = JsonDocument.Parse(File.ReadAllText(payloadFile)))
            {
                var root = payloadDoc.RootElement;
                Assert.False(root.TryGetProperty("numSubClients", out _));
                Assert.False(root.TryGetProperty("mediaNumSubClients", out _));
                Assert.False(root.TryGetProperty("bulkNumSubClients", out _));
                Assert.False(root.TryGetProperty("bulkSendConcurrency", out _));
            }

            Environment.SetEnvironmentVariable("NLINK_NKN_NUM_SUBCLIENTS", "6");
            Environment.SetEnvironmentVariable("NLINK_NKN_MEDIA_NUM_SUBCLIENTS", "8");
            Environment.SetEnvironmentVariable("NLINK_NKN_BULK_NUM_SUBCLIENTS", "10");
            Environment.SetEnvironmentVariable("NLINK_NKN_BULK_SEND_CONCURRENCY", "7");
            var configuredOptions = LoadNknOptionsWithOverrides(Path.Combine(tempDir, "id-configured.json"), "mock-topology-configured");
            var configuredIdentity = NknIdentityStore.LoadOrCreate(configuredOptions);
            using (var adapterConfigured = new RealNknClientAdapter(configuredIdentity, configuredOptions))
            {
                await adapterConfigured.ConnectAsync(CancellationToken.None);
                await adapterConfigured.DisconnectAsync();
            }

            using (var payloadDoc = JsonDocument.Parse(File.ReadAllText(payloadFile)))
            {
                var root = payloadDoc.RootElement;
                Assert.Equal(6, root.GetProperty("numSubClients").GetInt32());
                Assert.Equal(8, root.GetProperty("mediaNumSubClients").GetInt32());
                Assert.Equal(10, root.GetProperty("bulkNumSubClients").GetInt32());
                Assert.Equal(7, root.GetProperty("bulkSendConcurrency").GetInt32());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_NUM_SUBCLIENTS", prevNumSubClients);
            Environment.SetEnvironmentVariable("NLINK_NKN_MEDIA_NUM_SUBCLIENTS", prevMediaNumSubClients);
            Environment.SetEnvironmentVariable("NLINK_NKN_BULK_NUM_SUBCLIENTS", prevBulkNumSubClients);
            Environment.SetEnvironmentVariable("NLINK_NKN_BULK_SEND_CONCURRENCY", prevBulkSendConcurrency);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_Connect_FailsCleanly_WhenBridgeProtocolVersionIsOutdated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-protocol-outdated", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var bridgePath = Path.Combine(tempDir, "mock-bridge-protocol-outdated.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildMockBridgeScriptWithCustomConnect(connectBehaviorJs: @"
    emit({ event:'ok', id: msg.id ?? null, cmd:'connect' });
    setTimeout(() => emit({
      event:'ready',
      protocol:1,
      channels:['control','media','bulk'],
      address:'legacy.addr',
      controlAddress:'legacy.addr',
      mediaAddress:'legacy-media.addr',
      bulkAddress:'legacy-bulk.addr',
      connectId: msg.connectId ?? null
    }), 40);
    return;
    "));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            var options = LoadNknOptionsWithOverrides(Path.Combine(tempDir, "id.json"), "mock-protocol-outdated");
            var identity = NknIdentityStore.LoadOrCreate(options);
            using var adapter = new RealNknClientAdapter(identity, options);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ConnectAsync(CancellationToken.None));
            Assert.Contains("bridge_protocol_outdated", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("required protocol 2", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_ProgressDiagnostics_AreRecorded_OnConnectReadyTimeout()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-progress-timeout", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var bridgePath = Path.Combine(tempDir, "mock-bridge-progress-timeout.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildMockBridgeScriptWithCustomConnect(connectBehaviorJs: @"
    emit({ event:'ok', id: msg.id ?? null, cmd:'connect' });
    emit({ event:'rpc_selected', rpc:'https://mock-rpc-1.example:30003', connectId: msg.connectId ?? null, ts: Date.now() });
    return;
    "));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            var options = LoadNknOptionsWithOverrides(Path.Combine(tempDir, "id.json"), "mock-progress-timeout");
            var identity = NknIdentityStore.LoadOrCreate(options);
            using var adapter = new RealNknClientAdapter(identity, options);
            adapter.SetConnectReadyTimeoutForTests(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<TimeoutException>(() => adapter.ConnectAsync(CancellationToken.None));
            var snapshot = NknRuntimeDiagnostics.Snapshot();
            Assert.Equal("rpc_selected", snapshot.LastProgressEventType);
            Assert.Equal("https://mock-rpc-1.example:30003", snapshot.LastSelectedRpc);
            Assert.True(snapshot.LastProgressEventUtcTicks > 0);
            Assert.Contains("bridge_connect_ready_timeout", snapshot.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("progress=rpc_selected", snapshot.LastError, StringComparison.OrdinalIgnoreCase);
            var failure = TransportFailureMapper.FromSignals(snapshot.LastError);
            Assert.Equal(TransportFailureCategory.HandshakeTimeout, failure.Category);
            Assert.True(failure.IsTransient);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Trait("Category", "BridgeStabilityPromotion")]
    [Fact]
    public async Task Bridge_Disconnect_WithUnresponsiveShutdownBridge_ForcesKill_AndCleansProcessHandles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-ignore-shutdown", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var bridgePath = Path.Combine(tempDir, "mock-bridge-ignore-shutdown.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildMockBridgeScript(delayPongMs: 0, respondToPing: true, respondToShutdown: false));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            var options = NknTransportOptions.Load();
            var identity = new NknIdentity("mock-ignore-shutdown", "mock-ignore-shutdown.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await adapter.StartBridgeAsync(cts.Token);
            Assert.True(adapter.IsBridgeProcessRunning);
            await adapter.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => !adapter.IsBridgeProcessRunning, TimeSpan.FromSeconds(2));
            var debugState = adapter.GetDebugStateForTests();
            Assert.False(debugState.HasProcessReference);
            Assert.False(debugState.HasStdinReference);
            Assert.False(debugState.HasStdoutReaderTaskReference);
            Assert.False(debugState.HasStderrReaderTaskReference);
            Assert.Equal(0, debugState.TrackedPid);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Trait("Category", "BridgeStabilityPromotion")]
    [Fact]
    public async Task Bridge_Dispose_AfterStart_ShutsDownProcess_AndClearsHandles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-dispose", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var bridgePath = Path.Combine(tempDir, "mock-bridge-dispose.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildMockBridgeScript(delayPongMs: 0, respondToPing: true, respondToShutdown: true));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        Process? bridgeProcess = null;
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            var options = NknTransportOptions.Load();
            var identity = new NknIdentity("mock-dispose", "mock-dispose.fake");
            var adapter = new RealNknClientAdapter(identity, options);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await adapter.StartBridgeAsync(cts.Token);
            Assert.True(adapter.IsBridgeProcessRunning);
            var snapshot = NknRuntimeDiagnostics.Snapshot();
            Assert.True(snapshot.BridgePid > 0);
            bridgeProcess = Process.GetProcessById(snapshot.BridgePid);
            adapter.Dispose();
            await WaitUntilAsync(() =>
            {
                try
                {
                    return bridgeProcess.HasExited;
                }
                catch
                {
                    return true;
                }
            }, TimeSpan.FromSeconds(3));
            var debugState = adapter.GetDebugStateForTests();
            Assert.False(debugState.HasProcessReference);
            Assert.False(debugState.HasStdinReference);
            Assert.False(debugState.HasStdoutReaderTaskReference);
            Assert.False(debugState.HasStderrReaderTaskReference);
            Assert.Equal(0, debugState.TrackedPid);
        }
        finally
        {
            try
            {
                bridgeProcess?.Dispose();
            }
            catch
            {
            }

            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void RealNknClientAdapter_SuppressesExpectedShutdownWebSocketStderr()
    {
        var adapter = (RealNknClientAdapter)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(RealNknClientAdapter));
        var gateField = typeof(RealNknClientAdapter).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic);
        var shuttingDownField = typeof(RealNknClientAdapter).GetField("shuttingDown", BindingFlags.Instance | BindingFlags.NonPublic);
        var disposedField = typeof(RealNknClientAdapter).GetField("disposed", BindingFlags.Instance | BindingFlags.NonPublic);
        var method = typeof(RealNknClientAdapter).GetMethod("ShouldSuppressBridgeStderrDuringShutdown", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(gateField);
        Assert.NotNull(shuttingDownField);
        Assert.NotNull(disposedField);
        Assert.NotNull(method);
        gateField!.SetValue(adapter, new object ());
        shuttingDownField!.SetValue(adapter, true);
        disposedField!.SetValue(adapter, false);
        Assert.True((bool)(method!.Invoke(adapter, ["[nkn-bridge] WebSocket error: WebSocket was closed before the connection was established"]) ?? false));
        Assert.False((bool)(method.Invoke(adapter, ["[nkn-bridge] some other error"]) ?? true));
        shuttingDownField.SetValue(adapter, false);
        disposedField.SetValue(adapter, true);
        Assert.True((bool)(method.Invoke(adapter, ["[nkn-bridge] WebSocket error: WebSocket was closed before the connection was established"]) ?? false));
    }

    [Fact]
    public void Bridge_PingTimeoutDuringActiveFileTransferWithRecentBridgeHealth_IsSuppressed()
    {
        var adapter = (RealNknClientAdapter)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(RealNknClientAdapter));
        var activeSessionsField = typeof(RealNknClientAdapter).GetField("activeFileTransferDataSessions", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(activeSessionsField);
        activeSessionsField!.SetValue(adapter, 1);

        var suppressed = adapter.SuppressBridgePingTimeoutForActiveFileTransferForTests(
            framesSentSinceLast: 12,
            messagesReceivedSinceLast: 0,
            controlLastReceivedAgeMs: 45_987,
            bulkLastReceivedAgeMs: 42_460,
            disconnectSignalCount: 0,
            healthSummaryAge: TimeSpan.FromSeconds(1));

        Assert.True(suppressed);
        var logText = LocalOperationalLog.GetRecentLogText();
        Assert.Contains("event=nkn_bridge_ping_timeout_filetransfer_recovery_suppressed", logText, StringComparison.Ordinal);
        Assert.Contains("reason=recent_bridge_health_summary", logText, StringComparison.Ordinal);
    }

    [Fact]
    public void Bridge_PingTimeoutDuringActiveFileTransferWithoutRecentBridgeHealth_IsNotSuppressed()
    {
        var adapter = (RealNknClientAdapter)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(RealNknClientAdapter));
        var activeSessionsField = typeof(RealNknClientAdapter).GetField("activeFileTransferDataSessions", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(activeSessionsField);
        activeSessionsField!.SetValue(adapter, 1);

        var suppressed = adapter.SuppressBridgePingTimeoutForActiveFileTransferForTests(
            framesSentSinceLast: 12,
            messagesReceivedSinceLast: 0,
            controlLastReceivedAgeMs: 45_987,
            bulkLastReceivedAgeMs: 42_460,
            disconnectSignalCount: 0,
            healthSummaryAge: TimeSpan.FromSeconds(10));

        Assert.False(suppressed);
        var logText = LocalOperationalLog.GetRecentLogText();
        Assert.Contains("event=nkn_bridge_ping_timeout_filetransfer_recovery_not_suppressed", logText, StringComparison.Ordinal);
        Assert.Contains("reason=stale_bridge_health_and_send", logText, StringComparison.Ordinal);
    }

    [Fact]
    public void Bridge_PingTimeoutDuringActiveFileTransferWithRecentBridgeSend_IsSuppressed()
    {
        var adapter = (RealNknClientAdapter)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(RealNknClientAdapter));
        var activeSessionsField = typeof(RealNknClientAdapter).GetField("activeFileTransferDataSessions", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(activeSessionsField);
        activeSessionsField!.SetValue(adapter, 1);

        var suppressed = adapter.SuppressBridgePingTimeoutForActiveFileTransferAfterRecentSendForTests(
            NknBridgeChannel.Bulk,
            payloadBytes: 64_950,
            serializedBytes: 65_050,
            sendAge: TimeSpan.FromSeconds(1));

        Assert.True(suppressed);
        var logText = LocalOperationalLog.GetRecentLogText();
        Assert.Contains("event=nkn_bridge_ping_timeout_filetransfer_recovery_suppressed", logText, StringComparison.Ordinal);
        Assert.Contains("reason=recent_bridge_send", logText, StringComparison.Ordinal);
    }

    [Fact]
    public void Bridge_PingTimeoutDuringActiveFileTransferWithStaleBridgeSend_IsNotSuppressed()
    {
        var adapter = (RealNknClientAdapter)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(RealNknClientAdapter));
        var activeSessionsField = typeof(RealNknClientAdapter).GetField("activeFileTransferDataSessions", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(activeSessionsField);
        activeSessionsField!.SetValue(adapter, 1);

        var suppressed = adapter.SuppressBridgePingTimeoutForActiveFileTransferAfterRecentSendForTests(
            NknBridgeChannel.Bulk,
            payloadBytes: 64_950,
            serializedBytes: 65_050,
            sendAge: TimeSpan.FromSeconds(10));

        Assert.False(suppressed);
        var logText = LocalOperationalLog.GetRecentLogText();
        Assert.Contains("event=nkn_bridge_ping_timeout_filetransfer_recovery_not_suppressed", logText, StringComparison.Ordinal);
        Assert.Contains("reason=stale_bridge_health_and_send", logText, StringComparison.Ordinal);
    }

    [Fact]
    public void Bridge_ReceiveStallRecoveryFailureDuringActiveFileTransferEmitsExhaustedNotDisconnected()
    {
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("receive-stall-active-filetransfer-exhausted", "receive-stall-active-filetransfer-exhausted.fake");
        using var adapter = new RealNknClientAdapter(identity, options);
        var disconnectedCount = 0;
        BridgeLifecycleEvent? exhaustedEvent = null;
        adapter.Disconnected += (_, _) => Interlocked.Increment(ref disconnectedCount);
        adapter.BridgeLifecycle += (_, e) =>
        {
            if (e.Kind == BridgeLifecycleEventKind.ReceiveStallRecoveryExhausted)
            {
                exhaustedEvent = e;
            }
        };
        adapter.RegisterActiveFileTransferDataSession("transfer-receive-stall-active-exhausted");

        Assert.True(adapter.EmitActiveFileTransferReceiveStallRecoveryExhaustedForTests("tuna_activation_offer_send_timeout"));

        Assert.Equal(0, Volatile.Read(ref disconnectedCount));
        Assert.NotNull(exhaustedEvent);
        Assert.Equal("tuna_activation_offer_send_timeout_recovery_failed", exhaustedEvent.Value.ExitReasonText);
        var logText = LocalOperationalLog.GetRecentLogText();
        Assert.Contains("event=nkn_bridge_receive_stall_recovery_exhausted_for_filetransfer", logText, StringComparison.Ordinal);
        Assert.Contains("stall_reason=tuna_activation_offer_send_timeout", logText, StringComparison.Ordinal);
    }

    [Fact]
    public void Bridge_ReceiveStallRecoveryFailureWithoutActiveFileTransferDoesNotEmitExhausted()
    {
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("receive-stall-no-filetransfer-exhausted", "receive-stall-no-filetransfer-exhausted.fake");
        using var adapter = new RealNknClientAdapter(identity, options);
        BridgeLifecycleEvent? exhaustedEvent = null;
        adapter.BridgeLifecycle += (_, e) =>
        {
            if (e.Kind == BridgeLifecycleEventKind.ReceiveStallRecoveryExhausted)
            {
                exhaustedEvent = e;
            }
        };

        Assert.False(adapter.EmitActiveFileTransferReceiveStallRecoveryExhaustedForTests("tuna_activation_offer_send_timeout"));
        Assert.Null(exhaustedEvent);
    }

    [Fact]
    public void Bridge_PostTunaFallbackRequestClearsStaleRecoveryInProgressGate()
    {
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("post-tuna-stale-recovery-gate", "post-tuna-stale-recovery-gate.fake");
        using var adapter = new RealNknClientAdapter(identity, options);
        adapter.RegisterActiveFileTransferDataSession("transfer-post-tuna-stale-recovery-gate");
        adapter.RegisterActiveFileTransferRuntime("transfer-post-tuna-stale-recovery-gate");
        var staleStartedTick = Stopwatch.GetTimestamp() - (long)(Stopwatch.Frequency * 25.0);
        SetPrivateField(adapter, "receiveStallRecoveryInProgress", 1);
        SetPrivateField(adapter, "receiveStallRecoveryCount", 1);
        SetPrivateField(adapter, "receiveStallLastRecoveryStartedTick", staleStartedTick);
        SetPrivateField(adapter, "receiveStallLastRecoveryCompletedTick", 0L);
        SetPrivateField(adapter, "receiveStallRecoveryAwaitingReceiveProof", 0);
        var logBaseline = LocalOperationalLog.GetRecentLogText().Length;

        var cleared = Assert.IsType<bool>(InvokePrivateMethod(
            adapter,
            "TryClearCompletedUnprovenPostTunaFallbackRecoveryGate",
            "post_tuna_fallback_stale_state_refresh_send_retired",
            "unit_test"));

        Assert.True(cleared);
        var logText = GetRecentLogTextSince(logBaseline);
        Assert.Contains("event=nkn_bridge_receive_stall_recovery_stale_gate_cleared", logText, StringComparison.Ordinal);
        Assert.Contains("reason=post_tuna_fallback_stale_in_progress_recovery", logText, StringComparison.Ordinal);
        Assert.Contains("requested_reason=post_tuna_fallback_stale_state_refresh_send_retired", logText, StringComparison.Ordinal);
        adapter.UnregisterActiveFileTransferRuntime("transfer-post-tuna-stale-recovery-gate");
        adapter.UnregisterActiveFileTransferDataSession("transfer-post-tuna-stale-recovery-gate");
    }

    [Trait("Category", "LegacySmoke")]
    [Trait("Category", "BridgeStabilityPromotion")]
    [Fact]
    public async Task Bridge_Dispose_WithUnresponsiveShutdownBridge_ForcesKill_AndClearsHandles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-dispose-ignore-shutdown", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var bridgePath = Path.Combine(tempDir, "mock-bridge-dispose-ignore-shutdown.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildMockBridgeScript(delayPongMs: 0, respondToPing: true, respondToShutdown: false));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        Process? bridgeProcess = null;
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            var options = NknTransportOptions.Load();
            var identity = new NknIdentity("mock-dispose-force-kill", "mock-dispose-force-kill.fake");
            var adapter = new RealNknClientAdapter(identity, options);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await adapter.StartBridgeAsync(cts.Token);
            Assert.True(adapter.IsBridgeProcessRunning);
            var snapshot = NknRuntimeDiagnostics.Snapshot();
            Assert.True(snapshot.BridgePid > 0);
            bridgeProcess = Process.GetProcessById(snapshot.BridgePid);
            adapter.Dispose();
            await WaitUntilAsync(() =>
            {
                try
                {
                    return bridgeProcess.HasExited;
                }
                catch
                {
                    return true;
                }
            }, TimeSpan.FromSeconds(4));
            var debugState = adapter.GetDebugStateForTests();
            Assert.False(debugState.HasProcessReference);
            Assert.False(debugState.HasStdinReference);
            Assert.False(debugState.HasStdoutReaderTaskReference);
            Assert.False(debugState.HasStderrReaderTaskReference);
            Assert.Equal(0, debugState.TrackedPid);
        }
        finally
        {
            try
            {
                bridgeProcess?.Dispose();
            }
            catch
            {
            }

            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Trait("Category", "BridgeStabilityPromotion")]
    [Fact]
    public async Task Bridge_StderrSpam_DoesNotHang_AndShutsDownCleanly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-stderr-spam", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var bridgePath = Path.Combine(tempDir, "mock-bridge-stderr-spam.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildMockBridgeScriptWithStderrSpam());
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            var options = NknTransportOptions.Load();
            var identity = new NknIdentity("mock-stderr-spam", "mock-stderr-spam.fake");
            using var adapter = new RealNknClientAdapter(identity, options);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await adapter.StartBridgeAsync(cts.Token);
            await adapter.PingBridgeAsync(cts.Token);
            await adapter.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
            await WaitUntilAsync(() => !adapter.IsBridgeProcessRunning, TimeSpan.FromSeconds(2));
            var debugState = adapter.GetDebugStateForTests();
            Assert.False(debugState.HasProcessReference);
            Assert.False(debugState.HasStdoutReaderTaskReference);
            Assert.False(debugState.HasStderrReaderTaskReference);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Trait("Category", "BridgeStabilityPromotion")]
    [Fact]
    public async Task Bridge_RapidStartDisposeCycles_DoNotLeaveOrphanProcessesOrHandleRefs()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-rapid-cycles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var bridgePath = Path.Combine(tempDir, "mock-bridge-rapid-cycles.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildMockBridgeScript(delayPongMs: 0, respondToPing: true, respondToShutdown: true));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            for (var i = 0; i < 50; i++)
            {
                var options = NknTransportOptions.Load();
                var identity = new NknIdentity("mock-cycle-" + i, "mock-cycle.fake");
                using var adapter = new RealNknClientAdapter(identity, options);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await adapter.StartBridgeAsync(cts.Token);
                Assert.True(adapter.IsBridgeProcessRunning);
                await adapter.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
                await WaitUntilAsync(() => !adapter.IsBridgeProcessRunning, TimeSpan.FromSeconds(2));
                var debugState = adapter.GetDebugStateForTests();
                Assert.False(debugState.HasProcessReference);
                Assert.False(debugState.HasStdinReference);
                Assert.False(debugState.HasStdoutReaderTaskReference);
                Assert.False(debugState.HasStderrReaderTaskReference);
                Assert.Equal(0, debugState.TrackedPid);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "BridgeStabilityPromotion")]
    [Fact]
    public async Task Bridge_RapidStartDisposeCycles200_Promotion_NoOrphansOrHandleRefs()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-rapid-cycles-200", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var bridgePath = Path.Combine(tempDir, "mock-bridge-rapid-cycles-200.js");
        WriteBridgeScriptWithManifest(bridgePath, BuildMockBridgeScript(delayPongMs: 0, respondToPing: true, respondToShutdown: true));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            for (var i = 0; i < 200; i++)
            {
                var options = NknTransportOptions.Load();
                var identity = new NknIdentity("mock-cycle200-" + i, "mock-cycle200.fake");
                using var adapter = new RealNknClientAdapter(identity, options);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await adapter.StartBridgeAsync(cts.Token);
                Assert.True(adapter.IsBridgeProcessRunning);
                await adapter.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
                await WaitUntilAsync(() => !adapter.IsBridgeProcessRunning, TimeSpan.FromSeconds(2));
                var debugState = adapter.GetDebugStateForTests();
                Assert.False(debugState.HasProcessReference);
                Assert.False(debugState.HasStdinReference);
                Assert.False(debugState.HasStdoutReaderTaskReference);
                Assert.False(debugState.HasStderrReaderTaskReference);
                Assert.Equal(0, debugState.TrackedPid);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Bridge_TrackedPidCleanup_KillsOrphanNodeProcess_ByPidAndStartTime()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var nodePath = Path.Combine(bundleDir, "node.exe");
        if (!File.Exists(nodePath))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-mock-bridge-orphan", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var scriptPath = Path.Combine(tempDir, "idle-node.js");
        File.WriteAllText(scriptPath, "setInterval(() => {}, 1000);");
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = nodePath,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        proc.StartInfo.ArgumentList.Add(scriptPath);
        Assert.True(proc.Start());
        var pid = proc.Id;
        var startTimeUtcFileTime = proc.StartTime.ToUniversalTime().ToFileTimeUtc();
        try
        {
            Assert.True(RealNknClientAdapter.TryCleanupTrackedNodeProcessForTests(pid, startTimeUtcFileTime));
            await WaitUntilAsync(() =>
            {
                try
                {
                    return proc.HasExited;
                }
                catch
                {
                    return true;
                }
            }, TimeSpan.FromSeconds(3));
        }
        finally
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            // best effort
            }

            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    private static void WriteBridgeScriptWithManifest(string bridgePath, string script)
    {
        File.WriteAllText(bridgePath, script);
        var bridgeScriptSha256 = ComputeSha256Hex(bridgePath);
        var manifestPath = Path.Combine(Path.GetDirectoryName(bridgePath)!, "bridge-manifest.json");
        File.WriteAllText(
            manifestPath,
            $$"""
            {
              "manifestVersion": 1,
              "appVersion": "0.5.4-test",
              "buildTimestampUtc": "2026-04-13T00:00:00.0000000Z",
              "bridgeScriptSha256": "{{bridgeScriptSha256}}",
              "nodeVersion": "v24.13.1",
              "capabilities": {
                "ownerPidWatchdog": true,
                "killOnCloseJob": true
              }
            }
            """);
    }

    private static string ComputeSha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string BuildReceiveStallRecoveryMockBridgeScript(
        string countFile,
        int stallConnectCount = 1,
        int controlMessagesReceivedSinceLast = 0,
        int bulkMessagesReceivedSinceLast = 0,
        int totalMessagesReceivedSinceLast = 0,
        int controlLastReceivedAgeMs = 9000,
        int bulkLastReceivedAgeMs = 9000,
        int? postRecoveryControlMessagesReceivedSinceLast = null,
        int? postRecoveryBulkMessagesReceivedSinceLast = null,
        int? postRecoveryTotalMessagesReceivedSinceLast = null,
        int? postRecoveryControlLastReceivedAgeMs = null,
        int? postRecoveryBulkLastReceivedAgeMs = null,
        int stallHealthSampleCount = 3,
        int stallHealthSampleSpacingMs = 50,
        bool rampStallBulkAgeForAllChannelRecovery = false,
        string connectKey = "mock-key")
    {
        var normalizedStallHealthSampleSpacingMs = Math.Max(1, stallHealthSampleSpacingMs);
        var stallHealthBlock = rampStallBulkAgeForAllChannelRecovery
            ? $@"
      const stallBulkAges = [3000, 5000, 7000, 9000];
      for (let i = 0; i < {Math.Max(1, stallHealthSampleCount)}; i++) {{
        const bulkAgeMs = stallBulkAges[Math.min(i, stallBulkAges.length - 1)];
        emitHealth(
          60 + (i * {normalizedStallHealthSampleSpacingMs}),
          {controlMessagesReceivedSinceLast},
          {bulkMessagesReceivedSinceLast},
          {totalMessagesReceivedSinceLast},
          {controlLastReceivedAgeMs},
          bulkAgeMs);
      }}"
            : $@"
      for (let i = 0; i < {Math.Max(1, stallHealthSampleCount)}; i++) {{
        emitHealth(60 + (i * {normalizedStallHealthSampleSpacingMs}));
      }}";
        var postRecoveryHealthBlock =
            postRecoveryControlMessagesReceivedSinceLast is null ||
            postRecoveryBulkMessagesReceivedSinceLast is null ||
            postRecoveryTotalMessagesReceivedSinceLast is null ||
            postRecoveryControlLastReceivedAgeMs is null ||
            postRecoveryBulkLastReceivedAgeMs is null
                ? string.Empty
                : $@"
    else {{
      emitHealth(
        60,
        {postRecoveryControlMessagesReceivedSinceLast.Value},
        {postRecoveryBulkMessagesReceivedSinceLast.Value},
        {postRecoveryTotalMessagesReceivedSinceLast.Value},
        {postRecoveryControlLastReceivedAgeMs.Value},
        {postRecoveryBulkLastReceivedAgeMs.Value});
    }}";
        return
$@"'use strict';
const fs = require('fs');
const readline = require('readline');
const rl = readline.createInterface({{ input: process.stdin, crlfDelay: Infinity, terminal: false }});
let connectCount = 0;
try {{
  if (fs.existsSync({JsonSerializer.Serialize(countFile)})) {{
    connectCount = Number.parseInt(fs.readFileSync({JsonSerializer.Serialize(countFile)}, 'utf8').trim(), 10) || 0;
  }}
}} catch (e) {{
  connectCount = 0;
}}
function emit(obj) {{ process.stdout.write(JSON.stringify(obj) + '\n'); }}
function emitHealth(
  delayMs,
  controlMessagesReceivedSinceLast = {controlMessagesReceivedSinceLast},
  bulkMessagesReceivedSinceLast = {bulkMessagesReceivedSinceLast},
  totalMessagesReceivedSinceLast = {totalMessagesReceivedSinceLast},
  controlLastReceivedAgeMs = {controlLastReceivedAgeMs},
  bulkLastReceivedAgeMs = {bulkLastReceivedAgeMs}) {{
  setTimeout(() => emit({{
    event: 'bridge_transport_health_summary',
    selected_rpc: '(none)',
    selected_rpc_key: '(none)',
    selected_rpc_stage: 'none',
    connect_id: 'mock-connect',
    connect_key: {JsonSerializer.Serialize(connectKey)},
    ready_emitted: 1,
    client_ready_age_ms: 10000,
    disconnect_count_since_last: 0,
    connect_failed_count_since_last: 0,
    ws_error_count_since_last: 0,
    rpc_fallback_attempt_count_since_last: 0,
    control_ready: 1,
    media_ready: 1,
    bulk_ready: 1,
    frames_sent_since_last: 1,
    latest_disconnect_reason: '(none)',
    sample_window_ms: 2000,
    control_subclients: 4,
    media_subclients: 8,
    bulk_subclients: 4,
    bulk_send_concurrency: 4,
    control_messages_received_since_last: controlMessagesReceivedSinceLast,
    media_messages_received_since_last: 0,
    bulk_messages_received_since_last: bulkMessagesReceivedSinceLast,
    total_messages_received_since_last: totalMessagesReceivedSinceLast,
    control_bytes_received_since_last: 0,
    media_bytes_received_since_last: 0,
    bulk_bytes_received_since_last: 0,
    total_bytes_received_since_last: 0,
    control_last_received_age_ms: controlLastReceivedAgeMs,
    media_last_received_age_ms: 9000,
    bulk_last_received_age_ms: bulkLastReceivedAgeMs
  }}), delayMs);
}}
rl.on('line', (line) => {{
  if (!line || !line.trim()) return;
  let msg;
  try {{ msg = JSON.parse(line); }} catch (e) {{ emit({{ event:'error', id:null, cmd:null, reason:'Invalid JSON' }}); return; }}
  if (msg.cmd === 'hello') {{
    emit({{ event:'hello_ok', id: msg.id ?? null, protocol: 2, sdk: 'mock-sdk@1.0.0' }});
    return;
  }}
  if ((msg.type === 'ping') || (msg.cmd === 'ping')) {{
    emit({{ type:'pong', id: msg.id ?? null, ts: Date.now() }});
    return;
  }}
  if (msg.cmd === 'connect') {{
    connectCount++;
    fs.writeFileSync({JsonSerializer.Serialize(countFile)}, String(connectCount));
    emit({{ event:'ok', id: msg.id ?? null, cmd:'connect' }});
    setTimeout(() => emit({{ event:'ready', protocol:2, channels:['control','media','bulk'], address:'receive-stall.addr', controlAddress:'receive-stall.addr', mediaAddress:'receive-stall-media.addr', bulkAddress:'receive-stall-bulk.addr', connectId: msg.connectId ?? null }}), 10);
    if (connectCount <= {stallConnectCount}) {{
{stallHealthBlock}
    }}
    {postRecoveryHealthBlock}
    return;
  }}
  if (msg.cmd === 'shutdown') {{
    emit({{ event:'ok', id: msg.id ?? null, cmd:'shutdown' }});
    emit({{ event:'disconnected', reason:'shutdown' }});
    setTimeout(() => process.exit(0), 10);
    return;
  }}
  emit({{ event:'ok', id: msg.id ?? null, cmd: msg.cmd ?? msg.type ?? null }});
}});
";
    }

    private static string BuildReceiveStallRecoverySoftFailureThenHardRestartMockBridgeScript(string countFile)
    {
        var serializedCountFile = JsonSerializer.Serialize(countFile);
        return
$@"'use strict';
const fs = require('fs');
const readline = require('readline');
const rl = readline.createInterface({{ input: process.stdin, crlfDelay: Infinity, terminal: false }});
function emit(obj) {{ process.stdout.write(JSON.stringify(obj) + '\n'); }}
function readConnectCount() {{
  try {{
    if (fs.existsSync({serializedCountFile})) {{
      return Number.parseInt(fs.readFileSync({serializedCountFile}, 'utf8').trim(), 10) || 0;
    }}
  }} catch (e) {{}}
  return 0;
}}
rl.on('line', (line) => {{
  if (!line || !line.trim()) return;
  let msg;
  try {{ msg = JSON.parse(line); }} catch (e) {{ emit({{ event:'error', id:null, cmd:null, reason:'Invalid JSON' }}); return; }}
  if (msg.cmd === 'hello') {{
    emit({{ event:'hello_ok', id: msg.id ?? null, protocol: 2, sdk: 'mock-sdk@1.0.0' }});
    return;
  }}
  if ((msg.type === 'ping') || (msg.cmd === 'ping')) {{
    emit({{ type:'pong', id: msg.id ?? null, ts: Date.now() }});
    return;
  }}
  if (msg.cmd === 'connect') {{
    const connectCount = readConnectCount() + 1;
    fs.writeFileSync({serializedCountFile}, String(connectCount));
    emit({{ event:'ok', id: msg.id ?? null, cmd:'connect' }});
    if (connectCount === 2) {{
      emit({{ event:'rpc_selected', rpc:'https://mock-rpc-soft-failure.example:30003', connectId: msg.connectId ?? null, ts: Date.now() }});
      return;
    }}
    setTimeout(() => emit({{
      event:'ready',
      protocol:2,
      channels:['control','media','bulk'],
      address:'post-tuna-soft-fallback-' + connectCount + '.addr',
      controlAddress:'post-tuna-soft-fallback-' + connectCount + '.addr',
      mediaAddress:'post-tuna-soft-fallback-' + connectCount + '-media.addr',
      bulkAddress:'post-tuna-soft-fallback-' + connectCount + '-bulk.addr',
      connectId: msg.connectId ?? null
    }}), 20);
    return;
  }}
  if (msg.cmd === 'shutdown') {{
    emit({{ event:'ok', id: msg.id ?? null, cmd:'shutdown' }});
    emit({{ event:'disconnected', reason:'shutdown' }});
    setTimeout(() => process.exit(0), 10);
    return;
  }}
  emit({{ event:'ok', id: msg.id ?? null, cmd: msg.cmd ?? msg.type ?? null }});
}});
";
    }

    private static string BuildReceiveStallRecoveryTransientAllZeroMockBridgeScript(string countFile)
    {
        return
$@"'use strict';
const fs = require('fs');
const readline = require('readline');
const rl = readline.createInterface({{ input: process.stdin, crlfDelay: Infinity, terminal: false }});
let connectCount = 0;
try {{
  if (fs.existsSync({JsonSerializer.Serialize(countFile)})) {{
    connectCount = Number.parseInt(fs.readFileSync({JsonSerializer.Serialize(countFile)}, 'utf8').trim(), 10) || 0;
  }}
}} catch (e) {{
  connectCount = 0;
}}
function emit(obj) {{ process.stdout.write(JSON.stringify(obj) + '\n'); }}
function emitHealth(delayMs, bulkAgeMs) {{
  setTimeout(() => emit({{
    event: 'bridge_transport_health_summary',
    selected_rpc: '(none)',
    selected_rpc_key: '(none)',
    selected_rpc_stage: 'none',
    connect_id: 'mock-connect',
    connect_key: 'mock-key',
    ready_emitted: 1,
    client_ready_age_ms: 10000,
    disconnect_count_since_last: 0,
    connect_failed_count_since_last: 0,
    ws_error_count_since_last: 0,
    rpc_fallback_attempt_count_since_last: 0,
    control_ready: 1,
    media_ready: 1,
    bulk_ready: 1,
    frames_sent_since_last: 1,
    latest_disconnect_reason: '(none)',
    sample_window_ms: 2000,
    control_subclients: 4,
    media_subclients: 8,
    bulk_subclients: 4,
    bulk_send_concurrency: 4,
    control_messages_received_since_last: 0,
    media_messages_received_since_last: 0,
    bulk_messages_received_since_last: 0,
    total_messages_received_since_last: 0,
    control_bytes_received_since_last: 0,
    media_bytes_received_since_last: 0,
    bulk_bytes_received_since_last: 0,
    total_bytes_received_since_last: 0,
    control_last_received_age_ms: 9000,
    media_last_received_age_ms: 9000,
    bulk_last_received_age_ms: bulkAgeMs
  }}), delayMs);
}}
rl.on('line', (line) => {{
  if (!line || !line.trim()) return;
  let msg;
  try {{ msg = JSON.parse(line); }} catch (e) {{ emit({{ event:'error', id:null, cmd:null, reason:'Invalid JSON' }}); return; }}
  if (msg.cmd === 'hello') {{
    emit({{ event:'hello_ok', id: msg.id ?? null, protocol: 2, sdk: 'mock-sdk@1.0.0' }});
    return;
  }}
  if ((msg.type === 'ping') || (msg.cmd === 'ping')) {{
    emit({{ type:'pong', id: msg.id ?? null, ts: Date.now() }});
    return;
  }}
  if (msg.cmd === 'connect') {{
    connectCount++;
    fs.writeFileSync({JsonSerializer.Serialize(countFile)}, String(connectCount));
    emit({{ event:'ok', id: msg.id ?? null, cmd:'connect' }});
    setTimeout(() => emit({{ event:'ready', protocol:2, channels:['control','media','bulk'], address:'all-zero-probe-window.addr', controlAddress:'all-zero-probe-window.addr', mediaAddress:'all-zero-probe-window-media.addr', bulkAddress:'all-zero-probe-window-bulk.addr', connectId: msg.connectId ?? null }}), 10);
    if (connectCount === 1) {{
      emitHealth(60, 3000);
      emitHealth(110, 5000);
      emitHealth(160, 7000);
    }}
    return;
  }}
  if (msg.cmd === 'shutdown') {{
    emit({{ event:'ok', id: msg.id ?? null, cmd:'shutdown' }});
    emit({{ event:'disconnected', reason:'shutdown' }});
    setTimeout(() => process.exit(0), 10);
    return;
  }}
  emit({{ event:'ok', id: msg.id ?? null, cmd: msg.cmd ?? msg.type ?? null }});
}});
";
    }

    private static string BuildActiveFileTransferCooldownRecoveryMockBridgeScript(string countFile)
    {
        return
$@"'use strict';
const fs = require('fs');
const readline = require('readline');
const rl = readline.createInterface({{ input: process.stdin, crlfDelay: Infinity, terminal: false }});
let connectCount = 0;
try {{
  if (fs.existsSync({JsonSerializer.Serialize(countFile)})) {{
    connectCount = Number.parseInt(fs.readFileSync({JsonSerializer.Serialize(countFile)}, 'utf8').trim(), 10) || 0;
  }}
}} catch (e) {{
  connectCount = 0;
}}
function emit(obj) {{ process.stdout.write(JSON.stringify(obj) + '\n'); }}
function emitHealth(delayMs, controlMessages, bulkMessages, totalMessages, controlAgeMs, bulkAgeMs) {{
  setTimeout(() => emit({{
    event: 'bridge_transport_health_summary',
    selected_rpc: '(none)',
    selected_rpc_key: '(none)',
    selected_rpc_stage: 'none',
    connect_id: 'mock-connect',
    connect_key: 'mock-key-' + connectCount,
    ready_emitted: 1,
    client_ready_age_ms: 10000,
    disconnect_count_since_last: 0,
    connect_failed_count_since_last: 0,
    ws_error_count_since_last: 0,
    rpc_fallback_attempt_count_since_last: 0,
    control_ready: 1,
    media_ready: 1,
    bulk_ready: 1,
    frames_sent_since_last: 1,
    latest_disconnect_reason: '(none)',
    sample_window_ms: 2000,
    control_subclients: 4,
    media_subclients: 8,
    bulk_subclients: 4,
    bulk_send_concurrency: 4,
    control_messages_received_since_last: controlMessages,
    media_messages_received_since_last: 0,
    bulk_messages_received_since_last: bulkMessages,
    total_messages_received_since_last: totalMessages,
    control_bytes_received_since_last: controlMessages > 0 ? 1 : 0,
    media_bytes_received_since_last: 0,
    bulk_bytes_received_since_last: bulkMessages > 0 ? 1 : 0,
    total_bytes_received_since_last: totalMessages > 0 ? 1 : 0,
    control_last_received_age_ms: controlAgeMs,
    media_last_received_age_ms: 9000,
    bulk_last_received_age_ms: bulkAgeMs
  }}), delayMs);
}}
function scheduleHealthForConnect() {{
  if (connectCount <= 4) {{
    emitHealth(60, 0, 0, 0, 9000, 9000);
    emitHealth(110, 0, 0, 0, 9000, 9000);
    emitHealth(160, 0, 0, 0, 9000, 9000);
    return;
  }}

  if (connectCount === 5) {{
    emitHealth(60, 1, 1, 2, 100, 100);
    emitHealth(3100, 0, 0, 0, 9000, 9000);
    emitHealth(3200, 0, 0, 0, 9000, 9000);
    emitHealth(3300, 0, 0, 0, 9000, 9000);
    return;
  }}

  if (connectCount === 6) {{
    emitHealth(3100, 0, 1, 1, 9000, 100);
    emitHealth(3200, 0, 1, 1, 9000, 100);
  }}
}}
rl.on('line', (line) => {{
  if (!line || !line.trim()) return;
  let msg;
  try {{ msg = JSON.parse(line); }} catch (e) {{ emit({{ event:'error', id:null, cmd:null, reason:'Invalid JSON' }}); return; }}
  if (msg.cmd === 'hello') {{
    emit({{ event:'hello_ok', id: msg.id ?? null, protocol: 2, sdk: 'mock-sdk@1.0.0' }});
    return;
  }}
  if ((msg.type === 'ping') || (msg.cmd === 'ping')) {{
    emit({{ type:'pong', id: msg.id ?? null, ts: Date.now() }});
    return;
  }}
  if (msg.cmd === 'connect') {{
    connectCount++;
    fs.writeFileSync({JsonSerializer.Serialize(countFile)}, String(connectCount));
    emit({{ event:'ok', id: msg.id ?? null, cmd:'connect' }});
    setTimeout(() => emit({{ event:'ready', protocol:2, channels:['control','media','bulk'], address:'active-filetransfer-cooldown.addr', controlAddress:'active-filetransfer-cooldown.addr', mediaAddress:'active-filetransfer-cooldown-media.addr', bulkAddress:'active-filetransfer-cooldown-bulk.addr', connectId: msg.connectId ?? null }}), 10);
    scheduleHealthForConnect();
    return;
  }}
  if (msg.cmd === 'shutdown') {{
    emit({{ event:'ok', id: msg.id ?? null, cmd:'shutdown' }});
    emit({{ event:'disconnected', reason:'shutdown' }});
    setTimeout(() => process.exit(0), 10);
    return;
  }}
  emit({{ event:'ok', id: msg.id ?? null, cmd: msg.cmd ?? msg.type ?? null }});
}});
";
    }

    private static long TryGetMaxJsonLong(IEnumerable<string> lines, string eventName, string propertyName)
    {
        long max = 0;
        foreach (var line in lines)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("event", out var eventProperty) ||
                    !string.Equals(eventProperty.GetString(), eventName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (root.TryGetProperty(propertyName, out var property) &&
                    property.ValueKind == JsonValueKind.Number &&
                    property.TryGetInt64(out var value))
                {
                    max = Math.Max(max, value);
                }
            }
            catch (JsonException)
            {
            }
        }

        return max;
    }

    private static string GetRecentLogTextSince(int baselineLength)
    {
        var text = LocalOperationalLog.GetRecentLogText();
        if (baselineLength <= 0)
        {
            return text;
        }

        return text.Length >= baselineLength
            ? text[baselineLength..]
            : text;
    }

}
