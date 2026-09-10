using System.Reflection;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "GuiHarness")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferGuiDiagnosticCompanionTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task GuiDiagnosticCompanion_IsolatesPrimaryAndValidatesImmutableEvidence(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var factory = typeof(FileTransferRetainedAuthorityProofAnalyzerTests).GetMethod(
            "BuildFileTunaV4InitialActivationRejectedReceiveFixture", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(factory);
        var lines = Assert.IsType<List<string>>(factory.Invoke(null,
            ["transfer_file_tuna_v4_receive_regular_rejected"]));
        var root = Path.Combine(Path.GetTempPath(), $"nlink-companion-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var fixture = Path.Combine(root, "input.log");
            await File.WriteAllLinesAsync(fixture, lines);
            var script = await File.ReadAllTextAsync(Path.Combine(FindRepoRoot(), "tests", "nLink.SmokeTests.Core",
                "Support", "GuiDiagnosticCompanion.ps1"));
            var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), root, fixture], host);
            Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
            Assert.Contains("gui-diagnostic-companion-ok", result.Stdout, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
