namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierCanonicalDestinationTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task NormalLoader_ExposesOnlySourceBoundAdoption(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var script = """
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'
            . (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
            $type='NLink.Tools.LocalGuiWriterRequest+CanonicalAttemptDestination' -as [type]
            $dispatch=[NLink.Tools.LocalGuiWriterRequest+CanonicalAttemptDispatch]
            if($null -eq $type -or $null -eq $dispatch.GetMethod('TryAdoptDestination')) {throw 'Production destination handoff missing'}
            if($type.GetConstructors().Length -or $null -ne $type.GetMethod('TryBindClosedWritersForTest') -or
                $null -ne $dispatch.GetMethod('TryAdoptDestinationForTest')) {throw 'Test destination authority leaked'}
            $names=@($dispatch.GetMethod('TryAdoptDestination').GetParameters() | ForEach-Object Name)
            if(($names -join ',') -cne 'request,expectedSource,cancellationToken') {throw 'Destination accepts caller-authored expectations'}
            'canonical-destination-loader-ok'
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("canonical-destination-loader-ok", result.Stdout);
    }

    [Theory]
    [InlineData("powershell", "")]
    [InlineData("pwsh", "")]
    [InlineData("pwsh", "source")]
    [InlineData("pwsh", "attempt")]
    public async Task OriginalDestination_RequiresSameAttemptSourceAndHelper(string host, string retirement)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "production-canonical-destination-20260929",
            host + "-" + retirement + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatch.ps1"));
        script = script.Replace("$ErrorActionPreference='Stop';", "$env:NLINK_TEST_DESTINATION_RETIRE='" + retirement + "'; $ErrorActionPreference='Stop';", StringComparison.Ordinal);
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, "writer-source-read-cross-process-gui-valid"], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        var gui = await File.ReadAllTextAsync(Path.Combine(directory, "gui-smoke-stdout.log"));
        Assert.Contains("canonical-destination-controls-ok", gui);
        Assert.Contains("attempt-destination-closed-writers-ok", gui);
        if (retirement.Length > 0) Assert.Contains("canonical-destination-retired=" + retirement, gui);
        var measurement = await File.ReadAllLinesAsync(Path.Combine(directory, "source-measurement.txt"));
        foreach (var role in new[] { "helpee", "helper" })
        {
            var output = Assert.Single(Directory.GetFiles(Path.Combine(directory, "process-output"), role + "-*.stdout.log"));
            Assert.Contains("cross-process-" + role + "=268435456; sha256=" + measurement[1], await File.ReadAllTextAsync(output));
        }
    }
}
