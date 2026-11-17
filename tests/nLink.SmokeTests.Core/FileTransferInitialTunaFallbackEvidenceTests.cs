using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "CoordinatorSnapshots")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferInitialTunaFallbackEvidenceTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData(FileTransferDirection.Outbound, false)]
    [InlineData(FileTransferDirection.Inbound, false)]
    [InlineData(FileTransferDirection.Outbound, true)]
    [InlineData(FileTransferDirection.Inbound, true)]
    public async Task InitialTunaFallback_ExchangeSnapshotsPreserveExactInitialSource(FileTransferDirection direction, bool transition)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = Path.Combine(Path.GetTempPath(), "nlink-initial-fallback", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "actual-emitter.log");
            await File.WriteAllLinesAsync(path, FileTransferInitialRegularRouteEvidenceTests.Emit(
                transition ? FileTransferCoordinatorTests.CreateInitialFallbackTransitionSnapshotFixture(direction)
                    : FileTransferCoordinatorTests.CreateInitialFallbackExchangeSnapshotFixture(direction),
                transition ? EmitInitialFallbackTransitionEffects : null));
            const string script = """
                param([string]$RepoRoot, [string]$InputPath, [int]$Transition)
                $ErrorActionPreference = 'Stop'
                . (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
                $lines = [IO.File]::ReadAllLines($InputPath)
                $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $lines
                $records = @(for ($i = 0; $i -lt $lines.Count; $i++) { [pscustomobject]@{Line=$lines[$i]; LineIndex=$i} })
                $compact = Get-FileTransferCoordinatorSnapshotEvaluation -SnapshotRecords $records
                $route = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $full
                if ($full.ArtifactEvidenceVerdict -cne 'pass' -or -not $full.StreamComplete -or
                    $null -ne $full.FirstUnresolvedBoundary -or $route.Verdict -cne 'pass') {
                    throw "Initial exchange evidence failed: $($full.ArtifactEvidenceFailureReason); $($route.Findings -join ';')"
                }
                if ((Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass' -or
                    (Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $compact).Verdict -cne 'pass') {
                    throw 'Independent initial exchange consumers differ'
                }
                $exchanges = @($full.Boundaries | ForEach-Object operations | Where-Object operationRole -CEQ 'fallback_bootstrap_exchange')
                $expectedInvalidations = if ($Transition -eq 1) { 0 } else { 1 }
                if ($exchanges.Count -eq 0 -or @($exchanges | Where-Object afterState -CEQ 'Invalidated').Count -ne $expectedInvalidations) {
                    throw 'Initial exchange lifecycle missing'
                }
                foreach ($exchange in $exchanges) {
                    $source = $exchange.routeTransition.sourceBoundary
                    if ($source.route -cne 'file_tuna_v4' -or $source.protocolVersion -ne 4 -or $source.liveRouteEpoch -ne 0 -or
                        $source.routeCommitGeneration -ne 0 -or $source.routePublicationGeneration -ne 1) {
                        throw 'Initial exchange source was fabricated as a committed source'
                    }
                }
                if ($Transition -eq 1) {
                    $commits = @($full.Boundaries | ForEach-Object operations | Where-Object {
                        $_.family -ceq 'route_transition' -and $_.operationRole -ceq 'terminal' -and
                        $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Committed' -and $_.stateApplied
                    })
                    if ($commits.Count -ne 1) { throw 'Exact initial fallback commit missing' }
                    $typed = $commits[0].routeTransition
                    if ($typed.sourceBoundary.route -cne 'file_tuna_v4' -or $typed.sourceBoundary.liveRouteEpoch -ne 0 -or
                        $typed.sourceBoundary.routeCommitGeneration -ne 0 -or $typed.targetBoundary.route -cne 'post_tuna_fallback_v6' -or
                        $typed.targetBoundary.protocolVersion -ne 6 -or $typed.targetBoundary.liveRouteEpoch -ne 1) {
                        throw 'Initial fallback transition source/target mismatch'
                    }
                    foreach ($mutation in @('missing-initial-publication', 'missing-transition-retirement', 'invented-commit', 'changed-source')) {
                        $copy = $full | ConvertTo-Json -Depth 100 -Compress | ConvertFrom-Json
                        $commit = @($copy.Boundaries | Where-Object decisionKind -CEQ 'CommitFileTransferRouteTransition')[0]
                        switch ($mutation) {
                            'missing-initial-publication' {
                                $copy.Boundaries = @($copy.Boundaries | Where-Object decisionKind -CNE 'RoutePublication')
                            }
                            'missing-transition-retirement' {
                                $commit.operations = @($commit.operations | Where-Object { $_.operationRole -cne 'current' })
                            }
                            'invented-commit' {
                                $invented = $commit.operations | Where-Object operationRole -CEQ 'route_publication' | Select-Object -Last 1 |
                                    ConvertTo-Json -Depth 32 -Compress | ConvertFrom-Json
                                $invented.operationRole = 'route_commit'
                                $commit.operations += $invented
                            }
                            'changed-source' {
                                ($commit.operations | Where-Object operationRole -CEQ 'terminal').routeTransition.sourceBoundary.dataSessionIdentity = 'replacement'
                            }
                        }
                        if ((Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $copy).Verdict -cne 'fail') {
                            throw "Initial fallback route authority mutation accepted: $mutation"
                        }
                    }
                }
                """;
            foreach (var host in new[] { "powershell", "pwsh" })
            {
                var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), path, transition ? "1" : "0"], host);
                Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
            }
        }
        finally { TryDeleteDirectory(root); }
    }

    private static void EmitInitialFallbackTransitionEffects(Type emitterType, object emitter,
        FileTransferCoordinatorTransitionEnvelope envelope)
    {
        // Model Service effects from exact Core receipts for analyzer input only.
        // This does not assert that the adapter handoff or physical callbacks ran.
        foreach (var operation in envelope.Aggregate.Operations.Where(operation => operation.ReceiptValidated))
        {
            string[] kinds = [];
            if (operation.Family == "route_transition")
            {
                kinds = envelope.DecisionKind switch
                {
                    "BeginFileTransferRouteTransitionPhysicalDrain" when operation.OperationRole == "current" => ["pause_installed"],
                    "CommitFileTransferRouteTransition" when operation.OperationRole == envelope.Direction => ["completed_drain_receipt"],
                    "CommitFileTransferRouteTransition" when operation.OperationRole == "terminal" &&
                        operation.BeforeState == "None" && operation.AfterState == "Committed" => ["route_commit_adopted", "target_runtime_leg_installed"],
                    "ApplyFileTransferRouteTransitionDispositionReceipt" when operation.OperationRole == envelope.Direction =>
                        ["route_transition_disposition_applied", "transition_pause_released", "recovered_epoch_publication"],
                    "ApplyFileTransferRouteTransitionRetainedFrameReceipt" when operation.OperationRole == envelope.Direction =>
                        ["retained_frame_response", "retained_frame_applied"],
                    _ => [],
                };
            }
            else if (envelope.DecisionKind == "CommitFileTransferRouteTransition" &&
                operation.Family == "route_runtime" && operation.OperationRole == "route_publication" && operation.AfterState == "Published")
            {
                kinds = ["route_publication_forwarded"];
            }
            foreach (var kind in kinds)
            {
                emitterType.GetMethod("EnqueueAuthorityEffect", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(emitter, [envelope.CoordinatorInstanceId, envelope.SessionId, envelope.TransferId,
                        Enum.Parse<FileTransferDirection>(envelope.Direction, true), envelope.Aggregate.AfterStateGeneration,
                        operation.Family, operation.AuthorityId, operation.OperationGeneration, operation.ReceiptIdentity,
                        operation.ReceiptGeneration, operation.ReceiptStatus, operation.ReceiptFingerprint, kind,
                        $"{operation.OperationKey}:{kind}", operation.ReceiptGeneration, operation.OperationKey]);
            }
        }
    }
}
