using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed class FileTransferDispatchRangeTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [Trait("FileTransferAnalyzerSuite", "DispatchRangeExclusion")]
    public async Task DispatchRangeExclusion_OnlyCanonicalDisjointV4RangesExclude(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        // The bounded diagnostic schema must track the producer's codec limits.
        Assert.Equal(1_500_000, FileTransferProtocol.MaxChunkCountV4);
        Assert.Equal(3, FileTransferProtocol.MaxChunkBatchSegmentsV4);
        var root = FindRepoRoot();
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "PublicationDispatchRangeControls.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("dispatch-range-controls=", result.Stdout, StringComparison.Ordinal);
    }
}
