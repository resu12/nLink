param([string]$RepoRoot,[ValidateSet('scope','ownership')][string]$Mode)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1')
$assembly=[NLink.Tools.LocalRetainedSourceRequest].Assembly
$flags=[Reflection.BindingFlags]'Instance,Public,NonPublic'
$clone=[object].GetMethod('MemberwiseClone',$flags)
function New-Raw([string]$Name,[hashtable]$Fields) {
    $type=$assembly.GetType('NLink.Tools.'+$Name,$true)
    $value=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($type)
    foreach($name in $Fields.Keys) {$type.GetField($name,$flags).SetValue($value,$Fields[$name])}
    return $value
}
function Set-Field($Value,[string]$Name,$Data) {$Value.GetType().GetField($Name,$flags).SetValue($Value,$Data)}
function Vector([string]$Name,[object[]]$Items) {
    $array=[Array]::CreateInstance($assembly.GetType('NLink.Tools.'+$Name,$true),$Items.Count)
    for($i=0;$i -lt $Items.Count;$i++) {$array.SetValue($Items[$i],$i)}
    return ,$array
}
function Range([long]$First,[long]$Last) {New-Raw 'LocalFrontierFileManifest+Range' @{First=$First;Last=$Last}}
function Evidence([string]$Value) {
    $sha=[Security.Cryptography.SHA256]::Create()
    try {'fte1-'+[BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value))).Replace('-','').ToLowerInvariant().Substring(0,24)}
    finally {$sha.Dispose()}
}
# Private synthetic objects exercise only comparison and rejection boundaries.
# They have no file handles, native custody, receipt text or transfer authority.
if($Mode -ceq 'scope') {
    $cases=@('valid','unicode','exact-spaces','session','transfer','trimmed-session','pid','creation','hash','same-files',
        'empty-session','none-transfer','index','sender-payload','receiver-request','receiver-process','receiver-creation',
        'receiver-writer','receiver-sequence','sender-request','sender-process','sender-creation','sender-writer','sender-sequence',
        'completion-span','marker-span','order','drain-order','foreign-payload')
    foreach($fault in $cases) {
        $requests=[guid[]]@([guid]::NewGuid(),[guid]::NewGuid());$processes=[int[]]@(10,20);$creations=[long[]]@(100,200)
        $writers=[string[]]@('sender','receiver');$sequences=[long[]]@(1000,2000)
        $payload=New-Raw 'LocalFrontierFileManifest+Payload' @{Completion=(Range 80 80);Marker=(Range 90 90)}
        $empty=Vector 'LocalFrontierFileManifest+Payload' @()
        $manifests=Vector 'LocalFrontierFileManifest' @(
            (New-Raw 'LocalFrontierFileManifest' @{AppRequest=$requests[0];AppId=$processes[0];AppCreation=$creations[0];Writer=$writers[0];Drain=(Range 100 1000);Payloads=$empty}),
            (New-Raw 'LocalFrontierFileManifest' @{AppRequest=$requests[1];AppId=$processes[1];AppCreation=$creations[1];Writer=$writers[1];Drain=(Range 100 2000);Payloads=(Vector 'LocalFrontierFileManifest+Payload' @($payload))}))
        $writerMap=[Collections.Generic.Dictionary[string,long]]::new([StringComparer]::Ordinal)
        $writerMap.Add('sender',1000);$writerMap.Add('receiver',2000)
        $facts=New-Raw 'LocalFrontierParentFacts' @{manifests=$manifests;writers=$writerMap}
        $session='s';$transfer='t'
        if($fault -ceq 'unicode') {$session='session-'+[char]0x017e;$transfer='transfer-'+[char]0x03bb}
        if($fault -cin @('exact-spaces','trimmed-session')) {$session=' s '}
        $pair=New-Raw 'CanonicalTransferPair' @{requests=$requests;processes=$processes;creations=$creations;writers=$writers;sequences=$sequences;
            identities=[string[]]@('file-a','file-b');Session=$session;Transfer=$transfer;Sha256='3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699'}
        $rs=Evidence $session;$rt=Evidence $transfer;$pidValue=20;$creation=200L;$index=0
        switch($fault) {
            'session' {$rs=Evidence 'other'}
            'transfer' {$rt=Evidence 'other'}
            'trimmed-session' {$rs=Evidence 's'}
            'pid' {$pidValue=10}
            'creation' {$creation=201L}
            'hash' {Set-Field $pair 'Sha256' ('0'*64)}
            'same-files' {Set-Field $pair 'identities' ([string[]]@('file-a','file-a'))}
            'empty-session' {Set-Field $pair 'Session' ' '}
            'none-transfer' {Set-Field $pair 'Transfer' '(none)'}
            'index' {$index=1}
            'sender-payload' {Set-Field $manifests[0] 'Payloads' (Vector 'LocalFrontierFileManifest+Payload' @($payload));Set-Field $manifests[1] 'Payloads' $empty}
            'receiver-request' {$requests[1]=[guid]::NewGuid()}
            'receiver-process' {$processes[1]++}
            'receiver-creation' {$creations[1]++}
            'receiver-writer' {$writers[1]='other'}
            'receiver-sequence' {$sequences[1]++}
            'sender-request' {$requests[0]=[guid]::NewGuid()}
            'sender-process' {$processes[0]++}
            'sender-creation' {$creations[0]++}
            'sender-writer' {$writers[0]='other'}
            'sender-sequence' {$sequences[0]++}
            'completion-span' {Set-Field $payload 'Completion' (Range 79 80)}
            'marker-span' {Set-Field $payload 'Marker' (Range 89 90)}
            'order' {Set-Field $payload 'Marker' (Range 80 80)}
            'drain-order' {Set-Field $payload 'Marker' (Range 100 100)}
        }
        if($fault -ceq 'foreign-payload') {
            $accepted=$facts.GetType().GetMethod('HasReceiverPayload',$flags).Invoke($facts,
                [object[]]@($clone.Invoke($payload,$null),$requests[1],$processes[1],$creations[1],$writers[1],$sequences[1]))
        } else {
            $accepted=$pair.GetType().GetMethod('MatchesCanonicalReceiptScope',$flags).Invoke($pair,
                [object[]]@($facts,$index,$rs,$rt,$pidValue,$creation))
        }
        if($accepted -ne ($fault -cin @('valid','unicode','exact-spaces'))) {throw ('Scope comparison outcome: '+$fault)}
    }
    'canonical-same-pair-scope-controls='+$cases.Count
    return
}
$prefix='LocalRetainedSourceRequest+FrontierWorkerInputs+'
$cases=@('owner','retired','null-transfer','copy-transfer','transfer-pair','null-batch','copy-batch','batch-pair',
    'payload-set','null-payload','copy-payload','unresolved','repeated','closed-inputs','time-budget','record-budget')
