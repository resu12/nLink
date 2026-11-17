Set-StrictMode -Version Latest
if ($null -eq ('NLink.Tools.LocalNativeInputCapture' -as [type])) {
    . (Join-Path $PSScriptRoot 'LocalInputReadLease.ps1')
}
