param([string]$RepoRoot)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1')
$type=[NLink.Tools.LocalRetainedSourceRequest+NativeMembership]
if(@($type.GetMembers([Reflection.BindingFlags]'Public,NonPublic,Instance,Static') |
    Where-Object {$_.Name -match 'Collector|collector'}).Count -ne 0) {throw 'Collector origin API leaked into production loader'}
$properties=@($type.GetProperties() | ForEach-Object Name | Sort-Object)
if(($properties -join ',') -cne 'CodeUnitVisits,RecordVisits,SelectedIndex') {throw 'Production membership API changed'}
'collector-origin-join-api-absent'
