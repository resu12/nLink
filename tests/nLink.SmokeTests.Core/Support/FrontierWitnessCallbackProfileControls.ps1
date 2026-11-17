param([string]$RepoRoot)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalNativeFrontierWitness.ps1')
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierWitnessCallbackProfile.ps1')
$original=(Get-Command Resolve-FileTransferNativeFrontierWitness).Definition
$profiled=Add-FrontierWitnessCallbackProfileForTest $original
$pattern='\r?\n# witness-profile-begin\r?\n(?s:.*?)# witness-profile-end\r?\n'
if([regex]::Matches($profiled,$pattern).Count -ne 11 -or [regex]::Replace($profiled,$pattern,'') -cne $original) {
    throw 'Profile changed original callback text'
}
$tokens=$null; $errors=$null
$null=[Management.Automation.Language.Parser]::ParseInput($profiled,[ref]$tokens,[ref]$errors)
if($errors.Count) {throw ('Profile parse: '+($errors -join ';'))}
foreach($bad in @($profiled,$original.Replace('$before=Snapshot','$before=Changed'),($original+' $before=Snapshot'))) {
    $rejected=$false; try {$null=Add-FrontierWitnessCallbackProfileForTest $bad} catch {$rejected=$true}
    if(-not $rejected) {throw 'Changed, duplicate, or repeated seam accepted'}
}
# Exercise the actual BeforeResolve install in its dot-sourced caller scope.
# Only the independent late-publication probe is replaced with an inert no-op.
$probe=[IO.File]::ReadAllText((Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierCanonicalTailPrimaryProbe.ps1'))
$probe=$probe.Replace(". (Join-Path `$RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierLatePublicationPrimaryProbe.ps1')",'. {param($Stage,[switch]$CanonicalTail)}')
$env:NLINK_TEST_PAYLOAD_PRIMARY_CASE='canonical-tail-late-publication'; $env:NLINK_TEST_DIAGNOSTIC_CUSTODY=''
. ([scriptblock]::Create($probe)) -Stage BeforeResolve
if((Get-Command Resolve-FileTransferNativeFrontierWitness).Definition -cne $profiled) {throw 'Probe did not install exact profiled callback'}

# Inert types cannot retain native handles, evaluate authority, or issue output.
# They execute the original PowerShell callback with an injected visit failure.
$types=@'
using System;
using System.Collections.Generic;
namespace NLink.Tools {
 public static class LocalPublicationInputLimits { public const long ProducerCodeUnits = 1000000; }
 public sealed class LocalRetainedSourceRequest {
  public sealed class FrontierWorkerInputs {
   public sealed class NativePayloadSet { public object[] GetPayloads() { return new object[] { new object() }; } }
   public sealed class NativeWitnessSelection { public NativeWitnessSelection(object p, int[] i, string s) {} }
   public sealed class EvaluationCustody {
    public int Limit, Visits, OwnCalls, ResolveCalls; public bool BrokenRecorder;
    public readonly List<long[]> Samples = new List<long[]>();
    public bool Owns(object p, object c, string[] f, string[] k, object fv, object cv) { OwnCalls++; return true; }
    public object TryResolveWitnesses(object p, object c, string[] f, string[] k, object fv, object cv, NativePayloadSet payload,
      object failure, object candidates, object proof, object index, Func<Action, NativeWitnessSelection> callback) {
     ResolveCalls++; return callback(delegate { Visits++; if (Visits >= Limit) throw new InvalidOperationException("frontier_evaluation_budget"); });
    }
    public void RecordWitnessCallbackForTest(int phase, int rows, int owned, int selected, bool terminal, bool failed) {
     if (BrokenRecorder) throw new InvalidOperationException("diagnostic recorder failure must not escape");
     Samples.Add(new long[] { phase, Visits, rows, owned, selected, terminal ? 1 : 0, failed ? 1 : 0 });
    }
   }
  }
 }
}
'@
$projection=[IO.File]::ReadAllText((Join-Path $RepoRoot 'tools/FileTransferOps/LocalFrontierInputRows.cs'))
$arguments=@{}
if($PSVersionTable.PSVersion.Major -lt 6) {$arguments.ReferencedAssemblies=@('System.dll','System.Core.dll',[Management.Automation.PSObject].Assembly.Location)}
Add-Type -TypeDefinition ($projection+"`n"+$types.Replace('using System;','').Replace('using System.Collections.Generic;','')) @arguments
$full=[string[]]@('event=noop; value=first','event=noop; value=second'); $compact=[string[]]@('compact')
$events=@(for($i=0;$i -lt $full.Length;$i++) {[pscustomobject]@{RawLine=$full[$i];FilePath='original';EventName='noop';LineNumber=$i+1}})
$failure=[pscustomobject]@{Class='filetransfer_fallback_frontier_recent_progress_grace_recheck_missing';Reason='reason';Variant='variant';Event=$events[1]}
$candidates=@($failure)
$proof=[pscustomobject]@{UnresolvedCount=1;RecoveredCount=0;UnresolvedCandidateRecords=@([pscustomobject]@{Event=$failure.Event;Reason='reason';Variant='variant';Recovered=$false})}
$view=[pscustomobject]@{Mode='SnapshotV1Authoritative';ContractVersion=1;ArtifactEvidenceVerdict='pass';StreamComplete=$true;
    AuthoritativeFamilies=@('route_runtime','route_transition');Boundaries=@();CatalogVersion='1';CatalogHash='catalog';BoundaryHash='boundary'}
$index=[pscustomobject]@{AllEvents=$events;LogFiles=@('original')}
$payload=[NLink.Tools.LocalRetainedSourceRequest+FrontierWorkerInputs+NativePayloadSet]::new()
$before=@($full,$compact,$events,$failure,$candidates,$proof,$view,$index) | ConvertTo-Json -Depth 12 -Compress
foreach($limit in @(1,3,4,100)) {
    foreach($broken in @($false,$true)) {
        $results=@(foreach($definition in @($original,$profiled)) {
            Set-Item -Path Function:Resolve-FileTransferNativeFrontierWitness -Value ([scriptblock]::Create($definition))
            $custody=[NLink.Tools.LocalRetainedSourceRequest+FrontierWorkerInputs+EvaluationCustody]::new()
            $custody.Limit=$limit; $custody.BrokenRecorder=$broken; $omission=''
            $result=Resolve-FileTransferNativeFrontierWitness -Custody $custody -Evaluations $null -Context $null -Full $full -Compact $compact `
                -FullView $view -CompactView $view -Payloads $payload -Index $index -Failure $failure -Candidates $candidates -RecentProof $proof -OmissionReason ([ref]$omission)
            if($null -ne $result -or $custody.OwnCalls -ne 1 -or $custody.ResolveCalls -ne 1) {throw 'Callback changed original call count/result'}
            [pscustomobject]@{Omission=$omission;Visits=$custody.Visits;Samples=$custody.Samples}
        })
        if($results[0].Omission -cne $results[1].Omission -or $results[0].Visits -ne $results[1].Visits -or $results[0].Samples.Count) {throw 'Profile changed original failure/visits'}
        $expectedError=if($limit -eq 100) {'native_frontier_scope_unavailable'} else {'frontier_evaluation_budget'}
        if(-not $results[1].Omission.Contains($expectedError)) {throw ('Wrong original failure: '+$results[1].Omission)}
        if($broken) {if($results[1].Samples.Count) {throw 'Broken recorder produced samples'}}
        else {
            $terminal=$results[1].Samples[$results[1].Samples.Count-1]
            $phase=if($limit -eq 1){0}elseif($limit -eq 100){2}else{1}
            $rows=if($limit -eq 4){1}elseif($limit -eq 100){2}else{0}
            if($terminal[0] -ne $phase -or $terminal[1] -ne $results[1].Visits -or $terminal[2] -ne $rows -or $terminal[5] -ne 1 -or $terminal[6] -ne 1) {
                throw 'Callback terminal phase/materialized count mismatch'
            }
        }
        if((@($full,$compact,$events,$failure,$candidates,$proof,$view,$index) | ConvertTo-Json -Depth 12 -Compress) -cne $before -or
            -not [object]::ReferenceEquals($events[1],$failure.Event) -or -not [object]::ReferenceEquals($full[0],$events[0].RawLine)) {throw 'Original inputs changed'}
    }
}
'witness-callback-profile-controls=8'
