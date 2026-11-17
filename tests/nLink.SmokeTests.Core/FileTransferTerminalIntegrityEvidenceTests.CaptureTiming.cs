using System.Diagnostics;
using System.Text.Json;

namespace NLink.SmokeTests;

public sealed partial class FileTransferTerminalIntegrityEvidenceTests
{
    [Theory]
    [InlineData("pwsh", "terminal")]
    [InlineData("powershell", "terminal")]
    [InlineData("pwsh", "production")]
    [InlineData("powershell", "production")]
    public async Task TerminalCaptureTiming_PreservesBudgetsOwnershipAndProduction(string host, string mode)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "capture-timing-alignment-20260925", host + "-" + mode + "-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        using var process = new Process { StartInfo = new ProcessStartInfo(host)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = root,
        } };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "TerminalCaptureTiming.ps1"), root, directory, mode })
        { process.StartInfo.ArgumentList.Add(argument); }
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(deadline.Token); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); } }
        await File.WriteAllTextAsync(Path.Combine(directory, "stdout.txt"), await stdout);
        await File.WriteAllTextAsync(Path.Combine(directory, "stderr.txt"), await stderr);
        Assert.True(process.ExitCode == 0, await stdout + Environment.NewLine + await stderr);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, mode == "terminal" ? "timing.json" : "production.json")));
        var observations = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(mode == "terminal" ? 8 : 2, observations.Length);
        foreach (var observation in observations)
        {
            if (mode == "production")
            {
                foreach (var field in new[] { "TimingApiAbsent", "PathnameCaptureOwned", "StreamBehaviorPreserved" })
                { Assert.True(observation.GetProperty(field).GetBoolean(), field); }
                continue;
            }
            var scenario = observation.GetProperty("Mode").GetString();
            Assert.True(observation.GetProperty("FilesReleased").GetBoolean());
            Assert.Equal(scenario is "pre-metadata" or "ownership", observation.GetProperty("CaptureAccepted").GetBoolean());
            var before = observation.GetProperty("BeforeCaptureMilliseconds").GetInt64();
            var after = observation.GetProperty("AfterCaptureMilliseconds").GetInt64();
            Assert.True(after >= before);
            if (scenario == "pre-metadata")
            {
                Assert.Equal(350, observation.GetProperty("ReadDelayMilliseconds").GetInt32());
                Assert.InRange(observation.GetProperty("ProcessingMilliseconds").GetInt64(), 350, 5000);
                Assert.InRange(after, 0, 250);
            }
            if (scenario == "inside-metadata")
            {
                Assert.Equal(350, observation.GetProperty("MetadataDelayMilliseconds").GetInt32());
                Assert.True(before >= 350);
            }
            if (scenario == "post-return-expiry") { Assert.True(before >= 350); }
            if (scenario == "ownership") { Assert.Equal(9, observation.GetProperty("RejectedOwnershipControls").GetInt32()); }
            if (scenario == "processing-expired")
            {
                Assert.True(observation.GetProperty("ProcessingExpired").GetBoolean());
                Assert.Equal(5100, observation.GetProperty("ReadDelayMilliseconds").GetInt32());
                Assert.True(observation.GetProperty("ProcessingMilliseconds").GetInt64() > 5000);
            }
        }
        Assert.False(Directory.Exists(Path.Combine(directory, "primary")));
    }
}
