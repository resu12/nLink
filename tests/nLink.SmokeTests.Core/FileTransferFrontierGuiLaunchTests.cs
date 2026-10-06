using System.Diagnostics;
using System.Text.Json;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FileTransferFrontierGuiLaunchTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [Trait("FileTransferRuntimeSuite", "FrontierGuiLaunch")]
    public async Task FrontierGuiLaunch_OriginalOwnerBindsEveryAppBeforeSuccessfulExitEvidence(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-gui-launch-20260916", host + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        var line = Assert.Single(result.Stdout.Split('\n').Select(l => l.Trim()), l => l.StartsWith("frontier-gui-launch="));
        var json = line["frontier-gui-launch=".Length..];
        using var document = JsonDocument.Parse(json);
        Assert.Equal(25, document.RootElement.GetProperty("Cases").GetInt32());
        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), json);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [Trait("FileTransferRuntimeSuite", "FrontierGuiLaunch")]
    public async Task FrontierGuiLaunch_NativeParentDeathRevokesWithoutStoppingApps(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-gui-launch-20260916", "parent-death-" + host + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, "parent-death"], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        var line = Assert.Single(result.Stdout.Split('\n').Select(l => l.Trim()), l => l.StartsWith("frontier-gui-parent-death="));
        using var document = JsonDocument.Parse(line["frontier-gui-parent-death=".Length..]);
        var data = document.RootElement;
        var path = Path.Combine(directory, "parent-death-result.json");
        Assert.Equal(path, data.GetProperty("Result").GetString());
        Process? worker = null;
        try
        {
            try { worker = Process.GetProcessById(data.GetProperty("GuiProcessId").GetInt32()); }
            catch (ArgumentException) { }
            if (worker is not null)
            {
                Assert.Equal(data.GetProperty("GuiCreation").GetInt64(), worker.StartTime.ToUniversalTime().ToFileTimeUtc());
                Assert.True(worker.WaitForExit(20000), "GUI fixture did not exit after parent death");
            }
            Assert.True(File.Exists(path), "GUI fixture did not record native parent-death rejection");
            using var completion = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.True(completion.RootElement.GetProperty("ParentDeathRejected").GetBoolean());
            Assert.True(completion.RootElement.GetProperty("AppsStillRunning").GetBoolean());
        }
        finally
        {
            if (worker is not null)
            {
                if (!worker.HasExited && worker.StartTime.ToUniversalTime().ToFileTimeUtc() == data.GetProperty("GuiCreation").GetInt64())
                { worker.Kill(); worker.WaitForExit(5000); }
                worker.Dispose();
            }
        }
    }
}
