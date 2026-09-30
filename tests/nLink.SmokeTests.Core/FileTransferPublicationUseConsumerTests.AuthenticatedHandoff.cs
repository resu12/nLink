namespace NLink.SmokeTests;

public sealed partial class FileTransferPublicationUseConsumerTests
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task AuthenticatedHandoff_ProfileRefusesMixedScopesAndDefaultLoaderOmitsApi(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        const string script = """
            param([string]$RepoRoot,[string]$Directory)
            $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
            $required=@('NLINK_TEST_SELECTED_NATIVE_WRITER','NLINK_TEST_NATIVE_WITNESS_ADAPTER','NLINK_TEST_MEMBERSHIP_BORROWER','NLINK_TEST_COLLECTOR_PRIMARY')
            $forbidden=@('NLINK_TEST_SELECTED_ORIGIN_JOIN','NLINK_TEST_NATIVE_PRIMARY_FILES','NLINK_TEST_COLLECTOR_MEMBERSHIP',
                'NLINK_TEST_TERMINAL_PAYLOAD_JOIN','NLINK_TEST_PRE_VERDICT_INTEGRITY','NLINK_PUBLICATION_TERMINAL_SOURCE_PROBE')
            $env:NLINK_TEST_AUTHENTICATED_PAYLOAD_HANDOFF='1'
            foreach($control in $required+$forbidden) {
                foreach($flag in $required) {[Environment]::SetEnvironmentVariable($flag,'1','Process')}
                foreach($flag in $forbidden) {[Environment]::SetEnvironmentVariable($flag,'0','Process')}
                [Environment]::SetEnvironmentVariable($control,$(if($control -cin $required) {'0'} else {'1'}),'Process')
                $reason=''
                try {
                    & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/PublicationLivePrimaryWorker.ps1') `
                        -Mode AnalyzeRetained -LogPath 'missing.log' -ArtifactDir $Directory -TailMinutes 0 -LiveRouteProofMode RegularActivationCycle
                } catch {$reason=$_.Exception.Message}
                $expected=if($control -cin @('NLINK_TEST_SELECTED_NATIVE_WRITER','NLINK_TEST_SELECTED_ORIGIN_JOIN')) {'authenticated_payload_profile_scope'} else {'selected_native_writer_profile_scope'}
                if($reason -cne $expected -or $null -ne ('NLink.Tools.LocalRetainedSourceRequest' -as [type]) -or
                    $null -ne (Get-Command Invoke-FileTransferRetainedAnalysis -ErrorAction SilentlyContinue) -or
                    (Test-Path -LiteralPath $Directory)) {throw ('authenticated_profile_started:'+ $control+': '+$reason)}
            }
            foreach($flag in $required) {[Environment]::SetEnvironmentVariable($flag,'1','Process')}
            foreach($flag in $forbidden) {[Environment]::SetEnvironmentVariable($flag,'0','Process')}
            $env:NLINK_TEST_AUTHENTICATED_PAYLOAD_HANDOFF='0'
            & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/TerminalRequestFileBinding.ps1') $RepoRoot
            if($null -ne ('NLink.Tools.LocalRetainedSourceRequest+SelectedPayloadOwnerForTest' -as [type]) -or
                $null -ne ('NLink.Tools.LocalRetainedSourceRequest+SelectedTerminalJoinForTest' -as [type]) -or
                $null -ne [NLink.Tools.LocalRetainedSourceRequest+MembershipBorrowerForTest].GetMethod('TryJoinSelectedTerminalsForTest') -or
                $null -ne [NLink.Tools.LocalRetainedSourceRequest+MembershipBorrowerForTest].GetMethod('TryBindSelectedPayloadForTest')) {throw 'authenticated_api_present_by_default'}
            'authenticated-handoff-profile-guards=10'
            """;
        var directory = Path.Combine(FindRepoRoot(), "artifacts", "authenticated-payload-handoff-20260926", "rejected-" + Guid.NewGuid().ToString("N"));
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), directory], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("authenticated-handoff-profile-guards=10", result.Stdout, StringComparison.Ordinal);
    }
}
