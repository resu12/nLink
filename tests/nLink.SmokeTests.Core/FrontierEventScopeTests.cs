namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierEventScopeTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("pwsh")]
    [InlineData("powershell")]
    public async Task DataSessionReader_ResolvesOnlyOriginalUnambiguousIdentity(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
            . (Join-Path $RepoRoot 'tools/GuiSmokeStructuredFields.ps1')
            . (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
            . (Join-Path $RepoRoot 'tools/FileTransferOps/LocalNativeFrontierWitness.ps1')
            $id='native-carrier-'+('a'*31)+'1:1'
            $token=ConvertTo-FileTransferCoordinatorEvidenceToken $id
            $raw='data_session_identity='+$id
            $projected='frontier_scope_data_session_identity='+$token
            $checks=0
            function Check([string]$Body,[string]$Error,[string]$Token=$token) {
                $line='event=filetransfer_v6_frontier_recent_progress_grace_deferred; '+$Body
                $fields=ConvertFrom-GuiSmokeLogLedgerStructuredFields $line
                $before=$fields|ConvertTo-Json -Compress
                $identity=Read-FileTransferNativeFrontierDataSession $line $fields
                if($identity.Error -cne $Error) {throw ('Data-session case '+$script:checks+': '+$identity.Error+' expected='+$Error)}
                if(-not $Error -and $identity.Token -cne $Token) {throw 'Data-session token mismatch'}
                if(($fields|ConvertTo-Json -Compress) -cne $before) {throw 'Data-session reader mutated fields'}
                $script:checks++
            }
            Check $raw ''
            Check "$raw; $projected" ''
            foreach($value in @('[redacted]', '[redacted]:1', '[redacted]:009')) {
                Check "data_session_identity=$value; $projected" ''
                Check "data_session_identity=$value" 'native_frontier_data_session_unavailable'
            }
            Check "$raw; $projected; range_fingerprint=x; range_fingerprint=x" ''
            Check '' 'native_frontier_data_session_unavailable'
            Check $projected 'native_frontier_data_session_unavailable'
            foreach($value in @('', ' ', '(none)', 'prefix[redacted]:1', '[redacted]:1suffix', '[redacted]:-1', '[redacted]:+1', '[redacted]:x', '[redacted]:١', '[redacted]:[redacted]')) {
                Check "data_session_identity=$value; $projected" 'native_frontier_data_session_unavailable'
            }
            foreach($value in @('', '(none)', ('fte2-'+('0'*24)), ('fte1-'+('A'*24)), ('fte1-'+('0'*23)), ('fte1-'+('0'*25)), ($token+'-suffix'))) {
                Check "$raw; frontier_scope_data_session_identity=$value" 'native_frontier_data_session_invalid'
            }
            foreach($key in @('data_session_identity','frontier_scope_data_session_identity')) {
                $value=if($key -ceq 'data_session_identity') {$id} else {$token}
                Check "$raw; $projected; $key=$value" 'native_frontier_duplicate_data_session'
                Check "$raw; $projected; $($key.ToUpperInvariant())=foreign" 'native_frontier_duplicate_data_session'
            }
            Check "data_session_identity=other:1; $projected" 'native_frontier_data_session_conflict'
            Check "$raw; $projected`nother=x" 'native_frontier_data_session_invalid'
            Check "$raw; $projected`rother=x" 'native_frontier_data_session_invalid'
            Check "data_session_identity=$token" '' (ConvertTo-FileTransferCoordinatorEvidenceToken $token)
            Check "DATA_SESSION_IDENTITY=$id; FRONTIER_SCOPE_DATA_SESSION_IDENTITY=$token" ''
            $legacy=Read-FileTransferNativeFrontierDataSession $raw (ConvertFrom-GuiSmokeLogLedgerStructuredFields $raw)
            $new="data_session_identity=[redacted]:1; $projected"
            $resolved=Read-FileTransferNativeFrontierDataSession $new (ConvertFrom-GuiSmokeLogLedgerStructuredFields $new)
            if($legacy.Token -cne $resolved.Token -or $legacy.Projected -or -not $resolved.Projected -or -not $legacy.Present -or -not $resolved.Present) {throw 'Mixed data-session representation mismatch'}
            $empty=Read-FileTransferNativeFrontierDataSession '' @{}
            if($empty.Present -or $empty.Projected) {throw 'Absent identity marked present'}
            'data-session-reader-checks='+$checks
            """, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("data-session-reader-checks=", result.Stdout);
    }

    [Theory]
    [InlineData("pwsh")]
    [InlineData("powershell")]
    public async Task ScopeReader_RejectsAmbiguousIdentityWithoutMutatingRows(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
            . (Join-Path $RepoRoot 'tools/GuiSmokeStructuredFields.ps1')
            . (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
            . (Join-Path $RepoRoot 'tools/FileTransferOps/LocalNativeFrontierWitness.ps1')
            $session=ConvertTo-FileTransferCoordinatorEvidenceToken 'scope-session'
            $transfer=ConvertTo-FileTransferCoordinatorEvidenceToken 'scope-transfer'
            $raw='session_id=scope-session; transfer_id=scope-transfer'
            $projected="frontier_scope_session_id=$session; frontier_scope_transfer_id=$transfer"
            $checks=0
            function Check([string]$Body,[string]$Error,[string]$Session=$session,[string]$Transfer=$transfer) {
                $line='event=filetransfer_v6_receiver_state_deferred; '+$Body
                $fields=ConvertFrom-GuiSmokeLogLedgerStructuredFields $line
                $before=$fields|ConvertTo-Json -Compress
                $scope=Read-FileTransferNativeFrontierScope $line $fields
                if($scope.Error -cne $Error) {throw ('Scope case '+$script:checks+': expected='+$Error+' actual='+$scope.Error+' body='+$Body)}
                if(-not $Error -and ($scope.Session -cne $Session -or $scope.Transfer -cne $Transfer)) {throw 'Scope token mismatch'}
                if(($fields|ConvertTo-Json -Compress) -cne $before) {throw 'Scope reader mutated fields'}
                $script:checks++
            }
            Check $raw ''
            Check "$raw; $projected" ''
            Check "session_id=[redacted]; transfer_id=[redacted]; $projected" ''
            Check "session_id=sess_[redacted]; transfer_id=[redacted]; $projected" ''
            Check "session_id=scope-session; transfer_id=[redacted]; $projected" ''
            Check "$raw; $projected; range_fingerprint=x; range_fingerprint=x" ''
            Check 'session_id=[redacted]; transfer_id=[redacted]' 'native_frontier_scope_unavailable'
            foreach($missing in @('session_id','transfer_id')) {
                Check (($raw -split '; ' | Where-Object {-not $_.StartsWith($missing+'=')}) -join '; ') 'native_frontier_scope_unavailable'
            }
            foreach($sentinel in @('', '(none)', '[redacted]', 'prefix[redacted]', 'sess_[redacted]')) {
                Check "session_id=$sentinel; transfer_id=scope-transfer" 'native_frontier_scope_unavailable'
            }
            foreach($key in @('session_id','transfer_id','frontier_scope_session_id','frontier_scope_transfer_id')) {
                $value=if($key -like 'frontier_*') {$session} else {'scope-session'}
                Check "$raw; $projected; ${key}=$value" 'native_frontier_duplicate_scope'
                Check "$raw; $projected; $($key.ToUpperInvariant())=foreign" 'native_frontier_duplicate_scope'
            }
            foreach($key in @('frontier_scope_session_id','frontier_scope_transfer_id')) {
                $other=($projected -split '; ' | Where-Object {-not $_.StartsWith($key+'=')}) -join '; '
                Check "$raw; $other" 'native_frontier_scope_invalid'
                foreach($bad in @('', '(none)', ('fte2-'+('0'*24)), ('fte1-'+('A'*24)), ('fte1-'+('0'*23)), ('fte1-'+('0'*25)), ($session+'-suffix'))) {
                    Check "$raw; $other; ${key}=$bad" 'native_frontier_scope_invalid'
                }
            }
            Check "session_id=other; transfer_id=scope-transfer; $projected" 'native_frontier_scope_conflict'
            Check "session_id=scope-session; transfer_id=other; $projected" 'native_frontier_scope_conflict'
            Check "session_id=prefix[redacted]; transfer_id=[redacted]; $projected" 'native_frontier_scope_unavailable'
            Check "session_id=(none); transfer_id=[redacted]; $projected" 'native_frontier_scope_unavailable'
            Check "$raw; $projected`nsecret=x" 'native_frontier_scope_invalid'
            $foreignSession=ConvertTo-FileTransferCoordinatorEvidenceToken 'foreign-session'
            $foreignTransfer=ConvertTo-FileTransferCoordinatorEvidenceToken 'foreign-transfer'
            Check "session_id=[redacted]; transfer_id=[redacted]; frontier_scope_session_id=$foreignSession; frontier_scope_transfer_id=$transfer" '' $foreignSession $transfer
            Check "session_id=[redacted]; transfer_id=[redacted]; frontier_scope_session_id=$session; frontier_scope_transfer_id=$foreignTransfer" '' $session $foreignTransfer
            $literal=ConvertTo-FileTransferCoordinatorEvidenceToken $session
            Check "session_id=$session; transfer_id=scope-transfer" '' $literal $transfer
            # Diagnostic partition only, not a constructed native witness.
            $rows=@($raw, "$raw; $projected", "session_id=[redacted]; transfer_id=[redacted]; $projected",
                "session_id=[redacted]; transfer_id=[redacted]; frontier_scope_session_id=$session; frontier_scope_transfer_id=$foreignTransfer")
            $scopes=@($rows | ForEach-Object {Read-FileTransferNativeFrontierScope $_ (ConvertFrom-GuiSmokeLogLedgerStructuredFields $_)})
            if(@($scopes | Where-Object {$_.Error -ceq '' -and $_.Session -ceq $session -and $_.Transfer -ceq $transfer}).Count -ne 3 -or
                @($scopes | Where-Object {$_.Error -ceq '' -and $_.Session -ceq $session -and $_.Transfer -ceq $foreignTransfer}).Count -ne 1) {throw 'Mixed legacy/projected partition failed'}
            'scope-reader-checks='+$checks
            """, [root], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("scope-reader-checks=", result.Stdout);
    }
}
