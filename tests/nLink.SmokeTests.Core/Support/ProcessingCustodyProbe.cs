#if NLINK_TEST_BUFFERED_LEDGER
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using NLink.Tools;
using NLink.Tools.FrontierLedger;

namespace NLink.Tests
{
    public static class ProcessingCustodyProbe
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static int scopes, legacy;
        private static Action<string> fault;
        public static void Boundary(string name)
        {
            if (name == "scope") scopes++;
            if (name == "binding-legacy") legacy++;
            if (fault != null) fault(name);
        }
        private static void Require(bool value, string reason)
        { if (!value) throw new InvalidOperationException(reason); }
        private static object Get(object value, string field) { return value.GetType().GetField(field, Flags).GetValue(value); }
        private static void Set(object value, string field, object replacement) { value.GetType().GetField(field, Flags).SetValue(value, replacement); }
        private static T Copy<T>(T value) { return (T)typeof(object).GetMethod("MemberwiseClone", Flags).Invoke(value, null); }
        private static void Flip(MemoryMappedViewAccessor view, long offset) { view.Write(offset, (byte)(view.ReadByte(offset) ^ 1)); }
        private static void SetElapsed(Stopwatch work, int seconds)
        {
            foreach (var field in typeof(Stopwatch).GetFields(Flags))
                if (field.Name.TrimStart('_') == "elapsed") { field.SetValue(work, Stopwatch.Frequency * seconds); return; }
            throw new InvalidOperationException("work_clock_missing");
        }
        private static void Count(LocalGuiWriterRequest.FrontierSourceRead source, int expected, bool old)
        {
            scopes = legacy = 0;
            Require(source.CanRead, "original_not_current");
            Require(scopes == expected && legacy == (old ? 1 : 0), "count:" + scopes + "; legacy=" + legacy);
        }
        private static void Drain(LocalGuiWriterRequest.FrontierSourceRead source)
        { var buffer = new byte[65536]; int read; while ((read = source.Read(buffer, 0, buffer.Length)) != 0) Require(read <= 32768, "read_cap_changed"); }
        internal static void Run(LocalGuiWriterRequest.FrontierFinalizerBinding binding, LocalFrontierFileAdoption adoption,
            LocalGuiWriterRequest.FrontierSourceRead source, string endpoint, string mode)
        {
            Count(source, 10, true);
            Require(source.TryBeginProcessingWork(), "processing_missing");
            Count(source, 2, false);
            var owner = typeof(LocalGuiWriterRequest).GetField("frontierLaunchOwner", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var finalizer = (LocalGuiWriterRequest.Finalizer)Get(binding, "finalizer");
            var files = (LocalGuiWriterRequest.FrontierSealedFiles)Get(binding, "files");
            var work = (Stopwatch)Get(adoption, "work");
            if (mode.StartsWith("extended-", StringComparison.Ordinal))
            {
                Require(Object.ReferenceEquals(work, Get(owner, "frontierWork")), "shared_clock_replaced");
                SetElapsed(work, 6);
                Count(source, 2, false);
                mode = mode.Substring(9);
            }
            if (mode == "budget-window")
            {
                Require(LocalFrontierFileAdoption.MaximumActiveMilliseconds == 15000, "approved_budget_changed");
                Require(source.Read(new byte[32768], 0, 32768) > 0, "extended_read_missing");
                Drain(source);
                Require(source.TryFinishProcessingWork(), "extended_finish_failed");
                Require(!work.IsRunning && work.ElapsedMilliseconds >= 6000, "accumulated_work_reset");
                SetElapsed(work, 14);
                Count(source, 10, true);
                Require(Object.ReferenceEquals(work, Get(adoption, "work")) &&
                    Object.ReferenceEquals(work, Get(owner, "frontierWork")) &&
                    !work.IsRunning && work.ElapsedMilliseconds >= 14000, "finalization_clock_changed");
                var handles = new List<SafeFileHandle>();
                foreach (var item in (IList)Get(adoption, "adopted"))
                    foreach (FileStream file in (IList)Get(item, "Files")) handles.Add(file.SafeFileHandle);
                handles.Add(((FileStream)Get(adoption, "sealedSource")).SafeFileHandle);
                SetElapsed(work, 16);
                Require(!source.CanRead, "expired_finalization_current");
                foreach (var handle in handles) Require(handle.IsClosed, "expired_finalization_handle_leaked");
            }
            else if (mode == "counts")
            {
                Count(source, 2, false); // A second call must resample, not reuse the first verdict.
                scopes = legacy = 0;
                Require(source.Read(new byte[65536], 0, 65536) > 0, "read_missing");
                Require(scopes == 16 && legacy == 0, "read_count:" + scopes);
            }
            else if (mode == "cursor-counts")
            {
                using (var input = new GuiSmokeLedgerStreamRead(source, true))
                {
                    scopes = legacy = 0;
                    Require(input.ReadLine() != null, "cursor_line_missing");
                    Require(scopes == 20 && legacy == 0, "cursor_refill_count:" + scopes);
                }
            }
            else if (mode == "copies")
            {
                var readerCopy = Copy(source); Require(!readerCopy.CanRead, "reader_copy_current"); readerCopy.Dispose();
                var bindingCopy = Copy(binding); Require(!bindingCopy.IsCurrent, "binding_copy_current"); bindingCopy.Dispose();
                var filesCopy = Copy(files); Require(!filesCopy.IsCurrent, "files_copy_current"); filesCopy.Dispose();
                var adopterCopy = Copy(adoption); Require(!adopterCopy.ProcessingFilesCurrent(source), "adopter_copy_current"); adopterCopy.Dispose();
                Require(!adoption.ProcessingFilesCurrent(readerCopy) && !adoption.ProcessingFilesCurrent(new object()), "foreign_reader_current");
                var finalizerCopy = Copy(finalizer); Require(!finalizerCopy.OwnsFrontierBinding(binding, files), "finalizer_copy_current"); finalizerCopy.Dispose();
                ((IDisposable)Copy(owner)).Dispose();
                var foreignBinding = new LocalGuiWriterRequest.FrontierFinalizerBinding(finalizer, files);
                Require(!foreignBinding.IsCurrent, "foreign_binding_current"); foreignBinding.Dispose();
                Count(source, 2, false);
            }
            else if (mode == "after-finish")
            {
                Drain(source); Require(source.TryFinishProcessingWork(), "finish_failed"); Count(source, 10, true);
            }
            else if (mode == "parent-attempt")
            {
                Set(binding, "parentHandoffAttempted", true);
                try { Count(source, 10, true); }
                finally { Set(binding, "parentHandoffAttempted", false); }
            }
            else
            {
                var contributions = (IList)Get(adoption, "adopted");
                var retained = new List<FileStream>();
                foreach (var item in contributions) foreach (FileStream file in (IList)Get(item, "Files")) retained.Add(file);
                retained.Add((FileStream)Get(adoption, "sealedSource"));
                var handles = new List<SafeFileHandle>(); foreach (var file in retained) handles.Add(file.SafeFileHandle);
                var channels = (LocalFrontierFileChannel[])Get(owner, "fileChannels");
                var requests = (Array)Get(owner, "fileRequests");
                var request = requests.GetValue(0);
                var gui = (MemoryMappedViewAccessor)Get(owner, "view");
                var native = Get(request, "native");
                var app = (MemoryMappedViewAccessor)Get(native, "view");
                var channel = channels[0];
                int split = mode.IndexOf('-'); string when = mode.Substring(0, split), mutation = mode.Substring(split + 1);
                string target = "processing-" + when;
                if (when == "read") target = "processing-open";
                if (mutation == "finish") Drain(source);
                bool injected = false; Exception error = null; int visits = 0;
                WaitHandle originalParent = null; EventWaitHandle exited = null;
                MemoryMappedFile restoreMap = null; MemoryMappedViewAccessor restoreView = null;
                if (mutation == "restored") { restoreMap = MemoryMappedFile.OpenExisting(endpoint); restoreView = restoreMap.CreateViewAccessor(); }
                scopes = legacy = 0;
                fault = boundary =>
                {
                    if (boundary != target || (when == "read" && ++visits != 2)) return;
                    fault = null; injected = true;
                    try
                    {
                        if (mutation == "gui" || mutation == "restored") Flip(gui, 64);
                        else if (mutation == "stage") gui.Write(48, 0);
                        else if (mutation == "completion") gui.Write(52, 1);
                        else if (mutation == "app") Flip(app, 64);
                        else if (mutation == "digest") Flip(channel.View, 96);
                        else if (mutation == "body") Flip(channel.View, 192);
                        else if (mutation == "tail") Flip(channel.View, 192 + channel.View.ReadInt32(88) - 1);
                        else if (mutation == "reserved") Flip(channel.View, 191);
                        else if (mutation == "ack") Flip(channel.View, 144);
                        else if (mutation == "state") channel.View.Write(8, LocalFrontierFileChannel.Adopted);
                        else if (mutation == "channel-cancel") channel.cancelled.Set();
                        else if (mutation == "closed") { using (var closed = EventWaitHandle.OpenExisting(endpoint + ".closed")) closed.Set(); }
                        else if (mutation == "parent-signal")
                        {
                            originalParent = (WaitHandle)Get(owner, "parentExit"); exited = new EventWaitHandle(true, EventResetMode.ManualReset);
                            Set(owner, "parentExit", new LocalGuiWriterRequest.ProcessWait(new SafeWaitHandle(exited.SafeWaitHandle.DangerousGetHandle(), false)));
                        }
                        else if (mutation == "ledger") ((FileStream)Get(Get(owner, "ledgerOrigin"), "file")).Dispose();
                        else if (mutation == "output") ((LocalGuiWriterRequest.FrontierOutputCapture)Get(Get(request, "launch"), "Output")).Revoke();
                        else if (mutation.StartsWith("handle-", StringComparison.Ordinal)) retained[Int32.Parse(mutation.Substring(7))].Dispose();
                        else if (mutation == "identity")
                        {
                            var expected = Get(Get(contributions[1], "Manifest"), "Source");
                            Set(expected, "IndexLow", (uint)Get(expected, "IndexLow") ^ 1u);
                        }
                        else if (mutation == "length")
                        {
                            var payloads = (Array)Get(Get(contributions[0], "Manifest"), "Payloads");
                            var expected = Get(payloads.GetValue(0), "File"); Set(expected, "Length", (long)Get(expected, "Length") + 1);
                        }
                        else if (mutation == "budget") SetElapsed(work, 16);
                        else if (mutation == "stopped") ((Stopwatch)Get(adoption, "work")).Stop();
                        else if (mutation == "cancel") ((CancellationTokenSource)Get(adoption, "processingLifetime")).Cancel();
                        else if (mutation == "finish") Require(adoption.FinishProcessingWork(source), "race_finish_failed");
                        else if (mutation == "dispose") Require(Task.Run(() => adoption.Dispose()).Wait(2000), "disposal_deadlock");
                        else throw new InvalidOperationException("unknown_mutation:" + mutation);
                    }
                    catch (Exception failure) { error = failure; }
                };
                try
                {
                    bool rejected;
                    if (when == "read")
                    {
                        rejected = false;
                        try { source.Read(new byte[32768], 0, 32768); } catch (IOException) { rejected = true; }
                    }
                    else rejected = !source.CanRead;
                    if (error != null) throw new InvalidOperationException("injection_failed:" + mode, error);
                    Require(injected && rejected && legacy == 0, "fault_admitted_or_legacy_fallback:" + mode);
                    foreach (var handle in handles) Require(handle.IsClosed, "native_handle_leaked:" + mode);
                    if (restoreView != null) Flip(restoreView, 64);
                    Require(!source.CanRead, "retirement_not_sticky");
                }
                finally
                {
                    fault = null;
                    if (originalParent != null) originalParent.Dispose();
                    if (exited != null) exited.Dispose();
                    if (restoreView != null) restoreView.Dispose();
                    if (restoreMap != null) restoreMap.Dispose();
                }
            }
            Console.WriteLine("processing-custody=" + mode + "; scopes=" + scopes);
        }
    }
}
#endif
