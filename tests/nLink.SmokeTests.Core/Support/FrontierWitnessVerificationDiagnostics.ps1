function Test-LateWitnessRejectedForTest([string]$Check,$Row,[scriptblock]$Condition,
    $nativePrimaryCustody,$nativePrimaryFull,$nativePrimaryCompact,$nativePrimaryPair) {
    # The analyzer's private variables are not visible in nested function scopes.
    # Explicit parameters also keep the original predicates bound to those values.
    $sample=$null; $diagnosticInputs=$null; $diagnosticWatch=$null
    try {
        if($null -ne $script:latePublicationObservation -and -not $script:latePublicationObservation.Contains('WitnessVerification')) {
            # Scalars only; rows may be cleared by the original check's retirement.
            $sample=[ordered]@{Check=$Check;FullIndex=0;CompactIndex=0;SourceLine=0;
                Frequency=[Diagnostics.Stopwatch]::Frequency;BeforeVisits=$null;BeforeWorkTicks=$null;BeforeClosed=$null}
            if($null -ne $Row) {$sample.FullIndex=$Row.FullIndex; $sample.CompactIndex=$Row.CompactIndex; $sample.SourceLine=$Row.SourceLine}
            $diagnosticInputs=Read-LatePublicationField $nativePrimaryCustody 'inputs'
            $diagnosticWatch=Read-LatePublicationField $diagnosticInputs 'work'
            $sample.BeforeVisits=Read-LatePublicationField $diagnosticInputs 'evaluationVisits'
            $sample.BeforeWorkTicks=$diagnosticWatch.ElapsedTicks
            $sample.BeforeClosed=Read-LatePublicationField $diagnosticInputs 'closed'
        }
    } catch {} # Missing/broken instrumentation cannot replace the original result.
    $rejected=$false; $threw=$true
    try {
        $rejected=[bool](& $Condition)
        $threw=$false
        return $rejected
    } finally {
        try {
            if($null -ne $sample) {
                $sample.Rejected=$rejected; $sample.Threw=$threw
                $sample.AfterVisits=$null; $sample.AfterWorkTicks=$null; $sample.AfterClosed=$null
                $sample.CloseCaller=$null
                if($null -ne $diagnosticInputs -and $null -ne $diagnosticWatch) {
                    $sample.AfterVisits=Read-LatePublicationField $diagnosticInputs 'evaluationVisits'
                    $sample.AfterWorkTicks=$diagnosticWatch.ElapsedTicks
                    $sample.AfterClosed=Read-LatePublicationField $diagnosticInputs 'closed'
                    $sample.CloseCaller=Read-LatePublicationField $diagnosticInputs 'PrimarySourceCloseForTest'
                }
                # A rejected origin lookup can clear SourceLine before the original
                # comparison reads it. Preserve that first closure even if 0 == 0.
                if($rejected -or $threw -or ($sample.BeforeClosed -ceq $false -and $sample.AfterClosed -ceq $true)) {
                    $script:latePublicationObservation.WitnessVerification=$sample
                    [Console]::Error.WriteLine('late-witness-verification; diagnostic_json='+($sample | ConvertTo-Json -Compress))
                }
            }
        } catch {} # Never call currentness, reset a meter, or affect cleanup.
    }
}
