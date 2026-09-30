param([string]$RepoRoot)
if($null -ne ('NLink.Tools.LocalGuiWriterRequest' -as [type])) {throw 'attempt_dispatch_profile_already_loaded'}
function Add-Type {
    param([string[]]$Path,$CompilerParameters,[string]$CompilerOptions,[string]$ErrorAction)
    $arguments=@{Path=@($Path)+(Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatch.cs')+
        (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptRoles.cs')+
        (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptWriters.cs')+
        (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptSource.cs')+
        (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDestinationGui.cs')+
        (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadWrapper.cs')+
        (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadParent.cs')+
        (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadPrimary.cs')+
        (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierWitnessResolutionDiagnostics.cs')+
        (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/AttemptSenderSourceChannel.cs')+
        (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/AttemptPeerProcessChannel.cs')+
        (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/OwnedSourceCheckProfile.cs')+
        (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptSenderRead.cs');ErrorAction='Stop'}
    if($null -ne $CompilerParameters) {
        $CompilerParameters.CompilerOptions += ',NLINK_TEST_ATTEMPT_DISPATCH'
        $arguments.CompilerParameters=$CompilerParameters
    } else {$arguments.CompilerOptions=$CompilerOptions+',NLINK_TEST_ATTEMPT_DISPATCH'}
    Microsoft.PowerShell.Utility\Add-Type @arguments
}
try {. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')}
finally {Remove-Item Function:Add-Type}
