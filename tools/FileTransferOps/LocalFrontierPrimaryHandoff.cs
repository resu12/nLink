using System;
using System.Threading;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        public sealed partial class FrontierPrimaryInputs
        {
            private bool workerOfferAttempted;
            private LocalFrontierParentFileChannel workerChannel;
            private LocalFrontierPrimaryScope workerScope;
            // Requires an already-bound actual worker. No caller process ids,
            // descriptors, fact bytes or request digests are accepted.
            public bool TryPrepareWorkerHandoff()
            {
                lock (owner.frontierInputSync)
                {
                    if (!Object.ReferenceEquals(owner.frontierInputs, this)) return false;
                    lock (sync)
                    {
                        bool success = false; work.Start();
                        try
                        {
                            if (workerOfferAttempted) return false; workerOfferAttempted = true;
                            if (!IsCurrent || owner.workerExit == null || owner.workerId <= 0 || owner.workerCreation <= 0 ||
                                owner.workerId == owner.view.ReadInt32(4) || owner.view.ReadInt32(0) != WriterMagic || Array.Exists(opened, value => value)) return false;
                            workerScope = new LocalFrontierPrimaryScope(owner.map, owner.frontierBody);
                            workerChannel = LocalFrontierParentFileChannel.Create(owner.Name + ".frontier-primary-v1", workerScope.Binding,
                                pins.ExportPrimaryOffer(), CancellationToken.None);
                            if (workerChannel == null) return false;
                            Action check = () => { if (!IsCurrent || !workerScope.IsCurrent) throw new InvalidOperationException(); };
                            workerChannel.Facts = LocalFrontierParentFactsChannel.Create(workerChannel.Endpoint, workerChannel.OfferHash, facts.CopyForPrimary(), check);
                            if (workerChannel.Facts == null) return false;
                            WatchRequest(workerChannel.Cancelled); WatchRequest(workerChannel.Facts.Cancelled);
                            success = IsCurrent && workerScope.IsCurrent; return success;
                        }
                        catch { return false; }
                        finally { work.Stop(); if (!success) Close(); }
                    }
                }
            }
        }
    }
}
