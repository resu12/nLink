using System;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class FrontierParentRegistration
        {
            private bool acknowledgmentAttempted;
            private FrontierParentAcknowledgment acknowledgment;
            internal FrontierParentAcknowledgment Acknowledge(FrontierParentFacts facts)
            {
                lock (owner.frontierParentSync)
                {
                    if (!Object.ReferenceEquals(owner.frontierParentRegistration, this) || !Object.ReferenceEquals(parentFacts, facts)) return null;
                    bool success = false; LocalFrontierParentAcknowledgmentChannel channel = null; parentWork.Start();
                    try
                    {
                        if (acknowledgmentAttempted) return null; acknowledgmentAttempted = true;
                        if (parentWork.ElapsedMilliseconds > 5000 || !OwnsParentFacts(facts)) return null;
                        var files = Channel.Files; var received = files.Facts;
                        if (received.Acknowledgment != null) return null;
                        channel = LocalFrontierParentAcknowledgmentChannel.Create(files.Endpoint, files.OfferHash, received.ReceiptHash);
                        if (channel == null || !OwnsParentFacts(facts) || parentWork.ElapsedMilliseconds > 5000) return null;
                        received.Acknowledgment = channel; acknowledgment = new FrontierParentAcknowledgment(this, facts, channel);
                        success = OwnsAcknowledgment(acknowledgment) && parentWork.ElapsedMilliseconds <= 5000;
                        return success ? acknowledgment : null;
                    }
                    catch { return null; }
                    finally { parentWork.Stop(); if (!success) { owner.RetireFrontierParentRegistration(); if (channel != null) channel.Dispose(); } }
                }
            }
            internal bool OwnsAcknowledgment(FrontierParentAcknowledgment value)
            {
                lock (owner.frontierParentSync)
                {
                    if (value == null || !Object.ReferenceEquals(acknowledgment, value)) return false;
                    if (Object.ReferenceEquals(value.Facts, parentFacts) && OwnsParentFacts(parentFacts) &&
                        Object.ReferenceEquals(Channel.Files.Facts.Acknowledgment, value.Channel) && value.Channel.IsCurrent) return true;
                    owner.RetireFrontierParentRegistration(); return false;
                }
            }
            internal void DisposeAcknowledgment(FrontierParentAcknowledgment value)
            { lock (owner.frontierParentSync) { if (Object.ReferenceEquals(acknowledgment, value)) owner.RetireFrontierParentRegistration(); } }
        }
        public sealed partial class FrontierParentFacts
        {
            public FrontierParentAcknowledgment TryAcknowledge() { return owner.Acknowledge(this); }
        }
        public sealed partial class FrontierParentAcknowledgment : IDisposable
        {
            private readonly FrontierParentRegistration owner;
            internal readonly FrontierParentFacts Facts;
            internal readonly LocalFrontierParentAcknowledgmentChannel Channel;
            internal FrontierParentAcknowledgment(FrontierParentRegistration owner, FrontierParentFacts facts, LocalFrontierParentAcknowledgmentChannel channel)
            { this.owner = owner; Facts = facts; Channel = channel; }
            public bool IsCurrent { get { return owner.OwnsAcknowledgment(this); } }
            public bool GuiConfirmed { get { return IsCurrent && Channel.State == 2; } }
            public bool LiveProofEligible { get { return false; } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            public void Dispose() { owner.DisposeAcknowledgment(this); }
        }
        public sealed partial class FrontierFinalizerBinding
        {
            private bool parentAcknowledgmentAttempted;
            public bool TryConfirmParentAcknowledgment()
            {
                lock (sourceSync)
                {
                    if (!IsCurrent) return false;
                    bool success = false; LocalFrontierParentAcknowledgmentChannel channel = null;
                    try
                    {
                        if (parentAcknowledgmentAttempted) return false; parentAcknowledgmentAttempted = true;
                        if (!OwnsParentSubmission(parentSubmission) || !parentSubmission.FactsReceived) return false;
                        var adoption = files.GetOriginalAdoption(); var fileChannel = parentSubmission.Channel;
                        if (adoption == null || fileChannel.Facts.Acknowledgment != null) return false;
                        success = adoption.TryRunRecoveryRead(() =>
                        {
                            if (!adoption.IsSourceVerified || !parentPreparation.Files.IsCurrent) return false;
                            channel = LocalFrontierParentAcknowledgmentChannel.Open(fileChannel.Endpoint, fileChannel.OfferHash, fileChannel.Facts.ReceiptHash);
                            return channel != null && adoption.IsSourceVerified && parentPreparation.Files.IsCurrent && channel.MarkConfirmed();
                        });
                        if (!success) return false;
                        fileChannel.Facts.Acknowledgment = channel;
                        success = OwnsParentSubmission(parentSubmission); return success;
                    }
                    catch { return false; }
                    finally { if (!success) { Dispose(); if (channel != null) channel.Dispose(); } }
                }
            }
        }
        public sealed partial class FrontierParentFileSubmission
        {
            public bool ParentAcknowledgmentConfirmed
            { get { return IsCurrent && Channel.Facts != null && Channel.Facts.Acknowledgment != null && Channel.Facts.Acknowledgment.State == 2; } }
        }
    }
}
