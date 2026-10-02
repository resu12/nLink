namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class NativeContentReadProfileTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell", "valid")]
    [InlineData("pwsh", "valid")]
    [InlineData("pwsh", "time-budget")]
    [InlineData("pwsh", "stale-owner")]
    [InlineData("pwsh", "mutate-during")]
    public async Task SourceAndParserObservationsPreserveNativeInputRejections(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "native-content-read-profile-20261002", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script,
            [root, directory, "cursor-parent-files-facts-ack-commit-primary-worker-content-native-read-profile-" + scenario, "seal",
                typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location, typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("content-native-acquisition=" + scenario, result.Stdout);
        Assert.Contains("AddedAuthorityEvaluations\":0", result.Stdout);
        Assert.Contains("ContentCoverageProven\":false", result.Stdout);
        var output = await File.ReadAllTextAsync(Path.Combine(directory, "content-native-worker-stdout.txt"));
        var rows = output.Split('\n').Where(line => line.StartsWith("content-native-read-profile=", StringComparison.Ordinal)).ToArray();
        Assert.InRange(rows.Length, 1, 32);
        for (var i = 0; i < rows.Length; i++) Assert.Contains("sequence=" + (i + 1) + ";", rows[i]);
        Assert.Contains("phase=source_lines_assignment_returned;", output);
        Assert.Contains("phase=parser_returned;", output);
        Assert.Contains("phase=source_read_finally;", output);
        Assert.Contains("source_ended=True;", output);
        Assert.Contains("rejected=none; close=open; exception=none;", output);
        Assert.True(output.Length <= 65536);
    }
}
