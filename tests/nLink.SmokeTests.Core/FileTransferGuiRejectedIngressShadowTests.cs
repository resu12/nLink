using System.Reflection;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "GuiHarness")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferGuiRejectedIngressShadowTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuiRejectedIngressShadow_CharacterizationAndIndependentParity(bool includeShadow)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        // Keep the established fixture as the single source without changing its existing callers.
        var factory = typeof(FileTransferRetainedAuthorityProofAnalyzerTests).GetMethod(
            "BuildFileTunaV4InitialActivationRejectedReceiveFixture", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(factory);
        var lines = Assert.IsType<List<string>>(factory.Invoke(null,
            ["transfer_file_tuna_v4_receive_regular_rejected"]));
        var fixturePath = Path.Combine(Path.GetTempPath(), $"nlink-shadow-fixture-{Guid.NewGuid():N}.log");
        try
        {
            await File.WriteAllLinesAsync(fixturePath, lines);
            var script = await File.ReadAllTextAsync(Path.Combine(FindRepoRoot(), "tests", "nLink.SmokeTests.Core",
                "Support", "GuiRejectedIngressShadowParity.ps1"));
            string? firstHost = null;
            foreach (var host in new[] { "powershell", "pwsh" })
            {
                var arguments = new List<string> { FindRepoRoot(), fixturePath };
                if (!includeShadow) { arguments.Add("-CharacterizationOnly"); }
                var result = await RunPowerShellScriptTextAsync(script, arguments, host);
                Assert.True(result.ExitCode == 0, $"{host} STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
                var normalized = Assert.Single(result.Stdout.Split('\n').Select(line => line.Trim()).Where(line =>
                    line.StartsWith("gui-rejected-ingress-shadow=", StringComparison.Ordinal)));
                if (firstHost is not null) { Assert.Equal(firstHost, normalized); }
                firstHost = normalized;
            }
        }
        finally
        {
            File.Delete(fixturePath);
        }
    }
}
