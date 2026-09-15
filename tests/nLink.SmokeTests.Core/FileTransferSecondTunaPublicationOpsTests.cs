namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "ScriptSurface")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferSecondTunaPublicationOpsTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SecondTunaPublication_ServiceEvidenceBindsRetirementAndPreservesRejectedHandoffBoundary(bool replayPostCommitCallbacks)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var repoRoot = FindRepoRoot();
        var root = Path.Combine(Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR")
            ?? Path.GetTempPath(), "second-tuna-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var input = Path.Combine(root, "receiver.log");
            await File.WriteAllTextAsync(input,
                await SessionFileTransferV4SenderTests.CreateSecondTunaPublicationEvidenceFixtureAsync(
                    replayPostCommitCallbacks: replayPostCommitCallbacks));
            var script = await File.ReadAllTextAsync(Path.Combine(repoRoot, "tests", "nLink.SmokeTests.Core",
                "Support", "SecondTunaServicePublicationEvidence.ps1"));
            string? first = null;
            foreach (var host in new[] { "powershell", "pwsh" })
            {
                var result = await RunPowerShellScriptTextAsync(script,
                    [repoRoot, input, replayPostCommitCallbacks ? "1" : "0"], host);
                await File.WriteAllTextAsync(Path.Combine(root, host + "-result.txt"), result.Stdout + result.Stderr);
                Assert.True(result.ExitCode == 0, $"{host} STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
                var projection = Assert.Single(result.Stdout.Split('\n').Select(line => line.Trim()).Where(line =>
                    line.StartsWith("second-tuna-service=", StringComparison.Ordinal)));
                if (first is not null) { Assert.Equal(first, projection); }
                first = projection;
            }
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR") is null)
            {
                TryDeleteDirectory(root);
            }
        }
    }
}
