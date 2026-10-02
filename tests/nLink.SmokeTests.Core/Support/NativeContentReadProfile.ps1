# Instrument only the test process's copy of the original function. Every
# original statement and its order remain intact; no additional source pass.
$original=(Get-Item Function:Read-FileTransferLogEvents).ScriptBlock.ToString()
$sourceLine='$sourceLines = $OwnedSource.ReadLines()'
$conversion='$event = ConvertFrom-FileTransferLogLine -Line $line -FilePath $file -LineNumber $lineNumber -Sequence $sequence'
$return='return $events.ToArray()'
foreach($anchor in @($sourceLine,$conversion,$return)) {
    if([regex]::Matches($original,[regex]::Escape($anchor)).Count -ne 1) {throw 'Content read profile original anchor differs'}
}
$sourceObservation=
    "[NLink.Tests.NativeContentReadProfileForTest]::Observe('source_lines_assignment_started')`n"+$sourceLine+
    "`n[NLink.Tests.NativeContentReadProfileForTest]::Observe('source_lines_assignment_returned')"
$conversionObservation=
    '[NLink.Tests.NativeContentReadProfileForTest]::ParserStart($lineNumber)'+"`n"+$conversion+"`n"+
    '[NLink.Tests.NativeContentReadProfileForTest]::ParserReturned($lineNumber)'
$returnObservation="[NLink.Tests.NativeContentReadProfileForTest]::Observe('event_array_return_started')`n"+$return
$instrumented=$original.Replace($sourceLine,$sourceObservation).Replace($conversion,$conversionObservation).Replace($return,$returnObservation)
# Removing the inserted observations must reproduce the exact original body.
$removed=$instrumented.Replace($sourceObservation,$sourceLine).Replace($conversionObservation,$conversion).Replace($returnObservation,$return)
if($removed -cne $original) {throw 'Content read profile changed an original statement'}
Set-Item Function:Read-FileTransferLogEvents ([scriptblock]::Create($instrumented))
