namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierProductionPrimaryPayloadTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell", "valid")]
    [InlineData("pwsh", "valid")]
    [InlineData("pwsh", "foreign-completion")]
    [InlineData("pwsh", "pair-retired")]
    [InlineData("pwsh", "parent-rejected")]
    [InlineData("pwsh", "gate-not-selected")]
    public async Task ProductionPrimary_PreservesReportsAndOriginalCustody(string host, string fault)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "production-primary-payload-verification-20260930", host + "-" + fault + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierProductionPrimaryPayload.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, fault], host, timeout: TimeSpan.FromMinutes(6));
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("production-primary-payload-checked=" + fault, result.Stdout);
        var output = await File.ReadAllTextAsync(Path.Combine(directory, "filetransfer-retained-analysis.stdout.txt"));
        Assert.Contains("original parent analyzer evaluations=3 nativeReads=1", output);
        Assert.Contains("production-primary-reports-unchanged=" + fault, output);
        Assert.Contains("production-primary-files-released", output);
        var receipt = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "filetransfer-retained-analysis-attempt-receipt.json")));
        using (receipt)
        {
            Assert.Equal("proof_incomplete", receipt.RootElement.GetProperty("status").GetString());
            Assert.False(receipt.RootElement.GetProperty("completedProofArtifacts").GetBoolean());
            Assert.True(receipt.RootElement.GetProperty("analyzerInputUnchanged").GetBoolean());
        }
        var received = Assert.Single(Directory.GetFiles(Path.Combine(directory, "native-received"), "*.bin", SearchOption.AllDirectories));
        foreach (var path in new[] { received, Path.Combine(directory, "filetransfer-tuna-gui-payload.bin") })
        {
            using var released = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Equal(268435456, released.Length);
        }
    }
}
