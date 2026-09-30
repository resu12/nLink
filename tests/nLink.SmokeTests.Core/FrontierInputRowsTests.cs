namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierInputRowsTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("pwsh", "differential")]
    [InlineData("powershell", "differential")]
    [InlineData("pwsh", "scale")]
    [InlineData("powershell", "scale")]
    public async Task CompiledProjectionPreservesOriginalRowsChecksAndVisits(string host, string mode)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierInputRowsControls.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, mode], host);
        var artifacts = Path.Combine(root, "artifacts/canonical-witness-row-projection-20260929/controls", host + "-" + mode + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifacts);
        await File.WriteAllTextAsync(Path.Combine(artifacts, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("frontier-input-rows-" + mode + "=passed", result.Stdout);
    }
}
