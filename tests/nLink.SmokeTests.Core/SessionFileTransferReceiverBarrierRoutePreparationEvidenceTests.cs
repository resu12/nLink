using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Fact]
    [Trait("FileTransferRuntimeSuite", "InitialActivationReceiverBarrier")]
    [Trait("FileTransferRuntimeSuite", "FallbackRouteTransition")]
    public async Task ReceiverBarrier_RoutePreparationUsesRealServiceEffectsWithoutCloseEffect()
        => await CreateReceiverBarrierRoutePreparationEvidenceFixtureAsync();

    internal static async Task<IReadOnlyList<string>> CreateReceiverBarrierRoutePreparationEvidenceFixtureAsync()
    {
        var log = await CreateInboundBootstrapCheckpointEvidenceFixtureAsync(receiverBarrier: true);
        var lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r')).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        var preparations = new List<string>();
        var initialPublications = new List<string>();
        foreach (var line in lines.Where(line => line.Contains("event=filetransfer_coordinator_transition_snapshot;", StringComparison.Ordinal)))
        {
            var payload = Regex.Match(line, @"(?:^|; )snapshot_payload=([^;]+)").Groups[1].Value;
            Assert.NotEmpty(payload);
            payload = payload.Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.GetProperty("aggregate").GetProperty("operations").EnumerateArray().Any(operation =>
                operation.GetProperty("operationRole").GetString() == "route_publication" &&
                operation.GetProperty("operationGeneration").GetInt64() == 1 &&
                operation.GetProperty("beforeState").GetString() == "None" &&
                operation.GetProperty("afterState").GetString() == "Published"))
            {
                initialPublications.Add(json);
            }
            if (root.GetProperty("decisionKind").GetString() == "PrepareFileTransferRouteTransition" &&
                root.GetProperty("aggregate").GetProperty("operations").EnumerateArray().Any(operation =>
                    operation.GetProperty("operationRole").GetString() == "receiver_barrier_terminal" &&
                    operation.GetProperty("beforeState").GetString() == "None" &&
                    operation.GetProperty("afterState").GetString() == "Invalidated"))
            {
                preparations.Add(json);
            }
            Assert.DoesNotContain(root.GetProperty("aggregate").GetProperty("operations").EnumerateArray(), operation =>
                operation.GetProperty("operationRole").GetString() == "receiver_barrier_terminal" &&
                operation.GetProperty("afterState").GetString() == "Closed");
        }
        Assert.Single(preparations);
        Assert.Single(initialPublications);
        Assert.Single(lines, line => line.Contains("event=filetransfer_coordinator_authority_effect;", StringComparison.Ordinal) &&
            line.Contains("effect_kind=route_publication_forwarded;", StringComparison.Ordinal) &&
            line.Contains("operation_generation=1;", StringComparison.Ordinal));
        foreach (var effect in new[]
        {
            "route_transition_handoff_supplied", "pause_installed", "completed_drain_receipt",
            "route_commit_adopted", "route_transition_disposition_applied", "transition_pause_released",
            "recovered_epoch_publication",
        })
        {
            Assert.Contains(lines, line => line.Contains("event=filetransfer_coordinator_authority_effect;", StringComparison.Ordinal) &&
                line.Contains($"effect_kind={effect};", StringComparison.Ordinal));
        }
        Assert.DoesNotContain("effect_kind=receiver_barrier_close_applied;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_coordinator_snapshot_stream_completed;", log, StringComparison.Ordinal);
        return Array.AsReadOnly(lines);
    }

    private static void PublishInitialReceiverBarrierRegularRouteForTest(
        SessionFileTransferService service,
        LoopbackFileTransferTransport bridge,
        object context,
        string reason)
    {
        var serviceGate = typeof(SessionFileTransferService).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
        lock (serviceGate)
        {
            var before = CreateCoordinatorStateForTest(context);
            Assert.Null(before.CurrentLeg);
            Assert.Null(before.CurrentRuntimeOwner);
            Assert.Null(before.CurrentRoutePublicationToken);
            Assert.NotNull(typeof(SessionFileTransferService)
                .GetMethod("EnsureInboundTransferLegLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [context, reason, null]));
            var publishRoute = typeof(SessionFileTransferService)
                .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(method => method.Name == "TryPublishRouteSelectedIfAuthorizedLocked" &&
                    method.GetParameters()[0].ParameterType == context.GetType() &&
                    method.GetParameters()[4].ParameterType.IsByRef);
            Assert.True(Assert.IsType<bool>(publishRoute.Invoke(service,
                [context, FileTransferRouteResolverInput.FromTransport(bridge), reason, reason, null, null])));
            var state = CreateCoordinatorStateForTest(context);
            var publication = Assert.IsType<RoutePublicationToken>(state.CurrentRoutePublicationToken);
            Assert.Equal(1, publication.PublicationGeneration);
            Assert.Equal(FileTransferCoordinatorActionKind.RoutePublication, publication.ActionKind);
            Assert.Equal(FileTransferRoute.RegularNknV4Fast, publication.Route);
            Assert.Equal(FileTransferProtocol.ProtocolVersionV4, publication.ProtocolVersion);
            Assert.Equal(0, publication.LiveRouteEpoch);
            Assert.Equal(state.CurrentRuntimeOwner, publication.RuntimeOwner);
        }
    }

    private static void BindReceiverBarrierBeforeInitialRoutePreparationForTest(
        SessionFileTransferService service,
        LoopbackFileTransferTransport bridge,
        object context,
        IFileTransferDataSession dataSession)
    {
        var snapshot = Assert.IsType<RuntimeUnlockRouteCommitSnapshot>(bridge.RuntimeUnlockRouteCommitSnapshot);
        var listenerRunId = Assert.IsType<string>(snapshot.TunaPathLeaseListenerRunId);
        Assert.NotEmpty(listenerRunId);
        var negotiation = new InitialActivationReceiverBarrierNegotiation(
            dataSession.SessionId, dataSession.TransferId, snapshot.TransactionGeneration, snapshot.OfferGeneration,
            snapshot.OfferGeneration, "receiver-barrier-service-initial-offer", snapshot.TransactionGeneration,
            snapshot.LaunchGeneration, snapshot.TunaPathLeaseGeneration, listenerRunId,
            Ack: new($"test-answer-ack-{snapshot.TransactionGeneration}-{snapshot.OfferGeneration}",
                new string('a', 64), new string('b', 64), snapshot.PathProbeAckedUtcMs));
        var args = new InitialActivationReceiverBarrierBindingEventArgs(dataSession, 1, negotiation, null);
        var serviceGate = typeof(SessionFileTransferService).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
        lock (serviceGate)
        {
            Assert.Null(CreateCoordinatorStateForTest(context).CurrentRuntimeUnlockAdmission);
            typeof(SessionFileTransferService).GetMethod("OnReceiverBarrierBindingRequested", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [bridge, args]);
            Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Accepted, args.Decision!.Status);
            Assert.Equal(InitialActivationReceiverBarrierState.Bound,
                CreateCoordinatorStateForTest(context).PersistentState.InitialActivationReceiverBarrier.Current!.State);
            var admissionMethod = typeof(SessionFileTransferService).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Single(method => method.Name == "RequestRuntimeUnlockAdmissionLocked" &&
                    method.GetParameters()[0].ParameterType == context.GetType());
            var admission = Assert.IsType<RuntimeUnlockAdmissionDecision>(admissionMethod.Invoke(null,
                [context, "receiver_barrier_service_fixture", "precommit_probe_received", false]));
            Assert.True(admission.Granted, admission.Reason);
            var attached = CreateCoordinatorStateForTest(context).PersistentState.InitialActivationReceiverBarrier.Current!;
            Assert.Equal(InitialActivationReceiverBarrierState.AdmissionAttached, attached.State);
            Assert.Same(args.Decision.Authority, attached.Authority);
            Assert.Equal(negotiation, attached.Negotiation);
        }
    }
}
