param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId)
& (Join-Path $PSScriptRoot 'PrimaryPublicationWitnessGenerated.ps1') -RepoRoot $RepoRoot -Directory $Directory `
    -RunId $RunId -ScenarioId $ScenarioId -Lane completion `
    -CaseSelection @('valid-0','valid-7','completion-time-budget','completion-payload-budget','completion-native-cancel','completion-writer-cancel')
