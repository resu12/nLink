using System;
using System.IO;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class FrontierFinalizerBinding
        {
            private bool parentFactsAttempted;
            public bool TryPublishParentFacts()
            {
                lock (sourceSync)
                {
                    if (!IsCurrent) return false;
                    bool success = false; LocalFrontierParentFactsChannel channel = null;
                    try
                    {
                        if (parentFactsAttempted) return false; parentFactsAttempted = true;
                        if (!OwnsParentSubmission(parentSubmission) || !parentSubmission.FilesPulled) return false;
                        var adoption = files.GetOriginalAdoption(); var fileChannel = parentSubmission.Channel;
                        if (adoption == null || fileChannel.Facts != null) return false;
                        success = adoption.TryRunRecoveryRead(() =>
                        {
                            // Resource/currentness callback only: do not enter
                            // GUI/finalizer locks under the adopter lock.
                            Action check = () => { if (!adoption.IsSourceVerified || !parentPreparation.Files.IsCurrent) throw new IOException("parent_facts_revoked"); };
                            var bytes = LocalFrontierParentFacts.Encode(parentPreparation, files, fileChannel.Body, check);
                            if (LocalFrontierParentFacts.Decode(bytes, fileChannel.Body, finalizer.run, finalizer.scenario, check) == null) return false;
                            channel = LocalFrontierParentFactsChannel.Create(fileChannel.Endpoint, fileChannel.OfferHash, bytes, check);
                            return channel != null;
                        });
                        if (!success) return false;
                        fileChannel.Facts = channel; success = OwnsParentSubmission(parentSubmission); return success;
                    }
                    catch { return false; }
                    finally { if (!success) { Dispose(); if (channel != null) channel.Dispose(); } }
                }
            }
        }
        public sealed partial class FrontierParentFileSubmission
        {
            public bool FactsPublished { get { return IsCurrent && Channel.Facts != null; } }
            public bool FactsReceived { get { return IsCurrent && Channel.Facts != null && Channel.Facts.State == 2; } }
        }
        public sealed partial class FrontierParentRegistration
        {
            private bool factsAttempted;
            private FrontierParentFacts parentFacts;
            internal FrontierParentFacts ReceiveFacts(FrontierParentFilesPending pending)
            {
                lock (owner.frontierParentSync)
                {
                    if (!Object.ReferenceEquals(owner.frontierParentRegistration, this) || !Object.ReferenceEquals(PendingFiles, pending)) return null;
                    bool success = false; LocalFrontierParentFactsChannel channel = null; parentWork.Start();
                    try
                    {
                        if (factsAttempted) return null; factsAttempted = true;
                        Action check = () => { if (parentWork.ElapsedMilliseconds > 5000 || !OwnsPendingFiles(pending)) throw new IOException("parent_facts_revoked"); };
                        check(); var fileChannel = Channel.Files; if (fileChannel.Facts != null) return null;
                        channel = LocalFrontierParentFactsChannel.Open(fileChannel.Endpoint, fileChannel.OfferHash, check);
                        if (channel == null) return null;
                        var packet = LocalFrontierParentFacts.Decode(channel.Body, fileChannel.Body, owner.run, owner.scenario, check);
                        if (packet == null) return null;
                        check(); if (!channel.MarkReceived()) return null;
                        fileChannel.Facts = channel; parentFacts = new FrontierParentFacts(this, pending, packet);
                        success = OwnsParentFacts(parentFacts) && parentWork.ElapsedMilliseconds <= 5000; return success ? parentFacts : null;
                    }
                    catch { return null; }
                    finally { parentWork.Stop(); if (!success) { owner.RetireFrontierParentRegistration(); if (channel != null) channel.Dispose(); } }
                }
            }
            internal bool OwnsParentFacts(FrontierParentFacts value)
            {
                lock (owner.frontierParentSync)
                {
                    if (value == null || !Object.ReferenceEquals(parentFacts, value)) return false;
                    return Object.ReferenceEquals(value.Pending, PendingFiles) && OwnsPendingFiles(PendingFiles) && Channel.Files.Facts != null && Channel.Files.Facts.State == 2;
                }
            }
            internal void DisposeParentFacts(FrontierParentFacts value)
            { lock (owner.frontierParentSync) { if (Object.ReferenceEquals(parentFacts, value)) owner.RetireFrontierParentRegistration(); } }
        }
        public sealed partial class FrontierParentFilesPending
        {
            public FrontierParentFacts TryReceiveFacts()
            { return Object.ReferenceEquals(original, this) ? owner.ReceiveFacts(this) : null; }
        }
        // Complete fact receipt is still provisional and GUI-lifetime-bound.
        public sealed partial class FrontierParentFacts : IDisposable
        {
            private readonly FrontierParentRegistration owner;
            internal readonly FrontierParentFilesPending Pending;
            internal readonly LocalFrontierParentFacts Packet;
            internal FrontierParentFacts(FrontierParentRegistration owner, FrontierParentFilesPending pending, LocalFrontierParentFacts packet)
            { this.owner = owner; Pending = pending; Packet = packet; }
            public bool IsCurrent { get { return owner.OwnsParentFacts(this); } }
            public int SourceLineCount { get { return IsCurrent ? Packet.SourceLines : 0; } }
            public int OriginCount { get { return IsCurrent ? Packet.OriginCount : 0; } }
            public int RecoveryMarkerCount { get { return IsCurrent ? Packet.MarkerCount : 0; } }
            public bool LiveProofEligible { get { return false; } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            public void Dispose() { owner.DisposeParentFacts(this); }
        }
    }
}
