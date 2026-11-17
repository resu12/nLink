using System.Diagnostics;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FileTransferSupersededSourceAccountingTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("OpsSuite", "SupersededSourceAccounting")]
    public async Task SupersededSource_ActualJoinedCaptureAndReportsHaveCrossHostParity(bool delayedSnapshots)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(Path.GetTempPath(), "nlink-superseded-accounting-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "joined.log");
            // Isolate the process-wide operational logger so the fixture uses a
            // fresh actual-emitter capture regardless of parent test ordering.
            // The selected test uses fake peers, never a live NKN connection.
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var argument in new[] { "test", "tests/nLink.SmokeTests.Core/nLink.SmokeTests.Core.csproj",
                "--no-build", "--no-restore", "--filter",
                "FullyQualifiedName=NLink.SmokeTests.NknAccelerationTransportTests." + (delayedSnapshots
                    ? "JoinedFallbackRetry_StalledRepairWithDelayedReceiverSnapshots"
                    : "JoinedFallbackRetry_StalledRepairAndOldSourceReplayRemainUnresolvedAcrossPeerHandoff"),
                "--verbosity", "quiet" }) { start.ArgumentList.Add(argument); }
            start.Environment["NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR"] = directory;
            start.Environment["NLINK_FILETRANSFER_ACCEPTANCE_LOG_PATH"] = path;
            start.Environment["NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID"] = "superseded-accounting-unit";
            start.Environment["NLINK_FILETRANSFER_SCENARIO_RUN_ID"] = "joined-fake-peer-unit";
            using var child = Process.Start(start)!;
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try { await child.WaitForExitAsync(timeout.Token); }
            catch { if (!child.HasExited) { child.Kill(entireProcessTree: true); } throw; }
            Assert.True(child.ExitCode == 0, $"Joined fixture: {await stdout}{Environment.NewLine}{await stderr}");
            Assert.True(File.Exists(path), "Actual joined capture was not emitted.");
            const string script = """
                param([string]$RepoRoot,[string]$InputPath,[string]$OutputRoot,[int]$DelayedSnapshots)
                $ErrorActionPreference='Stop'
                & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/SupersededSourceAccounting.ps1') -RepoRoot $RepoRoot -InputPath $InputPath -ExpectDelayedReceiverSnapshots:([bool]$DelayedSnapshots)
                & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/StallGraceAccountingReports.ps1') -RepoRoot $RepoRoot -OutputRoot $OutputRoot
                """;
            string? previous = null;
            foreach (var host in new[] { "powershell", "pwsh" })
            {
                var result = await RunPowerShellScriptTextAsync(script, [root, path, Path.Combine(directory, host), delayedSnapshots ? "1" : "0"], host);
                Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
                Assert.Contains("superseded-source-accounting-ok", result.Stdout, StringComparison.Ordinal);
                Assert.Contains("stall-grace-accounting-reports-ok", result.Stdout, StringComparison.Ordinal);
                if (previous is not null) { Assert.Equal(previous, result.Stdout.Trim()); }
                previous = result.Stdout.Trim();
            }
        }
        catch
        {
            var capture = Path.Combine(directory, "joined.log");
            if (File.Exists(capture))
            {
                var failures = Path.Combine(root, "artifacts", "early-yield-evidence-20260930", "superseded-source-failures");
                Directory.CreateDirectory(failures);
                File.Copy(capture, Path.Combine(failures, Guid.NewGuid().ToString("N") + ".log"));
            }
            throw;
        }
        finally { TryDeleteDirectory(directory); }
    }
}
