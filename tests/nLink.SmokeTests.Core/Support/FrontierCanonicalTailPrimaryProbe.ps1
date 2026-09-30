param([ValidateSet('Capture','BeforeResolve','AfterResolve','AfterOptional','Closed')][string]$Stage)
if($env:NLINK_TEST_PAYLOAD_PRIMARY_CASE -cne 'canonical-tail-late-publication' -or $env:NLINK_TEST_DIAGNOSTIC_CUSTODY) {
    throw 'Canonical tail probe requires its separate actual-pair profile'
}
if($Stage -ceq 'Closed' -and $null -ne $script:latePublicationObservation) {
    $script:latePublicationObservation.CanonicalTailProfile='canonical-tail-late-publication'
    $script:latePublicationObservation.SamePairJoined=$script:canonicalTailJoined
}
try {
    if($Stage -ceq 'BeforeResolve') {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierWitnessCallbackProfile.ps1')
        $definition=(Get-Command Resolve-FileTransferNativeFrontierWitness -CommandType Function).Definition
        $definition=Add-FrontierWitnessCallbackProfileForTest $definition
        Set-Item -Path Function:Resolve-FileTransferNativeFrontierWitness -Value ([scriptblock]::Create($definition))
    }
    if($Stage -ceq 'AfterResolve') {
        $diagnostic=[ordered]@{Frequency=[Diagnostics.Stopwatch]::Frequency}
        foreach($name in @('Stage','Reason','Calls','State','FullCount','CompactCount','WorkTicks','Visits','Stages','Callback')) {
            $field=$nativePrimaryCustody.GetType().GetField(('WitnessResolution'+$name+'ForTest'),[Reflection.BindingFlags]'Instance,NonPublic')
            if($null -ne $field) {$diagnostic[$name]=$field.GetValue($nativePrimaryCustody)}
        }
        [Console]::Error.WriteLine('canonical-witness-resolution; diagnostic_json='+($diagnostic | ConvertTo-Json -Depth 6 -Compress))
        if($null -ne $script:latePublicationObservation) {$script:latePublicationObservation.WitnessResolution=$diagnostic}
    }
    . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierLatePublicationPrimaryProbe.ps1') -Stage $Stage -CanonicalTail
    if($Stage -ceq 'Capture') {
        # This is the exact decoded object from the original native payload
        # capture above. Never reopen an artifact or synthesize a receipt.
        $script:canonicalTailReceipt=$lateReceipt
    } elseif($Stage -ceq 'AfterResolve') {
        if($null -eq $script:canonicalTailReceipt -or -not $script:latePublicationResolved -or
            $null -eq $nativePrimaryTransfer -or $script:canonicalTailJoined) {throw 'Original canonical receipt/pair missing or repeated'}
        if(-not $nativeWitness.SamePairMatched -or -not $nativePrimaryTransfer.IsCurrent -or
            $nativePrimaryTransfer.ByteCount -ne 268435456 -or
            $nativePrimaryTransfer.Sha256 -cne '3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699' -or
            $nativePrimaryTransfer.LiveProofEligible -or $nativePrimaryTransfer.TransferIntegrityEstablished -or
            $nativePrimaryTransfer.PublicationCompletionIssued) {throw 'Original production same-pair witness join unavailable'}
        $script:transferEvaluation=$nativePrimaryTransfer
        $script:primaryPayloadBound=$true
        $script:canonicalTailJoined=$true
        Write-Host 'canonical-production-pair-check-observed'
    } elseif($Stage -ceq 'AfterOptional') {
        if(-not $script:canonicalTailJoined -or $verdict -cne 'FAIL_PROTOCOL_OR_INTEGRITY' -or $gateStatus -cne 'fail') {
            throw 'Canonical tail witness changed the original failed route verdict or missed its join'
        }
    }
} finally {
    if($Stage -ceq 'Closed') {$script:canonicalTailReceipt=$null}
}
