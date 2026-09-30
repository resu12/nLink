param([string]$Definition)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$body=$Definition.Replace("`r`n","`n")
function Replace-One([string]$Old,[string]$New) {
    if($script:body.Split([string[]]@($Old),[StringSplitOptions]::None).Length -ne 2) {throw "Collector parent seam changed: $Old"}
    $script:body=$script:body.Replace($Old,$New)
}
Replace-One '$script:requestName = $localWriterRequest.Name' @'
    $script:requestName = $localWriterRequest.Name
    if(-not $localWriterRequest.EnableCollectorOutputsForTest()) {throw 'Collector output channel unavailable'}
    $copy=[object].GetMethod('MemberwiseClone',[Reflection.BindingFlags]'Instance,NonPublic').Invoke($localWriterRequest,@())
    if($copy.EnableCollectorOutputsForTest()) {throw 'Copied request opened output channel'}
    $copy.CloseCollectorOutputsForTest()
'@
Replace-One @'
        while ($true) {
            $process.Refresh()
'@ @'
        while ($true) {
            $localWriterRequest.PollCollectorOutputsForTest()
            $process.Refresh()
'@
Replace-One '$private:acceptedWriter = $localWriterRequest.TakeAfterExitWithPayloads($private:frontierGuiReports, $private:frontierParentCustody)' @'
                $private:acceptedWriter = $localWriterRequest.TakeAfterExitWithCollectorOutputsForTest()
                if($null -ne $acceptedWriter) {
                    $copied=[object].GetMethod('MemberwiseClone',[Reflection.BindingFlags]'Instance,NonPublic').Invoke($acceptedWriter,@())
                    if($null -ne $localWriterRequest.TakeCollectorOutputsForTest($copied)) {throw 'Copied completion took output custody'}
                    $script:collectorOutputOwner=$localWriterRequest.TakeCollectorOutputsForTest($acceptedWriter)
                    if($null -eq $script:collectorOutputOwner -or -not $script:collectorOutputOwner.OwnsCompletion($acceptedWriter)) {throw 'Original completion lost output custody'}
                    if($null -ne $localWriterRequest.TakeCollectorOutputsForTest($acceptedWriter)) {throw 'Repeated output adoption admitted'}
                }
'@
Replace-One 'if ($null -ne $localWriterRequest) { $localWriterRequest.Dispose() }' @'
        if ($null -ne $localWriterRequest) {
            try {$localWriterRequest.CloseCollectorOutputsForTest()} finally {$localWriterRequest.Dispose()}
        }
'@
$body
