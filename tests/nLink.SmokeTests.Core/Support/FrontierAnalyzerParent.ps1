$analyzerProcess=[Diagnostics.Process]::new(); $analyzerRun=$null; $analyzerCollision=$null; $analyzerTransport=$null
$analyzerBegin=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$primary.Name+'.test.analyzer.begin')
$analyzerContinue=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$primary.Name+'.test.analyzer.continue')
try {
    # The ordinary baseline uses a byte-identical independent file, because the
    # real native input is intentionally unavailable to path reopening.
    $analyzerBaseline=Join-Path $Directory 'baseline-source.log'
    $analyzerRead=$primary.TryOpenTerminalSource($source)
    if($null -eq $analyzerRead) {throw 'Analyzer baseline native read unavailable'}
    $analyzerCopy=[IO.File]::Create($analyzerBaseline)
    try {$analyzerBuffer=[byte[]]::new(32768); while(($analyzerBytes=$analyzerRead.Read($analyzerBuffer,0,$analyzerBuffer.Length)) -gt 0) {$analyzerCopy.Write($analyzerBuffer,0,$analyzerBytes)}}
    finally {$analyzerCopy.Dispose(); $analyzerRead.Dispose()}
    $analyzerStart=[Diagnostics.ProcessStartInfo]::new(); $analyzerStart.FileName=(Get-Process -Id $PID).Path
    $analyzerStart.UseShellExecute=$false; $analyzerStart.CreateNoWindow=$true
    $analyzerStart.RedirectStandardOutput=$true; $analyzerStart.RedirectStandardError=$true
    $analyzerArguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAnalyzerWorker.ps1'),
        '-RepoRoot',$RepoRoot,'-SourcePath',$source,'-Directory',$Directory,'-Case',$analyzerCase)
    $analyzerStart.Arguments=($analyzerArguments | ForEach-Object {'"'+$_+'"'}) -join ' '
    $analyzerStart.EnvironmentVariables['NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID']='frontier-gui-run'
    $analyzerStart.EnvironmentVariables['NLINK_FILETRANSFER_SCENARIO_RUN_ID']='frontier-gui-scenario'
    $analyzerStart.EnvironmentVariables['NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT']='1'
    $analyzerProcess.StartInfo=$analyzerStart
    if($analyzerCase -ceq 'missing-receiver') {
        $analyzerCollision=[IO.MemoryMappedFiles.MemoryMappedFile]::CreateNew($primary.Name+'.frontier-primary-v1.files-v1.diagnostic-v1',160)
    }
    $analyzerRun=$sourcePreparation.StartWorkerWithReports($analyzerProcess,$Directory,'None',[DateTimeOffset]::UtcNow.ToString('O'))
    if(-not $analyzerRun.SourceBound -or -not $analyzerRun.NativeHandoffReady -or
        ($null -eq $analyzerRun.DiagnosticReceiver) -ne ($analyzerCase -ceq 'missing-receiver')) {throw 'Analyzer native launch unavailable'}
    if(-not $analyzerBegin.WaitOne(60000)) {throw 'Analyzer baseline deadline'}
    $analyzerAdoption=$null
    if($null -ne $analyzerRun.DiagnosticReceiver -and $analyzerCase -cne 'no-adoption') {
        $analyzerAdoption=$analyzerRun.DiagnosticReceiver.TryAdoptWhenOfferedAsync()
    }
    [void]$analyzerContinue.Set()
    if(-not $analyzerProcess.WaitForExit(90000)) {throw 'Analyzer worker deadline'}
    $analyzerStdout=$analyzerRun.Stdout.GetAwaiter().GetResult(); $analyzerStderr=$analyzerRun.Stderr.GetAwaiter().GetResult()
    $analyzerExit=if($analyzerCase -ceq 'report-failure') {7} else {0}
    if($analyzerProcess.ExitCode -ne $analyzerExit -or $analyzerStderr -cne 'original analyzer stderr' -or
        $analyzerStdout -notmatch 'frontier-analyzer-worker=') {throw ('Analyzer failed: '+$analyzerStdout+' '+$analyzerStderr)}
    if($null -ne $analyzerAdoption -and -not $analyzerAdoption.Wait(25000)) {throw 'Analyzer adoption task leaked'}
    if($null -ne $analyzerRun.DiagnosticReceiver) {$analyzerTransport=$analyzerRun.DiagnosticReceiver.TakeAfterExit()}
    $analyzerExpected=$analyzerCase -ceq 'valid'
    if(($null -ne $analyzerTransport) -ne $analyzerExpected) {
        throw ('Analyzer transport mismatch: '+$analyzerStdout+' adopted='+$(if($null -ne $analyzerAdoption) {$analyzerAdoption.Result} else {$false}))
    }
    $analyzerJoin=$analyzerRun.DiagnosticOutput.TryJoinDiagnostic($analyzerTransport)
    if(($null -ne $analyzerJoin) -ne $analyzerExpected) {throw 'Analyzer output join changed outcome'}
    if($null -ne $analyzerTransport -and ($analyzerTransport.LiveProofEligible -or
        $analyzerTransport.NativeWriterCustodyEstablished -or $analyzerTransport.PublicationCompletionIssued)) {throw 'Analyzer diagnostic promoted authority'}
    if($analyzerExpected) {
        $analyzerRows=@($analyzerStdout -split '\r?\n' | Where-Object {$_.StartsWith('frontier-analyzer-native=')})
        if($analyzerRows.Count -ne 1) {throw 'Original native pair metadata missing'}
        $analyzerPair=$analyzerRows[0].Substring('frontier-analyzer-native='.Length) | ConvertFrom-Json
        if($analyzerTransport.FullInvocationId -ne [guid]$analyzerPair.fullInvocation -or
            $analyzerTransport.CompactInvocationId -ne [guid]$analyzerPair.compactInvocation -or
            $analyzerTransport.FullInvocationId -eq $analyzerTransport.CompactInvocationId -or
            -not $analyzerPair.diagnosticOfferAttempted -or -not $analyzerPair.diagnosticCommitAttempted) {throw 'Diagnostic was not bound to the original primary pair'}
    }
    $analyzerStdout.Trim()
    'frontier-analyzer-integration='+$prepareOwner+'/'+$analyzerCase
} finally {
    [void]$analyzerContinue.Set()
    try {if($null -ne $analyzerProcess.Id -and -not $analyzerProcess.WaitForExit(5000)) {$analyzerProcess.Kill(); [void]$analyzerProcess.WaitForExit(5000)}}
    catch [InvalidOperationException] {} finally {$analyzerProcess.Dispose()}
    if($null -ne $analyzerCollision) {$analyzerCollision.Dispose()}
    $analyzerBegin.Dispose(); $analyzerContinue.Dispose()
}
