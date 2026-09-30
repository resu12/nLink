param([string]$RepoRoot)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
if($null -ne ('NLink.Tools.LocalInputReadLease' -as [type])) {throw 'borrower_types_already_loaded'}
$loader=[IO.File]::ReadAllText((Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1'))
$needle='Add-Type -Path @('
if($loader.Split([string[]]@($needle),[StringSplitOptions]::None).Length -ne 2) {throw 'borrower_loader_seam'}
$extra="'"+(Join-Path $PSScriptRoot 'SelectedMembershipBorrower.cs').Replace("'","''")+"',"
$loader=$loader.Replace($needle,$needle+"`n"+$extra)
$loader=$loader.Replace('/define:NLINK_FRONTIER_LEDGER','/define:NLINK_FRONTIER_LEDGER,NLINK_TEST_MEMBERSHIP_BORROWER')
$loader=$loader.Replace('$PSScriptRoot',("'"+(Join-Path $RepoRoot 'tools/FileTransferOps').Replace("'","''")+"'"))
. ([scriptblock]::Create($loader))
