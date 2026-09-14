Set-StrictMode -Version Latest
if ($null -eq ('NLink.Tools.LocalInputReadLease' -as [type])) {
    if ($null -ne ('NLink.Tools.LocalNativeInputCapture' -as [type])) { throw 'incompatible_local_ownership_types' }
    Add-Type -Path @(
        (Join-Path $PSScriptRoot 'LocalInputReadLease.cs'),
        (Join-Path $PSScriptRoot 'LocalRetainedSourceRequest.cs'),
        (Join-Path $PSScriptRoot 'LocalGuiWriterRequest.cs'),
        (Join-Path $PSScriptRoot 'LocalGuiWriterRelay.cs'),
        (Join-Path $PSScriptRoot 'LocalNativeInputCapture.cs'),
        (Join-Path $PSScriptRoot 'LocalPrimaryPublicationCompletion.cs'),
        (Join-Path $PSScriptRoot 'LocalPrimaryPublicationChannel.cs'),
        (Join-Path $PSScriptRoot 'LocalPublicationAdoption.cs')) -ErrorAction Stop
}
