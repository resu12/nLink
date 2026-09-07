using System.Collections.Concurrent;
using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "CoordinatorSnapshots")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferRecoverySuccessorEvidenceTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData(FileTransferDirection.Outbound, false)]
    [InlineData(FileTransferDirection.Inbound, false)]
    [InlineData(FileTransferDirection.Outbound, true)]
    [InlineData(FileTransferDirection.Inbound, true)]
    public async Task RecoverySuccessor_PressureHandoffUsesRealEmitterAndIndependentConsumers(
        FileTransferDirection direction, bool predecessorProofObserved)
    {
        if (!OperatingSystem.IsWindows()) { return; }

        var bindings = new Dictionary<string, FileTransferCoordinatorAuthorityEffectBinding>(StringComparer.Ordinal);
        var envelopes = FileTransferCoordinatorTests.CreateCompletedRecoveryPressureSuccessorSnapshotFixture(
            direction, predecessorProofObserved, decision =>
            {
                if (decision.SnapshotAuthorityEffectBinding is { } binding)
                {
                    bindings.Add(decision.SnapshotEnvelope!.TransitionId, binding);
                }
            });
        var terminalBindings = new Dictionary<string, FileTransferCoordinatorAuthorityEffectBinding>(StringComparer.Ordinal);
        var terminalEnvelopes = FileTransferCoordinatorTests.CreateCompletedRecoveryPressureSuccessorSnapshotFixture(
            direction, predecessorProofObserved, decision =>
            {
                if (decision.SnapshotAuthorityEffectBinding is { } binding)
                {
                    terminalBindings.Add(decision.SnapshotEnvelope!.TransitionId, binding);
                }
            }, terminalizeUnprovenSuccessor: true);
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-recovery-successor-evidence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            await File.WriteAllLinesAsync(Path.Combine(tempRoot, "complete.log"), Emit(envelopes, bindings));
            var completionIndex = envelopes.Select((envelope, index) => (envelope, index))
                .Last(pair => pair.envelope.DecisionKind == nameof(FileTransferCoordinator.ObserveReceiveRecoveryExecutionReceipt)).index;
            await File.WriteAllLinesAsync(Path.Combine(tempRoot, "unproven.log"),
                Emit(envelopes.Take(completionIndex + 1).ToArray(), bindings));
            await File.WriteAllLinesAsync(Path.Combine(tempRoot, "terminal-unproven.log"),
                Emit(terminalEnvelopes, terminalBindings));
            var script = File.ReadAllText(Path.Combine(FindRepoRoot(), "tests", "nLink.SmokeTests.Core",
                "Support", "RecoverySuccessorHandoffSnapshotEvidence.ps1"));
            var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), tempRoot]);
            Assert.True(result.ExitCode == 0, $"STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
        }
        finally { TryDeleteDirectory(tempRoot); }
    }

    private static string[] Emit(
        IReadOnlyList<FileTransferCoordinatorTransitionEnvelope> envelopes,
        IReadOnlyDictionary<string, FileTransferCoordinatorAuthorityEffectBinding> bindings)
    {
        var service = typeof(SessionFileTransferService);
        var emitterType = service.GetNestedType("CoordinatorSnapshotStreamEmitter", BindingFlags.NonPublic)!;
        var emitter = Activator.CreateInstance(emitterType, nonPublic: true)!;
        // Capture the real emitter queue synchronously. Only the asynchronous
        // log sink is replaced; projection, continuity, receipt indexing and
        // effect selection all execute production code.
        emitterType.GetField("drainScheduled", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(emitter, 1);
        var first = envelopes[0];
        var direction = Enum.Parse<FileTransferDirection>(first.Direction, ignoreCase: true);
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var started = emitterType.GetMethod("Start", flags)!.Invoke(emitter,
            [first.CoordinatorInstanceId, first.SessionId, first.TransferId, direction,
                first.Aggregate.BeforeStateGeneration, first.Aggregate.BeforeStateFingerprint]);
        Assert.Equal(true, started);

        var pressure = service.GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Single(method =>
            method.Name == "EnqueueRegularV4PressureDecisionAuthorityEffect" &&
            method.GetParameters()[0].ParameterType == emitterType);
        var recoveryMethods = service.GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Where(method =>
            method.Name == "EnqueueReceiveRecoveryAuthorityEffect" &&
            method.GetParameters()[0].ParameterType == emitterType).ToArray();
        var exactRecovery = recoveryMethods.Single(method => method.GetParameters()[^1].ParameterType ==
            typeof(FileTransferCoordinatorAuthorityEffectBinding));
        var roleRecovery = recoveryMethods.Single(method => method.GetParameters().Length == 9);
        var recoveryKind = service.GetMethod("GetReceiveRecoveryDecisionEffect", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var envelope in envelopes)
        {
            emitterType.GetMethod("Enqueue", flags)!.Invoke(emitter, [envelope]);
            if (envelope.DecisionKind == nameof(FileTransferCoordinatorActionKind.RoutePublication))
            {
                var publication = Assert.Single(envelope.Aggregate.Operations, operation =>
                    operation.Family == "route_runtime" && operation.OperationRole == "route_publication");
                emitterType.GetMethod("EnqueueAuthorityEffect", flags)!.Invoke(emitter,
                    [envelope.CoordinatorInstanceId, envelope.SessionId, envelope.TransferId, direction,
                        envelope.Aggregate.AfterStateGeneration, publication.Family, publication.AuthorityId,
                        publication.OperationGeneration, publication.ReceiptIdentity, publication.ReceiptGeneration,
                        publication.ReceiptStatus, publication.ReceiptFingerprint, "route_publication_forwarded",
                        publication.ReceiptIdentity, publication.ReceiptGeneration, null]);
            }
            object?[] common = [emitter, envelope.CoordinatorInstanceId, envelope.SessionId,
                envelope.TransferId, direction, envelope.Aggregate.AfterStateGeneration, envelope];
            pressure.Invoke(null, common);
            if (bindings.TryGetValue(envelope.TransitionId, out var binding))
            {
                exactRecovery.Invoke(null, [.. common, binding]);
            }
            else if (recoveryKind.Invoke(null, [envelope]) is ValueTuple<string, string> effect)
            {
                roleRecovery.Invoke(null, [.. common, effect.Item1, effect.Item2]);
            }
        }
        emitterType.GetMethod("Complete", flags)!.Invoke(emitter,
            [first.CoordinatorInstanceId, first.SessionId, first.TransferId, direction]);
        var queue = (ConcurrentQueue<string>)emitterType.GetField("pending", flags)!.GetValue(emitter)!;
        return queue.Select((line, index) => SensitiveDataRedactor.Redact(
            $"{line}; log_writer_id=recovery-successor-writer; log_writer_sequence={index + 1}")).ToArray();
    }
}
