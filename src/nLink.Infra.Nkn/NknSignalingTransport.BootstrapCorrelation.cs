using System.Collections.Immutable;
using System.Security.Cryptography;
using NLink.Core.FileTransfer;

namespace NLink.Infra.Nkn;

public sealed partial class NknSignalingTransport
{
    private sealed record BootstrapPhaseExecutionSlot(FileTransferBootstrapPhaseCommand Command,
        IFileTransferDataSession DataSession, FileTransferBootstrapPhaseExecution Execution);

    private BootstrapPhaseExecutionSlot? activeCorrelatedBootstrapPhase;
    private readonly Dictionary<FileTransferBootstrapPhase, BootstrapPhaseExecutionSlot> correlatedBootstrapPhaseReceipts = [];

    // Advertised for fresh transfers; Core requires mutual selection before use.
    bool IFileTransferRouteTransitionBridge.SupportsFallbackBootstrapCorrelation => true;

    void IFileTransferRouteTransitionBridge.ObserveFallbackBootstrapAgreement(FileTransferBootstrapAgreement agreement)
    {
        if (agreement.Accept?.FallbackBootstrapCorrelationVersion != 1 ||
            agreement.Offer.SessionId != currentSessionSecurityState.SessionId?.Value) { return; }
        lock (controlSecureStateGate)
        {
            if (fileTransferStates.TryGetValue(agreement.Offer.TransferId, out var state) && !state.IsTerminal &&
                state.InitiatedLocally == (agreement.Direction == FileTransferDirection.Outbound))
            {
                fileTransferStates[agreement.Offer.TransferId] = state with
                {
                    CorrelatedBootstrapSelected = true,
                    BootstrapLocalIssuer = agreement.Direction == FileTransferDirection.Outbound
                        ? agreement.Offer.FallbackBootstrapIssuerId : agreement.Accept.FallbackBootstrapIssuerId,
                };
            }
        }
    }

    private bool IsBootstrapEvidenceOnlyDataFrame(FileTransferDataFrame frame)
    {
        if (IsCorrelatedBootstrapDataFrame(frame)) { return true; }
        if (!(frame is FileTransferReceiverStateFrameV6 state && FileTransferProtocol.IsFallbackV6ProofBootstrapReceiverState(state) ||
            frame is FileTransferFrontierRequestFrameV6 request && FileTransferProtocol.TryGetFallbackV6ProofBootstrapGeneration(request, out _) ||
            frame is FileTransferTransportEpochFrameV6 epoch && epoch.RecoveryMode == FileTransferProtocol.FallbackV6ProofBootstrapRecoveryMode)) { return false; }
        lock (controlSecureStateGate)
        {
            return fileTransferStates.TryGetValue(frame.TransferId, out var current) && current.CorrelatedBootstrapSelected ||
                fileTransferTerminalTombstones.TryGetValue(frame.TransferId, out var terminal) && terminal.CorrelatedBootstrapSelected;
        }
    }

    FileTransferBootstrapPhaseExecution IFileTransferRouteTransitionBridge.ExecuteFallbackBootstrapPhase(
        FileTransferBootstrapPhaseCommand command, IFileTransferDataSession dataSession,
        Func<bool> isCommandCurrent, CancellationToken ct)
    {
        lock (accelerationGate)
        {
            if (activeCorrelatedBootstrapPhase is { } active)
            {
                return active.Command == command && ReferenceEquals(active.DataSession, dataSession)
                    ? active.Execution : FileTransferBootstrapPhaseExecution.Rejected(command, "bootstrap_phase_execution_conflict");
            }
            if (activeFallbackV6ProofBootstrapTask is not null)
            {
                return FileTransferBootstrapPhaseExecution.Rejected(command, "bootstrap_phase_execution_conflict");
            }
            if (correlatedBootstrapPhaseReceipts.TryGetValue(command.Phase, out var terminal) &&
                terminal.Command == command && ReferenceEquals(terminal.DataSession, dataSession))
            {
                return terminal.Execution;
            }
            var delivery = new TaskCompletionSource<FileTransferBootstrapPhaseReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
            var settlement = new TaskCompletionSource<FileTransferBootstrapPhaseReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
            var slot = new BootstrapPhaseExecutionSlot(command, dataSession, new(delivery.Task, settlement.Task));
            activeCorrelatedBootstrapPhase = slot;
            _ = Task.Run(() => ExecuteCorrelatedBootstrapPhaseAsync(slot, delivery, settlement, isCommandCurrent, ct), CancellationToken.None);
            return slot.Execution;
        }
    }

