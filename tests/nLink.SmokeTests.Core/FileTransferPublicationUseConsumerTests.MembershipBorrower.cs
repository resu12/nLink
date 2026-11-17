namespace NLink.SmokeTests;

public sealed partial class FileTransferPublicationUseConsumerTests
{
    [Theory]
    [InlineData("active")]
    [InlineData("dispose")]
    [InlineData("unavailable")]
    [Trait("FileTransferAnalyzerSuite", "SelectedMembershipBorrower")]
    public async Task SelectedMembershipBorrower_OriginalFailureKeepsReaderOwnership(string borrowerCase)
        => await RunGeneratedConsumerAsync("SelectedMembershipBorrowerGenerated", "selected-membership-borrower=",
            "selected-membership-borrower-20260926", failureFixture: true, membershipBorrowerCase: borrowerCase);

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [Trait("FileTransferAnalyzerSuite", "SelectedMembershipIntegrationBoundary")]
    public async Task SelectedMembershipBorrower_NativeProfileStopsBeforeLoadingOrAnalysis(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "selected-terminal-integration-20260926", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        const string script = """
            param([string]$RepoRoot,[string]$Directory)
            $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
            $env:NLINK_TEST_MEMBERSHIP_BORROWER='1'
            $env:NLINK_TEST_NATIVE_PRIMARY_FILES='1'
            $env:NLINK_TEST_COLLECTOR_PRIMARY='1'
            $env:NLINK_TEST_COLLECTOR_MEMBERSHIP='1'
            $env:NLINK_TEST_TERMINAL_PAYLOAD_JOIN='1'
            $analysis=Join-Path $Directory 'analysis-must-not-start'
            $reason=''
            try {
                & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/PublicationLivePrimaryWorker.ps1') `
                    -Mode AnalyzeRetained -LogPath (Join-Path $Directory 'no-source.log') `
                    -ArtifactDir $analysis -TailMinutes 0 -LiveRouteProofMode RegularActivationCycle
            } catch {$reason=$_.Exception.Message}
            if($reason -cne 'borrower_native_profile_not_implemented') {throw ('Unexpected mixed-profile result: '+$reason)}
            foreach($name in @('NLink.Tools.LocalInputReadLease','NLink.Tools.LocalRetainedSourceRequest','NLink.Tests.HeldFileNativeParent')) {
                if($null -ne ($name -as [type])) {throw ('Mixed profile loaded native type: '+$name)}
            }
            foreach($name in @('Invoke-FileTransferRetainedAnalysis','Get-FileTransferCoordinatorSnapshotEvaluation')) {
                if($null -ne (Get-Command $name -ErrorAction SilentlyContinue)) {throw ('Mixed profile loaded analyzer: '+$name)}
            }
            if(Test-Path -LiteralPath $analysis) {throw 'Mixed profile started artifact creation'}
            'selected-terminal-boundary=blocked-before-native-load-and-analysis'
            """;
        var result = await RunPowerShellScriptTextAsync(script, [root, directory], host);
        await File.WriteAllTextAsync(Path.Combine(directory, host + ".stdout.txt"), result.Stdout);
        await File.WriteAllTextAsync(Path.Combine(directory, host + ".stderr.txt"), result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("selected-terminal-boundary=blocked-before-native-load-and-analysis", result.Stdout, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(directory, "analysis-must-not-start")));
    }
}
