param([string]$RepoRoot)
function Add-Type {
    param([string[]]$Path,$CompilerParameters,[string]$CompilerOptions,[string]$ErrorAction)
    $arguments=@{Path=@($Path)+(Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/BufferedLedgerProbe.cs')+(Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/ProcessingCustodyProbe.cs')+(Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/RecoveryBufferProbe.cs')+
        (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/NativeContentScaleProbe.cs');ErrorAction='Stop'}
    if($null -ne $CompilerParameters) {
        $CompilerParameters.CompilerOptions+=',NLINK_TEST_BUFFERED_LEDGER,NLINK_TEST_NATIVE_CONTENT_ACQUISITION'
        $arguments.CompilerParameters=$CompilerParameters
    } else {$arguments.CompilerOptions=$CompilerOptions+',NLINK_TEST_BUFFERED_LEDGER,NLINK_TEST_NATIVE_CONTENT_ACQUISITION'}
    Microsoft.PowerShell.Utility\Add-Type @arguments
}
try {. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')}
finally {Remove-Item Function:Add-Type}
