param([string]$RepoRoot,[string]$Directory,[string]$Case)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$env:NLINK_TEST_PAYLOAD_PRIMARY_CASE='canonical-tail-late-publication'
$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID='phase-checkpoint-control'
$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID='phase-checkpoint-control:1'
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchProjection.ps1')
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierPrimaryPhaseCheckpoints.ps1')
$support=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierPrimaryCheckpoints.ps1'
. $support $Directory
$path=Join-Path $Directory 'test-primary-checkpoints.ndjson'
$stages=@('started','compact-enter','arrays-ready','origins-ready','registration-start','registration-end',
    'full-start','full-end','compact-start','compact-end','payloads-ready','parity-ready','pre-correlator','finished')
function Assert-PhaseRecords([string[]]$Expected) {
    $bytes=[IO.File]::ReadAllBytes($path)
    $lines=[IO.File]::ReadAllLines($path,[Text.UTF8Encoding]::new($false,$true))
    if($bytes.Length -gt 131072 -or $bytes[-1] -ne 10 -or $lines.Length -gt 16 -or $lines.Length -ne $Expected.Length) {throw 'Phase checkpoint bounds/prefix changed'}
    $elapsed=-1L
    for($i=0;$i -lt $lines.Length;$i++) {
        $row=$lines[$i] | ConvertFrom-Json
        if([Text.Encoding]::UTF8.GetByteCount($lines[$i]+"`n") -gt 8192 -or $row.schemaVersion -ne 1 -or
            -not $row.diagnosticOnly -or $row.processId -ne $PID -or $row.sequence -ne $i+1 -or
            $row.acceptanceRunId -cne 'phase-checkpoint-control' -or $row.scenarioRunId -cne 'phase-checkpoint-control:1' -or
            $row.stage -cne $Expected[$i] -or $row.elapsedMilliseconds -lt $elapsed) {throw 'Phase checkpoint metadata/order changed'}
        $null=[DateTimeOffset]::Parse($row.timestampUtc)
        $elapsed=$row.elapsedMilliseconds
    }
}
if($Case -ceq 'projection') {
    $ast=Read-AttemptAst (Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1')
    $original=Get-AttemptFunction $ast 'Write-FileTransferCompactRetainedAnalysisArtifacts'
    . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadPrimaryGuards.ps1')
    . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierLatePublicationProbeGuards.ps1')
    $guarded=Add-FrontierLatePublicationProbeGuards (Add-AttemptPayloadPrimaryProbeGuards $original) -CanonicalTail
    foreach($withGuards in @($false,$true)) {
        $source=if($withGuards) {$guarded} else {$original}
        $projected=Add-FrontierPrimaryPhaseCheckpoints $original
        if($withGuards) {$projected=Add-FrontierLatePublicationProbeGuards (Add-AttemptPayloadPrimaryProbeGuards $projected) -CanonicalTail}
        $restored=[regex]::Replace($projected,"(?m)^Write-FrontierPrimaryCheckpoint '[a-z-]+' 'original phase boundary' # fixture-primary-phase\r?\n",'')
        $tokens=$null; $errors=$null
        $null=[Management.Automation.Language.Parser]::ParseInput($projected,[ref]$tokens,[ref]$errors)
        if($errors.Count -or $restored -cne $source -or [regex]::Matches($projected,'# fixture-primary-phase').Count -ne 12) {throw 'Original phase projection changed statements or syntax'}
    }
    $caught=$false
    try {$null=Add-FrontierPrimaryPhaseCheckpoints ($original.Replace('    $snapshotEvaluation = $compactSnapshotEvaluation','    $snapshotEvaluation = $null'))} catch {$caught=$true}
    if(-not $caught) {throw 'Changed original seam was accepted'}
    foreach($profile in @('canonical-fallback','canonical-source','valid','CANONICAL-TAIL-LATE-PUBLICATION','')) {
        $env:NLINK_TEST_PAYLOAD_PRIMARY_CASE=$profile
        if((Add-FrontierPrimaryPhaseCheckpoints $original) -cne $original) {throw 'Phase projection escaped exact tail profile'}
    }
    $workerAst=Read-AttemptAst (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierCanonicalTailPrimaryWorker.ps1')
    Invoke-Expression (Get-AttemptFunction $workerAst 'Get-CanonicalTailPrimaryWorkerForTest')
    $worker=Get-CanonicalTailPrimaryWorkerForTest $RepoRoot
    $prefix=$worker.Substring(0,$worker.IndexOf('# The outer source contains its own param block.'))
    foreach($profile in @('canonical-fallback','canonical-tail-late-publication','canonical-source','valid','CANONICAL-TAIL-LATE-PUBLICATION','')) {
        $target=Join-Path $Directory ('profile-'+[Array]::IndexOf(@('canonical-fallback','canonical-tail-late-publication','canonical-source','valid','CANONICAL-TAIL-LATE-PUBLICATION',''),$profile))
        [void][IO.Directory]::CreateDirectory($target)
        [IO.File]::WriteAllText((Join-Path $target 'parent-case.json'),(@{repoRoot=$RepoRoot;scenario='merged-native'} | ConvertTo-Json -Compress))
        $env:NLINK_TEST_PAYLOAD_PRIMARY_CASE=$profile
        . ([scriptblock]::Create($prefix)) -ArtifactDir $target
        $tokens=$null; $errors=$null
        $null=[Management.Automation.Language.Parser]::ParseInput($text,[ref]$tokens,[ref]$errors)
        if($errors.Count -or [regex]::Matches($text,'\$profile\.Invoke\(\$inputs,\$null\)').Count -ne 1 -or
            [regex]::Matches($text,'& \$realParser').Count -ne 2 -or [regex]::Matches($text,'& \$realEvaluator').Count -ne 1) {throw 'Original reader/evaluator projection changed'}
        if($text.Contains("Write-FrontierPrimaryCheckpoint 'source-origins'") -ne ($profile -ceq 'canonical-fallback')) {throw 'Existing origin observation projection changed'}
        if((Test-Path -LiteralPath (Join-Path $target 'test-primary-checkpoints.ndjson')) -ne ($profile -cin @('canonical-fallback','canonical-tail-late-publication'))) {throw 'Checkpoint launch scope changed'}
    }
} elseif($Case -ceq 'worker-loading') {
    $modulePath=Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1'
    $moduleAst=Read-AttemptAst $modulePath
    $original=Get-AttemptFunction $moduleAst 'Write-FileTransferCompactRetainedAnalysisArtifacts'
    $anchor='    $snapshotCatalog = Get-FileTransferCoordinatorEvidenceCatalog'
    if(($original.Split(@($anchor),[StringSplitOptions]::None)).Count -ne 2 -or
        ($moduleAst.EndBlock.Extent.Text.Split(@($anchor),[StringSplitOptions]::None)).Count -ne 3) {throw 'Function/parent scope distinction changed'}
    $caught=$false
    try {$null=Add-FrontierPrimaryPhaseCheckpoints $moduleAst.EndBlock.Extent.Text} catch {
        if($_.Exception.Message -cne ('Attempt seam changed: '+$anchor)) {throw}
        $caught=$true
    }
    if(-not $caught) {throw 'Broad parent scope bypassed strict duplicate-anchor rejection'}
    $workerAst=Read-AttemptAst (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierCanonicalTailPrimaryWorker.ps1')
    Invoke-Expression (Get-AttemptFunction $workerAst 'Get-CanonicalTailPrimaryWorkerForTest')
    $baseWorker=[IO.File]::ReadAllText((Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadPrimaryWorker.ps1'))
    $profiles=@('canonical-fallback','canonical-tail-late-publication','canonical-source','valid','CANONICAL-TAIL-LATE-PUBLICATION','')
    for($i=0;$i -lt $profiles.Count;$i++) {
        $profile=$profiles[$i]; $env:NLINK_TEST_PAYLOAD_PRIMARY_CASE=$profile
        $target=Join-Path $Directory ('profile-'+$i); [void][IO.Directory]::CreateDirectory($target)
        [IO.File]::WriteAllText((Join-Path $target 'parent-case.json'),(@{repoRoot=$RepoRoot;scenario='merged-native'} | ConvertTo-Json -Compress))
        $worker=if($profile -ceq 'canonical-tail-late-publication') {Get-CanonicalTailPrimaryWorkerForTest $RepoRoot} else {$baseWorker}
        $prefix=$worker.Substring(0,$worker.IndexOf('# The outer source contains its own param block.'))
        . ([scriptblock]::Create($prefix)) -ArtifactDir $target
        $tokens=$null; $errors=$null
        $loadedAst=[Management.Automation.Language.Parser]::ParseInput($text,[ref]$tokens,[ref]$errors)
        $selector=@($loadedAst.EndBlock.Statements | Where-Object {
            $_ -is [Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -ceq '$definition' -and
            $_.Right.Extent.Text.Contains('Get-Command Write-FileTransferCompactRetainedAnalysisArtifacts')})
        $invoke=@($loadedAst.EndBlock.Statements | Where-Object {$_.Extent.Text -ceq 'Invoke-Expression $definition'})
        $loadLine=". (Join-Path `$RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1')"
        $start=$text.IndexOf($loadLine,[StringComparison]::Ordinal)
        if($errors.Count -or $selector.Count -ne 1 -or $invoke.Count -ne 1 -or $start -lt 0 -or
            $text.LastIndexOf($loadLine,[StringComparison]::Ordinal) -ne $start -or
            $selector[0].Extent.EndOffset -ge $invoke[0].Extent.StartOffset) {throw 'Actual worker loading hook changed'}
        # Execute the actual module-load and selector, not the retained analyzer body.
        Invoke-Expression $text.Substring($start,$selector[0].Extent.EndOffset-$start)
        if($definition -cne $original) {throw 'Actual worker selected more than the intended function'}
        $selectedAst=[Management.Automation.Language.Parser]::ParseInput($definition,[ref]$tokens,[ref]$errors)
        if($errors.Count -or $selectedAst.EndBlock.Statements.Count -ne 1 -or
            $selectedAst.EndBlock.Statements[0] -isnot [Management.Automation.Language.FunctionDefinitionAst] -or
            $selectedAst.EndBlock.Statements[0].Name -cne 'Write-FileTransferCompactRetainedAnalysisArtifacts') {throw 'Selected scope is not exactly one function'}
        $expected=$original.Replace('$PSScriptRoot',("'"+(Join-Path $RepoRoot 'tools/FileTransferOps').Replace("'","''")+"'"))
        $expected=Add-AttemptPayloadPrimaryProbeGuards $expected
        if($profile -ceq 'canonical-tail-late-publication') {
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierLatePublicationProbeGuards.ps1')
            $expected=Add-FrontierLatePublicationProbeGuards $expected -CanonicalTail
        }
        Invoke-Expression $text.Substring($selector[0].Extent.EndOffset,$invoke[0].Extent.EndOffset-$selector[0].Extent.EndOffset)
        $restored=[regex]::Replace($definition,"(?m)^Write-FrontierPrimaryCheckpoint '[a-z-]+' 'original phase boundary' # fixture-primary-phase\r?\n",'')
        $markers=[regex]::Matches($definition,'# fixture-primary-phase').Count
        $expectedMarkers=if($profile -ceq 'canonical-tail-late-publication') {12} else {0}
        if($restored -cne $expected -or $markers -ne $expectedMarkers -or
            (Get-Command Write-FileTransferCompactRetainedAnalysisArtifacts).ScriptBlock.Ast.Extent.Text -cne $definition) {throw 'Actual hook changed original statements or guard/profile scope'}
        $checkpoint=Join-Path $target 'test-primary-checkpoints.ndjson'
        $enabled=$profile -cin @('canonical-fallback','canonical-tail-late-publication')
        if((Test-Path -LiteralPath $checkpoint) -ne $enabled) {throw 'Actual worker checkpoint launch scope changed'}
        if($enabled) {
            $rows=@([IO.File]::ReadAllLines($checkpoint)|ForEach-Object {$_|ConvertFrom-Json})
            if($rows.Count -ne 1 -or $rows[0].stage -cne 'started' -or -not $rows[0].diagnosticOnly -or
                $rows[0].processId -ne $PID -or $rows[0].sequence -ne 1 -or
                $rows[0].acceptanceRunId -cne 'phase-checkpoint-control' -or $rows[0].scenarioRunId -cne 'phase-checkpoint-control:1') {throw 'Loading invoked work or changed exact diagnostic correlation'}
        }
        $files=@(Get-ChildItem -LiteralPath $target -File)
        if($files.Count -ne $(if($enabled){2}else{1}) -or $script:primaryPayloadChecked -or $script:primaryPayloadBound -or $null -ne $script:transferEvaluation) {throw 'Loading executed retained work or emitted proof artifacts'}
        [pscustomobject]@{profile=$profile;selectedFunctionOnly=$true;phaseMarkers=$markers;originalStatementsAndGuardsPreserved=$true;analyzerBodyInvoked=$false;strictDuplicateAnchorRejected=$caught}|ConvertTo-Json -Compress
    }
} elseif($Case -ceq 'inactive') {
    foreach($profile in @('canonical-source','valid','CANONICAL-TAIL-LATE-PUBLICATION','')) {
        $env:NLINK_TEST_PAYLOAD_PRIMARY_CASE=$profile
        Write-FrontierPrimaryCheckpoint 'full-start' 'must not be emitted'
    }
    if(Test-Path -LiteralPath $path) {throw 'Phase sink escaped exact profile'}
} elseif($Case -ceq 'limits') {
    foreach($name in @('count','bytes','existing','mutation','wrong-stage')) {
        $target=Join-Path $Directory $name; [void][IO.Directory]::CreateDirectory($target)
        . $support $target
        $targetPath=Join-Path $target 'test-primary-checkpoints.ndjson'
        switch($name) {
            'count' {
                for($i=0;$i -lt 20;$i++) {Write-FrontierPrimaryCheckpoint 'full-start' ('x'*4096)}
                if([IO.File]::ReadAllLines($targetPath).Length -ne 16 -or (Get-Item -LiteralPath $targetPath).Length -gt 131072) {throw 'Tail count limit changed'}
            }
            'bytes' {Write-FrontierPrimaryCheckpoint 'full-start' ('x'*8192); if(Test-Path -LiteralPath $targetPath) {throw 'Oversized tail record accepted'}}
            'existing' {
                [IO.File]::WriteAllText($targetPath,'existing evidence')
                Write-FrontierPrimaryCheckpoint 'full-start' 'must not overwrite'
                if([IO.File]::ReadAllText($targetPath) -cne 'existing evidence') {throw 'Tail evidence overwritten'}
            }
            'mutation' {
                Write-FrontierPrimaryCheckpoint 'started' 'original'
                [IO.File]::AppendAllText($targetPath,'external mutation')
                $before=[IO.File]::ReadAllText($targetPath)
                Write-FrontierPrimaryCheckpoint 'finished' 'must not append'
                if([IO.File]::ReadAllText($targetPath) -cne $before) {throw 'Mutated tail evidence extended'}
            }
            'wrong-stage' {Write-FrontierPrimaryCheckpoint 'source-profile' 'fallback stage'; if(Test-Path -LiteralPath $targetPath) {throw 'Fallback-only stage enabled for tail'}}
        }
        if(-not $script:primaryCheckpointStopped) {throw 'Tail sink did not fail closed'}
    }
} else {
    $script:full=[string[]]@('full','shared'); $script:compact=[string[]]@('shared')
    $script:fullView=[pscustomobject]@{kind='full'}; $script:compactView=[pscustomobject]@{kind='compact'}
    $script:trace=[Collections.Generic.List[string]]::new()
    $LocalSourceReader=[object]::new(); $script:context=[object]::new()
    $script:fullOrdinals=[int[]]@(1,2); $script:compactOrdinals=[int[]]@(2)
    $LocalSourceOwner=[pscustomobject]@{}
    $LocalSourceOwner | Add-Member ScriptMethod TryBeginFrontierPrimary {
        param($reader,$context,$full,$fullOrigins,$compact,$compactOrigins)
        $script:trace.Add('registration')
        if(-not [object]::ReferenceEquals($reader,$LocalSourceReader) -or -not [object]::ReferenceEquals($context,$script:context) -or
            -not [object]::ReferenceEquals($full,$script:full) -or -not [object]::ReferenceEquals($compact,$script:compact) -or
            -not [object]::ReferenceEquals($fullOrigins,$script:fullOrdinals) -or -not [object]::ReferenceEquals($compactOrigins,$script:compactOrdinals)) {throw 'Original registration arguments changed'}
        if($Case -ceq 'registration-error') {throw 'original registration failure'}
        return [object]::new()
    }
    function Get-FileTransferCoordinatorEvidenceCatalog {$script:trace.Add('catalog'); return 'catalog'}
    function Get-FileTransferCoordinatorSnapshotEvaluation([string[]]$Lines) {
        $kind=if([object]::ReferenceEquals($Lines,$script:full)) {'full'} elseif([object]::ReferenceEquals($Lines,$script:compact)) {'compact'} else {throw 'Original evaluator array changed'}
        $script:trace.Add($kind)
        if($Case -ceq ($kind+'-error')) {throw ('original '+$kind+' failure')}
        if($Case -ceq 'timeout-child' -and $kind -ceq 'full') {
            [IO.File]::WriteAllText((Join-Path $Directory 'phase-pid.txt'),[string]$PID)
            Start-Sleep -Seconds 60
            throw 'Timed-out phase control survived'
        }
        if($kind -ceq 'full') {return $script:fullView} else {return $script:compactView}
    }
    function Compare-FileTransferCoordinatorSnapshotEvaluations($Full,$Compact) {
        $script:trace.Add('parity')
        if(-not [object]::ReferenceEquals($Full,$script:fullView) -or -not [object]::ReferenceEquals($Compact,$script:compactView)) {throw 'Original parity arguments changed'}
        if($Case -ceq 'parity-error') {throw 'original parity failure'}
        return $true
    }
    function Get-PhasePreCorrelatorControl {$script:trace.Add('pre-correlator'); return 'legacy'}
    $definition=@'
function Invoke-PhaseControl {
    try {
    $snapshotCatalog = Get-FileTransferCoordinatorEvidenceCatalog
    $primaryFullSnapshotLines=$script:full; $primaryCompactSnapshotLines=$script:compact
    $private:nativePrimaryContext=$script:context
    $nativeFullOrdinals=$script:fullOrdinals; $nativeCompactOrdinals=$script:compactOrdinals
    if ($null -ne $LocalSourceOwner -and $null -ne $LocalSourceReader) {
                $private:nativePrimaryCustody = $LocalSourceOwner.TryBeginFrontierPrimary($LocalSourceReader,
                    $private:nativePrimaryContext, $primaryFullSnapshotLines, $nativeFullOrdinals,
                    $primaryCompactSnapshotLines, $nativeCompactOrdinals)
    }
    $fullSnapshotEvaluation = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $primaryFullSnapshotLines
    $compactSnapshotEvaluation = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $primaryCompactSnapshotLines
    # Only an admitted bounded private context retains these array references.
    $snapshotParity = Compare-FileTransferCoordinatorSnapshotEvaluations -Full $fullSnapshotEvaluation -Compact $compactSnapshotEvaluation
    $snapshotEvaluation = $compactSnapshotEvaluation
    $fileTransferReceiveRecoveryOperationLegacyProof =
        Get-PhasePreCorrelatorControl
    return $snapshotEvaluation
    } finally {$script:trace.Add('cleanup')}
}
'@
    $baselineTrace=''; $baselineFailure=''
    foreach($instrumented in @(if($Case -cne 'timeout-child') {$false}; $true)) {
        $script:trace.Clear(); $failure=''; $value=$null
        if($instrumented) {
            if($Case -ceq 'write-failure') {[void][IO.Directory]::CreateDirectory($path)}
            Invoke-Expression (Add-FrontierPrimaryPhaseCheckpoints $definition)
            Write-FrontierPrimaryCheckpoint 'started' 'control started'
        } else {Invoke-Expression $definition}
        try {
            $value=Invoke-PhaseControl
            if($instrumented) {Write-FrontierPrimaryCheckpoint 'finished' 'control finished'}
        } catch {
            $failure=$_.Exception.Message
            if($instrumented) {Write-FrontierPrimaryCheckpoint 'worker-error' $failure}
            if($_.ScriptStackTrace -notmatch 'Invoke-PhaseControl') {throw 'Original failure stack lost'}
        }
        if(-not $instrumented) {$baselineTrace=$script:trace -join ','; $baselineFailure=$failure; continue}
        if(($script:trace -join ',') -cne $baselineTrace -or $failure -cne $baselineFailure) {throw 'Diagnostic projection changed original calls/error/cleanup'}
        if(-not $failure -and -not [object]::ReferenceEquals($value,$script:compactView)) {throw 'Diagnostic projection changed original result'}
    }
    if($Case -ceq 'write-failure') {
        if(-not $script:primaryCheckpointStopped) {throw 'Write failure was not contained'}
    } else {
        $expected=$stages
        if($Case.EndsWith('-error')) {
            $last=switch($Case) {'registration-error' {'registration-start'} 'full-error' {'full-start'} 'compact-error' {'compact-start'} 'parity-error' {'payloads-ready'}}
            $expected=@($stages[0..[Array]::IndexOf($stages,$last)])+@('worker-error')
        }
        Assert-PhaseRecords $expected
    }
}
[Console]::Out.WriteLine('original stdout'); [Console]::Error.WriteLine('original stderr')
'primary-phase-checkpoint-control='+$Case
