$workerCase=$primaryCase.Substring('worker-'.Length)
$sourceDiagnostics=$workerCase.StartsWith('source-diagnostic-',[StringComparison]::Ordinal)
if($sourceDiagnostics) {$workerCase='source-'+$workerCase.Substring('source-diagnostic-'.Length)}
$piping=$workerCase.StartsWith('join-pipes-',[StringComparison]::Ordinal); $primaryOutput=$null
$nativeProcess=$null; $offerMap=$null; $offerView=$null; $factsMap=$null; $factsView=$null; $signals=@{}
$endpoint=$primary.Name+'.frontier-primary-v1.files-v1'
try {
    foreach($part in @('begin','attached','release')) {$signals[$part]=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$primary.Name+'.test.worker.'+$part)}
    $start=[Diagnostics.ProcessStartInfo]::new(); $start.FileName=(Get-Process -Id $PID).Path
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $script=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierPrimaryWorker.ps1'
    if($workerCase.StartsWith('source-',[StringComparison]::Ordinal)) {$script=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierPrimarySource.ps1'}
    if($workerCase.StartsWith('read-validation-',[StringComparison]::Ordinal)) {$script=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierPrimaryReadValidation.ps1'}
    if($workerCase.StartsWith('origin-batch-',[StringComparison]::Ordinal)) {$script=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierPrimaryOrigins.ps1'}
    if($workerCase.StartsWith('evaluations-',[StringComparison]::Ordinal)) {$script=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierPrimaryEvaluations.ps1'}
    if($workerCase.StartsWith('payloads-',[StringComparison]::Ordinal)) {$script=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierPrimaryPayloads.ps1'}
    if($workerCase.StartsWith('witnesses-',[StringComparison]::Ordinal)) {$script=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierPrimaryWitnesses.ps1'}
    if($workerCase.StartsWith('join-',[StringComparison]::Ordinal)) {$script=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierPrimaryJoin.ps1'}
    $arguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',$script,'-RepoRoot',$RepoRoot,'-Name',$primary.Name,
        '-RequestId',$primary.RequestId.ToString('D'),'-SourcePath',$completion.Outputs[3].Path,'-Case',$workerCase)
    if($sourceDiagnostics) {$arguments+='-SourceDiagnostics'}
    if($workerCase.StartsWith('join-pipes-terminal-',[StringComparison]::Ordinal)) {$arguments+=@('-TerminalDirectory',$Directory)}
    $diagnosticCase=Get-Variable rendezvousCase -ValueOnly -ErrorAction SilentlyContinue
    if($diagnosticCase) {$arguments+=@('-RendezvousCase',$diagnosticCase)}
    $start.Arguments=($arguments | ForEach-Object {'"'+$_.Replace('"','\"')+'"'}) -join ' '
    if($piping) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierPrimaryOutputStart.ps1')
        if($skipPipes) {return}
    } else {
        $nativeProcess=[Diagnostics.Process]::Start($start); [void]$nativeProcess.Handle
        $nativeOut=$nativeProcess.StandardOutput.ReadToEndAsync(); $nativeErr=$nativeProcess.StandardError.ReadToEndAsync()
    }
    if($piping) { } elseif($workerCase -ceq 'self-worker') {
        $self=[Diagnostics.Process]::GetCurrentProcess()
        try {if(-not $primary.BindWorker($self)) {throw 'Self-bound worker fixture failed'}} finally {$self.Dispose()}
    } elseif($workerCase -cne 'unbound' -and -not $primary.BindWorker($nativeProcess)) {throw 'Native primary worker binding failed'}
    foreach($pin in $primaryPins) {[PrimaryPositionProbe]::Set($pin,23L)}
    if($workerCase -ceq 'copied-inputs') {
        $copy=$parentClone.Invoke($inputs,$null)
        if($copy.TryPrepareWorkerHandoff() -or -not $inputs.IsCurrent) {throw 'Copied inputs prepared/retired worker handoff'}
    }
    if($workerCase -ceq 'preopened') {if($null -eq $open.Invoke($inputs,@(0))) {throw 'Preopened fixture failed'}}
    if($workerCase -ceq 'prepare-budget') {
        $w=$inputs.GetType().GetField('work',$parentFlags).GetValue($inputs)
        $elapsed=@($w.GetType().GetFields($parentFlags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
        $elapsed.SetValue($w,[long]([Diagnostics.Stopwatch]::Frequency*21))
    }
    if($workerCase -cne 'missing-offer') {
        $prepared=if($piping -and $null -ne $reportRun -and $reportRun.NativeHandoffReady) {$true} else {$inputs.TryPrepareWorkerHandoff()}
        if($prepared -eq ($workerCase -cin @('unbound','self-worker','preopened','prepare-budget','existing-publication'))) {throw 'Worker preparation gate mismatch'}
        if($prepared) {
            $offerMap=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($endpoint); $offerView=$offerMap.CreateViewAccessor()
            $factsMap=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($endpoint+'.facts-v1'); $factsView=$factsMap.CreateViewAccessor()
        }
    }
    if($workerCase -ceq 'unbound' -and -not $primary.BindWorker($nativeProcess)) {throw 'Late fixture binding failed'}
    if($piping) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierPrimaryOutputParent.ps1')
        return
    }
    if($workerCase.StartsWith('join-output-',[StringComparison]::Ordinal)) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierDiagnosticOutputParent.ps1')
        return
    }
    switch($workerCase) {
        'repeat-prepare' {if($inputs.TryPrepareWorkerHandoff() -or $inputs.IsCurrent) {throw 'Worker offer replay retained custody'}}
        'cancel-before' {$primary.Dispose()}
        'file-binding' {$offerView.Write(16L,[byte]($offerView.ReadByte(16L) -bxor 1))}
        'file-state' {$offerView.Write(8L,2)}
        'file-tail' {$offerView.Write(2375L,[byte]1)}
        'facts-body' {$factsView.Write(80L,[byte]($factsView.ReadByte(80L) -bxor 1))}
        'facts-binding' {$factsView.Write(16L,[byte]($factsView.ReadByte(16L) -bxor 1))}
        'facts-length' {$factsView.Write(12L,[int]::MaxValue)}
        'facts-padding' {$factsView.Write(4194383L,[byte]1)}
        'worker-incarnation' {$primaryView.Write(40L,$primaryView.ReadInt64(40L)+1L)}
        'parent-incarnation' {$primaryView.Write(8L,$primaryView.ReadInt64(8L)+1L)}
        'parent-read-after-offer' {if($null -ne $open.Invoke($inputs,@(0)) -or $inputs.IsCurrent) {throw 'Parent read after native offer was allowed'}}
    }
    if($workerCase -cin @('native-handle','partial-pull','native-topology')) {
        # Keep the entire transport coherent while making one actual parent
        # handle refer to the wrong file. Native identity, not hashes, must reject.
        $index=if($workerCase -ceq 'partial-pull') {$primaryPins.Count-1} else {1}
        # A distinct duplicate avoids the packet's duplicate-number guard.
        $sourceHandle=$primary.GetType().GetField('frontierSourceHandle',$parentFlags).GetValue($primary)
        $offerView.Write([long](128+64+52*$index+24),$sourceHandle.DangerousGetHandle().ToInt64())
        if($workerCase -ceq 'native-topology') {
            $offerView.Write([long](128+64+52*$index+24),$primaryPins[$index].DangerousGetHandle().ToInt64())
            $offerView.Write(144L,[byte]($offerView.ReadByte(144L) -bxor 1))
        }
        $body=[byte[]]::new($offerView.ReadInt32(64L)); [void]$offerView.ReadArray(128L,$body,0,$body.Length)
        $sha=[Security.Cryptography.SHA256]::Create()
        try {
            $digest=$sha.ComputeHash($body); for($i=0;$i -lt 32;$i++) {$offerView.Write([long](96+$i),$digest[$i])}
            $snapshot=[byte[]]::new(2376); [void]$offerView.ReadArray(0L,$snapshot,0,$snapshot.Length)
            $digest=$sha.ComputeHash($snapshot); for($i=0;$i -lt 32;$i++) {$factsView.Write([long](16+$i),$digest[$i])}
        } finally {$sha.Dispose()}
    }
    [void]$signals.begin.Set()
    if(-not $signals.attached.WaitOne(30000)) {
        if($nativeProcess.HasExited) {throw "Native worker failed before attachment: $($nativeOut.GetAwaiter().GetResult()) $($nativeErr.GetAwaiter().GetResult())"}
        throw 'Native worker attachment deadline'
    }
    if($workerCase -cin @('wrong-request','wrong-run','wrong-path') -and -not $inputs.IsCurrent) {throw 'Rejected foreign parameters cancelled parent custody'}
    if($workerCase -cin @('valid','copied-inputs','post-scope','post-facts','post-parent-pin','parent-cancel','worker-dispose','inputs-dispose',
        'copied-worker','copied-worker-inputs','attach-replay','worker-budget','post-worker-pin',
        'source-valid','source-copied-reader','source-copied-worker','source-origin-substitution','source-origin-index','source-pre-eof',
        'source-post-scope','source-post-facts','source-parent-cancel','source-post-parent-pin','evaluations-valid','evaluations-actual-evaluations',
        'payloads-valid','payloads-two-payloads','payloads-both-writers','payloads-foreign-identities','payloads-pre-take',
        'witnesses-valid','witnesses-maximum','witnesses-foreign-identities','join-valid','join-foreign-context',
        'join-prepare-valid','join-prepare-copied-batch','join-prepare-copied-preparation','join-prepare-descriptor-copy')) {
        foreach($pin in $primaryPins) {if([PrimaryPositionProbe]::Position($pin) -ne 23L) {throw 'Worker pull or fixture hash changed parent native positions'}}
    }
    switch($workerCase) {
        'join-parent-cancel' {
            $event=[Threading.EventWaitHandle]::OpenExisting($primary.Name+'.closed')
            try {[void]$event.Set()} finally {$event.Dispose()}
        }
        'join-prepare-parent-cancel' {
            $event=[Threading.EventWaitHandle]::OpenExisting($primary.Name+'.closed')
            try {[void]$event.Set()} finally {$event.Dispose()}
        }
        'witnesses-post-scope' {$primaryView.Write(64L,[byte]($primaryView.ReadByte(64L) -bxor 1))}
        'witnesses-post-facts' {$factsView.Write(80L,[byte]($factsView.ReadByte(80L) -bxor 1))}
        'witnesses-post-parent-pin' {$primaryPins[0].Dispose(); if($inputs.IsCurrent) {throw 'Closed witness parent pin remained current'}}
        'witnesses-parent-cancel' {
            $event=[Threading.EventWaitHandle]::OpenExisting($primary.Name+'.closed')
            try {[void]$event.Set()} finally {$event.Dispose()}
        }
        'payloads-post-scope' {$primaryView.Write(64L,[byte]($primaryView.ReadByte(64L) -bxor 1))}
        'payloads-post-facts' {$factsView.Write(80L,[byte]($factsView.ReadByte(80L) -bxor 1))}
        'payloads-post-parent-pin' {$primaryPins[0].Dispose(); if($inputs.IsCurrent) {throw 'Closed payload parent pin remained current'}}
        'payloads-parent-cancel' {
            $event=[Threading.EventWaitHandle]::OpenExisting($primary.Name+'.closed')
            try {[void]$event.Set()} finally {$event.Dispose()}
        }
        'evaluations-post-scope' {$primaryView.Write(64L,[byte]($primaryView.ReadByte(64L) -bxor 1))}
        'evaluations-post-facts' {$factsView.Write(80L,[byte]($factsView.ReadByte(80L) -bxor 1))}
        'evaluations-parent-cancel' {
            $event=[Threading.EventWaitHandle]::OpenExisting($primary.Name+'.closed')
            try {[void]$event.Set()} finally {$event.Dispose()}
        }
        'source-post-scope' {$primaryView.Write(64L,[byte]($primaryView.ReadByte(64L) -bxor 1))}
        'source-post-facts' {$factsView.Write(80L,[byte]($factsView.ReadByte(80L) -bxor 1))}
        'source-post-parent-pin' {$primaryPins[0].Dispose(); if($inputs.IsCurrent) {throw 'Closed source parent pin remained current'}}
        'source-parent-cancel' {
            $event=[Threading.EventWaitHandle]::OpenExisting($primary.Name+'.closed')
            try {[void]$event.Set()} finally {$event.Dispose()}
        }
        'post-scope' {$primaryView.Write(64L,[byte]($primaryView.ReadByte(64L) -bxor 1))}
        'post-facts' {$factsView.Write(80L,[byte]($factsView.ReadByte(80L) -bxor 1))}
        'post-parent-pin' {$primaryPins[0].Dispose(); if($inputs.IsCurrent) {throw 'Closed parent pin remained current'}}
        'parent-cancel' {
            $event=[Threading.EventWaitHandle]::OpenExisting($primary.Name+'.closed')
            try {[void]$event.Set()} finally {$event.Dispose()}
        }
    }
    [void]$signals.release.Set()
    if(-not $nativeProcess.WaitForExit(30000)) {throw 'Native worker exit deadline'}
    $nativeProcess.WaitForExit(); $output=$nativeOut.GetAwaiter().GetResult(); $nativeError=$nativeErr.GetAwaiter().GetResult()
    if($nativeProcess.ExitCode -ne 0 -or $output -notmatch 'frontier-primary-worker=') {throw "Native worker fixture failed: $output $nativeError"}
    $originLine=@($output -split '\r?\n' | Where-Object {$_.StartsWith('frontier-primary-source-origins=')})
    if($originLine.Count) {
        $parentFacts=$inputs.GetType().GetField('facts',$parentFlags).GetValue($inputs)
        $expected=$parentFacts.GetType().GetField('semanticOrigins',$parentFlags).GetValue($parentFacts)
        if($originLine.Count -ne 1 -or $originLine[0] -cne ('frontier-primary-source-origins='+($expected -join ','))) {throw 'Worker origins differ from original parent facts'}
    }
    $witnessLine=@($output -split '\r?\n' | Where-Object {$_.StartsWith('frontier-primary-witnesses-facts=')})
    if($witnessLine.Count) {
        if($witnessLine.Count -ne 1) {throw 'Repeated witness diagnostic record'}
        $received=$witnessLine[0].Substring('frontier-primary-witnesses-facts='.Length) | ConvertFrom-Json
        $parentFacts=$inputs.GetType().GetField('facts',$parentFlags).GetValue($inputs)
        $origins=$parentFacts.GetType().GetField('semanticOrigins',$parentFlags).GetValue($parentFacts)
        $starts=$parentFacts.GetType().GetField('lineStarts',$parentFlags).GetValue($parentFacts)
        $bytes=$parentFacts.GetType().GetField('sourceBytes',$parentFlags).GetValue($parentFacts)
        foreach($row in $received.Rows) {
            $line=$origins[$row.Full-1]; $end=if($line -eq $starts.Length) {$bytes} else {$starts[$line]}
            if($row.Line -ne $line -or $row.Offset -ne $starts[$line-1] -or $row.Bytes -ne $end-$starts[$line-1]) {throw 'Worker witness differs from original parent cursor'}
        }
        $manifests=$parentFacts.GetType().GetField('manifests',$parentFlags).GetValue($parentFacts)
        $payload=$manifests[0].GetType().GetField('Payloads',$parentFlags).GetValue($manifests[0])[0]
        foreach($name in @('Completion','Marker')) {
            $actual=$received.$name; $expected=$payload.GetType().GetField($name,$parentFlags).GetValue($payload); $type=$expected.GetType()
            $offset=$type.GetField('Offset',$parentFlags).GetValue($expected); $length=$type.GetField('Length',$parentFlags).GetValue($expected)
            $endLine=if($offset+$length+2 -eq $bytes) {$starts.Length} else {[Array]::IndexOf($starts,[long]($offset+$length+2))}
            if($actual.ByteOffset -ne $offset -or $actual.ByteCount -ne $length -or $actual.FirstSourceLine -ne [Array]::IndexOf($starts,[long]$offset)+1 -or
                $actual.LastSourceLine -ne $endLine -or $actual.BatchGeneration -ne $type.GetField('Generation',$parentFlags).GetValue($expected) -or
                $actual.FirstSequence -ne $type.GetField('First',$parentFlags).GetValue($expected) -or $actual.LastSequence -ne $type.GetField('Last',$parentFlags).GetValue($expected) -or
                $actual.RawSha256 -cne [BitConverter]::ToString($type.GetField('RawHash',$parentFlags).GetValue($expected)).Replace('-','').ToLowerInvariant() -or
                $actual.BatchSha256 -cne [BitConverter]::ToString($type.GetField('BatchHash',$parentFlags).GetValue($expected)).Replace('-','').ToLowerInvariant()) {throw 'Worker witness anchor differs from original native manifest'}
        }
    }
    $payloadLine=@($output -split '\r?\n' | Where-Object {$_.StartsWith('frontier-primary-payloads-facts=')})
    if($payloadLine.Count) {
        if($payloadLine.Count -ne 1) {throw 'Repeated payload diagnostic record'}
        # Windows PowerShell keeps a parsed JSON array as one pipeline object.
        $decoded=$payloadLine[0].Substring('frontier-primary-payloads-facts='.Length) | ConvertFrom-Json
        $received=@($decoded)
        $parentFacts=$inputs.GetType().GetField('facts',$parentFlags).GetValue($inputs)
        $manifests=$parentFacts.GetType().GetField('manifests',$parentFlags).GetValue($parentFacts); $position=0
        foreach($manifest in $manifests) {
            $writer=$manifest.GetType().GetField('Writer',$parentFlags).GetValue($manifest)
            foreach($payload in $manifest.GetType().GetField('Payloads',$parentFlags).GetValue($manifest)) {
                if($position -ge $received.Count) {throw 'Worker omitted original native payload'}
                $row=$received[$position++]; $type=$payload.GetType()
                $expectedHash=[BitConverter]::ToString($type.GetField('Hash',$parentFlags).GetValue($payload)).Replace('-','').ToLowerInvariant()
                $file=$type.GetField('File',$parentFlags).GetValue($payload); $ft=$file.GetType()
                $identity=$ft.GetField('Volume',$parentFlags).GetValue($file).ToString('x8')+':'+
                    $ft.GetField('IndexHigh',$parentFlags).GetValue($file).ToString('x8')+$ft.GetField('IndexLow',$parentFlags).GetValue($file).ToString('x8')
                if($row.Observation -cne $type.GetField('Observation',$parentFlags).GetValue($payload).ToString('D') -or
                    $row.Writer -cne $writer -or $row.Hash -cne $expectedHash -or $row.File -cne $identity -or
                    $row.Bytes -ne $ft.GetField('Length',$parentFlags).GetValue($file)) {throw 'Worker payload differs from original parent manifest'}
            }
        }
        if($position -ne $received.Count) {throw 'Worker invented native payloads'}
    }
    $output.Trim()
    $primary.Dispose()
    if($inputs.IsCurrent -or @($primaryPins | Where-Object {-not $_.IsClosed}).Count) {throw 'Native worker handoff leaked parent pins'}
} finally {
    if($signals.ContainsKey('release')) {[void]$signals.release.Set()}
    if($null -ne $nativeProcess) {
        try { if($null -ne $nativeProcess.Id -and -not $nativeProcess.WaitForExit(5000)) {$nativeProcess.Kill(); [void]$nativeProcess.WaitForExit(5000)} }
        catch [InvalidOperationException] { }
        finally {$nativeProcess.Dispose()}
    }
    if($null -ne $primaryOutput) {$primaryOutput.Dispose()}
    if($null -ne $offerView) {$offerView.Dispose()}; if($null -ne $offerMap) {$offerMap.Dispose()}
    if($null -ne $factsView) {$factsView.Dispose()}; if($null -ne $factsMap) {$factsMap.Dispose()}
    foreach($event in $signals.Values) {$event.Dispose()}
}
