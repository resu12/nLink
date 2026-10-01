using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "ScriptSurface")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferEarlyYieldEvidenceTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task EarlyCheckpointPeerProof_ProductionEmitterPreservesOneOriginalObservation(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var repo = FindRepoRoot();
        var root = Path.Combine(repo, "artifacts", "early-yield-evidence-20260930", host);
        Directory.CreateDirectory(root);
        foreach (var direction in new[] { FileTransferDirection.Outbound, FileTransferDirection.Inbound })
        {
            var envelopes = FileTransferCoordinatorTests.CreateEarlyCheckpointClearProofSnapshotFixture(direction);
            var lines = FileTransferInitialRegularRouteEvidenceTests.Emit(envelopes, (type, emitter, envelope) =>
            {
                var methods = typeof(SessionFileTransferService).GetMethods(
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                var decision = methods.Single(method => method.Name == "EnqueueFallbackTailYieldDecisionAuthorityEffect" &&
                    method.GetParameters()[0].ParameterType == type);
                object?[] common = [emitter, envelope.CoordinatorInstanceId, envelope.SessionId, envelope.TransferId,
                    direction, envelope.Aggregate.AfterStateGeneration, envelope];
                decision.Invoke(null, common);
                // These stage callbacks are modeled. The production emitter must bind
                // their effects to exact Core receipts, without copying the peer observation.
                var stage = envelope.DecisionKind switch
                {
                    "FallbackTailQuiescent" => (Stage: "yield", Kind: "recovery_owner_retired_after_yield"),
                    "ApplyFallbackTailLocalProofReceipt" => (Stage: "paired_release", Kind: "paired_release_published"),
                    _ => (Stage: "", Kind: ""),
                };
                if (stage.Stage.Length != 0)
                    methods.Single(method => method.Name == "EnqueueFallbackTailYieldAuthorityEffect" &&
                        method.GetParameters()[0].ParameterType == type).Invoke(null, [.. common, stage.Stage, stage.Kind]);
            });
            Assert.Single(lines.Where(line => line.Contains("effect_kind=peer_proof_observed;", StringComparison.Ordinal)));
            Assert.Single(lines.Where(line => line.Contains("effect_kind=paired_release_published;", StringComparison.Ordinal)));
            var path = Path.Combine(root, "production-" + direction + ".log");
            await File.WriteAllLinesAsync(path, lines);
            const string script = """
                param([string]$RepoRoot, [string]$Path)
                $ErrorActionPreference = 'Stop'
                . (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
                $lines = [IO.File]::ReadAllLines($Path)
                $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $lines
                $records = @(for ($i = 0; $i -lt $lines.Count; $i++) { [pscustomobject]@{ Line=$lines[$i]; LineIndex=$i } })
                $compact = Get-FileTransferCoordinatorSnapshotEvaluation -SnapshotRecords $records
                if ($full.ArtifactEvidenceVerdict -cne 'pass' -or -not $full.StreamComplete -or
                    $null -ne $full.FirstUnresolvedBoundary -or
                    (Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass') {
                    throw "Production evidence failed: $($full.ArtifactEvidenceFailureReason)"
                }
                """;
            var result = await RunPowerShellScriptTextAsync(script, [repo, path], host);
            Assert.True(result.ExitCode == 0, $"{direction}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
        }
    }

    [Theory]
    [InlineData(FileTransferDirection.Outbound, "powershell")]
    [InlineData(FileTransferDirection.Inbound, "powershell")]
    [InlineData(FileTransferDirection.Outbound, "pwsh")]
    [InlineData(FileTransferDirection.Inbound, "pwsh")]
    public async Task EarlyCheckpointPeerProof_RequiresExactCustodyAndPromotionHistory(FileTransferDirection direction, string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var repo = FindRepoRoot();
        var root = Path.Combine(repo, "artifacts", "early-yield-evidence-20260930", host, direction.ToString());
        Directory.CreateDirectory(root);
        var envelopes = FileTransferCoordinatorTests.CreateEarlyCheckpointClearProofSnapshotFixture(direction)
            .Select(SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting);
        var fixture = Path.Combine(root, "envelopes.json");
        await File.WriteAllTextAsync(fixture, "[" + string.Join(",", envelopes.Select(e => e.ToCanonicalJson())) + "]");
        foreach (var transition in new[] { "retired", "successor", "yield" })
        {
            var discarded = FileTransferCoordinatorTests.CreateEarlyCheckpointProofDiscardFixture(direction, transition)
                .Select(SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting);
            await File.WriteAllTextAsync(Path.Combine(root, transition + ".json"),
                "[" + string.Join(",", discarded.Select(e => e.ToCanonicalJson())) + "]");
        }
        var script = File.ReadAllText(Path.Combine(repo, "tests", "nLink.SmokeTests.Core", "Support",
            "EarlyCheckpointPeerProofEvidence.ps1"));
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var profile = (string)typeof(SessionFileTransferService).GetField("CoordinatorSnapshotCatalogVersion", flags)!.GetRawConstantValue()!;
        var hash = (string)typeof(SessionFileTransferService).GetField("CoordinatorSnapshotCatalogHash", flags)!.GetRawConstantValue()!;
        var result = await RunPowerShellScriptTextAsync(script, [repo, fixture, root, profile, hash], host);
        Assert.True(result.ExitCode == 0, $"STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
    }
}
