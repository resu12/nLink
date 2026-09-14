param([string]$RepoRoot,[string]$SettingsPath)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$settings=[IO.File]::ReadAllText($SettingsPath) | ConvertFrom-Json
. (Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalPrimaryEvaluationInputs.ps1')
$result=@{Mode=$settings.mode;Associated=$false;Disposed=$false;Evaluations=0}
if($settings.mode -ceq 'controls') {
    $owner=$null; $source=$null; $context=$null
    try {
        $owner=[NLink.Tools.LocalRetainedSourceRequest]::TryAttach($env:NLINK_RETAINED_SOURCE_REQUEST,
            [guid]$env:NLINK_RETAINED_SOURCE_REQUEST_ID,$settings.path,$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID,$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID)
        if($null -eq $owner) { throw 'Actual retained owner missing' }
        $source=$owner.TryOpenSource()
        $emptyIndex=[pscustomobject]@{AllEvents=@();LogFiles=@($settings.path)}
        if($null -ne (New-FileTransferPrimaryEvaluationInputs $owner $source $emptyIndex $null ([string[]]@()) ([string[]]@()))) { throw 'Incomplete source admitted' }
        $events=@(Read-FileTransferLogEvents -LogFiles @($settings.path) -TailMinutes 0 -OwnedSource $source)
        $catalog=Get-FileTransferCoordinatorEvidenceCatalog
        $cases=@('valid','null-owner','forged-owner','wrong-reader','synthetic-origin','wrong-sequence','string-sequence','wrong-path',
            'wrong-full-line','wrong-compact-line','extra-compact-line','record-cap','line-cap','copy-array',
            'mutated-array','missing-view','native-cancel','double-complete','elapsed-budget','cancelled-source')
        foreach($case in $cases) {
            $index=New-FileTransferRetainedEventIndex -Events @($events | ForEach-Object { $_.PSObject.Copy() }) -LogFiles @($settings.path)
            [string[]]$full=@($index.AllEvents | ForEach-Object RawLine)
            [string[]]$compact=@($index.AllEvents | Where-Object { $catalog.SnapshotAuthorityV1 -contains $_.EventName -or $catalog.GenerationStampedEffects -contains $_.EventName } | ForEach-Object RawLine)
            $candidate=$owner; $reader=$source
            switch($case) {
                'null-owner' { $candidate=$null }
                'forged-owner' { $candidate=[pscustomobject]@{RequestId=$owner.RequestId;WriterContext=$owner.WriterContext} }
                'wrong-reader' { $reader=[pscustomobject]@{InputReadComplete=$true;SourcePath=$settings.path} }
                'synthetic-origin' { $index.AllEvents[0].LineNumber=0 }
                'wrong-sequence' { $index.AllEvents[0].Sequence=9 }
                'string-sequence' { $index.AllEvents[0].Sequence='1' }
                'wrong-path' { $index.AllEvents[0].FilePath+=':wrong' }
                'wrong-full-line' { $full[0]+=':wrong' }
                'wrong-compact-line' { $compact=[string[]]@('wrong') }
                'extra-compact-line' { $compact=[string[]]@($full[0]) }
                'record-cap' {
                    $script:capVisits=0
                    $poison=[pscustomobject]@{}
                    $poison | Add-Member ScriptProperty RawLine { $script:capVisits++; throw 'Over-cap input was visited' }
                    $count=[NLink.Tools.LocalPublicationInputLimits]::InputRecords+1
                    $index.AllEvents=[object[]](@($poison)*$count); $full=[string[]]::new($count)
                }
                'line-cap' { $index.AllEvents[0].RawLine='X'*262145; $full[0]=$index.AllEvents[0].RawLine }
            }
            $context=New-FileTransferPrimaryEvaluationInputs $candidate $reader $index $catalog $full $compact
            if($case -ceq 'record-cap' -and $script:capVisits -ne 0) { throw 'Record ceiling was checked after traversal' }
            $expected=$case -cin @('valid','copy-array','mutated-array','missing-view','native-cancel','double-complete','elapsed-budget','cancelled-source')
            if(($null -ne $context) -ne $expected) { throw "Preparation admission drift: $case" }
            if($null -eq $context) { continue }
            $token=$context.NativeInputs; $native=$context.NativeOwner
            $fullView=[pscustomobject]@{DiagnosticTestOnly='full'}; $compactView=[pscustomobject]@{DiagnosticTestOnly='compact'}
            if($case -ceq 'copy-array') { $full=[string[]]$full.Clone() }
            if($case -ceq 'mutated-array') { $full[0]+=':mutated' }
            if($case -ceq 'missing-view') { $fullView=$null }
            if($case -ceq 'native-cancel') { $native.Cancel() }
            if($case -ceq 'elapsed-budget') {
                $context.Work.Stop()
                $elapsed=@($context.Work.GetType().GetFields([Reflection.BindingFlags]'Instance,NonPublic') | Where-Object Name -In @('elapsed','_elapsed'))
                if($elapsed.Count -ne 1) {throw 'Primary test clock shape changed'}
                $elapsed[0].SetValue($context.Work,[long]([Diagnostics.Stopwatch]::Frequency*([NLink.Tools.LocalPublicationInputLimits]::ProducerMilliseconds+1)/1000))
            }
            if($case -ceq 'cancelled-source') { $source.Dispose() }
            $associated=Complete-FileTransferPrimaryEvaluationInputs $context $full $compact $fullView $compactView
            if($associated -ne ($case -cin @('valid','double-complete'))) { throw "Association drift: $case" }
            if($associated -and (-not [object]::ReferenceEquals($context.FullView,$fullView) -or
                -not [object]::ReferenceEquals($context.CompactView,$compactView) -or $context.FullOrigins[0] -ne 1)) { throw 'Exact view/origin lost' }
            if($case -ceq 'double-complete' -and (Complete-FileTransferPrimaryEvaluationInputs $context $full $compact $fullView $compactView)) { throw 'Association replayed' }
            Remove-FileTransferPrimaryEvaluationInputs $context
            if(-not $context.Disposed -or $native.Owns($token) -or $null -ne $context.FullView) { throw 'Disposed context retained ownership' }
            $context=$null
        }
        $result.Controls=$cases; $result.Disposed=$true
    } finally {
        if($null -ne $context) { Remove-FileTransferPrimaryEvaluationInputs $context }
        if($null -ne $source) { $source.Dispose() }; if($null -ne $owner) { $owner.Dispose() }
    }
} else {
    $realEvaluator=(Get-Command Get-FileTransferCoordinatorSnapshotEvaluation).ScriptBlock
    $script:evaluations=0; $script:observedContext=$null; $script:associated=$false
    $script:observedCustody=$null; $script:observedPair=$null; $script:custodyOwned=$false
    $script:observedFullLines=$null; $script:observedCompactLines=$null
    $script:observedFullView=$null; $script:observedCompactView=$null
    function Get-FileTransferCoordinatorSnapshotEvaluation {
        param([AllowEmptyCollection()][string[]]$Lines)
        $script:evaluations++
        foreach($name in @('localPrimaryInputs','localPrimaryCustody','localCompletedEvaluations','localPublicationCompletion')) {
            if($null -ne (Get-Variable $name -ErrorAction SilentlyContinue)) { throw "Private primary ownership leaked to evaluator helper: $name" }
        }
        if($settings.mode -ceq 'primary-evaluator-fails' -and $script:evaluations -eq 1) { throw 'injected primary evaluator failure' }
        $view=& $realEvaluator -Lines $Lines
        # Change the exact argument only after the real evaluator has consumed
        # it. Reports remain original; optional ownership must be discarded.
        if($settings.mode -ceq 'primary-input-mutated' -and $script:evaluations -eq 1) { $Lines[0]+=':mutation' }
        return $view
    }
    $body=(Get-Command Write-FileTransferCompactRetainedAnalysisArtifacts).ScriptBlock.ToString()
    $needle='    $snapshotParity = Compare-FileTransferCoordinatorSnapshotEvaluations'
    if($body.Split([string[]]@($needle),[StringSplitOptions]::None).Length -ne 2) { throw 'Actual association observation seam changed' }
    $observe=@'
    $script:observedContext=$private:localPrimaryInputs
    $script:associated=$null -ne $private:localPrimaryInputs -and $private:localPrimaryInputs.Associated
    $script:observedCustody=$private:localPrimaryCustody; $script:observedPair=$private:localCompletedEvaluations
    if($null -ne $private:localPrimaryInputs) {
        $script:observedFullLines=$private:localPrimaryInputs.FullLines; $script:observedCompactLines=$private:localPrimaryInputs.CompactLines
        $script:observedFullView=$fullSnapshotEvaluation; $script:observedCompactView=$compactSnapshotEvaluation
    }
    $script:custodyOwned=$null -ne $private:localPrimaryCustody -and $private:localPrimaryCustody.Owns(
        $private:localCompletedEvaluations,$private:localPrimaryInputs,$private:localPrimaryInputs.FullLines,
        $private:localPrimaryInputs.CompactLines,$fullSnapshotEvaluation,$compactSnapshotEvaluation)
'@
    $body=$body.Replace($needle,$observe+"`n"+$needle)
    # Observe the original finally as well, including an evaluator exception.
    $body=$body.Replace('try { Remove-FileTransferPrimaryEvaluationInputs $private:localPrimaryInputs }',
        'try { $script:observedContext=$private:localPrimaryInputs; Remove-FileTransferPrimaryEvaluationInputs $private:localPrimaryInputs }')
    $body=$body.Replace('try { $private:localPrimaryCustody.Dispose() }',
        'try { $script:observedCustody=$private:localPrimaryCustody; $private:localPrimaryCustody.Dispose() }')
    foreach($module in @('LocalPrimaryEvaluationInputs.ps1','LocalPrimaryPublicationWitness.ps1','LocalPrimaryPublicationCompletion.ps1','LocalPublicationFailureWitness.ps1')) {
        $body=$body.Replace("Join-Path `$PSScriptRoot '$module'", "Join-Path `$RepoRoot 'tools/FileTransferOps/$module'")
    }
    Set-Item Function:Write-FileTransferCompactRetainedAnalysisArtifacts -Value ([scriptblock]::Create($body))
    if($settings.mode -ceq 'primary-report-fails') { function Write-FileTransferEfficiencySummaryArtifacts { throw 'injected primary report failure' } }
    $env:NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT='1'; $failure=''
    try { $null=Invoke-FileTransferRetainedAnalysis -RepoRoot $RepoRoot -ArtifactDir $settings.output -LogPath @($settings.path) -TailMinutes 0 }
    catch { $failure=$_.Exception.Message }
    $expectedFailure=if($settings.mode -ceq 'primary-report-fails') { 'injected primary report failure' }
        elseif($settings.mode -ceq 'primary-evaluator-fails') { 'injected primary evaluator failure' } else { '' }
    if($failure -cne $expectedFailure) { throw "Primary failure changed: $failure" }
    $expectedAssociated=$settings.mode -cin @('primary-owned','primary-report-fails')
    if($script:associated -ne $expectedAssociated) { throw "Primary association drift: $($settings.mode)" }
    if($script:custodyOwned -ne $expectedAssociated) { throw "Private evaluator custody drift: $($settings.mode)" }
    if($null -ne $script:observedCustody -and $script:observedCustody.Owns($script:observedPair,$script:observedContext,
        $script:observedFullLines,$script:observedCompactLines,$script:observedFullView,$script:observedCompactView)) {
        throw 'Invocation custody survived actual primary cleanup'
    }
    if($settings.mode -cne 'primary-baseline' -and ($null -eq $script:observedContext -or -not $script:observedContext.Disposed -or $null -ne $script:observedContext.FullView)) { throw 'Actual primary cleanup missing' }
    $expectedEvaluations=if($settings.mode -ceq 'primary-evaluator-fails') { 1 } else { 3 }
    if($script:evaluations -ne $expectedEvaluations) { throw "Authority count changed: $script:evaluations" }
    $result.Evaluations=$script:evaluations; $result.Associated=$script:associated; $result.Disposed=$true; $result.PrimaryFailure=$failure
    $result.CustodyOwned=$script:custodyOwned
}
[IO.File]::WriteAllText($settings.result,($result | ConvertTo-Json -Depth 4 -Compress))
