function Write-NativeContentSourceReadFailure($Owned,[guid]$RequestId,[string]$HostName,[string]$Case) {
    if($HostName -cne 'pwsh' -or $Case -cne 'content-native-scale') {return}
    try {
        # One passive snapshot; never read source content or re-evaluate custody.
        $snapshot=$Owned.ContentReadFailureForTest()
        $row=[ordered]@{DiagnosticOnly=$true;SchemaVersion=1;Host=$HostName;Case=$Case;
            RequestId=$RequestId.ToString('D');StringsTruncated=$false}
        foreach($name in @('FirstRejectedPredicate','RejectionSnapshot','CloseReason','CloseSnapshot','NativeException')) {
            $value=[string]$snapshot[$name]
            $maximum=if($name -cin @('RejectionSnapshot','CloseSnapshot')) {128} else {64}
            if($value.Length -gt $maximum) {$value=$value.Substring(0,$maximum);$row.StringsTruncated=$true}
            $row[$name]=$value
        }
        foreach($name in @('WorkMilliseconds','ReadCalls','ProducerMilliseconds')) {$row[$name]=[long]$snapshot[$name]}
        foreach($name in @('WorkRunning','SourceEnded')) {$row[$name]=[bool]$snapshot[$name]}
        [Console]::Out.WriteLine('content-native-source-read-failure='+($row | ConvertTo-Json -Compress))
        [Console]::Out.Flush()
    } catch {
        # Diagnostic failure must not replace the original source-read exception.
    }
}
