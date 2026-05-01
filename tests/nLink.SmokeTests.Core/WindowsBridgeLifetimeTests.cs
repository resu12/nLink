using System.Diagnostics;
using System.Security.Cryptography;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class WindowsBridgeLifetimeTests
{
    [Fact]
    public void BridgeBundleManifest_LoadFromBuiltBundle_MatchesScriptHashAndCapabilities()
    {
        var bundleDir = TryFindBridgeBundleDirectory();
        if (bundleDir is null)
        {
            return;
        }

        var bridgePath = Path.Combine(bundleDir, "index.js");
        var manifestPath = Path.Combine(bundleDir, "bridge-manifest.json");
        if (!File.Exists(bridgePath) || !File.Exists(manifestPath))
        {
            return;
        }

        var identity = BridgeBundleIdentity.Load(bridgePath);

        Assert.False(identity.HasMismatch, $"Expected built bridge bundle manifest to match script, but status was '{identity.ManifestStatus}'.");
        Assert.True(identity.OwnerPidWatchdog);
        Assert.True(identity.KillOnCloseJob);
        Assert.Equal(Path.GetFullPath(bridgePath), identity.BridgeScriptPath);
        Assert.Equal(Path.GetFullPath(manifestPath), identity.ManifestPath);
    }

    [Fact]
    public void BridgeBundleManifest_Load_DetectsScriptHashMismatch()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-bridge-manifest-mismatch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var scriptPath = Path.Combine(tempDir, "index.js");
        var manifestPath = Path.Combine(tempDir, "bridge-manifest.json");
        File.WriteAllText(scriptPath, "console.log('bridge');");
        File.WriteAllText(
            manifestPath,
            """
            {
              "manifestVersion": 1,
              "appVersion": "0.5.4",
              "buildTimestampUtc": "2026-04-13T00:00:00.0000000Z",
              "bridgeScriptSha256": "deadbeef",
              "nodeVersion": "v24.13.1",
              "capabilities": {
                "ownerPidWatchdog": true,
                "killOnCloseJob": true
              }
            }
            """);

        try
        {
            var identity = BridgeBundleIdentity.Load(scriptPath);
            Assert.True(identity.HasMismatch);
            Assert.Equal("script_hash_mismatch", identity.ManifestStatus);
            Assert.Equal("bridge_script_sha256_mismatch", identity.ManifestReason);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
                // Best-effort test cleanup.
            }
        }
    }

    [Theory]
    [InlineData("ok", false)]
    [InlineData("manifest_missing", true)]
    [InlineData("manifest_invalid", true)]
    [InlineData("manifest_malformed", true)]
    [InlineData("capability_mismatch", true)]
    [InlineData("script_hash_mismatch", true)]
    public void BridgeBundleStartupGuard_RejectsEveryNonOkManifestStatus(string manifestStatus, bool shouldReject)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-bridge-startup-guard", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var identity = CreateBridgeBundleIdentityForStatus(tempDir, manifestStatus);
            var logs = new List<string>();
            var failures = new List<(string Code, string? Hint)>();

            if (shouldReject)
            {
                var ex = Assert.Throws<InvalidOperationException>(() =>
                    BridgeBundleStartupGuard.EnsureTrustedForStartup(
                        identity,
                        logs.Add,
                        (code, hint) => failures.Add((code, hint))));

                Assert.Contains(manifestStatus, ex.Message, StringComparison.Ordinal);
                Assert.Contains(logs, entry => entry.Contains("event=bridge_bundle_start_blocked", StringComparison.Ordinal));
                var failure = Assert.Single(failures);
                Assert.Equal($"{BridgeBundleStartupGuard.IntegrityFailureCodePrefix}:{manifestStatus}", failure.Code);
                Assert.Contains(failure.Code, NknRuntimeDiagnostics.Snapshot().LastError, StringComparison.Ordinal);
            }
            else
            {
                BridgeBundleStartupGuard.EnsureTrustedForStartup(
                    identity,
                    logs.Add,
                    (code, hint) => failures.Add((code, hint)));

                Assert.Empty(logs);
                Assert.Empty(failures);
            }
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task RealNknClientAdapter_StartBridgeAsync_BlocksMismatchedBundleBeforeLaunch()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-bridge-start-blocked", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var scriptPath = Path.Combine(tempDir, "index.js");
        File.WriteAllText(scriptPath, "console.log('tampered bridge');");
        WriteBridgeManifest(scriptPath, bridgeScriptSha256: "deadbeef");

        var previousBridgePath = Environment.GetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", scriptPath);
            NknRuntimeDiagnostics.SetLastError("(none)");

            using var adapter = new RealNknClientAdapter(
                new NknIdentity("blocked-mismatch", "blocked-mismatch.fake"),
                NknTransportOptions.Load());

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                adapter.StartBridgeAsync(CancellationToken.None));

            Assert.Contains("integrity verification", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(adapter.IsBridgeProcessRunning);
            var snapshot = NknRuntimeDiagnostics.Snapshot();
            Assert.Contains("NKN_START_FAILED", snapshot.LastError, StringComparison.Ordinal);
            Assert.Contains("bridge_bundle_integrity_failed:script_hash_mismatch", snapshot.LastError, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_BRIDGE_PATH", previousBridgePath);
            TryDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task WindowsKillOnCloseProcessJob_Dispose_KillsAssignedProcess()
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

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-bridge-job-kill", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var scriptPath = Path.Combine(tempDir, "idle-node.js");
        File.WriteAllText(scriptPath, "setInterval(() => {}, 1000);");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = nodePath,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add(scriptPath);
        Assert.True(process.Start());

        try
        {
            using (var guard = WindowsKillOnCloseProcessJob.TryAttach(process, _ => { }))
            {
                Assert.NotNull(guard);
            }

            await WaitUntilAsync(() =>
            {
                try { return process.HasExited; } catch { return true; }
            }, TimeSpan.FromSeconds(3));
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
                // Best-effort test cleanup.
            }

            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
                // Best-effort test cleanup.
            }
        }
    }

    [Fact]
    public async Task BridgeSupervisor_RequestShutdownAndCleanupAsync_ForceKillsHungBridgeProcess()
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

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-bridge-force-kill", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var scriptPath = Path.Combine(tempDir, "hung-bridge.js");
        File.WriteAllText(scriptPath, "setInterval(() => {}, 1000);");

        var logs = new List<string>();
        var supervisor = new BridgeSupervisor(
            callbacks: new BridgeSupervisorCallbacks
            {
                Log = logs.Add,
                SignalDisconnected = _ => { },
                OnUnexpectedExitDetected = _ => { },
                RecordBridgeFailure = (_, _) => { },
                EmitBridgeLifecycle = _ => { },
                GetBridgeBundleIdentity = () => null,
            },
            resolveNodePath: () => nodePath,
            resolveBridgePath: () => scriptPath,
            onStdoutJsonLineAsync: (_, _) => Task.CompletedTask,
            onStdoutBinaryFrameAsync: (_, _) => Task.CompletedTask,
            onStderrLineAsync: (_, _, _, _) => Task.CompletedTask,
            getCleanupReasonPrefix: () => "test",
            isDisposed: () => false,
            isShuttingDown: () => true,
            getReliabilityModeHint: () => "Helper",
            getCurrentUptimeMs: () => null);

        try
        {
            await supervisor.EnsureStartedAsync(CancellationToken.None);
            var pid = supervisor.CurrentPid;
            Assert.NotNull(pid);

            await supervisor.RequestShutdownAndCleanupAsync(
                sendShutdownAsync: _ => Task.CompletedTask,
                CancellationToken.None,
                shutdownReason: "test_hung_shutdown");

            await WaitUntilAsync(() => pid is null || !IsProcessAlive(pid.Value), TimeSpan.FromSeconds(5));
            Assert.Contains(logs, entry => entry.Contains("event=bridge_shutdown_force_kill", StringComparison.Ordinal));
            Assert.Contains(logs, entry => entry.Contains("event=bridge_shutdown_completed", StringComparison.Ordinal));
        }
        finally
        {
            supervisor.CleanupState();
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
                // Best-effort test cleanup.
            }
        }
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

            await Task.Delay(50);
        }

        Assert.True(condition(), "Timed out waiting for condition.");
    }

    private static string? TryFindBridgeBundleDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && current is not null; i++, current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "artifacts", "bridge", "win-x64");
            if (Directory.Exists(candidate) &&
                File.Exists(Path.Combine(candidate, "index.js")) &&
                File.Exists(Path.Combine(candidate, "node.exe")))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static BridgeBundleIdentity CreateBridgeBundleIdentityForStatus(string tempDir, string manifestStatus)
    {
        var scriptPath = Path.Combine(tempDir, "index-" + manifestStatus + ".js");
        File.WriteAllText(scriptPath, "console.log('bridge " + manifestStatus + "');");

        switch (manifestStatus)
        {
            case "ok":
                WriteBridgeManifest(scriptPath);
                break;
            case "manifest_missing":
                break;
            case "manifest_invalid":
                File.WriteAllText(Path.Combine(tempDir, "bridge-manifest.json"), """{"manifestVersion":1}""");
                break;
            case "manifest_malformed":
                File.WriteAllText(Path.Combine(tempDir, "bridge-manifest.json"), "{ malformed");
                break;
            case "capability_mismatch":
                WriteBridgeManifest(scriptPath, ownerPidWatchdog: true, killOnCloseJob: false);
                break;
            case "script_hash_mismatch":
                WriteBridgeManifest(scriptPath, bridgeScriptSha256: "deadbeef");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(manifestStatus), manifestStatus, "Unknown manifest status.");
        }

        var identity = BridgeBundleIdentity.Load(scriptPath);
        Assert.Equal(manifestStatus, identity.ManifestStatus);
        return identity;
    }

    private static void WriteBridgeManifest(
        string scriptPath,
        string? bridgeScriptSha256 = null,
        bool ownerPidWatchdog = true,
        bool killOnCloseJob = true)
    {
        bridgeScriptSha256 ??= ComputeSha256Hex(scriptPath);
        var manifestPath = Path.Combine(Path.GetDirectoryName(scriptPath)!, "bridge-manifest.json");
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
                "ownerPidWatchdog": {{FormatJsonBool(ownerPidWatchdog)}},
                "killOnCloseJob": {{FormatJsonBool(killOnCloseJob)}}
              }
            }
            """);
    }

    private static string ComputeSha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string FormatJsonBool(bool value) => value ? "true" : "false";

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }
}
