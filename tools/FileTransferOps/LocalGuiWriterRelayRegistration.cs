using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        private readonly object frontierRelaySync = new object();
        private bool frontierRelayAttempted;
        private FrontierRelayRegistration frontierRelayRegistration;

        // Pre-forward registration only. No files, completion record or primary
        // authority cross this boundary. The caller already owns its exact child.
        public FrontierRelayRegistration TryRegisterFrontierRelay()
        {
            if (!Object.ReferenceEquals(original, this)) return null;
            lock (frontierRelaySync)
            {
                if (frontierRelayAttempted) return null;
                bool held = false, success = false;
                try
                {
                    if (!relayMode || Volatile.Read(ref sealedState) != 0 || workerExit == null || workerExit.WaitOne(0) ||
                        workerId == GetCurrentProcessId() || GetFrontierProcessId(workerExit.SafeWaitHandle) != workerId) return null;
                    long creation, exit, kernel, user;
                    if (!GetProcessTimes(workerExit.SafeWaitHandle, out creation, out exit, out kernel, out user) || creation != workerCreation) return null;
                    frontierRelayAttempted = true;
                    var clock = Stopwatch.StartNew();
                    held = gate.WaitOne(0); if (!held || closed.WaitOne(0)) return null;
                    byte[] scope;
                    using (var current = Process.GetCurrentProcess())
                        scope = CreateRelayScope(RequestId, run, scenario, root, current.Id,
                            current.StartTime.ToUniversalTime().ToFileTimeUtc(), workerId, workerCreation);
                    if (!RelayScopeMatches(view, scope, 0)) return null;
                    var channel = FrontierRelayRegistrationChannel.Create(Name, scope);
                    if (channel == null) return null;
                    frontierRelayRegistration = new FrontierRelayRegistration(this, scope, channel);
                    success = clock.ElapsedMilliseconds <= MaximumCompletionMilliseconds && frontierRelayRegistration.IsCurrent;
                    return success ? frontierRelayRegistration : null;
                }
                catch (AbandonedMutexException) { held = true; return null; }
                catch { return null; }
                finally { Release(gate, held); if (!success) RetireFrontierRelayRegistration(); }
            }
        }

        private void RetireFrontierRelayRegistration()
        { lock (frontierRelaySync) { if (frontierRelayRegistration != null) frontierRelayRegistration.Close(); } }

        private static byte[] CreateRelayScope(Guid id, string run, string scenario, string root,
            int parent, long parentCreation, int wrapper, long wrapperCreation)
        {
            using (var bytes = new MemoryStream())
            using (var writer = new BinaryWriter(bytes, new UTF8Encoding(false, true)))
            {
                writer.Write(run); writer.Write(scenario); writer.Write(root); writer.Flush();
                var body = bytes.ToArray(); if (body.Length > RequestBytes) throw new InvalidOperationException();
                var scope = new byte[HeaderBytes + body.Length];
                Array.Copy(BitConverter.GetBytes(RelayMagic), 0, scope, 0, 4);
                Array.Copy(BitConverter.GetBytes(parent), 0, scope, 4, 4);
                Array.Copy(BitConverter.GetBytes(parentCreation), 0, scope, 8, 8);
                Array.Copy(BitConverter.GetBytes(wrapper), 0, scope, 16, 4);
                Array.Copy(BitConverter.GetBytes(body.Length), 0, scope, 20, 4);
                Array.Copy(id.ToByteArray(), 0, scope, 24, 16);
                Array.Copy(BitConverter.GetBytes(wrapperCreation), 0, scope, 40, 8);
                Array.Copy(body, 0, scope, HeaderBytes, body.Length); return scope;
            }
        }

        private static bool RelayScopeMatches(MemoryMappedViewAccessor view, byte[] scope, int minimumState)
        {
            if (scope == null) return false;
            int state = view.ReadInt32(48);
            if (state < minimumState || state > 1 || view.ReadInt32(52) != 0) return false;
            for (int i = 0; i < scope.Length; i++)
                if ((i < 48 || i >= 52) && view.ReadByte(i) != scope[i]) return false;
            return true;
        }

        public sealed partial class FrontierRelayRegistration : IDisposable
        {
            private readonly LocalGuiWriterRequest owner;
            private readonly byte[] scope;
            private readonly FrontierRelayRegistrationChannel channel;
            private int lastLegacyState;
            private bool closed;
            internal FrontierRelayRegistration(LocalGuiWriterRequest owner, byte[] scope, FrontierRelayRegistrationChannel channel)
            { this.owner = owner; this.scope = scope; this.channel = channel; lastLegacyState = owner.view.ReadInt32(48); }
            public bool IsCurrent
            {
                get
                {
                    lock (owner.frontierRelaySync)
                    {
                        if (!Object.ReferenceEquals(owner.frontierRelayRegistration, this)) return false;
                        try
                        {
                            if (!closed && Volatile.Read(ref owner.sealedState) == 0 && !owner.closed.WaitOne(0) &&
                                !owner.workerExit.WaitOne(0) && GetCurrentProcessId() == BitConverter.ToInt32(scope, 4) &&
                                channel.IsCurrent && RelayScopeMatches(owner.view, scope, channel.Attached ? 1 : lastLegacyState))
                            { lastLegacyState = owner.view.ReadInt32(48); return true; }
                        }
                        catch { }
                        Close(); return false;
                    }
                }
            }
            public bool WrapperAttached { get { lock (owner.frontierRelaySync) { return IsCurrent && channel.Attached; } } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool LiveProofEligible { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            internal void Close() { if (!closed) { closed = true; channel.Dispose(); } }
            public void Dispose()
            { lock (owner.frontierRelaySync) { if (Object.ReferenceEquals(owner.frontierRelayRegistration, this)) { owner.RetireFrontierRelayCustody(); Close(); } } }
        }

        public sealed partial class Relay
        {
            internal readonly object frontierRelaySync = new object();
            private bool frontierAttachAttempted;
            internal FrontierRelayHandoff frontierRelayHandoff;

            // Only the original, already attached Wrapper relay can join. No
            // caller-authored endpoint, process identity or scope is accepted.
            public FrontierRelayHandoff TryAttachFrontier()
            {
                if (!Object.ReferenceEquals(original, this)) return null;
                lock (frontierRelaySync)
                {
                    if (frontierAttachAttempted) return null;
                    bool held = false, success = false, attached = false;
                    FrontierRelayRegistrationChannel channel = null;
                    try
                    {
                        if (Volatile.Read(ref attempted) != 0 || !owner.relayMode || !owner.IsCurrent) return null;
                        frontierAttachAttempted = true;
                        var clock = Stopwatch.StartNew();
                        held = owner.gate.WaitOne(0); if (!held) return null;
                        long creation, exit, kernel, user;
                        if (!GetProcessTimes(owner.parentExit.SafeWaitHandle, out creation, out exit, out kernel, out user)) return null;
                        byte[] scope;
                        using (var current = Process.GetCurrentProcess())
                            scope = CreateRelayScope(owner.id, owner.run, owner.scenario, owner.root,
                                GetFrontierProcessId(owner.parentExit.SafeWaitHandle), creation, current.Id,
                                current.StartTime.ToUniversalTime().ToFileTimeUtc());
                        if (!RelayScopeMatches(owner.view, scope, 1)) return null;
                        channel = FrontierRelayRegistrationChannel.Open(owner.endpointName, scope);
                        if (channel == null || !owner.IsCurrent || clock.ElapsedMilliseconds > MaximumCompletionMilliseconds ||
                            !RelayScopeMatches(owner.view, scope, 1)) return null;
                        attached = true;
                        if (!channel.MarkAttached()) return null;
                        frontierRelayHandoff = new FrontierRelayHandoff(this, scope, channel);
                        success = frontierRelayHandoff.IsCurrent && clock.ElapsedMilliseconds <= MaximumCompletionMilliseconds;
                        return success ? frontierRelayHandoff : null;
                    }
                    catch (AbandonedMutexException) { held = true; return null; }
                    catch { return null; }
                    finally
                    {
                        Release(owner.gate, held);
                        if (!success && channel != null) { if (attached) channel.Dispose(); else channel.Close(); }
                    }
                }
            }
        }

        public sealed partial class FrontierRelayHandoff : IDisposable
        {
            private readonly Relay owner;
            private readonly byte[] scope;
            internal readonly FrontierRelayRegistrationChannel channel;
            private bool closed;
            internal FrontierRelayHandoff(Relay owner, byte[] scope, FrontierRelayRegistrationChannel channel)
            { this.owner = owner; this.scope = scope; this.channel = channel; }
            public bool IsCurrent
            {
                get
                {
                    lock (owner.frontierRelaySync)
                    {
                        if (!Object.ReferenceEquals(owner.frontierRelayHandoff, this)) return false;
                        try
                        {
                            if (!closed && owner.owner.IsCurrent && Volatile.Read(ref owner.attempted) == 0 &&
                                GetCurrentProcessId() == BitConverter.ToInt32(scope, 16) &&
                                RelayScopeMatches(owner.owner.view, scope, 1) && channel.IsCurrent && channel.Attached) return true;
                        }
                        catch { }
                        Close(); return false;
                    }
                }
            }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool LiveProofEligible { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            internal void Close() { if (!closed) { closed = true; CloseTransfer(); channel.Dispose(); } }
            public void Dispose()
            { lock (owner.frontierRelaySync) { if (Object.ReferenceEquals(owner.frontierRelayHandoff, this)) Close(); } }
        }

        internal sealed class FrontierRelayRegistrationChannel : IDisposable
        {
            private const int PrefixBytes = 32, Bytes = PrefixBytes + HeaderBytes + RequestBytes;
            private MemoryMappedFile map;
            private MemoryMappedViewAccessor view;
            private EventWaitHandle cancelled;
            private byte[] snapshot;
            private int lastState;
            private bool closed;
            internal string Endpoint;
            internal byte[] Binding { get { using (var hash = SHA256.Create()) return hash.ComputeHash(snapshot); } }
            internal WaitHandle Cancelled { get { return cancelled; } }
            private FrontierRelayRegistrationChannel() { }
            internal static FrontierRelayRegistrationChannel Create(string name, byte[] scope) { return OpenCore(name, scope, true); }
            internal static FrontierRelayRegistrationChannel Open(string name, byte[] scope) { return OpenCore(name, scope, false); }
            private static FrontierRelayRegistrationChannel OpenCore(string name, byte[] scope, bool create)
            {
                var value = new FrontierRelayRegistrationChannel();
                try
                {
                    name += ".frontier-relay-v1";
                    value.Endpoint = name;
                    value.map = create ? MemoryMappedFile.CreateNew(name, Bytes) : MemoryMappedFile.OpenExisting(name);
                    value.view = value.map.CreateViewAccessor(0, Bytes);
                    if (create)
                    {
                        bool created;
                        value.cancelled = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".cancelled", out created);
                        if (!created) return null;
                        value.snapshot = new byte[Bytes];
                        Array.Copy(BitConverter.GetBytes(0x4e525231), 0, value.snapshot, 0, 4);
                        Array.Copy(BitConverter.GetBytes(1), 0, value.snapshot, 4, 4);
                        Array.Copy(BitConverter.GetBytes(scope.Length), 0, value.snapshot, 12, 4);
                        Array.Copy(Guid.NewGuid().ToByteArray(), 0, value.snapshot, 16, 16);
                        Array.Copy(scope, 0, value.snapshot, PrefixBytes, scope.Length);
                        value.view.WriteArray(0, value.snapshot, 0, Bytes);
                    }
                    else
                    {
                        value.cancelled = EventWaitHandle.OpenExisting(name + ".cancelled");
                        value.snapshot = new byte[Bytes]; value.view.ReadArray(0, value.snapshot, 0, Bytes);
                        var nonce = new byte[16]; Array.Copy(value.snapshot, 16, nonce, 0, 16);
                        if (BitConverter.ToInt32(value.snapshot, 0) != 0x4e525231 || BitConverter.ToInt32(value.snapshot, 4) != 1 ||
                            BitConverter.ToInt32(value.snapshot, 8) != 0 || BitConverter.ToInt32(value.snapshot, 12) != scope.Length || new Guid(nonce) == Guid.Empty) return null;
                        for (int i = PrefixBytes; i < Bytes; i++)
                            if (value.snapshot[i] != (i - PrefixBytes < scope.Length ? scope[i - PrefixBytes] : 0)) return null;
                    }
                    if (!value.IsCurrent) return null;
                    var result = value; value = null; return result;
                }
                catch { return null; }
                // Failed create/open must not signal a colliding or foreign object.
                finally { if (value != null) value.Close(); }
            }
            internal bool Attached { get { return view.ReadInt32(8) == 1; } }
            internal bool IsCurrent
            {
                get
                {
                    try
                    {
                        if (closed || cancelled.WaitOne(0)) return false;
                        int state = view.ReadInt32(8); if (state < lastState || state > 1) return false;
                        lastState = state;
                        for (int i = 0; i < Bytes; i++) if ((i < 8 || i >= 12) && view.ReadByte(i) != snapshot[i]) return false;
                        return true;
                    }
                    catch { return false; }
                }
            }
            internal bool MarkAttached()
            { if (!IsCurrent || Attached) return false; view.Write(8, 1); lastState = 1; return IsCurrent; }
            internal void Close()
            {
                if (closed) return; closed = true;
                if (view != null) view.Dispose(); if (map != null) map.Dispose(); if (cancelled != null) cancelled.Dispose();
            }
            public void Dispose() { try { if (!closed && cancelled != null) cancelled.Set(); } catch { } Close(); }
        }
    }
}
