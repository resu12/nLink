using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        private FrontierRelayReports payloadRelayReports;
        private CanonicalPayloadParent payloadParent;
        public RelayedCompletion TakeRelayAfterExitWithPayloads(FrontierRelayReports reports)
        {
            lock (frontierRelaySync)
            {
                if (!Object.ReferenceEquals(original, this) || !relayMode || Volatile.Read(ref sealedState) != 0) return null;
                RelayedCompletion result = null;
                bool completionAttempted = false;
                try
                {
                    if (payloadParent != null)
                        payloadParent.TryTakeAfterExitCore(reports, true, out result, out completionAttempted);
                }
                catch { } // Optional payload rejection cannot consume the ordinary result.
                return completionAttempted ? result : TakeRelayAfterExit();
            }
        }
        private void RememberPayloadRelayReports(FrontierRelayReports reports) { payloadRelayReports = reports; }
        public CanonicalPayloadParent TryStartPayloadParent(FrontierRelayReports reports)
        {
            lock (frontierRelaySync)
            {
                if (!Object.ReferenceEquals(original, this) || !relayMode || payloadParent != null ||
                    reports == null || !Object.ReferenceEquals(payloadRelayReports, reports) ||
                    frontierRelayRegistration == null || !Object.ReferenceEquals(frontierRelayRegistration, reports.Registration) ||
                    !frontierRelayRegistration.IsCurrent || ReadAttempt(view) == null) return null;
                payloadParent = frontierRelayRegistration.CreatePayloadParent(reports);
                return payloadParent;
            }
        }
        public sealed partial class FrontierRelayRegistration
        {
            internal CanonicalPayloadParent CreatePayloadParent(FrontierRelayReports reports)
            { return new CanonicalPayloadParent(owner, reports, transferWork, channel.Endpoint, channel.Binding); }
        }
        public sealed partial class FrontierRelayCustody
        {
            private readonly CancellationTokenSource payloadLifetime = new CancellationTokenSource();
            internal CancellationToken PayloadLifetime { get { return payloadLifetime.Token; } }
            partial void ObserveParentMatch(string reason);
            private bool RefuseParentMatch(string reason)
            { ObserveParentMatch(reason); return false; }
            internal bool MatchesParentPayload(LocalGuiWriterRequest request, RelayedCompletion result,
                Guid gui, byte[] completionHash, Guid[] requests, int[] processes, long[] creations, string[] writers, long[] sequences)
            {
                lock (sync)
                {
                    ObserveParentMatch("evaluating");
                    if (!Object.ReferenceEquals(original, this)) return RefuseParentMatch("foreign-custody");
                    if (!Object.ReferenceEquals(owner, request)) return RefuseParentMatch("foreign-owner");
                    if (!Object.ReferenceEquals(accepted, result)) return RefuseParentMatch("accepted-result");
                    if (!IsCurrent) return RefuseParentMatch("custody-not-current");
                    if (result.WriterContext.RequestId != gui) return RefuseParentMatch("gui-request");
                    if (!CanonicalPayloadParent.SameCompletion(result.WriterContext, completionHash)) return RefuseParentMatch("completion-hash");
                    if (!facts.MatchesCompletion(result.WriterContext)) return RefuseParentMatch("completion-facts");
                    if (!facts.MatchesPayloadWriters(requests, processes, creations, writers, sequences)) return RefuseParentMatch("payload-writer-facts");
                    ObserveParentMatch("matched");
                    return true;
                }
            }
        }
        public sealed partial class CanonicalPayloadWrapper
        {
            private bool parentOfferAttempted;
            internal LocalPayloadParentChannel CreateParentPayloadOffer(FrontierRelayHandoff handoff, Completion completion, string endpoint, byte[] binding)
            {
                lock (owner.frontierParentSync) lock (sync)
                {
                    if (!Object.ReferenceEquals(original, this) || parentOfferAttempted || !IsCurrent || !verified ||
                        writerCustody == null || !handoff.MatchesPayloadAttempt(attemptBytes) ||
                        !writerCustody.MatchesPayloadWriters(owner, completion, writerRequests, writerProcesses,
                            writerCreations, new[] { helpeeWriter, helperWriter }, writerLastSequences)) return null;
                    parentOfferAttempted = true;
                    if (work.IsRunning || work.ElapsedMilliseconds > 5000) return null;
                    work.Start();
                    try
                    {
                        using (var stream = new MemoryStream())
                        using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true)))
                        {
                            writer.Write(owner.RequestId.ToByteArray());
                            writer.Write(attemptBytes.Length); writer.Write(attemptBytes);
                            writer.Write(CanonicalPayloadParent.CompletionHash(completion));
                            writer.Write(session); writer.Write(transfer);
                            for (int i = 0; i < 2; i++)
                            {
                                writer.Write(writerRequests[i].ToByteArray()); writer.Write(writerProcesses[i]);
                                writer.Write(writerCreations[i]); writer.Write(i == 0 ? helpeeWriter : helperWriter);
                                writer.Write(writerLastSequences[i]);
                                writer.Write(files[i].DangerousGetHandle().ToInt64()); writer.Write(identities[i]);
                                writer.Write(268435456L); writer.Write(hashes[i]);
                            }
                            writer.Flush();
                            if (stream.Length > LocalPayloadParentChannel.Capacity || !IsCurrent || work.ElapsedMilliseconds > 5000) return null;
                            var channel = LocalPayloadParentChannel.Create(endpoint, binding, stream.ToArray());
                            if (channel != null && (!IsCurrent || work.ElapsedMilliseconds > 5000)) { channel.Dispose(); return null; }
                            return channel;
                        }
                    }
                    finally { work.Stop(); }
                }
            }
            internal bool CommitParentPayload(LocalPayloadParentChannel channel)
            {
                lock (owner.frontierParentSync) lock (sync)
                {
                    if (work.IsRunning) return false;
                    work.Start();
                    try { return NativeWritersBound && work.ElapsedMilliseconds <= 5000 && channel.Commit() && work.ElapsedMilliseconds <= 5000; }
                    finally { work.Stop(); }
                }
            }
        }
        public sealed partial class FrontierRelayHandoff
        {
            partial void BeforePayloadCommit(ref bool cancelled);
            private bool payloadOfferAttempted;
            internal bool MatchesPayloadAttempt(byte[] attempt)
            { return owner.OwnsFrontierHandoff(this) && !offerAttempted && CanonicalPayloadWrapper.Equal(attempt, ReadAttempt(owner.owner.view)); }
            public bool TryOfferPayloadPair(CanonicalPayloadWrapper pair, Completion completion)
            {
                lock (owner.frontierRelaySync)
                {
                    if (!owner.OwnsFrontierHandoff(this) || payloadOfferAttempted || pair == null || !Matches(completion)) return false;
                    var offer = pair.CreateParentPayloadOffer(this, completion, channel.Endpoint, channel.Binding);
                    if (offer == null) return false;
                    payloadOfferAttempted = true;
                    bool success = false;
                    try
                    {
                        if (offer == null) return false;
                        bool cancelBeforeCommit = false;
                        BeforePayloadCommit(ref cancelBeforeCommit);
                        int signal = WaitHandle.WaitAny(new WaitHandle[] { owner.owner.parentExit, channel.Cancelled, offer.Cancelled, offer.Pulled }, 20000);
                        success = signal == 3 && !cancelBeforeCommit && IsCurrent && pair.NativeWritersBound &&
                            offer.Current && offer.State == 2 && pair.CommitParentPayload(offer);
                        return success;
                    }
                    finally { if (offer != null) { if (success) offer.Release(); else offer.Dispose(); } }
                }
            }
        }
        // Actual transfer handles only. The accepted native writer owner remains
        // a separate original object; disposing this pair does not dispose it.
        public sealed partial class CanonicalPayloadParent : IDisposable
        {
            private readonly CanonicalPayloadParent original;
            private readonly LocalGuiWriterRequest owner;
            private readonly FrontierRelayReports reports;
            private readonly Stopwatch work;
            private readonly string endpoint;
            private readonly byte[] binding, attempt;
            private readonly object sync = new object();
            private readonly SafeFileHandle[] files = new SafeFileHandle[2];
            private readonly string[] identities = new string[2], writers = new string[2];
            private readonly Guid[] requests = new Guid[2];
            private readonly int[] processes = new int[2];
            private readonly long[] creations = new long[2], sequences = new long[2];
            private readonly RegisteredWaitHandle closedWatch, exitWatch;
            private LocalPayloadParentChannel channel;
            private FrontierRelayCustody custody;
            private RelayedCompletion completion;
            private RegisteredWaitHandle writerWatch;
            private CancellationToken writerLifetime;
            private Guid gui;
            private byte[] completionHash;
            private bool pullAttempted, takeAttempted, taking, disposed;
            internal CanonicalPayloadParent(LocalGuiWriterRequest owner, FrontierRelayReports reports,
                Stopwatch work, string endpoint, byte[] binding)
            {
                original = this; this.owner = owner; this.reports = reports; this.work = work;
                this.endpoint = endpoint; this.binding = (byte[])binding.Clone(); attempt = ReadAttempt(owner.view);
                closedWatch = ThreadPool.RegisterWaitForSingleObject(owner.closed, (s,t) =>
                { lock (sync) { if (!taking && custody == null) Dispose(); } }, null, Timeout.Infinite, true);
                exitWatch = ThreadPool.RegisterWaitForSingleObject(owner.workerExit, (s,t) =>
                { lock (sync) { if (custody == null && (!WrapperSucceeded() || channel == null || !channel.Current || channel.State != 3)) Dispose(); } },
                    null, Timeout.Infinite, true);
            }
            private bool WrapperSucceeded()
            { try { int code; return owner.workerExit.WaitOne(0) && GetExitCodeProcess(owner.workerExit.SafeWaitHandle, out code) && code == 0; } catch { return false; } }
            public bool TryPullIfOffered()
            {
                lock (owner.frontierRelaySync) lock (sync)
                {
                    if (!Object.ReferenceEquals(original, this) || disposed || pullAttempted || !LocalPayloadParentChannel.Offered(endpoint)) return false;
                    pullAttempted = true; bool success = false;
                    if (work.IsRunning) { Dispose(); return false; }
                    work.Start();
                    try
                    {
                        if (work.ElapsedMilliseconds > 5000 || !reports.Registration.IsCurrent ||
                            !CanonicalPayloadWrapper.Equal(attempt, ReadAttempt(owner.view))) return false;
                        channel = LocalPayloadParentChannel.Open(endpoint, binding);
                        if (channel == null || !Pull(channel.Packet) || !FilesCurrent() ||
                            !reports.Registration.IsCurrent || work.ElapsedMilliseconds > 5000) return false;
                        cancelWatch = ThreadPool.RegisterWaitForSingleObject(channel.Cancelled, (s,t) => Dispose(), null, Timeout.Infinite, true);
                        success = channel.MarkPulled(); return success;
                    }
                    catch { return false; }
                    finally { work.Stop(); if (!success) Dispose(); }
                }
            }
            private RegisteredWaitHandle cancelWatch;
            private bool Pull(byte[] packet)
            {
                using (var stream = new MemoryStream(packet, false))
                using (var reader = new BinaryReader(stream, new UTF8Encoding(false, true)))
                {
                    gui = new Guid(reader.ReadBytes(16)); int length = reader.ReadInt32();
                    if (gui == Guid.Empty || length != attempt.Length || !CanonicalPayloadWrapper.Equal(attempt, reader.ReadBytes(length))) return false;
                    completionHash = reader.ReadBytes(32);
                    if (completionHash.Length != 32 || !TextValid(reader.ReadString(), 1024) || !TextValid(reader.ReadString(), 1024)) return false;
                    string hash = null;
                    for (int i = 0; i < 2; i++)
                    {
                        requests[i] = new Guid(reader.ReadBytes(16)); processes[i] = reader.ReadInt32(); creations[i] = reader.ReadInt64();
                        writers[i] = reader.ReadString(); sequences[i] = reader.ReadInt64();
                        long handle = reader.ReadInt64(); identities[i] = reader.ReadString(); long count = reader.ReadInt64(); string digest = reader.ReadString();
                        if (requests[i] == Guid.Empty || processes[i] <= 0 || creations[i] <= 0 || !TextValid(writers[i], 1024) ||
                            sequences[i] <= 0 || handle <= 0 || count != 268435456 ||
                            !System.Text.RegularExpressions.Regex.IsMatch(digest, @"\A[0-9a-f]{64}\z") || (hash != null && hash != digest)) return false;
                        hash = digest;
                        if (!CanonicalAttemptDestination.DuplicateHandle(owner.workerExit.SafeWaitHandle, new IntPtr(handle),
                            CanonicalAttemptDestination.GetCurrentProcess(), out files[i], 0x80000000, false, 0)) return false;
                    }
                    return stream.Position == stream.Length && requests[0] != requests[1] && processes[0] != processes[1] &&
                        writers[0] != writers[1] && identities[0] != identities[1];
                }
            }
            private bool FilesCurrent() { return CanonicalPayloadWrapper.PayloadFilesMatch(files, identities); }
            public bool ProvisionalCustody
            { get { lock (sync) return Object.ReferenceEquals(original, this) && !disposed && custody == null && channel != null && channel.Current && FilesCurrent(); } }
            partial void BeginPayloadTakeDiagnostics();
            partial void ObservePayloadTake(string reason);
            partial void ObserveNativePayloadTake(FrontierRelayCustody native);
            partial void ObserveParentPayloadMatch(FrontierRelayCustody native);
            private RelayedCompletion RefuseTake(string reason)
            { ObservePayloadTake(reason); return null; }
            public RelayedCompletion TryTakeAfterExit(FrontierRelayReports candidate)
            {
                RelayedCompletion result; bool completionAttempted;
                return TryTakeAfterExitCore(candidate, false, out result, out completionAttempted);
            }
            internal RelayedCompletion TryTakeAfterExitCore(FrontierRelayReports candidate, bool preserveNative,
                out RelayedCompletion result, out bool completionAttempted)
            {
                result = null; completionAttempted = false;
                lock (owner.frontierRelaySync)
                {
                    BeginPayloadTakeDiagnostics();
                    if (!Object.ReferenceEquals(original, this)) return RefuseTake("foreign-instance");
                    if (!Object.ReferenceEquals(reports, candidate)) return RefuseTake("foreign-reports");
                    if (disposed) return RefuseTake("disposed-before-take");
                    if (takeAttempted) return RefuseTake("already-attempted");
                    if (!owner.workerExit.WaitOne(0)) return RefuseTake("worker-running");
                    takeAttempted = true; bool success = false; FrontierRelayCustody native = null;
                    try
                    {
                        if (!WrapperSucceeded()) return RefuseTake("wrapper-exit");
                        if (reports.Stdout.Status != TaskStatus.RanToCompletion) return RefuseTake("stdout-incomplete");
                        if (reports.Stderr.Status != TaskStatus.RanToCompletion) return RefuseTake("stderr-incomplete");
                        if (channel == null) return RefuseTake("channel-missing");
                        if (!channel.Current) return RefuseTake("channel-not-current-before-native");
                        if (channel.State != 3) return RefuseTake("channel-not-committed");
                        if (!FilesCurrent()) return RefuseTake("files-before-native");
                        if (work.IsRunning) return RefuseTake("work-running");
                        if (work.ElapsedMilliseconds > 5000) return RefuseTake("work-before-native");
                        lock (sync) { if (disposed) return RefuseTake("disposed-before-native"); taking = true; }
                        completionAttempted = true;
                        result = owner.TakeRelayAfterExit(); native = owner.frontierRelayCustody;
                        ObserveNativePayloadTake(native);
                        work.Start();
                        try
                        {
                            if (result == null) return RefuseTake("native-relay-result");
                            if (native == null) return RefuseTake("native-relay-custody");
                            bool matchesParent = native.MatchesParentPayload(owner, result, gui, completionHash,
                                requests, processes, creations, writers, sequences);
                            ObserveParentPayloadMatch(native);
                            if (!matchesParent) return RefuseTake("native-parent-match");
                            if (!FilesCurrent()) return RefuseTake("files-after-native");
                            if (!channel.Current) return RefuseTake("channel-not-current-after-native");
                            if (work.ElapsedMilliseconds > 5000) return RefuseTake("work-after-native");
                            // The production caller retains the original one-use export.
                            // The strict component API still detaches custody here.
                            if (!preserveNative && !Object.ReferenceEquals(native, result.TryTakeFrontierCustody())) return RefuseTake("native-custody-take");
                            lock (sync)
                            {
                                if (disposed) return RefuseTake("disposed-before-bind");
                                custody = native; completion = result; writerLifetime = native.PayloadLifetime;
                                native.payloadParent = this;
                                writerWatch = ThreadPool.RegisterWaitForSingleObject(writerLifetime.WaitHandle, (s,t) => Dispose(), null, Timeout.Infinite, true);
                                if (disposed) return RefuseTake("disposed-after-bind");
                                if (writerLifetime.IsCancellationRequested) return RefuseTake("writer-lifetime-cancelled");
                                if (!FilesCurrent()) return RefuseTake("files-after-bind");
                                if (work.ElapsedMilliseconds > 5000) return RefuseTake("work-after-bind");
                                success = true; ObservePayloadTake("accepted");
                                return result;
                            }
                        }
                        finally { work.Stop(); }
                    }
                    finally
                    {
                        lock (sync) taking = false;
                        if (!success)
                        {
                            Dispose();
                            if (native != null && !preserveNative) native.Dispose();
                        }
                    }
                }
            }
            public FrontierRelayCustody WriterCustody(RelayedCompletion result)
            { return Object.ReferenceEquals(original, this) && Object.ReferenceEquals(completion, result) && IsCurrent ? custody : null; }
            public bool IsCurrent
            {
                get
                {
                    if (!Object.ReferenceEquals(original, this) || custody == null || !custody.IsCurrent) return false;
                    lock (sync)
                    {
                        if (!disposed && !writerLifetime.IsCancellationRequested && FilesCurrent()) return true;
                        Dispose(); return false;
                    }
                }
            }
            public bool LiveProofEligible { get { return false; } }
            public bool TransferIntegrityEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            public void Dispose()
            {
                if (!Object.ReferenceEquals(original, this)) return;
                lock (sync)
                {
                    if (disposed) return; disposed = true;
                    if (closedWatch != null) closedWatch.Unregister(null); if (exitWatch != null) exitWatch.Unregister(null);
                    if (cancelWatch != null) cancelWatch.Unregister(null);
                    if (writerWatch != null) writerWatch.Unregister(null);
                    foreach (var file in files) if (file != null) file.Dispose();
                    if (channel != null) channel.Dispose();
                }
            }
            internal static byte[] CompletionHash(Completion value)
            { using (var hash = SHA256.Create()) return hash.ComputeHash(EncodeRelayCompletion(value)); }
            internal static bool SameCompletion(Completion value, byte[] hash)
            { return CanonicalPayloadWrapper.Equal(CompletionHash(value), hash); }
        }
    }

}
