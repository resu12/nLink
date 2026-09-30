param([string]$RepoRoot,[string]$Directory,[string]$Case)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchProjection.ps1')
$env:NLINK_TEST_CANONICAL_ROLES_CASE=$Case
$text=Get-Content -LiteralPath (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierCanonicalGuiParentProbe.ps1') -Raw
$text=Replace-AttemptSeam $text 'FrontierCanonicalGuiWrapperProbe.ps1' 'FrontierCanonicalRolesWrapperProbe.ps1'
# The real parent's canonical bind/launch path runs without test C# helpers.
& ([scriptblock]::Create($text)) $RepoRoot $Directory 'enabled'
$evidence=Get-Content -LiteralPath (Join-Path $Directory 'canonical-roles.json') -Raw | ConvertFrom-Json
if(-not $evidence.Checked -or $evidence.Case -cne $Case) {throw 'Role checks missing'}
foreach($childId in $evidence.AppPids) {
    if($null -ne (Get-Process -Id $childId -ErrorAction SilentlyContinue)) {throw 'Role child survived cleanup'}
}
'canonical-roles='+$Case
