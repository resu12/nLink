param([string]$RepoRoot,[string]$Directory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalNativeInputCapture.ps1')
if([NLink.Tools.LocalRetainedSourceRequest]::MaximumSourceBytes -ne 268435456 -or
   [NLink.Tools.LocalInputReadLease]::MaximumWriterBytes -ne 268435456 -or
   [NLink.Tools.LocalNativeInputCapture]::MaximumCodeUnits -ne 268435456 -or
   [NLink.Tools.LocalNativeInputCapture]::MaximumActiveMilliseconds -ne 2000) {throw 'Live input limits not implemented'}
Add-Type -TypeDefinition @'
using System;
public static class LiveInputTestCurrent {
    public static int Calls, RejectAt;
    public static bool Throw;
    public static Func<long> CurrentUnits, CurrentRecords;
    public static long RejectAfterUnits, RejectAfterRecords;
    public static readonly Func<bool> Current = () => {
        Calls++;
        if (Throw) throw new InvalidOperationException("test-current-failure");
        if ((RejectAfterUnits > 0 && CurrentUnits() >= RejectAfterUnits) ||
            (RejectAfterRecords > 0 && CurrentRecords() >= RejectAfterRecords)) return false;
        return RejectAt == 0 || Calls < RejectAt;
    };
    public static void Reset() { Calls = RejectAt = 0; Throw = false; CurrentUnits = CurrentRecords = null; RejectAfterUnits = RejectAfterRecords = 0; }
}
'@
$roles=@('fullCapture','fullSnapshot','compactCapture','compactSnapshot')
$checks=[Collections.Generic.List[string]]::new()
foreach($v in @(
    @{name='empty';lines=[string[]]@();hash='e0d5da8ece8aa85f55de0491cfa4a8a0b0024f40b01d5474a499cc244679e670'},
    @{name='blank';lines=[string[]]@('');hash='17e6c97aa6bc619bdfc959ad84158419aee8404a32d0bc7ff0df90189d31b644'},
    @{name='pair';lines=[string[]]@('A','B');hash='53b9fc58d0134ef0a2f9ec7563a61bd21a11401c87e0432cb5589b268cbc2f54'},
    @{name='newline';lines=[string[]]@("A`nB");hash='cf7ab64f2259cc6c4ba0684b4c53db8341eef207144fc23643a1707aaa3dc56a'},
    @{name='surrogate';lines=[string[]]@([string][char]0xd800);hash='33a66b724245f06e5f2531f74081467d22b6a34a4195b763d3ce851406c3706f'})) {
    foreach($clone in @($false,$true)) {
        $arrays=[string[][]]@($v.lines,$v.lines,$v.lines,$v.lines)
        if($clone) {$arrays=[string[][]]@($v.lines.Clone(),$v.lines.Clone(),$v.lines.Clone(),$v.lines.Clone())}
        $owner=[NLink.Tools.LocalNativeInputCapture]::new([guid]::NewGuid());[LiveInputTestCurrent]::Reset()
        try {
            $token=$owner.CaptureInputs($arrays,[LiveInputTestCurrent]::Current)
            if($null -eq $token -or -not $owner.Owns($token)) {throw ('Batch omitted: '+$owner.OmissionReason)}
            foreach($role in $roles) {if($token.Get($role).Sha256 -cne $v.hash) {throw 'Framing changed'}}
            $multiplier=if($clone) {4} else {1}
            if($owner.RecordVisits -ne $multiplier*$v.lines.Length) {throw 'Array identity sharing changed'}
            $before=$token.Get('fullCapture').Sha256
            if($arrays[0].Length) {$arrays[0][0]='changed-after-capture'}
            if($token.Get('fullCapture').Sha256 -cne $before -or $null -ne $owner.CaptureInputs($arrays,[LiveInputTestCurrent]::Current)) {throw 'Immutable capture or one-shot changed'}
            $owner.Cancel(); if($owner.Owns($token)) {throw 'Cancelled capture remained owned'}
            $checks.Add($v.name+'-clone-'+$clone)
        } finally {$owner.Dispose()}
        # Keep the vector fixture immutable across the two cases.
        if(-not $clone -and $v.lines.Length) {
            switch($v.name) {'blank' {$v.lines[0]=''} 'pair' {$v.lines[0]='A'} 'newline' {$v.lines[0]="A`nB"} 'surrogate' {$v.lines[0]=[string][char]0xd800}}
        }
    }
}
foreach($case in @('missing-role','null-role','null-line','line-cap','record-cap','cancelled','owner-expired','owner-throws','mixed-scalar','time-cap')) {
    $owner=[NLink.Tools.LocalNativeInputCapture]::new([guid]::NewGuid());[LiveInputTestCurrent]::Reset()
    try {
        $line=[string[]]@('A');$arrays=[string[][]]@($line,$line,$line,$line)
        switch($case) {
            'missing-role' {$arrays=[string[][]]@($line)}
            'null-role' {$arrays[3]=$null}
            'null-line' {$line=[string[]]::new(1);$arrays=[string[][]]@($line,$line,$line,$line)}
            'line-cap' {$line[0]='x'*262145}
            'record-cap' {$line=[string[]]::new(262145);$arrays=[string[][]]@($line,$line,$line,$line)}
            'cancelled' {$owner.Cancel()}
            'owner-expired' {[LiveInputTestCurrent]::RejectAt=5}
            'owner-throws' {[LiveInputTestCurrent]::Throw=$true}
            'mixed-scalar' {$null=$owner.BeginInput('fullCapture',0)}
            'time-cap' {$owner.GetType().GetField('activeTicks',[Reflection.BindingFlags]'Instance,NonPublic').SetValue($owner,[long]([Diagnostics.Stopwatch]::Frequency*3))}
        }
        if($null -ne $owner.CaptureInputs($arrays,[LiveInputTestCurrent]::Current) -or $null -ne $owner.Take()) {throw ('Invalid batch admitted: '+$case)}
        if($case -eq 'record-cap' -and $owner.RecordVisits -ne 0) {throw 'Over-cap batch traversed'}
        $checks.Add($case)
    } finally {$owner.Dispose()}
}
$owner=[NLink.Tools.LocalNativeInputCapture]::new([guid]::NewGuid());[LiveInputTestCurrent]::Reset()
try {
    $line=[string[]](@('')*262144)
    $token=$owner.CaptureInputs([string[][]]@($line,$line,$line,$line),[LiveInputTestCurrent]::Current)
    if($null -eq $token -or $owner.RecordVisits -ne 262144) {throw 'Exact record cap rejected'}
    $checks.Add('exact-record-cap')
} finally {$owner.Dispose()}

# Real batch callbacks must stop at the next record/block and at final issuance.
foreach($case in @('record','block','issuance')) {
    $owner=[NLink.Tools.LocalNativeInputCapture]::new([guid]::NewGuid());[LiveInputTestCurrent]::Reset()
    try {
        $line=[string[]]@('A','B')
        if($case -eq 'block') {$line=[string[]]@(('x'*4097))}
        if($case -eq 'issuance') {
            $token=$owner.CaptureInputs([string[][]]@($line,$line,$line,$line),[LiveInputTestCurrent]::Current)
            if($null -eq $token) {throw 'Final owner check setup failed'}
            $lastCheck=[LiveInputTestCurrent]::Calls
            $owner.Dispose();$owner=[NLink.Tools.LocalNativeInputCapture]::new([guid]::NewGuid())
            [LiveInputTestCurrent]::Reset();[LiveInputTestCurrent]::RejectAt=$lastCheck
        } else {
            [LiveInputTestCurrent]::CurrentUnits=[Delegate]::CreateDelegate([Func[long]],$owner,'get_CodeUnitVisits')
            [LiveInputTestCurrent]::CurrentRecords=[Delegate]::CreateDelegate([Func[long]],$owner,'get_RecordVisits')
            if($case -eq 'block') {[LiveInputTestCurrent]::RejectAfterUnits=2048}
            else {[LiveInputTestCurrent]::RejectAfterRecords=1}
        }
        if($null -ne $owner.CaptureInputs([string[][]]@($line,$line,$line,$line),[LiveInputTestCurrent]::Current) -or
            $owner.OmissionReason -cne 'input_owner_inactive' -or $null -ne $owner.Take()) {throw ('Owner cancellation missed: '+$case)}
        if(($case -eq 'record' -and ($owner.RecordVisits -ne 1 -or $owner.CodeUnitVisits -ne 1)) -or
            ($case -eq 'block' -and ($owner.RecordVisits -ne 1 -or $owner.CodeUnitVisits -ne 2048))) {throw ('Owner check exceeded bounded work: '+$case)}
        $checks.Add('owner-'+$case)
    } finally {$owner.Dispose();[LiveInputTestCurrent]::Reset()}
}

# Deterministic scalar counter fault injection checks the exact arithmetic seam
# without claiming a fabricated counter state is an actual capture or proof.
$owner=[NLink.Tools.LocalNativeInputCapture]::new([guid]::NewGuid())
try {
    if(-not $owner.BeginInput('fullCapture',2)) {throw 'Code-unit boundary setup failed'}
    $counter=$owner.GetType().GetField('<CodeUnitVisits>k__BackingField',[Reflection.BindingFlags]'Instance,NonPublic')
    $counter.SetValue($owner,[NLink.Tools.LocalNativeInputCapture]::MaximumCodeUnits-1L)
    if(-not $owner.AppendLine(1,'A') -or $owner.CodeUnitVisits -ne [NLink.Tools.LocalNativeInputCapture]::MaximumCodeUnits -or
        $owner.AppendLine(2,'B') -or $owner.OmissionReason -cne 'line_shape_order_or_code_unit_limit' -or $null -ne $owner.Take()) {throw 'Code-unit cap arithmetic changed'}
    $checks.Add('code-unit-cap-arithmetic')
} finally {$owner.Dispose()}

# Size admission uses a real hash-held file, but this is not decoded-input or
# writer-history proof. No worker, GUI, evaluator or network is started.
$path=Join-Path $Directory 'source-byte-limit.bin'
foreach($size in @(268435456L,268435457L)) {
    $file=[IO.File]::Open($path,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try {$file.SetLength($size)} finally {$file.Dispose()}
    $hashed=[NLink.Tools.LocalInputReadLease]::HashAndHold($path);$request=$null;$writer=$null
    try {
        $request=[NLink.Tools.LocalRetainedSourceRequest]::TryCreate([guid]::NewGuid(),'RouteAcceptance','test-run','test-scenario',$hashed.Lease,1)
        $writer=[NLink.Tools.LocalInputReadLease]::OpenExpected($path,$hashed.Sha256,1)
        if(($null -ne $request) -ne ($size -eq 268435456L) -or ($null -ne $writer) -ne ($size -eq 268435456L)) {throw 'File byte boundary admission changed'}
        if($null -ne $writer -and $writer.IsVerified) {throw 'Size admission granted writer verification'}
        $checks.Add('byte-cap-'+$size)
    } finally {
        if($null -ne $request) {$request.Dispose()};if($null -ne $writer) {$writer.Dispose()};$hashed.Lease.Dispose()
    }
}
# Release the large generated payload without deleting any directory or user file.
$file=[IO.File]::Open($path,[IO.FileMode]::Truncate,[IO.FileAccess]::Write,[IO.FileShare]::None);$file.Dispose()
'local-live-input-capture='+(@{checks=$checks.ToArray();authorityEvaluations=0;publicationCompletions=0} | ConvertTo-Json -Compress)
