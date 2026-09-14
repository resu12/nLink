$prepare=(Get-Command New-FileTransferPrimaryEvaluationInputs).ScriptBlock.ToString()
$needle='finally { if (-not $retained -and $null -ne $nativeOwner) { $nativeOwner.Dispose() } }'
if($prepare.Split([string[]]@($needle),[StringSplitOptions]::None).Length -ne 2) {throw 'Original preparation cleanup changed'}
$observation=@'
finally {
    $facts=[ordered]@{Retained=$retained;ElapsedMilliseconds=$watch.ElapsedMilliseconds
        FullRecords=$FullLines.Length;CompactRecords=$CompactLines.Length
        SourceOwned=$SourceOwner.OwnsCompletedSource($SourceReader);NativeStarted=$null -ne $nativeOwner}
    if($null -ne $nativeOwner) {
        # Reaching native capture requires the entire original origin pass.
        $facts.OriginValidatedRecords=$FullLines.Length
        $facts.NativeOmission=$nativeOwner.OmissionReason
        $facts.NativeMilliseconds=$nativeOwner.ActiveMilliseconds
        $facts.NativeRecordVisits=$nativeOwner.RecordVisits
        $facts.NativeCodeUnits=$nativeOwner.CodeUnitVisits
    }
    [IO.File]::WriteAllText((Join-Path $ArtifactDir 'owned-preparation-observation.json'),($facts | ConvertTo-Json -Compress))
    if (-not $retained -and $null -ne $nativeOwner) { $nativeOwner.Dispose() }
}
'@
Set-Item Function:New-FileTransferPrimaryEvaluationInputs -Value ([scriptblock]::Create($prepare.Replace($needle,$observation)))
