namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "AcceptancePhase5Fast")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferEvidenceCatalogCheckoutTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell", "5")]
    [InlineData("pwsh", "7")]
    public async Task EvidenceCatalog_FreshCheckoutPreservesPinnedBytesAndGuiLoader(string host, string major)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var result = await RunPowerShellScriptTextAsync(CheckoutHarness, [FindRepoRoot(), major], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("catalog-checkout-ok", result.Stdout, StringComparison.Ordinal);
    }

    // A private Git index round-trips the current proposed files. Never stage in the
    // user's repository, execute acceptance, or reconstruct a retained report.
    private const string CheckoutHarness = """
        param([string]$RepoRoot, [int]$ExpectedMajor)
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'
        if ($PSVersionTable.PSVersion.Major -ne $ExpectedMajor) { throw 'wrong PowerShell host' }
        $root = Join-Path ([IO.Path]::GetTempPath()) ('nlink-catalog-checkout-' + [guid]::NewGuid().ToString('N'))
        $work = Join-Path $root 'work'
        $checkout = Join-Path $root 'checkout'
        $catalogRelative = 'tools/FileTransferOps/LegacyV0EvidenceCatalog.json'
        $expected = 'a2ad4a9853c5a9551c98e59c11c5d71e1a35b0b5b0ff3229da1f4d6f295083ac'
        New-Item -ItemType Directory -Path (Join-Path $work 'tools'), $checkout | Out-Null
        function Invoke-Git {
            & git -C $work @args
            if ($LASTEXITCODE -ne 0) { throw "git failed: $args" }
        }
        try {
            Copy-Item -LiteralPath (Join-Path $RepoRoot '.gitattributes') -Destination $work
            Copy-Item -LiteralPath (Join-Path $RepoRoot 'tools/FileTransferOps') -Destination (Join-Path $work 'tools') -Recurse
            foreach ($name in @('GuiSmokeLogLedger.ps1', 'GuiSmokeEvidenceCatalog.ps1', 'GuiSmokeStructuredFields.ps1', 'GuiSmokeContentHash.ps1', 'GuiSmokeLogLedgerStreamingProcessor.cs', 'Test-FileTransferEvidenceCatalog.ps1')) {
                Copy-Item -LiteralPath (Join-Path $RepoRoot ('tools/' + $name)) -Destination (Join-Path $work 'tools')
            }
            Invoke-Git init --quiet
            Invoke-Git config core.autocrlf true
            Invoke-Git add -- .gitattributes tools
            Invoke-Git checkout-index --all ('--prefix=' + ($checkout -replace '\\', '/') + '/')
            $catalog = Join-Path $checkout $catalogRelative
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $actual = [BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($catalog))).Replace('-', '').ToLowerInvariant() }
            finally { $sha.Dispose() }
            if ($actual -cne $expected) {
                throw 'fresh checkout changed pinned catalog bytes'
            }
            . (Join-Path $checkout 'tools/GuiSmokeLogLedger.ps1')
            $events = @(Get-GuiSmokeSemanticEvidenceEventNames)
            if ($events.Count -eq 0) { throw 'empty GUI evidence catalog' }
            $preflight = Join-Path $checkout 'tools/Test-FileTransferEvidenceCatalog.ps1'
            $ready = & $preflight
            if ($ready.catalogPath -cne $catalog -or $ready.catalogSha256 -cne $expected -or
                $ready.semanticEventCount -ne $events.Count) {
                throw 'preflight did not validate the same GUI catalog'
            }

            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                (Join-Path $RepoRoot 'tools/Run-FileTransferRouteAcceptance.ps1'), [ref]$tokens, [ref]$errors)
            if ($errors.Count -ne 0) { throw 'acceptance script did not parse' }
            $filesFunction = $ast.Find({ param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq 'Get-RouteAcceptanceEvidenceHarnessFiles'
            }, $true)
            . ([scriptblock]::Create($filesFunction.Extent.Text))
            $frozen = @(Get-RouteAcceptanceEvidenceHarnessFiles -RepoRoot $checkout)
            # The legacy provenance schema requires exactly five harness files.
            # The preflight's exact catalog path/hash is frozen separately by the launcher.
            if ($frozen.Count -ne 5) { throw 'legacy harness provenance schema changed' }
            $calls = @($ast.FindAll({ param($node)
                $node -is [System.Management.Automation.Language.CommandAst] -and
                $node.CommandElements[0].Extent.Text -like '*Test-FileTransferEvidenceCatalog.ps1*'
            }, $true))
            if ($calls.Count -ne 1) { throw 'missing or duplicate catalog preflight call' }
            $liveGate = $calls[0].Parent
            while ($null -ne $liveGate -and $liveGate -isnot [System.Management.Automation.Language.IfStatementAst]) {
                $liveGate = $liveGate.Parent
            }
            if ($null -eq $liveGate -or $liveGate.Clauses[0].Item1.Extent.Text -cne '-not $fakeMode') {
                throw 'catalog preflight is not at the live setup boundary'
            }
            $setupGate = [scriptblock]::Create('if (-not $fakeMode) ' + $liveGate.Clauses[0].Item2.Extent.Text)

            # Execute only the extracted setup gate: invalid bytes must throw before
            # credentials, packaged bridge setup, process cleanup or live preflight.
            $repoRoot = $checkout
            $fakeMode = $true
            . $setupGate
            foreach ($missing in @($false, $true)) {
                if ($missing) { Remove-Item -LiteralPath $catalog }
                else { [IO.File]::AppendAllText($catalog, ' ', [Text.UTF8Encoding]::new($false)) }
                $fakeMode = $false
                $rejected = $false
                try { . $setupGate }
                catch {
                    $prefix = if ($missing) { 'legacy_v0_evidence_catalog_missing:' } else { 'legacy_v0_evidence_catalog_hash_invalid:' }
                    $rejected = $_.Exception.Message.StartsWith($prefix, [StringComparison]::Ordinal)
                }
                if (-not $rejected) { throw 'invalid catalog did not stop live setup at the catalog gate' }
            }
            Write-Output ('catalog-checkout-ok host=' + $PSVersionTable.PSVersion + ' events=' + $events.Count)
        }
        finally {
            $resolved = [IO.Path]::GetFullPath($root)
            $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
            if (-not $resolved.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase)) { throw 'unsafe cleanup path' }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
        """;
}
