namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferPrimaryEvaluationInputTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task PrimaryEvaluationInputs_BindActualOwnerAndPreservePrimaryCalls(string host)
        => await RunAsync(host, "inputs");

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task PrimaryEvaluationCustody_RejectsSubstitutionReplayAndExpiredOwner(string host)
        => await RunAsync(host, "custody");

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task PrimaryEvaluationCustody_ActualFirstFailureSeamDropsRejectedOrThrowingCandidate(string host)
        => await RunAsync(host, "seam");

    private static async Task RunAsync(string host, string lane)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "publication-primary-custody-20260914", lane + "-" + host + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var support = lane == "seam" ? "PrimaryEvaluationCustodySeam.ps1" : "PrimaryEvaluationInputs.ps1";
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", support));
        var result = await RunPowerShellScriptTextAsync(script, lane == "seam" ? [root] : [root, directory, lane], host);
        var prefix = lane == "seam" ? "primary-custody-seam=" : "primary-evaluation-inputs=";
        Assert.True(result.ExitCode == 0 && result.Stdout.Contains(prefix, StringComparison.Ordinal), $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
        var line = Assert.Single(result.Stdout.Split('\n').Select(value => value.Trim()), value => value.StartsWith(prefix, StringComparison.Ordinal));
        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), line[prefix.Length..]);
    }
}
