function Add-FrontierDiagnosticProbeGuards([string]$Definition) {
    foreach($stage in @('Capture','Closed')) {
        $anchor=if($stage -ceq 'Capture') {'# Only an admitted bounded private context retains these array references.'}
            else {'try { $private:nativePrimaryCustody.Dispose() } catch { }'}
        $probe=@'
try { . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierDiagnosticPrimaryProbe.ps1') -Stage STAGE }
catch {
    if($null -eq $script:diagnosticProbeError) {$script:diagnosticProbeError=$_}
    [Console]::Error.WriteLine('diagnostic-primary-probe-error; stage=STAGE; reason='+$_.Exception.Message)
}
'@.Replace('STAGE',$stage)
        $replacement=if($stage -ceq 'Capture') {$probe+"`n"+$anchor} else {$anchor+"`n"+$probe}
        $Definition=Replace-AttemptSeam $Definition $anchor $replacement
    }
    # Observe the existing guarded call, without changing its inputs or branch.
    $anchor='$private:nativeWitness = Resolve-FileTransferNativeFrontierWitness'
    return (Replace-AttemptSeam $Definition $anchor ('$script:diagnosticResolverCalls++'+"`n"+$anchor))
}
