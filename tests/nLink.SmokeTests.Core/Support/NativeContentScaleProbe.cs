#if NLINK_TEST_NATIVE_CONTENT_ACQUISITION
using System;
using System.Diagnostics;
using System.IO;
using NLink.Tools.FrontierLedger;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class FrontierFinalizerBinding
        {
            public string ContentScaleStageForTest { get; private set; }
            public string ContentScaleErrorForTest { get; private set; }
            public long ContentScaleParseMillisecondsForTest { get; private set; }

            // Offline rejection probe: the same original processing operations,
            // source and limits as TryProcessObservedLedger. The extra stopwatch
            // measures a stage; it supplies no work allowance or success token.
            public GuiSmokeLedgerStreamingResult TryProcessObservedLedgerForContentScaleTest(string[] semanticEventNames)
            {
                lock (sourceSync)
                {
                    if (!IsCurrent) return null;
                    bool success = false; GuiSmokeLedgerStreamingResult result = null;
                    Stopwatch parse = null;
                    try
                    {
                        if (semanticEventNames != null && semanticEventNames.Length > GuiSmokeLogLedgerStreamingProcessor.MaximumSemanticEventNames) return null;
                        var events = semanticEventNames == null ? new string[0] : (string[])semanticEventNames.Clone();
                        ContentScaleStageForTest = "source_open";
                        var source = TryOpenSource();
                        if (source == null || !source.TryBeginProcessingWork()) return null;
                        ContentScaleStageForTest = "final_ledger_capture";
                        var final = TryCaptureLedgerFinal();
                        ContentScaleStageForTest = "recovery_capture";
                        var markers = TryCaptureRecoveryMarkers();
                        if (final == null || markers == null) return null;
                        string prefix = Path.Combine(finalizer.root, "frontier-cursor-" + Guid.NewGuid().ToString("N"));
                        var keys = new string[markers.DeferredBatchKeys.Count]; markers.DeferredBatchKeys.CopyTo(keys, 0);
                        ContentScaleStageForTest = "owned_cursor_parse";
                        NLink.Tests.NativeContentParserProfileForTest.Begin();
                        parse = Stopwatch.StartNew();
                        result = GuiSmokeLogLedgerStreamingProcessor.ProcessOwnedCursor(source, final.LineCount, final.Bookmark,
                            prefix + "-retained.tmp", prefix + "-writer.tmp", finalizer.run, finalizer.scenario,
                            keys, prefix + "-semantic.tmp", events, source.ProcessingLifetime);
                        parse.Stop();
                        ContentScaleStageForTest = "writer_recovery_cursor_match";
                        if (TryMatchProcessedWriters(result) == null || TryMatchRecovery(result) == null ||
                            TryMatchProcessedCursor(result) == null || !source.TryFinishProcessingWork()) return null;
                        success = OwnsCursorMatch(cursorMatch); return success ? result : null;
                    }
                    catch (Exception error)
                    {
                        ContentScaleErrorForTest = error is InvalidDataException && error.Message == "stream_source_not_current"
                            ? "stream_source_not_current" : error.GetType().Name;
                        return null;
                    }
                    finally
                    {
                        if (parse != null) { parse.Stop(); ContentScaleParseMillisecondsForTest = parse.ElapsedMilliseconds; }
                        NLink.Tests.NativeContentParserProfileForTest.End();
                        if (!success)
                        {
                            if (result != null && result.StreamCapture != null) result.StreamCapture.Dispose();
                            Dispose();
                        }
                    }
                }
            }
        }
    }
}

