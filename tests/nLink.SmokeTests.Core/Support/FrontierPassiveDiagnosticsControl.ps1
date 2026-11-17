param([string]$RepoRoot,[string]$Directory)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierPassiveProjection.ps1')
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchLoader.ps1') $RepoRoot -PassiveDiagnosticDirectory (Join-Path $Directory 'passive-diagnostics')
$proof=Get-Content -LiteralPath (Join-Path $Directory 'passive-diagnostics/projection.json') -Raw|ConvertFrom-Json
if($proof.Count -ne 6 -or @($proof|Where-Object {-not $_.originalTextRestored}).Count) {throw 'Projection round-trip proof missing'}
foreach($row in $proof) {
    if((Get-AttemptPassiveHash $row.original) -cne $row.originalSha256 -or
        (Get-AttemptPassiveHash $row.projected) -cne $row.projectedSha256) {throw 'Projection/source hash changed'}
}
# Compile the actual original/projected Current expressions against a deterministic
# clock-shaped value. This exercises exact <=15000 boundaries and call order,
# without sleeping, resetting a real adoption clock or obtaining any authority.
$adoption=@($proof|Where-Object { [IO.Path]::GetFileName($_.original) -ceq 'LocalFrontierFileAdoption.cs' })[0]
$original=[IO.File]::ReadAllText($adoption.original);$projected=[IO.File]::ReadAllText($adoption.projected)
function Get-CurrentBody([string]$Text) {
    $method=$Text.Substring($Text.IndexOf('        private bool Current()'))
    $method=$method.Substring(0,$method.IndexOf('        private bool Verify('))
    $match=[regex]::Match($method,'#else\s*(.*?)\s*#endif',[Text.RegularExpressions.RegexOptions]::Singleline)
    if(-not $match.Success) {throw 'Current expression not found'}
    return $match.Groups[1].Value
}
function Get-ParentEntry([string]$Name,[string]$Method,[bool]$Projected) {
    $row=@($proof|Where-Object {[IO.Path]::GetFileName($_.original) -ceq $Name})[0]
    $path=if($Projected){$row.projected}else{$row.original}
    $text=[IO.File]::ReadAllText($path);$text=$text.Substring($text.IndexOf('public bool '+$Method+'()'))
    $match=[regex]::Match($text,'lock \(sourceSync\)\s*\{(.*?)var adoption = files.GetOriginalAdoption\(\);',[Text.RegularExpressions.RegexOptions]::Singleline)
    if(!$match.Success){throw 'Parent entry control seam changed'}
    $body=$match.Groups[1].Value
    $body=[regex]::Replace($body,'bool success = false; LocalFrontierParent\w+Channel channel = null;','')
    $body=[regex]::Replace($body,'try\s*\{','')
    return $body.Replace('ParentProbe.Check','PassiveFixtureControls.ParentCheck').Replace('ParentProbe.Mark','PassiveFixtureControls.ParentMark')+'return true;'
}
$definitions=@'
using System;
using System.Diagnostics;
using System.IO;
public static class PassiveFixtureControls {
    public static Func<string,bool,bool> Check;
    public static Func<string,bool,bool> ParentCheck;
    public static Action<int> ParentMark;
    private static T Bind<T>(Type type,string name) {return (T)(object)Delegate.CreateDelegate(typeof(T),type.GetMethod(name));}
    public sealed class ClockValue { public long ElapsedMilliseconds; }
    public sealed class Original {
        public bool disposed; public ClockValue work = new ClockValue();
        public Func<bool> current, sealScope; private const int MaximumActiveMilliseconds = 15000;
        public bool Run() { ORIGINAL_BODY }
    }
    public sealed class Projected {
        public bool disposed; public ClockValue work = new ClockValue();
        public Func<bool> current, sealScope; private const int MaximumActiveMilliseconds = 15000;
        public bool Run() { PROJECTED_BODY }
    }
    public sealed class Submission {
        public bool state; public int reads;
        public bool FilesPulled {get {reads++;return state;}}
        public bool FactsReceived {get {reads++;return state;}}
    }
    public class EntryState {
        public bool current=true, owns=true, parentFactsAttempted, parentAcknowledgmentAttempted;
        public int currentCalls, ownerCalls;
        public Submission parentSubmission=new Submission {state=true};
        public bool IsCurrent {get {currentCalls++;return current;}}
        public bool OwnsParentSubmission(Submission value) {ownerCalls++;return owns;}
    }
    public sealed class OriginalEntry : EntryState {
        public bool Facts() { ORIGINAL_FACTS }
        public bool Acknowledge() { ORIGINAL_ACK }
    }
    public sealed class ProjectedEntry : EntryState {
        public bool Facts() { PROJECTED_FACTS }
        public bool Acknowledge() { PROJECTED_ACK }
    }
    public static void Run(Type diagnostic,Type parentDiagnostic) {
        var start=Bind<Action<Stopwatch>>(diagnostic,"Start");
        var mark=Bind<Action<int>>(diagnostic,"Mark");
        var readStart=Bind<Action<long>>(diagnostic,"ReadStart");
        var readEnd=Bind<Action>(diagnostic,"ReadEnd");
        var parseStart=Bind<Action>(diagnostic,"ParseStart");
        var parseEnd=Bind<Action>(diagnostic,"ParseEnd");
        var copyBegin=Bind<Action>(diagnostic,"CopyBegin");
        var copyEnd=Bind<Action<bool>>(diagnostic,"CopyEnd");
        var writeAfterCleanup=Bind<Action>(diagnostic,"WriteAfterCleanup");
        Check=Bind<Func<string,bool,bool>>(diagnostic,"Check");
        ParentCheck=Bind<Func<string,bool,bool>>(parentDiagnostic,"Check");
        ParentMark=Bind<Action<int>>(parentDiagnostic,"Mark");
        var parentStart=Bind<Action<Stopwatch>>(parentDiagnostic,"Start");
        var parentBegin=Bind<Action>(parentDiagnostic,"Begin");
        var parentEnd=Bind<Action<bool>>(parentDiagnostic,"End");
        var parentWrite=Bind<Action>(parentDiagnostic,"WriteAfterCleanup");
        var clock=Stopwatch.StartNew(); long before=clock.ElapsedTicks;
        start(clock);
        var previous=Console.Error; var output=new StringWriter(); Console.SetError(output);
        try {
            copyBegin();
            for(int scenario=0;scenario<6;scenario++) {
                int a=0,b=0;
                var original=new Original(); var projected=new Projected();
                original.work.ElapsedMilliseconds=projected.work.ElapsedMilliseconds=scenario==1?15001:15000;
                original.disposed=projected.disposed=scenario==2;
                original.current=()=>{a++;return scenario!=3;};
                projected.current=()=>{b++;return scenario!=3;};
                if(scenario!=4) {
                    original.sealScope=()=>{a+=10;if(scenario==5)original.work.ElapsedMilliseconds=15001;return true;};
                    projected.sealScope=()=>{b+=10;if(scenario==5)projected.work.ElapsedMilliseconds=15001;return true;};
                }
                bool x=original.Run(),y=projected.Run();
                if(x!=y || a!=b || x!=(scenario==0||scenario==4))
                    throw new Exception("Original/projected predicate result or evaluation order changed:"+scenario);
            }
            for(int i=0;i<100000;i++) {
                mark(i%16);
                readStart(i); readEnd();
                parseStart(); parseEnd();
            }
            if(!clock.IsRunning || clock.ElapsedTicks<before) throw new Exception("Original clock changed");
            copyEnd(false);
            writeAfterCleanup();
            string report=output.ToString();
            if(report.Split(new[]{Environment.NewLine},StringSplitOptions.RemoveEmptyEntries).Length!=18 ||
                !report.Contains("first_rejected=adoption_entry_budget") ||
                !report.Contains("rows=100000") || !report.Contains("read_sample_calls=394") ||
                !report.Contains("parse_sample_calls=394")) throw new Exception("Bounded diagnostic report changed:"+report);
            long length=output.GetStringBuilder().Length;
            writeAfterCleanup();
            if(output.GetStringBuilder().Length!=length || !clock.IsRunning) throw new Exception("Cleanup/reset changed original clock or emitted twice");
            parentStart(clock);parentBegin();
            for(int method=0;method<2;method++) for(int scenario=0;scenario<5;scenario++) {
                var a=new OriginalEntry();var b=new ProjectedEntry();
                a.current=b.current=scenario!=1;a.owns=b.owns=scenario!=3;
                a.parentSubmission.state=b.parentSubmission.state=scenario!=4;
                a.parentFactsAttempted=b.parentFactsAttempted=a.parentAcknowledgmentAttempted=b.parentAcknowledgmentAttempted=scenario==2;
                bool x=method==0?a.Facts():a.Acknowledge(),y=method==0?b.Facts():b.Acknowledge();
                if(x!=y || x!=(scenario==0) || a.currentCalls!=b.currentCalls || a.ownerCalls!=b.ownerCalls ||
                    a.parentSubmission.reads!=b.parentSubmission.reads || a.parentFactsAttempted!=b.parentFactsAttempted ||
                    a.parentAcknowledgmentAttempted!=b.parentAcknowledgmentAttempted)
                    throw new Exception("Parent entry result/order changed:"+method+":"+scenario);
                if(scenario==1 && (b.parentFactsAttempted || b.parentAcknowledgmentAttempted || b.ownerCalls!=0 || b.parentSubmission.reads!=0))
                    throw new Exception("Entry rejection consumed acknowledgment/facts attempt");
            }
            output.GetStringBuilder().Clear();parentStart(clock);parentBegin();
            for(int i=0;i<100000;i++) ParentMark(i%31);
            if(ParentCheck("parent_first",false) || ParentCheck("parent_later",false) || !ParentCheck("facts_callback_source",true) || !ParentCheck("facts_callback_pins",true))
                throw new Exception("Observer changed its operand");
            parentEnd(false);parentWrite();string parentReport=output.ToString();
            if(parentReport.Split(new[]{Environment.NewLine},StringSplitOptions.RemoveEmptyEntries).Length!=32 ||
                !parentReport.Contains("first_rejected=parent_first") || !parentReport.Contains("facts_source_checks=1") ||
                !parentReport.Contains("facts_pin_checks=1") || !clock.IsRunning || clock.ElapsedTicks<before)
                throw new Exception("Bounded parent report/clock changed:"+parentReport);
            length=output.GetStringBuilder().Length;parentWrite();
            if(output.GetStringBuilder().Length!=length || !clock.IsRunning) throw new Exception("Parent cleanup emitted twice or changed clock");
        } finally {Console.SetError(previous);clock.Stop();}
    }
}
'@
$definitions=$definitions.Replace('ORIGINAL_BODY',(Get-CurrentBody $original)).Replace('PROJECTED_BODY',
    (Get-CurrentBody $projected).Replace('NLink.Tests.AttemptPassiveDiagnosticsForTest.Check','PassiveFixtureControls.Check'))
$definitions=$definitions.Replace('ORIGINAL_FACTS',(Get-ParentEntry 'LocalGuiWriterParentFacts.cs' 'TryPublishParentFacts' $false)).Replace('PROJECTED_FACTS',(Get-ParentEntry 'LocalGuiWriterParentFacts.cs' 'TryPublishParentFacts' $true)).Replace('ORIGINAL_ACK',(Get-ParentEntry 'LocalGuiWriterParentAcknowledgment.cs' 'TryConfirmParentAcknowledgment' $false)).Replace('PROJECTED_ACK',(Get-ParentEntry 'LocalGuiWriterParentAcknowledgment.cs' 'TryConfirmParentAcknowledgment' $true))
Add-Type -TypeDefinition $definitions -ErrorAction Stop
[PassiveFixtureControls]::Run([NLink.Tests.AttemptPassiveDiagnosticsForTest],[NLink.Tests.AttemptParentFactsDiagnosticsForTest])
'passive-controls=projection-roundtrip; boundary15000/15001; short-circuit-counts; original-clock; fixed18-line-report; sampling394/100000; cleanup-once'
'parent-passive-controls=entry-currentness; short-circuit-counts; original-clock; fixed32-line-report; cleanup-once'

