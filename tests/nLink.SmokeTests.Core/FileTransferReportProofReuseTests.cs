namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "ScriptSurface")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferReportProofReuseTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task ReportProofHandoff_WriterReusesAllSixExactProofsOnceWithoutOutputChanges(int eventCount)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $summary = New-TestSummary ([int]$Case)
            $beforeProperties = @($summary.PSObject.Properties.Name)
            $actual = Invoke-TestWriter -Summary $summary
            Assert-Calls 1
            foreach ($key in $script:ProofKeys) {
                if (-not [object]::ReferenceEquals($actual.Proofs.$key, $script:LastProofs[$key])) { throw "writer copied/recomputed $key" }
                Assert-NoPrivateProperties $actual.Proofs.$key
            }
            if (-not [object]::ReferenceEquals($actual.Classification, $script:TestClassification)) { throw 'classification changed' }
            if ($null -eq $actual.Handoff -or -not [object]::ReferenceEquals($actual.Handoff.Summary, $summary)) { throw 'writer lost exact summary handoff' }
            $resolved = Resolve-FileTransferReportProofHandoff -Summary $summary -Handoff $actual.Handoff
            if (-not [object]::ReferenceEquals($resolved, $actual.Handoff.Proofs)) { throw 'resolver copied proof container' }
            $json = $actual.Proofs | ConvertTo-Json -Depth 20 -Compress
            $baseline = Invoke-TestFreshProofs -Summary (New-TestSummary ([int]$Case))
            if (($baseline | ConvertTo-Json -Depth 20 -Compress) -cne $json) { throw 'reuse changed complete proof JSON' }
            Assert-Calls 2
            Assert-NoPrivateProperties $actual.Classification
            Assert-NoPrivateProperties $summary
            $added = @($summary.PSObject.Properties.Name | Where-Object { $_ -notin $beforeProperties })
            if ($added.Count -ne 1 -or $added[0] -cne '__FileTransferRecoveryFailureClassification_None') { throw 'handoff added summary state' }
            Write-Output 'report-proof-reuse-ok'
            """, eventCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task ReportProofHandoff_CachedClassifierClearsOutReferenceAndWriterComputesFallbacks()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $first = New-TestSummary 1
            $stale = $null
            $null = Get-FileTransferRecoveryFailureClassification -Summary $first -ReportProofHandoff ([ref]$stale)
            if ($null -eq $stale) { throw 'first handoff absent' }
            Assert-Calls 1
            $cached = Get-FileTransferRecoveryFailureClassification -Summary $first -ReportProofHandoff ([ref]$stale)
            if ($null -ne $stale -or -not [object]::ReferenceEquals($cached, $script:TestClassification)) { throw 'cached return retained stale outref' }
            Assert-Calls 1
            $written = Invoke-TestWriter -Summary $first
            if ($null -ne $written.Handoff) { throw 'cached writer fabricated a handoff' }
            Assert-Calls 2
            foreach ($key in $script:ProofKeys) {
                if (-not [object]::ReferenceEquals($written.Proofs.$key, $script:LastProofs[$key])) { throw 'cached path skipped fresh fallback' }
            }
            Write-Output 'report-proof-reuse-ok'
            """);
    }

    [Fact]
    public async Task ReportProofHandoff_CollectionIdentityPresenceAndOrdinalMutationRejectReuse()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $mutations = @(
                { param($s) $s.AllEvents = [object[]]@($s.AllEvents) },
                { param($s) $s.GlobalEvents = [object[]]@($s.GlobalEvents) },
                { param($s) $s.TransferEvents = [object[]]@($s.TransferEvents) },
                { param($s) $a = $s.AllEvents[0]; $s.AllEvents[0] = $s.AllEvents[1]; $s.AllEvents[1] = $a },
                { param($s) $s.TransferEvents[0] = [pscustomobject]@{ RawLine = $s.TransferEvents[0].RawLine; Sequence = $s.TransferEvents[0].Sequence } },
                { param($s) $s.PSObject.Properties.Remove('AllEvents') },
                { param($s) $s.PSObject.Properties.Remove('GlobalEvents') },
                { param($s) $s.TransferEvents = [object[]]@() })
            foreach ($mutation in $mutations) {
                $summary = New-TestSummary 3
                $handoff = $null
                $null = Get-FileTransferRecoveryFailureClassification -Summary $summary -ReportProofHandoff ([ref]$handoff)
                & $mutation $summary
                if ($null -ne (Resolve-FileTransferReportProofHandoff -Summary $summary -Handoff $handoff)) { throw 'mutated collection reused proof handoff' }
            }
            $summary = New-TestSummary 3
            $handoff = $null
            $null = Get-FileTransferRecoveryFailureClassification -Summary $summary -ReportProofHandoff ([ref]$handoff)
            $other = [pscustomobject]@{ AllEvents = $summary.AllEvents; GlobalEvents = $summary.GlobalEvents; TransferEvents = $summary.TransferEvents }
            if ($null -ne (Resolve-FileTransferReportProofHandoff -Summary $other -Handoff $handoff)) { throw 'different summary shared exact collections' }
            $script:AfterClassifier = { param($s, $h) $s.AllEvents = [object[]]@($s.AllEvents) }
            Reset-TestCalls
            $result = Invoke-TestWriter -Summary (New-TestSummary 3)
            Assert-Calls 2
            Write-Output 'report-proof-reuse-ok'
            """);
    }

    [Fact]
    public async Task ReportProofHandoff_MissingAndEmptyAllEventsRemainDistinctAndInvocationLocal()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $missing = New-TestSummary 0
            $missing.PSObject.Properties.Remove('AllEvents')
            $missingHandoff = $null
            $null = Get-FileTransferRecoveryFailureClassification -Summary $missing -ReportProofHandoff ([ref]$missingHandoff)
            if ($null -eq (Resolve-FileTransferReportProofHandoff -Summary $missing -Handoff $missingHandoff)) { throw 'unchanged absent collection failed identity' }
            $missing | Add-Member -NotePropertyName AllEvents -NotePropertyValue ([object[]]@())
            if ($null -ne (Resolve-FileTransferReportProofHandoff -Summary $missing -Handoff $missingHandoff)) { throw 'added empty collection reused absent input' }
            $empty = New-TestSummary 0
            $emptyHandoff = $null
            $null = Get-FileTransferRecoveryFailureClassification -Summary $empty -ReportProofHandoff ([ref]$emptyHandoff)
            $empty.PSObject.Properties.Remove('AllEvents')
            if ($null -ne (Resolve-FileTransferReportProofHandoff -Summary $empty -Handoff $emptyHandoff)) { throw 'removed empty collection reused present input' }
            Reset-TestCalls
            $one = Invoke-TestWriter -Summary (New-TestSummary 1)
            $two = Invoke-TestWriter -Summary (New-TestSummary 1)
            Assert-Calls 2
            if ([object]::ReferenceEquals($one.Handoff, $two.Handoff) -or
                [object]::ReferenceEquals($one.Proofs.FallbackTail, $two.Proofs.FallbackTail)) { throw 'two writer invocations shared private state' }
            if ($null -ne (Resolve-FileTransferReportProofHandoff -Summary $two.Handoff.Summary -Handoff $one.Handoff)) { throw 'cross-invocation handoff accepted' }
            Write-Output 'report-proof-reuse-ok'
            """);
    }

    [Fact]
    public async Task ReportProofHandoff_MissingIndividualProofFallsBackWithoutDiscardingOtherFive()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            foreach ($missingKey in $script:ProofKeys) {
                Reset-TestCalls
                $script:MissingKey = $missingKey
                $script:AfterClassifier = {
                    param($s, $h)
                    $h.Proofs.Remove($script:MissingKey)
                }
                $result = Invoke-TestWriter -Summary (New-TestSummary 1)
                foreach ($key in $script:ProofKeys) {
                    $expected = if ($key -ceq $missingKey) { 2 } else { 1 }
                    if ($script:Calls[$key] -ne $expected) { throw "wrong fallback count for $key when $missingKey absent" }
                    if (-not [object]::ReferenceEquals($result.Proofs.$key, $script:LastProofs[$key])) { throw 'proof object not preserved' }
                }
            }
            Reset-TestCalls
            $script:AfterClassifier = { param($s, $h) $h.Proofs.ControlPlane = $null }
            $result = Invoke-TestWriter -Summary (New-TestSummary 0)
            if ($script:Calls.ControlPlane -ne 2 -or $script:Calls.FallbackAuthority -ne 1) { throw 'null individual proof did not fall back narrowly' }
            Write-Output 'report-proof-reuse-ok'
            """);
    }

    [Fact]
    public async Task ReportProofHandoff_InvariantLedgerReusesFiveProofsAndStandaloneCallsFallBack()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $ledger = Find-TestFunction $script:ArtifactAst 'New-FileTransferInvariantLedgerObject'
            $statements = @($ledger.Body.EndBlock.Statements)
            $resolve = @($statements | Where-Object { $_.Extent.Text -match '=\s*Resolve-FileTransferReportProofHandoff\b' })
            $ledgerKeys = @($script:ProofKeys | Where-Object { $_ -cne 'FallbackTail' })
            $ledgerVariables = @($ledgerKeys | ForEach-Object { $script:ProofVariables[$_] })
            $proofs = @($statements | Where-Object { (Get-TestAssignmentName $_) -in $ledgerVariables })
            if ($resolve.Count -ne 1 -or $proofs.Count -ne 5 -or
                @($statements | Where-Object { (Get-TestAssignmentName $_) -ceq 'fallbackTailProof' }).Count -ne 0) { throw 'ledger consumer inventory changed' }
            # Retain the production parameter defaults, resolver, and all five guarded
            # assignments without executing unrelated ledger construction.
            $body = "function New-FileTransferInvariantLedgerObject {`n" + $ledger.Body.ParamBlock.Extent.Text + "`n" +
                $resolve[0].Extent.Text + "`n" + (($proofs | ForEach-Object { $_.Extent.Text }) -join "`n") + "`n`$proofs = [ordered]@{}`n"
            foreach ($key in $ledgerKeys) { $body += "`$proofs['$key'] = `$$($script:ProofVariables[$key])`n" }
            $body += "return [pscustomobject]@{ Proofs = [pscustomobject]`$proofs; Handoff = `$ReportProofHandoff }`n}"
            . ([scriptblock]::Create($body))
            $writerCall = @($script:Writer.Body.EndBlock.Statements | Where-Object { (Get-TestAssignmentName $_) -ceq 'invariantLedger' })
            if ($writerCall.Count -ne 1 -or $writerCall[0].Extent.Text -notmatch '-ReportProofHandoff\s+\$reportProofHandoff\b') { throw 'writer did not forward the same handoff to ledger' }
            foreach ($count in @(0, 1)) {
                Reset-TestCalls
                $Summary = New-TestSummary $count
                $writer = Invoke-TestWriter -Summary $Summary
                Assert-Calls 1
                $reportProofHandoff = $writer.Handoff
                $ArtifactDir = 'ledger-proof-reuse-fixture'
                $GateResult = [pscustomobject]@{ Verdict = 'fail' }
                $LiveRouteProofMode = 'None'
                . ([scriptblock]::Create($writerCall[0].Extent.Text))
                Assert-Calls 1
                if (-not [object]::ReferenceEquals($invariantLedger.Handoff, $writer.Handoff)) { throw 'writer changed ledger handoff identity' }
                foreach ($key in $ledgerKeys) {
                    if (-not [object]::ReferenceEquals($invariantLedger.Proofs.$key, $writer.Proofs.$key)) { throw "ledger copied/recomputed $key" }
                }
                $json = $invariantLedger.Proofs | ConvertTo-Json -Depth 20 -Compress
                $standalone = New-FileTransferInvariantLedgerObject -ArtifactDir $ArtifactDir -Summary $Summary -GateResult $GateResult
                if ($null -ne $standalone.Handoff -or ($standalone.Proofs | ConvertTo-Json -Depth 20 -Compress) -cne $json) { throw 'standalone ledger default changed proof JSON' }
                foreach ($key in $script:ProofKeys) {
                    $expected = if ($key -ceq 'FallbackTail') { 1 } else { 2 }
                    if ($script:Calls[$key] -ne $expected) { throw 'standalone ledger fallback count changed' }
                }
                $foreign = New-FileTransferInvariantLedgerObject -ArtifactDir $ArtifactDir -Summary (New-TestSummary $count) -GateResult $GateResult -ReportProofHandoff $writer.Handoff
                if (($foreign.Proofs | ConvertTo-Json -Depth 20 -Compress) -cne $json) { throw 'wrong-summary fallback changed proof output' }
                foreach ($key in $script:ProofKeys) {
                    $expected = if ($key -ceq 'FallbackTail') { 1 } else { 3 }
                    if ($script:Calls[$key] -ne $expected) { throw 'wrong-summary ledger reused stale proof' }
                }
                $writer.Handoff.Proofs.Remove('ControlPlane')
                $partial = New-FileTransferInvariantLedgerObject -ArtifactDir $ArtifactDir -Summary $Summary -GateResult $GateResult -ReportProofHandoff $writer.Handoff
                foreach ($key in $script:ProofKeys) {
                    $expected = if ($key -ceq 'FallbackTail') { 1 } elseif ($key -ceq 'ControlPlane') { 4 } else { 3 }
                    if ($script:Calls[$key] -ne $expected) { throw 'partial ledger fallback was not per-proof' }
                }
                if (($partial.Proofs | ConvertTo-Json -Depth 20 -Compress) -cne $json) { throw 'partial fallback changed ledger proof JSON' }
                Assert-NoPrivateProperties $Summary
            }
            Write-Output 'report-proof-reuse-ok'
            """);
    }

    [Fact]
    public async Task ReportProofHandoff_IsPrivateAndOnlySuccessfulClassifierExecutionPublishesIt()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $summary = New-TestSummary 1
            $script:ThrowProof = 'ControlPlane'
            $handoff = [pscustomobject]@{ stale = $true }
            $threw = $false
            try { $null = Get-FileTransferRecoveryFailureClassification -Summary $summary -ReportProofHandoff ([ref]$handoff) }
            catch { $threw = $true }
            if (-not $threw -or $null -ne $handoff) { throw 'partial classifier execution published proof state' }
            $script:ThrowProof = ''
            $result = Invoke-TestWriter -Summary $summary
            Assert-NoPrivateProperties $summary
            Assert-NoPrivateProperties $result.Classification
            foreach ($key in $script:ProofKeys) { Assert-NoPrivateProperties $result.Proofs.$key }
            $without = New-TestSummary 0
            $plain = @(Get-FileTransferRecoveryFailureClassification -Summary $without)
            if ($plain.Count -ne 1 -or -not [object]::ReferenceEquals($plain[0], $script:TestClassification)) { throw 'optional handoff changed existing caller output' }
            Assert-NoPrivateProperties $without
            $resolver = Find-TestFunction $script:ArtifactAst 'Resolve-FileTransferReportProofHandoff'
            $handoffNodes = @($script:Classifier.Body.FindAll({ param($node)
                $node -is [System.Management.Automation.Language.VariableExpressionAst] -and
                $node.VariablePath.UserPath -match '^(script|global):.*[Rr]eport[Pp]roof'
            }, $true))
            if ($handoffNodes.Count -ne 0 -or $resolver.Extent.Text -match '\$(script|global):') { throw 'proof handoff escaped invocation scope' }
            $writerCommands = @($script:Writer.Body.FindAll({ param($node)
                $node -is [System.Management.Automation.Language.CommandAst] -and
                $node.GetCommandName() -ceq 'Resolve-FileTransferReportProofHandoff'
            }, $true))
            if ($writerCommands.Count -ne 1) { throw 'writer must resolve handoff exactly once' }
            Write-Output 'report-proof-reuse-ok'
            """);
    }

    private static async Task RunHarnessAsync(string body, string argument = "")
    {
        var result = await RunPowerShellScriptTextAsync(HarnessPrefix + Environment.NewLine + body, [FindRepoRoot(), argument]);
        Assert.True(result.ExitCode == 0, $"STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
        Assert.Equal("report-proof-reuse-ok", result.Stdout.Trim());
    }

    private const string HarnessPrefix = """
        param([string]$RepoRoot, [string]$Case)
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'
        if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1) { throw 'Windows PowerShell 5.1 required' }
        . (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
        . (Join-Path $RepoRoot 'tools/FileTransferSoak/ArtifactWriters.ps1')
        function Read-TestAst([string]$Path) {
            $tokens = $null; $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
            if ($errors -and $errors.Count -ne 0) { throw ($errors | Out-String) }
            return $ast
        }
        function Find-TestFunction($Ast, [string]$Name) {
            $matches = @($Ast.FindAll({ param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $Name
            }, $true))
            if ($matches.Count -ne 1) { throw "production function not unique: $Name" }
            return $matches[0]
        }
        function Get-TestAssignmentName($Statement) {
            if ($Statement -isnot [System.Management.Automation.Language.AssignmentStatementAst]) { return '' }
            $variables = @($Statement.Left.FindAll({ param($n) $n -is [System.Management.Automation.Language.VariableExpressionAst] }, $true))
            if ($variables.Count -ne 1) { throw 'assignment target ambiguous' }
            return $variables[0].VariablePath.UserPath
        }
        function Assert-NoPrivateProperties($Value) {
            foreach ($name in $Value.PSObject.Properties.Name) {
                if ($name -in @('ReportProofHandoff', 'ReportProofs', 'InputCollections', 'Proofs', 'Handoff')) { throw "private report state leaked: $name" }
            }
        }
        function New-TestSummary([int]$Count) {
            $events = New-Object System.Collections.Generic.List[object]
            for ($i = 0; $i -lt $Count; $i++) {
                $events.Add([pscustomobject]@{ RawLine = "event=test; ordinal=$i"; Sequence = [long]$i; Fields = @{ ordinal = [string]$i } }) | Out-Null
            }
            return [pscustomobject]@{ AllEvents = [object[]]$events.ToArray(); GlobalEvents = [object[]]@(); TransferEvents = [object[]]$events.ToArray() }
        }
        $script:ProofVariables = [ordered]@{
            FallbackAuthority = 'fallbackAuthorityProof'; FallbackTail = 'fallbackTailProof'; ControlPlane = 'controlPlaneProof'
            StaleRejectedActionEffect = 'staleRejectedActionEffectProof'; FallbackRuntimeUnlockSeparation = 'fallbackRuntimeUnlockSeparationProof'
            PublicationAuthorityFieldCoverage = 'publicationAuthorityFieldCoverageProof'
        }
        $script:ProofKeys = @($script:ProofVariables.Keys)
        $script:TestClassification = [pscustomobject]@{ Class = 'fixture_boundary'; Verdict = 'fail'; RawCount = 2; DistinctCount = 1; RecoveredCount = 0; UnresolvedCount = 1 }
        $script:AfterClassifier = $null; $script:ThrowProof = ''
        function Reset-TestCalls {
            $script:Calls = @{}; $script:LastProofs = @{}
            foreach ($key in $script:ProofKeys) { $script:Calls[$key] = 0 }
        }
        function Assert-Calls([int]$Expected) {
            foreach ($key in $script:ProofKeys) { if ($script:Calls[$key] -ne $Expected) { throw "expected $Expected calls for $key, got $($script:Calls[$key])" } }
        }
        function New-TestProof([string]$Key) {
            $script:Calls[$Key]++
            if ($script:ThrowProof -ceq $Key) { throw "expected fixture proof failure: $Key" }
            $proof = [pscustomobject]@{ Name = $Key; Verdict = 'fail'; RawCount = 2; DistinctCount = 1; RecoveredCount = 0; UnresolvedCount = 1
                Boundary = [pscustomobject]@{ Id = 'boundary-exact'; Writer = 'writer-test'; Sequence = 17; Receipt = 'receipt-exact' }
                CandidateEvents = [object[]]@([pscustomobject]@{ Sequence = 17; RawLine = 'exact candidate' }, [pscustomobject]@{ Sequence = 19; RawLine = 'duplicate candidate' }) }
            $script:LastProofs[$Key] = $proof
            return $proof
        }
        function Get-FileTransferFallbackLegAuthorityProof { param($TransferEvents) New-TestProof 'FallbackAuthority' }
        function Get-FileTransferFallbackTailReconciliationProof { param($Summary) New-TestProof 'FallbackTail' }
        function Get-FileTransferControlPlaneIsolationProof { param($Summary) New-TestProof 'ControlPlane' }
        function Get-FileTransferStaleRejectedActionEffectProof { param($Summary) New-TestProof 'StaleRejectedActionEffect' }
        function Get-FileTransferFallbackRuntimeUnlockSeparationProof { param($Summary) New-TestProof 'FallbackRuntimeUnlockSeparation' }
        function Get-FileTransferPublicationAuthorityFieldCoverageProof { param($Summary) New-TestProof 'PublicationAuthorityFieldCoverage' }
        Reset-TestCalls
        $script:ArtifactAst = Read-TestAst (Join-Path $RepoRoot 'tools/FileTransferSoak/ArtifactWriters.ps1')
        $script:Classifier = Find-TestFunction $script:ArtifactAst 'Get-FileTransferRecoveryFailureClassification'
        $script:Writer = Find-TestFunction $script:ArtifactAst 'Write-FileTransferDiagnosticsArtifacts'
        $classifierStatements = @($script:Classifier.Body.EndBlock.Statements)
        $firstEvents = @($classifierStatements | Where-Object { (Get-TestAssignmentName $_) -ceq 'events' } | Select-Object -First 1)
        if ($firstEvents.Count -ne 1) { throw 'classifier prefix boundary missing' }
        $prefix = $script:Classifier.Extent.Text.Substring(0, $firstEvents[0].Extent.StartOffset - $script:Classifier.Extent.StartOffset)
        if ($prefix -notmatch 'ReportProofHandoff' -or $prefix -notmatch 'InputCollections|reportProofInputCollections') { throw 'handoff identity capture not in classifier prefix' }
        $proofStatements = @($classifierStatements | Where-Object { (Get-TestAssignmentName $_) -in @($script:ProofVariables.Values) })
        if ($proofStatements.Count -ne 6) { throw 'classifier six proof assignments not unique' }
        $publication = @($classifierStatements | Where-Object {
            $_ -is [System.Management.Automation.Language.IfStatementAst] -and $_.Extent.Text -match 'ReportProofHandoff\.Value\s*=' -and $_.Extent.Text -match '\bProofs\s*='
        })
        $cache = @($classifierStatements | Where-Object { $_.Extent.Text -match 'Add-Member -NotePropertyName \$cachePropertyName -NotePropertyValue \$classification' })
        if ($publication.Count -ne 1 -or $cache.Count -ne 1) { throw 'successful classifier publish/cache statements missing' }
        $proofText = ($proofStatements | ForEach-Object { $_.Extent.Text }) -join "`n"
        # Keep real identity, early return, proof calls, handoff publication and cache statements;
        # replace only the unrelated full-classifier body and its large result construction.
        $classificationBody = $prefix + "`n" + $proofText + "`n`$classification = `$script:TestClassification`n" +
            $publication[0].Extent.Text + "`n" + $cache[0].Extent.Text + "`nreturn `$classification`n}"
        . ([scriptblock]::Create($classificationBody))
        $freshCapture = "`n`$proofs = [ordered]@{}`n"
        foreach ($key in $script:ProofKeys) { $freshCapture += "`$proofs['$key'] = `$$($script:ProofVariables[$key])`n" }
        $freshCapture += "return [pscustomobject]`$proofs`n"
        . ([scriptblock]::Create("function Invoke-TestFreshProofs { param(`$Summary)`n" + $proofText + $freshCapture + '}'))
        $writerStatements = @($script:Writer.Body.EndBlock.Statements)
        $call = @($writerStatements | Where-Object { (Get-TestAssignmentName $_) -ceq 'recoveryClassification' })
        if ($call.Count -ne 1 -or $call[0].Extent.Text -notmatch '-ReportProofHandoff\s+\(\[ref\]\$(\w+)\)') { throw 'writer must provide fresh outref' }
        $handoffName = $Matches[1]
        $initialization = @($writerStatements | Where-Object { (Get-TestAssignmentName $_) -ceq $handoffName })
        $resolve = @($writerStatements | Where-Object { $_.Extent.Text -match '=\s*Resolve-FileTransferReportProofHandoff\b' })
        $writerProofs = @($writerStatements | Where-Object { (Get-TestAssignmentName $_) -in @($script:ProofVariables.Values) })
        if ($initialization.Count -ne 1 -or $resolve.Count -ne 1 -or $writerProofs.Count -ne 6 -or
            $initialization[0].Extent.StartOffset -ge $call[0].Extent.StartOffset) { throw 'writer handoff selection statements not exact' }
        $writerText = "function Invoke-TestWriter { param(`$Summary)`n`$LiveRouteProofMode = 'None'`n`$CoordinatorSnapshotEvaluationHandoff = `$null`n`$StageObserver = `$null`n" +
            $initialization[0].Extent.Text + "`n" + $call[0].Extent.Text + "`n" +
            "if (`$null -ne `$script:AfterClassifier) { & `$script:AfterClassifier `$Summary `$$handoffName }`n" +
            $resolve[0].Extent.Text + "`n" + (($writerProofs | ForEach-Object { $_.Extent.Text }) -join "`n") + "`n`$proofs = [ordered]@{}`n"
        foreach ($key in $script:ProofKeys) { $writerText += "`$proofs['$key'] = `$$($script:ProofVariables[$key])`n" }
        $writerText += "return [pscustomobject]@{ Classification = `$recoveryClassification; Proofs = [pscustomobject]`$proofs; Handoff = `$$handoffName }`n}"
        . ([scriptblock]::Create($writerText))
        """;
}
