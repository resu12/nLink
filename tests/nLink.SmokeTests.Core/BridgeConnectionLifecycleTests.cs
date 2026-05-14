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
        process.StartInfo.Environment["NLINK_BRIDGE_FAKE_BULK_SEND_CLIENT_NOT_READY_COUNT"] = "1";
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
                TimeSpan.FromSeconds(4));
            Assert.Equal(0, TryGetMaxJsonLong(stdoutLines, "bridge_bulk_send_summary", "send_failures"));
            Assert.Contains(stderrLines, line => line.Contains("Bulk queue transient send retry scheduled", StringComparison.Ordinal));

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
    public void Bridge_Source_DefaultBulkSendMode_IsRoundRobin()
    {
        var bridgePath = FindFileUpwards(Path.Combine("tools", "nkn-bridge", "index.js"));
        Assert.True(bridgePath is not null && File.Exists(bridgePath), "Bridge script not found.");

        var bridgeScript = File.ReadAllText(bridgePath);
        Assert.Contains("const DEFAULT_BULK_SEND_MODE = 'round_robin';", bridgeScript, StringComparison.Ordinal);
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
            var defaults = LoadNknOptionsWithOverrides(Path.Combine(tempDir, "default.json"), "topology-default");
            Assert.Equal(4, defaults.NumSubClients);
            Assert.Equal(8, defaults.MediaNumSubClients);
            Assert.Equal(4, defaults.BulkNumSubClients);
            Assert.Equal(4, defaults.BulkSendConcurrency);
            Assert.True(defaults.ReceiveStallRecoveryEnabled);
            Assert.True(defaults.ReceiveStallFileTransferFastRecoveryEnabled);
            Assert.False(defaults.ReceiveStallControlOnlyRecoveryEnabled);
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
            var clamped = LoadNknOptionsWithOverrides(Path.Combine(tempDir, "clamped.json"), "topology-clamped");
            Assert.Equal(1, clamped.NumSubClients);
            Assert.Equal(16, clamped.MediaNumSubClients);
            Assert.Equal(16, clamped.BulkNumSubClients);
            Assert.Equal(8, clamped.BulkSendConcurrency);
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
    public async Task Bridge_ReceiveStallRecovery_ReconnectsAfterFileTransferBulkReceiveStallWhenControlStale()
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
                () => File.Exists(countFile) && int.TryParse(File.ReadAllText(countFile).Trim(), out var count) && count >= 2,
                TimeSpan.FromSeconds(5));

            Assert.Contains("reason=bulk_receive_stalled", LocalOperationalLog.GetRecentLogText(), StringComparison.Ordinal);

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
    public async Task Bridge_ReceiveStallRecovery_RecoversDuringActiveFileTransferRuntimeWhenFeedbackStalls()
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
                    var reconnected =
                        File.Exists(countFile) &&
                        int.TryParse(File.ReadAllText(countFile).Trim(), out var currentCount) &&
                        currentCount >= 2;
                    return reconnected &&
                           text.Contains("event=nkn_bridge_receive_stall_detected", StringComparison.Ordinal) &&
                           text.Contains("event=nkn_bridge_receive_stall_recovery_started", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(5));

            Assert.True(File.Exists(countFile));
            Assert.True(int.TryParse(File.ReadAllText(countFile).Trim(), out var count));
            Assert.True(count >= 2, $"Expected bridge recovery reconnect while V6 runtime is active; observed {count} connect(s).");

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
    public async Task Bridge_ReceiveStallRecovery_AcceptsBulkProofForActiveFileTransferAfterReconnect()
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
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await adapter.ConnectAsync(cts.Token);

            await WaitUntilAsync(
                () =>
                {
                    var text = LocalOperationalLog.GetRecentLogText();
                    return text.Contains("event=nkn_bridge_receive_stall_recovery_filetransfer_bulk_proof_accepted", StringComparison.Ordinal) &&
                           text.Contains("event=nkn_bridge_receive_stall_recovery_receive_resumed", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(5));

            var logText = LocalOperationalLog.GetRecentLogText();
            Assert.Contains("requires_control_proof=1", logText, StringComparison.Ordinal);
            Assert.Contains("bulk_messages_received_since_last=12", logText, StringComparison.Ordinal);

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

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_FILETRANSFER_FAST_RECOVERY", null);
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
        var disconnectedCount = 0;

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_NODE_PATH", nodePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", bridgePath);
            Environment.SetEnvironmentVariable("NLINK_NKN_RECEIVE_STALL_RECOVERY", null);
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
            Environment.SetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY", null);
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
    public async Task Bridge_ReceiveStallRecovery_ControlOnlyOverrideReconnectsEvenWhenBulkReceiveActive()
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
                () => File.Exists(countFile) && int.TryParse(File.ReadAllText(countFile).Trim(), out var count) && count >= 2,
                TimeSpan.FromSeconds(5));

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
    public async Task Bridge_ReceiveStallRecovery_ForcesControlOnlyReconnectWhenFileTransferControlStaleBeyondGrace()
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
            Environment.SetEnvironmentVariable("NLINK_NKN_CONTROL_ONLY_STALL_RECOVERY", null);
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
                () => File.Exists(countFile) && int.TryParse(File.ReadAllText(countFile).Trim(), out var count) && count >= 2,
                TimeSpan.FromSeconds(15));

            var logText = LocalOperationalLog.GetRecentLogText();
            Assert.Contains("event=nkn_bridge_control_receive_recovery_forced", logText, StringComparison.Ordinal);
            Assert.Contains("reason=filetransfer_control_stale_beyond_grace", logText, StringComparison.Ordinal);

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
    public async Task Bridge_ReceiveStallRecovery_ActiveFileTransferWaitsForProofBeforeRetryingAfterBaseBudget()
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
            var logBaseline = LocalOperationalLog.GetRecentLogText().Length;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await adapter.ConnectAsync(cts.Token);

            await WaitUntilAsync(
                () => File.Exists(countFile) &&
                      int.TryParse(File.ReadAllText(countFile).Trim(), out var count) &&
                      count >= 2 &&
                      GetRecentLogTextSince(logBaseline).Contains("event=nkn_bridge_receive_stall_recovery_failed; reason=previous_recovery_unproven_cooldown", StringComparison.Ordinal),
                TimeSpan.FromSeconds(14));

            var logText = GetRecentLogTextSince(logBaseline);
            Assert.Contains("event=nkn_bridge_receive_stall_recovery_failed; reason=previous_recovery_unproven_cooldown", logText, StringComparison.Ordinal);
            var connectCount = int.Parse(File.ReadAllText(countFile).Trim());
            Assert.InRange(connectCount, 2, 2);

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
        bool rampStallBulkAgeForAllChannelRecovery = false)
    {
        var stallHealthBlock = rampStallBulkAgeForAllChannelRecovery
            ? $@"
      const stallBulkAges = [3000, 5000, 7000, 9000];
      for (let i = 0; i < {Math.Max(1, stallHealthSampleCount)}; i++) {{
        const bulkAgeMs = stallBulkAges[Math.min(i, stallBulkAges.length - 1)];
        emitHealth(
          60 + (i * 50),
          {controlMessagesReceivedSinceLast},
          {bulkMessagesReceivedSinceLast},
          {totalMessagesReceivedSinceLast},
          {controlLastReceivedAgeMs},
          bulkAgeMs);
      }}"
            : $@"
      for (let i = 0; i < {Math.Max(1, stallHealthSampleCount)}; i++) {{
        emitHealth(60 + (i * 50));
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
