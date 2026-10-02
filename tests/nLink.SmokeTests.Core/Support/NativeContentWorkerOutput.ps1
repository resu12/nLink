function Save-NativeContentWorkerOutput([string]$Directory,[string]$Phase,[bool]$Deadline,$OutputTask,$ErrorTask,[int]$BarrierMilliseconds=30000) {
    $limit=65536;$captures=[ordered]@{}
    foreach($entry in @(@('stdout',$OutputTask),@('stderr',$ErrorTask))) {
        $task=$entry[1];$text='';$state='unavailable'
        if($null -ne $task) {
            $state=$task.Status.ToString()
            if($task.Status -eq [Threading.Tasks.TaskStatus]::RanToCompletion) {$text=$task.GetAwaiter().GetResult();$state='complete'}
        }
        $length=$text.Length
        if($length -gt $limit) {$text=$text.Substring(0,$limit)}
        [IO.File]::WriteAllText((Join-Path $Directory ('content-native-worker-'+$entry[0]+'.txt')),$text,[Text.UTF8Encoding]::new($false))
        $captures[$entry[0]]=[ordered]@{State=$state;OriginalCharacters=$length;RetainedCharacters=$text.Length;Truncated=($length -gt $limit)}
    }
    [IO.File]::WriteAllText((Join-Path $Directory 'content-native-worker-output.json'),
        ([ordered]@{DiagnosticOnly=$true;Phase=$Phase;Deadline=$Deadline;BarrierMilliseconds=$BarrierMilliseconds;
            MaximumCharactersPerStream=$limit;Captures=$captures} | ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
}
