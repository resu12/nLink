using System;
using System.Collections.Generic;
using System.Threading;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        private FrontierParentCustody frontierParentCustody;
        private void RetireFrontierCustody()
        { lock (frontierParentSync) { if (frontierParentCustody != null) frontierParentCustody.Close(); } }
        private void BeginFrontierCustodyTake()
        {
            lock (frontierParentSync)
            {
                if (frontierParentCustody == null) return;
                frontierParentRegistration.parentWork.Start();
                try { if (!frontierParentCustody.BeginTake(closed)) frontierParentCustody.Close(); }
                catch { frontierParentCustody.Close(); }
            }
        }
        private bool ParentTerminalScopeCurrent()
        {
            var registration = frontierParentRegistration;
            if (registration == null || view == null || view.ReadInt32(0) != Magic || ReadId(view) != RequestId ||
                view.ReadInt32(4) != registration.ParentId || view.ReadInt64(8) != registration.ParentCreation ||
                view.ReadInt32(16) != registration.GuiId || view.ReadInt64(40) != registration.GuiCreation ||
                view.ReadInt32(20) != registration.Body.Length || view.ReadInt32(48) != 2 || view.ReadInt64(56) != 0) return false;
            for (int i = 0; i < registration.Body.Length; i++) if (view.ReadByte(HeaderBytes + i) != registration.Body[i]) return false;
            return true;
        }
        private void FinishFrontierCustody(byte[] body, Completion completion)
        {
            lock (frontierParentSync)
            {
                var custody = frontierParentCustody; if (custody == null) return;
                var work = frontierParentRegistration.parentWork; work.Start(); bool success = false;
                try
                {
                    success = body != null && completion != null && ParentTerminalScopeCurrent() &&
                        custody.Accept(body, completion) && work.ElapsedMilliseconds <= 5000;
                }
                catch { }
                finally { work.Stop(); if (!success) custody.Close(); }
            }
        }
        public sealed partial class FrontierParentRegistration
        {
            private bool commitAttempted;
            internal FrontierParentCustody PrepareCommit(FrontierParentAcknowledgment value)
            {
                lock (owner.frontierParentSync)
                {
                    if (!Object.ReferenceEquals(owner.frontierParentRegistration, this) || !Object.ReferenceEquals(acknowledgment, value)) return null;
                    bool success = false; FrontierParentFilesPending.CommitPins pins = null;
                    LocalFrontierParentCommitChannel channel = null; parentWork.Start();
                    try
                    {
                        if (commitAttempted) return null; commitAttempted = true;
                        Func<bool> current = () => parentWork.ElapsedMilliseconds <= 5000 && OwnsAcknowledgment(value) && value.GuiConfirmed;
                        if (!current() || owner.frontierParentCustody != null) return null;
                        pins = PendingFiles.DuplicateForCommit(current); if (pins == null) return null;
                        channel = LocalFrontierParentCommitChannel.Create(Channel.Files.Endpoint, value.Channel.BindingHash);
                        if (channel == null || !current() || !pins.IsCurrent) return null;
                        owner.frontierParentCustody = new FrontierParentCustody(owner, parentFacts.Packet, pins, channel, owner.workerExit);
                        owner.frontierParentCustody.Watch(Channel.Files.Cancelled, owner.closed);
                        success = current() && owner.frontierParentCustody.ResourceCurrent;
                        return success ? owner.frontierParentCustody : null;
                    }
                    catch { return null; }
                    finally
                    {
                        parentWork.Stop();
                        if (!success) { owner.RetireFrontierCustody(); owner.RetireFrontierParentRegistration(); if (pins != null) pins.Dispose(); if (channel != null) channel.Dispose(); }
                    }
                }
            }
        }
        public sealed partial class FrontierParentAcknowledgment
        {
            public FrontierParentCustody TryPrepareCommit() { return owner.PrepareCommit(this); }
        }
        // Private native escrow before TakeAfterExit; no public reader or primary
        // publication authority. Request disposal owns the final lifetime.
        public sealed partial class FrontierParentCustody : IDisposable
        {
            private readonly LocalGuiWriterRequest owner;
            private readonly LocalFrontierParentFacts facts;
            private readonly FrontierParentFilesPending.CommitPins pins;
            private readonly LocalFrontierParentCommitChannel channel;
            private readonly ProcessWait gui;
            private readonly object sync = new object();
            private readonly List<RegisteredWaitHandle> watches = new List<RegisteredWaitHandle>();
            private bool closed, taking, accepted;
            internal FrontierParentCustody(LocalGuiWriterRequest owner, LocalFrontierParentFacts facts,
                FrontierParentFilesPending.CommitPins pins, LocalFrontierParentCommitChannel channel, ProcessWait gui)
            { this.owner = owner; this.facts = facts; this.pins = pins; this.channel = channel; this.gui = gui; }
            internal bool ResourceCurrent { get { lock (sync) { return !closed && pins.IsCurrent; } } }
            private bool GuiSucceeded()
            { try { int code; return gui.WaitOne(0) && GetExitCodeProcess(gui.SafeWaitHandle, out code) && code == 0; } catch { return false; } }
            internal void Watch(WaitHandle provisionalCancellation, WaitHandle requestCancellation)
            {
                lock (sync)
                {
                    watches.Add(ThreadPool.RegisterWaitForSingleObject(channel.Cancelled, (s,t) => Close(), null, Timeout.Infinite, true));
                    watches.Add(ThreadPool.RegisterWaitForSingleObject(requestCancellation, (s,t) =>
                    { lock (sync) { if (!taking) Close(); } }, null, Timeout.Infinite, true));
                    watches.Add(ThreadPool.RegisterWaitForSingleObject(provisionalCancellation, (s,t) =>
                    { lock (sync) { if (!closed && !channel.IsArmed) Close(); } }, null, Timeout.Infinite, true));
                    watches.Add(ThreadPool.RegisterWaitForSingleObject(gui, (s,t) =>
                    { lock (sync) { if (!closed && (!GuiSucceeded() || !channel.IsCommitted)) Close(); } }, null, Timeout.Infinite, true));
                }
            }
            internal bool BeginTake(WaitHandle requestCancellation)
            {
                lock (sync)
                {
                    if (closed || taking || requestCancellation.WaitOne(0) || !GuiSucceeded() || !channel.IsCommitted || !pins.IsCurrent) return false;
                    // Only the original post-exit take may set its own legacy
                    // closed event without cancelling escrow. Dispose always
                    // closes escrow directly, including during this interval.
                    taking = true; return true;
                }
            }
            internal bool Accept(byte[] body, Completion completion)
            {
                lock (sync)
                {
                    if (closed || !taking || accepted || !GuiSucceeded() || !pins.IsCurrent || !channel.Matches(body) || !facts.MatchesCompletion(completion)) return false;
                    acceptedCompletion = completion; accepted = true; return true;
                }
            }
            public bool IsCurrent
            {
                get
                {
                    lock (owner.frontierParentSync)
                    {
                        if (!Object.ReferenceEquals(owner.frontierParentCustody, this)) return false;
                        lock (sync)
                        {
                            if (closed || !accepted) return false;
                            if (pins.IsCurrent && channel.IsCommitted) return true;
                            Close(); return false;
                        }
                    }
                }
            }
            public int FileCount { get { return IsCurrent ? pins.Count : 0; } }
            public int SourceLineCount { get { return IsCurrent ? facts.SourceLines : 0; } }
            public bool LiveProofEligible { get { return false; } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            internal void Close()
            {
                lock (sync)
                {
                    if (closed) return; closed = true;
                    foreach (var watch in watches) watch.Unregister(null);
                    pins.Dispose(); channel.Dispose();
                }
            }
            public void Dispose()
            { lock (owner.frontierParentSync) { if (Object.ReferenceEquals(owner.frontierParentCustody, this)) Close(); } }
        }
    }
}
