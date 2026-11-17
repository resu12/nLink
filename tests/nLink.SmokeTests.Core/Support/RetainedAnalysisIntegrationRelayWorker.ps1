param([string]$RepoRoot,[string]$Directory,[string]$GeneratedSource,[string]$RunId,[string]$ScenarioId)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$private:relay=$null; $private:completion=$null
try {
    $root=Join-Path $Directory 'valid'
    $private:relay=[NLink.Tools.LocalGuiWriterRequest]::TryAttachRelay($env:NLINK_GUI_WRITER_RELAY,
        [guid]$env:NLINK_GUI_WRITER_RELAY_ID,$RunId,$ScenarioId,$root)
    if($null -eq $private:relay) { throw 'Actual relay attachment missing' }
    $null=& (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/LocalGuiWriterRequest.ps1') `
        -RepoRoot $RepoRoot -Directory $Directory -AcceptedCompletion ([ref]$private:completion) `
        -GeneratedSource $GeneratedSource -GeneratedRun $RunId -GeneratedScenario $ScenarioId -ForwardRelay $private:relay
    if($null -eq $private:completion) { throw 'Actual finalizer completion missing' }
    [IO.File]::WriteAllText((Join-Path $root 'integration-relay.json'),(@{WrapperPid=$PID;GuiPid=$private:completion.GuiProcessId} | ConvertTo-Json))
} finally { if($null -ne $private:relay) { $private:relay.Dispose() } }
