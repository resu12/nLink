Set-StrictMode -Version Latest

# Fixed reporting text only. No evidence lookup or recovery/authority decision.
function Get-FileTransferRejectedIngressExplanation {
    param($Recovered, $RejectionChainMatched, $ReceiveDisposition, $Reason, $Variant)

    if ($Recovered -isnot [bool] -or $Recovered -or
        $RejectionChainMatched -isnot [bool] -or -not $RejectionChainMatched -or
        $ReceiveDisposition -isnot [string] -or $ReceiveDisposition -cne 'rejected_before_dispatch' -or
        $Reason -isnot [string] -or $Variant -isnot [string]) { return $null }

    $kind = if ($Reason -ceq 'missing_tuna_resend' -and
        $Variant -ceq 'second_tuna_rejected_regular_ingress_exact_tuna_resend_missing') { 'send' }
    elseif ($Reason -ceq 'missing_exact_tuna_receive' -and
        $Variant -ceq 'second_tuna_rejected_regular_ingress_exact_tuna_receive_missing') { 'receive' }
    else { return $null }

    return "The rejection chain matched, but the required pre-rejection exact Tuna $kind was not found. Later fallback delivery, if present, is a separate outcome and does not satisfy this recovery contract. Stale safety and durable delivery are not established by this diagnostic."
}
