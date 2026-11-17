param([string]$RepoRoot,[string]$Directory,[string]$Mode,[string]$Probe='',[switch]$RequireHashCmdlet)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$private:relay=$null; $private:completion=$null
$guiMode=if($Mode -ceq 'gui-nonzero') {'nonzero'} elseif($Mode -ceq 'gui-timeout') {'timeout'} elseif($Mode -ceq 'gui-output-fails') {'output-fails'} else {'valid'}
$root=Join-Path (Join-Path $Directory 'gui') $guiMode
try {
    if($env:NLINK_GUI_WRITER_RELAY) {
        if($null -ne [NLink.Tools.LocalGuiWriterRequest]::TryAttach([string]$env:NLINK_GUI_WRITER_RELAY,
            [guid]$env:NLINK_GUI_WRITER_RELAY_ID,[string]$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID,
            [string]$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID,$root)) { throw 'Relay admitted a GUI-finalizer attachment' }
        $run=[string]$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID
        if($Mode -ceq 'wrong-run') { $run+='-foreign' }
        $private:relay=[NLink.Tools.LocalGuiWriterRequest]::TryAttachRelay([string]$env:NLINK_GUI_WRITER_RELAY,
            [guid]$env:NLINK_GUI_WRITER_RELAY_ID,$run,[string]$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID,$root)
    }
    $args=@{RepoRoot=$RepoRoot;Directory=(Join-Path $Directory $(if($Mode -ceq 'wrong-root') {'foreign-gui'} else {'gui'}))
        AcceptedCompletion=[ref]$private:completion;SingleCase=$guiMode}
    if($Mode -cnotin @('no-completion','forged','decoded-copy')) { $args.ForwardRelay=$private:relay }
    $null=& (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/LocalGuiWriterRequest.ps1') @args
    if($null -ne $private:relay -and $Mode -ceq 'forged') {
        $rejected=$false
        try { $null=$private:relay.Forward(($private:completion | ConvertTo-Json -Depth 8 | ConvertFrom-Json)) } catch { $rejected=$true }
        if(-not $rejected) { throw 'Caller-authored completion accepted' }
    }
    if($null -ne $private:relay -and $Mode -ceq 'decoded-copy') {
        $flags=[Reflection.BindingFlags]'Instance,NonPublic'
        $bytes=[IO.MemoryStream]::new(); $writer=[IO.BinaryWriter]::new($bytes,[Text.UTF8Encoding]::new($false,$true),$true)
        try {
            $private:completion.GetType().GetMethod('WriteTo',$flags).Invoke($private:completion,@($writer)); $writer.Flush(); $bytes.Position=0
            $reader=[IO.BinaryReader]::new($bytes,[Text.UTF8Encoding]::new($false,$true),$true)
            try {
                $copy=$private:completion.GetType().GetMethod('ReadFrom',[Reflection.BindingFlags]'Static,NonPublic').Invoke($null,
                    @($reader,'gui-writer-parent-run','gui-writer-parent-run:scenario:1'))
                if($private:relay.Forward($copy)) { throw 'Decoded historical completion re-forwarded' }
            } finally { $reader.Dispose() }
        } finally { $writer.Dispose(); $bytes.Dispose() }
    }
    if($null -ne $private:relay -and $Mode -ceq 'repeat-forward' -and $private:relay.Forward($private:completion)) { throw 'Repeated forwarding accepted' }
    $hashProbe=$null
    if($RequireHashCmdlet) {
        $hashProbe=@{Version=$PSVersionTable.PSVersion.ToString();Module=(Get-Command Get-FileHash -ErrorAction Stop).Source
            Hash=(Get-FileHash -LiteralPath (Join-Path $RepoRoot 'VERSION') -Algorithm SHA256).Hash}
        $manifestChecks=@(& (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/FailedGuiAnalysisStatus.ps1') `
            -RepoRoot $RepoRoot -OutputRoot (Join-Path $Directory 'manifest-checks'))
        if($manifestChecks -cnotcontains 'failed-gui-analysis-status-ok') { throw 'Direct manifest checks did not complete' }
        $hashProbe.DirectManifestChecksPassed=$true
    }
    [IO.File]::WriteAllText((Join-Path $Directory 'worker.json'),(@{WrapperPid=$PID;GuiPid=$(if($null -ne $private:completion){$private:completion.GuiProcessId}else{0})
        Probe=$Probe;Attached=$null -ne $private:relay;GuiCompletion=$null -ne $private:completion;HashProbe=$hashProbe} | ConvertTo-Json -Compress))
    [Console]::Out.WriteLine('relay-stdout-1'); [Console]::Out.WriteLine('relay-stdout-2')
    [Console]::Error.WriteLine('relay-stderr-1'); [Console]::Error.WriteLine('relay-stderr-2')
    if($Mode -ceq 'wrapper-nonzero') { exit 9 }
} finally { if($null -ne $private:relay) { $private:relay.Dispose() } }
