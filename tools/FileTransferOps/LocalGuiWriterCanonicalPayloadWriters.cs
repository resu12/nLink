using System;
using System.Threading;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        // Optional payload diagnostics must not erase or repeat the original
        // completion take, including rejection after that take has succeeded.
        public Completion TakeAfterExitWithPayloads(FrontierGuiReports reports, FrontierParentCustody custody)
        {
            lock (frontierParentSync)
            {
                if (!Object.ReferenceEquals(original, this) || Volatile.Read(ref sealedState) != 0) return null;
                Completion completion = null;
                bool completionAttempted = false;
                try
                {
                    if (payloadWrapper != null && payloadWrapper.TryTakeAfterExit(reports) && payloadWrapper.TryVerifyFiles())
                        payloadWrapper.TryTakeWithWritersCore(reports, custody, out completion, out completionAttempted);
                }
                catch { } // Omit payload custody, not the original completion.
                return completionAttempted ? completion : TakeAfterExit();
            }
        }

        public sealed partial class CanonicalPayloadWrapper
        {
            private FrontierParentCustody writerCustody;
            private RegisteredWaitHandle writerCancelled;
            private bool takingWriters, writerTakeAttempted;

            private bool WriterScopeCurrent()
            {
                if (writerCustody == null) return owner.sealedState == 0 && !owner.closed.WaitOne(0);
                return Object.ReferenceEquals(owner.frontierParentCustody, writerCustody) &&
                    writerCustody.IsCurrent && owner.ParentTerminalScopeCurrent();
            }
            private void RequestClosed()
            {
                lock (owner.frontierParentSync)
                lock (sync)
                {
                    // Only the original synchronous terminal take can preserve payload pins.
                    if (!takingWriters && (writerCustody == null || !writerCustody.IsCurrent)) Dispose();
                }
            }
            private void RetireWriterLifetime()
            { if (writerCancelled != null) writerCancelled.Unregister(null); }
            public Completion TryTakeWithWriters(FrontierGuiReports candidate, FrontierParentCustody custody)
            {
                Completion completion;
                bool completionAttempted;
                return TryTakeWithWritersCore(candidate, custody, out completion, out completionAttempted);
            }
            internal Completion TryTakeWithWritersCore(FrontierGuiReports candidate, FrontierParentCustody custody,
                out Completion completion, out bool completionAttempted)
            {
                completion = null; completionAttempted = false;
                lock (owner.frontierParentSync)
                lock (sync)
                {
                    if (!Object.ReferenceEquals(original, this) || !Object.ReferenceEquals(reports, candidate) ||
                        custody == null || !Object.ReferenceEquals(owner.frontierParentCustody, custody) ||
                        writerTakeAttempted || !IsCurrent || !verified) return null;
                    writerTakeAttempted = true; takingWriters = true;
                    bool success = false;
                    try
                    {
                        completionAttempted = true;
                        completion = owner.TakeAfterExit();
                        if (work.IsRunning || work.ElapsedMilliseconds > 5000) return null;
                        work.Start();
                        try
                        {
                            if (!custody.MatchesPayloadWriters(owner, completion, writerRequests, writerProcesses,
                                writerCreations, new[] { helpeeWriter, helperWriter }, writerLastSequences)) return null;
                            writerCustody = custody;
                            writerCancelled = custody.WatchPayloadLifetime(Dispose);
                            success = IsCurrent && work.ElapsedMilliseconds <= 5000;
                            return success ? completion : null;
                        }
                        finally { work.Stop(); }
                    }
                    finally { takingWriters = false; if (!success) Dispose(); }
                }
            }
            public bool NativeWritersBound { get { return IsCurrent && writerCustody != null; } }
        }
        public sealed partial class FrontierParentCustody
        {
            internal RegisteredWaitHandle WatchPayloadLifetime(Action close)
            {
                lock (owner.frontierParentSync)
                lock (sync)
                {
                    if (!IsCurrent) throw new InvalidOperationException("payload_writer_custody_retired");
                    return ThreadPool.RegisterWaitForSingleObject(channel.Cancelled, (s,t) => close(), null, Timeout.Infinite, true);
                }
            }
            internal bool MatchesPayloadWriters(LocalGuiWriterRequest request, Completion completion,
                Guid[] requests, int[] processes, long[] creations, string[] writers, long[] sequences)
            {
                lock (owner.frontierParentSync)
                lock (sync)
                {
                    return Object.ReferenceEquals(owner, request) && Object.ReferenceEquals(owner.frontierParentCustody, this) &&
                        completion != null && Object.ReferenceEquals(acceptedCompletion, completion) && IsCurrent &&
                        facts.MatchesCompletion(completion) && facts.MatchesPayloadWriters(requests, processes, creations, writers, sequences);
                }
            }
        }
    }
    internal sealed partial class LocalFrontierParentFacts
    {
        internal bool MatchesPayloadWriters(Guid[] requests, int[] processes, long[] creations, string[] ids, long[] sequences)
        {
            if (manifests.Length != 2) return false;
            for (int i = 0; i < 2; i++)
            {
                LocalFrontierFileManifest found = null;
                foreach (var manifest in manifests) if (manifest.AppRequest == requests[i]) found = manifest;
                if (found == null || found.AppId != processes[i] || found.AppCreation != creations[i] ||
                    found.Writer != ids[i] || found.Drain.Last != sequences[i]) return false;
                long last;
                if (!writers.TryGetValue(ids[i], out last) || last != sequences[i]) return false;
            }
            return requests[0] != requests[1] && ids[0] != ids[1];
        }
    }
}
