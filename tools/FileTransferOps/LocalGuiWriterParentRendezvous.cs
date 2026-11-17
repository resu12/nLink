using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        internal enum ParentRendezvousStage
        { FilesOffered, FilesPulled, FactsOffered, AcknowledgmentOffered, AcknowledgmentConfirmed, CommitPrepared }

        internal sealed partial class ParentRegistrationChannel
        {
            private readonly EventWaitHandle[] rendezvousSignals = new EventWaitHandle[6];
            private void OpenRendezvous(bool create)
            {
                for (int i = 0; i < rendezvousSignals.Length; i++)
                {
                    string name = Endpoint + ".rendezvous-" + ((ParentRendezvousStage)i).ToString();
                    if (!create) rendezvousSignals[i] = EventWaitHandle.OpenExisting(name);
                    else
                    {
                        bool created;
                        rendezvousSignals[i] = new EventWaitHandle(false, EventResetMode.ManualReset, name, out created);
                        // Failed construction only closes local handles. It must
                        // neither reset nor cancel an existing foreign object.
                        if (!created) throw new IOException("Parent rendezvous already exists.");
                    }
                }
            }
            internal bool Offered(ParentRendezvousStage stage)
            { return rendezvousSignals[(int)stage].WaitOne(0); }
            internal bool Signal(ParentRendezvousStage stage)
            { return IsCurrent && rendezvousSignals[(int)stage].Set(); }
            internal bool WaitFor(ParentRendezvousStage stage, Stopwatch clock, WaitHandle closed, WaitHandle parentExit)
            {
                long remaining = LocalPublicationInputLimits.ProducerMilliseconds - clock.ElapsedMilliseconds;
                return remaining > 0 && IsCurrent && Files != null &&
                    WaitHandle.WaitAny(new WaitHandle[] { closed, parentExit, cancelled, Files.Cancelled, rendezvousSignals[(int)stage] },
                        (int)remaining) == 4 && clock.ElapsedMilliseconds <= LocalPublicationInputLimits.ProducerMilliseconds && IsCurrent;
            }
        }

        public sealed partial class FrontierFinalizerBinding
        {
            private bool parentRendezvousAttempted;
            private readonly Stopwatch parentRendezvousClock = new Stopwatch();

            // Scheduling only. The original caller still invokes the original
            // TryCompleteParentHandoff once, at its sole completion boundary.
            public bool TryPrepareParentHandoffWhenReady()
            {
                lock (sourceSync)
                {
                    if (!IsCurrent) return false;
                    if (parentRendezvousAttempted) { Dispose(); return false; }
                    parentRendezvousAttempted = true; parentRendezvousClock.Start();
                }
                bool success = false;
                try
                {
                    if (parentRendezvousClock.ElapsedMilliseconds >= LocalPublicationInputLimits.ProducerMilliseconds ||
                        TryAttachParentHandoff() == null || TryPrepareParentFiles() == null || TryPublishParentFiles() == null) return false;
                    var channel = parentHandoff.Channel;
                    if (!channel.Signal(ParentRendezvousStage.FilesOffered) ||
                        !WaitForParent(channel, ParentRendezvousStage.FilesPulled) || !TryPublishParentFacts() ||
                        !channel.Signal(ParentRendezvousStage.FactsOffered) ||
                        !WaitForParent(channel, ParentRendezvousStage.AcknowledgmentOffered) || !TryConfirmParentAcknowledgment() ||
                        !channel.Signal(ParentRendezvousStage.AcknowledgmentConfirmed)) return false;
                    success = WaitForParent(channel, ParentRendezvousStage.CommitPrepared);
                    return success;
                }
                catch { return false; }
                finally { parentRendezvousClock.Stop(); if (!success) Dispose(); }
            }
            private bool WaitForParent(ParentRegistrationChannel channel, ParentRendezvousStage stage)
            {
                // Never hold a finalizer/launch/adopter lock or active-work
                // clock while waiting. This one wall deadline is not renewed.
                return IsCurrent && channel.WaitFor(stage, parentRendezvousClock, frontierLaunchOwner.closed, frontierLaunchOwner.parentExit) && IsCurrent;
            }
        }

        public sealed partial class FrontierParentRegistration
        {
            private int rendezvousPhase;

            // Nonblocking watchdog probe. Absent offers do not consume any
            // original one-shot operation or charge the active-work budget.
            public FrontierParentCustody TryPrepareCommitIfOffered()
            {
                lock (owner.frontierParentSync)
                {
                    if (!Object.ReferenceEquals(owner.frontierParentRegistration, this) || rendezvousPhase == 3) return null;
                    bool success = false;
                    try
                    {
                        if (!IsCurrent) return null;
                        if (rendezvousPhase == 0)
                        {
                            if (!Channel.Offered(ParentRendezvousStage.FilesOffered)) { success = true; return null; }
                            if (TryPullFiles() == null || !Channel.Signal(ParentRendezvousStage.FilesPulled)) return null;
                            rendezvousPhase = 1;
                        }
                        if (rendezvousPhase == 1)
                        {
                            if (!Channel.Offered(ParentRendezvousStage.FactsOffered)) { success = true; return null; }
                            var facts = PendingFiles.TryReceiveFacts();
                            if (facts == null || facts.TryAcknowledge() == null || !Channel.Signal(ParentRendezvousStage.AcknowledgmentOffered)) return null;
                            rendezvousPhase = 2;
                        }
                        if (!Channel.Offered(ParentRendezvousStage.AcknowledgmentConfirmed)) { success = true; return null; }
                        var custody = acknowledgment.TryPrepareCommit();
                        if (custody == null || !Channel.Signal(ParentRendezvousStage.CommitPrepared)) return null;
                        rendezvousPhase = 3; success = true; return custody;
                    }
                    catch { return null; }
                    finally
                    {
                        if (!success) { owner.RetireFrontierCustody(); owner.RetireFrontierParentRegistration(); }
                    }
                }
            }
        }
    }
}
