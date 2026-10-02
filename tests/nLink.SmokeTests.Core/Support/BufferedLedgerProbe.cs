#if NLINK_TEST_BUFFERED_LEDGER
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using NLink.Tools;
using NLink.Tools.FrontierLedger;

namespace NLink.Tests
{
    public static class BufferedLedgerProbe
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static Action<string> fault;
        private static int fullChecks;
        private static FileStream firstOutput;
        public static void Boundary(string name, object value = null)
        {
            if (name == "output-duplicate-0") firstOutput = (FileStream)value;
            if (name == "full") fullChecks++;
            if (fault != null) fault(name);
        }
        private static void Require(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException(reason); }
        private static void Rejected(Action action)
        {
            bool rejected = false;
            try { action(); } catch (IOException) { rejected = true; } catch (InvalidDataException) { rejected = true; }
            catch (ObjectDisposedException) { rejected = true; }
            Require(rejected, "buffered_failure_admitted");
        }
        private static void Expire(Stopwatch work, int seconds)
        {
            foreach (var field in typeof(Stopwatch).GetFields(Flags))
                if (field.Name.TrimStart('_') == "elapsed") { field.SetValue(work, Stopwatch.Frequency * seconds); return; }
            throw new InvalidOperationException("work_clock_field_missing");
        }
        public static void CheckGenericCursorBudget()
        {
            foreach (bool cursor in new[] { false, true })
                using (var source = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("first\nsecond\n"), false))
                using (var input = new GuiSmokeLedgerStreamRead(source, cursor))
                {
                    Require(input.ReadLine() == "first", "generic_first_line_missing");
                    Expire((Stopwatch)typeof(GuiSmokeLedgerStreamRead).GetField("clock", Flags).GetValue(input), 6);
                    Rejected(() => input.ReadLine());
                }
        }
        public static void Run(LocalGuiWriterRequest.FrontierFinalizerBinding binding, object rawAdoption,
            string name, string directory, string sourcePath, string mode)
        {
            var adoption = (LocalFrontierFileAdoption)rawAdoption;
            var work = (Stopwatch)typeof(LocalFrontierFileAdoption).GetField("work", Flags).GetValue(adoption);
            var lifetime = (CancellationTokenSource)typeof(LocalFrontierFileAdoption).GetField("processingLifetime", Flags).GetValue(adoption);
            using (var closed = EventWaitHandle.OpenExisting(name + ".closed"))
            using (var source = binding.TryOpenSource())
            {
                Require(source != null, "original_source_missing");
                if (mode.StartsWith("custody-", StringComparison.Ordinal))
                { ProcessingCustodyProbe.Run(binding, adoption, source, name, mode.Substring(8)); return; }
                if (mode == "before-begin")
                { Rejected(() => { using (var input = new GuiSmokeLedgerStreamRead(source)) { } }); return; }
                Require(source.TryBeginProcessingWork(), "processing_not_started");
                if (mode == "copied-reader")
                {
                    var copy = (Stream)typeof(object).GetMethod("MemberwiseClone", Flags).Invoke(source, null);
                    Rejected(() => { using (var input = new GuiSmokeLedgerStreamRead(copy)) { } });
                    copy.Dispose(); Require(source.IsCurrent, "copy_retired_original"); return;
                }
                fullChecks = 0;
                if (mode == "cursor-local-budget" || mode == "cursor-cumulative-budget" || mode == "cursor-cancel-budget")
                {
                    using (var input = new GuiSmokeLedgerStreamRead(source, true))
                    {
                        Require(input.ReadLine() != null, "cursor_budget_first_line_missing");
                        var local = (Stopwatch)typeof(GuiSmokeLedgerStreamRead).GetField("clock", Flags).GetValue(input);
                        Expire(local, 6);
                        Require(input.ReadLine() != null && source.IsCurrent, "owned_cursor_rejected_extended_allowance");
                        if (mode == "cursor-local-budget") Expire(local, 11);
                        else if (mode == "cursor-cumulative-budget")
                        {
                            Expire(work, 11);
                            Require(input.ReadLine() != null && source.IsCurrent, "owned_cursor_rejected_shared_allowance");
                            Expire(work, 16);
                        }
                        else lifetime.Cancel();
                        Rejected(() => input.ReadLine());
                        Require(!source.IsCurrent, "cursor_budget_invalid_original_not_retired");
                    }
                    return;
                }
                if (mode == "native-buffered" || mode == "cursor-buffered" || mode == "budget-buffered" ||
                    mode == "local-budget-buffered" || mode == "cancel-buffered" || mode == "generic-wrapper")
                {
                    if (mode == "budget-buffered") Expire(work, 6);
                    Stream selected = mode == "generic-wrapper" ? (Stream)new BorrowedStream(source) : source;
                    using (var input = new GuiSmokeLedgerStreamRead(selected, mode == "cursor-buffered"))
                    {
                        Require(input.ReadLine() != null, "first_line_missing");
                        int before = fullChecks;
                        if (mode == "budget-buffered") Expire(work, 16);
                        else if (mode == "local-budget-buffered") Expire((Stopwatch)typeof(GuiSmokeLedgerStreamRead).GetField("clock", Flags).GetValue(input), 6);
                        else if (mode == "cancel-buffered") lifetime.Cancel();
                        else closed.Set();
                        if (mode == "budget-buffered" || mode == "local-budget-buffered" || mode == "cancel-buffered" || mode == "generic-wrapper")
                            Rejected(() => input.ReadLine());
                        else
                        {
                            Require(input.ReadLine() != null, "buffered_line_missing");
                            for (int i = 0; i < 512; i++) input.AddOrigin(0, 1);
                            Require(fullChecks == before, "buffered_work_repeated_full_ownership");
                            Rejected(input.CheckCurrent);
                        }
                        Require(!source.IsCurrent, "invalid_original_not_retired");
                    }
                    return;
                }
                var lines = File.ReadAllLines(sourcePath); // Expected text only, never source custody.
                bool injected = false;
                fault = boundary =>
                {
                    var target = mode == "budget-eof" ? "parse-complete" : mode == "cancel-duplicate" ? "output-duplicate-0" :
                        mode == "owner-capture" ? "capture-created" : mode == "budget-return" || mode == "output-return" ? "before-return" : "unused";
                    if (boundary != target || injected) return;
                    injected = true;
                    if (mode == "owner-capture") closed.Set();
                    else if (mode == "cancel-duplicate") lifetime.Cancel();
                    else if (mode == "output-return") firstOutput.Dispose();
                    else Expire(work, 16);
                };
                GuiSmokeLedgerStreamingResult result = null;
                try
                {
                    Action process = () => result = GuiSmokeLogLedgerStreamingProcessor.ProcessFromStreamWithRetainedOutputs(
                        source, lines.Length, 0, Path.Combine(directory, "buffered-retained.tmp"), Path.Combine(directory, "buffered-writer.tmp"),
                        "frontier-gui-run", "frontier-gui-scenario", new string[0], Path.Combine(directory, "buffered-semantic.tmp"),
                        new[] { "operational_log_writer_drained" }, source.ProcessingLifetime);
                    if (mode == "positive")
                    {
                        process(); Require(result.StreamCapture.IsCurrent && result.StreamCapture.HasRetainedOutputs, "capture_missing");
                        Require(result.SourceLineVisitCount == lines.Length && source.ReachedEnd, "parse_incomplete");
                        Require(result.RetainedLineCount == lines.Length, "retained_lines_changed");
                        using (var expected = new StreamReader(new FileStream(result.StreamCapture.Retained.CreationPath,
                            FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
                            Require(expected.ReadToEnd() == String.Join(Environment.NewLine, lines), "retained_bytes_changed");
                        Require(binding.TryMatchProcessedWriters(result) != null && source.TryFinishProcessingWork(), "original_writer_join_failed");
                        Require(!result.StreamCapture.LiveProofEligible && !result.StreamCapture.PublicationCompletionIssued, "authority_changed");
                    }
                    else
                    {
                        Rejected(process);
                        Require(injected && result == null && !source.IsCurrent, "boundary_did_not_fail_closed");
                    }
                }
                finally { fault = null; if (result != null) result.StreamCapture.Dispose(); }
                foreach (var path in Directory.GetFiles(directory, "buffered-*.tmp"))
                    using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                Console.WriteLine("buffered-ledger=" + mode + "; full_checks=" + fullChecks);
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
