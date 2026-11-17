using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using System.Diagnostics;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FileTransferFrontierPublicationPrimaryTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    [Trait("FileTransferRuntimeSuite", "FrontierPublicationPrimary")]
    public async Task FrontierPublicationPrimary_ActualPrimaryOwnsOfflineFailureWitnesses()
        => await RunOwnedAsync("bounded", nameof(FrontierPublicationPrimary_ActualPrimaryOwnsOfflineFailureWitnesses));

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FrontierPublicationPrimary")]
    public async Task FrontierPublicationPrimary_WitnessOverflowPreservesPrimaryFailure()
        => await RunOwnedAsync("overflow", nameof(FrontierPublicationPrimary_WitnessOverflowPreservesPrimaryFailure));

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FrontierPublicationPrimary")]
    public async Task FrontierPublicationPrimary_UnrelatedHistoryDoesNotConsumeEpisodeWitnesses()
        => await RunOwnedAsync("history", nameof(FrontierPublicationPrimary_UnrelatedHistoryDoesNotConsumeEpisodeWitnesses));

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FrontierPublicationPrimary")]
    public async Task FrontierPublicationPrimary_ConflictingFrameIdentityOmitsOptionalResult()
        => await RunOwnedAsync("conflict", nameof(FrontierPublicationPrimary_ConflictingFrameIdentityOmitsOptionalResult));

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FrontierPublicationPrimary")]
    public async Task FrontierPublicationPrimary_CancellationAfterPreparationOmitsOptionalResult()
        => await RunOwnedAsync("post-cancel", nameof(FrontierPublicationPrimary_CancellationAfterPreparationOmitsOptionalResult));

    private static async Task RunOwnedAsync(string mode, string testName)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        if (Environment.GetEnvironmentVariable("NLINK_FRONTIER_PRIMARY_FIXTURE") == "1")
        {
            var fixture = await SessionFileTransferV4SenderTests.RunFrontierPublicationFixtureAsync(
                mode switch { "overflow" => "primary_required_overflow", "history" => "primary_overflow",
                    "conflict" => "primary_conflict", _ => "primary_failure" }, true);
            var directory = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR")!;
            await File.WriteAllTextAsync(Path.Combine(directory, "writer-receipt.json"),
                FileTransferCoordinator.FrontierPublicationWriterBinding.CanonicalPayload(fixture.Producer.Receipt!));
            Assert.True(LocalOperationalLog.CompleteAndFlush(TimeSpan.FromSeconds(5)));
            return;
        }
        var root = FindRepoRoot();
        var output = Path.Combine(root, "artifacts", "frontier-publication-episode-20260915", "owned-" + mode + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var run = Guid.NewGuid().ToString("N");
        var scenario = run + ":offline-frontier";
        using var child = new Process { StartInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = root,
        } };
        child.StartInfo.ArgumentList.Add("vstest");
        child.StartInfo.ArgumentList.Add(typeof(FileTransferFrontierPublicationPrimaryTests).Assembly.Location);
        child.StartInfo.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName~" + testName);
        child.StartInfo.Environment["NLINK_FRONTIER_PRIMARY_FIXTURE"] = "1";
        child.StartInfo.Environment["NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR"] = output;
        child.StartInfo.Environment["NLINK_FILETRANSFER_ACCEPTANCE_LOG_PATH"] = Path.Combine(output, "nlink.log");
        child.StartInfo.Environment["NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID"] = run;
        child.StartInfo.Environment["NLINK_FILETRANSFER_SCENARIO_RUN_ID"] = scenario;
        child.Start();
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await child.WaitForExitAsync(deadline.Token); }
        catch { if (!child.HasExited) { child.Kill(entireProcessTree: true); } throw; }
        Assert.True(child.ExitCode == 0, (await stdout) + (await stderr));
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierPublicationPrimaryGenerated.ps1"));
        foreach (var host in new[] { "powershell", "pwsh" })
        {
            var result = await RunPowerShellScriptTextAsync(script, [root, output, run, scenario, mode], host, TimeSpan.FromMinutes(4));
            Assert.True(result.ExitCode == 0 && result.Stdout.Contains("frontier-primary-owned=", StringComparison.Ordinal),
                $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
            await File.WriteAllTextAsync(Path.Combine(output, host + "-owned.txt"), result.Stdout);
        }
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [Trait("FileTransferRuntimeSuite", "FrontierPublicationPrimary")]
    public async Task FrontierPublicationPrimary_FreshServiceSourceReportsActualUnresolvedFailure(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-publication-primary-20260915", host + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var fixture = await SessionFileTransferV4SenderTests.RunFrontierPublicationFixtureAsync("primary_failure", true);
        var capture = FileTransferCoordinator.FrontierPublicationOfflineCapture.TryFreeze(fixture.Producer, fixture.Lines, out var reason);
        Assert.True(capture is not null, reason);
        var input = Path.Combine(directory, "source.log");
        await File.WriteAllLinesAsync(input, fixture.Lines);
        await File.WriteAllTextAsync(Path.Combine(directory, "capture.json"), capture!.ExportFull());
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierPublicationPrimarySource.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, input], host);
        Assert.True(result.ExitCode == 0 && result.Stdout.Contains("frontier-primary-source=", StringComparison.Ordinal),
            $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout);
    }
}
