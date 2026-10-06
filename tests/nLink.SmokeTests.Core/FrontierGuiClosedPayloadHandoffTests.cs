namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierGuiClosedPayloadHandoffTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task ProductionFinalizer_PreservesCompletionOnOptionalPayloadFailure(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierGuiClosedPayloadHandoff.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, "Contract"], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("gui-closed-payload-contract-ok", result.Stdout);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task ProductionCompletionBlock_OffersOriginalClosedPairBeforeParentCompletion(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "production-gui-closed-payload-handoff-20260930", host + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierAttemptDispatch.ps1"));
        script = script.Replace("$ErrorActionPreference='Stop';", "$env:NLINK_TEST_GUI_CLOSED_PAYLOAD_HANDOFF='1'; $ErrorActionPreference='Stop';", StringComparison.Ordinal);
        var result = await RunPowerShellScriptTextAsync(script,
            [root, directory, "writer-source-read-cross-process-gui-valid", "writer-valid"], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("attempt-payload-native-writers-checked=writer-valid", result.Stdout);
        var gui = await File.ReadAllTextAsync(Path.Combine(directory, "gui-smoke-stdout.log"));
        Assert.Contains("gui-original-closed-payload-handoff-ok", gui);
        Assert.DoesNotContain("attempt-role-writers-ok", gui);
        var received = Assert.Single(Directory.GetFiles(Path.Combine(directory, "native-received"), "*.bin", SearchOption.AllDirectories));
        foreach (var path in new[] { received, Path.Combine(directory, "filetransfer-tuna-gui-payload.bin") })
        {
            using var released = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Equal(268435456, released.Length);
        }
    }
}
