Set-StrictMode -Version Latest

# Trusted primary issuer adapter, not an optional formatter or a public carrier
# consumer. The primary alone supplies its private custody capability. The old
# diagnostic Prepared.Json and any candidate payload/checksums are never read.
function New-FileTransferPrimaryPublicationCompletion {
    param($Custody, $Evaluations, $Context, $Prepared)
    # Assignment through private: does not make an existing parameter private.
    # Explicit options prevent nested codecs from resolving these capabilities
    # and primary references through PowerShell's dynamic variable lookup.
    foreach ($name in @('Custody','Evaluations','Context','Prepared')) {
        Set-Variable -Name $name -Option Private -Value (Get-Variable -Name $name -ValueOnly)
    }
    $retained = $false
    $custodyType = 'NLink.Tools.LocalRetainedSourceRequest+PrimaryEvaluationCustody' -as [type]
    $pairType = 'NLink.Tools.LocalRetainedSourceRequest+CompletedEvaluations' -as [type]
    $ownedCustody = $null -ne $custodyType -and $custodyType.IsInstanceOfType($Custody)
    try {
        if ($null -eq $Context -or $Context.Disposed -or -not $Context.Associated -or
            $null -eq $Prepared -or $Prepared.Status -cne 'prepared_primary_publication_witness' -or
            $Prepared.PublicationCompletionIssued -or -not $ownedCustody -or $null -eq $pairType -or
            -not $pairType.IsInstanceOfType($Evaluations)) { return $null }
        $Context.Work.Start()
        if (-not $Custody.Owns($Evaluations,$Context,$Context.FullLines,$Context.CompactLines,$Context.FullView,$Context.CompactView) -or
            -not $Context.NativeOwner.Owns($Context.NativeInputs) -or $null -eq $Context.WriterReader -or
            -not $Context.WriterReader.IsVerified) { return $null }
        $catalog = ConvertTo-FileTransferLocalCarrierJson @{
            contractVersion=$Context.FullView.ContractVersion;catalogVersion=$Context.FullView.CatalogVersion
            catalogHash=$Context.FullView.CatalogHash;boundaryHash=$Context.FullView.BoundaryHash
        }
        $remaining = [Math]::Min(2000L,[NLink.Tools.LocalPublicationInputLimits]::ProducerMilliseconds - $Context.Work.ElapsedMilliseconds)
        if ($remaining -lt 1) { throw 'primary_publication_completion_time_budget' }
        $completion = $Custody.TryPreparePublication($Evaluations,$Context,$Context.FullLines,$Context.CompactLines,
            $Context.FullView,$Context.CompactView,$Context.NativeOwner,$Context.NativeInputs,$Context.WriterReader,
            $Prepared.FullRowJson,$Prepared.CompactRowJson,$Prepared.WitnessJson,$Prepared.ProvenanceJson,$catalog,[int]$remaining)
        if ($null -eq $completion -or $Context.Work.ElapsedMilliseconds -gt [NLink.Tools.LocalPublicationInputLimits]::ProducerMilliseconds -or
            -not $Custody.OwnsPublication($completion)) { throw 'primary_publication_completion_omitted' }
        $retained = $true; return $completion
    }
    catch {
        if ($null -ne $Context) { $Context.PublicationCompletionOmissionReason = $_.Exception.Message }
        return $null
    }
    finally {
        if (-not $retained -and $ownedCustody) { $Custody.CancelPublication(); $Custody.Dispose() }
        if ($null -ne $Context) { $Context.Work.Stop() }
    }
}
