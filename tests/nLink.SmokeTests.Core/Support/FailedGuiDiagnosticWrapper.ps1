param([string]$RepoRoot,[string]$Directory,[string]$GuiMode='nonzero',[int]$WrapperExit=9,[switch]$MutateSource,[switch]$FrontierLaunch)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$root=Join-Path $Directory $GuiMode
$relay=if($env:NLINK_GUI_FRONTIER_RELAY_START_V1 -ceq '1') {
    [NLink.Tools.LocalGuiWriterRequest]::TryAttachRelayReady($env:NLINK_GUI_WRITER_RELAY,
        [guid]$env:NLINK_GUI_WRITER_RELAY_ID,$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID,$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID,$root)
} else {
    [NLink.Tools.LocalGuiWriterRequest]::TryAttachRelay($env:NLINK_GUI_WRITER_RELAY,
        [guid]$env:NLINK_GUI_WRITER_RELAY_ID,$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID,$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID,$root)
}
try {
    if($null -eq $relay) {throw 'Synthetic wrapper relay attachment failed'}
    $completion=$null
    & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/LocalGuiWriterRequest.ps1') `
        -RepoRoot $RepoRoot -Directory $Directory -SingleCase $GuiMode -AcceptedCompletion ([ref]$completion) -ForwardRelay $relay -FrontierLaunch:$FrontierLaunch
    if($MutateSource){[IO.File]::AppendAllText((Join-Path $root 'filetransfer-retained-log-slice.log'),"changed`n")}
} finally {if($null -ne $relay){$relay.Dispose()}}
exit $WrapperExit
