#if NLINK_TEST_BUFFERED_LEDGER
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using NLink.Tools;

namespace NLink.Tests
{
    public static class RecoveryBufferProbe
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static Action<string, object> fault;
        private static int fullChecks, lines, markerEnds, bufferedLines, eofCount, readPasses;
        public static void Boundary(string name, object value = null)
        {
            if (name == "full") fullChecks++;
            if (name == "line") { lines++; if ((bool)value) bufferedLines++; }
            if (name == "line-end") markerEnds++;
            if (name == "eof") eofCount++;
            if (name == "before-read") readPasses++;
            if (fault != null) fault(name, value);
        }
        private static void Require(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException(reason); }
        private static object Field(object target, string name)
        { return target.GetType().GetField(name, Flags).GetValue(target); }
        private static object Copy(object value)
        { return typeof(object).GetMethod("MemberwiseClone", Flags).Invoke(value, null); }
        private static void Expire(Stopwatch work)
        {
            foreach (var field in typeof(Stopwatch).GetFields(Flags))
                if (field.Name.TrimStart('_') == "elapsed") { field.SetValue(work, Stopwatch.Frequency * 11); return; }
            throw new InvalidOperationException("work_clock_field_missing");
        }
        public static void Run(LocalGuiWriterRequest.FrontierFinalizerBinding binding, object adoption, string name, string mode)
        {
            var work = (Stopwatch)Field(adoption, "work");
            var lifetime = (CancellationTokenSource)Field(adoption, "processingLifetime");
            fullChecks = lines = markerEnds = bufferedLines = eofCount = readPasses = 0;
            using (var closed = EventWaitHandle.OpenExisting(name + ".closed"))
            using (var source = binding.TryOpenSource())
            {
                Require(source != null && source.TryBeginProcessingWork(), "recovery_buffer_work_missing");
                ReadOnlyCollection<LocalGuiWriterRequest.FrontierRecoveryRead> readers = null;
                LocalGuiWriterRequest.FrontierRecoveryMarkers provisional = null;
                bool injected = false, reachedBlock = false;
                int atFault = -1, atLine = -1;
                string target = "unused", mutation = "unused";
                int split = mode.LastIndexOf('-');
                if (split >= 0) { target = mode.Substring(0, split); mutation = mode.Substring(split + 1); }
                fault = (boundary, value) =>
                {
                    if (boundary == "readers") readers = (ReadOnlyCollection<LocalGuiWriterRequest.FrontierRecoveryRead>)value;
                    if (boundary == "before-return") provisional = (LocalGuiWriterRequest.FrontierRecoveryMarkers)value;
                    if (boundary == "line") atLine = fullChecks;
                    if (boundary == "line-end") Require(fullChecks == atLine, "native_marker_repeated_full_check");
                    if (injected && (target == "line" || target == "line-end") && boundary == "buffer-complete")
                    { Require(fullChecks == atFault, "buffered_work_repeated_full_check"); reachedBlock = true; }
                    if (boundary != target || injected) return;
                    injected = true; atFault = fullChecks;
                    if (mutation == "owner") closed.Set();
                    else if (mutation == "cancel") lifetime.Cancel();
                    else if (mutation == "budget") Expire(work);
                    else if (mutation == "stopped") work.Stop();
                    else if (mutation == "source") source.Dispose();
                    else if (mutation == "reader") readers[0].Dispose();
                    else throw new InvalidOperationException("unknown_recovery_mutation");
                };
                try
                {
                    if (mode == "foreign" || mode == "wrapped" || mode == "copied-source" || mode == "copied-recovery")
                    {
                        fault = null;
                        readers = binding.TryOpenRecoveryOutputs();
                        Require(readers != null, "primitive_readers_missing");
                        var check = (Action)Delegate.CreateDelegate(typeof(Action), binding,
                            binding.GetType().GetMethod("CheckRecoveryWork", Flags));
                        var selectedSource = mode == "copied-source" ? (LocalGuiWriterRequest.FrontierSourceRead)Copy(source) : source;
                        var scan = new FrontierRecoveryMarkerScan(check, selectedSource, readers);
                        Stream selected = mode == "foreign" ? (Stream)new MemoryStream(Encoding.UTF8.GetBytes("a\nb\nc\n"), false) :
                            mode == "wrapped" ? (Stream)new BorrowedStream(readers[1]) :
                            mode == "copied-recovery" ? (Stream)Copy(readers[1]) : readers[1];
                        bool rejected = false;
                        try { scan.Read(selected, 1, "frontier-gui-run", "frontier-gui-scenario"); }
                        catch (IOException) { rejected = true; }
                        catch (InvalidDataException) { rejected = true; }
                        if (mode == "copied-source" || mode == "copied-recovery")
                        {
                            Require(rejected, "copied_reader_admitted");
                            if (mode == "copied-source") selectedSource.Dispose(); else selected.Dispose();
                            Require(source.IsCurrent, "copy_retired_original");
                        }
                        else
                        {
                            Require(!rejected && lines > 0 && bufferedLines == 0, "generic_stream_used_buffered_guard");
                            Require(fullChecks == 2 + 3 * readPasses + lines + markerEnds, "generic_full_line_checks_missing");
                            selected.Dispose();
                        }
                    }
                    else
                    {
                        var result = binding.TryCaptureRecoveryMarkers();
                        if (mode == "native" || mode == "no-markers")
                        {
                            Require(result != null && result.IsCurrent && eofCount == 4 && lines == bufferedLines && lines > 0,
                                "original_four_stream_scan_missing");
                            Require(result.SourceLineVisitCount == lines && result.Markers.Count == (mode == "native" ? 4 : 0),
                                "marker_facts_changed");
                            Require(markerEnds == result.Markers.Count && fullChecks == 11 + 3 * readPasses +
                                result.DeferredBatchKeys.Count + result.Markers.Count,
                                "full_boundary_count_changed");
                            Require(!result.LiveProofEligible && !result.NativeWriterCustodyEstablished && !result.PublicationCompletionIssued,
                                "marker_authority_added");
                        }
                        else
                        {
                            Require(injected && result == null && !binding.IsCurrent && binding.RecoveryMatch == null,
                                "invalid_recovery_facts_escaped");
                            if (target == "line" || target == "line-end")
                            {
                                Require(reachedBlock == (mutation == "owner"), "buffered_rejection_boundary_changed");
                                Require(fullChecks == atFault + (mutation == "owner" ? 1 : 0), "unexpected_buffered_full_checks");
                            }
                            closed.Reset();
                            Require(!binding.IsCurrent && binding.TryCaptureRecoveryMarkers() == null, "retired_recovery_revived");
                        }
                    }
                }
                finally { fault = null; binding.Dispose(); }
                Require(!source.CanRead && (provisional == null || !provisional.IsCurrent), "retired_facts_current");
                if (readers != null) foreach (var reader in readers) Require(!reader.CanRead, "recovery_reader_retained");
                Require(binding.RecoveryMatch == null, "recovery_match_escaped");
                Console.WriteLine("recovery-buffer=" + mode + "; full_checks=" + fullChecks + "; lines=" + lines +
                    "; buffered_lines=" + bufferedLines + "; marker_ends=" + markerEnds + "; eof=" + eofCount);
            }
        }
        private sealed class BorrowedStream : Stream
        {
            private readonly Stream source;
            internal BorrowedStream(Stream source) { this.source = source; }
            public override bool CanRead { get { return source.CanRead; } }
            public override bool CanWrite { get { return false; } }
            public override bool CanSeek { get { return false; } }
            public override long Length { get { return source.Length; } }
            public override long Position { get { return source.Position; } set { throw new NotSupportedException(); } }
            public override int Read(byte[] buffer, int offset, int count) { return source.Read(buffer, offset, count); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }
    }
}
#endif
