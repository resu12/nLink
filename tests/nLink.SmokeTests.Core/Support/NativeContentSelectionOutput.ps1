function Write-NativeContentSelectionOutput($Result,[int]$CallbackCount) {
    # Read the returned diagnostic DTO only; never recheck native custody.
    $row=[ordered]@{DiagnosticOnly=$true;ResultReturned=($null -ne $Result);Status=$null;FirstMissingEdge=$null;
        RecordVisits=$null;CodeUnitVisits=$null;CumulativeMilliseconds=$null;WitnessCount=0;CallbackCount=$CallbackCount;StringsTruncated=$false}
    if($null -ne $Result) {
        foreach($name in @('Status','FirstMissingEdge')) {
            $value=[string]$Result.$name
            if($value.Length -gt 256) {$value=$value.Substring(0,256);$row.StringsTruncated=$true}
            $row[$name]=$value
        }
        foreach($name in @('RecordVisits','CodeUnitVisits','CumulativeMilliseconds')) {$row[$name]=[long]$Result.$name}
        $row.WitnessCount=$Result.GetWitnesses().Length
    }
    [Console]::Out.WriteLine('content-native-selection='+($row | ConvertTo-Json -Compress))
    [Console]::Out.Flush()
}
