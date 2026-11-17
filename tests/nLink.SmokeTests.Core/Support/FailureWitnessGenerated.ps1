param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId)
& (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/NativeInputCaptureGenerated.ps1') `
    -RepoRoot $RepoRoot -Directory $Directory -RunId $RunId -ScenarioId $ScenarioId -TestReadLease -TestWitness
