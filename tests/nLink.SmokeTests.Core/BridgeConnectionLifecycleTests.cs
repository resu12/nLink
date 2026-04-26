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
public sealed class BridgeConnectionLifecycleTests : SessionRuntimeConnectionTestBase
{
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
        var bridgePath = FindFileUpwards(Path.Combine("tools", "nkn-bridge", "index.js")) ?? Path.Combine(bundleDir!, "index.js");
        Assert.True(File.Exists(nodePath), $"Bridge runtime not found. Expected bundled node at '{nodePath}'. Run installer/Build-BridgeBundle.ps1.");
        Assert.True(File.Exists(bridgePath), $"Bridge script not found. Expected workspace tools/nkn-bridge/index.js or bundled bridge script at '{bridgePath}'.");
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
        File.WriteAllText(bridgePath, BuildMockBridgeScript(delayPongMs: 250, respondToPing: true));
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
        File.WriteAllText(bridgePath, BuildMockBridgeScript(delayPongMs: 0, respondToPing: false));
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
        File.WriteAllText(bridgePath, BuildMockBridgeScriptWithCustomConnect(connectBehaviorJs: $@"
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
        File.WriteAllText(bridgePath, BuildMockBridgeScriptWithCustomConnect(connectBehaviorJs: $@"
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
        File.WriteAllText(bridgePath, BuildMockBridgeScriptWithCustomConnect(connectBehaviorJs: @"
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
        File.WriteAllText(bridgePath, BuildMockBridgeScriptWithCustomConnect(connectBehaviorJs: $@"
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
        File.WriteAllText(bridgePath, BuildMockBridgeScriptWithCustomConnect(connectBehaviorJs: @"
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

    [Fact]
    public void NknTransportOptions_ParsesSubClientTopologyOverrides()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-nkn-topology-options", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var prevNumSubClients = Environment.GetEnvironmentVariable("NLINK_NKN_NUM_SUBCLIENTS");
        var prevMediaNumSubClients = Environment.GetEnvironmentVariable("NLINK_NKN_MEDIA_NUM_SUBCLIENTS");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NUM_SUBCLIENTS", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_MEDIA_NUM_SUBCLIENTS", null);
            var defaults = LoadNknOptionsWithOverrides(Path.Combine(tempDir, "default.json"), "topology-default");
            Assert.Equal(4, defaults.NumSubClients);
            Assert.Equal(8, defaults.MediaNumSubClients);
            Assert.False(defaults.HasSubClientTopologyOverride);
            Assert.False(defaults.ShouldSendSubClientTopology);

            Environment.SetEnvironmentVariable("NLINK_NKN_NUM_SUBCLIENTS", "6");
            Environment.SetEnvironmentVariable("NLINK_NKN_MEDIA_NUM_SUBCLIENTS", null);
            var inheritedMedia = LoadNknOptionsWithOverrides(Path.Combine(tempDir, "inherited.json"), "topology-inherited");
            Assert.Equal(6, inheritedMedia.NumSubClients);
            Assert.Equal(6, inheritedMedia.MediaNumSubClients);
            Assert.True(inheritedMedia.HasSubClientTopologyOverride);

            Environment.SetEnvironmentVariable("NLINK_NKN_NUM_SUBCLIENTS", "0");
            Environment.SetEnvironmentVariable("NLINK_NKN_MEDIA_NUM_SUBCLIENTS", "99");
            var clamped = LoadNknOptionsWithOverrides(Path.Combine(tempDir, "clamped.json"), "topology-clamped");
            Assert.Equal(1, clamped.NumSubClients);
            Assert.Equal(16, clamped.MediaNumSubClients);
            Assert.True(clamped.HasSubClientTopologyOverride);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NUM_SUBCLIENTS", prevNumSubClients);
            Environment.SetEnvironmentVariable("NLINK_NKN_MEDIA_NUM_SUBCLIENTS", prevMediaNumSubClients);
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
        File.WriteAllText(bridgePath, BuildMockBridgeScriptWithCustomConnect(connectBehaviorJs: $@"
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
        File.WriteAllText(bridgePath, BuildMockBridgeScriptWithCustomConnect(connectBehaviorJs: $@"
    fs.writeFileSync({JsonSerializer.Serialize(payloadFile)}, JSON.stringify(msg));
    emit({{ event:'ok', id: msg.id ?? null, cmd:'connect' }});
    setTimeout(() => emit({{ event:'ready', protocol:2, channels:['control','media','bulk'], address:'payload-test.addr', controlAddress:'payload-test.addr', mediaAddress:'payload-test-media.addr', bulkAddress:'payload-test-bulk.addr', connectId: msg.connectId ?? null }}), 20);
    return;
    "));
        var prevNodePath = Environment.GetEnvironmentVariable("NLINK_NKN_NODE_PATH");
        var prevBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");
        var prevNumSubClients = Environment.GetEnvironmentVariable("NLINK_NKN_NUM_SUBCLIENTS");
        var prevMediaNumSubClients = Environment.GetEnvironmentVariable("NLINK_NKN_MEDIA_NUM_SUBCLIENTS");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_NUM_SUBCLIENTS", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_MEDIA_NUM_SUBCLIENTS", null);

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
            }

            Environment.SetEnvironmentVariable("NLINK_NKN_NUM_SUBCLIENTS", "6");
            Environment.SetEnvironmentVariable("NLINK_NKN_MEDIA_NUM_SUBCLIENTS", "8");
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
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_NUM_SUBCLIENTS", prevNumSubClients);
            Environment.SetEnvironmentVariable("NLINK_NKN_MEDIA_NUM_SUBCLIENTS", prevMediaNumSubClients);
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
        File.WriteAllText(bridgePath, BuildMockBridgeScriptWithCustomConnect(connectBehaviorJs: @"
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
        File.WriteAllText(bridgePath, BuildMockBridgeScriptWithCustomConnect(connectBehaviorJs: @"
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
        File.WriteAllText(bridgePath, BuildMockBridgeScript(delayPongMs: 0, respondToPing: true, respondToShutdown: false));
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
        File.WriteAllText(bridgePath, BuildMockBridgeScript(delayPongMs: 0, respondToPing: true, respondToShutdown: true));
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
        File.WriteAllText(bridgePath, BuildMockBridgeScript(delayPongMs: 0, respondToPing: true, respondToShutdown: false));
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
        File.WriteAllText(bridgePath, BuildMockBridgeScriptWithStderrSpam());
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
        File.WriteAllText(bridgePath, BuildMockBridgeScript(delayPongMs: 0, respondToPing: true, respondToShutdown: true));
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
        File.WriteAllText(bridgePath, BuildMockBridgeScript(delayPongMs: 0, respondToPing: true, respondToShutdown: true));
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

    [Trait("Category", "Manual")]
    [ManualBridgeFact]
    public async Task Bridge_ProcessKill_RestartsAndUpdatesDiagnostics()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bundleDir = TryFindBridgeBundleDirectory();
        Assert.True(bundleDir is not null, "Bridge runtime not found. Build artifacts/bridge/win-x64 first (run installer/Build-BridgeBundle.ps1).");
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
                return snap.BridgeRestartCount > before.BridgeRestartCount && snap.BridgePid > 0 && snap.BridgePid != before.BridgePid;
            }, TimeSpan.FromSeconds(10));
            var after = NknRuntimeDiagnostics.Snapshot();
            Assert.True(after.BridgeRestartCount > before.BridgeRestartCount, "Bridge restart count did not increment.");
            Assert.NotEqual(before.BridgePid, after.BridgePid);
            Assert.Equal("crash", after.BridgeLastExitReason);
            await adapter.DisconnectAsync();
            await WaitUntilAsync(() => !adapter.IsBridgeProcessRunning, TimeSpan.FromSeconds(3));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", prevNodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", prevBridgePath);
        }
    }

}
