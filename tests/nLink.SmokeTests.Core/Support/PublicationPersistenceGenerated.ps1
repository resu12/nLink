param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
foreach($mode in @('write','existing','write-error','guards')) {
    & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/PublicationPositiveIntegrationGenerated.ps1') `
        -RepoRoot $RepoRoot -Directory $Directory -RunId $RunId -ScenarioId $ScenarioId -PersistenceMode $mode
}
'persistence-integration={"Cases":4,"RealPrimaryAndReceipt":true,"OriginalFailurePreserved":true,"Live":false}'
