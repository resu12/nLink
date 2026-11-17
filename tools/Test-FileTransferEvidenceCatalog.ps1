# Offline prerequisite: safe to run before freezing inputs or consuming a live attempt.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'GuiSmokeLogLedger.ps1')
$eventNames = @(Get-GuiSmokeSemanticEvidenceEventNames)
$catalogPath = Join-Path $PSScriptRoot 'FileTransferOps/LegacyV0EvidenceCatalog.json'
[pscustomobject]@{
    catalogPath = $catalogPath
    catalogSha256 = (Get-FileTransferCoordinatorEvidenceCatalog).LegacyV0.Hash
    semanticEventCount = $eventNames.Count
    powerShellVersion = $PSVersionTable.PSVersion.ToString()
}
