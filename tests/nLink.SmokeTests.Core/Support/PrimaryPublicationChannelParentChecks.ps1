param([string]$Mode,$Request,$OtherRequest,$WorkerResult)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
# Deliberate transport corruption is test-only. No such raw handle/API is given
# to a production formatter; same-account malicious MMF writes are out of scope.
$controlMap=$null; $control=$null; $payloadMap=$null; $payload=$null
try {
    $name=$Request.Name+'.publication'
    $controlMap=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($name+'.control')
    $control=$controlMap.CreateViewAccessor(0,384)
    $payloadMap=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($name+'.payload')
    $payload=$payloadMap.CreateViewAccessor(0,262144)
    switch($Mode) {
        'channel-control-partial' { $control.Write(4,[int]1) }
        'channel-wrong-request' { $control.WriteArray(24,[guid]::NewGuid().ToByteArray(),0,16) }
        'channel-wrong-worker' { $control.Write(8,[int]$PID) }
        'channel-wrong-creation' { $control.Write(16,[long]1) }
        'channel-wrong-request-hash' { $control.WriteArray(72,[Text.Encoding]::ASCII.GetBytes(('0'*64)),0,64) }
        'channel-payload-corrupt' { $payload.Write(0,[byte]0) }
        'channel-length-overflow' { $control.Write(264,[int]262145) }
        'channel-wrong-namespace' {
            $otherControlMap=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($OtherRequest.Name+'.publication.control')
            $otherPayloadMap=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($OtherRequest.Name+'.publication.payload')
            $otherControl=$otherControlMap.CreateViewAccessor(0,384); $otherPayload=$otherPayloadMap.CreateViewAccessor(0,262144)
            try {
                $header=[byte[]]::new(384); [void]$control.ReadArray(0,$header,0,384); $otherControl.WriteArray(0,$header,0,384)
                $bytes=[byte[]]::new($control.ReadInt32(264)); [void]$payload.ReadArray(0,$bytes,0,$bytes.Length)
                $otherPayload.WriteArray(0,$bytes,0,$bytes.Length)
            } finally { $otherControl.Dispose(); $otherPayload.Dispose(); $otherControlMap.Dispose(); $otherPayloadMap.Dispose() }
            if($null -ne $OtherRequest.TakePublicationAfterExit() -or $null -ne $OtherRequest.TakePublicationAfterExit()) {
                throw 'Same attempt/worker but different parent namespace admitted copied completion'
            }
        }
    }
} finally {
    if($null -ne $control) { $control.Dispose() }; if($null -ne $payload) { $payload.Dispose() }
    if($null -ne $controlMap) { $controlMap.Dispose() }; if($null -ne $payloadMap) { $payloadMap.Dispose() }
}
if($Mode -ceq 'channel-parent-cancel') { $Request.Dispose() }
$received=$Request.TakePublicationAfterExit()
$expected=$Mode -like 'valid-*' -or $Mode -ceq 'channel-wrong-namespace'
if(($null -ne $received) -ne $expected) { throw "Parent channel result drift: $Mode" }
if($null -ne $Request.TakePublicationAfterExit()) { throw 'Parent channel read replay admitted' }
if($expected) {
    $bytes=$received.CopyPayload(); $json=[Text.Encoding]::ASCII.GetString($bytes); $body=$json | ConvertFrom-Json
    $sha=[Security.Cryptography.SHA256]::Create()
    try { $actualHash=[BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
    $receipt=$received.Completion
    if($received.OutputNamespace -cne ($Request.Name+'.publication') -or $body.request.outputNamespace -cne $received.OutputNamespace -or
        $body.request.requestId -cne $Request.RequestId.ToString('D') -or $receipt.RequestId -ne $Request.RequestId -or
        $receipt.PayloadSha256 -cne $WorkerResult.Completion.PayloadSha256 -or $receipt.PayloadBytes -ne $bytes.Length -or
        $receipt.PayloadSha256 -cne $actualHash -or
        $receipt.FullInvocationId.ToString('D') -cne $WorkerResult.Completion.FullInvocationId -or
        $receipt.CompactInvocationId.ToString('D') -cne $WorkerResult.Completion.CompactInvocationId) { throw 'Parent receipt differs from exact original issuer' }
    $bytes[0]=0
    if([Text.Encoding]::ASCII.GetString($received.CopyPayload()) -cne $json) { throw 'Parent payload copy is mutable authority' }
    foreach($value in @($received,$receipt)) {
        if($value.GetType().GetConstructors().Count -ne 0) { throw 'Public transport completion constructor' }
        foreach($property in $value.GetType().GetProperties()) { if($null -ne $property.GetSetMethod()) { throw 'Public transport completion setter' } }
    }
}
# Only a zero-argument take and the typed parent creation are publicly exposed.
$methods=@([NLink.Tools.LocalRetainedSourceRequest].GetMethods() | Where-Object Name -eq 'TakePublicationAfterExit')
if($methods.Count -ne 1 -or $methods[0].GetParameters().Count -ne 0) { throw 'Caller expected-digest API exposed' }
if($null -ne [NLink.Tools.LocalRetainedSourceRequest+Worker].GetMethod('Publish') -or
    $null -ne [NLink.Tools.LocalRetainedSourceRequest].GetNestedType('PublicationChannel')) { throw 'Public completion writer endpoint exposed' }
return @{Received=$expected;ReplayRejected=$true;ExpectedDigestSuppliedByCaller=$false;LiveAdoption=$false}
