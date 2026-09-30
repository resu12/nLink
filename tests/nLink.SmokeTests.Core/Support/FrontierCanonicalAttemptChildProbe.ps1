param([string]$ExePath,[string]$WalletPath,[string]$WalletPassword,[string]$SidecarPath,[string]$PayerMode,
    [string]$RouteMode,[string]$Fault,[string]$Direction,[string]$PayloadSize,[string]$ArtifactDir,
    [int]$TimeoutSeconds,[int]$ProgressTimeoutSeconds,[string]$RetainedAnalysisOwner,[string]$LiveToggleSequence)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$RepoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$type=[NLink.Tools.LocalGuiWriterRequest]
$relay=$type::TryAttachRelayReady($env:NLINK_GUI_WRITER_RELAY,[guid]$env:NLINK_GUI_WRITER_RELAY_ID,
    $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID,$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID,$ArtifactDir)
if($null -eq $relay) {throw 'Original child relay missing'}
try {
    $flags=[Reflection.BindingFlags]'Instance,NonPublic'
    $owner=$relay.GetType().GetField('owner',$flags).GetValue($relay)
    $view=$owner.GetType().GetField('view',$flags).GetValue($owner)
    $magic=$view.ReadInt32(8256)
    $bound=$magic -ne 0
    if($bound) {
        if($magic -ne 0x4e415431) {throw 'Unexpected binding format'}
        $bytes=[byte[]]::new($view.ReadInt32(8260)); [void]$view.ReadArray(8264,$bytes,0,$bytes.Length)
        $stream=[IO.MemoryStream]::new($bytes,$false); $reader=[IO.BinaryReader]::new($stream,[Text.UTF8Encoding]::new($false,$true))
        try {
            if([guid]::new($reader.ReadBytes(16)) -ne [guid]$env:NLINK_GUI_WRITER_RELAY_ID -or
                $reader.ReadString() -cne $env:NLINK_GUI_WRITER_RELAY -or
                $reader.ReadInt32() -ne $view.ReadInt32(4) -or $reader.ReadInt64() -ne $view.ReadInt64(8) -or
                $reader.ReadInt32() -ne 1) {throw 'Binding lost original parent/request'}
            $expected=@($ExePath,$Direction,$PayerMode,$Fault,$RouteMode,$PayloadSize,$LiveToggleSequence,
                [string]$TimeoutSeconds,[string]$ProgressTimeoutSeconds,'RegularActivationCycle',
                'measured_live_regular_activation_cycle',(Get-Process -Id $PID).Path)
            foreach($value in $expected) {if($reader.ReadString() -cne $value) {throw 'Bound projection differs from actual child arguments/host'}}
            if($stream.Position -ne $stream.Length) {throw 'Extra bound fields'}
        } finally {$reader.Dispose();$stream.Dispose()}
    }
    $testApi=$null -ne $type.GetMethod('TryBindAttemptForTest')
    [ordered]@{Bound=$bound;TestApiPresent=$testApi;ChildPid=$PID} | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $ArtifactDir 'canonical-attempt-child.json') -Encoding UTF8
    'canonical-attempt-child-checked'
} finally {$relay.Dispose()}

