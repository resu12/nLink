param([string]$RepoRoot,[switch]$ReadProfile,[switch]$SelectionProfile,[switch]$SelectionComponentProfile,[switch]$TerminalSelection)
if($null -ne ('NLink.Tools.LocalRetainedSourceRequest' -as [type])) {throw 'content_native_profile_already_loaded'}
if($SelectionComponentProfile -and $ReadProfile) {throw 'content_selection_component_profile_with_source_profile'}
if($TerminalSelection -and $ReadProfile) {throw 'content_terminal_selection_with_source_profile'}
function Add-Type {
    param([string[]]$Path,$CompilerParameters,[string]$CompilerOptions,[string]$ErrorAction)
    $arguments=@{Path=@($Path)+(Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/NativeContentAcquisition.cs')+
        (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/NativeContentScaleProbe.cs')+
        (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/NativeGuiHeaderSnapshotParity.cs');ErrorAction='Stop'}
    $defines=',NLINK_TEST_NATIVE_CONTENT_ACQUISITION'
    if($TerminalSelection) {
        $arguments.Path+=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/NativeContentTerminalSelection.cs'
        $defines+=',NLINK_TEST_NATIVE_CONTENT_TERMINAL_SELECTION'
    }
    if($ReadProfile) {
        # Reuse the reviewed diagnostic partials and original native-read hooks.
        $arguments.Path+=@('FrontierAttemptDispatch','FrontierAttemptRoles','FrontierAttemptWriters','FrontierAttemptSource',
            'FrontierAttemptDestinationGui','FrontierAttemptPayloadWrapper','FrontierAttemptPayloadParent','FrontierAttemptPayloadPrimary',
            'FrontierWitnessResolutionDiagnostics','AttemptSenderSourceChannel','AttemptPeerProcessChannel','OwnedSourceCheckProfile',
            'FrontierAttemptSenderRead','NativeContentReadProfile' | ForEach-Object {Join-Path $RepoRoot ('tests/nLink.SmokeTests.Core/Support/'+$_+'.cs')})
        $defines+=',NLINK_TEST_ATTEMPT_DISPATCH,NLINK_TEST_NATIVE_READ_PROFILE'
    }
    if($SelectionProfile -or $SelectionComponentProfile) {
        $arguments.Path+=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/NativeContentSelectionProfile.cs'
        $defines+=',NLINK_TEST_NATIVE_SELECTION_PROFILE'
    }
    if($SelectionComponentProfile) {$defines+=',NLINK_TEST_NATIVE_SELECTION_COMPONENT_PROFILE'}
    if($null -ne $CompilerParameters) {
        $CompilerParameters.CompilerOptions+=$defines;$arguments.CompilerParameters=$CompilerParameters
    } else {$arguments.CompilerOptions=$CompilerOptions+$defines}
    Microsoft.PowerShell.Utility\Add-Type @arguments
}
try {. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1')}
finally {Remove-Item Function:Add-Type}
