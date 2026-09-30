param([string]$RepoRoot,[string]$Directory)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
if($env:NLINK_TEST_NATIVE_REQUEST_BINDING -ceq '1') {
    & (Join-Path $PSScriptRoot 'TerminalRequestFileBinding.ps1') $RepoRoot
} else {Add-Type -Path (Join-Path $PSScriptRoot 'HeldFileNativeParent.cs') -ErrorAction Stop}
$private:nativeFiles=$null
try {
    $private:nativeFiles=[NLink.Tests.HeldFileNativeParent]::Receive($env:NLINK_TEST_NATIVE_FILE_REQUEST,
        $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID,$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID,(Join-Path $Directory 'nlink.log'))
    $duplicateRejected=$false
    try {[void][NLink.Tests.HeldFileNativeParent]::Receive('unused','unused','unused','unused')}
    catch {if($_.Exception.ToString().Contains('native_parent_already_adopted')) {$duplicateRejected=$true} else {throw}}
    if(-not $duplicateRejected) {throw 'Repeated native adoption admitted'}
    [Console]::Out.WriteLine('nlink-native-parent-adopted:'+$private:nativeFiles.RequestId.ToString('D'))
    [Console]::Out.Flush()
    $private:nativeFiles.ConfirmProducerRelease()
    $files=$private:nativeFiles.Describe()
    $request=$private:nativeFiles.RequestId.ToString('D')
    # The default path remains source-only; the explicit test branch associates
    # this actual owner with the original request, never its exported metadata.
    if($env:NLINK_TEST_NATIVE_REQUEST_BINDING -ceq '1') {
        & (Join-Path $PSScriptRoot 'PublicationTerminalSourceGenerated.ps1') $RepoRoot $Directory -NativeFiles $private:nativeFiles
    } else {& (Join-Path $PSScriptRoot 'PublicationTerminalSourceGenerated.ps1') $RepoRoot $Directory}
    $forwarded=$env:NLINK_TEST_NATIVE_PRIMARY_FILES -ceq '1' -and -not $env:NLINK_TEST_NATIVE_PRIMARY_MUTATION
    if($private:nativeFiles.IsCurrent -eq $forwarded) {throw 'Native parent files have unexpected handoff lifetime'}
} finally {if($null -ne $private:nativeFiles) {$private:nativeFiles.Dispose()}}
if($private:nativeFiles.IsCurrent) {throw 'Native parent owner revived after disposal'}
[IO.File]::WriteAllText((Join-Path $Directory 'native-parent-result.json'),([ordered]@{
    Schema='nlink.test-native-parent-files/v1';Status='verified_offline_native_parent_adoption_only'
    RequestId=$request;ProcessId=$PID;Files=$files;RepeatedAdoptionRejected=$duplicateRejected;Disposed=$true
    ForwardedToPrimary=$forwarded
    HashesAreProducerMeasurements=$true;PrimaryFileOwnerHandoffProven=$false;AcceptanceChanged=$false;Live=$false
} | ConvertTo-Json -Depth 6 -Compress))