foreach($fault in $cases) {
    $worker=New-Raw 'LocalRetainedSourceRequest+Worker' @{frontierWorkerSync=[object]::new()}
    $watch=[Diagnostics.Stopwatch]::new()
    # closed=true deliberately prevents these unowned fixtures from reaching
    # native resources; positive native ownership remains an integration gate.
    $inputs=New-Raw 'LocalRetainedSourceRequest+FrontierWorkerInputs' @{owner=$worker;sync=[object]::new();work=$watch;closed=$true;evaluationVisits=0L}
    $pair=New-Raw ($prefix+'EvaluationPair') @{}
    $custody=New-Raw ($prefix+'EvaluationCustody') @{inputs=$inputs;state=3;issued=$pair}
    Set-Field $inputs 'evaluation' $custody
    $transfer=New-Raw ($prefix+'CanonicalTransferEvaluation') @{owner=$custody;Pair=$pair}
    Set-Field $custody 'transferEvaluation' $transfer
    $payload=New-Raw ($prefix+'NativePayload') @{}
    $payloads=New-Raw ($prefix+'NativePayloadSet') @{owner=$custody;Pair=$pair;rows=(Vector ($prefix+'NativePayload') @($payload))}
    Set-Field $custody 'payloadSet' $payloads
    $batch=New-Raw ($prefix+'NativeWitnessBatch') @{owner=$custody;Pair=$pair;Payloads=$payloads;
        '<Payload>k__BackingField'=$payload;originalState=(New-Raw 'LocalFrontierDiagnosticState' @{})}
    Set-Field $custody 'witnessBatch' $batch
    switch($fault) {
        'owner' {$custody=$clone.Invoke($custody,$null)}
        'retired' {Set-Field $custody 'state' 4}
        'null-transfer' {$transfer=$null}
        'copy-transfer' {$transfer=$clone.Invoke($transfer,$null)}
        'transfer-pair' {Set-Field $transfer 'Pair' ($clone.Invoke($pair,$null))}
        'null-batch' {$batch=$null}
        'copy-batch' {$batch=$clone.Invoke($batch,$null)}
        'batch-pair' {Set-Field $batch 'Pair' ($clone.Invoke($pair,$null))}
        'payload-set' {Set-Field $batch 'Payloads' ($clone.Invoke($payloads,$null))}
        'null-payload' {$payload=$null}
        'copy-payload' {$payload=$clone.Invoke($payload,$null)}
        'unresolved' {Set-Field $batch 'originalState' $null}
        'repeated' {Set-Field $custody 'canonicalSamePairAttempted' $true}
        'time-budget' {
            $elapsed=@($watch.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
            $elapsed.SetValue($watch,[long]([Diagnostics.Stopwatch]::Frequency*([NLink.Tools.LocalPublicationInputLimits]::EvaluationMilliseconds+1000)/1000))
        }
        'record-budget' {Set-Field $inputs 'evaluationVisits' ([long][NLink.Tools.LocalPublicationInputLimits]::EvaluationVisits)}
    }
    if($custody.TryCheckCanonicalSamePairWitness($transfer,$batch,$payload,'s','t',20,200L)) {throw ('Unowned join accepted: '+$fault)}
    $attempted=$custody.GetType().GetField('canonicalSamePairAttempted',$flags).GetValue($custody)
    if($attempted -ne ($fault -cin @('repeated','closed-inputs','time-budget','record-budget')) -or $watch.IsRunning) {throw ('Attempt/budget boundary changed: '+$fault)}
    if($fault -ceq 'time-budget' -and $watch.ElapsedMilliseconds -lt ([NLink.Tools.LocalPublicationInputLimits]::EvaluationMilliseconds+1000)) {throw 'Original clock was reset'}
    if($fault -ceq 'record-budget' -and $inputs.GetType().GetField('evaluationVisits',$flags).GetValue($inputs) -le [NLink.Tools.LocalPublicationInputLimits]::EvaluationVisits) {throw 'Original visits were reset'}
}
'canonical-same-pair-ownership-controls='+$cases.Count
