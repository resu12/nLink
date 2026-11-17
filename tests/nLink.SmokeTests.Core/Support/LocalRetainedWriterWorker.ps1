param([string]$RepoRoot,[string]$SettingsPath)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1')
$settings=[IO.File]::ReadAllText($SettingsPath) | ConvertFrom-Json
$mode=[string]$settings.mode; $owner=$null; $source=$null; $writer=$null; $iterator=$null
$result=@{attached=$false;sourceComplete=$false;writerContext=$false;writerOpened=$false;writerVerified=$false;writerLines=0L;guiRequest='';retainedRequest='';verifiedAfterDispose=$false}
try {
    $id=if($mode -ceq 'wrong-request') { [guid]::NewGuid() } else { [guid]$env:NLINK_RETAINED_SOURCE_REQUEST_ID }
    $owner=[NLink.Tools.LocalRetainedSourceRequest]::TryAttach($env:NLINK_RETAINED_SOURCE_REQUEST,$id,$settings.path,
        $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID,$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID)
    if($null -ne $owner) {
        $result.attached=$true; $result.writerContext=$null -ne $owner.WriterContext; $result.retainedRequest=$owner.RequestId.ToString('D')
        if($result.writerContext) { $result.guiRequest=$owner.WriterContext.RequestId.ToString('D') }
        if($mode -ceq 'early-writer' -and $null -ne $owner.TryOpenWriter()) { throw 'Writer opened before source EOF' }
        $source=$owner.TryOpenSource()
        if($null -eq $source) { throw 'Source reader missing' }
        $iterator=([Collections.IEnumerable]$source.ReadLines()).GetEnumerator()
        while($iterator.MoveNext()) { if($mode -cin @('partial-source','source-no-eof')) { break } }
        $iterator.Dispose(); $iterator=$null
        $result.sourceComplete=$source.InputReadComplete
        if($mode -ceq 'source-disposed') { $source.Dispose() }
        $writer=$owner.TryOpenWriter()
        if($null -ne $writer) {
            $result.writerOpened=$true
            if($writer.IsVerified -or $null -ne $owner.TryOpenWriter()) { throw 'Candidate verified early or opened twice' }
            try {
                $iterator=([Collections.IEnumerable]$writer.VerifyAndReadLines()).GetEnumerator()
                while($iterator.MoveNext()) {
                    if($mode -ceq 'partial-writer') { break }
                    if($mode -ceq 'cancel-writer') {
                        $closed=[Threading.EventWaitHandle]::OpenExisting($env:NLINK_RETAINED_SOURCE_REQUEST+'.closed')
                        try { [void]$closed.Set() } finally { $closed.Dispose() }
                    }
                    if($mode -ceq 'source-disposed-during-writer') { $source.Dispose() }
                    if($mode -ceq 'budget') {
                        # Fault the exact revised clock seam; no throughput or
                        # stale 300-ms assumption in this negative control.
                        $lease=$writer.GetType().GetField('lease',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($writer)
                        $clock=$lease.GetType().GetField('verificationClock',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($lease)
                        $clock.Stop()
                        $elapsed=@($clock.GetType().GetFields([Reflection.BindingFlags]'Instance,NonPublic') | Where-Object Name -In @('elapsed','_elapsed'))
                        if($elapsed.Count -ne 1) {throw 'Writer test clock shape changed'}
                        $elapsed[0].SetValue($clock,[long]([Diagnostics.Stopwatch]::Frequency*([NLink.Tools.LocalInputReadLease]::MaximumVerificationMilliseconds+1)/1000))
                    }
                }
            } catch { $result.verificationFailure=$_.Exception.Message }
            finally { if($null -ne $iterator) { $iterator.Dispose(); $iterator=$null } }
            $result.writerVerified=$writer.IsVerified; $result.writerLines=$writer.LinesRead
            if($mode -ceq 'budget' -and $result.verificationFailure -notmatch 'input_verification_time_limit') {throw 'Writer budget failure was not observed'}
        }
    }
} finally {
    if($null -ne $iterator) { $iterator.Dispose() }
    if($null -ne $owner) { $owner.Dispose() }
    if($null -ne $writer) { $result.verifiedAfterDispose=$writer.IsVerified; $writer.Dispose() }
    if($null -ne $source) { $source.Dispose() }
}
[IO.File]::WriteAllText($settings.result,($result | ConvertTo-Json -Compress))
