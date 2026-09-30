param([string]$RepoRoot,[string]$Directory,[string]$Case)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$type=[NLink.Tools.LocalGuiWriterRequest]
if($null -eq $type.GetMethod('TryBindCanonicalAttempt')) {throw 'Production canonical attempt binder missing'}
if($null -ne $type.GetMethod('TryBindAttemptForTest')) {throw 'Test-only API leaked'}
$hostPath=(Get-Process -Id $PID).Path
$run='canonical-attempt-controls'
$scope=$run+':regular-v4-live-activation-off-on-off-256mb:1'
$arguments=[string[]]@('-ExePath',$hostPath,'-WalletPath','private-wallet-path','-WalletPassword','private-wallet-password',
    '-SidecarPath','private-sidecar-path','-PayerMode','helpee','-RouteMode','live-regular-activation-cycle',
    '-Fault','switch-off','-Direction','helpee-to-helper','-PayloadSize','256MiB','-ArtifactDir',$Directory,
    '-TimeoutSeconds','100','-ProgressTimeoutSeconds','30','-RetainedAnalysisOwner','RouteAcceptance',
    '-LiveToggleSequence','on,off,on,off')
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
function New-Request([string]$Scenario=$scope) {
    [NLink.Tools.LocalGuiWriterRequest]::TryCreateRelay([guid]::NewGuid(),$run,$Scenario,$Directory)
}
function Clone($Object) {[object].GetMethod('MemberwiseClone',$flags).Invoke($Object,$null)}
$request=New-Request
try {
    if($null -eq $request) {throw 'Original request missing'}
    foreach($pair in @(@('-ExePath','relative.exe'),@('-PayerMode','helper'),@('-RouteMode','preactivated'),
        @('-Fault','none'),@('-Direction','helper-to-helpee'),@('-PayloadSize','128MiB'),
        @('-ArtifactDir',(Join-Path $Directory 'foreign')),@('-TimeoutSeconds','0'),
        @('-ProgressTimeoutSeconds','101'),@('-RetainedAnalysisOwner','Wrapper'),@('-LiveToggleSequence','off,on'))) {
        $changed=[string[]]$arguments.Clone()
        $changed[[array]::IndexOf($changed,$pair[0])+1]=$pair[1]
        if($request.TryBindCanonicalAttempt(1,$changed,'RegularActivationCycle',$hostPath)) {throw ('Changed argument admitted: '+$pair[0])}
    }
    foreach($changed in @(
        [pscustomobject]@{Values=[string[]]($arguments+@('-PayloadSize','256MiB'))},
        [pscustomobject]@{Values=[string[]]($arguments[2..($arguments.Length-1)])},
        [pscustomobject]@{Values=[string[]]($arguments+@('-unpaired'))},
        [pscustomobject]@{Values=[string[]]($arguments+@('-Unexpected','value'))})) {
        if($request.TryBindCanonicalAttempt(1,$changed.Values,'RegularActivationCycle',$hostPath)) {throw 'Malformed launch arguments admitted'}
    }
    foreach($key in @('-pAyLoAdSiZe','-Unexpected')) {
        $changed=[string[]]$arguments.Clone(); $changed[2]=$key
        if($request.TryBindCanonicalAttempt(1,$changed,'RegularActivationCycle',$hostPath)) {throw 'Same-length duplicate/unknown argument admitted'}
    }
    if($request.TryBindCanonicalAttempt(2,$arguments,'RegularActivationCycle',$hostPath) -or
        $request.TryBindCanonicalAttempt(1,$arguments,'None',$hostPath) -or
        $request.TryBindCanonicalAttempt(1,$arguments,'RegularActivationCycle','relative-host')) {throw 'Changed attempt/proof/host admitted'}
    $copy=Clone $request
    if($copy.TryBindCanonicalAttempt(1,$arguments,'RegularActivationCycle',$hostPath)) {throw 'Copied request admitted'}
    $copy.Dispose()
    $view=$type.GetField('view',$flags).GetValue($request)
    $before=[byte[]]::new(8256); [void]$view.ReadArray(0,$before,0,$before.Length)
    if(-not $request.TryBindCanonicalAttempt(1,$arguments,'RegularActivationCycle',$hostPath) -or
        $request.TryBindCanonicalAttempt(1,$arguments,'RegularActivationCycle',$hostPath)) {throw 'Original binding missing/repeated'}
    $after=[byte[]]::new($before.Length); [void]$view.ReadArray(0,$after,0,$after.Length)
    if([Convert]::ToBase64String($before) -cne [Convert]::ToBase64String($after)) {throw 'Original header/registration changed'}
    $all=[byte[]]::new(81984); [void]$view.ReadArray(0,$all,0,$all.Length)
    $text=[Text.Encoding]::UTF8.GetString($all)
    foreach($secret in @('private-wallet-path','private-wallet-password','private-sidecar-path')) {
        if($text.Contains($secret)) {throw 'Credential/unneeded path retained'}
    }
    if(@($all[16448..($all.Length-1)] | Where-Object {$_ -ne 0}).Count) {throw 'Completion area changed'}
} finally {if($null -ne $request) {$request.Dispose()}}
foreach($kind in @('foreign-scope','ordinary','retired','launched')) {
    $request=if($kind -ceq 'ordinary') {$type::TryCreate([guid]::NewGuid(),$run,$scope,$Directory)}
        elseif($kind -ceq 'foreign-scope') {New-Request ($run+':another:1')} else {New-Request}
    try {
        if($kind -ceq 'retired') {$request.Dispose()}
        if($kind -ceq 'launched') {
            $process=Get-Process -Id $PID
            try {if(-not $request.BindWorker($process)) {throw 'Existing worker binding unavailable'}} finally {$process.Dispose()}
        }
        if($request.TryBindCanonicalAttempt(1,$arguments,'RegularActivationCycle',$hostPath)) {throw ('Unavailable request admitted: '+$kind)}
    } finally {$request.Dispose()}
}
'canonical-attempt-binding='+$Case
