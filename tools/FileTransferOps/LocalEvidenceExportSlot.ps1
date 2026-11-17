Set-StrictMode -Version Latest

if ($null -eq ('NLink.Tools.LocalEvidenceExportSlot' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'LocalEvidenceExportSlot.cs')
}
