param([string]$RepoRoot,[string]$Directory,[string]$Case)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$clone=[object].GetMethod('MemberwiseClone',$flags)
$root=Join-Path (Join-Path $Directory 'gui') 'valid'
[void][IO.Directory]::CreateDirectory($root)
$run='gui-writer-parent-run'; $scenario='gui-writer-parent-run:scenario:1'
$request=[NLink.Tools.LocalGuiWriterRequest]::TryCreateRelay([guid]::NewGuid(),$run,$scenario,$root)
if($null -eq $request) {throw 'Original relay creation failed'}
$name=$request.Name; $nativeName=$name+'.frontier-relay-v1'; $events=@{}
$registration=$null; $collision=$null; $collisionView=$null; $view=$null; $map=$null; $nativeView=$null; $nativeMap=$null; $process=$null
$stdout=$null; $stderr=$null
function Wait-RelayFixture([string]$Part) {
    if(-not $events[$Part].WaitOne(15000)) {
        if($process.HasExited) {throw ('Wrapper exited: '+$stdout.GetAwaiter().GetResult()+' / '+$stderr.GetAwaiter().GetResult())}
        throw ('Relay fixture deadline: '+$Part)
    }
}
try {
    foreach($type in @([NLink.Tools.LocalGuiWriterRequest+FrontierRelayRegistration],[NLink.Tools.LocalGuiWriterRequest+FrontierRelayHandoff])) {
        if($type.GetConstructors().Length -or @($type.GetProperties() | Where-Object {$null -ne $_.GetSetMethod()}).Count) {throw 'Public native relay capability factory/setter'}
    }
    foreach($part in @('legacy-go','legacy-attached','native-go','native-attached','review')) {
        $events[$part]=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$name+'.test.'+$part)
    }
    if($Case -ceq 'guards') {
        if($null -ne $request.TryRegisterFrontierRelay()) {throw 'Unbound relay registered'}
        $ordinary=[NLink.Tools.LocalGuiWriterRequest]::TryCreate([guid]::NewGuid(),$run,$scenario,$root)
        $self=[NLink.Tools.LocalGuiWriterRequest]::TryCreateRelay([guid]::NewGuid(),$run,$scenario,$root)
        try {
            if(-not $ordinary.BindWorker((Get-Process -Id $PID)) -or $null -ne $ordinary.TryRegisterFrontierRelay()) {throw 'GUI role registered as relay'}
            if(-not $self.BindWorker((Get-Process -Id $PID)) -or $null -ne $self.TryRegisterFrontierRelay()) {throw 'Parent registered itself as Wrapper'}
        } finally {$ordinary.Dispose(); $self.Dispose()}
    }
    $info=[Diagnostics.ProcessStartInfo]::new(); $info.FileName=(Get-Process -Id $PID).Path
    $arguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierRelayRegistrationWorker.ps1'),
        '-RepoRoot',$RepoRoot,'-Directory',$Directory,'-Case',$Case,'-Name',$name,'-RequestId',$request.RequestId.ToString('D'))
    $info.Arguments=($arguments | ForEach-Object {'"'+([string]$_).Replace('"','\"')+'"'}) -join ' '
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true; $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    $process=[Diagnostics.Process]::new(); $process.StartInfo=$info
    if(-not $process.Start()) {throw 'Fixture Wrapper startup failed'}
    $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
    if(-not $request.BindWorker($process)) {throw 'Original Wrapper bind failed'}
    if($Case -ceq 'guards') {
        $copy=$clone.Invoke($request,$null)
        if($null -ne $copy.TryRegisterFrontierRelay() -or $copy.BindWorker($process)) {throw 'Copied relay request consumed original'}
        $copy.Dispose()
    }
    if($Case -ceq 'map-collision') {
        $collision=[IO.MemoryMappedFiles.MemoryMappedFile]::CreateNew($nativeName,16480)
        $collisionView=$collision.CreateViewAccessor(); $collisionView.Write(0L,1234567)
    }
    if($Case -ceq 'event-collision') {$collision=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$nativeName+'.cancelled')}
    if($Case -cne 'missing') {$registration=$request.TryRegisterFrontierRelay()}
    $registered=$Case -cnotin @('missing','map-collision','event-collision')
    if(($null -ne $registration) -ne $registered) {throw 'Registration existence mismatch'}
    if($registered) {
        if(-not $registration.IsCurrent -or $registration.WrapperAttached -or $registration.NativeWriterCustodyEstablished -or
            $registration.LiveProofEligible -or $registration.PublicationCompletionIssued) {throw 'Registration acquired authority or lost initial scope'}
        $nativeMap=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($nativeName); $nativeView=$nativeMap.CreateViewAccessor()
        if($Case -ceq 'guards') {
            $copy=$clone.Invoke($registration,$null)
            if($copy.IsCurrent -or $copy.WrapperAttached) {throw 'Copied registration current'}
            $copy.Dispose()
            if($null -ne $request.TryRegisterFrontierRelay() -or -not $registration.IsCurrent) {throw 'Replay retired original registration'}
        }
    }
    $map=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($name); $view=$map.CreateViewAccessor()
    [void]$events['legacy-go'].Set(); Wait-RelayFixture 'legacy-attached'
    if($Case -ceq 'guards') {
        $scope=$registration.GetType().GetField('scope',$flags).GetValue($registration).Clone(); $scope[24]=$scope[24] -bxor 1
        $channel=[NLink.Tools.LocalGuiWriterRequest].GetNestedType('FrontierRelayRegistrationChannel',[Reflection.BindingFlags]::NonPublic)
        if($null -ne $channel.GetMethod('Open',[Reflection.BindingFlags]'Static,NonPublic').Invoke($null,@($name,$scope)) -or
            -not $registration.IsCurrent) {throw 'Rejected foreign open cancelled original'}
    }
    switch($Case) {
        'request-id' {$view.Write(24L,[byte]($view.ReadByte(24L) -bxor 1))}
        'scope-body' {$view.Write(64L,[byte]($view.ReadByte(64L) -bxor 1))}
        'wrapper-incarnation' {$view.Write(40L,$view.ReadInt64(40L)+1L)}
        'parent-incarnation' {$view.Write(8L,$view.ReadInt64(8L)+1L)}
        'version' {$nativeView.Write(4L,2)}
        'padding' {$nativeView.Write(16479L,[byte]1)}
        'nonce' {for($i=16;$i -lt 32;$i++) {$nativeView.Write([long]$i,[byte]0)}}
        'channel-state' {$nativeView.Write(8L,2)}
    }
    [void]$events['native-go'].Set(); Wait-RelayFixture 'native-attached'
    $admitted=$registered -and $Case -cnotin @('request-id','scope-body','wrapper-incarnation','parent-incarnation','version','padding','nonce','channel-state')
    $current=$admitted -and $Case -cnotin @('relay-dispose','handoff-dispose','peer-exit')
    if($Case -ceq 'peer-exit' -and -not $process.WaitForExit(15000)) {throw 'Abrupt Wrapper exit missing'}
    if($null -ne $registration -and ($registration.WrapperAttached -ne $current -or $registration.IsCurrent -ne $current)) {throw 'Native registration/peer observation mismatch'}
    switch($Case) {
        'request-dispose' {$request.Dispose()}
        'registration-dispose' {$registration.Dispose()}
        'legacy-after-dispose' {$registration.Dispose()}
        'early-take' {if($null -ne $request.TakeRelayAfterExit()) {throw 'Running Wrapper adopted'}}
        'channel-regression' {$nativeView.Write(8L,0)}
        'legacy-regression' {$view.Write(48L,0)}
    }
    if($Case -cin @('request-dispose','registration-dispose','legacy-after-dispose','early-take','channel-regression','legacy-regression') -and $registration.IsCurrent) {throw 'Retired/regressed native registration still current'}
    [void]$events['review'].Set()
    if(-not $process.WaitForExit(15000)) {throw 'Wrapper completion deadline'}
    $output=$stdout.GetAwaiter().GetResult(); $errorText=$stderr.GetAwaiter().GetResult()
    if($process.ExitCode -ne 0) {throw ('Wrapper failed: '+$output+' / '+$errorText)}
    if($Case -cne 'peer-exit' -and ($output.Trim() -cne 'native-relay-wrapper-ok' -or $errorText.Trim() -cne 'native-relay-wrapper-stderr')) {throw 'Original output changed'}
    $completion=$request.TakeRelayAfterExit()
    if(($null -ne $completion) -ne ($Case -cin @('legacy-forward','legacy-after-dispose'))) {throw 'Native registration changed legacy completion'}
    if($null -ne $completion -and ($completion.WrapperProcessId -ne $process.Id -or $completion.WriterContext.ArtifactRoot -cne $root)) {throw 'Legacy process/scope changed'}
    if($null -ne $registration -and ($registration.IsCurrent -or $registration.WrapperAttached)) {throw 'Native registration survived Wrapper exit/take'}
    if($Case -ceq 'map-collision' -and $collisionView.ReadInt32(0L) -ne 1234567) {throw 'Foreign mapping overwritten'}
    if($Case -ceq 'event-collision' -and $collision.WaitOne(0)) {throw 'Foreign cancellation event signalled'}
    foreach($field in @('frontierParentRegistration','frontierParentCustody')) {
        if($null -ne $request.GetType().GetField($field,$flags).GetValue($request)) {throw 'Relay registration minted GUI custody'}
    }
    'frontier-relay-registration='+$Case
} finally {
    if($null -ne $process) {try {if(-not $process.HasExited) {$process.Kill(); [void]$process.WaitForExit(5000)}} finally {$process.Dispose()}}
    $request.Dispose(); if($null -ne $registration) {$registration.Dispose()}
    foreach($resource in @($nativeView,$nativeMap,$view,$map,$collisionView,$collision)) {if($null -ne $resource) {$resource.Dispose()}}
    foreach($event in $events.Values) {$event.Dispose()}
}
$leaked=$null
try {$leaked=[Threading.EventWaitHandle]::OpenExisting($nativeName+'.cancelled')} catch [Threading.WaitHandleCannotBeOpenedException] { }
if($null -ne $leaked) {$leaked.Dispose(); throw 'Native relay registration event leaked'}
$leakedMap=$null
try {$leakedMap=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($nativeName)} catch [IO.FileNotFoundException] { }
if($null -ne $leakedMap) {$leakedMap.Dispose(); throw 'Native relay registration mapping leaked'}
