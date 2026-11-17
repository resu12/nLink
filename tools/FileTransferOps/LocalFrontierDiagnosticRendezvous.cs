using System;
using System.Threading;
using System.Threading.Tasks;

namespace NLink.Tools
{
    internal sealed partial class LocalFrontierDiagnosticChannel
    {
        private EventWaitHandle offeredSignal, adoptedSignal;
        private EventWaitHandle OpenSignal(string name)
        {
            if (!parent) return EventWaitHandle.OpenExisting(name);
            bool created;
            var signal = new EventWaitHandle(false, EventResetMode.ManualReset, name, out created);
            if (created) return signal;
            signal.Dispose(); throw new InvalidOperationException("frontier_diagnostic_signal_exists");
        }
        private bool OpenRendezvous(string name)
        {
            try
            {
                offeredSignal = OpenSignal(name + ".offered");
                adoptedSignal = OpenSignal(name + ".adopted"); return true;
            }
            catch { return false; }
        }
        private static bool Signal(EventWaitHandle signal)
        { try { return signal.Set(); } catch { return false; } }
        private bool WaitForSignal(WaitHandle requestClosed, WaitHandle peerExit, WaitHandle signal)
        {
            try
            {
                return WaitHandle.WaitAny(new[] { requestClosed, peerExit, cancelled, signal }, LocalPublicationInputLimits.ProducerMilliseconds) == 3 &&
                    !requestClosed.WaitOne(0) && !peerExit.WaitOne(0) && IsOpen;
            }
            catch { return false; }
        }
        internal bool WaitForOffer(WaitHandle requestClosed, WaitHandle workerExit)
        { return parent && WaitForSignal(requestClosed, workerExit, offeredSignal); }
        internal bool WaitForAdoption(WaitHandle requestClosed, WaitHandle parentExit)
        { return !parent && WaitForSignal(requestClosed, parentExit, adoptedSignal); }
        internal bool OfferAvailable(WaitHandle requestClosed, WaitHandle workerExit)
        {
            try
            {
                return parent && WaitHandle.WaitAny(new[] { requestClosed, workerExit, cancelled, offeredSignal }, 0) == 3 &&
                    !requestClosed.WaitOne(0) && !workerExit.WaitOne(0) && IsOpen;
            }
            catch { return false; }
        }
    }

    public sealed partial class LocalRetainedSourceRequest
    {
        public sealed partial class FrontierDiagnosticReceiver
        {
            private bool adoptionWaitAttempted;
            // Called by the original process watchdog. An absent offer consumes
            // neither the one-shot adoption nor an idle/active-work budget.
            public bool TryAdoptIfOffered()
            {
                lock (owner.frontierInputSync) lock (sync)
                {
                    if (!Original || closed || adoptionWaitAttempted || adoptionAttempted ||
                        !channel.OfferAvailable(owner.closed, owner.workerExit)) return false;
                    return TryAdopt();
                }
            }
            // The caller can keep its original process watchdog and report tasks.
            // Idle waiting holds no ownership lock and consumes no active-work budget.
            public Task<bool> TryAdoptWhenOfferedAsync()
            {
                lock (owner.frontierInputSync) lock (sync)
                {
                    if (!Original || closed || adoptionWaitAttempted || adoptionAttempted) return Task.FromResult(false);
                    adoptionWaitAttempted = true;
                    return Task.Run(delegate
                    {
                        if (!channel.WaitForOffer(owner.closed, owner.workerExit)) { Close(); return false; }
                        return TryAdopt();
                    });
                }
            }
        }

        public sealed partial class FrontierWorkerInputs
        {
            public sealed partial class EvaluationCustody
            {
                private bool diagnosticWaitAttempted;
                internal bool CommitDiagnosticWhenAdopted(NativeDiagnosticPreparation candidate)
                {
                    LocalFrontierDiagnosticChannel channel;
                    lock (inputs.owner.frontierWorkerSync) lock (inputs.sync)
                    {
                        if (!OriginalPreparation(candidate) || diagnosticWaitAttempted || diagnosticCommitAttempted) return false;
                        diagnosticWaitAttempted = true;
                        if (!diagnosticOffered) { RejectDiagnosticOutput(); return false; }
                        channel = inputs.diagnosticOutput;
                    }
                    // The peer may acquire the channel gate while this worker waits.
                    bool ready = channel.WaitForAdoption(inputs.owner.closed, inputs.owner.parentExit);
                    lock (inputs.owner.frontierWorkerSync) lock (inputs.sync)
                    {
                        if (!ready) { RejectDiagnosticOutput(); return false; }
                        return CommitDiagnostic(candidate);
                    }
                }
            }

            public sealed partial class NativeDiagnosticPreparation
            {
                public bool TryCommitOutputWhenAdopted() { return owner.CommitDiagnosticWhenAdopted(this); }
            }
        }
    }
}
