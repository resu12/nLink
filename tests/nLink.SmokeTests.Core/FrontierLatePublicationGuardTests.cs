namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierLatePublicationGuardTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("valid")] [InlineData("capacity")] [InlineData("hash")] [InlineData("full_text")]
    [InlineData("physical")] [InlineData("compact")] [InlineData("failure_row")] [InlineData("failure_identity")]
    [InlineData("flag")] [InlineData("outcome")] [InlineData("marker")]
    [InlineData("ownership")] [InlineData("duplicate")] [InlineData("lower_index")] [InlineData("upper_index")]
    [InlineData("compact_bound")] [InlineData("compact_origin")]
    public async Task LateWitnessProbe_RejectsLossOfOriginalExactRows(string fault)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "all-copy-publication-delay-integration-20260928", "witness-guard-" + fault + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$RepoRoot,[string]$ArtifactDir,[string]$Fault)
            $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
            $probe=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierLatePublicationPrimaryProbe.ps1'
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchProjection.ps1')
            $ast=Read-AttemptAst $probe
            foreach($name in @('Get-LatePublicationHash','Get-LateReports')) {Invoke-Expression (Get-AttemptFunction $ast $name)}
            # Extract the production probe's fixed report names for fixture data,
            # not a native source/writer or manufactured production failure.
            foreach($match in [regex]::Matches((Get-AttemptFunction $ast 'Get-LateReports'),"'([^']+\.(?:txt|json))'")) {
                [IO.File]::WriteAllText((Join-Path $ArtifactDir $match.Groups[1].Value),'deterministic-row-guard-fixture')
            }
            $nativePrimaryFull=[string[]]@('failure','completion','marker'); $nativePrimaryCompact=[string[]]@($nativePrimaryFull)
            $fullSnapshotEvaluation=[pscustomobject]@{Mode='SnapshotV1Authoritative'}
            $compactSnapshotEvaluation=[pscustomobject]@{Mode='SnapshotV1Authoritative'}
            $firstCompactProofChainFailure=[pscustomobject]@{Class='filetransfer_fallback_frontier_recent_progress_grace_recheck_missing';Reason='original';Variant='original';
                Event=[pscustomobject]@{RawLine=$nativePrimaryFull[0];LineNumber=1;Sequence=1}}
            $compactProofChainCandidateArray=@($firstCompactProofChainFailure)
            $fileTransferFallbackFrontierRecentProgressGraceRecheckProof=[pscustomobject]@{UnresolvedCount=1;RecoveredCount=0}
            $verdict='FAIL_PROTOCOL_OR_INTEGRITY'; $gateStatus='fail'; $hardFailureCount=1; $nativePrimaryPair=[object]::new()
            $script:latePublicationResolverCalls=0; $script:latePublicationResolved=$false
            $script:latePublicationPayload=[pscustomobject]@{Fixture='not-native'}
            $script:latePublicationObservation=[ordered]@{CompletionPhysicalLine=2;MarkerPhysicalLine=3}
            $script:fixtureRows=@(1..3 | ForEach-Object {
                [pscustomobject]@{FullIndex=$_;CompactIndex=$_;SourceLine=$_;SemanticIndex=$_;Text=$nativePrimaryFull[$_-1];
                    LineSha256=(Get-LatePublicationHash ([Text.Encoding]::UTF8.GetBytes($nativePrimaryFull[$_-1])))}
            })
            $batch=[pscustomobject]@{Payload=$script:latePublicationPayload;DiagnosticSnapshot='original';LiveProofEligible=$false;
                NativeWriterCustodyEstablished=$false;PublicationCompletionIssued=$false;
                Completion=[pscustomobject]@{FirstSourceLine=2;LastSourceLine=2};Marker=[pscustomobject]@{FirstSourceLine=3;LastSourceLine=3}}
            $batch | Add-Member ScriptMethod GetWitnesses {return $script:fixtureRows}
            $script:authorityCalls=[Collections.Generic.List[string]]::new()
            $batch | Add-Member ScriptMethod Owns {param($row)
                $script:authorityCalls.Add('owns:'+$row.FullIndex)
                return $Fault -cne 'ownership' -and @($script:fixtureRows | Where-Object {[object]::ReferenceEquals($_,$row)}).Count -gt 0}
            $nativePrimaryCustody=[pscustomobject]@{}
            $nativePrimaryCustody | Add-Member ScriptMethod GetPhysicalOrigin {param($pair,$lines,$view,$index)
                $kind=if([object]::ReferenceEquals($view,$fullSnapshotEvaluation)) {'full'} else {'compact'}
                $script:authorityCalls.Add($kind+':'+$index)
                if($Fault -ceq 'compact_origin' -and $kind -ceq 'compact') {return 99}
                return [int]$index}
            $nativeWitness=[pscustomobject]@{Status='matched_native_frontier_diagnostic';DiagnosticOutcome='PublishedWithinObservationBound';Batch=$batch;
                OriginalSnapshot='original';FailureFullIndex=1;LiveProofEligible=$false;NativeWriterCustodyEstablished=$false;PublicationCompletionIssued=$false}
            . $probe -Stage BeforeResolve
            switch($Fault) {
                'capacity' {$script:fixtureRows=@(1..65 | ForEach-Object {$script:fixtureRows[0]})}
                'hash' {$script:fixtureRows[0].LineSha256='bad'}
                'full_text' {$script:fixtureRows[0].Text='changed'}
                'physical' {$script:fixtureRows[0].SourceLine=9}
                'compact' {$script:fixtureRows[0].CompactIndex=2}
                'duplicate' {$script:fixtureRows[1]=$script:fixtureRows[0]}
                'lower_index' {$script:fixtureRows[0].FullIndex=0}
                'upper_index' {$script:fixtureRows[0].FullIndex=4}
                'compact_bound' {$script:fixtureRows[0].CompactIndex=4}
                'failure_row' {$nativeWitness.FailureFullIndex=2}
                'failure_identity' {$firstCompactProofChainFailure.Event.RawLine=[string]::new([char[]]'failure')}
                'flag' {$batch.LiveProofEligible=$true}
                'outcome' {$nativeWitness.DiagnosticOutcome='PublishedBeforeOriginalDeadline'}
                'marker' {$batch.Marker.LastSourceLine=4}
            }
            foreach($name in @('nativePrimaryCustody','nativePrimaryFull','nativePrimaryCompact','nativePrimaryPair')) {Set-Variable -Name $name -Option Private}
            $failure=''; try {. $probe -Stage AfterResolve} catch {$failure=$_.Exception.Message}
            if(($failure -eq '') -ne ($Fault -ceq 'valid') -or $script:latePublicationResolved -ne ($Fault -ceq 'valid')) {throw ('Witness guard changed: '+$failure)}
            if($script:latePublicationReportsBefore -cne $script:latePublicationReportsAfter) {throw 'Probe mutated original reports'}
            $checks=@{capacity='capacity';ownership='ownership';duplicate='unique-index';lower_index='full-lower-bound';upper_index='full-upper-bound';
                hash='line-hash';full_text='full-text';physical='full-origin';compact='compact-text';compact_bound='compact-upper-bound';compact_origin='compact-origin'}
            if($checks.ContainsKey($Fault)) {
                if(-not $script:latePublicationObservation.Contains('WitnessVerification') -or
                    $script:latePublicationObservation.WitnessVerification.Check -cne $checks[$Fault] -or
                    -not $script:latePublicationObservation.WitnessVerification.Rejected) {throw 'First failed witness condition was not captured'}
            } elseif($script:latePublicationObservation.Contains('WitnessVerification')) {throw 'Successful row checks reported a failure'}
            if($Fault -ceq 'valid' -and ($script:authorityCalls -join ',') -cne 'owns:1,full:1,compact:1,owns:2,full:2,compact:2,owns:3,full:3,compact:3') {throw 'Authority checks reordered or repeated'}
            if($Fault -ceq 'ownership' -and ($script:authorityCalls -join ',') -cne 'owns:1') {throw 'Failed ownership did not short circuit'}
            'late-witness-guard-ok'
            """, [root, directory, fault], "pwsh");
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("late-witness-guard-ok", result.Stdout);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task WitnessVerificationDiagnostics_PreservePredicateAndFirstClosure(string host)
    {
        var script = """
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchProjection.ps1')
            $ast=Read-AttemptAst (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierLatePublicationPrimaryProbe.ps1')
            $lateFlags=[Reflection.BindingFlags]'Instance,NonPublic'
            Invoke-Expression (Get-AttemptFunction $ast 'Read-LatePublicationField')
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierWitnessVerificationDiagnostics.ps1')
            # Inert scalar meter: no native handles, authority, or publication.
            Add-Type -TypeDefinition @'
            using System.Diagnostics;
            public sealed class WitnessVerificationMeterFixture {
                internal readonly Stopwatch work = new Stopwatch();
                internal long evaluationVisits;
                internal bool closed;
                internal string PrimarySourceCloseForTest = "open";
                public void Advance(bool close) { evaluationVisits++; closed = close; if (close) PrimarySourceCloseForTest = "CheckCurrent"; }
            }
            public sealed class WitnessVerificationCustodyFixture {
                internal readonly WitnessVerificationMeterFixture inputs = new WitnessVerificationMeterFixture();
                public void Advance(bool close) { inputs.Advance(close); }
            }
            '@
            $private:nativePrimaryCustody=[WitnessVerificationCustodyFixture]::new()
            $private:nativePrimaryFull=[string[]]@('full'); $private:nativePrimaryCompact=[string[]]@('compact'); $private:nativePrimaryPair=[object]::new()
            $row=[pscustomobject]@{FullIndex=7;CompactIndex=3;SourceLine=42}
            $script:latePublicationObservation=[ordered]@{}; $script:predicateCalls=0
            $script:privateOriginals=@($nativePrimaryCustody,$nativePrimaryFull,$nativePrimaryCompact,$nativePrimaryPair)
            $result=Test-LateWitnessRejectedForTest 'full-text' $row {
                $script:predicateCalls++
                $actual=@($nativePrimaryCustody,$nativePrimaryFull,$nativePrimaryCompact,$nativePrimaryPair)
                for($i=0;$i -lt 4;$i++) {if(-not [object]::ReferenceEquals($actual[$i],$script:privateOriginals[$i])) {throw 'Private predicate input lost'}}
                $false
            } $nativePrimaryCustody $nativePrimaryFull $nativePrimaryCompact $nativePrimaryPair
            if($result -or $script:predicateCalls -ne 1 -or $script:latePublicationObservation.Count) {throw 'Successful predicate changed'}
            $result=Test-LateWitnessRejectedForTest 'full-origin' $row {
                $script:predicateCalls++; $nativePrimaryCustody.Advance($true)
                $row.FullIndex=0; $row.CompactIndex=0; $row.SourceLine=0
                0 -ne $row.SourceLine
            } $nativePrimaryCustody $nativePrimaryFull $nativePrimaryCompact $nativePrimaryPair
            $sample=$script:latePublicationObservation.WitnessVerification
            if($result -or $script:predicateCalls -ne 2 -or $sample.Check -cne 'full-origin' -or
                $sample.FullIndex -ne 7 -or $sample.CompactIndex -ne 3 -or $sample.SourceLine -ne 42 -or
                $sample.BeforeVisits -ne 0 -or $sample.AfterVisits -ne 1 -or $sample.BeforeClosed -or -not $sample.AfterClosed -or
                $sample.Rejected -or $sample.Threw -or $sample.CloseCaller -cne 'CheckCurrent' -or
                $sample.BeforeWorkTicks -ne 0 -or $sample.AfterWorkTicks -ne 0) {throw 'First closure or pre-clear scalar identity lost'}
            $frozen=$sample | ConvertTo-Json -Compress
            if(-not (Test-LateWitnessRejectedForTest 'ownership' $row {$script:predicateCalls++;$true} $nativePrimaryCustody) -or
                ($script:latePublicationObservation.WitnessVerification | ConvertTo-Json -Compress) -cne $frozen) {throw 'First closure replaced'}
            $script:latePublicationObservation=[ordered]@{}
            $original=[InvalidOperationException]::new('predicate-sentinel'); $caught=$null
            try {$null=Test-LateWitnessRejectedForTest 'line-hash' $row {throw $original} $nativePrimaryCustody} catch {$caught=$_.Exception}
            while($null -ne $caught.InnerException) {$caught=$caught.InnerException}
            if(-not [object]::ReferenceEquals($caught,$original) -or -not $script:latePublicationObservation.WitnessVerification.Threw) {throw 'Original exception changed'}
            $script:latePublicationObservation=[ordered]@{}; $nativePrimaryCustody=$null
            if(-not (Test-LateWitnessRejectedForTest 'ownership' $row {$true} $nativePrimaryCustody) -or
                $script:latePublicationObservation.WitnessVerification.Check -cne 'ownership') {throw 'Missing meter replaced predicate'}
            $script:latePublicationObservation=[object]::new()
            if(Test-LateWitnessRejectedForTest 'full-text' $row {$false}) {throw 'Broken observer replaced predicate'}
            'witness-verification-diagnostics=passed'
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("witness-verification-diagnostics=passed", result.Stdout);
    }

    [Theory]
    [InlineData("valid")] [InlineData("missing")] [InlineData("live")] [InlineData("custody")] [InlineData("publication")]
    [InlineData("offer")] [InlineData("wait")] [InlineData("commit_attempt")] [InlineData("commit_result")]
    public async Task LateOutput_RequiresOriginalCommitNotJustAnAttempt(string fault)
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$RepoRoot,[string]$Fault)
            $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchProjection.ps1')
            $ast=Read-AttemptAst (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierLatePublicationPrimaryProbe.ps1')
            foreach($name in @('Read-LatePublicationField','Test-LateOutput')) {Invoke-Expression (Get-AttemptFunction $ast $name)}
            $lateFlags=[Reflection.BindingFlags]'Instance,NonPublic'
            # Private-field observation controls only; no native output is created.
            Add-Type @'
            public sealed class LateOutputFixture {
                private readonly bool diagnosticOffered, diagnosticWaitAttempted, diagnosticCommitAttempted;
                private readonly Inputs inputs;
                public LateOutputFixture(string fault) {
                    diagnosticOffered=fault!="offer"; diagnosticWaitAttempted=fault!="wait";
                    diagnosticCommitAttempted=fault!="commit_attempt"; inputs=new Inputs(fault!="commit_result");
                }
                private sealed class Inputs { private readonly Channel diagnosticOutput; public Inputs(bool result) {diagnosticOutput=new Channel(result);} }
                private sealed class Channel { private readonly bool committed; public Channel(bool result) {committed=result;} }
            }
            '@
            $preparation=if($Fault -ceq 'missing') {$null} else {[pscustomobject]@{LiveProofEligible=$Fault -ceq 'live';
                NativeWriterCustodyEstablished=$Fault -ceq 'custody';PublicationCompletionIssued=$Fault -ceq 'publication'}}
            $failure=''; try {Test-LateOutput ([LateOutputFixture]::new($Fault)) $preparation} catch {$failure=$_.Exception.Message}
            if(($failure -eq '') -ne ($Fault -ceq 'valid')) {throw ('Output gate changed: '+$failure)}
            'late-output-guard-ok'
            """, [FindRepoRoot(), fault], "pwsh");
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("late-output-guard-ok", result.Stdout);
    }

    [Theory]
    [InlineData("unchanged")] [InlineData("report")] [InlineData("view")] [InlineData("view_identity")]
    [InlineData("reason")] [InlineData("failure_identity")] [InlineData("proof_count")]
    [InlineData("candidate_identity")] [InlineData("verdict")] [InlineData("gate")]
    [InlineData("omission")] [InlineData("omission_escaped")]
    public async Task LateProbe_HashesReportsAndPreservesOriginalStateAroundResolver(string fault)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "all-copy-publication-delay-integration-20260928", "report-guard-" + fault + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$RepoRoot,[string]$ArtifactDir,[string]$Fault)
            $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
            function Get-FileTransferCoordinatorSnapshotEvaluation {throw 'Extra evaluation forbidden'}
            function Read-FileTransferLogEvents {throw 'Extra native/source read forbidden'}
            $probe=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierLatePublicationPrimaryProbe.ps1'
            foreach($name in @('filetransfer-operator-verdict.txt','filetransfer-route-consistency-summary.txt',
                'transfer-terminal-summary.txt','stability-gates-summary.txt','phase0-invariant-ledger.txt','filetransfer-invariant-summary.txt',
                'filetransfer-retained-compact-summary.json','filetransfer-coordinator-snapshot-compact-summary.json',
                'filetransfer-coordinator-snapshot-full-summary.json','filetransfer-coordinator-snapshot-parity-summary.json')) {
                [IO.File]::WriteAllText((Join-Path $ArtifactDir $name),'deterministic-guard-fixture')
            }
            $fullSnapshotEvaluation=[pscustomobject]@{Mode='SnapshotV1Authoritative';BoundaryHash='same';StreamComplete=$true}
            $compactSnapshotEvaluation=[pscustomobject]@{Mode='SnapshotV1Authoritative';BoundaryHash='same';StreamComplete=$true}
            $firstCompactProofChainFailure=[pscustomobject]@{Class='filetransfer_fallback_frontier_recent_progress_grace_recheck_missing';Reason='original';Variant='original'}
            $compactProofChainCandidateArray=@($firstCompactProofChainFailure)
            $fileTransferFallbackFrontierRecentProgressGraceRecheckProof=[pscustomobject]@{UnresolvedCount=1;RecoveredCount=0}
            $verdict='FAIL_PROTOCOL_OR_INTEGRITY'; $gateStatus='fail'; $hardFailureCount=1
            $script:latePublicationResolverCalls=0; $nativeWitness=$null
            . $probe -Stage BeforeResolve
            if($script:latePublicationResolverCalls -ne 1) {throw 'Original optional call count lost'}
            if($Fault -ceq 'omission') {$script:latePublicationOmissionReason='native_frontier_receipt_ambiguity'}
            if($Fault -ceq 'omission_escaped') {$script:latePublicationOmissionReason="fixture `"quoted`"`r`nnext line"}
            switch($Fault) {
                'report' {[IO.File]::AppendAllText((Join-Path $ArtifactDir 'phase0-invariant-ledger.txt'),'changed')}
                'view' {$fullSnapshotEvaluation.BoundaryHash='changed'}
                'view_identity' {$fullSnapshotEvaluation=$fullSnapshotEvaluation | Select-Object *}
                'reason' {$firstCompactProofChainFailure.Reason='changed'}
                'failure_identity' {$firstCompactProofChainFailure=$firstCompactProofChainFailure | Select-Object *}
                'proof_count' {$fileTransferFallbackFrontierRecentProgressGraceRecheckProof.UnresolvedCount=0}
                'candidate_identity' {$compactProofChainCandidateArray=@($firstCompactProofChainFailure | Select-Object *)}
                'verdict' {$verdict='PASS'}
                'gate' {$gateStatus='pass'}
            }
            $originalError=[Console]::Error; $diagnostics=[IO.StringWriter]::new(); $failure=''
            try {
                [Console]::SetError($diagnostics)
                try {. $probe -Stage AfterResolve} catch {$failure=$_.Exception.Message}
            } finally {[Console]::SetError($originalError)}
            $record=$diagnostics.ToString().TrimEnd([char[]]"`r`n"); $diagnostics.Dispose()
            $prefix='late-publication-resolver-omission; reason_json='
            if(-not $record.StartsWith($prefix,[StringComparison]::Ordinal) -or $record.Contains("`r") -or $record.Contains("`n") -or
                ($record.Substring($prefix.Length) | ConvertFrom-Json) -cne $script:latePublicationOmissionReason) {throw 'Original reason lost or not one JSON-escaped line'}
            $expected=switch($Fault) {
                {$_ -cin @('unchanged','omission','omission_escaped')} {'Original optional resolver produced no witness'}
                'report' {'Original reports changed during optional resolver'}
                'candidate_identity' {'Original candidate identity changed'}
                default {'Original primary state changed around optional resolver'}
            }
            if($failure -cne $expected) {throw ('Report/state guard missed mutation: '+$failure)}
            if($Fault -cne 'report' -and $script:latePublicationReportsBefore -cne $script:latePublicationReportsAfter) {throw 'Probe mutated original reports'}
            'late-report-state-guard-ok'
            """, [root, directory, fault], "pwsh");
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("late-report-state-guard-ok", result.Stdout);
    }

    [Theory]
    [InlineData("valid")] [InlineData("open_pin")] [InlineData("payload_text")] [InlineData("witness_text")]
    [InlineData("wrong_failure")] [InlineData("no_resolver")] [InlineData("no_witness")] [InlineData("no_output")]
    public async Task LateCleanup_RecordsUnmetMilestonesAndDropsProbeReferences(string fault)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "all-copy-publication-delay-integration-20260928", "cleanup-guard-" + fault + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$RepoRoot,[string]$ArtifactDir,[string]$Fault)
            $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
            $script:latePublicationObservation=[ordered]@{Fixture='deterministic-cleanup-only'}
            $script:latePublicationPins=@([pscustomobject]@{IsClosed=$Fault -cne 'open_pin'})
            $script:latePublicationPayload=[pscustomobject]@{Text=$(if($Fault -ceq 'payload_text') {'retained'} else {$null})}
            $script:latePublicationWitnesses=@([pscustomobject]@{Text=$(if($Fault -ceq 'witness_text') {'retained'} else {$null})})
            $script:latePublicationResolverCalls=if($Fault -ceq 'no_resolver') {0} else {1}
            $expectedOmission=if($Fault -ceq 'no_resolver') {$null} elseif($Fault -ceq 'no_witness') {'native_frontier_receipt_ambiguity'} else {''}
            $script:latePublicationOmissionReason=$expectedOmission
            $script:latePublicationResolved=$Fault -cne 'no_witness'; $script:latePublicationOutput=$Fault -cne 'no_output'
            $script:latePublicationReportsBefore='same'; $script:latePublicationReportsAfter='same'; $script:latePublicationChecked=$false
            $script:latePublicationFull='held'; $script:latePublicationCompact='held'; $script:latePublicationState='held'; $script:latePublicationCandidates=@('held')
            $verdict='FAIL_PROTOCOL_OR_INTEGRITY'; $gateStatus='fail'
            $firstCompactProofChainFailure=[pscustomobject]@{Class=$(if($Fault -ceq 'wrong_failure') {'other'} else {'filetransfer_fallback_frontier_recent_progress_grace_recheck_missing'})}
            $script:latePublicationFailure=$firstCompactProofChainFailure
            $failure=''; try {. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierLatePublicationPrimaryProbe.ps1') -Stage Closed} catch {$failure=$_.Exception.Message}
            $observed=Get-Content -LiteralPath (Join-Path $ArtifactDir 'late-publication-primary-observation.json') -Raw | ConvertFrom-Json
            if($observed.PrimaryVerdict -cne 'FAIL_PROTOCOL_OR_INTEGRITY' -or $observed.GateStatus -cne 'fail' -or
                $observed.ResolverOmissionReason -cne $expectedOmission -or
                $observed.Fixture -cne 'deterministic-cleanup-only' -or ($failure -eq '') -ne ($Fault -ceq 'valid') -or
                $script:latePublicationChecked -ne ($Fault -ceq 'valid')) {throw ('Cleanup changed original result: '+$failure)}
            if($null -ne $script:latePublicationFull -or $null -ne $script:latePublicationCompact -or $null -ne $script:latePublicationFailure -or
                $null -ne $script:latePublicationState -or $script:latePublicationCandidates.Count -or $script:latePublicationWitnesses.Count -or
                $null -ne $script:latePublicationPayload -or $script:latePublicationPins.Count -or
                $null -ne $script:latePublicationOmissionReason) {throw 'Probe retained source/view/payload references'}
            'late-cleanup-guard-ok'
            """, [root, directory, fault], "pwsh");
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("late-cleanup-guard-ok", result.Stdout);
    }

    [Theory]
    [InlineData("none", true)] [InlineData("none", false)] [InlineData("Capture", true)]
    [InlineData("BeforeResolve", true)] [InlineData("AfterResolve", true)] [InlineData("AfterOptional", true)]
    [InlineData("Closed", true)] [InlineData("resolver", true)] [InlineData("original", true)]
    [InlineData("omission", true)] [InlineData("unreported", true)]
    public async Task LateGuards_PreserveOriginalReportsResolverOutputAndCleanup(string fault, bool eligible)
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$RepoRoot,[string]$Fault,[string]$EligibleText)
            $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchProjection.ps1')
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierLatePublicationProbeGuards.ps1')
            $original=Get-AttemptFunction (Read-AttemptAst (Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1')) 'Write-FileTransferCompactRetainedAnalysisArtifacts'
            $guarded=Add-FrontierLatePublicationProbeGuards $original
            $tokens=$null; $errors=$null
            $null=[Management.Automation.Language.Parser]::ParseInput($guarded,[ref]$tokens,[ref]$errors)
            if($errors.Count) {throw ($errors | Out-String)}
            if([regex]::Matches($guarded,'-OmissionReason').Count -ne 1) {throw 'Original-call reason capture missing/duplicated'}
            $duplicate=''; try {$null=Add-FrontierLatePublicationProbeGuards $guarded} catch {$duplicate=$_.Exception.Message}
            if($duplicate -cne 'Original optional resolver already captures omission reason') {throw 'Duplicate instrumentation not rejected'}
            foreach($operation in @('TryCapturePayloads','Get-FileTransferCoordinatorSnapshotEvaluation',
                'Resolve-FileTransferNativeFrontierWitness','TryPrepareDiagnostic','TryOfferOutput','TryCommitOutputWhenAdopted')) {
                if([regex]::Matches($guarded,[regex]::Escape($operation)).Count -ne [regex]::Matches($original,[regex]::Escape($operation)).Count) {throw 'Probe duplicated original operation'}
            }
            $definition=@'
            function Invoke-OriginalForTest {
                $private:nativePrimaryCustody=$script:fakeCustody
                $fileTransferFallbackFrontierRecentProgressGraceRecheckProof='original-proof'
                $localFailureWitnessOutputEnabled=$false
                try {
                    # Only an admitted bounded private context retains these array references.
                    $script:reports++
                    if($Fault -ceq 'original') {throw 'original report failed'}
                    if($EligibleText -ceq 'True') {
                        try {
                            $private:nativeWitness = Resolve-FileTransferNativeFrontierWitness `
                                -RecentProof $fileTransferFallbackFrontierRecentProgressGraceRecheckProof
                            if($null -ne $private:nativeWitness) {$script:outputAttempts++}
                        } catch { }
                    }
                    if ($localFailureWitnessOutputEnabled) {throw 'Unexpected output mode'}
                    return 'original-result'
                } finally {
                    try { $private:nativePrimaryCustody.Dispose() } catch { }
                }
            }
            '@
            $definition=Add-FrontierLatePublicationProbeGuards $definition
            $definition=$definition.Replace(". (Join-Path `$RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierLatePublicationPrimaryProbe.ps1')",'Invoke-ProbeForTest')
            $script:latePublicationProbeError=$null
            $script:latePublicationOmissionReason=$null
            $script:reports=0; $script:disposed=0; $script:resolutions=0; $script:outputAttempts=0
            $script:stages=[Collections.Generic.List[string]]::new()
            function Invoke-ProbeForTest([string]$Stage) {
                $script:stages.Add($Stage)
                if($Stage -ceq 'Closed' -and $script:disposed -ne 1) {throw 'Probe preceded original disposal'}
                if($Stage -ceq $Fault) {throw ('probe-'+$Stage)}
            }
            function Resolve-FileTransferNativeFrontierWitness($RecentProof,[ref]$OmissionReason) {
                if($RecentProof -cne 'original-proof') {throw 'Original proof changed'}
                if($null -eq $OmissionReason) {throw 'Original-call reason reference missing'}
                $script:resolutions++
                if($Fault -ceq 'resolver') {throw 'original resolver refused'}
                if($Fault -ceq 'omission') {$OmissionReason.Value='native_frontier_receipt_ambiguity'; return $null}
                if($Fault -ceq 'unreported') {return $null}
                $OmissionReason.Value=''
                return 'original-witness'
            }
            $script:fakeCustody=[pscustomobject]@{}
            $script:fakeCustody | Add-Member ScriptMethod Dispose {$script:disposed++}
            Invoke-Expression $definition
            $failure=''; $value=$null
            try {$value=Invoke-OriginalForTest} catch {$failure=$_.Exception.Message}
            if($script:reports -ne 1 -or $script:disposed -ne 1) {throw 'Original reports/cleanup lost'}
            $calls=if($EligibleText -ceq 'True' -and $Fault -cne 'original') {1} else {0}
            $outputs=if($calls -eq 1 -and $Fault -cnotin @('resolver','omission','unreported')) {1} else {0}
            if($script:resolutions -ne $calls -or $script:outputAttempts -ne $outputs) {throw 'Original optional call/output branch changed'}
            $expectedOmission=if($calls -eq 0 -or $Fault -cin @('resolver','unreported')) {$null}
                elseif($Fault -ceq 'omission') {'native_frontier_receipt_ambiguity'} else {''}
            if($script:latePublicationOmissionReason -cne $expectedOmission) {throw 'Original-call omission result changed'}
            $expectedStages=if($Fault -ceq 'original') {'Capture,Closed'} elseif($calls -eq 0) {'Capture,AfterOptional,Closed'}
                elseif($Fault -ceq 'resolver') {'Capture,BeforeResolve,AfterOptional,Closed'} else {'Capture,BeforeResolve,AfterResolve,AfterOptional,Closed'}
            if(($script:stages -join ',') -cne $expectedStages) {throw 'Probe stage order changed'}
            if($Fault -ceq 'original') {if($failure -cne 'original report failed') {throw 'Original error masked'}}
            elseif($failure -or $value -cne 'original-result') {throw 'Probe replaced original result'}
            $expectedError=if($Fault -cin @('none','original','resolver','omission','unreported')) {''} else {'probe-'+$Fault}
            $observedError=if($null -eq $script:latePublicationProbeError) {''} else {$script:latePublicationProbeError.Exception.Message}
            if($observedError -cne $expectedError) {throw 'Probe failure was lost'}
            'late-guards-ok'
            """, [FindRepoRoot(), fault, eligible.ToString()], "pwsh");
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("late-guards-ok", result.Stdout);
    }

    [Theory]
    [InlineData("valid")] [InlineData("positive")] [InlineData("root")] [InlineData("successor")]
    [InlineData("frontier")] [InlineData("paused")] [InlineData("terminal")] [InlineData("epoch")]
    [InlineData("live")] [InlineData("custody")] [InlineData("publication")] [InlineData("early_completion")]
    [InlineData("late_write")] [InlineData("late_completion")] [InlineData("clock_identity")] [InlineData("clock_order")]
    public async Task LateReceiptGate_RejectsNonMatchingDiagnosticFacts(string fault)
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$RepoRoot,[string]$Fault)
            $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchProjection.ps1')
            $ast=Read-AttemptAst (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierLatePublicationPrimaryProbe.ps1')
            Invoke-Expression (Get-AttemptFunction $ast 'Test-LateReceipt')
            # Deterministic data-only rejection controls; this is not a native receipt.
            function Clock($sequence,$utc,$counter) {[pscustomobject]@{ProcessId=1;ProcessCreationFileTime=1;ObservationId='o';ClockInstanceId='c';
                Frequency=1000;CaptureSequence=$sequence;UtcTicks=$utc;CounterBefore=$counter;CounterAfter=$counter+1}}
            $r=[pscustomobject]@{Outcome='PublishedWithinObservationBound';LiveProofEligible=$false;NativeWriterCustodyEstablished=$false;PublicationCompletionIssued=$false;
                RootGeneration=1;RecheckedGeneration=1;FirstWriteGeneration=1;ProcessId=1;ProcessCreationFileTime=1;ObservationId='o';ClockInstanceId='c';Frequency=1000;
                OriginalDeadlineUtcTicks=8500000L;PublicationDeadlineUtcTicks=14000000L;
                Source=[pscustomobject]@{Route='post_tuna_fallback_v6';ProtocolVersion=6;TransportEpoch=2};
                Frame=[pscustomobject]@{ContiguousCommittedChunkIndex=4;DurableReceivedHighestChunkIndex=7;BytesCommitted=86016;TransferPaused=$false;TerminalReady=$false;TransportEpoch=2;MissingRanges=@()};
                Completion=[pscustomobject]@{DecisionKind='FallbackControlPlaneSendCompleted';DecisionStatus='Accepted'};
                Lifecycle=@([pscustomobject]@{Kind='Write';Clock=(Clock 1 4000000 400);Binding=[pscustomobject]@{Before=1;After=4;Count=3;Highest=7}},
                    [pscustomobject]@{Kind='AcceptedCompletion';Clock=(Clock 2 9000000 900);Binding=$null})}
            switch($Fault) {
                'positive' {$r.Outcome='PublishedBeforeOriginalDeadline'}
                'root' {$r.RecheckedGeneration=2}
                'successor' {$r.Lifecycle+= [pscustomobject]@{Kind='StallSuccessor';Clock=$null}}
                'frontier' {$r.Frame.ContiguousCommittedChunkIndex=5}
                'paused' {$r.Frame.TransferPaused=$true}
                'terminal' {$r.Frame.TerminalReady=$true}
                'epoch' {$r.Frame.TransportEpoch=3}
                'live' {$r.LiveProofEligible=$true}
                'custody' {$r.NativeWriterCustodyEstablished=$true}
                'publication' {$r.PublicationCompletionIssued=$true}
                'early_completion' {$r.Lifecycle[1].Clock.UtcTicks=8499999}
                'late_write' {$r.Lifecycle[0].Clock.UtcTicks=8500000}
                'late_completion' {$r.Lifecycle[1].Clock.UtcTicks=14000000}
                'clock_identity' {$r.Lifecycle[1].Clock.ClockInstanceId='other'}
                'clock_order' {$r.Lifecycle[1].Clock.CounterBefore=399}
            }
            $failure=''; try {Test-LateReceipt $r} catch {$failure=$_.Exception.Message}
            if(($failure -eq '') -ne ($Fault -ceq 'valid')) {throw ('Unexpected receipt gate: '+$failure)}
            'late-receipt-gate-ok'
            """, [FindRepoRoot(), fault], "pwsh");
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("late-receipt-gate-ok", result.Stdout);
    }

    [Theory]
    [InlineData("writer-valid", "", "", "")]
    [InlineData("writer-source-read-cross-process-carrier-frontier-schedule", "", "", "")]
    [InlineData("writer-source-read-cross-process-carrier-full-frontier-late-publication", "", "", "")]
    [InlineData("writer-source-read-cross-process-carrier-frontier-late-publication", "writer-valid", "", "")]
    [InlineData("writer-source-read-cross-process-carrier-frontier-late-publication", "", "valid", "")]
    [InlineData("writer-source-read-cross-process-carrier-frontier-late-publication", "", "", "canonical-fallback")]
    public async Task LateProfile_RejectsOtherTransferOrPairHooksBeforeLaunch(string scenario, string wrapper, string parent, string primary)
    {
        if (!OperatingSystem.IsWindows()) return;
        var script = await File.ReadAllTextAsync(Path.Combine(FindRepoRoot(), "tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatch.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), "unused", scenario, wrapper, parent, primary, "frontier-late-publication"], "pwsh");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Diagnostic custody requires the separate small profile", result.Stderr);
    }
}
