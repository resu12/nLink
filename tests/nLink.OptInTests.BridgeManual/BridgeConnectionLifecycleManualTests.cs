using System.Diagnostics;
using NLink.App.Configuration;
using NLink.Core.Diagnostics;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

[Trait("Area", "BridgeManual")]
public sealed class BridgeConnectionLifecycleManualTests : CoreSmokeTestsBase
{
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
