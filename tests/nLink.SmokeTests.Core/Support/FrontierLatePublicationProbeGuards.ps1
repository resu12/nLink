function Add-FrontierLatePublicationProbeGuards([string]$Definition,[switch]$CanonicalTail) {
    foreach($stage in @('Capture','BeforeResolve','AfterResolve','AfterOptional','Closed')) {
        $anchor=switch($stage) {
            'Capture' {'# Only an admitted bounded private context retains these array references.'}
            'BeforeResolve' {'$private:nativeWitness = Resolve-FileTransferNativeFrontierWitness'}
            'AfterResolve' {
                $tokens=$null; $errors=$null
                $ast=[Management.Automation.Language.Parser]::ParseInput($Definition,[ref]$tokens,[ref]$errors)
                $calls=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.CommandAst] -and
                    $node.GetCommandName() -ceq 'Resolve-FileTransferNativeFrontierWitness'},$true))
                if($errors.Count -or $calls.Count -ne 1) {throw 'Original optional resolver command changed'}
                if(@($calls[0].CommandElements | Where-Object {$_ -is [Management.Automation.Language.CommandParameterAst] -and
                    $_.ParameterName -ieq 'OmissionReason'}).Count) {throw 'Original optional resolver already captures omission reason'}
                $calls[0].Extent.Text
            }
            'AfterOptional' {'if ($localFailureWitnessOutputEnabled) {'}
            'Closed' {'try { $private:nativePrimaryCustody.Dispose() } catch { }'}
        }
        $probe=@'
try { . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierLatePublicationPrimaryProbe.ps1') -Stage STAGE }
catch {
    if($null -eq $script:latePublicationProbeError) {$script:latePublicationProbeError=$_}
    [Console]::Error.WriteLine('late-publication-primary-probe-error; stage=STAGE; reason='+$_.Exception.Message)
}
'@.Replace('STAGE',$stage)
        if($CanonicalTail) {$probe=$probe.Replace('FrontierLatePublicationPrimaryProbe.ps1','FrontierCanonicalTailPrimaryProbe.ps1')}
        $replacement=if($stage -cin @('AfterResolve','Closed')) {$anchor+"`n"+$probe} else {$probe+"`n"+$anchor}
        if($stage -ceq 'AfterResolve') {
            $replacement=$anchor+' -OmissionReason ([ref]$script:latePublicationOmissionReason)'+"`n"+$probe
        }
        $Definition=Replace-AttemptSeam $Definition $anchor $replacement
    }
    return $Definition
}