namespace NLink.Tests
{
    // Passive diagnostics for the original copy interval. No custody inputs.
    public static class NativeSemanticCopyProbeForTest
    {
        private static int thread;
        private static Stopwatch clock;
        public static string RejectedPredicate { get; private set; }
        public static long RejectedCumulativeMilliseconds { get; private set; }
        public static long RejectedCopyMilliseconds { get; private set; }
        public static long CopiedBytes { get; private set; }
        public static long ExpectedBytes { get; private set; }
        public static long CopyMilliseconds { get; private set; }
        public static bool Completed { get; private set; }
        public static Action FaultAfterFirstChunkForTest;
        private static bool Active { get { return thread != 0 && thread == System.Threading.Thread.CurrentThread.ManagedThreadId; } }
        public static void Begin()
        {
            RejectedPredicate = null; RejectedCumulativeMilliseconds = RejectedCopyMilliseconds = 0;
            CopiedBytes = ExpectedBytes = CopyMilliseconds = 0; Completed = false;
            clock = Stopwatch.StartNew(); thread = System.Threading.Thread.CurrentThread.ManagedThreadId;
        }
        public static bool CheckBudget(string name, long milliseconds, int maximum)
        {
            bool result = milliseconds <= maximum;
            if (!result && Active && RejectedPredicate == null)
            {
                RejectedPredicate = name; RejectedCumulativeMilliseconds = milliseconds;
                RejectedCopyMilliseconds = clock.ElapsedMilliseconds;
            }
            return result;
        }
        public static void Progress(long bytes, long expected)
        {
            if (!Active) return;
            CopiedBytes = bytes; ExpectedBytes = expected;
            var fault = FaultAfterFirstChunkForTest; FaultAfterFirstChunkForTest = null;
            if (fault != null) fault();
        }
        public static void End(bool completed)
        {
            if (Active) { clock.Stop(); CopyMilliseconds = clock.ElapsedMilliseconds; Completed = completed; thread = 0; }
            FaultAfterFirstChunkForTest = null;
        }
    }

    // Passive samples only. No source checks, budget reset or success decision.
    public static class NativeContentParserProfileForTest
    {
        private static bool active, sampled;
        private static long started;
        private static readonly long[] ticks = new long[6], samples = new long[6];
        private static readonly long[] readerTicks = new long[25], readerSamples = new long[25];
        public static string RejectedPredicate { get; private set; }
        public static long RejectedLocalMilliseconds { get; private set; }
        public static long RowsStarted { get; private set; }
        public static long[] StageTicks { get { return (long[])ticks.Clone(); } }
        public static long[] StageSamples { get { return (long[])samples.Clone(); } }
        public static long[] ReaderTicks { get { return (long[])readerTicks.Clone(); } }
        public static long[] ReaderSamples { get { return (long[])readerSamples.Clone(); } }
        public static void Begin()
        {
            active = true; sampled = false; RowsStarted = 0; RejectedPredicate = null; RejectedLocalMilliseconds = 0;
            Array.Clear(ticks, 0, ticks.Length); Array.Clear(samples, 0, samples.Length);
            Array.Clear(readerTicks, 0, readerTicks.Length); Array.Clear(readerSamples, 0, readerSamples.Length);
        }
        public static void End() { active = sampled = false; }
        public static void BeginRow(long index)
        {
            if (!active) return;
            RowsStarted++; sampled = (index & 127) == 0;
            if (sampled) started = Stopwatch.GetTimestamp();
        }
        public static void Stage(int stage)
        {
            if (!active || !sampled) return;
            long now = Stopwatch.GetTimestamp(); ticks[stage] += now - started; samples[stage]++; started = now;
        }
        public static void Reject(string predicate, long milliseconds)
        {
            if (!active || RejectedPredicate != null) return;
            RejectedPredicate = predicate; RejectedLocalMilliseconds = milliseconds;
        }
        public static long ReaderStart() { return active && sampled ? Stopwatch.GetTimestamp() : 0; }
        public static void ReaderStage(int stage, long start)
        {
            if (start == 0) return;
            readerTicks[stage] += Stopwatch.GetTimestamp() - start; readerSamples[stage]++;
        }
        public static bool Check(int stage, Func<bool> check)
        {
            long start = ReaderStart(); bool result = check(); ReaderStage(stage, start); return result;
        }
    }
}
#endif
