param([string]$RepoRoot,[string]$Directory)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
# Execute the actual fixture tails; do not duplicate their guards or evaluate authority.
$relay=[IO.File]::ReadAllText((Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierRelayAnalysis.ps1'))
$start=$relay.IndexOf('    $expectedStatus=')
$end=$relay.IndexOf('} finally {Set-Location')
if($start -lt 0 -or $end -le $start) {throw 'Relay guard seam changed'}
$tail=[scriptblock]::Create($relay.Substring($start,$end-$start))
$cases=@('fallback','source-profile','toy','ordinary','owner','status','input','hash','hash-passes','line-passes','launches',
    'native','origins','current-custody','open-pin','evaluations','payload-marker','payload-pin')
foreach($fault in $cases) {
    & {
        $env:NLINK_TEST_PAYLOAD_PRIMARY_CASE=switch($fault) {'source-profile' {'canonical-source'} 'toy' {'valid'} 'ordinary' {''} default {'canonical-fallback'}}
        $receipt=[pscustomobject]@{owner='RouteAcceptance';status='completed';analyzerInputUnchanged=$true;analyzerInputSha256Before='expected';
            completedProofArtifacts=$true;coordinatorSnapshotMode='SnapshotV1Authoritative';exitCode=0;exitCodeAvailable=$true;timedOut=$false}
        $analysisFailure=''; $expectedHash='expected'; $Case='analysis'
        $script:relayHashes=2; $script:relayCounts=2; $script:relayLaunches=1
        $observed=[pscustomobject]@{native=$true;origins=@(5,8)}
        $script:relayPreparation=[pscustomobject]@{IsCurrent=$false}
        $script:relayPrimaryPins=@([pscustomobject]@{IsClosed=$true})
        $script:primaryPayloadHandles=@([pscustomobject]@{IsClosed=$true})
        $output="original parent analyzer evaluations=3 nativeReads=1`nattempt-payload-primary-checked=$env:NLINK_TEST_PAYLOAD_PRIMARY_CASE`nselected_failure=(none); diagnostic_witness=unavailable"
        $expected='Offline canonical-fallback verification stopped before route-cycle acceptance'
        switch($fault) {
            'source-profile' {$expected='Offline canonical-source verification stopped before route-cycle acceptance'}
            'toy' {$receipt.status='proof_incomplete';$receipt.completedProofArtifacts=$false;$receipt.coordinatorSnapshotMode='LegacyV0';$analysisFailure="FileTransfer-Ops retained analysis exited successfully without complete proof artifacts for $Directory";$expected=$analysisFailure}
            'ordinary' {$expected=''}
            'owner' {$receipt.owner='foreign'}
            'status' {$receipt.status='proof_incomplete'}
            'input' {$receipt.analyzerInputUnchanged=$false}
            'hash' {$receipt.analyzerInputSha256Before='changed'}
            'hash-passes' {$script:relayHashes=3}
            'line-passes' {$script:relayCounts=3}
            'launches' {$script:relayLaunches=2}
            'native' {$observed.native=$false}
            'origins' {$observed.origins=@(0)}
            'current-custody' {$script:relayPreparation.IsCurrent=$true}
            'open-pin' {$script:relayPrimaryPins[0].IsClosed=$false}
            'evaluations' {$output=$output.Replace('evaluations=3','evaluations=4')}
            'payload-marker' {$output=$output.Replace('primary-checked','primary-missing')}
            'payload-pin' {$script:primaryPayloadHandles[0].IsClosed=$false}
        }
        [IO.File]::WriteAllText((Join-Path $Directory 'filetransfer-retained-analysis.stdout.txt'),$output)
        $actual=''; try {& $tail} catch {$actual=$_.Exception.Message}
        $valid=$fault -cin @('fallback','source-profile','toy','ordinary')
        if(($valid -and $actual -cne $expected) -or (-not $valid -and (-not $actual -or $actual -ceq $expected))) {throw ('Relay guard outcome changed: '+$fault+' / '+$actual)}
    }
}
$dispatch=[IO.File]::ReadAllText((Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatch.ps1'))
$start=$dispatch.IndexOf("if(`$PrimaryPayloadCase -ceq 'canonical-source')")
if($start -lt 0) {throw 'Dispatch guard seam changed'}
$tail=[scriptblock]::Create($dispatch.Substring($start))
$dispatchCases=@('fallback','source-profile','toy','fallback-incomplete','fallback-source','fallback-empty','source-fallback','toy-fallback',
    'analysis-count','publication','run-identity','scenario-identity')
foreach($fault in $dispatchCases) {
    & {
        $PrimaryPayloadCase='canonical-fallback'; $Case='analysis'; $expectedFailure=$true
        $failure='Offline canonical-fallback verification stopped before route-cycle acceptance'
        $script:analysisCalls=1; $publication=$null
        $beforeRun=$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID; $beforeScenario=$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID
        switch($fault) {
            'source-profile' {$PrimaryPayloadCase='canonical-source';$failure='Offline canonical-source verification stopped before route-cycle acceptance'}
            'toy' {$PrimaryPayloadCase='valid';$failure="FileTransfer-Ops retained analysis exited successfully without complete proof artifacts for $Directory"}
            'fallback-incomplete' {$failure="FileTransfer-Ops retained analysis exited successfully without complete proof artifacts for $Directory"}
            'fallback-source' {$failure='Offline canonical-source verification stopped before route-cycle acceptance'}
            'fallback-empty' {$failure=''}
            'source-fallback' {$PrimaryPayloadCase='canonical-source'}
            'toy-fallback' {$PrimaryPayloadCase='valid'}
            'analysis-count' {$script:analysisCalls=2}
            'publication' {$publication=[pscustomobject]@{Accepted=$true}}
            'run-identity' {$beforeRun='changed-run'}
            'scenario-identity' {$beforeScenario='changed-scenario'}
        }
        $rejected=$false; try {& $tail} catch {$rejected=$true}
        if($rejected -ne ($fault -cnotin @('fallback','source-profile','toy'))) {throw ('Dispatch guard outcome changed: '+$fault)}
    }
}
'canonical-receipt-guard-controls='+($cases.Count+$dispatchCases.Count)
