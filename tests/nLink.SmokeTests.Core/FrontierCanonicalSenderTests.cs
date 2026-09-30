namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierCanonicalSenderTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task NormalLoader_ExposesOriginalSenderDeliveryWithoutTestFaults(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var script = """
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'
            . (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
            $source=[NLink.Tools.LocalGuiWriterRequest+CanonicalAttemptSource]
            $channel='NLink.Tools.LocalCanonicalSenderSource' -as [type]
            if($null -eq $source.GetMethod('TrySendToHelpee') -or $null -eq $channel) {
                throw 'Production sender handoff missing'
            }
            if($channel.GetConstructors().Length -or $null -ne $source.GetMethod('TrySendToHelpeeForTest') -or
                $null -ne ('NLink.Tests.AttemptSenderSourceChannel' -as [type])) {throw 'Test sender authority leaked'}
            $flags=[Reflection.BindingFlags]'Static,NonPublic'
            if($channel.GetMethod('Receive',$flags).GetParameters().Length -ne 2 -or
                @($channel.GetMethod('Send',$flags).GetParameters() | Where-Object Name -eq 'fault').Count) {throw 'Sender channel scope changed'}
            'canonical-sender-controls-ok'
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("canonical-sender-controls-ok", result.Stdout);
    }
}
