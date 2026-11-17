using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "CoordinatorSnapshots")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed partial class FileTransferBootstrapCorrelationEvidenceTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("", true)]
    [InlineData("-unknown", false)]
    public void BootstrapCorrelation_RedactorPreservesOnlyTheExplicitProfile(string suffix, bool preserved)
    {
        var profile = "snapshot-authority-v1-receiver-barrier-v1-checkpoint-repair-continuity-v1-fallback-bootstrap-correlation-v1" + suffix;
        var line = NLink.Core.Logging.SensitiveDataRedactor.Redact(
            $"event=filetransfer_coordinator_transition_snapshot; snapshot_catalog_version={profile}; reason=test");
        Assert.Equal(preserved, line.Contains($"snapshot_catalog_version={profile};", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task BootstrapCorrelation_ProjectedExchangeHasExactFullCompactCatalogParity(bool announceFirst, bool outbound)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var directory = Path.Combine(FindRepoRoot(), "artifacts", "validation", "consolidation-bounded-20260907",
            "bootstrap-correlation-implementation", "evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "projected-exchange.log");
        var envelopes = FileTransferCoordinatorTests.CreateBootstrapCorrelationSnapshotFixture(announceFirst, outbound);
        // Production emitter/projector/redactor with modeled Coordinator receipts, not live delivery or writer evidence.
        await File.WriteAllLinesAsync(path, Emit(envelopes), new UTF8Encoding(false));
        const string script = """
            param([string]$RepoRoot, [string]$InputPath)
            & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/BootstrapCorrelationEvidence.ps1') -RepoRoot $RepoRoot -InputPath $InputPath
            """;
        string? firstOutput = null;
        foreach (var host in new[] { "powershell", "pwsh" })
        {
            var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), path], host);
            Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
            Assert.Contains("bootstrap-correlation-evidence-ok", result.Stdout, StringComparison.Ordinal);
            var normalized = Assert.Single(result.Stdout.Split('\n').Select(line => line.Trim()).Where(line =>
                line.StartsWith("bootstrap-correlation-normalized=", StringComparison.Ordinal)));
            if (firstOutput is not null) { Assert.Equal(firstOutput, normalized); }
            firstOutput = normalized;
        }
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    public async Task BootstrapCorrelation_AgreementHistoryPreservesNegotiationAndLegacyMode(bool advertised, bool selected, bool outbound)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var directory = Path.Combine(FindRepoRoot(), "artifacts", "validation", "consolidation-bounded-20260907",
            "bootstrap-correlation-implementation", "agreement-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "agreement.log");
        await File.WriteAllLinesAsync(path, Emit(FileTransferBootstrapAgreementTests.CreateSnapshotFixture(advertised, selected, outbound)),
            new UTF8Encoding(false));
        const string script = """
            param([string]$RepoRoot, [string]$InputPath)
            & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/BootstrapAgreementEvidence.ps1') -RepoRoot $RepoRoot -InputPath $InputPath
            """;
        string? firstOutput = null;
        foreach (var host in new[] { "powershell", "pwsh" })
        {
            var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), path], host);
            Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
            var normalized = Assert.Single(result.Stdout.Split('\n').Select(line => line.Trim()).Where(line =>
                line.StartsWith("bootstrap-agreement-normalized=", StringComparison.Ordinal)));
            if (firstOutput is not null) { Assert.Equal(firstOutput, normalized); }
            firstOutput = normalized;
        }
    }

    private static string[] Emit(IReadOnlyList<FileTransferCoordinatorTransitionEnvelope> envelopes, bool modeledOwnershipEffects = false,
        bool modeledFinalPublication = false)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = typeof(SessionFileTransferService).GetNestedType("CoordinatorSnapshotStreamEmitter", BindingFlags.NonPublic)!;
        var emitter = Activator.CreateInstance(type, nonPublic: true)!;
        // Hold the drain to inspect the real projection. Required physical effects are absent from these modeled fixtures.
        type.GetField("drainScheduled", flags)!.SetValue(emitter, 1);
        var first = envelopes[0];
        var direction = Enum.Parse<FileTransferDirection>(first.Direction, ignoreCase: true);
        Assert.Equal(true, type.GetMethod("Start", flags)!.Invoke(emitter,
            [first.CoordinatorInstanceId, first.SessionId, first.TransferId, direction,
                first.Aggregate.BeforeStateGeneration, first.Aggregate.BeforeStateFingerprint]));
        foreach (var envelope in envelopes)
        {
            type.GetMethod("Enqueue", flags)!.Invoke(emitter, [envelope]);
            if (modeledOwnershipEffects) { EmitModeledOwnershipEffects(type, emitter, envelope); }
            if (modeledFinalPublication)
            {
                foreach (var publication in envelope.Aggregate.Operations.Where(operation =>
                    operation.OperationRole == "route_publication" && operation.AfterState == "Published" &&
                    operation.RouteRuntime?.Boundary.LiveRouteEpoch == 4))
                {
                    type.GetMethod("EnqueueAuthorityEffect", flags)!.Invoke(emitter,
                        [envelope.CoordinatorInstanceId, envelope.SessionId, envelope.TransferId, direction,
                            envelope.Aggregate.AfterStateGeneration, publication.Family, publication.AuthorityId,
                            publication.OperationGeneration, publication.ReceiptIdentity, publication.ReceiptGeneration,
                            publication.ReceiptStatus, publication.ReceiptFingerprint, "route_publication_forwarded",
                            publication.ReceiptIdentity, publication.ReceiptGeneration, null]);
                }
            }
        }
        type.GetMethod("Complete", flags)!.Invoke(emitter, [first.CoordinatorInstanceId, first.SessionId, first.TransferId, direction]);
        var queue = (ConcurrentQueue<string>)type.GetField("pending", flags)!.GetValue(emitter)!;
        return queue.Select((line, index) => SensitiveDataRedactor.Redact(
            $"{line}; log_writer_id=bootstrap-correlation-fixture; log_writer_sequence={index + 1}")).ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BootstrapCorrelation_InvalidationPreservesPreviouslyDeliveredReceipt(bool outbound)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var directory = Path.Combine(FindRepoRoot(), "artifacts", "validation", "consolidation-bounded-20260907",
            "bootstrap-correlation-implementation", "invalidation-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "invalidation.log");
        await File.WriteAllLinesAsync(path, Emit(FileTransferCoordinatorTests.CreateBootstrapInvalidationHistory(outbound)), new UTF8Encoding(false));
        const string script = """
            param([string]$RepoRoot, [string]$InputPath)
            $ErrorActionPreference = 'Stop'
            . (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
            $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines ([IO.File]::ReadAllLines($InputPath))
            $errors = @(Get-FileTransferBootstrapCorrelationHistoryErrors @($full.Boundaries))
            if ($errors.Count -ne 0 -or $full.ArtifactEvidenceFailureReason -cmatch 'snapshot_bootstrap_correlation') {
                throw "Unchanged invalidation receipt rejected: $errors; $($full.ArtifactEvidenceFailureReason)"
            }
            $invalidated = @($full.Boundaries | Where-Object decisionKind -CEQ 'InvalidateFallbackBootstrapExchange')
            $phase = @($invalidated.operations | Where-Object operationRole -CEQ 'fallback_bootstrap_phase')
            if ($invalidated.Count -ne 1 -or $phase.Count -ne 1 -or $phase[0].beforeState -cne 'Delivered' -or
                $phase[0].afterState -cne 'Delivered' -or @($phase[0].routeTransition.receipts).Count -ne 1) {
                throw 'Invalidation fixture lost its unchanged delivered receipt'
            }
            """;
        foreach (var host in new[] { "powershell", "pwsh" })
        {
            var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), path], host);
            Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BootstrapCorrelation_RealSuccessorStartRetirementHasCatalogCoverage(bool outbound)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var directory = Path.Combine(FindRepoRoot(), "artifacts", "validation", "consolidation-bounded-20260907",
            "bootstrap-correlation-implementation", "successor-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "successor-start.log");
        await File.WriteAllLinesAsync(path, Emit([FileTransferCoordinatorTests.CreateBootstrapSuccessorStartSnapshot(outbound)]), new UTF8Encoding(false));
        const string script = """
            param([string]$RepoRoot, [string]$InputPath)
            $ErrorActionPreference = 'Stop'
            . (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
            $catalog = Get-FileTransferCoordinatorEvidenceCatalog -ProfileVersion $script:FileTransferCoordinatorBootstrapInitiationCatalogVersion
            $line = @([IO.File]::ReadAllLines($InputPath) | Where-Object {
                (Get-FileTransferCoordinatorSnapshotEventName $_) -ceq 'filetransfer_coordinator_transition_snapshot'
            })[0]
            $envelope = (ConvertFrom-FileTransferCoordinatorSnapshotPayload (Get-FileTransferCoordinatorSnapshotLineField $line 'snapshot_payload')).Value
            foreach ($operation in $envelope.aggregate.operations) {
                if ((Test-FileTransferCoordinatorBootstrapInitiationMarker $operation) -and
                    -not (Test-FileTransferCoordinatorBootstrapInitiationOperation $envelope $operation $catalog)) {
                    throw "successor_initiation_edge:$($operation.operationRole)"
                }
                $valid = if (Test-FileTransferCoordinatorBootstrapCorrelationMarker $operation) {
                    Test-FileTransferCoordinatorBootstrapCorrelationOperation $envelope $operation $catalog
                } else { Test-FileTransferCoordinatorSnapshotFamily1OperationCatalog $envelope $operation $catalog }
                if (-not $valid) { throw "successor_catalog_edge:$($operation.operationRole):$($operation.beforeState)->$($operation.afterState)" }
            }
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), path], "pwsh");
        Assert.True(result.ExitCode == 0, $"{result.Stdout}{Environment.NewLine}{result.Stderr}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BootstrapCorrelation_SuccessorRetirementRequiresExactCommittedHistory(bool outbound)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var directory = Path.Combine(FindRepoRoot(), "artifacts", "validation", "consolidation-bounded-20260907",
            "bootstrap-correlation-implementation", "retirement-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "successor-history.log");
        await File.WriteAllLinesAsync(path, Emit(FileTransferCoordinatorTests.CreateBootstrapSuccessorHistory(outbound)), new UTF8Encoding(false));
        const string script = """
            param([string]$RepoRoot, [string]$InputPath)
            & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/BootstrapCorrelationRetirementEvidence.ps1') -RepoRoot $RepoRoot -InputPath $InputPath
            """;
        string? firstOutput = null;
        foreach (var host in new[] { "powershell", "pwsh" })
        {
            var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), path], host);
            Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
            var normalized = Assert.Single(result.Stdout.Split('\n').Select(line => line.Trim()).Where(line =>
                line.StartsWith("bootstrap-retirement-normalized=", StringComparison.Ordinal)));
            if (firstOutput is not null) { Assert.Equal(firstOutput, normalized); }
            firstOutput = normalized;
        }
    }
}

public sealed partial class FileTransferCoordinatorTests
{
    internal static IReadOnlyList<FileTransferCoordinatorTransitionEnvelope> CreateBootstrapInvalidationHistory(bool outbound)
    {
        var envelopes = new List<FileTransferCoordinatorTransitionEnvelope>();
        var (sender, receiver) = CreateCorrelatedBootstrapPair(capture: Capture);
        var solicit = StartCorrelated(sender, capture: Capture);
        Capture(solicit.SnapshotEnvelope!);
        var response = StartCorrelated(receiver, Assert.IsType<FileTransferFallbackBootstrapRequestFrameV6>(solicit.Command!.Frame), Capture);
        Capture(response.SnapshotEnvelope!);
        var issued = outbound ? solicit : response;
        var delivered = FileTransferCoordinator.ApplyFallbackBootstrapPhaseReceipt(issued.State,
            CorrelatedReceipt(issued.Command!, FileTransferBootstrapPhaseReceiptStatus.Delivered));
        Assert.True(delivered.Accepted, delivered.Reason);
        Capture(delivered.SnapshotEnvelope!);
        var invalidated = FileTransferCoordinator.InvalidateFallbackBootstrapExchange(delivered.State, "test_invalidation");
        Assert.True(invalidated.Accepted, invalidated.Reason);
        Capture(invalidated.SnapshotEnvelope!);
        return envelopes;

        void Capture(FileTransferCoordinatorTransitionEnvelope envelope)
        {
            Assert.NotNull(envelope);
            if ((envelope.Direction == "outbound") == outbound) { envelopes.Add(envelope); }
        }
    }

    internal static IReadOnlyList<FileTransferCoordinatorTransitionEnvelope> CreateBootstrapSuccessorHistory(bool outbound,
        bool commitSuccessor = false, bool invalidateSourceHold = false)
    {
        var envelopes = new List<FileTransferCoordinatorTransitionEnvelope>();
        var (sender, receiver) = CreateCorrelatedBootstrapPair(capture: Capture);
        var first = CompleteCorrelatedExchange(sender, receiver, Capture);
        sender = ReactivateCorrelatedFallback(CommitCorrelatedFallback(first.Sender, Capture), Capture).State;
        receiver = ReactivateCorrelatedFallback(CommitCorrelatedFallback(first.Receiver, Capture), Capture).State;
        var second = CompleteCorrelatedExchange(sender, receiver, Capture);
        var state = outbound ? second.Sender : second.Receiver;
        AssertSecondBootstrapGeneration(state);
        if (commitSuccessor) { state = CommitCorrelatedFallback(state, Capture); }
        if (invalidateSourceHold)
        {
            var exchange = state.PersistentState.FileTransferRouteTransition.BootstrapExchange!;
            if (!commitSuccessor) { Assert.True(FileTransferCoordinator.IsFallbackBootstrapExchangeCurrent(state, exchange)); }
            var invalidated = FileTransferCoordinator.InvalidateFallbackBootstrapSourceHold(state, "test_source_hold_invalidated");
            Assert.True(invalidated.Accepted);
            Assert.False(FileTransferCoordinator.IsFallbackBootstrapExchangeCurrent(invalidated.State, exchange));
            Assert.True(invalidated.State.PersistentState.FileTransferRouteTransition.BootstrapExchange!.Completed);
            Capture(invalidated.SnapshotEnvelope!);
        }
        return envelopes;

        void Capture(FileTransferCoordinatorTransitionEnvelope envelope)
        {
            Assert.NotNull(envelope);
            if ((envelope.Direction == "outbound") == outbound) { envelopes.Add(envelope); }
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertSecondBootstrapGeneration(FileTransferCoordinatorState state)
        => Assert.Equal(2, state.PersistentState.FileTransferRouteTransition.BootstrapExchange!.Bootstrap.BootstrapGeneration);

    internal static FileTransferCoordinatorTransitionEnvelope CreateBootstrapSuccessorStartSnapshot(bool outbound)
    {
        var (sender, receiver) = CreateCorrelatedBootstrapPair();
        var completed = CompleteCorrelatedExchange(sender, receiver);
        sender = ReactivateCorrelatedFallback(CommitCorrelatedFallback(completed.Sender)).State;
        receiver = ReactivateCorrelatedFallback(CommitCorrelatedFallback(completed.Receiver)).State;
        var solicit = StartCorrelated(sender);
        var response = StartCorrelated(receiver, Assert.IsType<FileTransferFallbackBootstrapRequestFrameV6>(solicit.Command!.Frame));
        var successor = outbound ? solicit : response;
        Assert.True(successor.Accepted, successor.Reason);
        Assert.Equal(2, successor.Command!.Bootstrap.BootstrapGeneration);
        Assert.Equal(3, successor.State.CurrentLiveRouteEpoch!.EpochId);
        return Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(successor.SnapshotEnvelope);
    }

    internal static IReadOnlyList<FileTransferCoordinatorTransitionEnvelope> CreateBootstrapCorrelationSnapshotFixture(bool announceFirst, bool outbound)
    {
        var envelopes = new List<FileTransferCoordinatorTransitionEnvelope>();
        var (sender, receiver) = CreateCorrelatedBootstrapPair(capture: CaptureEnvelope);
        var solicit = StartCorrelated(sender, capture: CaptureEnvelope);
        sender = Capture(solicit);
        var first = StartCorrelated(receiver, announceFirst ? null : Assert.IsType<FileTransferFallbackBootstrapRequestFrameV6>(solicit.Command!.Frame), CaptureEnvelope);
        receiver = Capture(first);
        if (announceFirst) { receiver = Capture(ObserveCorrelated(receiver, solicit.Command!.Frame)); }
        receiver = Complete(receiver, first.Command!);
        var response = receiver.PersistentState.FileTransferRouteTransition.BootstrapExchange!.Final?.Command ?? first.Command!;
        if (announceFirst) { receiver = Complete(receiver, response); }
        sender = Complete(sender, solicit.Command!);
        var confirm = ObserveCorrelated(sender, response.Frame);
        sender = Complete(Capture(confirm), confirm.Command!);
        Assert.True(sender.PersistentState.FileTransferRouteTransition.BootstrapExchange!.Completed);
        receiver = Capture(ObserveCorrelated(receiver, confirm.Command!.Frame));
        Assert.True(receiver.PersistentState.FileTransferRouteTransition.BootstrapExchange!.Completed);
        var completed = outbound ? sender : receiver;
        var rejected = FileTransferCoordinator.ObserveFallbackBootstrapPeerFrame(completed, outbound ? response.Frame : confirm.Command.Frame,
            completed.CurrentDataSessionIdentity!, completed.CurrentDataSessionGeneration);
        Assert.False(rejected.Accepted);
        Assert.Same(completed, rejected.State);
        Capture(rejected);
        return envelopes;

        FileTransferCoordinatorState Capture(FileTransferBootstrapExchangeDecision decision)
        {
            var envelope = Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(decision.SnapshotEnvelope);
            CaptureEnvelope(envelope);
            return decision.State;
        }
        void CaptureEnvelope(FileTransferCoordinatorTransitionEnvelope envelope)
        {
            Assert.NotNull(envelope);
            if ((envelope.Direction == "outbound") == outbound) { envelopes.Add(envelope); }
        }
        FileTransferCoordinatorState Complete(FileTransferCoordinatorState state, FileTransferBootstrapPhaseCommand command)
        {
            var delivered = FileTransferCoordinator.ApplyFallbackBootstrapPhaseReceipt(state, CorrelatedReceipt(command, FileTransferBootstrapPhaseReceiptStatus.Delivered));
            Assert.True(delivered.Accepted, delivered.Reason);
            var settled = FileTransferCoordinator.ApplyFallbackBootstrapPhaseReceipt(Capture(delivered), CorrelatedReceipt(command, FileTransferBootstrapPhaseReceiptStatus.Settled));
            Assert.True(settled.Accepted, settled.Reason);
            return Capture(settled);
        }
    }
}
