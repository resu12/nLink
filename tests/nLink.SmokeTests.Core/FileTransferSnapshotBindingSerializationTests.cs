using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "CoordinatorSnapshots")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferSnapshotBindingSerializationTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    public async Task SnapshotBindingSerialization_ArraysHaveExactJsonAndHashInBothPowerShellHosts()
    {
        if (!OperatingSystem.IsWindows()) { return; }

        var expected = new Dictionary<string, string>
        {
            ["empty"] = "{\"LocalProofEffects\":[]}",
            ["singleton"] = "{\"LocalProofEffects\":[{\"EffectIdentity\":\"a\"}]}",
            ["pair"] = "{\"LocalProofEffects\":[{\"EffectIdentity\":\"b\"},{\"EffectIdentity\":\"a\"}]}",
            ["nested"] = "{\"LocalProofEffects\":[[],[1],[1,2]]}",
            ["nulls"] = "{\"LocalProofEffects\":[null,1,null]}",
            ["null"] = "{\"LocalProofEffects\":null}",
            ["wrapper"] = "{\"LocalProofEffects\":{\"Count\":1,\"value\":[1]}}",
        };
        foreach (var host in new[] { "powershell", "pwsh" })
        {
            var result = await RunPowerShellScriptTextAsync(Harness, [FindRepoRoot(), "arrays"], host);
            Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
            using var output = JsonDocument.Parse(result.Stdout);
            foreach (var (name, json) in expected)
            {
                var actual = output.RootElement.GetProperty(name);
                Assert.Equal(json, actual.GetProperty("Json").GetString());
                Assert.Equal(Hash(json), actual.GetProperty("Hash").GetString());
            }
        }
    }

    [Fact]
    public async Task SnapshotBindingSerialization_SavedSixBindingsHaveExactDualHostParityAndStrictMutationFailures()
    {
        if (!OperatingSystem.IsWindows()) { return; }

        var root = FindRepoRoot();
        // Immutable semantic capture from control 20260907T093634Z: four paired
        // yields with two local effects, and two pending proofs with empty arrays.
        var expected = (await File.ReadAllTextAsync(Path.Combine(root,
            "tests/nLink.SmokeTests.Core/Support/FallbackTailPeerProofBindings.json"))).Trim();
        Assert.Equal(40778, expected.Length);
        Assert.Equal("f0cfe16a0913e594d499d8f3d7fb448ca569eccc1ff1cdeb37ad9db994760893", Hash(expected));

        foreach (var host in new[] { "powershell", "pwsh" })
        {
            var result = await RunPowerShellScriptTextAsync(Harness, [root, "bindings"], host);
            Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
            using var output = JsonDocument.Parse(result.Stdout);
            Assert.Equal(expected, output.RootElement.GetProperty("Json").GetString());
            Assert.Equal(Hash(expected), output.RootElement.GetProperty("Hash").GetString());
            Assert.Equal(6, output.RootElement.GetProperty("Count").GetInt32());
            Assert.True(output.RootElement.GetProperty("StrictNegativesPassed").GetBoolean());
        }
    }

    private static string Hash(string json) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();

    // Calls only semantic normalization, empty-evidence construction and parity;
    // never invokes a retained report, GUI process, or live network scenario.
    private const string Harness = """
        param([string]$RepoRoot, [string]$Mode)
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'
        . (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')

        function Get-JsonHash([string]$Json) {
            Get-FileTransferCoordinatorSnapshotSha256 -Bytes ([Text.UTF8Encoding]::new($false).GetBytes($Json))
        }
        if ($Mode -ceq 'arrays') {
            $cases = @'
        [
          {"Name":"empty","Input":{"LocalProofEffects":[]}},
          {"Name":"singleton","Input":{"LocalProofEffects":[{"EffectIdentity":"a","LineIndex":7}]}},
          {"Name":"pair","Input":{"LocalProofEffects":[{"EffectIdentity":"b","EvidenceLineIndex":8},{"EffectIdentity":"a"}]}},
          {"Name":"nested","Input":{"LocalProofEffects":[[],[1],[1,2]]}},
          {"Name":"nulls","Input":{"LocalProofEffects":[null,1,null]}},
          {"Name":"null","Input":{"LocalProofEffects":null}},
          {"Name":"wrapper","Input":{"LocalProofEffects":{"value":[1],"Count":1}}}
        ]
        '@ | ConvertFrom-Json
            $results = [ordered]@{}
            foreach ($case in $cases) {
                $normalized = ConvertTo-FileTransferCoordinatorSnapshotBindingSemanticValue -Value $case.Input
                $json = ConvertTo-Json -InputObject $normalized -Depth 32 -Compress
                $results[$case.Name] = [ordered]@{ Json = $json; Hash = Get-JsonHash $json }
            }
            ConvertTo-Json -InputObject $results -Depth 8 -Compress
            return
        }

        $fixturePath = Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FallbackTailPeerProofBindings.json'
        $fixture = [IO.File]::ReadAllText($fixturePath)
        $bindings = $fixture | ConvertFrom-Json
        if ($bindings.Count -ne 6) { throw 'fixture must contain six bindings, not a nested array' }
        $normalized = ConvertTo-FileTransferCoordinatorSnapshotBindingSemanticValue -Value $bindings
        $json = ConvertTo-Json -InputObject $normalized -Depth 32 -Compress
        $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines @()
        $full | Add-Member -NotePropertyName '__FallbackTailPeerProofBindings' -NotePropertyValue $bindings -Force
        $summary = Get-FileTransferCoordinatorSnapshotFallbackTailPeerProofBindingSemanticSummary -Evaluation $full
        if ($summary.Count -ne 6 -or $summary.Hash -cne (Get-JsonHash $json)) { throw 'production summary diverged from exact normalization' }

        $empty = Get-FileTransferCoordinatorSnapshotFallbackTailPeerProofBindingSemanticSummary -Evaluation ([pscustomobject]@{})
        if ($empty.Count -ne 0 -or $empty.Hash -cne (Get-JsonHash '[]')) { throw 'empty binding summary changed' }
        foreach ($mutation in @('none', 'location', 'effect', 'proof', 'receipt', 'order', 'duplicate', 'missing', 'wrapped')) {
            $copy = $fixture | ConvertFrom-Json
            switch ($mutation) {
                'none' { [Array]::Reverse($copy) }
                'location' {
                    $copy[0] | Add-Member -NotePropertyName LineIndex -NotePropertyValue 999 -Force
                    $copy[0].LocalProofEffects[0] | Add-Member -NotePropertyName EvidenceLineIndex -NotePropertyValue 1000 -Force
                }
                'effect' { $copy[0].LocalProofEffects[0].EffectIdentity = 'wrong-effect' }
                'proof' { $copy[0].PeerProofFingerprint = 'wrong-proof' }
                'receipt' { $copy[0].LocalProofEffects[0].ReceiptFingerprint = 'wrong-receipt' }
                'order' { [Array]::Reverse($copy[0].LocalProofEffects) }
                'duplicate' { $copy[0].LocalProofEffects = @($copy[0].LocalProofEffects) + @($copy[0].LocalProofEffects[0]) }
                'missing' { $copy = @($copy[0..4]) }
                'wrapped' { $copy[0].LocalProofEffects = [pscustomobject]@{ value = $copy[0].LocalProofEffects; Count = 2 } }
            }
            $compact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines @()
            $compact | Add-Member -NotePropertyName '__FallbackTailPeerProofBindings' -NotePropertyValue $copy -Force
            $parity = Compare-FileTransferCoordinatorSnapshotEvaluations -Full $full -Compact $compact
            $expectedReason = if ($mutation -in @('none','location')) { '' }
                elseif ($mutation -ceq 'missing') { 'snapshot_full_compact_fallback_tail_peer_proof_binding_count_mismatch' }
                else { 'snapshot_full_compact_fallback_tail_peer_proof_binding_hash_mismatch' }
            if ($parity.FailureReason -cne $expectedReason -or
                $parity.Verdict -cne $(if ($expectedReason) { 'fail' } else { 'pass' })) {
                throw "$mutation did not retain exact parity validation: $($parity | ConvertTo-Json -Compress)"
            }
        }
        [ordered]@{ Json = $json; Hash = $summary.Hash; Count = $summary.Count; StrictNegativesPassed = $true } |
            ConvertTo-Json -Depth 8 -Compress
        """;
}