    private async Task ExecuteCorrelatedBootstrapPhaseAsync(BootstrapPhaseExecutionSlot slot,
        TaskCompletionSource<FileTransferBootstrapPhaseReceipt> delivery,
        TaskCompletionSource<FileTransferBootstrapPhaseReceipt> settlement, Func<bool> isCurrent, CancellationToken ct)
    {
        var command = slot.Command;
        var submissions = new List<FileTransferPhysicalSendSubmission>(2);
        var status = FileTransferBootstrapPhaseReceiptStatus.Failed;
        var reason = "bootstrap_phase_execution_invalid";
        try
        {
            if (slot.DataSession is not TransportFileTransferDataSession physical ||
                slot.DataSession.SessionId != command.Bootstrap.Observation.SessionId ||
                slot.DataSession.TransferId != command.Bootstrap.Observation.TransferId ||
                command.FrameIdentity != PostTunaFallbackPeerProofFrameIdentity.Create(command.Frame) ||
                command.EpochFrame is { } epoch && command.EpochFrameIdentity != new PostTunaFallbackPeerProofFrameIdentity(
                    epoch.Type, epoch.TransportEpoch, Convert.ToHexString(SHA256.HashData(FileTransferDataFrameCodec.Serialize(epoch))).ToLowerInvariant()) ||
                !isCurrent())
            {
                throw new InvalidOperationException(reason);
            }
            ct.ThrowIfCancellationRequested();
            if (!slot.DataSession.IsAvailable)
            {
                status = FileTransferBootstrapPhaseReceiptStatus.Held;
                reason = "bootstrap_phase_data_session_unavailable";
            }
            else
            {
                if (command.EpochFrame is { } epochFrame)
                {
                    var epochSubmission = ((IFileTransferPhysicalSendSubmissionSession)physical).SubmitPhysicalSend(epochFrame, null, ct);
                    submissions.Add(epochSubmission);
                    await epochSubmission.DeliveryTask.ConfigureAwait(false);
                }
                if (!isCurrent() || !slot.DataSession.IsAvailable) { throw new InvalidOperationException("bootstrap_phase_stale_before_proof"); }
                ct.ThrowIfCancellationRequested();
                var proofSubmission = ((IFileTransferPhysicalSendSubmissionSession)physical).SubmitPhysicalSend(command.Frame, null, ct);
                submissions.Add(proofSubmission);
                await proofSubmission.DeliveryTask.ConfigureAwait(false);
                status = FileTransferBootstrapPhaseReceiptStatus.Delivered;
                reason = "bootstrap_phase_delivered";
            }
        }
        catch (Exception ex)
        {
            reason = ex is OperationCanceledException ? "bootstrap_phase_canceled" : "bootstrap_phase_send_failed";
        }

        // Registration is now closed for the whole phase. First success is not settlement.
        var deliveryReceipt = new FileTransferBootstrapPhaseReceipt(command, status, true, Copies(), reason);
        delivery.TrySetResult(deliveryReceipt);
        foreach (var submission in submissions)
        {
            try { await submission.PhysicalCompletionTask.ConfigureAwait(false); }
            catch { /* Failed/canceled copies still settle; their exact outcomes remain in the receipt. */ }
        }
        var settledReceipt = status == FileTransferBootstrapPhaseReceiptStatus.Held ? deliveryReceipt
            : new FileTransferBootstrapPhaseReceipt(command, FileTransferBootstrapPhaseReceiptStatus.Settled, true, Copies(), "bootstrap_phase_settled");
        lock (accelerationGate)
        {
            if (ReferenceEquals(activeCorrelatedBootstrapPhase, slot))
            {
                activeCorrelatedBootstrapPhase = null;
                if (status != FileTransferBootstrapPhaseReceiptStatus.Held)
                {
                    foreach (var retired in correlatedBootstrapPhaseReceipts.Where(item => item.Value.Command.Bootstrap != command.Bootstrap)
                        .Select(item => item.Key).ToArray()) { correlatedBootstrapPhaseReceipts.Remove(retired); }
                    correlatedBootstrapPhaseReceipts[command.Phase] = slot;
                }
            }
        }
        // Release the single executor slot before this callback can authorize a successor phase.
        settlement.TrySetResult(settledReceipt);

        ImmutableArray<FileTransferBootstrapPhysicalCopy> Copies()
            => submissions.SelectMany(submission => submission.BootstrapCopies?.Invoke() ?? []).ToImmutableArray();
    }
}
