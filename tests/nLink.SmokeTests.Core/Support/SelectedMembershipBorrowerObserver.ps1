param([string]$Primary,[string]$CleanupSeam)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
function Replace-One([string]$Text,[string]$Needle,[string]$Replacement) {
    if($Text.Split([string[]]@($Needle),[StringSplitOptions]::None).Length -ne 2) {throw 'borrower_unique_seam'}
    return $Text.Replace($Needle,$Replacement)
}
$witness=[IO.File]::ReadAllText((Join-Path $repoRoot 'tools/FileTransferOps/LocalPrimaryPublicationWitness.ps1'))
$seam='$Context.WriterReader = $writer; $matched = $true'
$retain=@'
$Context.WriterReader = $writer; $matched = $true
        try {
            if($env:NLINK_TEST_MEMBERSHIP_BORROWER_CASE -cne 'unavailable') {
                [void]$Context.SourceOwner.BindMembershipBorrowerForTest($writer,$membership,$Context,$Index,$Failure,$work.records,$work.codeUnits)
            }
        }
        catch { } # Test observation cannot change original witness preparation.
'@
$witness=Replace-One $witness $seam $retain
if($env:NLINK_TEST_SELECTED_NATIVE_WRITER -ceq '1') {
    $witness=Replace-One $witness '$Context.SourceOwner.TryOpenWriter()' `
        '$Context.SourceOwner.TryOpenNativeWitnessWriterForTest($Context.SourceOwner.CollectorOutputsForTest,$Context.SourceReader,$Context,$Context.Work)'
}
$witness=$witness.Replace('$PSScriptRoot',("'"+(Join-Path $repoRoot 'tools/FileTransferOps').Replace("'","''")+"'"))
$load='. (Join-Path $PSScriptRoot ''LocalPrimaryPublicationWitness.ps1'')'
$Primary=Replace-One $Primary $load ('. ([scriptblock]::Create('''+$witness.Replace("'","''")+'''))')
$probePath=(Join-Path $PSScriptRoot 'SelectedMembershipBorrowerProbe.ps1').Replace("'","''")
$probe="& ([scriptblock]::Create([IO.File]::ReadAllText('$probePath'))) -Context `$private:localPrimaryInputs -Custody `$private:localPrimaryCustody -Pair `$private:localCompletedEvaluations -Index `$Index -Failure `$firstCompactProofChainFailure -Witness `$preparedLocalFailureWitness -Prepared `$private:localPublicationWitness -Completion `$private:localPublicationCompletion -ArtifactDir `$ArtifactDir -Evaluations `$script:livePrimaryEvaluations"
if($env:NLINK_TEST_SELECTED_ORIGIN_JOIN -ceq '1') {
    if($env:NLINK_TEST_SELECTED_NATIVE_WRITER -cne '1') {throw 'selected_origin_requires_native_profile'}
    $originPath=(Join-Path $PSScriptRoot 'SelectedCollectorOriginProbe.ps1').Replace("'","''")
    $probe+=" -OriginProbe ([scriptblock]::Create([IO.File]::ReadAllText('$originPath')))"
}
if($env:NLINK_TEST_AUTHENTICATED_PAYLOAD_HANDOFF -ceq '1') {
    $payloadPath=(Join-Path $PSScriptRoot 'SelectedPayloadOwnerProbe.ps1').Replace("'","''")
    $probe+=" -OriginProbe ([scriptblock]::Create([IO.File]::ReadAllText('$payloadPath')))"
}
$probe='try { '+$probe+' } catch { [IO.File]::WriteAllText((Join-Path $ArtifactDir ''membership-borrower-error.txt''),[string]$_ + "`n" + $_.ScriptStackTrace) }'
$Primary=Replace-One $Primary $CleanupSeam ($probe+"`n        "+$CleanupSeam)
$source=(Get-Command Invoke-FileTransferRetainedAnalysis).ScriptBlock.ToString()
if($env:NLINK_TEST_SELECTED_NATIVE_WRITER -ceq '1') {
    $receive='if ($null -ne $retainedSourceOwner) { $retainedSourceReader = $retainedSourceOwner.TryOpenSource() }'
    $adopt=@'
if ($null -ne $retainedSourceOwner) {
    try {
        $payloadHandoff=$env:NLINK_TEST_AUTHENTICATED_PAYLOAD_HANDOFF -ceq '1'
        if($payloadHandoff) {$retainedSourceOwner.ReceiveTerminalFilesForTest($env:NLINK_TEST_NATIVE_PRIMARY_LOG,$true)}
        else {$retainedSourceOwner.ReceiveSeededCollectorForTest()}
        $repeated=$false
        try {
            if($payloadHandoff) {$retainedSourceOwner.ReceiveTerminalFilesForTest($env:NLINK_TEST_NATIVE_PRIMARY_LOG,$true)}
            else {$retainedSourceOwner.ReceiveSeededCollectorForTest()}
        }
        catch {
            $expected=if($payloadHandoff) {'native_primary_receive_repeated_or_stale'} else {'seeded_primary_receive_state'}
            if($_.Exception.ToString().Contains($expected)) {$repeated=$true} else {throw}
        }
        if(-not $repeated -or ($null -ne $retainedSourceOwner.TerminalFilesForTest) -ne $payloadHandoff) {throw 'seeded_adoption_scope'}
        $copy=[object].GetMethod('MemberwiseClone',[Reflection.BindingFlags]'Instance,NonPublic').Invoke($retainedSourceOwner,@())
        if($null -ne $copy.TryOpenCollectorSourceForTest()) {throw 'seeded_copied_source_open'}
        $retainedSourceReader=$retainedSourceOwner.TryOpenCollectorSourceForTest()
        if($null -eq $retainedSourceReader -or $null -ne $retainedSourceOwner.TryOpenCollectorSourceForTest()) {throw 'seeded_source_not_one_shot'}
    } catch {
        [IO.File]::WriteAllText((Join-Path $resolvedArtifactDir 'selected-native-adoption-error.txt'),[string]$_+"`n"+$_.ScriptStackTrace)
        throw
    }
}
'@
    $source=Replace-One $source $receive $adopt
}
$cleanup='if ($null -ne $retainedSourceOwner) { $retainedSourceOwner.Dispose() }'
$after=@'
if ($null -ne $retainedSourceOwner) {
    $hadBorrower=$retainedSourceOwner.MembershipBorrowerIssuedForTest
    if(-not $retainedSourceOwner.MembershipBorrowerRetiredForTest) {throw 'borrower_survived_context_cleanup'}
    if($env:NLINK_TEST_SELECTED_ORIGIN_JOIN -ceq '1') {
        $originIssued=$retainedSourceOwner.SelectedOriginIssuedForTest
        if(-not $retainedSourceOwner.SelectedOriginRetiredForTest) {throw 'selected_origin_survived_context_cleanup'}
    }
    if($env:NLINK_TEST_SELECTED_NATIVE_WRITER -ceq '1') {
        $files=$retainedSourceOwner.CollectorOutputsForTest
        $payloadHandoff=$env:NLINK_TEST_AUTHENTICATED_PAYLOAD_HANDOFF -ceq '1'
        $payloadFiles=$retainedSourceOwner.TerminalFilesForTest
        if($null -eq $files -or ($null -ne $payloadFiles) -ne $payloadHandoff) {throw 'seeded_cleanup_owner'}
        if($payloadHandoff -and -not $retainedSourceOwner.SelectedPayloadOwnerRetiredForTest) {throw 'selected_payload_survived_context_cleanup'}
        if($env:NLINK_TEST_SELECTED_TERMINAL_CASE -and -not $retainedSourceOwner.SelectedTerminalsRetiredForTest) {throw 'selected_terminals_survived_context_cleanup'}
        $retainedSourceOwner.CloseTerminalFilesForTest()
        if($files.IsCurrent -or ($payloadHandoff -and $payloadFiles.IsCurrent)) {throw 'seeded_collector_survived_cleanup'}
        [IO.File]::WriteAllText((Join-Path $resolvedArtifactDir 'selected-native-cleanup.json'),
            (@{FourOutputsDisposed=$true;RepeatedAdoptionRejected=$true;RepeatedSourceOpenRejected=$true;NoTerminalOwner=(-not $payloadHandoff)
                PayloadFilesDisposed=$payloadHandoff;PayloadOwnerRetired=$payloadHandoff
                SelectedTerminalsRetired=[bool]$env:NLINK_TEST_SELECTED_TERMINAL_CASE}|ConvertTo-Json -Compress))
    }
    $retainedSourceOwner.Dispose()
    if(-not $retainedSourceOwner.MembershipBorrowerRetiredForTest) {throw 'borrower_survived_worker_cleanup'}
    if($env:NLINK_TEST_SELECTED_ORIGIN_JOIN -ceq '1') {
        if(-not $retainedSourceOwner.SelectedOriginRetiredForTest) {throw 'selected_origin_survived_worker_cleanup'}
        [IO.File]::WriteAllText((Join-Path $resolvedArtifactDir 'selected-origin-cleanup.json'),
            (@{Issued=$originIssued;RetiredAfterContext=$true;RetiredAfterWorker=$true;AcceptanceChanged=$false}|ConvertTo-Json -Compress))
    }
    [IO.File]::WriteAllText((Join-Path $resolvedArtifactDir 'membership-borrower-cleanup.json'),
        (@{Issued=$hadBorrower;RetiredAfterContext=$true;RetiredAfterWorker=$true;AcceptanceChanged=$false}|ConvertTo-Json -Compress))
}
'@
$source=Replace-One $source $cleanup $after
$source=$source.Replace('$PSScriptRoot',("'"+(Join-Path $repoRoot 'tools/FileTransferOps').Replace("'","''")+"'"))
[pscustomobject]@{Primary=$Primary;Source=$source}
