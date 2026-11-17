using System.Text;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    [InlineData(false, 4)]
    [InlineData(true, 4)]
    [InlineData(false, 5)]
    [InlineData(true, 5)]
    [InlineData(false, 6)]
    [InlineData(true, 6)]
    [InlineData(false, 7)]
    [InlineData(true, 7)]
    [InlineData(false, 8)]
    [InlineData(true, 8)]
    public void TransitionLocalRouteDescriptors_MatchFreshRebuild(bool inbound, int change)
    {
        var pair = CreateCorrelatedBootstrapPair(providerGeneration: 7);
        var issued = StartCorrelated(inbound ? pair.Receiver : pair.Sender);
        var before = issued.State;
        // Projection-only perturbations do not authorize effects or patch issued DTOs.
        var after = change switch
        {
            0 => before with { LastFallbackControlPlaneTokenGeneration = before.LastFallbackControlPlaneTokenGeneration + 1 },
            1 => FreshTransitionGraph(before),
            2 => FileTransferCoordinator.ApplyFallbackBootstrapPhaseReceipt(before,
                CorrelatedReceipt(issued.Command!, FileTransferBootstrapPhaseReceiptStatus.Delivered)).State,
            3 => before with { CurrentDataSessionProviderGeneration = 99 },
            4 => before with { CurrentDataSessionIdentity = "replacement-session" },
            5 => before with { CurrentDataSessionGeneration = 18 },
            6 => before with { TerminalGeneration = before.TerminalGeneration + 1 },
            7 => before with { CurrentTransportEpochDecision = before.CurrentTransportEpochDecision.GetValueOrDefault() with
                { TransportEpoch = (before.CurrentTransportEpochDecision?.TransportEpoch ?? 0) + 1 } },
            8 => before with { CurrentFallbackCheckpointLease = before.CurrentFallbackCheckpointLease.GetValueOrDefault() with
                { CheckpointGeneration = 19, CheckpointRequestId = "replacement-checkpoint" } },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        var freshAfter = FreshTransitionGraph(after);
        var method = typeof(FileTransferCoordinator).GetMethods(
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Single(method => method.Name == "CreateOperationTransitionSnapshots" && method.GetParameters().Length == 4);
        object?[] actualArguments = [before, after, null, null];
        object?[] freshArguments = [before, freshAfter, null, null];
        var actualOperations = method.Invoke(null, actualArguments)!;
        var freshOperations = method.Invoke(null, freshArguments)!;
        var options = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
        string Canonical(object value) => FileTransferCoordinatorTransitionEnvelope.SerializeCanonical(value, options);
        Assert.Equal(Canonical(freshOperations), Canonical(actualOperations));
        Assert.Equal(Canonical(freshArguments[2]!), Canonical(actualArguments[2]!));
        Assert.Equal(Canonical(freshArguments[3]!), Canonical(actualArguments[3]!));
        var prior = ((System.Collections.IEnumerable)actualArguments[2]!).Cast<object>().ToArray();
        var current = ((System.Collections.IEnumerable)actualArguments[3]!).Cast<object>().ToArray();
        var retained = prior.Where(descriptor => (string)descriptor.GetType().GetProperty("Family")!.GetValue(descriptor)! == "route_transition").ToArray();
        Assert.NotEmpty(retained);
        foreach (var descriptor in retained)
        {
            // Only the unchanged graph/boundary case may reuse the exact descriptor.
            Assert.Equal(change == 0, current.Any(candidate => ReferenceEquals(candidate, descriptor)));
        }

        var coordinator = before.PersistentState.CoordinatorInstanceId!;
        FileTransferCoordinatorSnapshotResult Snapshot(FileTransferCoordinatorState next) =>
            FileTransferCoordinator.CreateTransitionSnapshot(before, next, coordinator,
                before.PersistentState.SnapshotStateGeneration, "DescriptorParity", "Accepted", "fixture_projection");
        var actual = Snapshot(after);
        var expected = Snapshot(freshAfter);
        Assert.Equal(Canonical(expected.Envelope.Aggregate), Canonical(actual.Envelope.Aggregate));
        Assert.Equal(expected.Envelope.DecisionStatus, actual.Envelope.DecisionStatus);
        Assert.Equal(expected.State.PersistentState.SnapshotStateFingerprint, actual.State.PersistentState.SnapshotStateFingerprint);
        Assert.Equal(FileTransferCoordinator.CalculateSnapshotStateFingerprint(freshAfter),
            FileTransferCoordinator.CalculateSnapshotStateFingerprint(after));
        Assert.Equal(7, issued.Command!.Bootstrap.Observation.DurableCheckpoint.DataSessionProviderGeneration);
        Assert.Equal(17, issued.Command.Bootstrap.SourceDataSessionGeneration);

        static FileTransferCoordinatorState FreshTransitionGraph(FileTransferCoordinatorState state) => state with
        {
            PersistentState = state.PersistentState with
            {
                FileTransferRouteTransition = state.PersistentState.FileTransferRouteTransition with { },
            },
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BootstrapProviderSnapshot_RetainsFrozenProviderAndRejectsStaleReceipts(bool inbound)
    {
        var pair = CreateCorrelatedBootstrapPair(providerGeneration: 7);
        var issued = StartCorrelated(inbound ? pair.Receiver : pair.Sender);
        var command = Assert.IsType<FileTransferBootstrapPhaseCommand>(issued.Command);
        var hold = issued.State.PersistentState.FileTransferRouteTransition.BootstrapSourceHold!;
        Assert.Equal(17, command.Bootstrap.SourceDataSessionGeneration);
        Assert.Equal(7, command.Bootstrap.Observation.DurableCheckpoint.DataSessionProviderGeneration);
        Assert.Equal(7, hold.Command.Initiation.DurableCheckpoint.DataSessionProviderGeneration);
        var original = issued.SnapshotEnvelope!.ToCanonicalJson();
        var source = Assert.Single(issued.SnapshotEnvelope.Aggregate.Operations,
            operation => operation.OperationRole == "fallback_v6_bootstrap").RouteTransition!.SourceBoundary;
        foreach (var operation in issued.SnapshotEnvelope.Aggregate.Operations.Where(operation =>
                     operation.OperationRole is "fallback_bootstrap_exchange" or "fallback_bootstrap_phase"))
        {
            Assert.Equal(source, operation.RouteTransition!.SourceBoundary);
            Assert.Equal(source, operation.RouteTransition.BootstrapInitiation!.SourceBoundary);
        }

        // A later provider observation must not rewrite the source of retained evidence.
        var replacement = issued.State with { CurrentDataSessionProviderGeneration = 99 };
        var retiredHold = FileTransferCoordinator.InvalidateFallbackBootstrapSourceHold(replacement, "fixture_retirement");
        var retiredExchange = FileTransferCoordinator.InvalidateFallbackBootstrapExchange(replacement, "fixture_retirement");
        foreach (var envelope in new[] { retiredHold.SnapshotEnvelope!, retiredExchange.SnapshotEnvelope! })
        {
            Assert.Equal(99, envelope.Aggregate.BeforeBoundary.DataSessionProviderGeneration);
            var operation = Assert.Single(envelope.Aggregate.Operations.Where(operation =>
                operation.OperationRole is "fallback_bootstrap_source_hold" or "fallback_bootstrap_exchange"));
            Assert.Equal(source, operation.RouteTransition!.SourceBoundary);
            Assert.Equal(source, operation.RouteTransition.BootstrapInitiation!.SourceBoundary);
        }
        var receipt = CorrelatedReceipt(command, FileTransferBootstrapPhaseReceiptStatus.Delivered);
        foreach (var stale in new[]
                 {
                     retiredExchange.State,
                     issued.State with { CurrentDataSessionGeneration = 18 },
                     issued.State with { CurrentDataSessionIdentity = "replacement-session" },
                 })
        {
            var rejected = FileTransferCoordinator.ApplyFallbackBootstrapPhaseReceipt(stale, receipt);
            Assert.False(rejected.Accepted);
            Assert.Same(stale, rejected.State);
            Assert.Null(rejected.Command);
            Assert.All(rejected.SnapshotEnvelope!.Aggregate.Operations, operation => Assert.False(operation.StateApplied));
        }
        Assert.Equal(original, issued.SnapshotEnvelope.ToCanonicalJson());
    }

    internal static IReadOnlyList<FileTransferCoordinatorTransitionEnvelope> CreateBootstrapProviderSnapshotFixture(
        bool inbound, long providerGeneration)
    {
        var envelopes = new List<FileTransferCoordinatorTransitionEnvelope>();
        var pair = CreateCorrelatedBootstrapPair(capture: Capture, providerGeneration: providerGeneration);
        var issued = StartCorrelated(inbound ? pair.Receiver : pair.Sender, capture: Capture);
        Capture(issued.SnapshotEnvelope!);
        CompleteCorrelatedPhase(issued.State, issued.Command!, Capture);
        return envelopes;

        void Capture(FileTransferCoordinatorTransitionEnvelope envelope)
        {
            if ((envelope.Direction == "inbound") == inbound) { envelopes.Add(envelope); }
        }
    }
}

public sealed partial class FileTransferBootstrapCorrelationEvidenceTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 7)]
    [InlineData(true, 7)]
    public async Task BootstrapProviderSnapshot_ExactFullCompactCorrelationRejectsMissingAndReplacement(bool inbound, long providerGeneration)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var directory = Path.Combine(FindRepoRoot(), "artifacts", "bootstrap-provider", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "projected.log");
        await File.WriteAllLinesAsync(path, Emit(FileTransferCoordinatorTests.CreateBootstrapProviderSnapshotFixture(inbound, providerGeneration)),
            new UTF8Encoding(false));
        foreach (var host in new[] { "powershell", "pwsh" })
        {
            var result = await RunPowerShellScriptTextAsync(ProviderSnapshotHarness,
                [FindRepoRoot(), path, inbound.ToString(), providerGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture)], host);
            await File.WriteAllTextAsync(Path.Combine(directory, host + ".stdout.log"), result.Stdout);
            await File.WriteAllTextAsync(Path.Combine(directory, host + ".stderr.log"), result.Stderr);
            Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
            Assert.Contains("bootstrap-provider-snapshot-ok", result.Stdout, StringComparison.Ordinal);
        }
    }

    private const string ProviderSnapshotHarness = """
        param([string]$RepoRoot, [string]$InputPath, [string]$Inbound, [long]$Provider)
        Set-StrictMode -Version Latest
        $ErrorActionPreference='Stop'
        . (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
        $captured=[IO.File]::ReadAllLines($InputPath)
        $cases=if($Provider -eq 0){@('exact','replacement')}else{@('exact','missing','replacement')}
        foreach($case in $cases){
            $lines=[string[]]$captured.Clone()
            $changed=0
            for($i=0;$i -lt $lines.Count;$i++){
                if((Get-FileTransferCoordinatorSnapshotEventName $lines[$i]) -cne 'filetransfer_coordinator_transition_snapshot'){continue}
                $envelope=(ConvertFrom-FileTransferCoordinatorSnapshotPayload (Get-FileTransferCoordinatorSnapshotLineField $lines[$i] 'snapshot_payload')).Value
                if($envelope.decisionKind -cne 'StartFallbackBootstrapExchange'){continue}
                $phase=@($envelope.aggregate.operations|Where-Object operationRole -CEQ 'fallback_bootstrap_phase')
                if($phase.Count -ne 1){throw 'Phase not unique'}
                $boundary=$phase[0].routeTransition.sourceBoundary
                if((Get-FileTransferCoordinatorSnapshotObjectProperty $boundary 'dataSessionProviderGeneration' 0) -ne $Provider -or
                    $boundary.dataSessionGeneration -ne 17){throw 'Independent generations not preserved'}
                if($Provider -eq 0 -and $boundary.PSObject.Properties.Name -ccontains 'dataSessionProviderGeneration'){throw 'Legacy omission changed'}
                if($case -ceq 'exact'){continue}
                foreach($scope in @($boundary,$phase[0].routeTransition.targetBoundary,$phase[0].routeTransition.bootstrapInitiation.sourceBoundary)){
                    if($case -ceq 'missing'){$scope.PSObject.Properties.Remove('dataSessionProviderGeneration')}
                    else{$scope|Add-Member dataSessionProviderGeneration ($Provider+1) -Force}
                }
                $bytes=[Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json $envelope -Depth 64 -Compress))
                $hash=Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes
                $payload=[Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
                $lines[$i]=[regex]::Replace($lines[$i],'snapshot_sha256=[^;]*',('snapshot_sha256='+$hash))
                $lines[$i]=[regex]::Replace($lines[$i],'snapshot_payload=[^;]*',('snapshot_payload='+$payload))
                $changed++
            }
            if($case -cne 'exact' -and $changed -ne 1){throw 'Negative mutation not unique'}
            $full=Get-FileTransferCoordinatorSnapshotEvaluation -Lines $lines
            $records=@(for($i=0;$i -lt $lines.Count;$i++){[pscustomobject]@{Line=$lines[$i];LineIndex=$i}})
            $compact=Get-FileTransferCoordinatorSnapshotEvaluation -SnapshotRecords $records
            foreach($view in @($full,$compact)){
                if($view.Mode -cne 'SnapshotV1Authoritative' -or -not $view.StreamComplete){throw 'Incomplete authoritative stream'}
                if($case -ceq 'exact'){
                    $errors=@($view.ArtifactEvidenceFailureReason -split ';'|Where-Object {$_})
                    # Modeled inbound activation has no physical responder effects; retain that independent failure.
                    $expected=if($Inbound -ceq 'True'){4}else{0}
                    if($errors.Count -ne $expected -or @($errors|Where-Object {$_ -cnotmatch '^snapshot_responder_probe_handoff_required_effect_missing:'}).Count){
                        throw "Unexpected exact artifact failure: $($view.ArtifactEvidenceFailureReason)"
                    }
                    $verdict=if($expected -eq 0){'pass'}else{'fail'}
                    if($view.ArtifactEvidenceVerdict -cne $verdict){throw 'Exact verdict mismatch'}
                }elseif($view.ArtifactEvidenceVerdict -cne 'fail' -or
                    $view.ArtifactEvidenceFailureReason -cnotmatch 'snapshot_operation_catalog_mismatch:'){
                    throw "Provider mismatch was not rejected: $($view.ArtifactEvidenceFailureReason)"
                }
            }
            if((Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass'){throw 'Full/compact parity failed'}
            Write-Output ("case=$case; provider=$Provider; artifact=$($full.ArtifactEvidenceVerdict); parity=pass; reason=$($full.ArtifactEvidenceFailureReason)")
        }
        Write-Output 'bootstrap-provider-snapshot-ok'
        """;
}
