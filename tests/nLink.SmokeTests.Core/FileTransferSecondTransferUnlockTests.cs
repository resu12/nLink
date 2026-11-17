namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "GuiHarness")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferSecondTransferUnlockTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task SecondTransferPreparation_PreservesOnActionAndRequiresNewPublication(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = Path.Combine(FindRepoRoot(), "artifacts", "second-transfer-unlock-tests", $"{host}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var script = File.ReadAllText(Path.Combine(FindRepoRoot(), "tests", "nLink.SmokeTests.Core",
            "Support", "SecondTransferUnlock.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), root], host);
        await File.WriteAllTextAsync(Path.Combine(root, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("second-transfer-unlock-ok cases=36", result.Stdout, StringComparison.Ordinal);
    }
}
