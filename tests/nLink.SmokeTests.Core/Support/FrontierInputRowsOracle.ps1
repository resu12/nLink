# Frozen pre-optimization input-row loop; independent differential oracle.
function Invoke-FrontierInputRowsOracle {
    param([string[]]$Full,$Index,$Failure,$rows,[Action]$visit)
    function Require([bool]$Value,[string]$Reason) {if(-not $Value) {throw $Reason}}
    $work=@{Units=0L}
                $failurePosition=-1
                for($i=0;$i -lt $Full.Length;$i++) {
                    $visit.Invoke(); $line=$Full[$i]; $event=$Index.AllEvents[$i]
                    Require ([object]::ReferenceEquals($line,$event.RawLine) -and $event.FilePath -ceq $Index.LogFiles[0] -and
                        $line.Length -le 262144 -and $line.Length -le [NLink.Tools.LocalPublicationInputLimits]::ProducerCodeUnits-$work.Units -and
                        $line.Split([char[]]@(';'),257).Length -le 256) 'native_frontier_input'
                    $work.Units+=$line.Length
                    $fields=ConvertFrom-GuiSmokeLogLedgerStructuredFields $line
                    $identity=Read-FileTransferNativeFrontierScope $line $fields
                    Require ($identity.Error -cne 'native_frontier_duplicate_scope') 'native_frontier_duplicate_scope'
                    $dataSession=Read-FileTransferNativeFrontierDataSession $line $fields
                    $rows.Add([pscustomobject]@{Position=$i;Fields=$fields;Identity=$identity;DataSession=$dataSession})
                    if([object]::ReferenceEquals($event,$Failure.Event)) {Require ($failurePosition -eq -1) 'native_frontier_failure_duplicate'; $failurePosition=$i}
                }
    return $failurePosition
}
