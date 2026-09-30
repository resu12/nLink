param([string]$RepoRoot,[string]$Directory,[string]$Role,[string]$Signal,[string]$Case)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
[void][Reflection.Assembly]::LoadFrom((Join-Path $RepoRoot 'src/nLink.Core/bin/Debug/net8.0/nLink.Core.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/bin/Debug/net8.0/NLink.SmokeTests.Core.dll'))
[void][NLink.SmokeTests.FrontierAttemptWritersTests]::RunAppAsync($Directory,$Role,$Signal,$Case).GetAwaiter().GetResult()
