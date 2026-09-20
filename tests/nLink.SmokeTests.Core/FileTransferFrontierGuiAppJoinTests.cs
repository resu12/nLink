using System.Diagnostics;
using System.Text.Json;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferFrontierGuiAppJoinTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task FrontierGuiAppJoin_RequestsCanBeCreatedAtEachOriginalSequentialLaunch(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-gui-app-join-20260916", "sequential-" + host + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script,
            [root, directory, "sequential-request", "join", typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        var line = Assert.Single(result.Stdout.Split('\n').Select(l => l.Trim()), l => l.StartsWith("frontier-gui-launch="));
        var json = line["frontier-gui-launch=".Length..];
        using var document = JsonDocument.Parse(json);
        Assert.Equal(1, document.RootElement.GetProperty("Cases").GetInt32());
        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), json);
    }

    [Fact]
    public async Task FrontierGuiAppJoin_LoadersRejectSplitCoreAndToolCodecOwnership()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        const string script = """
            param([string]$RepoRoot,[string]$CoreAssembly)
            $ErrorActionPreference='Stop'
            $core=[Reflection.Assembly]::LoadFrom($CoreAssembly)
            . (Join-Path $RepoRoot 'tools/FileTransferOps/LocalFrontierAppRequest.ps1')
            if([NLink.Tools.LocalFrontierAppRequest].Assembly -ne $core) { throw 'Managed app codec replaced' }
            $rejected=$false
            try { . (Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1') }
            catch { if($_.Exception.Message -cne 'incompatible_local_ownership_types') { throw }; $rejected=$true }
            if(-not $rejected -or $null -ne ('NLink.Tools.LocalGuiWriterRequest' -as [type])) { throw 'Split ownership bundle loaded' }
            'split-codec=rejected'
            """;
        var result = await RunPowerShellScriptTextAsync(script,
            [FindRepoRoot(), typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location], "pwsh");
        Assert.True(result.ExitCode == 0 && result.Stdout.Contains("split-codec=rejected", StringComparison.Ordinal),
            result.Stdout + Environment.NewLine + result.Stderr);
    }

    [Theory]
    [InlineData("powershell", false)]
    [InlineData("pwsh", false)]
    [InlineData("powershell", true)]
    [InlineData("pwsh", true)]
    [Trait("FileTransferRuntimeSuite", "FrontierGuiAppJoin")]
    public async Task FrontierGuiAppJoin_OriginalGuiOwnsBothNativeCoreAppRequests(string host, bool parentDeath)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-gui-app-join-20260916", host + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script,
            [root, directory, parentDeath ? "parent-death" : "", "join", typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        var prefix = parentDeath ? "frontier-gui-parent-death=" : "frontier-gui-launch=";
        var line = Assert.Single(result.Stdout.Split('\n').Select(l => l.Trim()), l => l.StartsWith(prefix));
        var json = line[prefix.Length..];
        using var document = JsonDocument.Parse(json);
        if (!parentDeath)
        {
            Assert.Equal(22, document.RootElement.GetProperty("Cases").GetInt32());
            await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), json);
            return;
        }
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
                Assert.True(worker.WaitForExit(20000), "GUI join fixture did not exit after native parent death");
            }
            Assert.True(File.Exists(path), "GUI join fixture did not record parent-death rejection");
            using var completion = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.True(completion.RootElement.GetProperty("ParentDeathRejected").GetBoolean());
            Assert.True(completion.RootElement.GetProperty("AppsStillRunning").GetBoolean());
            Assert.True(completion.RootElement.GetProperty("AppLeasesRevoked").GetBoolean());
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
