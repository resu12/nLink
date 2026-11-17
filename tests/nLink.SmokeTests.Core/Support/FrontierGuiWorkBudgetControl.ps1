param([string]$RepoRoot,[string]$Directory,[ValidateSet('default','canonical','release','ineligible','non-gui')][string]$Mode)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiWorkBudget.ps1')
$base=[ordered]@{
 NLINK_TEST_ATTEMPT_DISPATCH='1';NLINK_TEST_FRONTIER_WORKER_CONFIGURATION='Debug';NLINK_TEST_FRONTIER_ROUTE_CYCLE='canonical-cycle-v1';
 NLINK_TEST_ATTEMPT_CASE='writer-source-read-cross-process-carrier-full-canonical-tail-late-publication';
 NLINK_TEST_PAYLOAD_WRAPPER_CASE='writer-valid';NLINK_TEST_PAYLOAD_PARENT_CASE='valid';
 NLINK_TEST_PAYLOAD_PRIMARY_CASE='canonical-tail-late-publication';NLINK_TEST_DIAGNOSTIC_CUSTODY=''
}
function Set-BaseScope {foreach($key in $base.Keys){[Environment]::SetEnvironmentVariable($key,$base[$key],'Process')}}
Set-BaseScope
if(!(Test-AttemptGuiWorkBudgetScope)){throw 'Exact canonical scope rejected'};$checks=1
foreach($key in $base.Keys){
 Set-BaseScope;[Environment]::SetEnvironmentVariable($key,'invalid','Process')
 if(Test-AttemptGuiWorkBudgetScope){throw ('Changed scope admitted: '+$key)};$checks++
 if($base[$key]){
  Set-BaseScope;[Environment]::SetEnvironmentVariable($key,$null,'Process')
  if(Test-AttemptGuiWorkBudgetScope){throw ('Missing scope admitted: '+$key)};$checks++
  if($base[$key].ToUpperInvariant() -cne $base[$key]){
   Set-BaseScope;[Environment]::SetEnvironmentVariable($key,$base[$key].ToUpperInvariant(),'Process')
   if(Test-AttemptGuiWorkBudgetScope){throw ('Case-changed scope admitted: '+$key)};$checks++
  }
 }
}
Set-BaseScope
switch($Mode){
 'default' {foreach($key in $base.Keys){[Environment]::SetEnvironmentVariable($key,$null,'Process')}}
 'release' {$env:NLINK_TEST_FRONTIER_WORKER_CONFIGURATION='Release'}
 'ineligible' {$env:NLINK_TEST_FRONTIER_ROUTE_CYCLE='late-ineligible-v1';$env:NLINK_TEST_DIAGNOSTIC_CUSTODY='frontier-late-publication'}
}
$passive=if($Mode -ceq 'non-gui'){''}else{Join-Path $Directory 'passive-diagnostics'}
$original=Join-Path $RepoRoot 'tools/FileTransferOps/LocalFrontierFileAdoption.cs';$originalHash=Get-AttemptGuiWorkHash $original
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchLoader.ps1') $RepoRoot -PassiveDiagnosticDirectory $passive
$type=[NLink.Tools.LocalGuiWriterRequest].Assembly.GetType('NLink.Tools.LocalFrontierFileAdoption')
$limit=$type.GetField('MaximumActiveMilliseconds',[Reflection.BindingFlags]'Static,NonPublic').GetRawConstantValue()
$expected=if($Mode -ceq 'canonical'){30000}else{15000}
if($limit -ne $expected){throw "GUI work budget mismatch: expected=$expected actual=$limit"}
$projection=Join-Path $Directory 'passive-diagnostics-work-budget/projection.json'
if($Mode -ceq 'canonical'){
 $proof=Get-Content -LiteralPath $projection -Raw|ConvertFrom-Json
 if($proof.defaultMilliseconds -ne 15000 -or $proof.fixtureMilliseconds -ne 30000 -or !$proof.allOtherTextUnchanged -or
    $proof.authorityGranted -or (Get-AttemptGuiWorkHash $proof.original) -cne $proof.originalSha256 -or
    (Get-AttemptGuiWorkHash $proof.projected) -cne $proof.projectedSha256){throw 'Intentional policy projection not exact'}
 $passiveProof=Get-Content -LiteralPath (Join-Path $passive 'projection.json') -Raw|ConvertFrom-Json
 $adoption=@($passiveProof|Where-Object {[IO.Path]::GetFileName($_.original) -ceq 'LocalFrontierFileAdoption.cs'})
 if($adoption.Count -ne 1 -or $adoption[0].originalSha256 -cne $proof.projectedSha256 -or !$adoption[0].originalTextRestored){throw 'Budget/passive projection chain changed'}
}elseif(Test-Path -LiteralPath $projection){throw 'Noncanonical caller inherited extended work budget'}
if((Get-AttemptGuiWorkHash $original) -cne $originalHash -or
   ![IO.File]::ReadAllText($original).Contains('internal const int MaximumActiveMilliseconds = 15000;')){throw 'Default tool budget was edited'}
Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Reflection;
public static class GuiWorkBudgetClockProbe {
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    private static void Require(bool value,string reason){if(!value)throw new InvalidOperationException(reason);}
    private static void Elapsed(Stopwatch work,int milliseconds){
        Require(!work.IsRunning,"clock unexpectedly running");
        foreach(var f in typeof(Stopwatch).GetFields(Flags)) if(f.Name.TrimStart('_')=="elapsed"){
            f.SetValue(work,(Stopwatch.Frequency*milliseconds+999)/1000);return;
        }
        throw new InvalidOperationException("original elapsed field missing");
    }
    private static object Adoption(Type type,Stopwatch work,Func<bool> current){
        return Activator.CreateInstance(type,Flags,null,new object[]{Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),current,work},null);
    }
    public static string Run(Assembly assembly,int limit){
        var type=assembly.GetType("NLink.Tools.LocalFrontierFileAdoption");
        var ownerType=assembly.GetType("NLink.Tools.LocalGuiWriterRequest+FrontierLaunchOwner");
        var owner=Activator.CreateInstance(ownerType,true);
        var work=(Stopwatch)ownerType.GetField("frontierWork",Flags).GetValue(owner);
        var ledger=ownerType.GetMethod("LedgerOriginCurrent",Flags);
        var current=type.GetMethod("Current",Flags);var seal=type.GetField("sealScope",Flags);
        int boundaries=0;
        try {
            Require(!(bool)ownerType.GetProperty("IsCurrent").GetValue(owner,null),"clock fabricated original ownership");
            foreach(string name in new[]{"LiveProofEligible","NativeWriterCustodyEstablished","PublicationCompletionIssued"})
                Require(!(bool)ownerType.GetProperty(name).GetValue(owner,null),"budget created authority");
            foreach(int ms in new[]{0,14999,15000,15001,29999,30000,30001}){
                Elapsed(work,ms);int calls=0;
                object value=Adoption(type,work,()=>{calls++;return true;});
                try {
                    seal.SetValue(value,(Func<bool>)(()=> (bool)ledger.Invoke(owner,null)));
                    Require(Object.ReferenceEquals(type.GetField("work",Flags).GetValue(value),work),"clock replaced");
                    Require((bool)current.Invoke(value,null)==(ms<=limit),"entry boundary changed:"+ms);
                    Require((bool)ledger.Invoke(owner,null)==(ms<=limit),"nested ledger boundary changed:"+ms);
                    Require(calls==(ms<=limit?1:0) && !work.IsRunning && work.ElapsedMilliseconds==ms,"boundary short-circuit/clock changed");
                    boundaries++;
                }finally{((IDisposable)value).Dispose();}
            }
            Elapsed(work,limit);int outer=0,inner=0;
            object nested=Adoption(type,work,()=>{outer++;Elapsed(work,limit+1);return true;});
            try {
                seal.SetValue(nested,(Func<bool>)(()=>{inner++;return (bool)ledger.Invoke(owner,null);}));
                Require(!(bool)current.Invoke(nested,null) && outer==1 && inner==1 && work.ElapsedMilliseconds==limit+1,
                    "nested cumulative expiry bypassed");
            }finally{((IDisposable)nested).Dispose();}
            Elapsed(work,limit);object exit=Adoption(type,work,()=>true);
            try{
                seal.SetValue(exit,(Func<bool>)(()=>{Elapsed(work,limit+1);return true;}));
                Require(!(bool)current.Invoke(exit,null),"exit budget bypassed");
            }finally{((IDisposable)exit).Dispose();}
            Elapsed(work,0);int actions=0;object revoked=Adoption(type,work,()=>false);
            try{
                Require(!(bool)type.GetProperty("IsRetained",Flags).GetValue(revoked,null),"revoked callback admitted");
                Require(!(bool)type.GetMethod("TryRunControl",Flags).Invoke(revoked,new object[]{(Func<bool>)(()=>{actions++;return true;})}) && actions==0,
                    "retired work revived");
            }finally{((IDisposable)revoked).Dispose();}
            Elapsed(work,limit+1);object expired=Adoption(type,work,()=>true);
            try{
                Require(!(bool)type.GetProperty("IsRetained",Flags).GetValue(expired,null),"expired work retained");
                Elapsed(work,0);
                Require(!(bool)type.GetProperty("IsRetained",Flags).GetValue(expired,null),"expired work revived after synthetic rewind");
            }finally{((IDisposable)expired).Dispose();}
            Elapsed(work,limit/2);object cumulative=Adoption(type,work,()=>true);
            try{
                long before=work.ElapsedTicks;
                Require((bool)type.GetMethod("TryRunControl",Flags).Invoke(cumulative,new object[]{(Func<bool>)(()=>true)}),"bounded control failed");
                Require(!work.IsRunning && work.ElapsedTicks>=before,"control reset cumulative work");
                work.Start();
                Require((bool)type.GetProperty("IsRetained",Flags).GetValue(cumulative,null) && work.IsRunning,"nested check stopped outer interval");
                work.Stop();
            }finally{((IDisposable)cumulative).Dispose();}
            return "boundaries="+boundaries+";nested-expiry;exit-expiry;revocation;no-revival;one-clock;no-authority";
        }finally{work.Stop();((IDisposable)owner).Dispose();}
    }
}
'@
$result=[GuiWorkBudgetClockProbe]::Run([NLink.Tools.LocalGuiWriterRequest].Assembly,$expected)
$record=[ordered]@{mode=$Mode;milliseconds=$limit;scopeChecks=$checks;clockChecks=$result;defaultSourceSha256=$originalHash;authorityGranted=$false}
[IO.File]::WriteAllText((Join-Path $Directory 'budget-result.json'),($record|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
'gui-work-budget='+$limit+'; mode='+$Mode+'; scope-checks='+$checks+'; '+$result
