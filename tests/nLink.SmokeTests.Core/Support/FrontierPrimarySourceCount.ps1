. (Join-Path $RepoRoot 'tools/FileTransferSoak/RetainedAnalysisEvidence.ps1')
$countCase=$primaryCase.Substring('count-'.Length)
$countLease=$hashRead.Lease; $countPath=$completion.Outputs[3].Path
$hashPosition=19L; [PrimaryPositionProbe]::Set($custodyPins[4],$hashPosition)
$countRejected=$false; $ordinary=$null
try {
    switch($countCase) {
        'copied-lease' {$countLease=$parentClone.Invoke($countLease,$null)}
        'wrong-path' {$countPath=$completion.Outputs[0].Path}
        'disposed-lease' {$countLease.Dispose()}
        'closed-pin' {$countLease.GetType().GetField('anchor',$parentFlags).GetValue($countLease).SafeFileHandle.Dispose()}
        'no-native' {
            $path=Join-Path $Directory 'ordinary-count-source.log'; [IO.File]::WriteAllText($path,'ordinary')
            $ordinary=[NLink.Tools.LocalInputReadLease]::HashAndHold($path); $countLease=$ordinary.Lease; $countPath=$path
        }
        'metadata' {$countLease.GetType().GetField('expectedLines',$parentFlags).SetValue($countLease,99999L)}
    }
    try {$primarySourceLines=Get-FileTransferEvidenceLineCount -Path $countPath -ReadLease $countLease}
    catch {$countRejected=$true}
    $invalid=$countCase -cin @('copied-lease','wrong-path','disposed-lease','closed-pin','no-native')
    if($countRejected -ne $invalid) {throw ('Native count rejection mismatch: '+$countCase)}
    if($countCase -cin @('copied-lease','wrong-path','no-native')) {
        if(-not $hashRead.Lease.IsVerified -or -not $custody.IsCurrent) {throw 'Rejected count retired original custody'}
        $primarySourceLines=Get-FileTransferEvidenceLineCount -Path $completion.Outputs[3].Path -ReadLease $hashRead.Lease
    }
    if(-not $invalid -or $countCase -cin @('copied-lease','wrong-path','no-native')) {
        if($primarySourceLines -ne $completion.Outputs[3].LineCount -or
            [PrimaryPositionProbe]::Position($custodyPins[4]) -ne $hashPosition) {throw 'Native count used metadata or moved source position'}
        if($countCase -ceq 'replay') {
            $rejected=$false
            try {$null=Get-FileTransferEvidenceLineCount -Path $countPath -ReadLease $countLease} catch {$rejected=$true}
            if(-not $rejected -or -not $hashRead.Lease.IsVerified -or -not $custody.IsCurrent) {throw 'Count replay scanned or retired source'}
        }
    }
    'frontier-primary-source-count='+$countCase
} finally {if($null -ne $ordinary) {$ordinary.Lease.Dispose()}}
