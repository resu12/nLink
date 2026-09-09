using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "CoordinatorSnapshots")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferCheckpointResponderCoexistenceEvidenceTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    public async Task CheckpointResponderCarry_IndependentReceiverBarrierHasExactCrossHostConsumerParity()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var envelopes = FileTransferCoordinatorTests.CreateCheckpointResponderBarrierCoexistenceSnapshotFixture();
        var directory = Path.Combine(Path.GetTempPath(), "nlink-checkpoint-coexistence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "projected-evidence.log");
            await File.WriteAllLinesAsync(path, Emit(envelopes), new UTF8Encoding(false));
            var script = await File.ReadAllTextAsync(Path.Combine(FindRepoRoot(), "tests", "nLink.SmokeTests.Core",
                "Support", "CheckpointResponderCoexistenceSnapshotEvidence.ps1"));
            string? firstHostOutput = null;
            foreach (var host in new[] { "powershell", "pwsh" })
            {
                var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), path, directory], host);
                Assert.True(result.ExitCode == 0, $"{host} STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
                Assert.Contains("checkpoint-coexistence-evidence-ok", result.Stdout);
                var normalized = Assert.Single(result.Stdout.Split('\n').Select(line => line.Trim()).Where(line =>
                    line.StartsWith("checkpoint-coexistence-normalized=", StringComparison.Ordinal)));
                if (firstHostOutput is not null) { Assert.Equal(firstHostOutput, normalized); }
                firstHostOutput = normalized;
            }
        }
        finally { TryDeleteDirectory(directory); }
    }

    internal static string[] Emit(IReadOnlyList<FileTransferCoordinatorTransitionEnvelope> envelopes)
    {
        const BindingFlags instance = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
        var service = typeof(SessionFileTransferService);
        var emitterType = service.GetNestedType("CoordinatorSnapshotStreamEmitter", BindingFlags.NonPublic)!;
        var emitter = Activator.CreateInstance(emitterType, nonPublic: true)!;
        emitterType.GetField("drainScheduled", instance)!.SetValue(emitter, 1);
        var first = envelopes[0];
        var direction = Enum.Parse<FileTransferDirection>(first.Direction, ignoreCase: true);
        Assert.Equal(FileTransferDirection.Inbound, direction);
        Assert.Equal(true, emitterType.GetMethod("Start", instance)!.Invoke(emitter,
            [first.CoordinatorInstanceId, first.SessionId, first.TransferId, direction,
                first.Aggregate.BeforeStateGeneration, first.Aggregate.BeforeStateFingerprint]));

        // The private context is only a sink adapter for the unmodified responder
        // projector; it never participates in a Coordinator or runtime decision.
        var contextType = service.GetNestedType("InboundTransferContext", BindingFlags.NonPublic)!;
        var context = RuntimeHelpers.GetUninitializedObject(contextType);
        foreach (var (name, value) in new (string, object)[]
        {
            ("CoordinatorInstanceId", first.CoordinatorInstanceId), ("SessionId", first.SessionId),
            ("TransferId", first.TransferId), ("CoordinatorSnapshotEmitter", emitter),
        })
        {
            contextType.GetField($"<{name}>k__BackingField", instance)!.SetValue(context, value);
        }
        foreach (var envelope in envelopes) { EnqueueIntoContext(context, envelope); }
        emitterType.GetMethod("Complete", instance)!.Invoke(emitter,
            [first.CoordinatorInstanceId, first.SessionId, first.TransferId, direction]);
        var queue = (ConcurrentQueue<string>)emitterType.GetField("pending", instance)!.GetValue(emitter)!;
        return queue.Select((line, index) => SensitiveDataRedactor.Redact(
            $"{line}; log_writer_id=checkpoint-coexistence-fixture; log_writer_sequence={index + 1}")).ToArray();
    }

    internal static void EnqueueIntoContext(object context, FileTransferCoordinatorTransitionEnvelope envelope)
    {
        const BindingFlags instance = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
        const BindingFlags statics = BindingFlags.NonPublic | BindingFlags.Static;
        var service = typeof(SessionFileTransferService);
        var contextType = service.GetNestedType("InboundTransferContext", BindingFlags.NonPublic)!;
        Assert.Equal(contextType, context.GetType());
        var emitter = contextType.GetProperty("CoordinatorSnapshotEmitter", instance)!.GetValue(context)!;
        var emitterType = emitter.GetType();
        var direction = Enum.Parse<FileTransferDirection>(envelope.Direction, ignoreCase: true);
        Assert.Equal(FileTransferDirection.Inbound, direction);
        // Project into the existing stream through a generation-specific sink.
        // Never replace the real service context's persistent authority graph.
        var sink = RuntimeHelpers.GetUninitializedObject(contextType);
        foreach (var (name, value) in new (string, object)[]
        {
            ("CoordinatorInstanceId", envelope.CoordinatorInstanceId), ("SessionId", envelope.SessionId),
            ("TransferId", envelope.TransferId), ("CoordinatorSnapshotEmitter", emitter),
        })
        {
            contextType.GetField($"<{name}>k__BackingField", instance)!.SetValue(sink, value);
        }
        contextType.GetProperty("CoordinatorPersistentState", instance)!.SetValue(sink,
            new FileTransferCoordinatorPersistentState { SnapshotStateGeneration = envelope.Aggregate.AfterStateGeneration });
        var responder = service.GetMethod("EnqueueResponderProbeHandoffDecisionAuthorityEffect", statics)!;
        var pressure = service.GetMethods(statics).Single(method =>
            method.Name == "EnqueueRegularV4PressureDecisionAuthorityEffect" && method.GetParameters()[0].ParameterType == emitterType);
        var fallback = service.GetMethods(statics).Single(method =>
            method.Name == "EnqueueFallbackTailYieldDecisionAuthorityEffect" && method.GetParameters()[0].ParameterType == emitterType);
        var enqueueEffect = emitterType.GetMethod("EnqueueAuthorityEffect", instance)!;
        {
            emitterType.GetMethod("Enqueue", instance)!.Invoke(emitter, [envelope]);
            responder.Invoke(null, [sink, envelope]);
            object?[] common = [emitter, envelope.CoordinatorInstanceId, envelope.SessionId,
                envelope.TransferId, direction, envelope.Aggregate.AfterStateGeneration, envelope];
            pressure.Invoke(null, common);
            fallback.Invoke(null, common);

            // Supply exact Coordinator-authored stage receipts through production
            // effect indexing/projection. These are analyzer contract fixtures,
            // not assertions that a physical transport callback was executed.
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
                foreach (var kind in kinds) { Effect(operation, operation, kind); }
                if (operation.FallbackTailYield is not null && operation.OperationRole == "yield_current")
                {
                    var stage = envelope.DecisionKind switch
                    {
                        "FallbackTailQuiescent" => "yield",
                        "ApplyFallbackTailPeerProofReceipt" => "paired_release",
                        _ => null,
                    };
                    if (stage is not null)
                    {
                        foreach (var receipt in operation.FallbackTailYield.Receipts.Where(receipt => receipt.Stage == stage && receipt.ReceiptValidated))
                        {
                            Effect(operation, receipt, stage == "yield" ? "recovery_owner_retired_after_yield" : "paired_release_published");
                        }
                    }
                }
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
    }
}
