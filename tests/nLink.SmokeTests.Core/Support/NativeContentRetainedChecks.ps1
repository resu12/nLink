# Small non-terminal controls: optional success or exhaustion leaves the real
# payload/witness operations available on the same original native clock.
if($custody.GetType().GetField('payloadAttempted',$flags).GetValue($custody)) {throw 'Optional selection consumed payload slot'}
$visitsBeforeReplay=$owned.GetType().GetField('evaluationVisits',$flags).GetValue($owned)
if($null -ne $custody.TryAcquireContentInputsForTest($true,$pair,$context,$full,$compact,$fullView,$compactView,$events,$selected,$select) -or
    $owned.GetType().GetField('evaluationVisits',$flags).GetValue($owned) -ne $visitsBeforeReplay) {throw 'Optional selection replayed or charged work'}
$set=$custody.TryCapturePayloads($pair,$context,$full,$compact,$fullView,$compactView)
if($null -eq $set -or -not $set.IsCurrent) {throw 'Optional result prevented later original payload capture'}
$payloadRows=$set.GetPayloads()
if($payloadRows.Length -lt 1) {throw 'Retained control did not exercise native payload reading'}
foreach($payload in $payloadRows) {
    $sha=[Security.Cryptography.SHA256]::Create()
    try {
        $bytes=[Text.UTF8Encoding]::new($false,$true).GetBytes($payload.Text)
        $hash=[BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-','').ToLowerInvariant()
        if(-not $set.Owns($payload) -or $bytes.Length -ne $payload.ByteCount -or $hash -cne $payload.Sha256) {throw 'Retained payload ownership/bytes changed'}
    } finally {$sha.Dispose()}
}
$batch=$custody.TryCaptureWitnesses($pair,$context,$full,$compact,$fullView,$compactView,$set,$payloadRows[0],([int[]]@(1,$full.Length)))
if($null -eq $batch -or -not $batch.IsCurrent) {throw 'Optional result prevented later original witness capture'}
$witnessRows=$batch.GetWitnesses()
if($witnessRows.Length -ne 2 -or $batch.LiveProofEligible -or $batch.NativeWriterCustodyEstablished -or $batch.PublicationCompletionIssued) {throw 'Retained witness shape or authority changed'}
foreach($row in $witnessRows) {
    if(-not $batch.Owns($row) -or -not [object]::ReferenceEquals($row.Text,$full[$row.FullIndex-1]) -or
        $row.SourceLine -ne $reader.GetFrontierPhysicalOrigin($row.FullIndex,$row.Text)) {throw 'Retained witness original mapping changed'}
}
if(-not $custody.Owns($pair,$context,$full,$compact,$fullView,$compactView)) {throw 'Later consumers retired original custody'}
'content-native-retained-consumers=original-payloads-witnesses'
