param([string]$RepoRoot,[ValidateSet('differential','scale')][string]$Mode)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalNativeFrontierWitness.ps1')
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierInputRowsOracle.ps1')
$source=[IO.File]::ReadAllText((Join-Path $RepoRoot 'tools/FileTransferOps/LocalFrontierInputRows.cs'))
$limit=if($Mode -ceq 'scale'){1073741824}else{1048576}
$types=@'
namespace NLink.Tools {
 public static class LocalPublicationInputLimits { public const long ProducerCodeUnits = LIMIT; }
}
public sealed class ProjectionVisits {
 public int Count, FailAt;
 public System.Action Visit { get { return delegate { Count++; if (Count == FailAt) throw new System.InvalidOperationException("frontier_evaluation_budget"); }; } }
}
'@
$arguments=@{}
if($PSVersionTable.PSVersion.Major -lt 6) {$arguments.ReferencedAssemblies=@('System.dll','System.Core.dll',[Management.Automation.PSObject].Assembly.Location)}
Add-Type -TypeDefinition ($source+"`n"+$types.Replace('LIMIT',[string]$limit)) @arguments
function CanonicalRow($row) {
    $fields=[Collections.Generic.List[object]]::new();$names=[string[]]@($row.Fields.Keys)
    [Array]::Sort($names,[StringComparer]::Ordinal)
    foreach($name in $names) {$fields.Add([ordered]@{Name=$name;Value=$row.Fields[$name];Type=$row.Fields[$name].GetType().FullName})}
    return ([ordered]@{Position=$row.Position;Fields=$fields;
        Identity=[ordered]@{Session=$row.Identity.Session;Transfer=$row.Identity.Transfer;Error=$row.Identity.Error};
        DataSession=[ordered]@{Token=$row.DataSession.Token;Present=$row.DataSession.Present;Projected=$row.DataSession.Projected;Error=$row.DataSession.Error}} | ConvertTo-Json -Depth 5 -Compress)
}
$script:checks=0
function Check([string]$Name,[string[]]$Lines,[string]$Fault='',[int]$FailAt=0,[string]$Expected='') {
    $events=@(for($i=0;$i -lt $Lines.Length;$i++) {[pscustomobject]@{RawLine=$Lines[$i];FilePath='original'}})
    $failure=[pscustomobject]@{Event=$events[$events.Count-1]}
    $index=[pscustomobject]@{AllEvents=$events;LogFiles=@('original')}
    switch($Fault) {
        'raw-copy' {$events[0].RawLine=[string]::new($Lines[0].ToCharArray())}
        'path-case' {$events[0].FilePath='ORIGINAL'}
        'path-null-character' {$events[0].FilePath="ori`0ginal"}
        'failure-copy' {$failure.Event=[pscustomobject]@{RawLine=$events[-1].RawLine;FilePath='original'}}
        'failure-duplicate' {$Lines[1]=$Lines[0];$events[1]=$events[0];$failure.Event=$events[0]}
    }
    $before=@($Lines,$events,$failure) | ConvertTo-Json -Depth 5 -Compress
    $results=@(foreach($compiled in @($false,$true)) {
        $rows=[Collections.Generic.List[object]]::new();$visits=[ProjectionVisits]::new();$visits.FailAt=$FailAt
        $position=-1;$code='';$watch=[Diagnostics.Stopwatch]::StartNew()
        try {
            if($compiled){$position=[NLink.Tools.LocalFrontierInputRows]::Project($Lines,$events,$index.LogFiles[0],$failure.Event,$rows,$visits.Visit,@{})}
            else {$position=Invoke-FrontierInputRowsOracle $Lines $index $failure $rows $visits.Visit}
        } catch {
            $match=[regex]::Match($_.Exception.Message,'(?:native_frontier_[a-z_]+|frontier_evaluation_budget)')
            if(-not $match.Success) {throw ('Unexpected projection exception '+$Name+': '+$_.Exception.Message)}
            $code=$match.Value
        }
        $watch.Stop()
        [pscustomobject]@{Rows=$rows;Position=$position;Visits=$visits.Count;Code=$code;Milliseconds=$watch.Elapsed.TotalMilliseconds}
    })
    $a=$results[0];$b=$results[1]
    if($a.Code -cne $Expected -or $b.Code -cne $a.Code -or $a.Visits -ne $b.Visits -or $a.Rows.Count -ne $b.Rows.Count -or
        (-not $a.Code -and $a.Position -ne $b.Position)) {throw ('Projection mismatch '+$Name+': '+($results | Select-Object Position,Visits,Code | ConvertTo-Json -Compress))}
    for($i=0;$i -lt $a.Rows.Count;$i++) {if((CanonicalRow $a.Rows[$i]) -cne (CanonicalRow $b.Rows[$i])) {throw ('Row mismatch '+$Name+': '+$i+' / '+(CanonicalRow $a.Rows[$i])+' / '+(CanonicalRow $b.Rows[$i]))}}
    if((@($Lines,$events,$failure) | ConvertTo-Json -Depth 5 -Compress) -cne $before) {throw 'Projection changed original inputs'}
    $script:checks++
    return [pscustomobject]@{Name=$Name;Count=$Lines.Length;Visits=$b.Visits;Rows=$b.Rows.Count;OracleMilliseconds=$a.Milliseconds;CompiledMilliseconds=$b.Milliseconds}
}
if($Mode -ceq 'scale') {
    $line='event=filetransfer_v6_receiver_state_deferred; log_writer_id=w; session_id=[redacted]; transfer_id=[redacted]; frontier_scope_session_id=fte1-000000000000000000000001; frontier_scope_transfer_id=fte1-000000000000000000000002; data_session_identity=[redacted]:1; frontier_scope_data_session_identity=fte1-000000000000000000000003; next_chunk_index=7; route=post_tuna_fallback_v6; protocol_version=6'
    $comparison=Check 'bounded-timing-comparison' ([string[]](@($line)*1024))
    $full=[string[]](@($line)*73955)
    $events=@(for($i=0;$i -lt $full.Length;$i++) {[pscustomobject]@{RawLine=$full[$i];FilePath='original'}})
    $rows=[Collections.Generic.List[object]]::new();$visits=[ProjectionVisits]::new()
    $watch=[Diagnostics.Stopwatch]::StartNew()
    $position=[NLink.Tools.LocalFrontierInputRows]::Project($full,$events,'original',$events[-1],$rows,$visits.Visit,@{})
    $watch.Stop()
    if($position -ne $full.Length-1 -or $rows.Count -ne $full.Length -or $visits.Count -ne $full.Length) {throw 'Scale projection count/origin mismatch'}
    [ordered]@{Comparison=$comparison;ScaleRows=$rows.Count;ScaleVisits=$visits.Count;ScaleMilliseconds=$watch.Elapsed.TotalMilliseconds;SyntheticOnly=$true} | ConvertTo-Json -Depth 4 -Compress
    'frontier-input-rows-scale=passed';return
}
$culture=[Threading.Thread]::CurrentThread.CurrentCulture
try {
    foreach($cultureName in @('en-US','tr-TR','sv-SE')) {
        [Threading.Thread]::CurrentThread.CurrentCulture=[Globalization.CultureInfo]::GetCultureInfo($cultureName)
        $session=ConvertTo-FileTransferCoordinatorEvidenceToken 'session';$transfer=ConvertTo-FileTransferCoordinatorEvidenceToken 'transfer';$data=ConvertTo-FileTransferCoordinatorEvidenceToken 'data:1'
        $raw='session_id=session; transfer_id=transfer';$projection="frontier_scope_session_id=$session; frontier_scope_transfer_id=$transfer"
        $unicode=([string][char]0x3bb)+[char]0xe9+[char]0x130+[char]0x131+[char]0x6f22+[char]0x5b57+[char]::ConvertFromUtf32(0x1f600)
        $cases=@('', ' ', 'event=x', 'event=x; note=a=b', ';;event=x;;', "event=x; name=$unicode", "[time] [INFO] [source] event=x; $raw", (([string][char]0xfeff)+"event=x; $raw"),
            "event=x; $raw", "event=x; $raw; $projection", "event=x; session_id=[redacted]; transfer_id=[redacted]; $projection",
            "event=x; session_id=sess_[redacted]; transfer_id=[redacted]; $projection", "event=x; session_id=session", "event=x; transfer_id=transfer",
            "event=x; $raw; frontier_scope_session_id=$session", "event=x; $raw; $projection`nother=x", "event=x; $raw; $projection`rother=x",
            "event=x; SESSION_ID=session; TRANSFER_ID=transfer", 'event=x; note=first; NOTE=last', 'event=x; event=y', 'event=x; EVENT=x',
            'event=x; __nlink_duplicate_structured_field=literal; note=x; note=y', 'event=x; __nlink_duplicate_writer_evidence_field=literal; EVENT=y',
            "event=x; k=1; $([char]0x212a)=2; I=3; $([char]0x131)=4; $([char]0x130)=5; i=6", "event=x; session_id=ori`0ginal; transfer_id=transfer")
        foreach($body in $cases) {$null=Check $cultureName @($body)}
        foreach($value in @('', '(none)', '[redacted]', 'prefix[redacted]', 'sess_[redacted]', 'session', 'other', $unicode, $session)) {
            foreach($projected in @('', ('; '+$projection))) {$null=Check 'scope-value' @("event=x; session_id=$value; transfer_id=transfer$projected")}
        }
        foreach($bad in @('', '(none)', ('fte2-'+('0'*24)), ('fte1-'+('A'*24)), ('fte1-'+('0'*23)), ('fte1-'+('0'*25)), ($session+'-suffix'))) {
            $null=Check 'projection-shape' @("event=x; $raw; frontier_scope_session_id=$bad; frontier_scope_transfer_id=$transfer")
        }
        foreach($key in @('session_id','transfer_id','frontier_scope_session_id','frontier_scope_transfer_id')) {
            $null=Check 'duplicate-scope' @("event=x; $raw; $projection; $key=other") '' 0 'native_frontier_duplicate_scope'
        }
        foreach($value in @('', '(none)', '[redacted]', '[redacted]:1', '[redacted]:009', ('[redacted]:'+([char]0x661)), '[redacted]:-1', 'prefix[redacted]:1', 'data:1', 'other', $data)) {
            foreach($projected in @('', "; frontier_scope_data_session_identity=$data")) {$null=Check 'data-value' @("event=x; data_session_identity=$value$projected")}
        }
        foreach($key in @('data_session_identity','frontier_scope_data_session_identity')) {
            $null=Check 'duplicate-data' @("event=x; data_session_identity=data:1; frontier_scope_data_session_identity=$data; $key=other")
        }
        foreach($key in @('event','acceptance_run_id','scenario_run_id','log_writer_id','log_writer_sequence','log_writer_batch_generation','log_writer_batch_first_sequence','log_writer_batch_last_sequence','log_writer_attempt_utc_ticks')) {
            $null=Check 'writer-duplicate' @("$key=first; $($key.ToUpperInvariant())=second")
        }
    }
} finally {[Threading.Thread]::CurrentThread.CurrentCulture=$culture}
foreach($fault in @('raw-copy','path-case')) {$null=Check $fault @('event=x','event=y') $fault 0 'native_frontier_input'}
foreach($fault in @('failure-copy','path-null-character')) {$null=Check $fault @('event=x','event=y') $fault}
$null=Check 'duplicate-failure' @('event=x','event=y') 'failure-duplicate' 0 'native_frontier_failure_duplicate'
$null=Check 'line-bound' @('event=x; value='+('a'*262129))
$null=Check 'line-over-bound' @('event=x; value='+('a'*262144)) '' 0 'native_frontier_input'
$null=Check 'field-bound' @(('event=x;'+(' k=v;'*255)).TrimEnd(';'))
$null=Check 'field-over-bound' @(('event=x;'+(' k=v;'*256)).TrimEnd(';')) '' 0 'native_frontier_input'
$null=Check 'cumulative-bound' ([string[]](@('event=x; value='+('a'*262129))*5)) '' 0 'native_frontier_input'
foreach($n in @(1,2,3)) {$null=Check 'visit-budget' @('event=x','event=y','event=z') '' $n 'frontier_evaluation_budget'}
# Budget denial precedes even malformed input, as in the original loop.
$null=Check 'visit-before-input' @('event=x; value='+('a'*262144)) '' 1 'frontier_evaluation_budget'
'frontier-input-rows-differential=passed; checks='+$script:checks
