param([string]$Mode,[string[]]$LogPath,[string]$ArtifactDir,[int]$TailMinutes,[string]$LiveRouteProofMode)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$root=Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$text=[IO.File]::ReadAllText((Join-Path $root 'tools/FileTransfer-Ops.ps1'))
$needle='$result = Invoke-FileTransferRetainedAnalysis'
if($text.Split([string[]]@($needle),[StringSplitOptions]::None).Length -ne 2) {throw 'Original primary invocation changed'}
$observer=". '"+(Join-Path $PSScriptRoot 'PublicationLivePrimaryObserver.ps1').Replace("'","''")+"'"
$text=$text.Replace($needle,$observer+"`n        "+$needle)
$text=$text.Replace('$PSScriptRoot',("'"+(Join-Path $root 'tools').Replace("'","''")+"'"))
# The complete original AnalyzeRetained CLI still performs the work and exits.
. ([scriptblock]::Create($text)) @PSBoundParameters
