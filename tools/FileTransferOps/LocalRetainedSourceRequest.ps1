Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'LocalInputReadLease.ps1')
if ($null -eq ('NLink.Tools.LocalRetainedSourceRequest' -as [type])) { throw 'native_source_request_type_unavailable' }

function Open-FileTransferRetainedSourceOwner {
    param([string]$Name, [guid]$RequestId, [string]$Path, [string]$Run, [string]$Scenario, [switch]$FrontierReady)
    if ($FrontierReady) {
        return [NLink.Tools.LocalRetainedSourceRequest]::TryAttachFrontierReady($Name, $RequestId, $Path, $Run, $Scenario)
    }
    return [NLink.Tools.LocalRetainedSourceRequest]::TryAttach($Name, $RequestId, $Path, $Run, $Scenario)
}

function New-FileTransferRetainedSourceRequest {
    param($ReadLease, [string]$AttemptId, [string]$Owner, [long]$LineCount, $WriterContext = $null, [bool]$PublicationRequested = $false)
    try {
        if ($null -eq $ReadLease -or -not $ReadLease.IsVerified -or $AttemptId -cnotmatch '\A[0-9a-f]{32}\z') { return $null }
        if ($null -ne $WriterContext) {
            try {
                if ($PublicationRequested) {
                    $request = [NLink.Tools.LocalRetainedSourceRequest]::TryCreateWithPublication([guid]$AttemptId, $Owner,
                        [string]$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID, [string]$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID,
                        $ReadLease, $LineCount, $WriterContext)
                    if ($null -ne $request) { return $request }
                }
                $request = [NLink.Tools.LocalRetainedSourceRequest]::TryCreateWithWriter([guid]$AttemptId, $Owner,
                    [string]$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID, [string]$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID,
                    $ReadLease, $LineCount, $WriterContext)
                if ($null -ne $request) { return $request }
            }
            catch { } # Missing/mismatched writer ownership does not revoke source ownership.
        }
        return [NLink.Tools.LocalRetainedSourceRequest]::TryCreate([guid]$AttemptId, $Owner,
            [string]$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID, [string]$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID,
            $ReadLease, $LineCount)
    }
    catch { return $null }
}
