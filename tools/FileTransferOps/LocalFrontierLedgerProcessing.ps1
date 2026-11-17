Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'LocalGuiWriterRequest.ps1')
if ($null -eq ('NLink.Tools.GuiSmokeLogLedgerStreamingProcessor' -as [type])) {
    Add-Type -Path (Join-Path (Split-Path $PSScriptRoot -Parent) 'GuiSmokeLogLedgerStreamingProcessor.cs')
}

# Explicit opt-in only. No production finalizer call site uses this entry yet.
# The result is original stream/write provenance, never finalizer approval.
function Invoke-FileTransferFrontierLedgerProcessing {
    param(
        [Parameter(Mandatory = $true)][NLink.Tools.LocalGuiWriterRequest+FrontierFinalizerBinding]$Binding,
        [Parameter(Mandatory = $true)][long]$FrozenLineCount,
        [Parameter(Mandatory = $true)][long]$Bookmark,
        [AllowEmptyCollection()][string[]]$SemanticEventNames = @()
    )
    $source = $null; $result = $null; $completed = $false
    try {
        if (-not $Binding.IsCurrent) { return $null }
        $run = $Binding.AcceptanceRunId; $scenario = $Binding.ScenarioRunId
        $root = $Binding.ArtifactRoot
        $source = $Binding.TryOpenSource()
        if ($null -eq $source -or -not $source.TryBeginProcessingWork()) { return $null }
        $markers = $Binding.TryCaptureRecoveryMarkers()
        if ($null -eq $markers) { return $null }
        $prefix = 'frontier-ledger-' + [guid]::NewGuid().ToString('N')
        $result = [NLink.Tools.FrontierLedger.GuiSmokeLogLedgerStreamingProcessor]::ProcessFromStreamWithRetainedOutputs(
            $source, $FrozenLineCount, $Bookmark, (Join-Path $root ($prefix + '-retained.tmp')),
            (Join-Path $root ($prefix + '-writer.tmp')), $run, $scenario, [string[]]@($markers.DeferredBatchKeys),
            (Join-Path $root ($prefix + '-semantic.tmp')), $SemanticEventNames, $source.ProcessingLifetime)
        $writerMatch = $Binding.TryMatchProcessedWriters($result)
        $recoveryMatch = $Binding.TryMatchRecovery($result)
        if ($null -eq $writerMatch -or $null -eq $recoveryMatch -or -not $source.TryFinishProcessingWork() -or -not $Binding.IsCurrent -or
            -not $result.OwnsStreamCapture($result.StreamCapture) -or
            -not $result.StreamCapture.MatchesOriginalSource($source) -or
            -not $result.StreamCapture.HasRetainedOutputs -or
            -not $result.StreamCapture.OwnsValidation($result.StreamCapture.Validation) -or
            -not $result.StreamCapture.Validation.IsCurrent -or -not $writerMatch.MatchesResult($result) -or -not $recoveryMatch.MatchesResult($result)) { return $null }
        $completed = $true
        return $result
    }
    catch { return $null }
    finally {
        # Successful provenance borrows the reader until its original binding is
        # retired. Failed temporary outputs are artifacts, never published inputs.
        if (-not $completed) {
            if ($null -ne $result -and $null -ne $result.StreamCapture) { $result.StreamCapture.Dispose() }
            if ($null -ne $source) { $source.Dispose() }
            $Binding.Dispose()
        }
    }
}
