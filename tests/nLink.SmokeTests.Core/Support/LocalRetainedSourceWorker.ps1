param([string]$RepoRoot,[string]$SettingsPath)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$settings=[IO.File]::ReadAllText($SettingsPath) | ConvertFrom-Json
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1')
if($settings.suite -ceq 'primary') { . (Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1') }
else { . (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1') }
$ready=[Threading.EventWaitHandle]::OpenExisting($settings.signal+'.ready')
$go=[Threading.EventWaitHandle]::OpenExisting($settings.signal+'.go')
$step=[Threading.EventWaitHandle]::OpenExisting($settings.signal+'.step')
$resume=[Threading.EventWaitHandle]::OpenExisting($settings.signal+'.resume')
$owner=$null; $reader=$null; $iterator=$null
$facts=[ordered]@{Attached=$false;Complete=$false;Lines=0;ParserEqual=$false;CurrentAfterClose=$false}
try {
    [void]$ready.Set()
    if(-not $go.WaitOne(15000)) { throw 'Test start not released' }
    if($settings.suite -ceq 'primary') {
        $realParser=(Get-Command Read-FileTransferLogEvents).ScriptBlock
        $realEvaluator=(Get-Command Get-FileTransferCoordinatorSnapshotEvaluation).ScriptBlock
        $script:observedReader=$null; $script:readComplete=$false; $script:evaluations=0
        function Read-FileTransferLogEvents {
            param([string[]]$LogFiles,[int]$TailMinutes,$OwnedSource=$null)
            $script:observedReader=$OwnedSource
            $parsed=@(& $realParser -LogFiles $LogFiles -TailMinutes $TailMinutes -OwnedSource $OwnedSource)
            if($null -ne $OwnedSource) { $script:readComplete=$OwnedSource.InputReadComplete }
            return $parsed
        }
        function Get-FileTransferCoordinatorSnapshotEvaluation {
            param([AllowEmptyCollection()][string[]]$Lines)
            $script:evaluations++
            & $realEvaluator -Lines $Lines
        }
        if($settings.mode -ceq 'primary-report-fails') {
            function Write-FileTransferEfficiencySummaryArtifacts { throw 'injected primary report failure' }
        }
        $failure=''; $witness=$null
        try { $null=Invoke-FileTransferRetainedAnalysis -RepoRoot $RepoRoot -ArtifactDir $settings.output -LogPath @($settings.path) -TailMinutes 0 -LocalFailureWitness ([ref]$witness) }
        catch { $failure=$_.Exception.Message }
        if($settings.mode -ceq 'primary-report-fails') {
            if($failure -cne 'injected primary report failure' -or $null -ne $witness) { throw 'Primary failure/optional witness changed' }
        }
        elseif($failure) { throw $failure }
        $facts.Attached=$null -ne $script:observedReader; $facts.Complete=$script:readComplete
        $facts.Evaluations=$script:evaluations; $facts.PrimaryFailure=$failure
        $facts.ReaderDisposed=$null -eq $script:observedReader -or -not $script:observedReader.InputReadComplete
        $facts.WitnessLine=if($null -ne $witness) { $witness.SourceLine } else { 0 }
    }
    else {
        $id=[guid]$env:NLINK_RETAINED_SOURCE_REQUEST_ID
        $path=$settings.path; $run=$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID; $scenario=$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID
        switch($settings.mode) {
            'wrong-request' { $id=[guid]::NewGuid() }
            'wrong-run' { $run+='-foreign' }
            'wrong-scenario' { $scenario+='-foreign' }
            'wrong-path' { $path+='.foreign' }
        }
        $owner=[NLink.Tools.LocalRetainedSourceRequest]::TryAttach($env:NLINK_RETAINED_SOURCE_REQUEST,$id,$path,$run,$scenario)
        $facts.Attached=$null -ne $owner
        if($null -ne [NLink.Tools.LocalRetainedSourceRequest]::TryAttach($env:NLINK_RETAINED_SOURCE_REQUEST,
            [guid]$env:NLINK_RETAINED_SOURCE_REQUEST_ID,$settings.path,$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID,
            $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID)) { throw 'Second attachment admitted' }
        if($null -ne $owner) {
            $facts.RequestId=$owner.RequestId.ToString('D'); $facts.SourceSha256=$owner.SourceSha256
            $facts.SourceIdentity=$owner.SourceIdentity; $facts.SourceLines=$owner.SourceLines
            if($settings.mode -ceq 'cancel-before-open') {
                [void]$step.Set(); if(-not $resume.WaitOne(15000)) { throw 'Cancellation not released' }
            }
            $reader=$owner.TryOpenSource()
            if($null -ne $owner.TryOpenSource()) { throw 'Second source open admitted' }
            if($null -ne $reader) {
                if($settings.mode -cin @('partial','exact-count-no-eof','cancel-after-line','parent-exit')) {
                    $iterator=[Collections.IEnumerable].GetMethod('GetEnumerator').Invoke($reader.ReadLines(),@())
                    $move=[Collections.IEnumerator].GetMethod('MoveNext')
                    if(-not $move.Invoke($iterator,@())) { throw 'First source line missing' }
                    if($settings.mode -ceq 'exact-count-no-eof') {
                        for($i=1;$i -lt $owner.SourceLines;$i++) { if(-not $move.Invoke($iterator,@())) { throw 'Expected line missing' } }
                    }
                    if($settings.mode -cin @('cancel-after-line','parent-exit')) {
                        [void]$step.Set(); if(-not $resume.WaitOne(15000)) { throw 'Cancellation not released' }
                        while($move.Invoke($iterator,@())) { }
                    }
                }
                else {
                    $expected=@(Read-FileTransferLogEvents -LogFiles @($settings.path) -TailMinutes 0)
                    $actual=@(Read-FileTransferLogEvents -LogFiles @($settings.path) -TailMinutes 0 -OwnedSource $reader)
                    $facts.ParserEqual=(ConvertTo-Json -InputObject $actual -Depth 8 -Compress) -ceq (ConvertTo-Json -InputObject $expected -Depth 8 -Compress)
                }
                $facts.Complete=$reader.InputReadComplete; $facts.Lines=$reader.LinesRead
                $replayed=$false
                try { foreach($line in $reader.ReadLines()) { $replayed=$true } } catch { }
                if($replayed) { throw 'Source reader replayed' }
            }
            if($null -ne $iterator) { [IDisposable].GetMethod('Dispose').Invoke($iterator,@()); $iterator=$null }
            if($null -ne $reader) { $reader.Dispose(); if($reader.InputReadComplete) { throw 'Disposed reader kept ownership' } }
            $owner.Dispose(); $facts.CurrentAfterClose=$owner.IsCurrent
        }
    }
    [IO.File]::WriteAllText($settings.result,(ConvertTo-Json -InputObject $facts -Depth 5 -Compress),[Text.UTF8Encoding]::new($false))
}
finally {
    if($null -ne $iterator) { [IDisposable].GetMethod('Dispose').Invoke($iterator,@()) }
    if($null -ne $reader) { $reader.Dispose() }; if($null -ne $owner) { $owner.Dispose() }
    $ready.Dispose(); $go.Dispose(); $step.Dispose(); $resume.Dispose()
}
