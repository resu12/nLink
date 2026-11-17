using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        private readonly object frontierParentSync = new object();
        private bool frontierParentAttempted;
        private FrontierParentRegistration frontierParentRegistration;

        // Registration only. No manifest, file handle, verdict or acknowledgment
        // is imported here. Provisional native pull is a separate operation.
        public FrontierParentRegistration TryRegisterFrontierParent()
        {
            if (!Object.ReferenceEquals(original, this)) return null;
            lock (frontierParentSync)
            {
                bool success = false;
                try
                {
                    if (frontierParentAttempted) return null; frontierParentAttempted = true;
                    if (relayMode || Volatile.Read(ref sealedState) != 0 || workerExit == null || workerExit.WaitOne(0) ||
                        GetFrontierProcessId(workerExit.SafeWaitHandle) != workerId || workerId == GetCurrentProcessId()) return null;
                    long creation, exit, kernel, user;
                    if (!GetProcessTimes(workerExit.SafeWaitHandle, out creation, out exit, out kernel, out user) || creation != workerCreation) return null;
                    byte[] body;
                    using (var bytes = new MemoryStream())
                    using (var writer = new BinaryWriter(bytes, new UTF8Encoding(false, true)))
                    { writer.Write(run); writer.Write(scenario); writer.Write(root); writer.Flush(); body = bytes.ToArray(); }
                    if (body.Length > RequestBytes) return null;
                    using (var current = System.Diagnostics.Process.GetCurrentProcess())
                        frontierParentRegistration = new FrontierParentRegistration(this, body, current.Id,
                            current.StartTime.ToUniversalTime().ToFileTimeUtc(), workerId, workerCreation);
                    if (!ParentScopeCurrent(frontierParentRegistration)) return null;
                    frontierParentRegistration.Channel = ParentRegistrationChannel.Create(Name, RequestId, frontierParentRegistration);
                    success = frontierParentRegistration.IsCurrent;
                    return success ? frontierParentRegistration : null;
                }
                catch { return null; }
                finally { if (!success) RetireFrontierParentRegistration(); }
            }
        }

        private bool ParentScopeCurrent(FrontierParentRegistration value)
        {
            try
            {
                if (!Object.ReferenceEquals(original, this) || relayMode || Volatile.Read(ref sealedState) != 0 ||
                    !Object.ReferenceEquals(frontierParentRegistration, value) || closed.WaitOne(0) || workerExit.WaitOne(0) ||
                    GetCurrentProcessId() != value.ParentId || GetFrontierProcessId(workerExit.SafeWaitHandle) != value.GuiId ||
                    view.ReadInt32(0) != Magic || ReadId(view) != RequestId || view.ReadInt32(4) != value.ParentId ||
                    view.ReadInt64(8) != value.ParentCreation || view.ReadInt32(16) != value.GuiId ||
                    view.ReadInt64(40) != value.GuiCreation || view.ReadInt32(20) != value.Body.Length ||
                    (view.ReadInt32(48) != 0 && view.ReadInt32(48) != 1) || view.ReadInt32(52) != 0 || view.ReadInt64(56) != 0) return false;
                for (int i = 0; i < value.Body.Length; i++) if (view.ReadByte(HeaderBytes + i) != value.Body[i]) return false;
                return true;
            }
            catch { return false; }
        }
        private void RetireFrontierParentRegistration()
        {
            lock (frontierParentSync)
            {
                if (frontierParentRegistration == null) return;
                if (frontierParentRegistration.PendingFiles != null) frontierParentRegistration.PendingFiles.Close();
                if (frontierParentRegistration.Channel != null) frontierParentRegistration.Channel.Dispose();
            }
        }

        public sealed partial class FrontierParentRegistration : IDisposable
        {
            private readonly LocalGuiWriterRequest owner;
            internal readonly byte[] Body;
            internal readonly int ParentId, GuiId;
            internal readonly long ParentCreation, GuiCreation;
            internal ParentRegistrationChannel Channel;
            internal FrontierParentRegistration(LocalGuiWriterRequest owner, byte[] body, int parentId, long parentCreation, int guiId, long guiCreation)
            { this.owner = owner; Body = body; ParentId = parentId; ParentCreation = parentCreation; GuiId = guiId; GuiCreation = guiCreation; }
            public bool IsCurrent
            {
                get
                {
                    lock (owner.frontierParentSync)
                    {
                        if (!Object.ReferenceEquals(owner.frontierParentRegistration, this)) return false;
                        if (owner.ParentScopeCurrent(this) && Channel != null && Channel.IsCurrent) return true;
                        owner.RetireFrontierParentRegistration(); return false;
                    }
                }
            }
            // Peer attachment is not parent adoption or a finalizer verdict.
            public bool GuiAttached { get { lock (owner.frontierParentSync) { return IsCurrent && Channel.State == 1; } } }
            public bool LiveProofEligible { get { return false; } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            public void Dispose()
            { lock (owner.frontierParentSync) { if (Object.ReferenceEquals(owner.frontierParentRegistration, this)) owner.RetireFrontierParentRegistration(); } }
        }

        public sealed partial class FrontierLaunchOwner
        {
            private ParentRegistrationChannel parentRegistration;
            internal bool ParentRegistrationCurrent()
            { return parentRegistration == null || parentRegistration.IsCurrent; }
            internal ParentRegistrationChannel AttachParentRegistration()
            {
                lock (sync)
                {
                    if (!IsCurrent || parentRegistration != null || fileAdoption == null) return null;
                    // The callback holds only the adopter lock; scope checks
                    // must not re-enter GUI/finalizer ownership locks.
                    bool attached = fileAdoption.TryRunRecoveryRead(() =>
                    {
                        if (!FileHandoffScopeCurrent()) return false;
                        parentRegistration = ParentRegistrationChannel.Attach(endpointName, id, parentId, parentCreation, guiId, guiCreation, registration);
                        return parentRegistration != null && FileHandoffScopeCurrent();
                    });
                    return attached ? parentRegistration : null;
                }
            }
        }

        public sealed partial class FrontierFinalizerBinding
        {
            private bool parentHandoffAttempted;
            private FrontierParentHandoff parentHandoff;
            public FrontierParentHandoff TryAttachParentHandoff()
            {
                lock (sourceSync)
                {
                    if (!IsCurrent) return null;
                    bool success = false;
                    try
                    {
                        if (parentHandoffAttempted) return null; parentHandoffAttempted = true;
                        if (!OwnsPersistedEvidence(persistedEvidence)) return null;
                        var channel = frontierLaunchOwner.AttachParentRegistration();
                        if (channel == null) return null;
                        parentHandoff = new FrontierParentHandoff(this, persistedEvidence, channel);
                        success = OwnsParentHandoff(parentHandoff); return success ? parentHandoff : null;
                    }
                    catch { return null; }
                    finally { if (!success) Dispose(); }
                }
            }
            internal bool OwnsParentHandoff(FrontierParentHandoff value)
            {
                lock (sourceSync)
                {
                    if (value == null || !Object.ReferenceEquals(parentHandoff, value)) return false;
                    if (Object.ReferenceEquals(value.Evidence, persistedEvidence) && OwnsPersistedEvidence(persistedEvidence) && value.Channel.IsCurrent) return true;
                    Dispose(); return false;
                }
            }
            internal void DisposeParentHandoff(FrontierParentHandoff value)
            { lock (sourceSync) { if (Object.ReferenceEquals(parentHandoff, value)) Dispose(); } }
        }
        public sealed class FrontierParentHandoff : IDisposable
        {
            private readonly FrontierFinalizerBinding owner;
            internal readonly FrontierPersistedEvidence Evidence;
            internal readonly ParentRegistrationChannel Channel;
            internal FrontierParentHandoff(FrontierFinalizerBinding owner, FrontierPersistedEvidence evidence, ParentRegistrationChannel channel)
            { this.owner = owner; Evidence = evidence; Channel = channel; }
            public bool IsCurrent { get { return owner.OwnsParentHandoff(this); } }
            public bool LiveProofEligible { get { return false; } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            public void Dispose() { owner.DisposeParentHandoff(this); }
        }

        // Fixed 128-byte control envelope; no paths, native handles or evidence
        // payloads. The original legacy request body is bound by SHA-256.
        internal sealed partial class ParentRegistrationChannel : IDisposable
        {
            private const int Bytes = 128, RegistrationMagic = 0x4e505231;
            private MemoryMappedFile map;
            private MemoryMappedViewAccessor view;
            private Mutex gate;
            private EventWaitHandle ready, cancelled;
            private byte[] snapshot;
            private int disposed, lastState;
            internal string Endpoint;
            internal LocalFrontierParentFileChannel Files;
            internal byte[] BindingHash { get { using (var hash = SHA256.Create()) return hash.ComputeHash(snapshot); } }
            internal int State { get { return view.ReadInt32(8); } }
            private ParentRegistrationChannel() { }
            internal static ParentRegistrationChannel Create(string name, Guid request, FrontierParentRegistration value)
            {
                var channel = new ParentRegistrationChannel();
                try
                {
                    string endpoint = name + ".frontier-parent-v1";
                    channel.Endpoint = endpoint;
                    channel.map = MemoryMappedFile.CreateNew(endpoint, Bytes); channel.view = channel.map.CreateViewAccessor(0, Bytes);
                    channel.gate = new Mutex(false, endpoint + ".gate");
                    channel.ready = new EventWaitHandle(false, EventResetMode.ManualReset, endpoint + ".ready");
                    channel.cancelled = new EventWaitHandle(false, EventResetMode.ManualReset, endpoint + ".cancelled");
                    channel.OpenRendezvous(true);
                    channel.view.Write(0, RegistrationMagic); channel.view.Write(4, 1);
                    channel.view.WriteArray(16, request.ToByteArray(), 0, 16);
                    channel.view.WriteArray(32, Guid.NewGuid().ToByteArray(), 0, 16);
                    channel.view.Write(48, value.ParentId); channel.view.Write(52, value.GuiId);
                    channel.view.Write(56, value.ParentCreation); channel.view.Write(64, value.GuiCreation);
                    using (var hash = SHA256.Create()) channel.view.WriteArray(72, hash.ComputeHash(value.Body), 0, 32);
                    channel.SaveSnapshot(); channel.ready.Set();
                    var result = channel; channel = null; return result;
                }
                catch { return null; }
                finally { if (channel != null) channel.Close(); }
            }
            internal static ParentRegistrationChannel Attach(string name, Guid request, int parentId, long parentCreation, int guiId, long guiCreation, byte[] body)
            {
                var channel = new ParentRegistrationChannel(); bool held = false;
                try
                {
                    string endpoint = name + ".frontier-parent-v1";
                    channel.Endpoint = endpoint;
                    channel.map = MemoryMappedFile.OpenExisting(endpoint); channel.view = channel.map.CreateViewAccessor(0, Bytes);
                    channel.gate = Mutex.OpenExisting(endpoint + ".gate");
                    channel.ready = EventWaitHandle.OpenExisting(endpoint + ".ready");
                    channel.cancelled = EventWaitHandle.OpenExisting(endpoint + ".cancelled");
                    channel.OpenRendezvous(false);
                    held = channel.gate.WaitOne(0);
                    if (!held || !channel.ready.WaitOne(0) || channel.cancelled.WaitOne(0) || channel.State != 0 ||
                        channel.view.ReadInt32(0) != RegistrationMagic || channel.view.ReadInt32(4) != 1 ||
                        channel.ReadGuid(16) != request || channel.ReadGuid(32) == Guid.Empty ||
                        channel.view.ReadInt32(48) != parentId || channel.view.ReadInt32(52) != guiId ||
                        channel.view.ReadInt64(56) != parentCreation || channel.view.ReadInt64(64) != guiCreation) return null;
                    using (var hash = SHA256.Create())
                    { var digest = hash.ComputeHash(body); for (int i = 0; i < digest.Length; i++) if (channel.view.ReadByte(72 + i) != digest[i]) return null; }
                    channel.SaveSnapshot(); if (!channel.IsCurrent) return null;
                    channel.view.Write(8, 1); channel.lastState = 1;
                    Release(channel.gate, held); held = false;
                    var result = channel; channel = null; return result;
                }
                catch (AbandonedMutexException) { held = true; return null; }
                catch { return null; }
                // A rejected open cannot cancel another original registration.
                finally { if (channel != null) { Release(channel.gate, held); channel.Close(); } }
            }
            private Guid ReadGuid(int offset)
            { var bytes = new byte[16]; view.ReadArray(offset, bytes, 0, bytes.Length); return new Guid(bytes); }
            private void SaveSnapshot()
            { snapshot = new byte[Bytes]; view.ReadArray(0, snapshot, 0, snapshot.Length); }
            internal bool IsCurrent
            {
                get
                {
                    try
                    {
                        if (Volatile.Read(ref disposed) != 0 || cancelled.WaitOne(0) || !ready.WaitOne(0)) return false;
                        int state = State;
                        if (state < Volatile.Read(ref lastState) || state > 1 || state < 0) return false;
                        if (state == 1) Interlocked.Exchange(ref lastState, 1);
                        for (int i = 0; i < Bytes; i++)
                        {
                            if (i >= 8 && i < 12) continue;
                            if (view.ReadByte(i) != snapshot[i] || ((i >= 12 && i < 16 || i >= 104) && snapshot[i] != 0)) return false;
                        }
                        return Files == null || Files.IsCurrent;
                    }
                    catch { return false; }
                }
            }
            public void Dispose()
            { try { if (cancelled != null) cancelled.Set(); } catch { } if (Files != null) Files.Dispose(); Close(); }
            private void Close()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                foreach (var signal in rendezvousSignals) if (signal != null) signal.Dispose();
                if (view != null) view.Dispose(); if (map != null) map.Dispose();
                if (gate != null) gate.Dispose(); if (ready != null) ready.Dispose(); if (cancelled != null) cancelled.Dispose();
            }
        }
    }
}
