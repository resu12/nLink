param([string]$Definition)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$body=$Definition.Replace("`r`n","`n")
$needle=@'
    finally {
        if ($null -ne $private:nativePrimaryCustody) {
'@
if($body.Split([string[]]@($needle),[StringSplitOptions]::None).Length -ne 2) {throw 'Collector primary cleanup seam changed'}
$body=$body.Replace($needle,(@'
    catch {
        # Preserve the original error even if an observation in finally cannot
        # inspect a result that the failed analysis never produced.
        [IO.File]::WriteAllText((Join-Path $ArtifactDir 'collector-primary-original-error.txt'),[string]$_+"`n"+$_.ScriptStackTrace)
        throw
    }
'@+"`n"+$needle))
$read='$fullSnapshotEvaluation = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $primaryFullSnapshotLines'
if($body.Split([string[]]@($read),[StringSplitOptions]::None).Length -ne 2) {throw 'Collector primary raw-input seam changed'}
$body=$body.Replace($read,(@'
    # The existing raw-evidence scanner already accepts a complete original
    # source read. Supply those exact arrays only after proving every physical
    # line survived parsing, without enabling full-frontier authority.
    if($null -eq $private:localPrimaryInputs -or $null -eq $LocalSourceOwner.CollectorOutputsForTest -or
        -not [object]::ReferenceEquals($private:localPrimaryInputs.FullLines,$primaryFullSnapshotLines)) {
        throw 'collector_primary_original_inputs_missing'
    }
    $private:localPrimaryInputs.Work.Start()
    try {
        $private:collectorRawContext=$private:localPrimaryInputs
        if($Index.LogFiles.Count -ne 1 -or $Index.LogFiles[0] -cne $LocalSourceOwner.SourcePath -or
            $collectorRawContext.FullOrigins.Length -ne $LocalSourceOwner.SourceLines -or
            -not $LocalSourceOwner.ValidateFullInputOrigins($LocalSourceReader,$collectorRawContext.NativeOwner,$collectorRawContext.NativeInputs,
                $Index.AllEvents,$collectorRawContext.FullLines,$collectorRawContext.FullOriginal,$collectorRawContext.FullOrigins,$collectorRawContext.Work)) {throw 'collector_primary_raw_source_coverage'}
        for($i=0;$i -lt $collectorRawContext.FullOrigins.Length;$i++) {
            if($collectorRawContext.FullOrigins[$i] -ne $i+1 -or $collectorRawContext.Work.ElapsedMilliseconds -gt 20000 -or
                -not $LocalSourceOwner.OwnsCompletedSource($LocalSourceReader)) {throw 'collector_primary_raw_source_gap'}
        }
        $nativeRawSourceLines=@{ $LocalSourceOwner.SourcePath = $collectorRawContext.FullLines }
        $script:collectorRawReadUsed=$true
    } finally {$private:localPrimaryInputs.Work.Stop()}
'@+"`n    "+$read))
$body
