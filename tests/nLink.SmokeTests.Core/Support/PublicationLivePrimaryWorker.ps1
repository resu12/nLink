param([string]$Mode,[string[]]$LogPath,[string]$ArtifactDir,[int]$TailMinutes,[string]$LiveRouteProofMode)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
# Keep the controlled GUI's readiness profile unchanged. Legacy diagnostic
# fixtures and selected route-cycle acceptance explicitly choose analysis scope.
if(-not [string]::IsNullOrWhiteSpace($env:NLINK_PUBLICATION_INTEGRATION_SETTINGS)) {
    $settings=Get-Content -LiteralPath $env:NLINK_PUBLICATION_INTEGRATION_SETTINGS -Raw | ConvertFrom-Json
    if($null -ne $settings.PSObject.Properties['AnalysisProofMode']) {
        $PSBoundParameters['LiveRouteProofMode']=[string]$settings.AnalysisProofMode
    }
}
$root=Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
if($env:NLINK_TEST_AUTHENTICATED_PAYLOAD_HANDOFF -ceq '1' -and $env:NLINK_TEST_SELECTED_NATIVE_WRITER -cne '1') {throw 'authenticated_payload_profile_scope'}
if($env:NLINK_TEST_SELECTED_NATIVE_WRITER -ceq '1') {
    & (Join-Path $PSScriptRoot 'TerminalRequestFileBinding.ps1') $root
} elseif($env:NLINK_TEST_MEMBERSHIP_BORROWER -ceq '1') {
    if($env:NLINK_TEST_NATIVE_PRIMARY_FILES -ceq '1') {throw 'borrower_native_profile_not_implemented'}
    & (Join-Path $PSScriptRoot 'SelectedMembershipBorrowerLoader.ps1') $root
}
if($env:NLINK_TEST_NATIVE_PRIMARY_FILES -ceq '1') {
    & (Join-Path $PSScriptRoot 'TerminalRequestFileBinding.ps1') $root
}
$text=[IO.File]::ReadAllText((Join-Path $root 'tools/FileTransfer-Ops.ps1'))
$needle='$result = Invoke-FileTransferRetainedAnalysis'
if($text.Split([string[]]@($needle),[StringSplitOptions]::None).Length -ne 2) {throw 'Original primary invocation changed'}
$observer=". '"+(Join-Path $PSScriptRoot 'PublicationLivePrimaryObserver.ps1').Replace("'","''")+"'"
$text=$text.Replace($needle,$observer+"`n        "+$needle)
$text=$text.Replace('$PSScriptRoot',("'"+(Join-Path $root 'tools').Replace("'","''")+"'"))
# The complete original AnalyzeRetained CLI still performs the work and exits.
. ([scriptblock]::Create($text)) @PSBoundParameters
