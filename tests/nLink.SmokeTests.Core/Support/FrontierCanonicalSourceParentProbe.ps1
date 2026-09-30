param([string]$RepoRoot,[string]$Directory,[string]$Case)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$env:NLINK_TEST_CANONICAL_SOURCE_CASE=$Case
& (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierCanonicalRolesParentProbe.ps1') $RepoRoot $Directory 'valid'
$evidence=Get-Content -LiteralPath (Join-Path $Directory 'canonical-source.json') -Raw | ConvertFrom-Json
if(-not $evidence.Checked -or $evidence.Case -cne $Case -or $evidence.SendCount -ne 1 -or
    $evidence.Hash -notmatch '^[0-9a-f]{64}$') {throw 'Source checks missing'}
# Corroboration only: this measurement never creates or replaces source custody.
$path=Join-Path $Directory 'filetransfer-tuna-gui-payload.bin'
$sha=[Security.Cryptography.SHA256]::Create()
$stream=[IO.File]::OpenRead($path)
try {$digest=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','').ToLowerInvariant()}
finally {$stream.Dispose(); $sha.Dispose()}
if($digest -cne $evidence.Hash) {throw 'Original hash changed'}
$exclusive=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
$exclusive.Dispose()
'canonical-source='+$Case
