using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class FrontierParentCustody
        {
            private Completion acceptedCompletion;
            private bool primaryAttempted;
            private bool primaryHashAttempted;
            // The optional primary's original hash pass, not a second scan.
            public LocalInputReadLease.HashReadResult TryHashPrimarySource()
            {
                lock (owner.frontierParentSync)
                {
                    if (!Object.ReferenceEquals(owner.frontierParentCustody, this)) return null;
                    lock (sync)
                    {
                        if (closed || !accepted) return null;
                        var work = owner.frontierParentRegistration.parentWork; work.Start();
                        LocalInputReadLease.HashReadResult result = null;
                        try
                        {
                            if (primaryHashAttempted) return null; primaryHashAttempted = true;
                            Func<bool> current = () => work.ElapsedMilliseconds <= 5000 && IsCurrent && facts.MatchesCompletion(acceptedCompletion);
                            if (!current()) return null;
                            result = LocalInputReadLease.HashFrontier(pins.Handles[4], acceptedCompletion.Outputs[3], current);
                            return result;
                        }
                        catch { return null; }
                        finally { work.Stop(); if (result == null) Close(); }
                    }
                }
            }
            // Request-side custody only, not worker/evaluator authority.
            public LocalRetainedSourceRequest.FrontierPrimaryInputs TryTransferToPrimary(LocalRetainedSourceRequest request)
            { return request == null || !Object.ReferenceEquals(owner.frontierParentCustody, this) ? null : request.AcceptFrontierInputs(this); }
            internal LocalRetainedSourceRequest.FrontierPrimaryInputs Transfer(LocalRetainedSourceRequest request)
            {
                lock (owner.frontierParentSync)
                {
                    if (!Object.ReferenceEquals(owner.frontierParentCustody, this)) return null;
                    lock (sync)
                    {
                        if (closed || !accepted) return null;
                        var work = owner.frontierParentRegistration.parentWork;
                        FrontierParentFilesPending.CommitPins transferred = null;
                        LocalRetainedSourceRequest.FrontierPrimaryInputs result = null;
                        bool success = false; work.Start();
                        try
                        {
                            if (primaryAttempted) return null; primaryAttempted = true;
                            Func<bool> current = () => work.ElapsedMilliseconds <= 5000 && IsCurrent &&
                                request.FrontierScopeCurrent(acceptedCompletion, true) && pins.MatchesPrimarySource(request.FrontierSourceHandle);
                            if (!current()) return null;
                            transferred = pins.DuplicateForPrimary(current);
                            if (transferred == null || !current()) return null;
                            result = new LocalRetainedSourceRequest.FrontierPrimaryInputs(request, facts, transferred);
                            result.WatchRequest(request.FrontierCancellation);
                            success = current() && result.ResourceCurrent;
                            return success ? result : null;
                        }
                        catch { return null; }
                        finally
                        {
                            work.Stop(); Close();
                            if (!success) { if (result != null) result.Close(); else if (transferred != null) transferred.Dispose(); }
                        }
                    }
                }
            }
        }
        public sealed partial class FrontierParentFilesPending
        {
            internal sealed partial class CommitPins
            {
                internal bool MatchesPrimarySource(SafeFileHandle handle)
                { lock (sync) { return IsCurrent && Matches(handle, packet.Entries[4].File); } }
                internal CommitPins DuplicateForPrimary(Func<bool> current)
                {
                    lock (sync)
                    {
                        var result = new CommitPins(packet); bool success = false;
                        try
                        {
                            if (!IsCurrent || !current()) return null;
                            for (int i = 0; i < Handles.Count; i++)
                            {
                                if (!current()) return null;
                                SafeFileHandle duplicate = null;
                                try
                                {
                                    if (!DuplicateCommitHandle(GetCurrentProcess(), Handles[i], GetCurrentProcess(), out duplicate, 0x80000000, false, 0) ||
                                        !Matches(duplicate, packet.Entries[i].File)) return null;
                                    result.Handles.Add(duplicate); duplicate = null;
                                }
                                finally { if (duplicate != null) duplicate.Dispose(); }
                            }
                            success = IsCurrent && current() && result.IsCurrent; return success ? result : null;
                        }
                        finally { if (!success) result.Dispose(); }
                    }
                }
            }
        }
    }
    public sealed partial class LocalRetainedSourceRequest
    {
        private readonly LocalRetainedSourceRequest frontierOriginal;
        private readonly object frontierInputSync = new object();
        private byte[] frontierBody, frontierHeader;
        private LocalGuiWriterRequest.Completion frontierWriter;
        private SafeFileHandle frontierSourceHandle;
        private FrontierPrimaryInputs frontierInputs;
        private bool frontierInputAttempted;
        private void RememberFrontierSource(byte[] body, LocalGuiWriterRequest.Completion writer)
        {
            if (writer == null) return;
            frontierBody = (byte[])body.Clone(); frontierWriter = writer;
            frontierSourceHandle = frozenSource.SafeFileHandle;
            frontierHeader = new byte[HeaderBytes]; view.ReadArray(0, frontierHeader, 0, frontierHeader.Length);
        }
        internal SafeFileHandle FrontierSourceHandle { get { return frontierSourceHandle; } }
        internal WaitHandle FrontierCancellation { get { return closed; } }
        internal bool FrontierScopeCurrent(LocalGuiWriterRequest.Completion writer, bool unbound)
        {
            try
            {
                if (!Object.ReferenceEquals(frontierOriginal, this) || !Object.ReferenceEquals(frontierWriter, writer) ||
                    writer == null || frontierBody == null || frontierHeader == null || Volatile.Read(ref disposed) != 0 ||
                    closed.WaitOne(0) || frozenSource == null || !frozenSource.CanRead || frozenSource.CanWrite ||
                    (unbound && workerExit != null) || (workerExit != null && workerExit.WaitOne(0))) return false;
                for (int i = 0; i < HeaderBytes; i++)
                    if ((i < 16 || i >= 20) && (i < 40 || i >= 48) && view.ReadByte(i) != frontierHeader[i]) return false;
                if (view.ReadInt32(16) != workerId || view.ReadInt64(40) != workerCreation) return false;
                for (int i = 0; i < frontierBody.Length; i++) if (view.ReadByte(HeaderBytes + i) != frontierBody[i]) return false;
                return true;
            }
            catch { return false; }
        }
        internal FrontierPrimaryInputs AcceptFrontierInputs(LocalGuiWriterRequest.FrontierParentCustody custody)
        {
            if (!Object.ReferenceEquals(frontierOriginal, this)) return null;
            lock (frontierInputSync)
            {
                if (frontierInputAttempted) return null; frontierInputAttempted = true;
                frontierInputs = custody.Transfer(this);
                if (frontierInputs != null && !frontierInputs.IsCurrent) { frontierInputs.Close(); return null; }
                return frontierInputs;
            }
        }
        private void CloseFrontierInputs()
        { lock (frontierInputSync) { if (frontierInputs != null) frontierInputs.Close(); } }
        private void WatchFrontierWorker()
        { lock (frontierInputSync) { if (frontierInputs != null) frontierInputs.WatchRequest(workerExit); } }
        public sealed partial class FrontierPrimaryInputs : IDisposable
        {
            private readonly LocalRetainedSourceRequest owner;
            private readonly LocalFrontierParentFacts facts;
            private readonly LocalGuiWriterRequest.FrontierParentFilesPending.CommitPins pins;
            private readonly object sync = new object();
            private readonly List<RegisteredWaitHandle> watches = new List<RegisteredWaitHandle>();
            private readonly Stopwatch work = new Stopwatch();
            private bool closed;
            internal FrontierPrimaryInputs(LocalRetainedSourceRequest owner, LocalFrontierParentFacts facts,
                LocalGuiWriterRequest.FrontierParentFilesPending.CommitPins pins)
            { this.owner = owner; this.facts = facts; this.pins = pins; readers = new OwnedReader[pins.Count]; opened = new bool[pins.Count]; }
            internal bool ResourceCurrent { get { lock (sync) { return !closed && pins.IsCurrent; } } }
            internal void WatchRequest(WaitHandle signal)
            { lock (sync) { if (!closed) watches.Add(ThreadPool.RegisterWaitForSingleObject(signal, (s,t) => Close(), null, Timeout.Infinite, true)); } }
            public bool IsCurrent
            {
                get
                {
                    lock (owner.frontierInputSync)
                    {
                        if (!Object.ReferenceEquals(owner.frontierInputs, this)) return false;
                        lock (sync)
                        {
                            if (!closed && owner.FrontierScopeCurrent(owner.frontierWriter, false) &&
                                work.ElapsedMilliseconds <= LocalPublicationInputLimits.ProducerMilliseconds && pins.IsCurrent &&
                                pins.MatchesPrimarySource(owner.FrontierSourceHandle) && (workerChannel == null || workerChannel.IsCurrent)) return true;
                            Close(); return false;
                        }
                    }
                }
            }
            public int FileCount { get { return IsCurrent ? pins.Count : 0; } }
            public int SourceLineCount { get { return IsCurrent ? facts.SourceLines : 0; } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            public bool LiveProofEligible { get { return false; } }
            internal void Close()
            {
                lock (sync)
                {
                    if (closed) return; closed = true;
                    CloseTransferPair();
                    foreach (var watch in watches) watch.Unregister(null);
                    foreach (var reader in readers) if (reader != null) reader.Close();
                    if (workerChannel != null) workerChannel.Dispose();
                    if (workerScope != null) workerScope.Dispose();
                    pins.Dispose(); work.Stop();
                }
            }
            public void Dispose()
            { lock (owner.frontierInputSync) { if (Object.ReferenceEquals(owner.frontierInputs, this)) Close(); } }
        }
    }
}
