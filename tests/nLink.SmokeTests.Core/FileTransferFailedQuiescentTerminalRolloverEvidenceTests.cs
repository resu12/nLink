using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "CoordinatorSnapshots")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferFailedQuiescentTerminalRolloverEvidenceTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData(FileTransferDirection.Inbound, false)]
    [InlineData(FileTransferDirection.Outbound, false)]
    [InlineData(FileTransferDirection.Inbound, true)]
    [InlineData(FileTransferDirection.Outbound, true)]
    public async Task FailedQuiescentTerminalRollover_NoPeerProofPreservesExactEvidenceAcrossBothHosts(
        FileTransferDirection direction, bool priorTerminal)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var envelopes = FileTransferCoordinatorTests.CreateFailedQuiescentTerminalRolloverSnapshotFixture(
            direction, priorTerminal, out var supersession, out var failed, out var oldTerminal,
            includeSuccessorHistory: true, includePeerProof: false);
        var directory = Path.Combine(Path.GetTempPath(), "nlink-no-peer-supersession-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "production-evidence.log");
            await File.WriteAllLinesAsync(path, Emit(envelopes), new UTF8Encoding(false));
            var script = await File.ReadAllTextAsync(Path.Combine(FindRepoRoot(), "tests", "nLink.SmokeTests.Core",
                "Support", "NoPeerFailedQuiescentSnapshotEvidence.ps1"));
            string? firstHostOutput = null;
            foreach (var host in new[] { "powershell", "pwsh" })
            {
                var result = await RunPowerShellScriptTextAsync(script,
                    [FindRepoRoot(), path, directory,
                        SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting(supersession).TransitionId,
                        SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting(failed).TransitionId,
                        oldTerminal is null ? "(none)" : SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting(oldTerminal).TransitionId], host);
                Assert.True(result.ExitCode == 0, $"{direction}/{priorTerminal}/{host} STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
                var normalized = Assert.Single(result.Stdout.Split('\n').Select(line => line.Trim()).Where(line =>
                    line.StartsWith("no-peer-supersession-normalized=", StringComparison.Ordinal)));
                if (firstHostOutput is not null) { Assert.Equal(firstHostOutput, normalized); }
                firstHostOutput = normalized;
            }
        }
        finally { TryDeleteDirectory(directory); }
    }

    [Theory]
    [InlineData(FileTransferDirection.Inbound, false)]
    [InlineData(FileTransferDirection.Outbound, false)]
    [InlineData(FileTransferDirection.Inbound, true)]
    [InlineData(FileTransferDirection.Outbound, true)]
    public async Task FailedQuiescentTerminalRollover_PendingOriginProvenanceIsExactAcrossBothHosts(
        FileTransferDirection direction, bool priorTerminal)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var envelopes = FileTransferCoordinatorTests.CreateFailedQuiescentTerminalRolloverSnapshotFixture(
            direction, priorTerminal, out var supersession, out var failed, out _, includeSuccessorHistory: true);
        var directory = Path.Combine(Path.GetTempPath(), "nlink-pending-failed-quiescent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "production-evidence.log");
            await File.WriteAllLinesAsync(path, Emit(envelopes), new UTF8Encoding(false));
            var script = await File.ReadAllTextAsync(Path.Combine(FindRepoRoot(), "tests", "nLink.SmokeTests.Core",
                "Support", "PendingFailedQuiescentPeerProofSnapshotEvidence.ps1"));
            string? firstHostOutput = null;
            foreach (var host in new[] { "powershell", "pwsh" })
            {
                var result = await RunPowerShellScriptTextAsync(script,
                    [FindRepoRoot(), path,
                        SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting(supersession).TransitionId,
                        SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting(failed).TransitionId], host);
                Assert.True(result.ExitCode == 0, $"{direction}/{priorTerminal}/{host} STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
                var normalized = Assert.Single(result.Stdout.Split('\n').Select(line => line.Trim()).Where(line =>
                    line.StartsWith("pending-failed-quiescent-normalized=", StringComparison.Ordinal)));
                if (firstHostOutput is not null) { Assert.Equal(firstHostOutput, normalized); }
                firstHostOutput = normalized;
            }
        }
        finally { TryDeleteDirectory(directory); }
    }

    [Fact]
    public async Task FailedQuiescentTerminalRollover_ProductionEvidencePinsSemanticStopAcrossBothHosts()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        // One production-width fixture includes the old local proof, release,
        // retry installation and terminal retirement. The Coordinator tests
        // separately cover the direction/three-operation control combinations.
        var envelopes = FileTransferCoordinatorTests.CreateFailedQuiescentTerminalRolloverSnapshotFixture(
            FileTransferDirection.Outbound, true, out var supersession, out var failed, out var oldTerminal);
        var directory = Path.Combine(Path.GetTempPath(), "nlink-failed-quiescent-rollover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "production-evidence.log");
            await File.WriteAllLinesAsync(path, Emit(envelopes), new UTF8Encoding(false));
            var script = await File.ReadAllTextAsync(Path.Combine(FindRepoRoot(), "tests", "nLink.SmokeTests.Core",
                "Support", "FailedQuiescentTerminalRolloverSnapshotEvidence.ps1"));
            string? firstHostOutput = null;
            foreach (var host in new[] { "powershell", "pwsh" })
            {
                var result = await RunPowerShellScriptTextAsync(script,
                    [FindRepoRoot(), path, directory,
                        SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting(supersession).TransitionId,
                        SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting(failed).TransitionId,
                        SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting(oldTerminal!).TransitionId], host);
                Assert.True(result.ExitCode == 0, $"{host} STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
                Assert.Contains("failed-quiescent-rollover-evidence-ok", result.Stdout);
                var normalized = Assert.Single(result.Stdout.Split('\n').Select(line => line.Trim()).Where(line =>
                    line.StartsWith("failed-quiescent-rollover-normalized=", StringComparison.Ordinal)));
                if (firstHostOutput is not null)
                {
                    Assert.True(firstHostOutput == normalized, DescribeHostDifference(firstHostOutput, normalized));
                }
                firstHostOutput = normalized;
            }
        }
        finally { TryDeleteDirectory(directory); }
    }

    private static string DescribeHostDifference(string expected, string actual)
    {
        if (expected == actual) { return string.Empty; }
        const string prefix = "failed-quiescent-rollover-normalized=";
        using var expectedCases = JsonDocument.Parse(expected[prefix.Length..]);
        using var actualCases = JsonDocument.Parse(actual[prefix.Length..]);
        foreach (var expectedCase in expectedCases.RootElement.EnumerateArray())
        {
            var name = expectedCase.GetProperty("Case").GetString();
            var actualCase = actualCases.RootElement.EnumerateArray().Single(value => value.GetProperty("Case").GetString() == name);
            if (expectedCase.GetRawText() == actualCase.GetRawText()) { continue; }
            using var expectedBoundaries = JsonDocument.Parse(expectedCase.GetProperty("Boundaries").GetString()!);
            using var actualBoundaries = JsonDocument.Parse(actualCase.GetProperty("Boundaries").GetString()!);
            foreach (var expectedBoundary in expectedBoundaries.RootElement.EnumerateArray())
            {
                var id = expectedBoundary.GetProperty("boundaryId").GetString();
                var matches = actualBoundaries.RootElement.EnumerateArray().Where(value => value.GetProperty("boundaryId").GetString() == id).ToArray();
                if (matches.Length != 1) { return $"Cross-host case={name}; boundary={id}; actual matching count={matches.Length}."; }
                if (expectedBoundary.GetRawText() != matches[0].GetRawText())
                {
                    return $"Cross-host case={name}; expected={Summary(expectedBoundary)}; actual={Summary(matches[0])}.";
                }
            }
            return $"Cross-host case={name}; boundary order, route result, or GUI normalization differs.";
        }
        return "Cross-host normalized case ordering or count differs.";

        static string Summary(JsonElement boundary)
        {
            string Read(string name) => boundary.TryGetProperty(name, out var value) ? value.ToString() : "(absent)";
            var operations = boundary.TryGetProperty("operations", out var values)
                ? string.Join(",", values.EnumerateArray().Select(operation =>
                    $"{operation.GetProperty("operationKey").GetString()}@{operation.GetProperty("authorityId").GetString()}"))
                : "(absent)";
            return $"id={Read("boundaryId")}; writer={Read("writer")}:{Read("writerSequence")}; class={Read("taxonomyClass")}; variant={Read("variant")}; operations=[{operations}]";
        }
    }

    private static string[] Emit(IReadOnlyList<FileTransferCoordinatorTransitionEnvelope> envelopes)
    {
        const BindingFlags instance = BindingFlags.NonPublic | BindingFlags.Instance;
        const BindingFlags statics = BindingFlags.NonPublic | BindingFlags.Static;
        var service = typeof(SessionFileTransferService);
        var emitterType = service.GetNestedType("CoordinatorSnapshotStreamEmitter", BindingFlags.NonPublic)!;
        var emitter = Activator.CreateInstance(emitterType, nonPublic: true)!;
        // Replace only the asynchronous log sink. Projection, receipt indexing,
        // continuity and fresh-effect selection remain production behavior.
        emitterType.GetField("drainScheduled", instance)!.SetValue(emitter, 1);
        var first = envelopes[0];
        var direction = Enum.Parse<FileTransferDirection>(first.Direction, ignoreCase: true);
        Assert.Equal(true, emitterType.GetMethod("Start", instance)!.Invoke(emitter,
            [first.CoordinatorInstanceId, first.SessionId, first.TransferId, direction,
                first.Aggregate.BeforeStateGeneration, first.Aggregate.BeforeStateFingerprint]));
        var pressure = Projector("EnqueueRegularV4PressureDecisionAuthorityEffect");
        var fallback = Projector("EnqueueFallbackTailYieldDecisionAuthorityEffect");
        var fallbackStage = Projector("EnqueueFallbackTailYieldAuthorityEffect");
        var enqueueEffect = emitterType.GetMethod("EnqueueAuthorityEffect", instance)!;
        foreach (var envelope in envelopes)
        {
            emitterType.GetMethod("Enqueue", instance)!.Invoke(emitter, [envelope]);
            object?[] common = [emitter, envelope.CoordinatorInstanceId, envelope.SessionId,
                envelope.TransferId, direction, envelope.Aggregate.AfterStateGeneration, envelope];
            pressure.Invoke(null, common);
            fallback.Invoke(null, common);
            if (envelope.DecisionKind == "FallbackTailQuiescent")
            {
                fallbackStage.Invoke(null, [.. common, "yield", "recovery_owner_retired_after_yield"]);
            }
            if (envelope.DecisionKind is "ApplyFallbackTailPeerProofReceipt" or "ApplyFallbackTailLocalProofReceipt")
            {
                fallbackStage.Invoke(null, [.. common, "paired_release", "paired_release_published"]);
            }

            // Exact Coordinator-authored route/retry receipts supply supporting
            // analyzer contract records through the production sink. This is
            // not a claim that physical callbacks ran or a live route passed.
            // No copied peer proof or displaced-terminal effect is supplied.
            foreach (var operation in envelope.Aggregate.Operations)
            {
                string[] kinds = [];
                if (operation.Family == "route_runtime" && operation.BeforeState == "None")
                {
                    kinds = operation.OperationRole switch
                    {
                        "route_publication" when operation.AfterState == "Published" => ["route_publication_forwarded"],
                        "route_commit" when operation.AfterState == "Committed" => ["route_commit_adopted", "target_runtime_leg_installed"],
                        _ => [],
                    };
                }
                else if (operation.Family == "route_transition")
                {
                    kinds = envelope.DecisionKind switch
                    {
                        "BeginFileTransferRouteTransitionPhysicalDrain" when operation.OperationRole == "current" => ["pause_installed"],
                        "CommitFileTransferRouteTransition" when operation.OperationRole == first.Direction => ["completed_drain_receipt"],
                        "CommitFileTransferRouteTransition" when operation.OperationRole == "terminal" &&
                            operation.BeforeState == "None" && operation.AfterState == "Committed" => ["route_commit_adopted", "target_runtime_leg_installed"],
                        "ApplyFileTransferRouteTransitionDispositionReceipt" when operation.OperationRole == first.Direction =>
                            ["route_transition_disposition_applied", "transition_pause_released", "recovered_epoch_publication"],
                        "ApplyFileTransferRouteTransitionRetainedFrameReceipt" when operation.OperationRole == first.Direction =>
                            ["retained_frame_response", "retained_frame_applied"],
                        _ => [],
                    };
                }
                else if (operation.Family == "runtime_unlock_retry_lifecycle")
                {
                    if (operation.OperationRole == "scope_invalidation" && operation.AfterState == "Applied")
                    {
                        kinds = ["scope_invalidation_applied"];
                    }
                    var stage = envelope.DecisionKind switch
                    {
                        "ObserveRuntimeUnlockRetryDispatchStage" when operation.OperationRole == "attempt_pending" => "stage",
                        "CommitRuntimeUnlockRetryDispatchInstall" when operation.OperationRole == "attempt_installed" &&
                            operation.AfterState == "Installed" => "install",
                        _ => null,
                    };
                    if (stage is not null)
                    {
                        foreach (var receipt in operation.RuntimeUnlockRetry!.Receipts.Where(receipt => receipt.Stage == stage))
                        {
                            Effect(operation, receipt, stage == "stage" ? "dispatch_stage_observed" : "install_committed");
                        }
                    }
                }
                foreach (var kind in kinds) { Effect(operation, operation, kind); }
            }

            void Effect(FileTransferCoordinatorOperationTransitionSnapshot operation, object receipt, string kind)
            {
                object Read(string name) => receipt.GetType().GetProperty(name)!.GetValue(receipt)!;
                Assert.True((bool)Read("ReceiptValidated"));
                var generation = (long)Read("ReceiptGeneration");
                enqueueEffect.Invoke(emitter,
                    [envelope.CoordinatorInstanceId, envelope.SessionId, envelope.TransferId, direction,
                        envelope.Aggregate.AfterStateGeneration, operation.Family, operation.AuthorityId,
                        operation.OperationGeneration, Read("ReceiptIdentity"), generation, Read("ReceiptStatus"),
                        Read("ReceiptFingerprint"), kind, $"{operation.OperationKey}:{kind}", Math.Max(1, generation), operation.OperationKey]);
            }
        }
        emitterType.GetMethod("Complete", instance)!.Invoke(emitter,
            [first.CoordinatorInstanceId, first.SessionId, first.TransferId, direction]);
        var queue = (ConcurrentQueue<string>)emitterType.GetField("pending", instance)!.GetValue(emitter)!;
        return queue.Select((line, index) => SensitiveDataRedactor.Redact(
            $"{line}; log_writer_id=failed-quiescent-rollover-fixture; log_writer_sequence={index + 1}")).ToArray();

        MethodInfo Projector(string name) => service.GetMethods(statics).Single(method =>
            method.Name == name && method.GetParameters()[0].ParameterType == emitterType);
    }
}
