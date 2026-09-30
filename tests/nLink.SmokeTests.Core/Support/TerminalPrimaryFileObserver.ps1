# Opt-in test seam: use the original attached source worker and its cleanup.
$source=(Get-Command Invoke-FileTransferRetainedAnalysis).ScriptBlock.ToString()
$receive='if ($null -ne $retainedSourceOwner) { $retainedSourceReader = $retainedSourceOwner.TryOpenSource() }'
$cleanup='if ($null -ne $retainedSourceOwner) { $retainedSourceOwner.Dispose() }'
foreach($needle in @($receive,$cleanup)) {
    if($source.Split([string[]]@($needle),[StringSplitOptions]::None).Length -ne 2) {throw 'Original terminal-file source seam changed'}
}
$adoption=@'
if ($null -ne $retainedSourceOwner) {
    try {
        $retainedSourceOwner.ReceiveTerminalFilesForTest($env:NLINK_TEST_NATIVE_PRIMARY_LOG,($env:NLINK_TEST_COLLECTOR_PRIMARY -ceq '1'))
        $repeated=$false
        try {$retainedSourceOwner.ReceiveTerminalFilesForTest($env:NLINK_TEST_NATIVE_PRIMARY_LOG)}
        catch {if($_.Exception.ToString().Contains('native_primary_receive_repeated_or_stale')) {$repeated=$true} else {throw}}
        if(-not $repeated) {throw 'Native primary accepted repeated adoption'}
        if($env:NLINK_TEST_COLLECTOR_PRIMARY -ceq '1') {
            $copiedOwner=[object].GetMethod('MemberwiseClone',[Reflection.BindingFlags]'Instance,NonPublic').Invoke($retainedSourceOwner,@())
            if($null -ne $copiedOwner.TryOpenCollectorSourceForTest()) {throw 'Copied worker opened collector source'}
            $retainedSourceReader=$retainedSourceOwner.TryOpenCollectorSourceForTest()
            if($null -eq $retainedSourceReader -or $null -ne $retainedSourceOwner.TryOpenCollectorSourceForTest()) {throw 'Collector source read not one-shot'}
        } else {$retainedSourceReader = $retainedSourceOwner.TryOpenSource()}
    } catch {
        [IO.File]::WriteAllText((Join-Path $resolvedArtifactDir 'native-primary-adoption-error.txt'),[string]$_)
        throw
    }
}
'@
$release=@'
if ($null -ne $retainedSourceOwner) {
    $private:terminalFiles=$retainedSourceOwner.TerminalFilesForTest
    $private:collectorFiles=if($env:NLINK_TEST_COLLECTOR_PRIMARY -ceq '1') {$retainedSourceOwner.CollectorOutputsForTest} else {$null}
    $private:collectorMembershipIssued=$env:NLINK_TEST_COLLECTOR_MEMBERSHIP -ceq '1' -and $retainedSourceOwner.CollectorMembershipWasIssuedForTest
    $private:collectorOriginJoinIssued=$env:NLINK_TEST_COLLECTOR_MEMBERSHIP -ceq '1' -and $retainedSourceOwner.CollectorOriginJoinWasIssuedForTest
    $private:payloadIssued=$retainedSourceOwner.TerminalPayloadJoinWasIssuedForTest
    $private:integrityConsumed=$retainedSourceOwner.TerminalIntegrityWasConsumedForTest
    $retainedSourceOwner.CloseTerminalFilesForTest()
    if($null -ne $collectorFiles) {
        if($collectorFiles.IsCurrent) {throw 'Collector primary files survived cleanup'}
        if($null -ne $collectorFiles.DescribeLineOriginForTest($retainedSourceReader,1)) {throw 'Collector origins survived owner cleanup'}
        [IO.File]::WriteAllText((Join-Path $resolvedArtifactDir 'collector-primary-cleanup.json'),
            '{"FourOutputsDisposed":true,"RepeatedSourceOpenRejected":true,"CopiedWorkerSourceOpenRejected":true,"ClosedOriginQueryRejected":true,"AcceptanceChanged":false}')
    }
    if($null -ne $private:terminalFiles) {
        if($private:terminalFiles.IsCurrent) {throw 'Native primary files survived cleanup'}
        [IO.File]::WriteAllText((Join-Path $resolvedArtifactDir 'native-primary-cleanup.json'),
            '{"AdoptedFilesDisposed":true,"RepeatedAdoptionRejected":true,"AcceptanceChanged":false}')
    } else {
        [IO.File]::WriteAllText((Join-Path $resolvedArtifactDir 'native-primary-rejection-counters.json'),
            (@{Evaluations=$script:livePrimaryEvaluations;Adopted=$false} | ConvertTo-Json -Compress))
    }
    $retainedSourceOwner.Dispose()
    if($env:NLINK_TEST_PRE_VERDICT_INTEGRITY -ceq '1') {
        if(-not $retainedSourceOwner.TerminalIntegrityRetiredForTest) {throw 'Terminal integrity survived cleanup'}
        [IO.File]::WriteAllText((Join-Path $resolvedArtifactDir 'terminal-integrity-cleanup.json'),
            (@{WasConsumed=$private:integrityConsumed;Retired=$true;AcceptanceChanged=$false}|ConvertTo-Json -Compress))
    }
    if($env:NLINK_TEST_TERMINAL_PAYLOAD_JOIN -ceq '1') {
        if(-not $retainedSourceOwner.TerminalPayloadJoinRetiredForTest) {throw 'Terminal payload join survived cleanup'}
        [IO.File]::WriteAllText((Join-Path $resolvedArtifactDir 'terminal-payload-cleanup.json'),
            (@{HadJoin=$private:payloadIssued;JoinRetired=$true;AcceptanceChanged=$false}|ConvertTo-Json -Compress))
    }
    if($env:NLINK_TEST_COLLECTOR_MEMBERSHIP -ceq '1') {
        if($null -ne $retainedSourceOwner.CollectorMembershipResultForTest -or $retainedSourceOwner.CollectorWriterVerifiedForTest) {throw 'Collector membership survived cleanup'}
        if(-not $retainedSourceOwner.CollectorOriginJoinRetiredForTest) {throw 'Collector origin join survived cleanup'}
        [IO.File]::WriteAllText((Join-Path $resolvedArtifactDir 'collector-membership-cleanup.json'),
            (@{HadOwnedMembership=$collectorMembershipIssued;HadOriginJoin=$collectorOriginJoinIssued;OriginJoinRetired=$true;
                MembershipRetired=$true;WriterClosed=$true;AcceptanceChanged=$false}|ConvertTo-Json -Compress))
    }
}
'@
$source=$source.Replace($receive,$adoption).Replace($cleanup,$release)
$source=$source.Replace('$PSScriptRoot',("'"+(Join-Path $repoRoot 'tools/FileTransferOps').Replace("'","''")+"'"))
Set-Item Function:Invoke-FileTransferRetainedAnalysis -Value ([scriptblock]::Create($source))
