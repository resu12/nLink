# Synthetic local protocol peer under the original helper process identity.
# This tests GUI custody, not Core finalization or end-to-end transfer success.
$destinationCase=$env:NLINK_TEST_GUI_DESTINATION_CASE
if($destinationCase -cin @('valid','slow','reject') -or $destinationCase.StartsWith('connected-')) {
    $send=[Threading.EventWaitHandle]::OpenExisting($Signal+'.destination.send')
    $done=[Threading.EventWaitHandle]::OpenExisting($Signal+'.destination.done')
    $stalled=[Threading.EventWaitHandle]::OpenExisting($Signal+'.destination.stalled')
    $pipe=$null; $hold=$null; $deadline=$null
    try {
        if(-not $send.WaitOne(95000)) {throw 'Synthetic receiver scheduling deadline'}
        . (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
        $pipe=[IO.Pipes.NamedPipeClientStream]::new('.', 'nlink-destination-'+([guid]$env:NLINK_APP_FRONTIER_REQUEST_ID).ToString('N'),[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous)
        $deadline=[Threading.CancellationTokenSource]::new(10000)
        $pipe.ConnectAsync($deadline.Token).GetAwaiter().GetResult()
        if($destinationCase -ceq 'reject') {
            $pipe.Write([byte[]]@(0,0,0,0),0,4)
        } elseif($destinationCase.StartsWith('connected-header-')) {
            $pipe.Write([byte[]]@(80,0),0,2)
        } elseif($destinationCase.StartsWith('connected-body-')) {
            $pipe.Write([BitConverter]::GetBytes([int]80),0,4)
            $pipe.WriteByte(1)
        } else {
            $path=Join-Path $Directory 'synthetic-destination.bin'
            [IO.File]::Copy((Join-Path $Directory 'filetransfer-tuna-gui-payload.bin'),$path)
            $hold=[NLink.Tools.LocalInputReadLease]::HashAndHold($path).Lease
            $stream=$hold.GetType().GetField('anchor',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($hold)
            $hash=New-Object byte[] 32
            for($i=0;$i -lt 32;$i++) {$hash[$i]=[Convert]::ToByte($hold.ExpectedSha256.Substring($i*2,2),16)}
            $nonce=[guid]::NewGuid().ToByteArray()
            $body=[IO.MemoryStream]::new(); $writer=[IO.BinaryWriter]::new($body)
            try {
                $writer.Write([int]0x4e444731); $writer.Write([byte[]]$nonce)
                $writer.Write(([guid]$env:NLINK_APP_FRONTIER_REQUEST_ID).ToByteArray()); $writer.Write([int]$PID)
                $writer.Write([long](Get-Process -Id $PID).StartTime.ToUniversalTime().ToFileTimeUtc())
                $writer.Write([long]$stream.SafeFileHandle.DangerousGetHandle().ToInt64())
                $writer.Write([string]$hold.FileIdentity); $writer.Write([long]$hold.ByteCount)
                $writer.Write('synthetic-session'); $writer.Write('synthetic-transfer'); $writer.Write([Convert]::ToBase64String($hash))
                $packet=$body.ToArray()
            } finally {$writer.Dispose(); $body.Dispose()}
            $header=[BitConverter]::GetBytes([int]$packet.Length)
            $pipe.Write($header,0,4); $pipe.Write($packet,0,$packet.Length)
            $ack=New-Object byte[] 16; $offset=0
            while($offset -lt 16) {
                $read=$pipe.ReadAsync($ack,$offset,16-$offset,$deadline.Token).GetAwaiter().GetResult()
                if($read -eq 0) {throw 'GUI closed before acknowledgment'}
                $offset+=$read
            }
            if([Convert]::ToBase64String($ack) -cne [Convert]::ToBase64String($nonce)) {throw 'GUI nonce changed'}
            if(-not $destinationCase.StartsWith('connected-commit-')) {$pipe.Write($nonce,0,16)}
        }
        if($destinationCase.StartsWith('connected-')) {
            [void]$stalled.Set()
            # No peer cancellation token: only server closure may end the stall.
            # The long watchdog fails the fixture, never supplies a passing EOF.
            $read=$pipe.ReadAsync((New-Object byte[] 1),0,1)
            try {
                if(-not $read.Wait(45000)) {throw 'Server did not close stalled destination pipe'}
                if($read.GetAwaiter().GetResult() -ne 0) {throw 'Unexpected data after destination stall'}
            } catch {
                if($_.Exception.GetBaseException() -isnot [IO.IOException]) {throw}
            }
            if($null -ne $hold) {$hold.Dispose(); $hold=$null}
        }
        [void]$done.Set()
    } finally {
        if($null -ne $pipe) {$pipe.Dispose()}
        if($null -ne $hold) {$hold.Dispose()}
        if($null -ne $deadline) {$deadline.Dispose()}
        $send.Dispose(); $done.Dispose(); $stalled.Dispose()
    }
}
