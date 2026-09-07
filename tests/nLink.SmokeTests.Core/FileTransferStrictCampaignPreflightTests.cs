namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "AcceptancePhase5Fast")]
[Trait("AcceptancePhase5Suite", "StrictCampaignPreflight")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferStrictCampaignPreflightTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task StrictCampaignPreflight_OneAttemptNoQuarantineAndMetadataPreserveDefaults(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }

        var result = await RunPowerShellScriptTextAsync(Harness, [FindRepoRoot(), "contracts", "false"], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("strict-campaign-contracts-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StrictCampaignPreflight_GlobalFailureCannotLaunchFilteredScenario(bool strict)
    {
        if (!OperatingSystem.IsWindows()) { return; }

        var result = await RunPowerShellScriptTextAsync(
            Harness, [FindRepoRoot(), "global", strict ? "true" : "false"]);
        Assert.Equal(strict ? 1 : 0, result.ExitCode);
        if (strict)
        {
            Assert.Contains("preflight-failure-finalized", result.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("scenario-launch", result.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("attempt 2/", result.Stdout, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("scenario-launch", result.Stdout, StringComparison.Ordinal);
            Assert.Contains("attempt 2/", result.Stdout, StringComparison.Ordinal);
            Assert.Contains("quarantined setup-only failure", result.Stdout, StringComparison.Ordinal);
        }
    }

    // Only selected production functions/branches are executed. The full acceptance
    // script, GUI, live preflight, analyzer, and retained report are never invoked.
    private const string Harness = """
        param([string]$RepoRoot, [string]$Mode, [string]$Strict)
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'
        $StrictCampaignPreflight = $Strict -eq 'true'
        $path = Join-Path $RepoRoot 'tools/Run-FileTransferRouteAcceptance.ps1'
        $tokens = $null
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
        if ($errors.Count -ne 0) { throw 'acceptance script did not parse' }
        $names = @(
            'Test-Phase5BridgeReadinessPreflightRetryableFailure',
            'Test-Phase5BridgeReadinessPreflightQuarantinableFailure',
            'Write-Phase5BridgeReadinessPreflightAttemptSummary',
            'Invoke-Phase5BridgeReadinessPreflightWithRetry',
            'Write-Phase5BridgeReadinessPreflightQuarantineSummary',
            'Write-Phase5ScenarioIsolationSummary',
            'Invoke-Phase5ScenarioIsolationPreflight')
        foreach ($name in $names) {
            $found = @($ast.FindAll({ param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
            }, $true))
            if ($found.Count -ne 1) { throw "missing production function $name" }
            . ([scriptblock]::Create($found[0].Extent.Text))
        }
        function Invoke-Phase5BridgeReadinessPreflight {
            param($RepoRoot, $RunRoot, $ResolvedExePath, $ArtifactName)
            $script:Calls++
            return [pscustomobject]@{
                Passed = $script:Pass
                ArtifactDir = Join-Path $RunRoot 'mock-preflight'
                FailureClass = if ($script:Pass) { '' } else { 'preflight_helpee_invite_bootstrap' }
                FailureReason = if ($script:Pass) { '' } else { 'helpee_invite_readiness_timeout' }
                FailureSubreason = $script:Subreason
                ExitCode = if ($script:Pass) { 0 } else { 1 }
            }
        }
        function Get-RouteAcceptanceAppProcessesForExePath { param($ResolvedExePath) return @() }
        function Write-RouteAcceptanceProcessCleanupArtifacts { param($ArtifactDir, $Reason, $Before, $After) }
        function Assert-RouteAcceptanceCleanAppProcessState { throw 'unexpected process cleanup' }
        function Start-Sleep { param($Seconds) }
        function Test-RouteAcceptanceEnvEnabled { param($Name) return $script:Skip }
        function Complete-RouteAcceptanceRunEvidence { param($RepoRoot, $RunRoot, $ScenarioNames) }
        function Write-Phase5PreflightFailureSummaryFiles {
            param($RunRoot, $BaselinePath, $PreflightResult)
            if ($script:Calls -ne 1 -or $PreflightResult.Passed) { throw 'strict global failure was retried or accepted' }
            Write-Output 'preflight-failure-finalized'
        }
        function Invoke-Phase4RouteAcceptanceScenario { Write-Output 'scenario-launch' }

        $root = Join-Path ([System.IO.Path]::GetTempPath()) ('nlink-strict-preflight-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $root | Out-Null
        $script:Pass = $false
        $script:Subreason = ''
        $script:Skip = $false
        $script:Calls = 0
        try {
            if ($Mode -eq 'global') {
                $runRoot = $root
                $resolvedExePath = ''
                $requestedScenarioNames = @('regular-v4-live-activation-off-on-off-256mb')
                $scenarios = @([pscustomobject]@{ Name = $requestedScenarioNames[0] })
                $baseline = [pscustomobject]@{ Path = 'unused' }
                $shouldRunPhase5Preflight = $true
                $globalBranch = @($ast.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.IfStatementAst] -and
                    $node.Clauses[0].Item1.Extent.Text -eq '$shouldRunPhase5Preflight'
                }, $true))
                if ($globalBranch.Count -ne 1) { throw 'global preflight branch not unique' }
                . ([scriptblock]::Create($globalBranch[0].Extent.Text))
                Invoke-Phase4RouteAcceptanceScenario
                return
            }

            Remove-Variable -Name StrictCampaignPreflight
            $runRoot = Join-Path $root 'unbound-policy'
            New-Item -ItemType Directory -Path $runRoot | Out-Null
            $result = Invoke-Phase5BridgeReadinessPreflightWithRetry -RepoRoot $RepoRoot -RunRoot $runRoot
            $metadata = Get-Content -LiteralPath (Join-Path $runRoot 'phase5-bridge-readiness-preflight-attempts.json') -Raw | ConvertFrom-Json
            if ($script:Calls -ne 2 -or $metadata.strictCampaignPreflight -or
                -not (Test-Phase5BridgeReadinessPreflightQuarantinableFailure -PreflightResult $result)) {
                throw 'unbound campaign policy must retain default retry and quarantine behavior'
            }

            foreach ($strictValue in @($false, $true)) {
                $StrictCampaignPreflight = $strictValue
                foreach ($special in @($false, $true)) {
                    foreach ($pass in @($false, $true)) {
                        $script:Calls = 0
                        $script:Pass = $pass
                        $script:Subreason = if ($special) { 'helpee_invite_host_before_bridge_ready' } else { '' }
                        $runRoot = Join-Path $root ([guid]::NewGuid().ToString('N'))
                        New-Item -ItemType Directory -Path $runRoot | Out-Null
                        $result = Invoke-Phase5BridgeReadinessPreflightWithRetry -RepoRoot $RepoRoot -RunRoot $runRoot
                        $expected = if ($strictValue -or $pass) { 1 } elseif ($special) { 3 } else { 2 }
                        if ($script:Calls -ne $expected -or $result.AttemptCount -ne $expected -or
                            $result.SelectedAttempt -ne $expected -or $result.RetryUsed -ne ($expected -gt 1) -or
                            $result.Passed -ne $pass) { throw 'preflight retry policy changed' }
                        $metadata = Get-Content -LiteralPath (Join-Path $runRoot 'phase5-bridge-readiness-preflight-attempts.json') -Raw | ConvertFrom-Json
                        if ($metadata.strictCampaignPreflight -ne $strictValue -or $metadata.attemptCount -ne $expected) {
                            throw 'preflight metadata does not describe actual policy'
                        }
                    }
                }
                $script:Pass = $false
                $script:Subreason = ''
                $script:Calls = 0
                $scenario = [pscustomobject]@{ Name = 'regular-v4-live-activation-off-on-off-256mb' }
                $result = Invoke-Phase5ScenarioIsolationPreflight -Scenario $scenario -RepoRoot $RepoRoot -RunRoot $root -FakeMode $false -AttemptIndex 1
                if ($script:Calls -ne 1 -or $result.Passed -ne (-not $strictValue) -or
                    $result.Quarantined -ne (-not $strictValue) -or $result.ExitCode -ne 1) {
                    throw 'scenario isolation hid or changed a preflight failure'
                }

                $manifest = [pscustomobject]@{ schemaVersion = 1 }
                $failureManifest = [pscustomobject]@{ schemaVersion = 1 }
                $metadataStatements = @($ast.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.PipelineAst] -and
                    $node.Extent.Text -match '^\$(manifest|failureManifest) \| Add-Member -NotePropertyName strictCampaignPreflight '
                }, $true))
                if ($metadataStatements.Count -ne 2) { throw 'both manifest paths must record campaign policy' }
                foreach ($statement in $metadataStatements) { . ([scriptblock]::Create($statement.Extent.Text)) }
                foreach ($value in @($manifest, $failureManifest)) {
                    $roundTrip = $value | ConvertTo-Json -Compress | ConvertFrom-Json
                    if ($roundTrip.strictCampaignPreflight -ne $strictValue -or $roundTrip.schemaVersion -ne 1) {
                        throw 'manifest policy serialization changed'
                    }
                }
            }

            $guard = @($ast.FindAll({ param($node)
                $node -is [System.Management.Automation.Language.IfStatementAst] -and
                $node.Clauses[0].Item1.Extent.Text -match '^\$StrictCampaignPreflight -and' -and
                $node.Extent.Text -match 'requires live Phase 5 execution'
            }, $true))
            if ($guard.Count -ne 1) { throw 'strict bypass rejection must be unique' }
            foreach ($strictValue in @($false, $true)) {
                foreach ($shape in @('live', 'skip', 'fake', 'wrong-matrix')) {
                    $StrictCampaignPreflight = $strictValue
                    $MatrixMode = if ($shape -eq 'wrong-matrix') { 'legacy' } else { 'phase5-analyzer-gui-acceptance' }
                    $fakeMode = $shape -eq 'fake'
                    $script:Skip = $shape -eq 'skip'
                    $rejected = $false
                    try { . ([scriptblock]::Create($guard[0].Extent.Text)) }
                    catch {
                        if ($_.Exception.Message -notlike 'StrictCampaignPreflight requires live Phase 5*') { throw }
                        $rejected = $true
                    }
                    if ($rejected -ne ($strictValue -and $shape -ne 'live')) { throw 'strict bypass guard changed default behavior' }
                }
            }
            $source = $ast.Extent.Text
            foreach ($name in @('manifest', 'failureManifest')) {
                $metadataIndex = $source.IndexOf('$' + $name + ' | Add-Member -NotePropertyName strictCampaignPreflight')
                $write = if ($name -eq 'manifest') { 'Set-RouteAcceptanceEvidenceManifest -Path $manifestPath -Manifest $manifest' } else { 'Write-RouteAcceptanceEvidenceManifest -Path $manifestPath -Manifest $failureManifest' }
                if ($metadataIndex -lt 0 -or $metadataIndex -gt $source.IndexOf($write)) { throw 'manifest policy must precede hash/write' }
            }
            if ($source -notmatch 'strict_campaign_preflight=\{0\}' -or
                $source -notmatch 'strictCampaignPreflight = \[bool\]\(Get-Variable') { throw 'top-level summary policy missing' }
            Write-Output 'strict-campaign-contracts-ok'
        }
        finally {
            [System.IO.Directory]::Delete($root, $true)
        }
        """;
}
