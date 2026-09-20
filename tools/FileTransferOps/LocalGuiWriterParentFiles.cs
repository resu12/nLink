using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class FrontierFinalizerBinding
        {
            private bool parentSubmissionAttempted;
            private FrontierParentFileSubmission parentSubmission;
            public FrontierParentFileSubmission TryPublishParentFiles()
            {
                lock (sourceSync)
                {
                    if (!IsCurrent) return null;
                    bool success = false;
                    try
                    {
                        if (parentSubmissionAttempted) return null; parentSubmissionAttempted = true;
                        if (!OwnsParentPreparation(parentPreparation)) return null;
                        var adoption = files.GetOriginalAdoption(); var registration = parentHandoff.Channel;
                        if (adoption == null || registration.Files != null || !adoption.TryRunRecoveryRead(() =>
                        {
                            var bytes = parentPreparation.Files.ExportParentPacket();
                            if (bytes == null || !registration.IsCurrent) return false;
                            registration.Files = LocalFrontierParentFileChannel.Create(registration.Endpoint, registration.BindingHash, bytes,
                                adoption.FinalizationLifetime(sourceReader));
                            return registration.Files != null && parentPreparation.Files.IsCurrent && registration.IsCurrent;
                        })) return null;
                        parentSubmission = new FrontierParentFileSubmission(this, parentPreparation, registration.Files);
                        success = OwnsParentSubmission(parentSubmission); return success ? parentSubmission : null;
                    }
                    catch { return null; }
                    finally { if (!success) Dispose(); }
                }
            }
            internal bool OwnsParentSubmission(FrontierParentFileSubmission value)
            {
                lock (sourceSync)
                {
                    if (value == null || !Object.ReferenceEquals(parentSubmission, value)) return false;
                    if (Object.ReferenceEquals(value.Preparation, parentPreparation) && OwnsParentPreparation(parentPreparation) && value.Channel.IsCurrent) return true;
                    Dispose(); return false;
                }
            }
            internal void DisposeParentSubmission(FrontierParentFileSubmission value)
            { lock (sourceSync) { if (Object.ReferenceEquals(parentSubmission, value)) Dispose(); } }
        }
        public sealed partial class FrontierParentFileSubmission : IDisposable
        {
            private readonly FrontierFinalizerBinding owner;
            internal readonly FrontierParentPreparation Preparation;
            internal readonly LocalFrontierParentFileChannel Channel;
            internal FrontierParentFileSubmission(FrontierFinalizerBinding owner, FrontierParentPreparation preparation, LocalFrontierParentFileChannel channel)
            { this.owner = owner; Preparation = preparation; Channel = channel; }
            public bool IsCurrent { get { return owner.OwnsParentSubmission(this); } }
            public bool FilesPulled { get { return IsCurrent && Channel.State == 2; } }
            public bool LiveProofEligible { get { return false; } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            public void Dispose() { owner.DisposeParentSubmission(this); }
        }
        public sealed partial class FrontierParentRegistration
        {
            private bool filesAttempted;
            internal readonly Stopwatch parentWork = new Stopwatch();
            internal FrontierParentFilesPending PendingFiles;
            public FrontierParentFilesPending TryPullFiles()
            {
                lock (owner.frontierParentSync)
                {
                    if (!Object.ReferenceEquals(owner.frontierParentRegistration, this)) return null;
                    bool success = false;
                    var work = parentWork; work.Start();
                    try
                    {
                        if (filesAttempted) return null; filesAttempted = true;
                        if (!IsCurrent || !GuiAttached || Channel.Files != null) return null;
                        Channel.Files = LocalFrontierParentFileChannel.Open(Channel.Endpoint, Channel.BindingHash);
                        if (Channel.Files == null || !IsCurrent) return null;
                        var packet = LocalFrontierParentFilePacket.Decode(Channel.Files.Body);
                        if (packet == null || packet.Gui != owner.RequestId) return null;
                        PendingFiles = new FrontierParentFilesPending(this, packet);
                        if (!PendingFiles.Pull(owner.workerExit.SafeWaitHandle, () => work.ElapsedMilliseconds <= 5000 && IsCurrent) ||
                            !IsCurrent || work.ElapsedMilliseconds > 5000 || !Channel.Files.MarkPulled()) return null;
                        PendingFiles.Watch(owner.workerExit, owner.closed, Channel.Files.Cancelled);
                        success = PendingFiles.IsCurrent && work.ElapsedMilliseconds <= 5000; return success ? PendingFiles : null;
                    }
                    catch { return null; }
                    finally { work.Stop(); if (!success) owner.RetireFrontierParentRegistration(); }
                }
            }
            internal bool OwnsPendingFiles(FrontierParentFilesPending value)
            {
                lock (owner.frontierParentSync)
                {
                    if (!Object.ReferenceEquals(owner.frontierParentRegistration, this) || !Object.ReferenceEquals(PendingFiles, value)) return false;
                    if (IsCurrent && Channel.Files != null && Channel.Files.State == 2 && value.NativeCurrent) return true;
                    owner.RetireFrontierParentRegistration(); return false;
                }
            }
            internal void DisposePendingFiles(FrontierParentFilesPending value)
            { lock (owner.frontierParentSync) { if (Object.ReferenceEquals(PendingFiles, value)) owner.RetireFrontierParentRegistration(); } }
        }
        // Provisional pins. Fact receipt is separately guarded; no reader,
        // acknowledgment or post-GUI/request lifetime is exposed.
        public sealed partial class FrontierParentFilesPending : IDisposable
        {
            private readonly FrontierParentFilesPending original;
            private readonly FrontierParentRegistration owner;
            private readonly object sync = new object();
            private readonly LocalFrontierParentFilePacket packet;
            private readonly List<SafeFileHandle> handles = new List<SafeFileHandle>();
            private readonly List<RegisteredWaitHandle> watches = new List<RegisteredWaitHandle>();
            private bool closed;
            internal FrontierParentFilesPending(FrontierParentRegistration owner, LocalFrontierParentFilePacket packet)
            { original = this; this.owner = owner; this.packet = packet; }
            public bool IsCurrent { get { return Object.ReferenceEquals(original, this) && owner.OwnsPendingFiles(this); } }
            public int FileCount { get { return IsCurrent ? packet.Entries.Length : 0; } }
            public int PayloadCount { get { return IsCurrent ? packet.Entries.Length - 10 : 0; } }
            public bool LiveProofEligible { get { return false; } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            internal bool Pull(SafeWaitHandle process, Func<bool> current)
            {
                lock (sync)
                {
                    if (!Object.ReferenceEquals(original, this) || closed || handles.Count != 0) return false;
                    foreach (var entry in packet.Entries)
                    {
                        if (!current()) return false;
                        SafeFileHandle handle = null;
                        try
                        {
                            if (!DuplicateHandle(process, new IntPtr(entry.File.Handle), GetCurrentProcess(), out handle, 0x80000000, false, 0) ||
                                !Matches(handle, entry.File)) return false;
                            handles.Add(handle); handle = null;
                        }
                        finally { if (handle != null) handle.Dispose(); }
                    }
                    return current() && NativeCurrent;
                }
            }
            internal void Watch(params WaitHandle[] events)
            {
                lock (sync)
                {
                    if (closed) return;
                    foreach (var signal in events)
                        watches.Add(ThreadPool.RegisterWaitForSingleObject(signal, (state, timedOut) => Close(), null, Timeout.Infinite, true));
                }
            }
            internal bool NativeCurrent
            {
                get
                {
                    lock (sync)
                    {
                        if (!Object.ReferenceEquals(original, this) || closed || handles.Count != packet.Entries.Length) return false;
                        for (int i = 0; i < handles.Count; i++) if (!Matches(handles[i], packet.Entries[i].File)) return false;
                        return true;
                    }
                }
            }
            internal void Close()
            {
                lock (sync)
                {
                    if (!Object.ReferenceEquals(original, this) || closed) return;
                    closed = true;
                    foreach (var watch in watches) watch.Unregister(null);
                    foreach (var handle in handles) handle.Dispose();
                }
            }
            public void Dispose() { if (Object.ReferenceEquals(original, this)) owner.DisposePendingFiles(this); }
            private static bool Matches(SafeFileHandle handle, LocalFrontierFileManifest.FileEntry expected)
            {
                LocalFrontierPreparedFiles.FileInformation info;
                return handle != null && !handle.IsClosed && !handle.IsInvalid && GetFileType(handle) == 1 &&
                    GetFileInformationByHandle(handle, out info) && (info.Attributes & 16) == 0 &&
                    info.Volume == expected.Volume && info.IndexHigh == expected.IndexHigh && info.IndexLow == expected.IndexLow &&
                    (((long)info.SizeHigh << 32) | info.SizeLow) == expected.Length;
            }
            [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
            [DllImport("kernel32.dll")] private static extern bool DuplicateHandle(SafeWaitHandle process, IntPtr handle, IntPtr target,
                out SafeFileHandle duplicate, uint access, bool inherit, uint options);
            [DllImport("kernel32.dll")] private static extern uint GetFileType(SafeFileHandle file);
            [DllImport("kernel32.dll")] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out LocalFrontierPreparedFiles.FileInformation info);
        }
    }
}
