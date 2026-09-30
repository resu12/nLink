namespace NLink.SmokeTests;

public sealed partial class FileTransferPublicationUseConsumerTests
{
    [Theory]
    [InlineData("powershell", "production")]
    [InlineData("pwsh", "production")]
    [InlineData("powershell", "terminal")]
    [InlineData("pwsh", "terminal")]
    public async Task NativeWitnessWriter_DefaultLoadersOmitAdapter(string host, string profile)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        const string script = """
            param([string]$RepoRoot,[string]$Profile)
            $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
            $env:NLINK_TEST_NATIVE_WITNESS_ADAPTER='0'
            $env:NLINK_TEST_SELECTED_NATIVE_WRITER='0'
            if($Profile -ceq 'terminal') {
                & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/TerminalRequestFileBinding.ps1') $RepoRoot
            } else {. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1')}
            foreach($type in @([NLink.Tools.LocalRetainedSourceRequest],[NLink.Tools.LocalRetainedSourceRequest+Worker],[NLink.Tools.LocalRetainedSourceRequest+PrimaryEvaluationCustody])) {
                if(@($type.GetMembers([Reflection.BindingFlags]'Instance,Public,NonPublic') | Where-Object {$_.Name -match 'NativeWitnessWriter|SeededCollector|SelectedOrigin'}).Count) {
                    throw 'Native writer adapter leaked into default loader'
                }
            }
            if($null -ne ('NLink.Tests.HeldFileNativeParent+SeededCollectorLogForTest' -as [type])) {throw 'Seeded log owner leaked into default loader'}
            if($null -ne ('NLink.Tools.LocalRetainedSourceRequest+SelectedCollectorOriginForTest' -as [type])) {throw 'Selected origin result leaked into default loader'}
            'native-writer-default-api-absent'
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), profile], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("native-writer-default-api-absent", result.Stdout, StringComparison.Ordinal);
    }
}
