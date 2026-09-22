namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "SnapshotParityReporting")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class RouteAcceptanceSnapshotParityReportingTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("pass")]
    [InlineData("fail")]
    [InlineData("missing-parity")]
    [InlineData("missing-full")]
    [InlineData("missing-compact")]
    [InlineData("malformed")]
    [InlineData("missing-field")]
    [InlineData("wrong-type")]
    [InlineData("wrong-parity-hash")]
    [InlineData("wrong-compact-count")]
    [InlineData("wrong-route-binding")]
    [InlineData("conflicting-text")]
    [InlineData("route-failure")]
    public Task RouteAcceptance_ProjectsOriginalSnapshotParityWithoutReevaluation(string mode) => RunContractAsync(mode);

    [Theory]
    [InlineData("fail-compact-primary")]
    [InlineData("legacy")]
    [InlineData("route-family-failure")]
    [InlineData("consistent-text")]
    [InlineData("wrong-verdict-type")]
    [InlineData("wrong-catalog")]
    public Task RouteAcceptance_PreservesDistinctParityReportingSemantics(string mode) => RunContractAsync(mode);

    [Theory]
    [InlineData("array-parity")]
    [InlineData("array-full")]
    [InlineData("array-compact")]
    public Task RouteAcceptance_RejectsArrayArtifactRoots(string mode) => RunContractAsync(mode);

    private async Task RunContractAsync(string mode)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "snapshot-parity-reporting-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var result = await RunPowerShellFileAsync(root,
            Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "SnapshotParityReporting.ps1"),
            ["-RepoRoot", root, "-Directory", directory, "-Mode", mode]);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("snapshot-parity-reporting-ok", result.Stdout, StringComparison.Ordinal);
    }
}
