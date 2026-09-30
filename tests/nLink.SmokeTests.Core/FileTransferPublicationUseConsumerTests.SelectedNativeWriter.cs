namespace NLink.SmokeTests;

public sealed partial class FileTransferPublicationUseConsumerTests
{
    [Theory]
    [InlineData("active")]
    [InlineData("dispose")]
    [InlineData("unavailable")]
    [Trait("FileTransferAnalyzerSuite", "SelectedNativeWriter")]
    public async Task SelectedNativeWriter_OriginalFailureKeepsWitnessAndBorrower(string borrowerCase)
        => await RunGeneratedConsumerAsync("SelectedNativeWriterGenerated", "selected-native-writer=",
            "selected-native-writer-20260926", failureFixture: true, membershipBorrowerCase: borrowerCase);

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [Trait("FileTransferAnalyzerSuite", "SelectedNativeWriter")]
    public async Task SelectedNativeWriter_UnsafeProfilesStopBeforeLoadingOrAnalysis(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        const string script = """
            param([string]$RepoRoot,[string]$Directory)
            $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
            $env:NLINK_TEST_SELECTED_NATIVE_WRITER='1'
            $required=@('NLINK_TEST_NATIVE_WITNESS_ADAPTER','NLINK_TEST_MEMBERSHIP_BORROWER','NLINK_TEST_COLLECTOR_PRIMARY')
            $forbidden=@('NLINK_TEST_NATIVE_PRIMARY_FILES','NLINK_TEST_COLLECTOR_MEMBERSHIP','NLINK_TEST_TERMINAL_PAYLOAD_JOIN',
                'NLINK_TEST_PRE_VERDICT_INTEGRITY','NLINK_PUBLICATION_TERMINAL_SOURCE_PROBE')
            foreach($control in $required+$forbidden) {
                foreach($flag in $required) {[Environment]::SetEnvironmentVariable($flag,'1','Process')}
                foreach($flag in $forbidden) {[Environment]::SetEnvironmentVariable($flag,'0','Process')}
                $value=if($control -cin $required) {'0'} else {'1'}
                [Environment]::SetEnvironmentVariable($control,$value,'Process')
                $reason=''
                try {
                    & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/PublicationLivePrimaryWorker.ps1') `
                        -Mode AnalyzeRetained -LogPath 'missing.log' -ArtifactDir $Directory -TailMinutes 0 -LiveRouteProofMode RegularActivationCycle
                } catch {$reason=$_.Exception.Message}
                if($reason -cne 'selected_native_writer_profile_scope') {throw ('Unexpected profile result: '+$control+': '+$reason)}
                if($null -ne ('NLink.Tools.LocalRetainedSourceRequest' -as [type]) -or
                    $null -ne (Get-Command Invoke-FileTransferRetainedAnalysis -ErrorAction SilentlyContinue) -or
                    (Test-Path -LiteralPath $Directory)) {throw 'Unsafe profile started analysis or native load'}
            }
            'selected-native-profile-guards=8'
            """;
        var directory = Path.Combine(FindRepoRoot(), "artifacts", "selected-native-writer-20260926", "rejected-" + Guid.NewGuid().ToString("N"));
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), directory], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("selected-native-profile-guards=8", result.Stdout, StringComparison.Ordinal);
    }
}
