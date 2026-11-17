namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FrontierProductionPairIntegrationTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    public async Task ProductionUsesSelectedReceiptAndProbeOnlyObserves()
    {
        var root = FindRepoRoot();
        var resolver = await File.ReadAllTextAsync(Path.Combine(root, "tools/FileTransferOps/LocalNativeFrontierWitness.ps1"));
        Assert.Contains("$state.Receipt=$receipt; $state.Payload=$chosen", resolver);
        Assert.Equal(1, resolver.Split("$r=$native.Text | ConvertFrom-Json").Length - 1);
        Assert.Equal(1, resolver.Split(".TryCheckCanonicalSamePairWitness(").Length - 1);
        Assert.DoesNotContain("TryBindTransferPair", resolver);
        Assert.DoesNotContain("Get-FileTransferCoordinatorSnapshotEvaluation", resolver);
        var probe = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierCanonicalTailPrimaryProbe.ps1"));
        Assert.DoesNotContain("TryCheckCanonicalSamePairWitness", probe);
        Assert.DoesNotContain("TryBindTransferPair", probe);
        Assert.Contains("$script:transferEvaluation=$nativePrimaryTransfer", probe);
        var binding = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadPrimaryProbe.ps1"));
        Assert.True(binding.IndexOf("if($primaryFault -ceq 'canonical-tail-late-publication') {return}", StringComparison.Ordinal) <
            binding.IndexOf(".TryBindTransferPair(", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("pwsh")] [InlineData("powershell")]
    public async Task ProductionBranchPreservesOriginalOrderAndOmission(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop';Set-StrictMode -Version Latest
            $text=[IO.File]::ReadAllText((Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1'))
            $t=$null;$e=$null;$ast=[Management.Automation.Language.Parser]::ParseInput($text,[ref]$t,[ref]$e)
            $blocks=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.IfStatementAst] -and
                $n.Extent.Text.StartsWith('if ($null -ne $private:nativePrimaryPayloads -and')},$true))
            if($e.Count -or $blocks.Count -ne 1) {throw 'Original optional branch missing'}
            $block=$blocks[0].Extent.Text
            if(-not $block.Contains('TryBindTransferPair') -or -not $block.Contains('-TransferEvaluation $private:nativePrimaryTransfer')) {
                throw 'Production pair invocation missing'
            }
            if($blocks[0].Extent.StartOffset -lt $text.IndexOf("'filetransfer-coordinator-snapshot-parity-summary.json'") -or
                [regex]::Matches($block,'TryBindTransferPair').Count -ne 1 -or
                [regex]::Matches($block,'Resolve-FileTransferNativeFrontierWitness').Count -ne 1) {throw 'Post-report one-shot order changed'}
            $block=$block.Replace(". (Join-Path `$PSScriptRoot 'LocalNativeFrontierWitness.ps1')",'')
            $ids=@(1..8 | ForEach-Object {[object]::new()})
            $nativePrimaryCustody=[pscustomobject]@{}
            $nativePrimaryCustody | Add-Member ScriptMethod TryBindTransferPair {
                param($pair,$context,$full,$compact,$fullView,$compactView)
                $script:order.Add('Bind')
                $actual=@($pair,$context,$full,$compact,$fullView,$compactView)
                for($i=0;$i -lt 6;$i++) {if(-not [object]::ReferenceEquals($actual[$i],$ids[$i])) {throw 'Original binding arguments changed'}}
                if($fault -ceq 'bind-throws') {throw 'bind-failed'}
                if($fault -ceq 'unpaired') {return $null}
                return $ids[6]
            }
            $prep=[pscustomobject]@{}
            $prep | Add-Member ScriptMethod TryOfferOutput {$script:order.Add('Offer');return $fault -cne 'offer-refused'}
            $prep | Add-Member ScriptMethod TryCommitOutputWhenAdopted {$script:order.Add('Commit');return $fault -cne 'commit-refused'}
            $batch=[pscustomobject]@{}
            $batch | Add-Member ScriptMethod TryPrepareDiagnostic {$script:order.Add('Prepare');if($fault -cne 'prepare-refused') {return $prep}}
            function Resolve-FileTransferNativeFrontierWitness {
                param($Custody,$Evaluations,$Context,$Full,$Compact,$FullView,$CompactView,$Payloads,$Index,$Failure,$Candidates,$RecentProof,$TransferEvaluation)
                $script:order.Add('Resolve')
                if(($null -eq $TransferEvaluation) -ne ($fault -ceq 'unpaired') -or
                    ($null -ne $TransferEvaluation -and -not [object]::ReferenceEquals($TransferEvaluation,$ids[6]))) {throw 'Original transfer association changed'}
                if($fault -ceq 'resolve-throws') {throw 'resolve-failed'}
                if($fault -cne 'resolve-refused') {return [pscustomobject]@{Batch=$batch}}
            }
            $invoke=[scriptblock]::Create($block)
            $nativePrimaryPair=$ids[0];$nativePrimaryContext=$ids[1];$nativePrimaryFull=$ids[2];$nativePrimaryCompact=$ids[3]
            $fullSnapshotEvaluation=$ids[4];$compactSnapshotEvaluation=$ids[5]
            $Index=$null;$compactProofChainCandidateArray=@();$fileTransferFallbackFrontierRecentProgressGraceRecheckProof=$null
            foreach($fault in @('valid','unpaired','no-payload','no-failure','unsupported','bind-throws','resolve-throws','resolve-refused','prepare-refused','offer-refused','commit-refused')) {
                $script:order=[Collections.Generic.List[string]]::new()
                $nativePrimaryPayloads=$ids[7];$firstCompactProofChainFailure=[pscustomobject]@{Class='filetransfer_fallback_frontier_recent_progress_grace_recheck_missing'}
                if($fault -ceq 'no-payload') {$nativePrimaryPayloads=$null}
                if($fault -ceq 'no-failure') {$firstCompactProofChainFailure=$null}
                if($fault -ceq 'unsupported') {$firstCompactProofChainFailure.Class='other'}
                $verdict='FAIL_PROTOCOL_OR_INTEGRITY';$gateStatus='fail'
                . $invoke
                $expected=if($fault -cin @('no-payload','no-failure','unsupported')) {''}
                    elseif($fault -ceq 'bind-throws') {'Bind'}
                    elseif($fault -cin @('resolve-throws','resolve-refused')) {'Bind,Resolve'}
                    elseif($fault -ceq 'prepare-refused') {'Bind,Resolve,Prepare'}
                    elseif($fault -ceq 'offer-refused') {'Bind,Resolve,Prepare,Offer'}
                    else {'Bind,Resolve,Prepare,Offer,Commit'}
                if(($script:order -join ',') -cne $expected -or $verdict -cne 'FAIL_PROTOCOL_OR_INTEGRITY' -or $gateStatus -cne 'fail') {throw ('Original output order changed: '+$fault)}
            }
            'production-pair-branch-controls=11'
            """, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("production-pair-branch-controls=11", result.Stdout);
    }

    [Theory]
    [InlineData("pwsh")] [InlineData("powershell")]
    public async Task ResolverChecksOriginalReceiptBeforeReturningWitness(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop';Set-StrictMode -Version Latest
            $text=[IO.File]::ReadAllText((Join-Path $RepoRoot 'tools/FileTransferOps/LocalNativeFrontierWitness.ps1'))
            $t=$null;$e=$null;$ast=[Management.Automation.Language.Parser]::ParseInput($text,[ref]$t,[ref]$e)
            $function=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and
                $n.Name -ceq 'Resolve-FileTransferNativeFrontierWitness'},$true))[0]
            $outer=@($function.Body.EndBlock.Statements | Where-Object {$_ -is [Management.Automation.Language.TryStatementAst]})[0]
            $checks=@($outer.Body.Statements | Where-Object {$_ -is [Management.Automation.Language.IfStatementAst] -and
                $_.Extent.Text.StartsWith('if ($null -ne $TransferEvaluation)')})
            if($checks.Count -ne 1) {throw 'Production receipt check missing'}
            $return=@($outer.Body.Statements | Where-Object {$_ -is [Management.Automation.Language.ReturnStatementAst]})[-1]
            $code='param($TransferEvaluation) try {'+$checks[0].Extent.Text+"`n"+$return.Extent.Text+'} catch '+
                $outer.CatchClauses[0].Body.Extent.Text+' finally '+$outer.Finally.Extent.Text
            $invoke=[scriptblock]::Create($code)
            $originalTransfer=[object]::new();$originalPayload=[object]::new()
            $originalReceipt=[pscustomobject]@{Source=[pscustomobject]@{SessionId='s';TransferId='t'};ProcessId=20;ProcessCreationFileTime=200L}
            $batch=[pscustomobject]@{DiagnosticSnapshot='original'}
            $batch | Add-Member ScriptMethod Dispose {$script:disposed++}
            $Custody=[pscustomobject]@{}
            $Custody | Add-Member ScriptMethod TryCheckCanonicalSamePairWitness {
                param($transfer,$witnesses,$payload,$session,$transferId,$process,$creation)
                $script:checks++
                if(-not [object]::ReferenceEquals($transfer,$originalTransfer) -or -not [object]::ReferenceEquals($witnesses,$batch) -or
                    -not [object]::ReferenceEquals($payload,$originalPayload) -or $session -cne 's' -or $transferId -cne 't' -or
                    $process -ne 20 -or $creation -ne 200L) {throw 'Original receipt arguments changed'}
                if($fault -ceq 'throws') {throw 'pair-failed'}
                return $fault -cne 'refuses'
            }
            foreach($fault in @('valid','unpaired','refuses','throws')) {
                $script:checks=0;$script:disposed=0;$reason=''
                $OmissionReason=[ref]$reason
                $state=@{Receipt=$originalReceipt;Payload=$originalPayload;SamePairMatched=$false;Error='';Outcome='PublishedWithinObservationBound';Failure=9}
                $candidate=if($fault -ceq 'unpaired') {$null} else {$originalTransfer}
                $result=& $invoke $candidate
                $accepted=$fault -cin @('valid','unpaired')
                if(($null -ne $result) -ne $accepted -or $script:checks -ne [int]($fault -cne 'unpaired') -or
                    $script:disposed -ne [int](-not $accepted)) {throw ('Same-pair result or call count: '+$fault)}
                if($accepted -and ($result.SamePairMatched -ne ($fault -ceq 'valid') -or $result.LiveProofEligible -or
                    $result.PublicationCompletionIssued -or $result.NativeWriterCustodyEstablished)) {throw 'Result changed diagnostic authority'}
                if($null -ne $state.Receipt -or $null -ne $state.Payload) {throw 'Resolver retained original receipt references'}
                if($fault -ceq 'refuses' -and $reason -cne 'native_frontier_same_pair_unavailable') {throw 'Pair omission reason lost'}
                if($fault -ceq 'throws' -and $reason -notmatch 'pair-failed') {throw 'Pair exception reason lost'}
            }
            'production-pair-resolver-controls=4'
            """, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("production-pair-resolver-controls=4", result.Stdout);
    }
}
