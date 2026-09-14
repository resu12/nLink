Set-StrictMode -Version Latest

if ($null -eq ('NLink.Tools.LocalGuiWriterRequest' -as [type])) {
    . (Join-Path $PSScriptRoot 'LocalInputReadLease.ps1')
}
if ($null -eq ('NLink.Tools.LocalGuiWriterRequest' -as [type])) { throw 'native_gui_writer_request_type_unavailable' }
