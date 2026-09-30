using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    // Production Service owns local proof creation; the fixture never calls the
    // local receipt factory/application or emits an authority effect itself.
    internal static async Task WithServiceYieldEvidenceSenderAsync(
        NknSignalingTransport transport, IFileTransferDataSession data, bool inbound,
        Func<Task> inspect)
    {
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        var context = inbound
            ? CreatePostTunaFallbackV6InboundContext(data.TransferId, data.SessionId, data)
            : CreatePostTunaFallbackV6OutboundContext(data.TransferId, sessionId: data.SessionId, dataSession: data);
        SetPrivateProperty(context, "PullPostTunaRecoveryActive", false);
        if (!inbound) { SetPrivateProperty(context, "PullTransportFrontierOnlyRepairActive", false); }
        var direction = inbound ? "Inbound" : "Outbound";
        typeof(SessionFileTransferService).GetField(inbound ? "inboundTransfer" : "outboundTransfer",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, context);
        var leg = GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg");
        var start = GetOperationalLogLength();
        lock (GetServiceGate(service))
        {
            object?[] args = [context, leg, leg.TransportEpochId, 100, 99, "service_yield_evidence", null];
            Assert.True(Call($"TryMark{direction}FallbackTailQuiescentWithCoordinatorLocked", args));
            var yield = Assert.IsType<FallbackTailYieldDecision>(args[^1]);
            Assert.True(Call($"Record{direction}FallbackTailLocalYieldLocked", context, leg, yield, 100, 99,
                "service_yield_evidence"));
        }
        await WaitUntilAsync(() => GetCoordinatorFallbackTailYieldSubstate(context).CurrentOperation?.LocalProof is not null, 5000);
        var envelope = GetPrivateProperty<FileTransferCoordinatorTransitionEnvelope>(context, "LastFallbackTailYieldSnapshotEnvelope");
        var projected = SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting(envelope);
        var row = Assert.Single(ReadOperationalLogTail(start).Split('\n'), line =>
            line.Contains("yield_evidence_role=receipt_association;", StringComparison.Ordinal) &&
            line.Contains("yield_receipt_stage=local_proof;", StringComparison.Ordinal));
        Assert.Contains("yield_evidence_status=bound;", row, StringComparison.Ordinal);
        Assert.Contains("yield_receipt_decision=Accepted;", row, StringComparison.Ordinal);
        Assert.Contains($"transition_id={projected.TransitionId};", row, StringComparison.Ordinal);
        var proof = GetCoordinatorFallbackTailYieldSubstate(context).CurrentOperation!.Value.LocalProof!.Value;
        Assert.Contains($"receipt_identity={SessionFileTransferService.ProjectYieldEvidenceToken(proof.ProofIdentity)};", row, StringComparison.Ordinal);
        Assert.Contains("authority_effect=none", row, StringComparison.Ordinal);
        await inspect();

        bool Call(string name, params object?[] args)
            => (bool)typeof(SessionFileTransferService).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, args)!;
    }

    internal static async Task CheckServiceYieldEvidenceReceiptsAsync(
        FileTransferReceivedDataFrame queued, FileTransferFallbackTailYieldProofReceivedEventArgs raised,
        bool eventFirst, string evidenceMode)
    {
        using var service = new SessionFileTransferService();
        var frame = Assert.IsType<FileTransferFallbackTailYieldFrameV6>(queued.Frame);
        var inbound = frame.Direction == "outbound";
        var context = inbound
            ? CreatePostTunaFallbackV6InboundContext(frame.TransferId, frame.SessionId)
            : CreatePostTunaFallbackV6OutboundContext(frame.TransferId, sessionId: frame.SessionId);
        typeof(SessionFileTransferService).GetField(inbound ? "inboundTransfer" : "outboundTransfer",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, context);
        var start = GetOperationalLogLength();
        var evidence = evidenceMode == "legacy" ? null : queued.YieldEvidence;
        if (evidenceMode == "rejected") { frame = frame with { AuthoritySchema = -1 }; }
        if (evidenceMode == "coordinator-rejected") { frame = frame with { LiveRouteEpoch = frame.LiveRouteEpoch + 1 }; }
        if (evidenceMode == "stale")
        {
            typeof(SessionFileTransferService).GetField(inbound ? "inboundTransfer" : "outboundTransfer",
                BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, null);
        }
        async Task Event()
            => await (Task)typeof(SessionFileTransferService).GetMethod("HandleIncomingFallbackTailYieldProofWithEvidenceAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [frame, evidenceMode == "legacy" ? null : raised.YieldEvidence])!;
        void Queue()
            => typeof(SessionFileTransferService).GetMethod(inbound ? "ApplyInboundFallbackTailYieldProofWithEvidence" : "ApplyOutboundFallbackTailYieldProofWithEvidence",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [context, frame, evidence]);
        if (eventFirst) { await Event(); }
        else { Queue(); }
        var afterFirst = GetCoordinatorFallbackTailYieldSubstate(context);
        if (eventFirst) { Queue(); }
        else { await Event(); }
        Assert.Equal(afterFirst, GetCoordinatorFallbackTailYieldSubstate(context));
        var rows = ReadOperationalLogTail(start).Split('\n').Where(line =>
            line.Contains("yield_evidence_role=receipt_association;", StringComparison.Ordinal) &&
            line.Contains("yield_receipt_stage=peer_proof;", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row => Assert.Contains("authority_effect=none", row, StringComparison.Ordinal));
        if (evidenceMode == "coordinator-rejected")
        {
            Assert.All(rows, row =>
            {
                Assert.Contains("yield_receipt_decision=Rejected;", row, StringComparison.Ordinal);
                Assert.Contains("receipt_status=Rejected;", row, StringComparison.Ordinal);
                Assert.Contains("receipt_stage=attempted_decision;", row, StringComparison.Ordinal);
                Assert.DoesNotContain("yield_evidence_status=bound;", row, StringComparison.Ordinal);
            });
            Assert.Null(GetCoordinatorFallbackTailYieldSubstate(context).CurrentOperation?.PeerProof);
            return;
        }
        if (evidenceMode is "rejected" or "stale")
        {
            Assert.All(rows, row => Assert.Contains("yield_receipt_decision=not_applied", row, StringComparison.Ordinal));
            Assert.All(rows, row => Assert.Contains("yield_receipt_state_changed=0;", row, StringComparison.Ordinal));
            Assert.Null(GetCoordinatorFallbackTailYieldSubstate(context).PendingPeerProofBeforeLocalYield);
            return;
        }
        Assert.Contains("yield_receipt_decision=Accepted;", rows[0], StringComparison.Ordinal);
        Assert.Contains("yield_receipt_decision=Idempotent;", rows[1], StringComparison.Ordinal);
        Assert.Contains($"yield_evidence_status={(evidenceMode == "legacy" ? "unbound" : evidenceMode == "retired" ? "retired" : "bound")};",
            rows[0], StringComparison.Ordinal);
        Assert.Contains("yield_receipt_state_changed=0;", rows[1], StringComparison.Ordinal);
        Assert.Equal(1, rows.Count(row => row.Contains("yield_receipt_decision=Accepted;", StringComparison.Ordinal)));
        var fallback = GetCoordinatorFallbackTailYieldSubstate(context);
        var pending = fallback.CurrentOperation?.PeerProof ?? fallback.PendingPeerProofBeforeLocalYield;
        Assert.NotNull(pending);
        Assert.Equal(SessionFileTransferService.CreateFallbackTailYieldProofIdentity(frame), pending.Value.ObservationIdentity);
    }
}
