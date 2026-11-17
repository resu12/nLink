using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        private FrontierDiagnosticReceiver frontierDiagnosticReceiver;
        public sealed partial class FrontierPrimaryInputs
        {
            private bool diagnosticReceiverAttempted;
            public FrontierDiagnosticReceiver TryPrepareDiagnosticReceiver()
            {
                lock (owner.frontierInputSync) lock (sync)
                {
                    if (!Object.ReferenceEquals(owner.frontierInputs, this)) return null;
                    LocalGuiWriterRequest.FrontierParentFilesPending.CommitPins escrow = null;
                    LocalFrontierPrimaryScope retainedScope = null; FrontierDiagnosticReceiver result = null;
                    bool success = false; work.Start();
                    try
                    {
                        if (diagnosticReceiverAttempted) { if (owner.frontierDiagnosticReceiver != null) owner.frontierDiagnosticReceiver.Close(); return null; }
                        diagnosticReceiverAttempted = true;
                        if (!IsCurrent || workerScope == null || workerChannel == null || workerChannel.Facts == null) return null;
                        escrow = pins.DuplicateForPrimary(() => IsCurrent);
                        if (escrow == null) return null;
                        retainedScope = new LocalFrontierPrimaryScope(owner.map, owner.frontierBody);
                        result = new FrontierDiagnosticReceiver(owner, this, facts, escrow, retainedScope, workerChannel);
                        escrow = null; retainedScope = null;
                        if (!IsCurrent || !result.Check(false)) return null;
                        owner.frontierDiagnosticReceiver = result; result.Watch(workerChannel);
                        if (!result.Check(true)) return null;
                        success = true; return result;
                    }
                    catch { return null; }
                    finally
                    {
                        work.Stop();
                        if (!success) { if (result != null) result.Close(); if (escrow != null) escrow.Dispose(); if (retainedScope != null) retainedScope.Dispose(); }
                    }
                }
            }
        }

        public sealed partial class FrontierDiagnosticReceiver : IDisposable
        {
            private readonly LocalRetainedSourceRequest owner;
            private readonly FrontierPrimaryInputs originalInputs;
            private readonly LocalFrontierParentFacts facts;
            private readonly LocalGuiWriterRequest.FrontierParentFilesPending.CommitPins pins;
            private readonly LocalFrontierPrimaryScope scope;
            private readonly LocalFrontierDiagnosticChannel channel;
            private readonly string scopeHash, offerHash, factsHash, sourceHash;
            private readonly object sync = new object();
            private readonly Stopwatch work = new Stopwatch();
            private readonly List<RegisteredWaitHandle> watches = new List<RegisteredWaitHandle>();
            private bool closed, adoptionAttempted, adopted, takeAttempted;
            private LocalFrontierDiagnosticPacket packet;
            private FrontierDiagnosticTransport transport;
            internal FrontierDiagnosticReceiver(LocalRetainedSourceRequest owner, FrontierPrimaryInputs originalInputs,
                LocalFrontierParentFacts facts, LocalGuiWriterRequest.FrontierParentFilesPending.CommitPins pins,
                LocalFrontierPrimaryScope scope, LocalFrontierParentFileChannel files)
            {
                this.owner = owner; this.originalInputs = originalInputs; this.facts = facts; this.pins = pins; this.scope = scope;
                scopeHash = LocalFrontierDiagnosticPacket.Hex(scope.Binding); offerHash = LocalFrontierDiagnosticPacket.Hex(files.OfferHash);
                factsHash = LocalFrontierDiagnosticPacket.Hex(files.Facts.ReceiptHash); sourceHash = owner.frontierWriter.Outputs[3].Sha256;
                channel = LocalFrontierDiagnosticChannel.Open(files.Endpoint,
                    LocalFrontierDiagnosticChannel.Binding(scope.Binding, files.OfferHash, files.Facts.ReceiptHash), true);
            }
            private bool Original { get { return Object.ReferenceEquals(owner.frontierOriginal, owner) && Object.ReferenceEquals(owner.frontierDiagnosticReceiver, this); } }
            internal bool Check(bool live)
            {
                try
                {
                    return !closed && work.ElapsedMilliseconds <= 5000 && Volatile.Read(ref owner.disposed) == 0 &&
                        !owner.closed.WaitOne(0) && channel != null && channel.IsOpen && scope.IsCurrent && pins.IsCurrent &&
                        owner.frozenSource != null && owner.frozenSource.CanRead && !owner.frozenSource.CanWrite && pins.MatchesPrimarySource(owner.FrontierSourceHandle) &&
                        (!live || (owner.workerExit != null && !owner.workerExit.WaitOne(0) && originalInputs.IsCurrent));
                }
                catch { return false; }
            }
            private void Require(bool live) { if (!Check(live)) throw new InvalidOperationException("frontier_diagnostic_parent_custody"); }
            internal void Watch(LocalFrontierParentFileChannel files)
            {
                lock (sync)
                {
                    if (closed) return;
                    watches.Add(ThreadPool.RegisterWaitForSingleObject(owner.closed, (s,t) => Close(), null, Timeout.Infinite, true));
                    watches.Add(ThreadPool.RegisterWaitForSingleObject(channel.Cancelled, (s,t) => Close(), null, Timeout.Infinite, true));
                    foreach (var signal in new[] { files.Cancelled, files.Facts.Cancelled })
                        watches.Add(ThreadPool.RegisterWaitForSingleObject(signal, (s,t) => InputsClosed(), null, Timeout.Infinite, true));
                    watches.Add(ThreadPool.RegisterWaitForSingleObject(owner.workerExit, (s,t) => WorkerExited(), null, Timeout.Infinite, true));
                }
            }
            private void InputsClosed() { lock (sync) { if (transport == null && (!adopted || channel.State != 4)) Close(); } }
            private bool ExitedSuccessfully()
            { int code; try { return owner.workerExit != null && owner.workerExit.WaitOne(0) && GetExitCodeProcess(owner.workerExit.SafeWaitHandle, out code) && code == 0; } catch { return false; } }
            private void WorkerExited() { lock (sync) { if (!ExitedSuccessfully() || (!adopted || (transport == null && channel.State != 4))) Close(); } }
            public bool TryAdopt()
            {
                lock (owner.frontierInputSync) lock (sync)
                {
                    if (!Original) return false;
                    bool success = false; work.Start();
                    try
                    {
                        if (adoptionAttempted) return false; adoptionAttempted = true;
                        Require(true);
                        success = channel.Adopt(bytes =>
                        {
                            packet = LocalFrontierDiagnosticPacket.Decode(bytes, owner.RequestId, owner.workerId, owner.workerCreation,
                                scopeHash, offerHash, factsHash, sourceHash, facts, pins, () => Require(true));
                            return packet != null;
                        }, () => Require(true));
                        adopted = success; return success;
                    }
                    catch { return false; }
                    finally { work.Stop(); if (!success) Close(); }
                }
            }
            // Transport only. This does not approve terminal reports or expose
            // the legacy publication-completion issuer.
            public FrontierDiagnosticTransport TakeAfterExit()
            {
                lock (owner.frontierInputSync) lock (sync)
                {
                    if (!Original) return null;
                    bool success = false; work.Start();
                    try
                    {
                        if (takeAttempted) return null; takeAttempted = true;
                        if (!adopted || !ExitedSuccessfully()) return null;
                        Require(false); byte[] bytes = channel.Take(() => Require(false));
                        if (bytes == null || packet == null) return null;
                        transport = new FrontierDiagnosticTransport(this, packet, bytes); success = true; return transport;
                    }
                    catch { return null; }
                    finally { work.Stop(); if (!success) Close(); }
                }
            }
            public bool IsCurrent
            {
                get
                {
                    lock (owner.frontierInputSync) lock (sync)
                    {
                        if (!Original) return false;
                        work.Start(); try { if (Check(false)) return true; Close(); return false; } finally { work.Stop(); }
                    }
                }
            }
            public bool LiveProofEligible { get { return false; } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            internal byte[] CopyTransport(FrontierDiagnosticTransport value)
            {
                lock (owner.frontierInputSync) lock (sync)
                {
                    if (!Original || !Object.ReferenceEquals(transport, value)) return null;
                    byte[] copy = null; bool success = false; work.Start();
                    try { Require(false); copy = value.CopyBytes(); Require(false); success = true; return copy; }
                    catch { return null; }
                    finally { work.Stop(); if (!success) { if (copy != null) Array.Clear(copy, 0, copy.Length); Close(); } }
                }
            }
            internal bool TransportCurrent(FrontierDiagnosticTransport value)
            { lock (owner.frontierInputSync) lock (sync) { return Original && Object.ReferenceEquals(transport, value) && IsCurrent; } }
            internal bool OwnsTransport(FrontierDiagnosticTransport value)
            { lock (owner.frontierInputSync) lock (sync) { return Original && value != null && Object.ReferenceEquals(transport, value); } }
            internal void DisposeTransport(FrontierDiagnosticTransport value)
            { lock (owner.frontierInputSync) lock (sync) { if (Original && Object.ReferenceEquals(transport, value)) Close(); } }
            internal void Close()
            {
                lock (sync)
                {
                    if (closed) return; closed = true;
                    foreach (var watch in watches) watch.Unregister(null);
                    if (transport != null) transport.Clear(); packet = null;
                    if (channel != null) { channel.Cancel(); channel.Dispose(); }
                    pins.Dispose(); scope.Dispose(); work.Stop();
                }
            }
            public void Dispose() { lock (owner.frontierInputSync) lock (sync) { if (Original) Close(); } }
        }

        public sealed class FrontierDiagnosticTransport : IDisposable
        {
            private readonly FrontierDiagnosticReceiver owner;
            private byte[] bytes;
            internal readonly string[] TerminalCatalog;
            public Guid FullInvocationId { get; private set; }
            public Guid CompactInvocationId { get; private set; }
            public Guid ObservationId { get; private set; }
            public int WitnessCount { get; private set; }
            public string DiagnosticSnapshot { get; private set; }
            internal FrontierDiagnosticTransport(FrontierDiagnosticReceiver owner, LocalFrontierDiagnosticPacket packet, byte[] bytes)
            { this.owner = owner; this.bytes = bytes; TerminalCatalog = (string[])packet.TerminalCatalog.Clone(); FullInvocationId = packet.Full; CompactInvocationId = packet.Compact; ObservationId = packet.Observation; WitnessCount = packet.WitnessCount; DiagnosticSnapshot = packet.Snapshot; }
            public bool IsCurrent { get { return owner.TransportCurrent(this); } }
            public bool LiveProofEligible { get { return false; } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            public byte[] CopyPayload() { return owner.CopyTransport(this); }
            internal byte[] CopyBytes() { return (byte[])bytes.Clone(); }
            internal void Clear() { if (bytes != null) Array.Clear(bytes, 0, bytes.Length); bytes = null; DiagnosticSnapshot = null; }
            public void Dispose() { owner.DisposeTransport(this); }
        }
    }
}
