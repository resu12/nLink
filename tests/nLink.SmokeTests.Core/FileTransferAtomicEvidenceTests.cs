namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "GuiHarness")]
[Trait("FileTransferEvidenceSuite", "OperationalLogIntegrity")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferAtomicEvidenceTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell", 222)]
    [InlineData("powershell", 223)]
    [InlineData("powershell", 224)]
    [InlineData("powershell", 270)]
    [InlineData("pwsh", 222)]
    [InlineData("pwsh", 223)]
    [InlineData("pwsh", 224)]
    [InlineData("pwsh", 270)]
    public async Task AtomicManifestReplacement_PreservesEvidenceAcrossPathBoundary(string host, int pathLength)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var repo = FindRepoRoot();
        var root = Path.Combine(repo, "artifacts", "atomic-evidence-tests", Guid.NewGuid().ToString("N"));
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$RepoRoot, [string]$Root, [int]$PathLength)
            $ErrorActionPreference = 'Stop'
            Set-StrictMode -Version Latest
            . (Join-Path $RepoRoot 'tools/FileTransferSoak/RetainedAnalysisEvidence.ps1')
            $name = 'filetransfer-tuna-gui-evidence-manifest.json'
            $padding = 'x' * ($PathLength - $Root.Length - $name.Length - 2)
            $path = Join-Path (Join-Path $Root $padding) $name
            if ($path.Length -ne $PathLength) { throw 'fixture path length mismatch' }
            $initial = [pscustomobject]@{
                schemaVersion = 1
                event = 'filetransfer_tuna_gui_direct_evidence_manifest'
                acceptanceRunId = 'run-a'
                scenarioRunId = 'run-a:scenario:1'
                currentRuntimeProof = $false
                retainedAnalysisReceiptSha256 = ''
            }
            Write-FileTransferTunaDirectEvidenceManifestProjection -Path $path -Source $initial | Out-Null
            $before = [IO.File]::ReadAllText($path)
            $receiptHash = 'a' * 64
            try {
                Write-FileTransferTunaDirectEvidenceManifestProjection -Path $path -Source $initial -Overrides @{
                    currentRuntimeProof = $true
                    retainedAnalysisReceiptSha256 = $receiptHash
                } | Out-Null
            }
            catch {
                if ([IO.File]::ReadAllText($path) -cne $before) { throw 'failed replacement changed original evidence' }
                throw
            }
            $published = [IO.File]::ReadAllText($path) | ConvertFrom-Json
            if ($published.acceptanceRunId -cne 'run-a' -or $published.scenarioRunId -cne 'run-a:scenario:1' -or
                $published.currentRuntimeProof -ne $true -or $published.retainedAnalysisReceiptSha256 -cne $receiptHash) {
                throw 'replacement lost manifest identity or receipt binding'
            }

            # A destination that forbids deletion must fail closed, not be truncated or rewritten in place.
            $before = [IO.File]::ReadAllText($path)
            $held = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
            $rejected = $false
            try {
                try { Write-FileTransferEvidenceAtomicJson -Path $path -Value @{ unexpected = 'replacement' } }
                catch { $rejected = $_.Exception.Message -like '*Atomic manifest replacement failed*' }
                if (-not $rejected) { throw 'locked destination did not reject atomic replacement' }
                if ([IO.File]::ReadAllText($path) -cne $before) { throw 'failed write altered the published manifest' }
            }
            finally { $held.Dispose() }
            if (@(Get-ChildItem -LiteralPath (Split-Path $path -Parent) -File -Filter '*.tmp').Count -ne 0) {
                throw 'atomic replacement left temporary files'
            }
            "atomic-path-ok: host=$($PSVersionTable.PSVersion.Major); destination=$PathLength; temporary=$($PathLength + 37)"
            """, [repo, root, pathLength.ToString(System.Globalization.CultureInfo.InvariantCulture)], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("atomic-path-ok:", result.Stdout, StringComparison.Ordinal);
    }
}
