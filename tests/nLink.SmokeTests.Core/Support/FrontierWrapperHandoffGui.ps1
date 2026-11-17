param([string]$ExePath,[int]$TimeoutSeconds)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$settings=Get-Content -LiteralPath $ExePath -Raw | ConvertFrom-Json
. (Join-Path $settings.RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierCleanupAdoptionGui.ps1') `
    -RepoRoot $settings.RepoRoot -Directory $settings.Directory -Case $settings.Case -FinalizerCase $settings.FinalizerCase
