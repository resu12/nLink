using System.Diagnostics;
using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierShutdownReadinessTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell", "service-shutdown-natural")]
    [InlineData("pwsh", "service-shutdown-natural")]
    [InlineData("powershell", "service-shutdown-delayed")]
    [InlineData("pwsh", "service-shutdown-delayed")]
    [InlineData("powershell", "service-shutdown-timeout")]
    [InlineData("pwsh", "service-shutdown-timeout")]
    public async Task NativeShutdownReadiness_WaitsWithoutFixtureDrain(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-shutdown-readiness-20260920", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierAppLifetime.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-app-lifetime=" + scenario, result.Stdout);
    }

    [Fact]
    public void ShutdownReadiness_EmptyQueueIsNotFinalSubmission()
    {
        var type = typeof(SessionFileTransferService).GetNestedType("CoordinatorSnapshotStreamEmitter", BindingFlags.NonPublic)!;
        var emitter = Activator.CreateInstance(type, nonPublic: true)!;
        var wait = type.GetMethod("WaitForFinalSubmission", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var clock = Stopwatch.StartNew();
        Assert.False((bool)wait.Invoke(emitter, [TimeSpan.FromMilliseconds(30)])!);
        Assert.InRange(clock.ElapsedMilliseconds, 20, 2000);
        Assert.False((bool)wait.Invoke(emitter, [TimeSpan.Zero])!);
    }

    [Theory]
    [InlineData("powershell", "deferred_write")]
    [InlineData("pwsh", "deferred_write")]
    [InlineData("powershell", "writer_failure")]
    [InlineData("pwsh", "writer_failure")]
    [InlineData("powershell", "catalog_cancel")]
    [InlineData("pwsh", "catalog_cancel")]
    public async Task NativeShutdownReadiness_RequiresExactPersistedReceipt(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-shutdown-readiness-20260920", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierNativeCatalogRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, typeof(FrontierShutdownReadinessTests).Assembly.Location,
            directory, scenario, "writer"], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-native-catalog=", result.Stdout);
    }
}
