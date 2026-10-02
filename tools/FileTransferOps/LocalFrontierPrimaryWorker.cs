using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        public static Worker TryAttachFrontier(string name, Guid requestId, string path, string run, string scenario)
        { return TryAttachCore(name, requestId, path, run, scenario, true); }

        public sealed partial class Worker
        {
            private readonly Worker frontierOriginal;
            internal readonly object frontierWorkerSync = new object();
            private bool frontierRequested;
            internal FrontierWorkerInputs frontierWorkerInputs;
            internal bool AttachFrontierInputs(string name, MemoryMappedFile map, byte[] body)
            {
                lock (frontierWorkerSync)
                {
                    if (!Object.ReferenceEquals(frontierOriginal, this) || frontierRequested) return false;
                    frontierRequested = true;
                    var work = Stopwatch.StartNew();
                    LocalFrontierPrimaryScope scope = null;
                    try
                    {
                        scope = new LocalFrontierPrimaryScope(map, body);
                        frontierWorkerInputs = new FrontierWorkerInputs(this, scope, work);
                        return frontierWorkerInputs.Attach(name);
                    }
                    catch { if (frontierWorkerInputs == null && scope != null) scope.Dispose(); CloseFrontierWorker(); return false; }
#if NLINK_TEST_ATTEMPT_DISPATCH
                    finally { work.Stop(); if (frontierWorkerInputs != null) frontierWorkerInputs.PrimaryAttachWorkTicksForTest = work.ElapsedTicks; }
#else
                    finally { work.Stop(); }
#endif
                }
            }
            internal bool FrontierBaseCurrent
            {
                get
                {
#if NLINK_TEST_ATTEMPT_DISPATCH
                    try { return PrimaryBaseCheckForTest("identity", Object.ReferenceEquals(frontierOriginal, this)) &&
                        PrimaryBaseCheckForTest("disposed", Volatile.Read(ref disposed) == 0) && PrimaryBaseCheckForTest("parent-missing", parentExit != null) &&
                        PrimaryBaseCheckForTest("parent-exit", !parentExit.WaitOne(0)) && PrimaryBaseCheckForTest("request-closed", !closed.WaitOne(0)); }
                    catch (Exception error) { PrimaryBaseCheckForTest("exception:" + error.GetType().FullName, false); return false; }
#else
                    try { return Object.ReferenceEquals(frontierOriginal, this) && Volatile.Read(ref disposed) == 0 && parentExit != null && !parentExit.WaitOne(0) && !closed.WaitOne(0); }
                    catch { return false; }
#endif
                }
            }
            private bool FrontierWorkerCurrent
            { get { lock (frontierWorkerSync) { return frontierWorkerInputs != null && frontierWorkerInputs.IsCurrent; } } }
            public FrontierWorkerInputs FrontierInputs
            { get { return Object.ReferenceEquals(frontierOriginal, this) && FrontierWorkerCurrent ? frontierWorkerInputs : null; } }
            private void CloseFrontierWorker()
            { lock (frontierWorkerSync) { if (frontierWorkerInputs != null) frontierWorkerInputs.Close(); } }
        }

        // Native file/source ownership and private-primary array/view association,
        // not semantic evaluation proof, witness selection or publication authority.
        public sealed partial class FrontierWorkerInputs : IDisposable
        {
            private readonly Worker owner;
            private readonly LocalFrontierPrimaryScope scope;
            private readonly object sync = new object();
            private readonly Stopwatch work;
            private readonly List<RegisteredWaitHandle> watches = new List<RegisteredWaitHandle>();
            private LocalFrontierParentFileChannel files;
            private LocalFrontierParentFacts facts;
            private LocalGuiWriterRequest.FrontierParentFilesPending.CommitPins pins;
            private bool attached, closed;
            internal FrontierWorkerInputs(Worker owner, LocalFrontierPrimaryScope scope, Stopwatch work) { this.owner = owner; this.scope = scope; this.work = work; }
            private bool ResourceCurrent
            { get { return ResourceCurrentWithin(LocalPublicationInputLimits.ProducerMilliseconds); } }
            private const int RetainedMilliseconds = 180000;
            private int RetainedMaximumMilliseconds
            { get { return evaluation != null && sourceEnded ? RetainedMilliseconds : LocalPublicationInputLimits.ProducerMilliseconds; } }
            // Registration selects a time allowance, never resource authority.
            private bool RetainedResourceCurrent
            { get { return ResourceCurrentWithin(RetainedMaximumMilliseconds); } }
            private bool ResourceCurrentWithin(int maximumMilliseconds)
            {
#if NLINK_TEST_ATTEMPT_DISPATCH
                return PrimarySourceCheckForTest("resource:closed", !closed) && PrimarySourceBudgetForTest("resource:budget", work.ElapsedMilliseconds, maximumMilliseconds) &&
                    PrimarySourceCheckForTest("resource:base", PrimaryMeasuredCurrentForTest(0)) && PrimarySourceCheckForTest("resource:scope", PrimaryMeasuredCurrentForTest(1)) &&
                    PrimarySourceCheckForTest("resource:files", PrimaryMeasuredCurrentForTest(2)) && PrimarySourceCheckForTest("resource:pins", PrimaryMeasuredCurrentForTest(3));
#elif NLINK_TEST_NATIVE_SELECTION_COMPONENT_PROFILE
                return !closed && work.ElapsedMilliseconds <= maximumMilliseconds &&
                    ContentSelectionComponentCurrentForTest(0) && ContentSelectionComponentCurrentForTest(1) &&
                    ContentSelectionComponentCurrentForTest(2) && ContentSelectionComponentCurrentForTest(3);
#else
                return !closed && work.ElapsedMilliseconds <= maximumMilliseconds &&
                    owner.FrontierBaseCurrent && scope.IsCurrent && (files == null || files.IsCurrent) && (pins == null || pins.IsCurrent);
#endif
            }
            internal bool Attach(string name)
            {
                lock (sync)
                {
                    bool success = false; work.Start();
                    try
                    {
                        if (!ResourceCurrent) return false;
                        files = LocalFrontierParentFileChannel.Open(name + ".frontier-primary-v1", scope.Binding);
                        if (files == null) return false;
                        Action check = () => { if (!ResourceCurrent) throw new InvalidOperationException(); };
                        files.Facts = LocalFrontierParentFactsChannel.Open(files.Endpoint, files.OfferHash, check);
                        if (files.Facts == null) return false;
                        facts = LocalFrontierParentFacts.DecodeForPrimary(files.Facts.Body, files.Body, owner.AcceptanceRunId, owner.ScenarioRunId, check);
                        if (facts == null || !facts.MatchesCompletion(owner.WriterContext)) return false;
                        var offered = LocalFrontierParentFilePacket.Decode(files.Body);
                        if (offered.Gui != owner.WriterContext.RequestId) return false;
                        var source = offered.Entries[4].File;
                        string identity = source.Volume.ToString("x8") + ":" + source.IndexHigh.ToString("x8") + source.IndexLow.ToString("x8");
                        if (identity != owner.SourceIdentity || source.Length != owner.SourceBytes) return false;
                        pins = LocalGuiWriterRequest.FrontierParentFilesPending.CommitPins.PullPrimary(owner.parentExit.SafeWaitHandle, files.Body, () => ResourceCurrent);
                        if (pins == null || !ResourceCurrent || !files.Facts.MarkReceived() || !files.MarkPulled()) return false;
                        attached = true;
#if NLINK_TEST_ATTEMPT_DISPATCH
                        int watchIndex = 0;
                        foreach (var signal in new WaitHandle[] { owner.closed, owner.parentExit, files.Cancelled, files.Facts.Cancelled })
                        {
                            string reason = new[] { "request-closed", "parent-exit", "files-cancelled", "facts-cancelled" }[watchIndex++];
                            watches.Add(ThreadPool.RegisterWaitForSingleObject(signal, (s,t) => Close("watch:" + reason), null, Timeout.Infinite, true));
                        }
#else
                        foreach (var signal in new WaitHandle[] { owner.closed, owner.parentExit, files.Cancelled, files.Facts.Cancelled })
                            watches.Add(ThreadPool.RegisterWaitForSingleObject(signal, (s,t) => Close(), null, Timeout.Infinite, true));
#endif
                        AttachTransferPair(name);
                        success = ResourceCurrent; return success;
                    }
                    catch { return false; }
                    finally { work.Stop(); if (!success) Close(); }
                }
            }
            public bool IsCurrent
            {
                get
                {
                    lock (owner.frontierWorkerSync)
                    {
                        if (!Object.ReferenceEquals(owner.frontierWorkerInputs, this)) return false;
                        lock (sync) { if (attached && RetainedResourceCurrent) return true; Close(); return false; }
                    }
                }
            }
            public int FileCount { get { return IsCurrent ? pins.Count : 0; } }
            public int SourceLineCount { get { return IsCurrent ? facts.SourceLines : 0; } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            public bool LiveProofEligible { get { return false; } }
#if NLINK_TEST_ATTEMPT_DISPATCH
            internal void Close([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
#else
            internal void Close()
#endif
            {
                lock (sync)
                {
                    if (closed) return; closed = true;
#if NLINK_TEST_ATTEMPT_DISPATCH
                    PrimarySourceCloseForTest = caller;
                    PrimarySourceCloseSnapshotForTest = PrimarySourceProgressForTest(work.ElapsedMilliseconds);
#endif
                    CloseTransferPair();
                    foreach (var watch in watches) watch.Unregister(null);
                    CloseDiagnosticOutput();
                    CloseEvaluation();
                    CloseSource();
                    if (pins != null) pins.Dispose(); if (files != null) files.Dispose(); scope.Dispose(); work.Stop();
                }
            }
            public void Dispose()
            { lock (owner.frontierWorkerSync) { if (Object.ReferenceEquals(owner.frontierWorkerInputs, this)) Close(); } }
        }
    }
}
