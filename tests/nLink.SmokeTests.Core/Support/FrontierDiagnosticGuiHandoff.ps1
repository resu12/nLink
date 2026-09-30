# Diagnostic receipt custody only; the fixture's held 256 MiB file is not the
# source of the small transfer and must never be paired with its destination.
foreach($step in @('copy','persist','prepare','complete')) {
    $accepted=switch($step) {
        'copy' {$null -ne $binding.TryCopySemanticEvidence()}
        'persist' {$null -ne $binding.TryPersistEvidence()}
        'prepare' {$binding.TryPrepareParentHandoffWhenReady()}
        'complete' {$binding.TryCompleteParentHandoff()}
    }
    [Console]::Error.WriteLine('diagnostic-writer-finalization; stage='+$step+'; accepted='+$accepted)
    if(-not $accepted) {throw ('Original diagnostic writer handoff incomplete: '+$step)}
}
if($binding.IsCurrent -or $join.IsCurrent -or $capture.IsCurrent) {throw 'Diagnostic handoff retained GUI custody'}
# The ordinary compatibility copy grants no native ownership; the handoff
# above already transferred the original deny-write native pins.
$ordinary=Copy-GuiSmokeFileAtomicCreateNew -SourcePath $capture.Semantic.CreationPath `
    -DestinationPath (Join-Path $Directory 'filetransfer-retained-log-slice.log') -AllowSourceWrite
if($ordinary.Sha256 -cne $capture.Semantic.Sha256 -or $ordinary.ByteCount -ne $capture.Semantic.ByteCount -or
    $ordinary.LineCount -ne $capture.Semantic.LineCount) {throw 'Diagnostic ordinary source copy differs'}
$writeProbe=$null
try {$writeProbe=[IO.File]::Open($capture.Semantic.CreationPath,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::ReadWrite)} catch [IO.IOException] {}
if($null -ne $writeProbe) {$writeProbe.Dispose();throw 'Diagnostic native log lost write protection'}
'attempt-diagnostic-native-writer-handoff-ok'
