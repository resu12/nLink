param([string]$RepoRoot)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalNativeInputCapture.ps1')
. (Join-Path $RepoRoot 'tools/GuiSmokeStructuredFields.ps1')
$parse=[NLink.Tools.LocalRetainedSourceRequest+WriterReader].GetMethod('MembershipFields',[Reflection.BindingFlags]'Static,NonPublic')
if($null -eq $parse) {throw 'Native metadata parser missing'}
$keys=@('event','acceptance_run_id','scenario_run_id','log_writer_id','log_writer_sequence',
    'log_writer_batch_generation','log_writer_batch_first_sequence','log_writer_batch_last_sequence','log_writer_attempt_utc_ticks')
$cases=[Collections.Generic.List[string]]::new()
foreach($key in $keys) {
    foreach($value in @('','0','01','9223372036854775807','9223372036854775808',"a=b`t",([string][char]0xd800))) {
        foreach($prefix in @('','[x] [y] ')) {
            $cases.Add($prefix+$key+'='+$value+'; unrecognized=x; unrecognized=y')
            $cases.Add($prefix+' '+$key+' = '+$value+' ; '+$key.ToUpperInvariant()+'=second')
        }
    }
}
foreach($line in @('','   ','ordinary writer message; log_writer_id=w; log_writer_sequence=3',
    ';;;=ignored; =ignored; event=x',('[x] event=x;'+(';'*254)))) {$cases.Add($line)}
foreach($line in $cases) {
    $expected=ConvertFrom-GuiSmokeLogLedgerStructuredFields $line
    $actual=$parse.Invoke($null,[object[]]@($line))
    foreach($key in $keys) {
        if($expected.ContainsKey($key) -ne $actual.ContainsKey($key) -or
           ($expected.ContainsKey($key) -and $expected[$key] -cne $actual[$key])) {throw ('Parser parity: '+$key)}
    }
    if($expected.ContainsKey('__nlink_duplicate_writer_evidence_field') -ne $actual.ContainsKey('__duplicate')) {throw 'Duplicate writer-field parity'}
}
$failed=$false
try {$null=$parse.Invoke($null,[object[]]@('event=x;'+(';'*255)))} catch {$failed=$true}
if(-not $failed) {throw '257 structured segments admitted'}
$selector=[IO.File]::ReadAllText((Join-Path $RepoRoot 'tools/FileTransferOps/LocalPublicationFailureWitness.ps1'))
if($selector -notmatch ('\$bucket.Count -gt '+[NLink.Tools.LocalPublicationInputLimits]::FailureBucketRecords+'\)')) {throw 'Standalone selector/shared admission mismatch'}
# Exercise the actual row admission expression, not a copied numeric predicate.
# These are synthetic arithmetic controls only, never completed primary views.
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/FileTransferOps/LocalPrimaryPublicationWitness.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count) {throw 'Witness module parse failed'}
$guards=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.IfStatementAst] -and
    $node.Clauses[0].Item1.Extent.Text.Contains('$work.operationRows + $boundary.operations.Count')},$true))
if($guards.Count -ne 1) {throw 'Actual operation admission guard changed'}
$guard=[scriptblock]::Create($guards[0].Clauses[0].Item1.Extent.Text)
function Integer($Value,[long]$Minimum=1) {return ($Value -is [int] -or $Value -is [long]) -and $Value -ge $Minimum}
$snapshot=[string[]]@('arithmetic-control')
$boundary=[pscustomobject]@{evidenceLineIndex=0;operations=[object[]]::new(64)}
foreach($total in @(8192,8193)) {
    $work=@{operationRows=[long]$total-64}
    if([bool](& $guard) -ne ($total -gt 8192)) {throw 'Combined operation row boundary changed'}
}
$work.operationRows=0L;$boundary.operations=[object[]]::new(65)
if(-not (& $guard)) {throw 'Per-boundary limit was relaxed'}
'live-membership-parser='+([ordered]@{ParityCases=$cases.Count;FieldCapPlusOneRejected=$true;OperationBoundaryCases=3;AuthorityEvaluations=0} | ConvertTo-Json -Compress)
