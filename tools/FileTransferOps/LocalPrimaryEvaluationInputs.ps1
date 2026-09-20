Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'LocalNativeInputCapture.ps1')

# Invocation-local preparation only. Neither this object nor its native token is
# a publication completion issuer. The primary keeps it private and disposes it.
function New-FileTransferPrimaryEvaluationInputs {
    param($SourceOwner, $SourceReader, $Index, $Catalog, $FullLines, $CompactLines)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $nativeOwner = $null
    $retained = $false
    try {
        $workerType = 'NLink.Tools.LocalRetainedSourceRequest+Worker' -as [type]
        $readerType = 'NLink.Tools.LocalRetainedSourceRequest+SourceReader' -as [type]
        if ($null -eq $workerType -or $null -eq $readerType -or
            -not $workerType.IsInstanceOfType($SourceOwner) -or -not $readerType.IsInstanceOfType($SourceReader) -or
            -not $SourceOwner.OwnsCompletedSource($SourceReader) -or $null -eq $SourceOwner.WriterContext -or
            $FullLines -isnot [string[]] -or $CompactLines -isnot [string[]] -or
            $Index.LogFiles.Count -ne 1 -or $Index.LogFiles[0] -cne $SourceOwner.SourcePath -or
            $Index.AllEvents.Count -ne $FullLines.Length -or $CompactLines.Length -gt $FullLines.Length -or
            $FullLines.Length -gt [NLink.Tools.LocalPublicationInputLimits]::InputRecords -or
            7L * $FullLines.Length + 4L * $CompactLines.Length -gt [NLink.Tools.LocalPublicationInputLimits]::ProducerRecords) { return $null }

        # The arrays are the original primary assembly outputs. This bounded
        # pass binds their physical origins; it does not reread the source.
        $fullOrigins = [int[]]::new($FullLines.Length)
        $compactOrigins = [int[]]::new($CompactLines.Length)
        $previous = 0; $compactPosition = 0
        for ($i = 0; $i -lt $FullLines.Length; $i++) {
            if ($watch.ElapsedMilliseconds -gt [NLink.Tools.LocalPublicationInputLimits]::ProducerMilliseconds -or -not $SourceOwner.OwnsCompletedSource($SourceReader)) { return $null }
            $event = $Index.AllEvents[$i]
            if ($event.RawLine -isnot [string] -or $event.LineNumber -isnot [int] -or $event.Sequence -isnot [int] -or
                $event.Sequence -ne $event.LineNumber -or $event.LineNumber -le $previous -or
                $event.LineNumber -gt $SourceOwner.SourceLines -or $event.FilePath -cne $SourceOwner.SourcePath -or
                -not [object]::ReferenceEquals($event.RawLine, $FullLines[$i])) { return $null }
            $previous = $event.LineNumber; $fullOrigins[$i] = $previous
            if ($Catalog.SnapshotAuthorityV1 -contains $event.EventName -or
                $Catalog.GenerationStampedEffects -contains $event.EventName) {
                if ($compactPosition -ge $CompactLines.Length -or
                    -not [object]::ReferenceEquals($event.RawLine, $CompactLines[$compactPosition])) { return $null }
                $compactOrigins[$compactPosition] = $previous; $compactPosition++
            }
        }
        if ($compactPosition -ne $CompactLines.Length) { return $null }
        $fullOriginal = $FullLines.Clone(); $compactOriginal = $CompactLines.Clone()
        $nativeOwner = [NLink.Tools.LocalNativeInputCapture]::new($SourceOwner.RequestId)
        $nativeInputs = $SourceOwner.CaptureNativeInputs($SourceReader,$nativeOwner,$FullLines,$CompactLines,$watch)
        if ($null -eq $nativeInputs -or -not $SourceOwner.ValidateInputReferences($SourceReader,
            $FullLines,$fullOriginal,$CompactLines,$compactOriginal,$watch)) { return $null }
        $context = [pscustomobject]@{
            SourceOwner = $SourceOwner; SourceReader = $SourceReader
            NativeOwner = $nativeOwner; NativeInputs = $nativeInputs
            FullLines = $FullLines; CompactLines = $CompactLines
            FullOriginal = $fullOriginal; CompactOriginal = $compactOriginal
            FullOrigins = $fullOrigins; CompactOrigins = $compactOrigins
            FullView = $null; CompactView = $null; Associated = $false; Disposed = $false
            PublicationAttempted = $false; PublicationOmissionReason = ''; WriterReader = $null
            PublicationCompletionOmissionReason = ''
            FrontierPublicationOmissionReason = ''
            Work = $watch; RecordVisitCeiling = 7L * $FullLines.Length + 4L * $CompactLines.Length
        }
        $watch.Stop()
        $retained = $true
        return $context
    }
    catch { return $null }
    finally { if (-not $retained -and $null -ne $nativeOwner) { $nativeOwner.Dispose() } }
}

function Complete-FileTransferPrimaryEvaluationInputs {
    param($Context, $FullLines, $CompactLines, $FullView, $CompactView)
    $associated = $false
    try {
        if ($null -eq $Context -or $Context.Disposed -or $Context.Associated) { return $false }
        $Context.Work.Start()
        if (-not [object]::ReferenceEquals($Context.FullLines, $FullLines) -or
            -not [object]::ReferenceEquals($Context.CompactLines, $CompactLines) -or
            $null -eq $FullView -or $null -eq $CompactView -or
            -not $Context.NativeOwner.Owns($Context.NativeInputs)) { return $false }
        if (-not $Context.SourceOwner.ValidateInputReferences($Context.SourceReader,$Context.FullLines,
            $Context.FullOriginal,$Context.CompactLines,$Context.CompactOriginal,$Context.Work)) { return $false }
        $Context.FullView = $FullView; $Context.CompactView = $CompactView
        $Context.Associated = $true; $associated = $true
        return $true
    }
    catch { return $false }
    finally {
        if ($null -ne $Context) {
            if ($associated) { $Context.Work.Stop() }
            else { Remove-FileTransferPrimaryEvaluationInputs $Context }
        }
    }
}

function Remove-FileTransferPrimaryEvaluationInputs {
    param($Context)
    if ($null -eq $Context) { return }
    $Context.Disposed = $true; $Context.Associated = $false
    if ($null -ne $Context.WriterReader) { $Context.WriterReader.Dispose(); $Context.WriterReader = $null }
    $Context.Work.Stop(); $Context.NativeOwner.Dispose()
    foreach ($name in @('SourceOwner', 'SourceReader', 'NativeInputs', 'FullLines', 'CompactLines',
        'FullOriginal', 'CompactOriginal', 'FullOrigins', 'CompactOrigins', 'FullView', 'CompactView')) {
        $Context.$name = $null
    }
}
