param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId)
& (Join-Path $PSScriptRoot 'PrimaryPublicationAdoptionGenerated.ps1') -RepoRoot $RepoRoot -Directory $Directory -RunId $RunId -ScenarioId $ScenarioId -OwnerSelection RouteAcceptance
