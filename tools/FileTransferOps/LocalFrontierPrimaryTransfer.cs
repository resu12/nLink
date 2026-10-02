using System;
using System.Threading;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class FrontierRelayCustody
        {
            internal CanonicalPayloadParent payloadParent;
            internal void CopyTransferPairToPrimary(LocalRetainedSourceRequest.FrontierPrimaryInputs inputs)
            { inputs.TryTakeTransferPair(payloadParent, this, accepted); }
        }
        public sealed partial class CanonicalPayloadParent
        {
            private bool primaryTransferAttempted;
            internal CanonicalTransferPair DuplicateForPrimary(FrontierRelayCustody candidate, RelayedCompletion result)
            {
                if (!Object.ReferenceEquals(original, this) || !Object.ReferenceEquals(custody, candidate) ||
                    !Object.ReferenceEquals(completion, result) || !IsCurrent) return null;
                lock (sync)
                {
                    if (disposed || primaryTransferAttempted || work.IsRunning) return null;
                    primaryTransferAttempted = true; work.Start(); CanonicalTransferPair pair = null;
                    try
                    {
                        if (work.ElapsedMilliseconds > 5000 || !FilesCurrent()) return null;
                        pair = CanonicalTransferPair.DuplicateLocal(channel.Packet, files, identities, result.WriterContext);
                        if (pair != null && FilesCurrent() && !writerLifetime.IsCancellationRequested && work.ElapsedMilliseconds <= 5000)
                        { var value = pair; pair = null; return value; }
                        return null;
                    }
                    finally { work.Stop(); if (pair != null) pair.Dispose(); }
                }
            }
        }
    }

    public sealed partial class LocalRetainedSourceRequest
    {
        public sealed partial class FrontierPrimaryInputs
        {
            private CanonicalTransferPair transferPair;
            private LocalPayloadParentChannel transferChannel;
            internal void TryTakeTransferPair(LocalGuiWriterRequest.CanonicalPayloadParent pair,
                LocalGuiWriterRequest.FrontierRelayCustody custody, LocalGuiWriterRequest.RelayedCompletion completion)
            {
                if (pair == null) return;
                lock (sync)
                {
                    work.Start();
                    try
                    {
                        if (transferPair == null && IsCurrent) transferPair = pair.DuplicateForPrimary(custody, completion);
                        if (!IsCurrent) CloseTransferPair();
                    }
                    finally { work.Stop(); }
                }
            }
            private void OfferTransferPair()
            {
                if (transferPair == null) return;
                transferChannel = LocalPayloadParentChannel.Create(owner.Name + ".actual-transfer-primary", workerScope.Binding, transferPair.Export());
                if (transferChannel == null || !IsCurrent) CloseTransferPair();
            }
            private void CloseTransferPair()
            {
                if (transferChannel != null) transferChannel.Dispose();
                if (transferPair != null) transferPair.Dispose();
            }
        }
        public sealed partial class FrontierWorkerInputs
        {
            private CanonicalTransferPair transferPair;
            private LocalPayloadParentChannel transferChannel;
            private void AttachTransferPair(string name)
            {
                try
                {
                    transferChannel = LocalPayloadParentChannel.Open(name + ".actual-transfer-primary", scope.Binding);
                    if (transferChannel == null) return;
                    transferPair = CanonicalTransferPair.Pull(transferChannel.Packet, owner.parentExit.SafeWaitHandle, owner.WriterContext, facts);
                    if (transferPair == null || !ResourceCurrent || !transferChannel.MarkPulled()) CloseTransferPair();
                    else watches.Add(ThreadPool.RegisterWaitForSingleObject(transferChannel.Cancelled,
                        (s,t) => { lock (sync) CloseTransferPair(); }, null, Timeout.Infinite, true));
                }
                catch { CloseTransferPair(); }
            }
            private bool TransferPairCurrent
            { get { return RetainedResourceCurrent && transferPair != null && transferPair.IsCurrent && transferChannel != null && transferChannel.Current && transferChannel.State == 2; } }
            private void CloseTransferPair()
            {
                if (transferPair != null) transferPair.Dispose();
                if (transferChannel != null) transferChannel.Dispose();
            }
            public sealed partial class EvaluationCustody
            {
                private CanonicalTransferEvaluation transferEvaluation;
                private bool transferBindingAttempted;
                public CanonicalTransferEvaluation TryBindTransferPair(EvaluationPair pair, object candidateContext, string[] candidateFull,
                    string[] candidateCompact, object candidateFullView, object candidateCompactView)
                {
                    lock (inputs.owner.frontierWorkerSync) lock (inputs.sync)
                    {
                        if (!Original || state != 3 || pair == null || !Object.ReferenceEquals(issued, pair) || transferBindingAttempted ||
                            !Matches(candidateContext, candidateFull, candidateCompact, candidateFullView, candidateCompactView)) return null;
                        transferBindingAttempted = true;
                        inputs.work.Start();
                        try
                        {
                            if (!Current() || !inputs.TransferPairCurrent) return null;
                            transferEvaluation = new CanonicalTransferEvaluation(this, pair, inputs.transferPair);
                            return transferEvaluation;
                        }
                        catch { return null; }
                        finally { inputs.work.Stop(); }
                    }
                }
                internal bool TransferCurrent(CanonicalTransferEvaluation value)
                {
                    lock (inputs.owner.frontierWorkerSync) lock (inputs.sync)
                    {
                        return Original && state == 3 && Object.ReferenceEquals(transferEvaluation, value) &&
                            Object.ReferenceEquals(issued, value.Pair) && CheckCurrent() && inputs.TransferPairCurrent;
                    }
                }
            }
            public sealed class CanonicalTransferEvaluation
            {
                private readonly EvaluationCustody owner;
                internal readonly EvaluationPair Pair;
                private readonly CanonicalTransferPair files;
                internal CanonicalTransferEvaluation(EvaluationCustody owner, EvaluationPair pair, CanonicalTransferPair files)
                { this.owner = owner; Pair = pair; this.files = files; }
                public bool IsCurrent { get { return owner.TransferCurrent(this); } }
                public long ByteCount { get { return IsCurrent ? 268435456 : 0; } }
                public string Sha256 { get { return IsCurrent ? files.Sha256 : null; } }
                public bool LiveProofEligible { get { return false; } }
                public bool TransferIntegrityEstablished { get { return false; } }
                public bool PublicationCompletionIssued { get { return false; } }
            }
        }
    }
}
