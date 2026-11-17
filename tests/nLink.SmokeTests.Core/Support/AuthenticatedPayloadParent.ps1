param([string]$RepoRoot,[string]$Directory)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$env:NLINK_TEST_AUTHENTICATED_PAYLOAD_HANDOFF='1'
$env:NLINK_TEST_SELECTED_NATIVE_WRITER='1'; $env:NLINK_TEST_NATIVE_WITNESS_ADAPTER='1'
$env:NLINK_TEST_MEMBERSHIP_BORROWER='1'; $env:NLINK_TEST_COLLECTOR_PRIMARY='1'
$env:NLINK_TEST_MEMBERSHIP_BORROWER_CASE='active'
if(-not $env:NLINK_TEST_PAYLOAD_OWNER_CASE) {$env:NLINK_TEST_PAYLOAD_OWNER_CASE='active'}
foreach($flag in @('NLINK_TEST_NATIVE_PRIMARY_FILES','NLINK_TEST_COLLECTOR_MEMBERSHIP','NLINK_TEST_TERMINAL_PAYLOAD_JOIN',
    'NLINK_TEST_PRE_VERDICT_INTEGRITY','NLINK_PUBLICATION_TERMINAL_SOURCE_PROBE','NLINK_TEST_SELECTED_ORIGIN_JOIN')) {
    [Environment]::SetEnvironmentVariable($flag,'0','Process')
}
& (Join-Path $PSScriptRoot 'TerminalRequestFileBinding.ps1') $RepoRoot
$files=$null
try {
    $files=[NLink.Tests.HeldFileNativeParent]::Receive($env:NLINK_TEST_NATIVE_FILE_REQUEST,
        $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID,$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID,(Join-Path $Directory 'nlink.log'))
    [Console]::Out.WriteLine('nlink-native-parent-adopted:'+$files.RequestId.ToString('D')); [Console]::Out.Flush()
    $files.ConfirmProducerRelease()
    $null=& (Join-Path $PSScriptRoot 'SelectedNativeWriterGenerated.ps1') -RepoRoot $RepoRoot -Directory $Directory `
        -RunId $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID -ScenarioId $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID -NativeFiles $files
    if($files.IsCurrent) {throw 'authenticated_payload_parent_not_released'}
} finally {if($null -ne $files) {$files.Dispose()}}
