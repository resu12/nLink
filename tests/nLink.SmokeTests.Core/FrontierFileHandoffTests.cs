using System.Diagnostics;
using System.Text.Json;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierFileHandoffTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "maximum", "copied", "before-seal", "repeat-enable", "no-channel", "attach-before-ready",
            "premature-ack", "repeat-submit", "early-complete", "catalog-cancel", "request-cancel", "body-before", "body-after",
            "ack-nonce", "event-only", "repeat-adopt", "repeat-ack", "state-replay", "nonzero", "app-crash", "gate-busy", "budget", "cancel-after",
            "catalog-cancel-race", "complete-after-adopt" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task FileHandoff_OriginalGuiAndCoreCatalogEnforceOneShotProtocol(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-file-rendezvous-20260916", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script,
            [root, directory, scenario, "files", typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location,
                typeof(FrontierFileHandoffFixture).Assembly.Location], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        var line = Assert.Single(result.Stdout.Split('\n').Select(l => l.Trim()), l => l.StartsWith("frontier-gui-launch="));
        using var document = JsonDocument.Parse(line["frontier-gui-launch=".Length..]);
        Assert.Equal(1, document.RootElement.GetProperty("Cases").GetInt32());
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task FileHandoff_NativeParentDeathRevokesBothAppChannels(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-file-rendezvous-20260916", host + "-parent-death-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script,
            [root, directory, "parent-death", "files", typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location,
                typeof(FrontierFileHandoffFixture).Assembly.Location], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        var line = Assert.Single(result.Stdout.Split('\n').Select(l => l.Trim()), l => l.StartsWith("frontier-gui-parent-death="));
        using var document = JsonDocument.Parse(line["frontier-gui-parent-death=".Length..]);
        var data = document.RootElement;
        Process? worker = null;
        try
        {
            try { worker = Process.GetProcessById(data.GetProperty("GuiProcessId").GetInt32()); } catch (ArgumentException) { }
            if (worker is not null)
            {
                Assert.Equal(data.GetProperty("GuiCreation").GetInt64(), worker.StartTime.ToUniversalTime().ToFileTimeUtc());
                Assert.True(worker.WaitForExit(20000));
            }
            using var completion = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "parent-death-result.json")));
            Assert.True(completion.RootElement.GetProperty("ParentDeathRejected").GetBoolean());
            Assert.True(completion.RootElement.GetProperty("FilesRevoked").GetBoolean());
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
