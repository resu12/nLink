namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "ScriptSurface")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferLocalEvidenceSlotTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task LocalEvidenceSlot_OffersWithoutWaitingAndRequiresExactExitedProducer(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "publication-slot-implementation-20260913", host + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "LocalEvidenceExportSlot.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory], host);
        Assert.True(result.ExitCode == 0 && result.Stdout.Contains("local-evidence-slot=", StringComparison.Ordinal),
            $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
        var line = Assert.Single(result.Stdout.Split('\n').Select(value => value.Trim()),
            value => value.StartsWith("local-evidence-slot=", StringComparison.Ordinal));
        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), line["local-evidence-slot=".Length..]);
    }
}
