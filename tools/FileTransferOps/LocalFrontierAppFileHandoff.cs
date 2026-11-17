#if NET8_0_OR_GREATER
#pragma warning disable 8600, 8601, 8602, 8603, 8604, 8618, 8625
#endif
using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace NLink.Tools
{
    public sealed partial class LocalFrontierAppRequest
    {
        private const string FilesReadySuffix = ".files-ready-v1";
        private EventWaitHandle filesReady;

        // The original GUI calls this only after creating both sealed channels.
        // This wakes attachment; it cannot supply a channel or writer authority.
        internal bool PublishFileHandoffReady()
        {
            lock (startSync)
            {
                try
                {
                    if (appId <= 0 || !MatchesGuiLaunch(parentId, parentCreation, appId, appCreation) ||
                        view.ReadInt32(8) > 2 || appExit == null || appExit.WaitOne(0)) return false;
                    filesReady.Set(); return true;
                }
                catch { return false; }
            }
        }

        public sealed partial class AppLease
        {
            private int fileAttachAttempted;
            private int fileReadyWaitAttempted;
            private FileHandoff fileHandoff;

            // Call before creating the catalog. The idle wait consumes no file
            // attach attempt and holds no request/catalog/logging mutex.
            internal bool WaitForFileHandoffReady()
            { return WaitForFileHandoffReady(null); }

            internal bool WaitForFileHandoffReady(WaitHandle stopping)
            {
                if (!Object.ReferenceEquals(original, this) || Interlocked.Exchange(ref fileReadyWaitAttempted, 1) != 0) return false;
                bool ready = false;
                try
                {
                    if (!IsCurrent || Volatile.Read(ref fileAttachAttempted) != 0) return false;
                    using (var signal = EventWaitHandle.OpenExisting(endpointName + FilesReadySuffix))
                    {
                        var waits = stopping == null ? new WaitHandle[] { cancelled, parentExit, signal } :
                            new WaitHandle[] { cancelled, parentExit, signal, stopping };
                        ready = WaitHandle.WaitAny(waits, StartWaitMilliseconds) == 2 &&
                            IsCurrent && Volatile.Read(ref fileAttachAttempted) == 0 && (stopping == null || !stopping.WaitOne(0));
                    }
                    return ready;
                }
                catch { return false; }
                finally
                {
                    if (!ready) { try { cancelled.Set(); } catch (ObjectDisposedException) { } }
                }
            }

            internal bool OwnsFileHandoff(FileHandoff candidate)
            { return Object.ReferenceEquals(original, this) && Object.ReferenceEquals(fileHandoff, candidate); }
            internal bool AttachmentCompleted { get { return Volatile.Read(ref completed) != 0; } }
            internal FileHandoff TryAttachFileHandoff(object owner, Func<bool> current)
            {
                if (!Object.ReferenceEquals(original, this) || Interlocked.Exchange(ref fileAttachAttempted, 1) != 0) return null;
                LocalFrontierFileChannel channel = null; bool held = false;
                try
                {
                    if (!OwnsCatalog(owner) || current == null || !current()) return null;
                    channel = LocalFrontierFileChannel.Open(endpointName, RequestId, parentId, parentCreation, appId, appCreation);
                    if (channel == null) return null;
                    held = channel.Gate.WaitOne(0);
                    if (!held || !channel.IsCurrent || channel.State != LocalFrontierFileChannel.Ready ||
                        !channel.EmptySubmission() || !OwnsCatalog(owner) || !current()) return null;
                    var result = new FileHandoff(this, owner, current, channel);
                    channel.View.Write(8, LocalFrontierFileChannel.Attached);
                    fileHandoff = result;
                    LocalFrontierFileChannel.Release(channel.Gate, held); held = false; channel = null;
                    return result;
                }
                catch (AbandonedMutexException) { held = true; return null; }
                catch { return null; }
                finally { if (channel != null) { LocalFrontierFileChannel.Release(channel.Gate, held); channel.Dispose(); } }
            }
        }

        // Exact original lease/catalog reference + protocol custody only. This
        // internal API does not attest caller-generated receipt or payload data.
        internal sealed class FileHandoff : IDisposable
        {
            private readonly AppLease lease;
            private readonly object owner;
            private readonly Func<bool> current;
            private readonly LocalFrontierFileChannel channel;
            private readonly object sync = new object();
            private readonly List<SafeFileHandle> pinned = new List<SafeFileHandle>();
            private bool submitAttempted, ackAttempted, adoptionWaitAttempted, disposed;
            internal bool Acknowledged { get; private set; }
            internal Guid GuiRequest { get { return channel.GuiRequest; } }
            internal FileHandoff(AppLease lease, object owner, Func<bool> current, LocalFrontierFileChannel channel)
            { this.lease = lease; this.owner = owner; this.current = current; this.channel = channel; }
            private bool Original { get { return lease.OwnsFileHandoff(this); } }
            // Writer checks run under the logging gate. Do not call back into
            // the catalog (or take sync) from this exact-owner native check.
            internal bool OwnsWriterScope(object candidate)
            {
                try { return Object.ReferenceEquals(owner, candidate) && Original && !disposed &&
                    lease.OwnsCatalog(owner) && channel.IsCurrent && channel.State >= LocalFrontierFileChannel.Attached; }
                catch { return false; }
            }
            internal bool IsCurrent
            {
                get
                {
                    try { return Original && !disposed && lease.OwnsCatalog(owner) && current() && channel.IsCurrent &&
                        channel.State >= LocalFrontierFileChannel.Attached && (!Acknowledged || channel.State == LocalFrontierFileChannel.Acknowledged); }
                    catch { return false; }
                }
            }
            internal bool TrySubmit(byte[] bytes, SafeFileHandle[] handles)
            {
                lock (sync)
                {
                    if (!Original || disposed) return false;
                    bool held = false, success = false;
                    try
                    {
                        if (submitAttempted) return false; submitAttempted = true;
                        if (!IsCurrent || bytes == null || bytes.Length > LocalFrontierFileManifest.MaximumBytes) return false;
                        var copy = (byte[])bytes.Clone(); var manifest = LocalFrontierFileManifest.Decode(copy);
                        if (manifest == null || manifest.GuiRequest != channel.GuiRequest || manifest.AppRequest != lease.RequestId ||
                            manifest.AppId != lease.appId || manifest.AppCreation != lease.appCreation || handles == null || handles.Length != manifest.Payloads.Length + 1) return false;
                        for (int i = 0; i < handles.Length; i++)
                        {
                            var handle = handles[i]; bool added = false;
                            if (handle == null || handle.IsClosed || handle.IsInvalid || handle.DangerousGetHandle().ToInt64() !=
                                (i == 0 ? manifest.Source.Handle : manifest.Payloads[i - 1].File.Handle)) return false;
                            handle.DangerousAddRef(ref added); if (!added) return false; pinned.Add(handle);
                        }
                        held = channel.Gate.WaitOne(0);
                        if (!held || !IsCurrent || channel.State != LocalFrontierFileChannel.Attached || !channel.EmptySubmission() || !channel.RememberSubmission(copy)) return false;
                        channel.PublishSubmission(); success = true;
                    }
                    catch (AbandonedMutexException) { held = true; return false; }
                    catch { return false; }
                    finally { LocalFrontierFileChannel.Release(channel.Gate, held); if (!success || !channel.IsCurrent) Cancel(); }
                    if (!IsCurrent) { Cancel(); return false; }
                }
                // Native state is complete and both the channel mutex and local
                // submission lock are released before the GUI can wake.
                try { channel.SubmittedEvent.Set(); return true; }
                catch { Cancel(); return false; }
            }
            // Submission precedes this optional one-shot idle wait. The native
            // channel's adopted state is still consumed only by the original
            // acknowledgment method below, after all of its exact checks.
            internal bool WaitForAdoption()
            {
                lock (sync)
                {
                    if (!Original || disposed || adoptionWaitAttempted || ackAttempted) return false;
                    adoptionWaitAttempted = true;
                }
                bool ready = false;
                try
                {
                    if (!submitAttempted || !IsCurrent) return false;
                    if (WaitHandle.WaitAny(new WaitHandle[] { lease.cancelled, lease.parentExit, channel.cancelled, channel.AdoptedEvent },
                        StartWaitMilliseconds) != 3) return false;
                    lock (sync)
                    {
                        ready = Original && !disposed && !ackAttempted && IsCurrent && channel.State == LocalFrontierFileChannel.Adopted;
                        return ready;
                    }
                }
                catch { return false; }
                finally { if (!ready) Cancel(); }
            }

            internal bool TryTakeAcknowledgment()
            {
                lock (sync)
                {
                    if (!Original || disposed) return false;
                    bool held = false, success = false;
                    try
                    {
                        if (ackAttempted) return false; ackAttempted = true;
                        if (!submitAttempted || !IsCurrent || !channel.AdoptedEvent.WaitOne(0)) return false;
                        held = channel.Gate.WaitOne(0);
                        if (!held || !IsCurrent || channel.State != LocalFrontierFileChannel.Adopted) return false;
                        channel.View.Write(8, LocalFrontierFileChannel.Acknowledged);
                        Acknowledged = true; ReleaseHandles(); success = true; return true;
                    }
                    catch (AbandonedMutexException) { held = true; return false; }
                    catch { return false; }
                    finally { LocalFrontierFileChannel.Release(channel.Gate, held); if (!success || !channel.IsCurrent) Cancel(); }
                }
            }
            internal void Cancel()
            {
                if (!Original) return;
                channel.Cancel();
                // Catalog invalidation may hold its own gate while an active
                // submission checks it. Signal first, never wait on that cycle.
                if (!Monitor.TryEnter(sync)) return;
                try { ReleaseHandles(); } finally { Monitor.Exit(sync); }
            }
            private void ReleaseHandles() { foreach (var handle in pinned) handle.DangerousRelease(); pinned.Clear(); }
            internal void RetireCatalog(object candidate)
            { if (Object.ReferenceEquals(owner, candidate) && (!Acknowledged || !lease.AttachmentCompleted)) Cancel(); }
            public void Dispose()
            {
                if (!Original) return;
                lock (sync)
                {
                    if (disposed) return; disposed = true;
                    if (!Acknowledged || !lease.AttachmentCompleted) channel.Cancel();
                    ReleaseHandles(); channel.Close();
                }
            }
        }
    }
}
