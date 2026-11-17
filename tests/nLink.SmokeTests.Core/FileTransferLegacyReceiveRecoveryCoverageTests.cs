namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "RetainedRuntimeUnlock")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferLegacyReceiveRecoveryCoverageTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    public async Task LegacyReceiveRecoveryCoverage_GenerationStringsAliasesAndOrMatchingEqualBaseline()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            foreach ($value in @('0', '00', '0.0', '+0', '-0', '-1', '', ' ', 'none', '(none)', '(missing)', '__missing__', '7', $null)) {
                $operation = New-TestEvent 'operation' @{ bridge_decision_generation=$value; recovery_liveness_lease_generation=$value } -Issued
                $decision = New-TestEvent 'decision' @{ bridge_decision_generation=$value }
                $lease = New-TestEvent 'lease' @{ regular_v4_liveness_generation=$value }
                $both = New-TestEvent 'both' @{ bridge_decision_generation=$value; regular_v4_liveness_generation=$value }
                $decisionOr = New-TestEvent 'decision-or' @{ bridge_decision_generation=$value; regular_v4_liveness_generation='conflict' }
                $leaseOr = New-TestEvent 'lease-or' @{ bridge_decision_generation='conflict'; regular_v4_liveness_generation=$value }
                Assert-Equivalent @($decision, $lease, $both, $decisionOr, $leaseOr) @($operation)
            }
            $operation = New-TestEvent 'operation' @{ bridge_decision_generation='7'; recovery_liveness_lease_generation='8' } -Issued
            $cases = @(
                (New-TestEvent 'decision-alias' @{ execution_command_generation='7' } -Missing @('bridge_decision_generation')),
                (New-TestEvent 'lease-alias' @{ recovery_liveness_lease_generation='8' } -Missing @('regular_v4_liveness_generation')),
                (New-TestEvent 'primary-decision-zero' @{ bridge_decision_generation='0'; execution_command_generation='7' }),
                (New-TestEvent 'primary-lease-zero' @{ regular_v4_liveness_generation='0'; recovery_liveness_lease_generation='8' }),
                (New-TestEvent 'primary-decision-conflict' @{ bridge_decision_generation='9'; execution_command_generation='7' }),
                (New-TestEvent 'primary-lease-conflict' @{ regular_v4_liveness_generation='9'; recovery_liveness_lease_generation='8' }),
                (New-TestEvent 'primary-decision-wins' @{ bridge_decision_generation='7'; execution_command_generation='9' }),
                (New-TestEvent 'primary-lease-wins' @{ regular_v4_liveness_generation='8'; recovery_liveness_lease_generation='9' }),
                (New-TestEvent 'missing-generations' @{} -Missing @('bridge_decision_generation', 'regular_v4_liveness_generation')),
                (New-TestEvent 'decision-or' @{ bridge_decision_generation='7'; regular_v4_liveness_generation='99' }),
                (New-TestEvent 'lease-or' @{ bridge_decision_generation='99'; regular_v4_liveness_generation='8' }))
            Assert-Equivalent $cases @($operation)
            $retained = @(Remove-FileTransferLegacyReceiveRecoveryEventsCoveredByOperations -Events $cases -OperationEvents @($operation))
            if (($retained.TestId -join ',') -cne 'primary-decision-zero,primary-lease-zero,primary-decision-conflict,primary-lease-conflict,missing-generations') { throw 'alias precedence or OR semantics changed' }
            $operationAliasOnly = New-TestEvent 'operation-alias-only' @{ execution_command_generation='7'; regular_v4_liveness_generation='8' } -Issued -Missing @('bridge_decision_generation', 'recovery_liveness_lease_generation')
            Assert-Equivalent @($cases[0], $cases[1]) @($operationAliasOnly)
            Write-Output 'legacy-coverage-ok'
            """);
    }

    [Fact]
    public async Task LegacyReceiveRecoveryCoverage_ScopeOrderDuplicatesAndNullsEqualBaseline()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $operation = New-TestEvent 'operation' @{ bridge_decision_generation='7'; recovery_liveness_lease_generation='8' } -Issued
            $events = New-Object System.Collections.Generic.List[object]
            foreach ($field in @('session_id', 'transfer_id', 'direction') + $scopeFields) {
                $mismatch = @{ bridge_decision_generation='7' }
                $mismatch[$field] = 'different'
                $events.Add((New-TestEvent ('mismatch-' + $field) $mismatch)) | Out-Null
                foreach ($value in @('', ' ', 'none', '(none)', $null)) {
                    $optional = @{ bridge_decision_generation='7' }
                    $optional[$field] = $value
                    $events.Add((New-TestEvent ('optional-' + $field) $optional)) | Out-Null
                }
                $events.Add((New-TestEvent ('missing-' + $field) @{ bridge_decision_generation='7' } -Missing @($field))) | Out-Null
            }
            $events.Add((New-TestEvent 'case-insensitive' @{ session_id='SESSION'; transfer_id='TRANSFER'; direction='INBOUND'; route='POST_TUNA_FALLBACK_V6'; bridge_decision_generation='7' })) | Out-Null
            $events.Add((New-TestEvent 'zero-matching-scope' @{})) | Out-Null
            $events.Add((New-TestEvent 'zero-different-scope' @{ session_id='other'; transfer_id='other' })) | Out-Null
            $unrelatedName = New-TestEvent 'unrelated-name' @{ bridge_decision_generation='7'; log_writer_id='unrelated-writer' }
            $unrelatedName.EventName = 'unrelated_diagnostic'
            $unrelatedName.Sequence = -10L
            $events.Add($unrelatedName) | Out-Null
            $duplicate = New-TestEvent 'duplicate-retained' @{}
            $events.Insert(0, $duplicate)
            $events.Add($duplicate) | Out-Null
            $events.Add($null) | Out-Null
            $inputEvents = [object[]]$events.ToArray()
            Assert-Equivalent $inputEvents @($operation, $operation)
            [array]::Reverse($inputEvents)
            Assert-Equivalent $inputEvents @($operation)
            Assert-Equivalent @() @($operation)
            Assert-Equivalent @($duplicate) @($operation)
            Assert-Equivalent @($null) @($operation)
            Assert-Equivalent @($null, $duplicate, $null, $duplicate) @()
            Assert-Equivalent @($duplicate) @($null)
            Assert-Equivalent @($duplicate) @($operation, $null)
            Assert-Equivalent $null $null
            Write-Output 'legacy-coverage-ok'
            """);
    }

    [Fact]
    public async Task LegacyReceiveRecoveryCoverage_BothLiteralZeroSkipsScopeAndReadsGenerationsOnce()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $operations = @(1..8 | ForEach-Object {
                New-TestEvent ("operation-$_") @{ bridge_decision_generation=[string](100 + $_); recovery_liveness_lease_generation=[string](200 + $_) } -Issued
            })
            $zero = New-TestEvent 'candidate-zero' @{}
            $defaultZero = New-TestEvent 'candidate-default' @{} -Missing @('bridge_decision_generation', 'regular_v4_liveness_generation')
            $nonzero = New-TestEvent 'candidate-nonzero' @{ bridge_decision_generation='7'; regular_v4_liveness_generation='8' }
            $script:FieldReads.Clear()
            $script:TrackReads = $true
            try { $retained = @(Remove-FileTransferLegacyReceiveRecoveryEventsCoveredByOperations -Events @($zero, $defaultZero, $nonzero) -OperationEvents $operations) }
            finally { $script:TrackReads = $false }
            Assert-References @($zero, $defaultZero, $nonzero) $retained
            foreach ($candidate in @($zero, $defaultZero, $nonzero)) {
                foreach ($field in $candidateGenerationFields) {
                    if ((Read-Count $candidate.TestId $field) -ne 1) { throw "candidate generation was not read once: $($candidate.TestId)/$field" }
                }
            }
            foreach ($candidate in @($zero, $defaultZero)) {
                foreach ($field in @('session_id', 'transfer_id', 'direction') + $scopeFields) {
                    if ((Read-Count $candidate.TestId $field) -ne 0) { throw "both-zero candidate entered scope matching: $field" }
                }
            }
            if ((Read-Count $nonzero.TestId 'session_id') -ne $operations.Count) { throw 'nonzero candidate did not retain the existing operation loop' }
            foreach ($value in @('00', '0.0', '+0', '-0', '', 'none', '(none)')) {
                $candidate = New-TestEvent 'noncanonical-zero' @{ bridge_decision_generation=$value }
                $script:FieldReads.Clear()
                $script:TrackReads = $true
                try { $null = @(Remove-FileTransferLegacyReceiveRecoveryEventsCoveredByOperations -Events @($candidate) -OperationEvents $operations) }
                finally { $script:TrackReads = $false }
                if ((Read-Count $candidate.TestId 'session_id') -ne $operations.Count) { throw "noncanonical generation was numerically normalized: '$value'" }
                foreach ($field in $candidateGenerationFields) {
                    if ((Read-Count $candidate.TestId $field) -ne 1) { throw 'noncanonical generation was reread per operation' }
                }
            }
            $script:FieldReads.Clear()
            $script:TrackReads = $true
            try { $null = @(Remove-FileTransferLegacyReceiveRecoveryEventsCoveredByOperations -Events @($zero, $defaultZero) -OperationEvents $operations) }
            finally { $script:TrackReads = $false }
            foreach ($operation in $operations) {
                foreach ($field in @('session_id', 'transfer_id', 'direction', 'bridge_decision_generation', 'recovery_liveness_lease_generation') + $scopeFields) {
                    if ((Read-Count $operation.TestId $field) -ne 0) { throw 'both-zero input caused operation scope/generation reads' }
                }
            }
            Write-Output 'legacy-coverage-ok'
            """);
    }

    [Fact]
    public async Task LegacyReceiveRecoveryCoverage_NoQualifyingIssuedOperationDoesNotReadCandidates()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $candidate = New-TestEvent 'candidate' @{ bridge_decision_generation='7'; regular_v4_liveness_generation='8' }
            $invalid = @(
                (New-TestEvent 'wrong-name' @{}),
                (New-TestEvent 'missing-authority' @{} -Issued -Missing @('receive_recovery_operation_authority_id')),
                (New-TestEvent 'blank-observation' @{ observation_identity=' ' } -Issued),
                (New-TestEvent 'missing-observation' @{} -Issued -Missing @('observation_identity')),
                (New-TestEvent 'zero-generation' @{ receive_recovery_operation_generation='0' } -Issued),
                (New-TestEvent 'negative-generation' @{ receive_recovery_operation_generation='-1' } -Issued),
                (New-TestEvent 'invalid-generation' @{ receive_recovery_operation_generation='not-a-number' } -Issued))
            foreach ($operations in @([pscustomobject]@{ Items=[object[]]@() }, [pscustomobject]@{ Items=[object[]]$invalid })) {
                Assert-Equivalent @($candidate, $candidate) $operations.Items
                $script:FieldReads.Clear()
                $script:TrackReads = $true
                try { $retained = @(Remove-FileTransferLegacyReceiveRecoveryEventsCoveredByOperations -Events @($candidate, $candidate) -OperationEvents $operations.Items) }
                finally { $script:TrackReads = $false }
                Assert-References @($candidate, $candidate) $retained
                if (@($script:FieldReads.Keys | Where-Object { $_ -like 'candidate|*' }).Count -ne 0) { throw 'no-issued fast path read candidate fields' }
            }
            foreach ($generation in @('01', '+1', '1.0', '(none)', $null)) {
                $operation = New-TestEvent 'operation' @{ receive_recovery_operation_generation=$generation; bridge_decision_generation='7' } -Issued
                Assert-Equivalent @($candidate) @($operation)
            }
            Write-Output 'legacy-coverage-ok'
            """);
    }

    private static async Task RunHarnessAsync(string body)
    {
        var result = await RunPowerShellScriptTextAsync(HarnessPrefix + BaselineFilter + Environment.NewLine + body, [FindRepoRoot()]);
        Assert.True(result.ExitCode == 0, $"STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
        Assert.Equal("legacy-coverage-ok", result.Stdout.Trim());
    }

    private const string HarnessPrefix = """
        param([string]$RepoRoot)
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'
        if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1) { throw 'Windows PowerShell 5.1 required' }
        . (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
        $tokens = $null
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/FileTransferSoak/ArtifactWriters.ps1'), [ref]$tokens, [ref]$errors)
        if ($errors -and $errors.Count -ne 0) { throw ($errors | Out-String) }
        $definition = @($ast.FindAll({ param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -ceq 'Remove-FileTransferLegacyReceiveRecoveryEventsCoveredByOperations'
        }, $true))
        if ($definition.Count -ne 1) { throw 'production compatibility filter is not unique' }
        . ([scriptblock]::Create($definition[0].Extent.Text))
        $scopeFields = @('route', 'protocol_version', 'live_route_epoch', 'route_publication_generation', 'runtime_generation',
            'terminal_generation', 'leg_generation', 'transport_epoch', 'bridge_recovery_generation', 'data_session_generation')
        $candidateGenerationFields = @('bridge_decision_generation', 'execution_command_generation', 'regular_v4_liveness_generation', 'recovery_liveness_lease_generation')
        $script:TrackReads = $false
        $script:FieldReads = @{}
        $script:RealFieldGetter = (Get-Command Get-FileTransferEventField -CommandType Function).ScriptBlock
        function Get-FileTransferEventField {
            param($Event, [Parameter(Mandatory=$true)][string]$Name, [string]$Default='')
            if ($script:TrackReads) {
                $id = if ($null -eq $Event) { '(null)' } else { [string]$Event.TestId }
                $key = $id + '|' + $Name
                if (-not $script:FieldReads.ContainsKey($key)) { $script:FieldReads[$key] = 0 }
                $script:FieldReads[$key]++
            }
            & $script:RealFieldGetter -Event $Event -Name $Name -Default $Default
        }
        function Read-Count([string]$Id, [string]$Name) {
            $key = $Id + '|' + $Name
            if ($script:FieldReads.ContainsKey($key)) { return $script:FieldReads[$key] }
            return 0
        }
        function New-TestEvent([string]$Id, [hashtable]$Overrides, [switch]$Issued, [string[]]$Missing=@()) {
            $fields = @{
                session_id='session'; transfer_id='transfer'; direction='inbound'; route='post_tuna_fallback_v6'; protocol_version='6';
                live_route_epoch='1'; route_publication_generation='2'; runtime_generation='3'; terminal_generation='0'; leg_generation='4';
                transport_epoch='5'; bridge_recovery_generation='6'; data_session_generation='7';
                bridge_decision_generation='0'; regular_v4_liveness_generation='0'; recovery_liveness_lease_generation='0'
                receive_recovery_operation_authority_id='operation-authority'; observation_identity='observation'; receive_recovery_operation_generation='1'
            }
            foreach ($name in $Overrides.Keys) { $fields[$name] = $Overrides[$name] }
            foreach ($name in $Missing) { $fields.Remove($name) }
            return [pscustomobject]@{ TestId=$Id; Fields=$fields; Sequence=100L; EventName=$(if ($Issued) { 'filetransfer_receive_recovery_operation_issued' } else { 'legacy-diagnostic' }) }
        }
        function Assert-References([AllowNull()][AllowEmptyCollection()][object[]]$Expected, [AllowNull()][AllowEmptyCollection()][object[]]$Actual) {
            if ($Expected.Count -ne $Actual.Count) { throw "retained event count changed: $($Expected.Count) != $($Actual.Count)" }
            for ($i = 0; $i -lt $Expected.Count; $i++) {
                if (-not [object]::ReferenceEquals($Expected[$i], $Actual[$i])) { throw "retained event reference/order changed at $i" }
            }
        }
        function Assert-Equivalent([AllowNull()][AllowEmptyCollection()][object[]]$Events, [AllowNull()][AllowEmptyCollection()][object[]]$Operations) {
            $before = @($Events, $Operations) | ConvertTo-Json -Depth 8 -Compress
            $expectedError = $null
            $actualError = $null
            try { $expected = @(Invoke-LegacyCoverageBaseline -Events $Events -OperationEvents $Operations) }
            catch { $expectedError = $_ }
            try { $actual = @(Remove-FileTransferLegacyReceiveRecoveryEventsCoveredByOperations -Events $Events -OperationEvents $Operations) }
            catch { $actualError = $_ }
            if (($null -eq $expectedError) -ne ($null -eq $actualError)) { throw 'null/invalid input exception behavior changed' }
            if ($null -ne $expectedError) {
                if ($expectedError.Exception.GetBaseException().GetType() -ne $actualError.Exception.GetBaseException().GetType() -or
                    $expectedError.Exception.GetBaseException().Message -cne $actualError.Exception.GetBaseException().Message) { throw 'null/invalid input exception changed' }
            }
            else { Assert-References $expected $actual }
            if ((@($Events, $Operations) | ConvertTo-Json -Depth 8 -Compress) -cne $before) { throw 'compatibility filtering mutated input events' }
        }

        """;

    // Frozen pre-fast-path predicate. Keep its matching rules independent of production edits.
    private const string BaselineFilter = """
        function Invoke-LegacyCoverageBaseline {
            param([AllowEmptyCollection()][object[]]$Events=@(), [AllowEmptyCollection()][object[]]$OperationEvents=@())
            [object[]]$issued = @($OperationEvents | Where-Object {
                $_.EventName -eq 'filetransfer_receive_recovery_operation_issued' -and
                -not [string]::IsNullOrWhiteSpace((Get-FileTransferEventField -Event $_ -Name 'receive_recovery_operation_authority_id' -Default '')) -and
                -not [string]::IsNullOrWhiteSpace((Get-FileTransferEventField -Event $_ -Name 'observation_identity' -Default '')) -and
                (Get-FileTransferEventInt64Field -Event $_ -Name 'receive_recovery_operation_generation' -Default 0) -gt 0
            })
            if ($issued.Count -eq 0) { return @($Events) }
            [object[]]$filtered = @(
                foreach ($candidate in @($Events)) {
                    $covered = $false
                    foreach ($operation in $issued) {
                        $sessionMatches = (Get-FileTransferEventField -Event $candidate -Name 'session_id' -Default '') -eq (Get-FileTransferEventField -Event $operation -Name 'session_id' -Default '')
                        $transferMatches = (Get-FileTransferEventField -Event $candidate -Name 'transfer_id' -Default '') -eq (Get-FileTransferEventField -Event $operation -Name 'transfer_id' -Default '')
                        if (-not $sessionMatches -or -not $transferMatches) { continue }
                        $candidateDirection = Get-FileTransferEventField -Event $candidate -Name 'direction' -Default ''
                        $operationDirection = Get-FileTransferEventField -Event $operation -Name 'direction' -Default ''
                        if (-not [string]::IsNullOrWhiteSpace($candidateDirection) -and $candidateDirection -ne $operationDirection) { continue }
                        $scopeMismatch = $false
                        foreach ($scopeField in @('route', 'protocol_version', 'live_route_epoch', 'route_publication_generation',
                                'runtime_generation', 'terminal_generation', 'leg_generation', 'transport_epoch', 'bridge_recovery_generation', 'data_session_generation')) {
                            $candidateScopeValue = Get-FileTransferEventField -Event $candidate -Name $scopeField -Default ''
                            if ([string]::IsNullOrWhiteSpace($candidateScopeValue) -or $candidateScopeValue -in @('(none)', 'none')) { continue }
                            $operationScopeValue = Get-FileTransferEventField -Event $operation -Name $scopeField -Default ''
                            if (-not [string]::Equals($candidateScopeValue, $operationScopeValue, [System.StringComparison]::OrdinalIgnoreCase)) { $scopeMismatch=$true; break }
                        }
                        if ($scopeMismatch) { continue }
                        $candidateBridgeDecision = Get-FileTransferEventField -Event $candidate -Name 'bridge_decision_generation' -Default (
                            Get-FileTransferEventField -Event $candidate -Name 'execution_command_generation' -Default '0')
                        $operationBridgeDecision = Get-FileTransferEventField -Event $operation -Name 'bridge_decision_generation' -Default '0'
                        $candidateLeaseGeneration = Get-FileTransferEventField -Event $candidate -Name 'regular_v4_liveness_generation' -Default (
                            Get-FileTransferEventField -Event $candidate -Name 'recovery_liveness_lease_generation' -Default '0')
                        $operationLeaseGeneration = Get-FileTransferEventField -Event $operation -Name 'recovery_liveness_lease_generation' -Default '0'
                        $exactDecision = $candidateBridgeDecision -ne '0' -and $candidateBridgeDecision -eq $operationBridgeDecision
                        $exactLease = $candidateLeaseGeneration -ne '0' -and $candidateLeaseGeneration -eq $operationLeaseGeneration
                        if ($exactDecision -or $exactLease) { $covered=$true; break }
                    }
                    if (-not $covered) { $candidate }
                }
            )
            return $filtered
        }
        """;
}
