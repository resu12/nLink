param([string]$RepoRoot,[string]$Directory,[string]$Mode)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
if($Mode -ceq 'terminal') {
    $arguments=@{CompilerOptions='/define:NLINK_FRONTIER_LEDGER,NLINK_TEST_TERMINAL_REQUEST'}
    if($PSVersionTable.PSVersion.Major -lt 6) {
        $parameters=[CodeDom.Compiler.CompilerParameters]::new()
        $parameters.GenerateInMemory=$true
        $parameters.CompilerOptions='/define:NLINK_FRONTIER_LEDGER,NLINK_TEST_TERMINAL_REQUEST'
        [void]$parameters.ReferencedAssemblies.Add('System.dll')
        [void]$parameters.ReferencedAssemblies.Add('System.Core.dll')
        $arguments=@{CompilerParameters=$parameters}
    }
    Add-Type -Path @((Join-Path $RepoRoot 'tools/GuiSmokeLogLedgerStreamingProcessor.cs'),
        (Join-Path $PSScriptRoot 'TerminalCaptureTiming.cs'),(Join-Path $PSScriptRoot 'TerminalCaptureTimingProbe.cs')) @arguments
    $observations=@(foreach($case in @('pre-metadata','inside-metadata','post-return-expiry','ownership','missing-clock','cleanup','factory-rejection','processing-expired')) {
        [NLink.Tests.TerminalCaptureTimingProbe]::Run((Join-Path $Directory $case),$case)
    })
    $observations | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Directory 'timing.json')
} elseif($Mode -ceq 'production') {
    . (Join-Path $RepoRoot 'tools/GuiSmokeLogLedger.ps1')
    . (Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1')
    $observations=@(foreach($namespace in @('NLink.Tools','NLink.Tools.FrontierLedger')) {
        $captureType=($namespace+'.GuiSmokeLedgerStreamCapture') -as [type]
        if(@($captureType.GetMembers([Reflection.BindingFlags]'Public,NonPublic,Instance,Static') |
            Where-Object {$_.Name -match 'Terminal|terminal'}).Count -ne 0) {throw 'Terminal timing API leaked into production compilation'}
        $processor=($namespace+'.GuiSmokeLogLedgerStreamingProcessor') -as [type]
        $root=Join-Path $Directory $namespace; [void][IO.Directory]::CreateDirectory($root)
        $source=Join-Path $root 'source.log'
        $line='event=operational_log_writer_started; acceptance_run_id=run; scenario_run_id=scenario; log_writer_id=writer-a; log_writer_sequence=1; log_writer_batch_generation=1; log_writer_batch_first_sequence=1; log_writer_batch_last_sequence=1; log_writer_attempt_utc_ticks=1'
        [IO.File]::WriteAllText($source,$line+"`n",[Text.UTF8Encoding]::new($false))
        $result=$processor::ProcessWithNativeCapture($source,1,0,(Join-Path $root 'retained.log'),(Join-Path $root 'writer.log'),
            'run','scenario',[string[]]@(),(Join-Path $root 'semantic.log'),[string[]]@('operational_log_writer_started'))
        if($null -eq $result.NativeCapture -or -not $result.OwnsNativeCapture($result.NativeCapture) -or $null -ne $result.StreamCapture) {
            throw 'Production pathname capture changed'
        }
        $stream=[IO.MemoryStream]::new([IO.File]::ReadAllBytes($source),$false); $streamResult=$null
        try {
            $streamResult=$processor::ProcessFromStream($stream,1,0,(Join-Path $root 'stream-retained.log'),(Join-Path $root 'stream-writer.log'),
                'run','scenario',[string[]]@(),(Join-Path $root 'stream-semantic.log'),[string[]]@('operational_log_writer_started'))
            if($null -ne $streamResult.NativeCapture -or -not $streamResult.StreamCapture.MatchesOriginalSource($stream) -or
                $streamResult.RetainedSha256 -cne $result.RetainedSha256 -or $streamResult.WriterSha256 -cne $result.WriterSha256 -or
                $streamResult.SemanticSha256 -cne $result.SemanticSha256) {throw 'Production stream behavior changed'}
        } finally {if($null -ne $streamResult) {$streamResult.StreamCapture.Dispose()}; $stream.Dispose()}
        [pscustomobject]@{Namespace=$namespace;TimingApiAbsent=$true;PathnameCaptureOwned=$true;StreamBehaviorPreserved=$true}
    })
    $observations | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Directory 'production.json')
} else {throw 'Unknown timing test mode'}
