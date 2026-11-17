using System.Diagnostics;
using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private sealed class FrontierCompletionWriteBinding(LocalOperationalLog.FrontierWriterRegistration writer)
    {
        private readonly object gate = new();
        private FileTransferCoordinatorTransitionEnvelope? completion;
        private CoordinatorSnapshotStreamEmitter? emitter;
        private bool queued, writeAttempted, invalid;
        private LocalOperationalLog.FrontierWriteReceipt? receipt;

        public bool TryStage(FileTransferCoordinatorTransitionEnvelope envelope, CoordinatorSnapshotStreamEmitter source)
        {
            lock (gate)
            {
                if (invalid || completion is not null) { return false; }
                completion = envelope;
                emitter = source;
                return true;
            }
        }

        public bool TryEnqueue(FileTransferCoordinatorTransitionEnvelope envelope)
        {
            lock (gate)
            {
                if (invalid || queued || !ReferenceEquals(completion, envelope)) { invalid = true; return false; }
                queued = true;
                return true;
            }
        }

        public void Revoke() { lock (gate) { invalid = true; receipt = null; } }

        public void Write(string message)
        {
            bool observe;
            lock (gate)
            {
                observe = !invalid && queued && !writeAttempted;
                writeAttempted = true;
            }
            if (!observe) { LocalOperationalLog.Info("FileTransferCoordinator", message); return; }
            var written = LocalOperationalLog.InfoWithFrontierReceipt(message, writer, this);
            lock (gate) { if (!invalid) { receipt = written; } }
        }

        public LocalOperationalLog.FrontierWriteReceipt? GetReceipt(FileTransferCoordinatorTransitionEnvelope envelope)
        {
            LocalOperationalLog.FrontierWriteReceipt? candidate;
            lock (gate)
            {
                if (invalid || !queued || !writeAttempted || !ReferenceEquals(completion, envelope)) { return null; }
                candidate = receipt;
            }
            return candidate?.IsPersistedFor(this) == true ? candidate : null;
        }

        public bool WaitForReceipt(FileTransferCoordinatorTransitionEnvelope envelope, TimeSpan timeout)
        {
            CoordinatorSnapshotStreamEmitter? source;
            lock (gate)
            {
                if (invalid || !queued || !ReferenceEquals(completion, envelope)) { return false; }
                source = emitter;
            }
            var clock = Stopwatch.StartNew();
            if (source is null || !source.WaitForFinalSubmission(timeout)) { return false; }
            LocalOperationalLog.FrontierWriteReceipt? candidate;
            lock (gate) { candidate = !invalid && writeAttempted ? receipt : null; }
            var remaining = timeout - clock.Elapsed;
            return candidate?.WaitForPersistence(this, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero) == true &&
                ReferenceEquals(candidate, GetReceipt(envelope));
        }
    }

    private sealed partial class FrontierBoundaryObservation
    {
        private FrontierCompletionWriteBinding? completionWriter;

        public void BindCompletionWriter(LocalOperationalLog.FrontierWriterRegistration writer)
            => completionWriter ??= new(writer);

        // Only the original decision about the original frame can stage this link.
        // No native custody check, logging or new clock read occurs under the service gate.
        public void StageCompletionWrite(InboundTransferContext context, FileTransferDataFrame frame,
            FallbackControlPlaneToken deliveryToken, FileTransferCoordinatorDecision accepted)
        {
            if (completionWriter is null || closed || !ReferenceEquals(originalFrame, frame) ||
                token != deliveryToken || accepted.FallbackControlPlaneToken != deliveryToken ||
                !accepted.ControlPlanePeerVisibleProofAccepted || accepted.MutationRejectedReason is not null ||
                accepted.SnapshotEnvelope is not { } envelope) { return; }
            context.CoordinatorSnapshotEmitter.StageFrontierCompletion(completionWriter, envelope);
        }

        public LocalOperationalLog.FrontierWriteReceipt? GetCompletionWrite()
        {
            lock (payloadGate)
            {
                // Preparation already validated this closed, immutable lifecycle.
                if (preparedPayload is null || entries.Count < 2 ||
                    entries[^2] is not { Kind: "AcceptedCompletion", Binding: FileTransferCoordinatorTransitionEnvelope envelope })
                { return null; }
                return completionWriter?.GetReceipt(envelope);
            }
        }

        public bool WaitForCompletionWrite(TimeSpan timeout)
        {
            FrontierCompletionWriteBinding? binding;
            FileTransferCoordinatorTransitionEnvelope envelope;
            lock (payloadGate)
            {
                if (preparedPayload is null || entries.Count < 2 ||
                    entries[^2] is not { Kind: "AcceptedCompletion", Binding: FileTransferCoordinatorTransitionEnvelope accepted })
                { return false; }
                binding = completionWriter;
                envelope = accepted;
            }
            // No capture/payload/catalog/native ownership gate is held while waiting.
            return binding?.WaitForReceipt(envelope, timeout) == true;
        }
    }
}
