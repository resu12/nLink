Set-StrictMode -Version Latest
if ($null -eq ('NLink.Tools.LocalFrontierAppRequest' -as [type])) {
    # The GUI and app request owners must share one codec type in this process.
    # A managed app that already loaded Core keeps Core's identical wire codec.
    . (Join-Path $PSScriptRoot 'LocalInputReadLease.ps1')
}
if ($null -eq ('NLink.Tools.LocalFrontierAppRequest' -as [type])) { throw 'native_frontier_app_request_type_unavailable' }
