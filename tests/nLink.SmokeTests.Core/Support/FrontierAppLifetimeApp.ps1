param([string]$RepoRoot,[string]$Directory,[string]$Case,[int]$Ordinal)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
[void][Reflection.Assembly]::LoadFrom((Join-Path $RepoRoot 'src/nLink.Core/bin/Debug/net8.0/nLink.Core.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/bin/Debug/net8.0/NLink.SmokeTests.Core.dll'))
[void][NLink.SmokeTests.FrontierAppLifetimeTests]::RunAppAsync($Directory,$Case,$Ordinal).GetAwaiter().GetResult()
