namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierDestinationGuiHandoffTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task DefaultLoader_DoesNotExposeDestinationAdopter(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var script = """
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'
            . (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
            if($null -ne ('NLink.Tools.LocalGuiWriterRequest+AttemptDestinationForTest' -as [type])) {throw 'Destination adopter leaked'}
            'destination-adopter-default-ok'
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("destination-adopter-default-ok", result.Stdout);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task OriginalApps_FullSizeDestinationReachesGui(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "destination-gui-handoff-20260926", host + "-full-size-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierAttemptDispatch.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, "writer-source-read-cross-process-gui-valid"], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        var gui = await File.ReadAllTextAsync(Path.Combine(directory, "gui-smoke-stdout.log"));
        foreach (var marker in new[] { "attempt-destination-gui-adoption-ok", "attempt-destination-closed-writers-ok",
            "attempt-source-custody-ok", "attempt-role-writers-ok" }) Assert.Contains(marker, gui);
        var measurement = await File.ReadAllLinesAsync(Path.Combine(directory, "source-measurement.txt"));
        foreach (var role in new[] { "helpee", "helper" })
        {
            var output = Assert.Single(Directory.GetFiles(Path.Combine(directory, "process-output"), role + "-*.stdout.log"));
            Assert.Contains("cross-process-" + role + "=268435456; sha256=" + measurement[1], await File.ReadAllTextAsync(output));
        }
    }

    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from fault in new[] { "valid", "bad-ack", "foreign-identity", "cancel-offer", "cancel-token", "closed-hold", "foreign-server" }
        select new object[] { host, fault };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OriginalHelper_GuiAdoptsOnlyCommittedNativeDestination(string host, string fault)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var scenario = "writer-destination-gui-" + fault;
        var directory = Path.Combine(root, "artifacts", "destination-gui-handoff-20260926", host + "-" + fault + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierAttemptDispatch.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        var gui = await File.ReadAllTextAsync(Path.Combine(directory, "gui-smoke-stdout.log"));
        Assert.Contains("attempt-destination-gui-adoption-ok", gui);
        Assert.Contains("attempt-role-writers-ok", gui);
        if (fault == "valid") Assert.Contains("attempt-destination-closed-writers-ok", gui);
        var app = string.Join("\n", await Task.WhenAll(Directory.GetFiles(directory, "*.stdout.log", SearchOption.AllDirectories)
            .Select(path => File.ReadAllTextAsync(path))));
        Assert.Contains("receiver-destination-gui=" + scenario, app);
        using var released = File.Open(Path.Combine(directory, "native-destination", "native-receive.bin"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
}
