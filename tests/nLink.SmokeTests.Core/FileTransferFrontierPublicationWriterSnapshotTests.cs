using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FileTransferFrontierPublicationWriterSnapshotTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [Trait("FileTransferRuntimeSuite", "FrontierPublicationWriterSnapshot")]
    public async Task FrontierPublicationWriter_FreshOriginalViewsRetainExactCompletionWithoutAnotherEvaluation(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-publication-writer-20260915", host + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var fixture = await SessionFileTransferV4SenderTests.RunFrontierPublicationFixtureAsync("newer_token", true);
        var capture = FileTransferCoordinator.FrontierPublicationOfflineCapture.TryFreeze(fixture.Producer, fixture.Lines, out var reason);
        Assert.True(capture is not null, reason);
        var input = Path.Combine(directory, "source.log");
        var payload = Path.Combine(directory, "capture.json");
        await File.WriteAllLinesAsync(input, fixture.Lines);
        await File.WriteAllTextAsync(payload, capture!.ExportFull());
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierPublicationWriterSnapshot.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, input, payload], host);
        Assert.True(result.ExitCode == 0 && result.Stdout.Contains("frontier-writer-snapshot=", StringComparison.Ordinal),
            $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout);
    }
}
