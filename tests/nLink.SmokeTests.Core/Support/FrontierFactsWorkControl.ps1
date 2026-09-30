param([string]$RepoRoot,[int]$Length=2097152,[int]$Count=147443)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
Add-Type -Path (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierFactsWorkControl.cs') -CompilerOptions '/define:FRONTIER_FACTS_WORK_CONTROL'
[FrontierFactsWorkControl]::Run([NLink.Tools.LocalGuiWriterRequest].Assembly,$Length,$Count)
