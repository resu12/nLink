namespace NLink.SmokeTests;

public sealed class FrontierCanonicalTailCompositionTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("pwsh")] [InlineData("powershell")]
    public async Task CombinedProjectionPreservesOriginalCallsOrderAndFailureCleanup(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop';Set-StrictMode -Version Latest
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchProjection.ps1')
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadPrimaryGuards.ps1')
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierLatePublicationProbeGuards.ps1')
            $ast=Read-AttemptAst (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierCanonicalTailPrimaryWorker.ps1')
            Invoke-Expression (Get-AttemptFunction $ast 'Get-CanonicalTailPrimaryWorkerForTest')
            Invoke-Expression (Get-AttemptFunction $ast 'Clear-CanonicalTailPrimaryReferencesForTest')
            $worker=Get-CanonicalTailPrimaryWorkerForTest $RepoRoot
            $tokens=$null;$errors=$null;$null=[Management.Automation.Language.Parser]::ParseInput($worker,[ref]$tokens,[ref]$errors)
            if($errors.Count) {throw 'Combined worker parse failed'}
            if([regex]::Matches($worker,'Add-AttemptPayloadPrimaryProbeGuards \$definition').Count -ne 1 -or
                [regex]::Matches($worker,'Add-FrontierLatePublicationProbeGuards \$definition -CanonicalTail').Count -ne 1 -or
                -not $worker.Contains('FrontierAttemptDispatchLoader.ps1')) {throw 'Original attempt worker/probes changed'}
            $original=Get-AttemptFunction (Read-AttemptAst (Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1')) 'Write-FileTransferCompactRetainedAnalysisArtifacts'
            $guarded=Add-FrontierLatePublicationProbeGuards (Add-AttemptPayloadPrimaryProbeGuards $original) -CanonicalTail
            $null=[Management.Automation.Language.Parser]::ParseInput($guarded,[ref]$tokens,[ref]$errors)
            if($errors.Count) {throw 'Combined analyzer parse failed'}
            foreach($operation in @('TryCapturePayloads','Get-FileTransferCoordinatorSnapshotEvaluation','Resolve-FileTransferNativeFrontierWitness',
                'TryPrepareDiagnostic','TryOfferOutput','TryCommitOutputWhenAdopted')) {
                if([regex]::Matches($guarded,[regex]::Escape($operation)).Count -ne [regex]::Matches($original,[regex]::Escape($operation)).Count) {throw ('Duplicated original operation: '+$operation)}
            }
            if([regex]::Matches($guarded,'-OmissionReason').Count -ne 1) {throw 'Resolver omission capture changed'}
            $template=@'
            function Invoke-OriginalForTest {
                $private:nativePrimaryCustody=$script:owner
                $localFailureWitnessOutputEnabled=$false
                try {
                    # Only an admitted bounded private context retains these array references.
                    if($fault -ceq 'original') {throw 'original-failure'}
                    if($fault -cne 'ineligible') {
                        try {
                            $private:nativeWitness = Resolve-FileTransferNativeFrontierWitness
                            if($null -ne $private:nativeWitness) {$script:order.Add('Output')}
                        } catch { }
                    }
                    if ($localFailureWitnessOutputEnabled) {throw 'wrong mode'}
                } finally {
                    try { $private:nativePrimaryCustody.Dispose() } catch { }
                }
            }
            '@
            $text=Add-FrontierLatePublicationProbeGuards (Add-AttemptPayloadPrimaryProbeGuards $template) -CanonicalTail
            $text=$text.Replace(". (Join-Path `$RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadPrimaryProbe.ps1')",'Invoke-PairProbeForTest')
            $text=$text.Replace(". (Join-Path `$RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierCanonicalTailPrimaryProbe.ps1')",'Invoke-TailProbeForTest')
            function Invoke-PairProbeForTest($Stage) {$script:order.Add('Pair'+$Stage);if($fault -ceq ('Pair'+$Stage)) {throw $fault}}
            function Invoke-TailProbeForTest($Stage) {$script:order.Add($Stage);if($fault -ceq $Stage) {throw $fault}}
            function Resolve-FileTransferNativeFrontierWitness([ref]$OmissionReason) {
                $script:order.Add('Resolve');if($fault -ceq 'resolver') {throw 'resolver-refused'}
                if($fault -ceq 'omitted') {$OmissionReason.Value='original-omission';return $null}
                return 'original-witness'
            }
            $owner=[pscustomobject]@{};$owner | Add-Member ScriptMethod Dispose {$script:order.Add('Dispose')}
            $env:NLINK_TEST_PAYLOAD_PRIMARY_CASE='canonical-tail-late-publication'
            Invoke-Expression $text
            foreach($fault in @('valid','PairBind','Capture','BeforeResolve','AfterResolve','AfterOptional','Closed','PairClosed','resolver','omitted','ineligible','original')) {
                $script:order=[Collections.Generic.List[string]]::new();$script:primaryPayloadBindError=$null
                $script:latePublicationProbeError=$null;$script:latePublicationOmissionReason=$null
                $failure='';try {Invoke-OriginalForTest} catch {$failure=$_.Exception.Message}
                $expected=if($fault -ceq 'PairBind') {'PairBind,Dispose,Closed,PairClosed'}
                    elseif($fault -ceq 'original') {'PairBind,Capture,Dispose,Closed,PairClosed'}
                    elseif($fault -ceq 'ineligible') {'PairBind,Capture,AfterOptional,Dispose,Closed,PairClosed'}
                    elseif($fault -ceq 'resolver') {'PairBind,Capture,BeforeResolve,Resolve,AfterOptional,Dispose,Closed,PairClosed'}
                    elseif($fault -ceq 'omitted') {'PairBind,Capture,BeforeResolve,Resolve,AfterResolve,AfterOptional,Dispose,Closed,PairClosed'}
                    else {'PairBind,Capture,BeforeResolve,Resolve,AfterResolve,Output,AfterOptional,Dispose,Closed,PairClosed'}
                if(($script:order -join ',') -cne $expected) {throw ('Composition order: '+$fault+' / '+($script:order -join ','))}
                $expectedFailure=if($fault -cin @('PairBind','PairClosed')) {$fault} elseif($fault -ceq 'original') {'original-failure'} else {''}
                if($failure -cne $expectedFailure) {throw ('Original failure lost: '+$fault)}
                $tailError=if($null -eq $script:latePublicationProbeError) {''} else {$script:latePublicationProbeError.Exception.Message}
                $expectedError=if($fault -cin @('Capture','BeforeResolve','AfterResolve','AfterOptional','Closed')) {$fault} else {''}
                if($tailError -cne $expectedError) {throw ('Probe failure lost: '+$fault)}
            }
            foreach($name in @('canonicalTailReceipt','transferEvaluation','latePublicationPayload','latePublicationState','latePublicationFailure',
                'latePublicationFull','latePublicationCompact','latePublicationOmissionReason','latePublicationProbeError')) {Set-Variable -Scope Script -Name $name -Value ([object]::new())}
            foreach($name in @('workerPayloadHandles','latePublicationWitnesses','latePublicationPins','latePublicationCandidates')) {Set-Variable -Scope Script -Name $name -Value @([object]::new())}
            Clear-CanonicalTailPrimaryReferencesForTest
            if($null -ne $script:canonicalTailReceipt -or $null -ne $script:transferEvaluation -or $null -ne $script:latePublicationPayload -or
                $script:workerPayloadHandles.Count -or $script:latePublicationWitnesses.Count -or $script:latePublicationPins.Count) {throw 'Combined worker retained references'}
            'canonical-composition-order-controls=12'
            """, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("canonical-composition-order-controls=12", result.Stdout);
    }

    [Theory]
    [InlineData("pwsh")] [InlineData("powershell")]
    public async Task CombinedProbeUsesOriginalReceiptAndBatchAndClearsOnFailure(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop';Set-StrictMode -Version Latest
            $probe=[IO.File]::ReadAllText((Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierCanonicalTailPrimaryProbe.ps1'))
            # Replace only the independently tested late probe with synthetic stage data.
            $probe=$probe.Replace(". (Join-Path `$RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierLatePublicationPrimaryProbe.ps1')",@'
            . {param($Stage,[switch]$CanonicalTail)
                if(-not $CanonicalTail) {throw 'Tail shape flag missing'}
                if($Stage -ceq 'Capture') {$lateReceipt=$script:originalReceipt}
                if($Stage -ceq 'AfterResolve') {$script:latePublicationResolved=$true}
                if($Stage -ceq 'Closed' -and $fault -ceq 'closed-failure') {throw 'original-close-failure'}
            }
            '@)
            $script:originalReceipt=[pscustomobject]@{Source=[pscustomobject]@{SessionId='s';TransferId='t'};ProcessId=20;ProcessCreationFileTime=200L}
            $script:latePublicationPayload=[object]::new();$script:batch=[object]::new()
            $nativePrimaryCustody=[pscustomobject]@{}
            $env:NLINK_TEST_PAYLOAD_PRIMARY_CASE='canonical-tail-late-publication';$env:NLINK_TEST_DIAGNOSTIC_CUSTODY=''
            foreach($fault in @('valid','unmatched','retired','size','hash','authority','missing-receipt','unbound','repeated','verdict','gate','closed-failure')) {
                $script:canonicalTailReceipt=$null;$script:canonicalTailJoined=$false;$script:primaryPayloadBound=$false;$script:transferEvaluation=$null
                $script:latePublicationObservation=[ordered]@{}
                $nativeWitness=[pscustomobject]@{Batch=$script:batch;SamePairMatched=($fault -cne 'unmatched')}
                $nativePrimaryTransfer=[pscustomobject]@{IsCurrent=($fault -cne 'retired');ByteCount=268435456L;
                    Sha256='3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699';
                    LiveProofEligible=$false;TransferIntegrityEstablished=$false;PublicationCompletionIssued=($fault -ceq 'authority')}
                if($fault -ceq 'size') {$nativePrimaryTransfer.ByteCount--}
                if($fault -ceq 'hash') {$nativePrimaryTransfer.Sha256='0'*64}
                . ([scriptblock]::Create($probe)) -Stage Capture
                if(-not [object]::ReferenceEquals($script:canonicalTailReceipt,$script:originalReceipt)) {throw 'Receipt was copied or reopened'}
                if($fault -ceq 'missing-receipt') {$script:canonicalTailReceipt=$null}
                if($fault -ceq 'unbound') {$nativePrimaryTransfer=$null}
                if($fault -ceq 'repeated') {$script:canonicalTailJoined=$true}
                $errorText='';try {. ([scriptblock]::Create($probe)) -Stage AfterResolve} catch {$errorText=$_.Exception.Message}
                $reject=$fault -cin @('unmatched','retired','size','hash','authority','missing-receipt','unbound','repeated')
                if([bool]$errorText -ne $reject -or $script:primaryPayloadBound -eq $reject) {throw ('Production join observation: '+$fault)}
                if(-not $reject) {
                    if(-not [object]::ReferenceEquals($script:transferEvaluation,$nativePrimaryTransfer)) {throw 'Production transfer association was substituted'}
                    $verdict=if($fault -ceq 'verdict') {'PASS'} else {'FAIL_PROTOCOL_OR_INTEGRITY'}
                    $gateStatus=if($fault -ceq 'gate') {'pass'} else {'fail'}
                    $errorText='';try {. ([scriptblock]::Create($probe)) -Stage AfterOptional} catch {$errorText=$_.Exception.Message}
                    if([bool]$errorText -ne ($fault -cin @('verdict','gate'))) {throw 'Failed route verdict was promoted'}
                }
                $errorText='';try {. ([scriptblock]::Create($probe)) -Stage Closed} catch {$errorText=$_.Exception.Message}
                if([bool]$errorText -ne ($fault -ceq 'closed-failure') -or $null -ne $script:canonicalTailReceipt) {throw 'Closed path retained receipt or hid error'}
            }
            $env:NLINK_TEST_DIAGNOSTIC_CUSTODY='frontier-late-publication'
            $errorText='';try {. ([scriptblock]::Create($probe)) -Stage Capture} catch {$errorText=$_.Exception.Message}
            if($errorText -cne 'Canonical tail probe requires its separate actual-pair profile') {throw 'Old small profile was combined'}
            'canonical-original-probe-controls=13'
            """, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("canonical-original-probe-controls=13", result.Stdout);
    }

    [Theory]
    [InlineData("receipt")] [InlineData("proof")] [InlineData("handoff")]
    public async Task CanonicalGatesRejectChangedGeometryReportsAndProfile(string mode)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "canonical-tail-primary-composition-20260928", "controls-" + mode + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierCanonicalTailCompositionControls.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, mode], "pwsh");
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("canonical-tail-" + mode + "-controls-ok", result.Stdout);
    }
}
