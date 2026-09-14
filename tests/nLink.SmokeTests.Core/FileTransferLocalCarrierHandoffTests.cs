namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "ScriptSurface")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferLocalCarrierHandoffTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task LocalNativeInputCapture_RetainedParentsPreservePrimaryUnderOmission(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "publication-native-preparation-20260914", "lifecycle-" + host + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "tools"));
        File.Copy(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "LocalCarrierControlledRetainedWorker.ps1"),
            Path.Combine(directory, "tools", "FileTransfer-Ops.ps1"));
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "LocalCarrierHandoff.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, "native"], host);
        Assert.True(result.ExitCode == 0 && result.Stdout.Contains("local-carrier-handoff=", StringComparison.Ordinal),
            $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
        var line = Assert.Single(result.Stdout.Split('\n').Select(value => value.Trim()),
            value => value.StartsWith("local-carrier-handoff=", StringComparison.Ordinal));
        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), line["local-carrier-handoff=".Length..]);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task LocalEvidenceSlot_RetainedParentsPreservePrimaryUnderOmissionAndContention(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "publication-slot-implementation-20260913", "lifecycle-" + host + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "tools"));
        File.Copy(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "LocalCarrierControlledRetainedWorker.ps1"),
            Path.Combine(directory, "tools", "FileTransfer-Ops.ps1"));
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "LocalCarrierHandoff.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, "slot"], host);
        Assert.True(result.ExitCode == 0 && result.Stdout.Contains("local-carrier-handoff=", StringComparison.Ordinal),
            $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
        var line = Assert.Single(result.Stdout.Split('\n').Select(value => value.Trim()),
            value => value.StartsWith("local-carrier-handoff=", StringComparison.Ordinal));
        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), line["local-carrier-handoff=".Length..]);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task LocalCarrierHandoff_RetainedParentsRequireExitBeforeReceiptAndRejectUnsafePreExitWork(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "publication-handoff-review-20260913", host + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "tools"));
        File.Copy(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "LocalCarrierControlledRetainedWorker.ps1"),
            Path.Combine(directory, "tools", "FileTransfer-Ops.ps1"));
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "LocalCarrierHandoff.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory], host);
        Assert.True(result.ExitCode == 0 && result.Stdout.Contains("local-carrier-handoff=", StringComparison.Ordinal),
            $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
        var line = Assert.Single(result.Stdout.Split('\n').Select(value => value.Trim()),
            value => value.StartsWith("local-carrier-handoff=", StringComparison.Ordinal));
        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), line["local-carrier-handoff=".Length..]);
    }
}
