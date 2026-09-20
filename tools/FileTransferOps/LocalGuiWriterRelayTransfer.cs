using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        private FrontierRelayCustody frontierRelayCustody;
        private void RetireFrontierRelayCustody()
        { lock (frontierRelaySync) { if (frontierRelayCustody != null && !frontierRelayCustody.Detached) frontierRelayCustody.Close(); } }
        private static byte[] EncodeRelayCompletion(Completion completion)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true)))
            { completion.WriteTo(writer); writer.Flush(); return stream.Length <= MaximumCompletionBytes ? stream.ToArray() : null; }
        }
        public sealed partial class FrontierParentCustody
        {
            internal bool OfferToRelay(FrontierRelayHandoff relay, Completion completion)
            {
                lock (owner.frontierParentSync)
                {
                    if (!Object.ReferenceEquals(owner.frontierParentCustody, this)) return false;
                    lock (sync)
                    {
                        if (closed || !accepted || primaryAttempted || primaryHashAttempted ||
                            !Object.ReferenceEquals(acceptedCompletion, completion) || !relay.Matches(completion)) return false;
                        primaryAttempted = true; relay.offerAttempted = true;
                        var work = owner.frontierParentRegistration.parentWork; work.Start();
                        try
                        {
                            Func<bool> current = () => work.ElapsedMilliseconds <= 5000 && IsCurrent && relay.IsCurrent && facts.MatchesCompletion(completion);
                            if (!current()) return false;
                            relay.offerPins = pins.DuplicateForPrimary(current);
                            if (relay.offerPins == null) return false;
                            Action check = () => { if (!current()) throw new IOException("frontier_relay_offer_retired"); };
                            relay.transfer = LocalFrontierRelayTransferChannel.Create(relay.channel.Endpoint, relay.channel.Binding,
                                relay.offerPins.ExportPrimaryOffer(), facts.CopyForPrimary(), EncodeRelayCompletion(completion), check);
                            return relay.transfer != null && current();
                        }
                        catch { return false; }
                        finally { work.Stop(); Close(); }
                    }
                }
            }
        }
        public sealed partial class FrontierRelayHandoff
        {
            internal FrontierParentFilesPending.CommitPins offerPins;
            internal LocalFrontierRelayTransferChannel transfer;
            internal bool offerAttempted;
            internal bool Matches(Completion completion)
            {
                return Object.ReferenceEquals(owner.frontierRelayHandoff, this) && !offerAttempted && IsCurrent &&
                    completion != null && completion.WasAcceptedHere() && completion.AcceptanceRunId == owner.owner.run &&
                    completion.ScenarioRunId == owner.owner.scenario && completion.ArtifactRoot == owner.owner.root;
            }
            // Idle rendezvous is at most 20 seconds, outside active-work clocks.
            // No original relay Forward or primary launch happens here.
            public bool TryOfferAndWait(FrontierParentCustody custody, Completion completion)
            {
                lock (owner.frontierRelaySync)
                {
                    if (custody == null || !Matches(completion)) return false;
                    bool success = false;
                    try
                    {
                        if (!custody.OfferToRelay(this, completion)) return false;
                        int signal = WaitHandle.WaitAny(new WaitHandle[] { owner.owner.parentExit, channel.Cancelled, transfer.Cancelled, transfer.Pulled }, 20000);
                        success = signal == 3 && IsCurrent && offerPins.IsCurrent && transfer.IsCurrent && transfer.State == 2;
                        return success;
                    }
                    catch { return false; }
                    finally { if (!success) CloseTransfer(); }
                }
            }
            internal void Forwarded(byte[] body)
            {
                lock (owner.frontierRelaySync)
                {
                    try
                    {
                        var work = Stopwatch.StartNew();
                        Action check = () => { if (work.ElapsedMilliseconds > 5000) throw new IOException("frontier_relay_forward_budget"); };
                        // Forward ended registration's pre-forward scope. Check
                        // the original peer, held pins and exact offered body.
                        if (!closed && transfer != null && offerPins != null && offerPins.IsCurrent &&
                            channel.IsCurrent && channel.Attached && owner.owner.IsCurrent && transfer.MarkForwarded(body, check)) return;
                    }
                    catch { }
                    if (transfer != null) transfer.Dispose();
                    CloseTransfer();
                }
            }
            private void CloseTransfer()
            {
                if (transfer != null)
                {
                    if (transfer.State == 3 && transfer.IsCurrent) transfer.Release(); else transfer.Dispose();
                    transfer = null;
                }
                if (offerPins != null) { offerPins.Dispose(); offerPins = null; }
            }
        }
        public sealed partial class FrontierRelayRegistration
        {
            private bool pullAttempted;
            private readonly Stopwatch transferWork = new Stopwatch();
            // Called only from the existing Wrapper wait. No offer => no work.
            public FrontierRelayCustody TryPullIfOffered()
            {
                lock (owner.frontierRelaySync)
                {
                    if (!Object.ReferenceEquals(owner.frontierRelayRegistration, this) || pullAttempted ||
                        !IsCurrent || !channel.Attached || !LocalFrontierRelayTransferChannel.Offered(channel.Endpoint)) return null;
                    pullAttempted = true; transferWork.Start();
                    LocalFrontierRelayTransferChannel transfer = null;
                    FrontierParentFilesPending.CommitPins pins = null; bool success = false;
                    try
                    {
                        Func<bool> current = () => transferWork.ElapsedMilliseconds <= 5000 && IsCurrent;
                        Action check = () => { if (!current()) throw new IOException("frontier_relay_pull_retired"); };
                        transfer = LocalFrontierRelayTransferChannel.Open(channel.Endpoint, channel.Binding, check);
                        if (transfer == null) return null;
                        var facts = LocalFrontierParentFacts.DecodeForPrimary(transfer.Facts, transfer.Files, owner.run, owner.scenario, check);
                        var packet = LocalFrontierParentFilePacket.Decode(transfer.Files);
                        Completion completion;
                        using (var stream = new MemoryStream(transfer.Completion, false))
                        using (var reader = new BinaryReader(stream, new UTF8Encoding(false, true)))
                        {
                            completion = Completion.ReadFrom(reader, owner.run, owner.scenario);
                            if (stream.Position != stream.Length) return null;
                        }
                        if (completion == null || completion.ArtifactRoot != owner.root || facts == null || packet == null ||
                            packet.Gui != completion.RequestId || !facts.MatchesCompletion(completion)) return null;
                        pins = FrontierParentFilesPending.CommitPins.PullPrimary(owner.workerExit.SafeWaitHandle, transfer.Files,
                            () => current() && transfer.IsCurrent);
                        if (pins == null || !current() || !transfer.CheckCurrent(check)) return null;
                        owner.frontierRelayCustody = new FrontierRelayCustody(owner, scope, facts, pins, transfer);
                        owner.frontierRelayCustody.Watch();
                        if (!transfer.MarkPulled() || !current()) return null;
                        success = true; return owner.frontierRelayCustody;
                    }
                    catch { return null; }
                    finally
                    {
                        transferWork.Stop();
                        if (!success)
                        {
                            owner.RetireFrontierRelayCustody();
                            if (pins != null) pins.Dispose(); if (transfer != null) transfer.Dispose();
                        }
                    }
                }
            }
        }
        // Independent pins are provisional until the original Wrapper exit and
        // relay take. A separate one-shot detach survives request cleanup.
        public sealed partial class FrontierRelayCustody : IDisposable
        {
            private readonly FrontierRelayCustody original;
            private readonly LocalGuiWriterRequest owner;
            private readonly byte[] scope;
            private readonly LocalFrontierParentFacts facts;
            private readonly FrontierParentFilesPending.CommitPins pins;
            private readonly LocalFrontierRelayTransferChannel transfer;
            private readonly object sync = new object();
            private readonly List<RegisteredWaitHandle> watches = new List<RegisteredWaitHandle>();
            private bool closed, taking, detached;
            private RelayedCompletion accepted;
            internal FrontierRelayCustody(LocalGuiWriterRequest owner, byte[] scope, LocalFrontierParentFacts facts,
                FrontierParentFilesPending.CommitPins pins, LocalFrontierRelayTransferChannel transfer)
            { original = this; this.owner = owner; this.scope = scope; this.facts = facts; this.pins = pins; this.transfer = transfer; }
            internal bool Detached { get { lock (sync) { return detached; } } }
            private bool WrapperSucceeded()
            { try { int code; return owner.workerExit.WaitOne(0) && GetExitCodeProcess(owner.workerExit.SafeWaitHandle, out code) && code == 0; } catch { return false; } }
            internal void Watch()
            {
                lock (sync)
                {
                    watches.Add(ThreadPool.RegisterWaitForSingleObject(transfer.Cancelled, TransferCancelled, null, Timeout.Infinite, true));
                    watches.Add(ThreadPool.RegisterWaitForSingleObject(owner.closed, (s,t) => { lock (sync) { if (!taking) Close(); } }, null, Timeout.Infinite, true));
                    watches.Add(ThreadPool.RegisterWaitForSingleObject(owner.workerExit, WrapperExited, null, Timeout.Infinite, true));
                }
            }
            // Unregister cannot retract a callback already queued. A detached
            // owner must never consult its now-disposed original request.
            private void TransferCancelled(object state, bool timedOut)
            { lock (sync) { if (!detached) Close(); } }
            private void WrapperExited(object state, bool timedOut)
            { lock (sync) { if (!closed && !detached && (!WrapperSucceeded() || transfer.State != 3 || !transfer.IsCurrent)) Close(); } }
            internal void BeginTake()
            {
                lock (sync)
                {
                    if (closed || taking || owner.closed.WaitOne(0) || !WrapperSucceeded() || transfer.State != 3 || !transfer.IsCurrent || !pins.IsCurrent)
                    { Close(); return; }
                    taking = true;
                }
            }
            internal bool Accept(byte[] body, RelayedCompletion result)
            {
                lock (sync)
                {
                    try
                    {
                        if (closed || !taking || accepted != null || !WrapperSucceeded() || !transfer.IsCurrent || transfer.State != 3 ||
                            !transfer.Matches(body) || !pins.IsCurrent || !facts.MatchesCompletion(result.WriterContext)) return false;
                        for (int i = 0; i < scope.Length; i++)
                            if ((i < 48 || i >= 56) && owner.view.ReadByte(i) != scope[i]) return false;
                        accepted = result; return true;
                    }
                    catch { return false; }
                }
            }
            internal FrontierRelayCustody Detach(RelayedCompletion result)
            {
                lock (sync)
                {
                    if (!Object.ReferenceEquals(original, this) || !Object.ReferenceEquals(accepted, result) || detached || !IsCurrent) return null;
                    detached = true;
                    foreach (var watch in watches) watch.Unregister(null);
                    watches.Clear(); transfer.Release(); return this;
                }
            }
            public bool IsCurrent
            {
                get
                {
                    lock (sync)
                    {
                        if (!Object.ReferenceEquals(original, this) || closed || accepted == null) return false;
                        if (pins.IsCurrent && (detached || (transfer.IsCurrent && transfer.State == 3))) return true;
                        Close(); return false;
                    }
                }
            }
            public int FileCount { get { return IsCurrent ? pins.Count : 0; } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool LiveProofEligible { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            internal void Close()
            {
                lock (sync)
                {
                    if (closed) return; closed = true;
                    foreach (var watch in watches) watch.Unregister(null);
                    pins.Dispose(); transfer.Dispose();
                }
            }
            public void Dispose() { if (Object.ReferenceEquals(original, this)) Close(); }
        }
    }
}
