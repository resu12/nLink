using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class FrontierLaunchOwner
        {
            private bool fileEnableAttempted;
            private readonly bool[] fileAdoptAttempted = new bool[2];
            private LocalFrontierFileChannel[] fileChannels;
            private FrontierAppRequest[] fileRequests;
            private LocalFrontierFileAdoption fileAdoption;
            private bool fileAdoptionWaitAttempted;
            private bool appExitSignalWaitAttempted;
            private Stopwatch fileAdoptionDeadline;
            private int fileAdoptionTimeoutMilliseconds;

            // One original shutdown wait, not a submission/ack issuer. Consume
            // each adoption only after its exact submitted state/event exists.
            public bool WaitForFileAdoptions(int timeoutMilliseconds)
            {
                lock (sync)
                {
                    if (!IsCurrent || fileAdoptionWaitAttempted) return false;
                    fileAdoptionWaitAttempted = true;
                    if (fileChannels == null || fileAdoptAttempted[0] || fileAdoptAttempted[1] || timeoutMilliseconds <= 0)
                    { Dispose(); return false; }
                    fileAdoptionTimeoutMilliseconds = timeoutMilliseconds;
                    fileAdoptionDeadline = Stopwatch.StartNew();
                }
                bool success = false;
                var adopted = new bool[2];
                try
                {
                    while (IsCurrent && FileAdoptionWithinDeadline())
                    {
                        for (int i = 0; i < 2; i++)
                        {
                            if (!adopted[i] && FileSubmissionAvailable(fileRequests[i]))
                            {
                                if (!TryAdoptFiles(fileRequests[i])) return false;
                                adopted[i] = true;
                            }
                        }
                        if (adopted[0] && adopted[1])
                        { success = IsCurrent && FileAdoptionWithinDeadline(); return success; }
                        var wake = new List<WaitHandle> { closed, parentExit, fileChannels[0].cancelled, fileChannels[1].cancelled };
                        for (int i = 0; i < 2; i++)
                        {
                            if (adopted[i]) continue;
                            wake.Add(fileChannels[i].SubmittedEvent);
                            wake.Add(fileRequests[i].launch.Exit);
                        }
                        int remaining = (int)Math.Max(0, timeoutMilliseconds - fileAdoptionDeadline.ElapsedMilliseconds);
                        // No GUI/adopter/channel lock is held during idle wait.
                        if (remaining == 0) return false;
                        int signaled = WaitHandle.WaitAny(wake.ToArray(), remaining);
                        if (signaled == WaitHandle.WaitTimeout || signaled < 4 || (signaled - 4) % 2 != 0) return false;
                        int submittedSlot = !adopted[0] && signaled == 4 ? 0 : 1;
                        if (!FileSubmissionAvailable(fileRequests[submittedSlot])) return false;
                    }
                    return false;
                }
                catch { return false; }
                finally
                {
                    // The deadline also fences active pull/hash/control checks
                    // through FileHandoffScopeCurrent, not just event waiting.
                    lock (sync) { if (!success) Dispose(); fileAdoptionDeadline = null; }
                }
            }

            // Process.HasExited may observe an exit code before the kernel
            // process object is signaled. Wait on the original handles once,
            // outside ownership locks, within the remaining cleanup budget.
            // This is exit evidence only; final ACK/attachment/exit-code checks
            // remain mandatory after the wait.
            public bool WaitForAppExitSignals(int timeoutMilliseconds)
            {
                var clock = Stopwatch.StartNew();
                WaitHandle[] exits;
                lock (sync)
                {
                    if (!IsCurrent || appExitSignalWaitAttempted) return false;
                    appExitSignalWaitAttempted = true;
                    if (!sealedLaunches || !fileAdoptionWaitAttempted || fileChannels == null ||
                        !fileAdoptAttempted[0] || !fileAdoptAttempted[1] || timeoutMilliseconds <= 0)
                    { Dispose(); return false; }
                    exits = new WaitHandle[] { fileRequests[0].launch.Exit, fileRequests[1].launch.Exit };
                }
                bool success = false;
                try
                {
                    foreach (var exit in exits)
                    {
                        int remaining = (int)Math.Max(0, timeoutMilliseconds - clock.ElapsedMilliseconds);
                        if (!IsCurrent || remaining == 0 || WaitHandle.WaitAny(new WaitHandle[]
                            { closed, parentExit, fileChannels[0].cancelled, fileChannels[1].cancelled, exit }, remaining) != 4)
                            return false;
                    }
                    success = IsCurrent && clock.ElapsedMilliseconds < timeoutMilliseconds;
                    return success;
                }
                catch { return false; }
                finally { if (!success) Dispose(); }
            }

            private bool FileAdoptionWithinDeadline()
            {
                var deadline = fileAdoptionDeadline;
                return deadline == null || deadline.ElapsedMilliseconds < fileAdoptionTimeoutMilliseconds;
            }

            // No imported endpoint, process, scope, manifest or callback. Only
            // the original fully bound/sealed GUI registry can create channels.
            public bool TryEnableFileHandoff()
            {
                lock (sync)
                {
                    if (!IsCurrent) return false;
                    bool success = false;
                    try
                    {
                        if (fileEnableAttempted) return false; fileEnableAttempted = true;
                        if (!sealedLaunches || appRequests.Count != 2 || apps.Count != 2) return false;
                        fileRequests = appRequests.ToArray(); fileChannels = new LocalFrontierFileChannel[2];
                        for (int i = 0; i < 2; i++)
                        {
                            var request = fileRequests[i]; var launch = request.launch;
                            if (launch == null || launch.Exit.WaitOne(0) || request.native.GuiAttachmentCompleted) return false;
                            fileChannels[i] = LocalFrontierFileChannel.Create(request.Name, id, request.RequestId, i,
                                guiId, guiCreation, launch.AppProcessId, launch.AppProcessCreationFileTime);
                            if (fileChannels[i] == null) return false;
                        }
                        fileAdoption = new LocalFrontierFileAdoption(id, fileRequests[0].RequestId, fileRequests[1].RequestId, FileHandoffScopeCurrent, frontierWork);
                        if (!FileHandoffScopeCurrent()) return false;
                        foreach (var channel in fileChannels) channel.ReadyEvent.Set();
                        foreach (var request in fileRequests) if (!request.native.PublishFileHandoffReady()) return false;
                        success = true; return true;
                    }
                    catch { return false; }
                    finally { if (!success) Dispose(); }
                }
            }

            // Lock-free immutable sealed registry checks. Adoption holds its own
            // lock and may race GUI disposal; never acquire sync from its callback.
            private bool FileHandoffScopeCurrent()
            {
                try
                {
                    return Object.ReferenceEquals(frontierLaunchOwner, this) && !disposed && sealedLaunches && FileAdoptionWithinDeadline() &&
                        !parentExit.WaitOne(0) && !closed.WaitOne(0) && MatchesRequest() && AppRequestsCurrent() && FileChannelsCurrent() && LedgerOriginCurrent() && ParentRegistrationCurrent() && FileAdoptionWithinDeadline();
                }
                catch { return false; }
            }
            private bool FileChannelsCurrent()
            {
#if NLINK_TEST_BUFFERED_LEDGER
                NLink.Tests.ProcessingCustodyProbe.Boundary("scope");
#endif
                if (fileChannels == null) return true;
                for (int i = 0; i < 2; i++)
                {
                    var channel = fileChannels[i]; var request = fileRequests[i];
                    if (channel == null || !channel.IsCurrent) return false;
                    int code;
                    // ACK is published before attachment completion and exit.
                    // Sample it after those observations, not from a stale state
                    // captured before the app could finish between reads.
                    if (channel.State > LocalFrontierFileChannel.Ready && !request.native.GuiAttachmentOpen &&
                        !(request.native.GuiAttachmentCompleted && channel.State == LocalFrontierFileChannel.Acknowledged)) return false;
                    if (request.launch.Exit.WaitOne(0) && (channel.State != LocalFrontierFileChannel.Acknowledged ||
                        !request.native.GuiAttachmentCompleted || !GetExitCodeProcess(request.launch.Exit.SafeWaitHandle, out code) || code != 0)) return false;
                }
                return true;
            }
            internal bool FileSubmissionAvailable(FrontierAppRequest request)
            {
                lock (sync)
                {
                    int slot = appRequests.IndexOf(request);
                    if (slot < 0 || !IsCurrent || fileChannels == null) return false;
                    try { return fileChannels[slot].SubmittedEvent.WaitOne(0) && fileChannels[slot].State == LocalFrontierFileChannel.Submitted; }
                    catch { Dispose(); return false; }
                }
            }
            internal bool TryAdoptFiles(FrontierAppRequest request)
            {
                int slot; LocalFrontierFileChannel channel; LocalFrontierFileAdoption adoption;
                lock (sync)
                {
                    slot = appRequests.IndexOf(request);
                    if (slot < 0 || !IsCurrent) return false;
                    if (fileChannels == null || fileAdoptAttempted[slot]) { Dispose(); return false; }
                    fileAdoptAttempted[slot] = true; channel = fileChannels[slot]; adoption = fileAdoption;
                }
                bool success = adoption.TryRunControl(() =>
                {
                    bool held = false; byte[] bytes;
                    try
                    {
                        if (!FileHandoffScopeCurrent() || !request.native.GuiAttachmentOpen) return false;
                        held = channel.Gate.WaitOne(0);
                        if (!held || !FileHandoffScopeCurrent()) return false;
                        bytes = channel.CaptureSubmission(); if (bytes == null) return false;
                        channel.View.Write(8, LocalFrontierFileChannel.Adopting);
                        LocalFrontierFileChannel.Release(channel.Gate, held); held = false;
                        if (!adoption.TryAdopt(slot, request.launch.Exit.SafeWaitHandle, bytes)) return false;
                        held = channel.Gate.WaitOne(0);
                        if (!held || !FileHandoffScopeCurrent() || !request.native.GuiAttachmentOpen ||
                            request.launch.Exit.WaitOne(0) || channel.State != LocalFrontierFileChannel.Adopting) return false;
                        channel.PublishAdopted(); return true;
                    }
                    catch (AbandonedMutexException) { held = true; return false; }
                    catch { return false; }
                    finally { LocalFrontierFileChannel.Release(channel.Gate, held); }
                });
                // Wake the app only after releasing the channel mutex and the
                // adoption work lock, so its one-shot acknowledgment can enter.
                if (success)
                {
                    try { success = FileHandoffScopeCurrent() && channel.AdoptedEvent.Set(); }
                    catch { success = false; }
                }
                if (!success) Dispose();
                return success;
            }
            public bool AllFileHandoffsAcknowledged
            {
                get
                {
                    lock (sync)
                    {
                        if (!IsCurrent || fileAdoption == null || fileAdoption.ContributionCount != 2) return false;
                        foreach (var channel in fileChannels) if (channel.State != LocalFrontierFileChannel.Acknowledged) return false;
                        return IsCurrent;
                    }
                }
            }
        }
    }
}
