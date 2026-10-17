using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Theory]
    [InlineData("receipt_replaced")]
    [InlineData("barrier_replaced")]
    [InlineData("listener_replaced")]
    [InlineData("answer_expired")]
    [InlineData("session_replaced")]
    [InlineData("transaction_replaced")]
    [InlineData("terminal")]
    [InlineData("publication_replaced")]
    [InlineData("data_session_replaced")]
    [InlineData("repair_generation_replaced")]
    [InlineData("dispatch_rejected")]
    [InlineData("after_probe_receipt_replaced")]
    [InlineData("after_probe_listener_replaced")]
    [InlineData("after_probe_answer_expired")]
    [Trait("FileTransferInfraSuite", "SenderDialerRetry")]
    public Task JoinedFallbackRetry_InstalledListenerAnswerRejectsStaleEvidence(string control)
        => RunJoinedSenderDialerRetryAsync(helperOnlyCold: true,
            verifyInstalledListenerAnswer: true, listenerAnswerControl: control);

    private static async Task VerifyInstalledListenerAnswerRejectionAsync(
        NknSignalingTransport transport, SessionFileTransferService service, object context,
        RetryableTunaAccelerationSession listener, object answer, string control)
    {
        // The sender's install follow-up is held: no peer probe can race these
        // mutations. The retained Answer came through the authenticated clients.
        await WaitUntilAsync(() => GetPrivateField(transport, "runtimeUnlockRecoveryRetryState") is { } state &&
            state.GetType().GetProperty("ContractState")!.GetValue(state)!.ToString() == "Completed",
            TimeSpan.FromSeconds(3));
        var coordinator = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(service, context);
        var capture = typeof(SessionFileTransferService).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == "TryCreateRuntimeUnlockPeerPreCommitAnswerAcceptanceScope" &&
                method.GetParameters().Length == 2);
        object?[] args = [coordinator, null];
        Assert.True((bool)capture.Invoke(null, args)!);
        var scope = Assert.IsType<RuntimeUnlockPeerPreCommitAnswerAcceptanceScope>(args[1]);
        var sourceCurrent = typeof(NknSignalingTransport).GetMethod(
            "IsRetainedListenerAnswerSourceCurrentUnsafe", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var accelerationGate = GetPrivateField(transport, "accelerationGate")!;
        var afterProbe = control.StartsWith("after_probe_", StringComparison.Ordinal);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var probe = new RuntimeUnlockPeerPreCommitAnswerAcceptanceReceipt(
            coordinator.SessionId, coordinator.TransferId, FileTransferDirection.Inbound, scope.Admission,
            1, 4, 1, "stale-listener-answer-probe", new string('a', 64), FileTransferTransportKind.Tuna,
            now, now + 1, scope.DataSessionGeneration, scope);
        var proofProvider = (IRuntimeUnlockRouteCommitProofProvider)transport;
        if (afterProbe)
        {
            Assert.Equal(RuntimeUnlockPeerPreCommitAnswerAcceptanceStatus.Held,
                ((IRuntimeUnlockPeerPreCommitAnswerAcceptanceObserver)transport)
                    .ObserveRuntimeUnlockPeerPreCommitAnswerAcceptance(probe).Status);
            var before = Assert.IsType<RuntimeUnlockTransactionSnapshot>(
                GetPrivateField(transport, "runtimeUnlockTransactionState"));
            Assert.True(proofProvider.NotifyRuntimeUnlockPathProbeStarted(coordinator.SessionId,
                coordinator.TransferId, 0, probe.ProbeId, FileTransferTransportKind.Tuna, "test_installed_answer_probe",
                scope, probe.PeerTransactionGeneration, probe.PeerOfferGeneration, probe.PeerTunaPathLeaseGeneration,
                before.TransactionGeneration, before.OfferGeneration, before.TunaPathLeaseGeneration,
                before.TunaPathLeaseListenerRunId, before.PathProbeState));
            control = control["after_probe_".Length..];
        }
        var logStart = GetOperationalLogLength();
        lock (accelerationGate)
        {
            // Dispatch completion can replace the immutable evidence record.
            // Mutate the retained instance, not a prior observation of it.
            answer = GetPrivateField(transport, "runtimeUnlockResponderAnswerProbeConvergence")!;
            Assert.True((bool)sourceCurrent.Invoke(transport, [answer, scope])!);
            // Completed retry evidence must not revive dispatch permission.
            var retry = GetPrivateField(transport, "runtimeUnlockRecoveryRetryState")!;
            var receipt = GetLastCommittedRuntimeUnlockRetryDispatchInstallReceipt(transport)!.Value;
            object?[] validation = [retry, receipt, null, true];
            Assert.False((bool)typeof(NknSignalingTransport).GetMethod(
                "TryValidateRuntimeUnlockRetryDispatchInstallReceiptCurrentUnsafe",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(transport, validation)!);

            switch (control)
            {
                case "receipt_replaced":
                    var shadow = GetRuntimeUnlockRetryDispatchInstallShadow(transport);
                    shadow.GetType().GetProperty("LastCommittedReceipt")!.SetValue(shadow,
                        receipt with { InstallGeneration = receipt.InstallGeneration + 1 });
                    break;
                case "barrier_replaced":
                    SetPrivateField(transport, "runtimeUnlockRetiredAnswerBarrier", null);
                    break;
                case "listener_replaced":
                    SetPrivateField(listener, "listenerAdoptionSnapshot", null);
                    break;
                case "answer_expired":
                    answer.GetType().GetProperty("AnswerDeadlineUtcMs")!.SetValue(answer,
                        DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds());
                    break;
                case "session_replaced":
                case "transaction_replaced":
                case "terminal":
                    var current = Assert.IsType<RuntimeUnlockTransactionSnapshot>(
                        GetPrivateField(transport, "runtimeUnlockTransactionState"));
                    SetPrivateField(transport, "runtimeUnlockTransactionState", control switch
                    {
                        "session_replaced" => current with { SessionId = "replacement-session" },
                        "transaction_replaced" => current with { TransactionGeneration = current.TransactionGeneration + 1 },
                        _ => current with { State = RuntimeUnlockTransactionState.Failed },
                    });
                    break;
                case "publication_replaced":
                    scope = scope with { RoutePublication = scope.RoutePublication with
                        { PublicationGeneration = scope.RoutePublication.PublicationGeneration + 1 } };
                    break;
                case "data_session_replaced":
                    scope = scope with { DataSessionGeneration = scope.DataSessionGeneration + 1,
                        DataSessionIdentity = scope.DataSessionIdentity + "-replacement" };
                    break;
                case "repair_generation_replaced":
                    scope = scope with { BridgeRecoveryGeneration = scope.BridgeRecoveryGeneration + 1 };
                    break;
                case "dispatch_rejected":
                    retry.GetType().GetProperty("DispatchAuthorityRejected")!.SetValue(retry, true);
                    break;
            }
        }

        if (afterProbe)
        {
            var before = Assert.IsType<RuntimeUnlockTransactionSnapshot>(
                GetPrivateField(transport, "runtimeUnlockTransactionState"));
            Assert.True(proofProvider.NotifyRuntimeUnlockPathProbeResult(coordinator.SessionId,
                coordinator.TransferId, 0, probe.ProbeId, FileTransferTransportKind.Tuna, true, "test_stale_answer_probe_ack",
                scope, probe.PeerTransactionGeneration, probe.PeerOfferGeneration, probe.PeerTunaPathLeaseGeneration,
                before.TransactionGeneration, before.OfferGeneration, before.TunaPathLeaseGeneration,
                before.TunaPathLeaseListenerRunId, before.PathProbeState));
            var retained = GetPrivateField(transport, "runtimeUnlockResponderAnswerProbeConvergence")!;
            Assert.NotEqual("Complete", retained.GetType().GetProperty("State")!.GetValue(retained)!.ToString());
            Assert.DoesNotContain("event=runtime_unlock_precommit_responder_peer_probe_handoff_bound;",
                ReadOperationalLogTail(logStart), StringComparison.Ordinal);
        }
        else
        {
            var decision = ((IRuntimeUnlockPeerPreCommitAnswerAcceptanceObserver)transport)
                .ObserveRuntimeUnlockPeerPreCommitAnswerAcceptance(probe with
                    { ReceiveScope = scope, DataSessionGeneration = scope.DataSessionGeneration });
            Assert.Equal(RuntimeUnlockPeerPreCommitAnswerAcceptanceStatus.Rejected, decision.Status);
            Assert.Equal("AwaitingPeerProbe", answer.GetType().GetProperty("State")!.GetValue(answer)!.ToString());
        }
        Assert.Equal(FileTransferRoute.PostTunaFallbackV6,
            SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(service, context)
                .CurrentRoutePublicationToken!.Value.Route);
    }
}
