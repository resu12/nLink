#if NLINK_TEST_TERMINAL_REQUEST && NLINK_FRONTIER_LEDGER
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using NLink.Tools.FrontierLedger;

namespace NLink.Tests
{
    public sealed class TerminalCaptureTimingObservation
    {
        public string Mode;
        public long ProcessingMilliseconds, BeforeCaptureMilliseconds = -1, AfterCaptureMilliseconds = -1;
        public bool CaptureAccepted, ProcessingExpired, FilesReleased;
        public int RejectedOwnershipControls, ReadDelayMilliseconds, MetadataDelayMilliseconds;
    }

    public static class TerminalCaptureTimingProbe
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static object Copy(object value)
        { return typeof(object).GetMethod("MemberwiseClone", Private).Invoke(value, null); }
        private static void Require(bool value, string message)
        { if (!value) throw new InvalidDataException(message); }

        public static TerminalCaptureTimingObservation Run(string directory, string mode)
        {
            Directory.CreateDirectory(directory);
            var observation = new TerminalCaptureTimingObservation { Mode = mode };
            var text = new StringBuilder();
            for (int i = 1; i <= 3; i++)
                text.Append("event=" + (i == 1 ? "operational_log_writer_started" : "selected") +
                    "; acceptance_run_id=run; scenario_run_id=scenario; log_writer_id=writer-a; log_writer_sequence=" + i +
                    "; log_writer_batch_generation=" + i + "; log_writer_batch_first_sequence=" + i +
                    "; log_writer_batch_last_sequence=" + i + "; log_writer_attempt_utc_ticks=" + i + "\n");
            var bytes = Encoding.UTF8.GetBytes(text.ToString());
            var input = new DelayedReadStream(bytes, mode == "pre-metadata" ? 350 : mode == "processing-expired" ? 5100 : 0,
                mode == "inside-metadata" ? 350 : 0);
            var paths = new[] { Path.Combine(directory, "retained.log"), Path.Combine(directory, "writer.log"), Path.Combine(directory, "semantic.log") };
            GuiSmokeLedgerStreamingResult result = null;
            try
            {
                var processing = Stopwatch.StartNew();
                try
                {
                    result = GuiSmokeLogLedgerStreamingProcessor.ProcessFromStreamWithRetainedOutputs(input, 3, 0,
                        paths[0], paths[1], "run", "scenario", new string[0], paths[2], new[] { "selected" }, CancellationToken.None);
                }
                catch (InvalidDataException error)
                {
                    if (mode != "processing-expired" || error.Message != "stream_source_not_current") throw;
                    observation.ProcessingExpired = true;
                }
                observation.ProcessingMilliseconds = processing.ElapsedMilliseconds;
                observation.ReadDelayMilliseconds = input.ReadDelayApplied;
                observation.MetadataDelayMilliseconds = input.MetadataDelayApplied;
                if (mode == "processing-expired")
                {
                    Require(observation.ProcessingExpired && result == null && observation.ProcessingMilliseconds > 5000,
                        "processing_timeout_not_preserved");
                }
                else
                {
                    Require(result != null && result.NativeCapture == null, "capture_published_before_consumption");
                    var capture = result.StreamCapture;
                    var sourcePath = Path.Combine(directory, "source.log");
                    if (mode == "ownership")
                    {
                        var copy = (GuiSmokeLedgerStreamCapture)Copy(capture);
                        var copiedResult = (GuiSmokeLedgerStreamingResult)Copy(result);
                        using (var foreignSource = new MemoryStream(bytes, false))
                        {
                            Reject(copy, result, input, sourcePath, observation);
                            Reject(capture, copiedResult, input, sourcePath, observation);
                            Reject(capture, new GuiSmokeLedgerStreamingResult(), input, sourcePath, observation);
                            Reject(capture, result, foreignSource, sourcePath, observation);
                        }
                        Require(!copy.BindTerminalCaptureClockForTest(result, Stopwatch.StartNew()), "copied_clock_binding");
                        Require(!capture.BindTerminalCaptureClockForTest(copiedResult, Stopwatch.StartNew()), "foreign_clock_binding");
                        Require(!capture.BindTerminalCaptureClockForTest(result, Stopwatch.StartNew()), "clock_rebound");
                        observation.RejectedOwnershipControls += 3;
                        copy.Dispose();
                        Require(capture.IsCurrent, "copy_retired_original_clock");
                    }
                    if (mode == "missing-clock")
                        typeof(GuiSmokeLedgerStreamCapture).GetField("terminalCaptureClock", Private).SetValue(capture, null);
                    if (mode == "cleanup") capture.Dispose();
                    if (mode == "factory-rejection") result.WriterGapCount = 1;
                    if (mode == "post-return-expiry") Thread.Sleep(350);
                    observation.CaptureAccepted = capture.CaptureTerminalNativeForTest(result, input, sourcePath,
                        out observation.BeforeCaptureMilliseconds, out observation.AfterCaptureMilliseconds);
                    bool expected = mode == "pre-metadata" || mode == "ownership";
                    Require(observation.CaptureAccepted == expected, "wrong_capture_eligibility:" + mode +
                        "; before_ms=" + observation.BeforeCaptureMilliseconds + "; after_ms=" + observation.AfterCaptureMilliseconds);
                    if (expected) Require(result.OwnsNativeCapture(result.NativeCapture), "original_capture_not_owned");
                    else Require(result.NativeCapture == null, "rejected_capture_published");
                    var firstCapture = result.NativeCapture;
                    if (mode == "factory-rejection") result.WriterGapCount = 0;
                    Reject(capture, result, input, sourcePath, observation);
                    Require(Object.ReferenceEquals(firstCapture, result.NativeCapture), "repeated_factory_changed_capture");
                    Require(!capture.BindTerminalCaptureClockForTest(result, Stopwatch.StartNew()), "consumed_clock_renewed");
                    capture.Dispose();
                    Reject(capture, result, input, sourcePath, observation);
                    Require(!capture.IsCurrent, "closed_timing_owner_current");
                }
            }
            finally
            {
                if (result != null && result.StreamCapture != null) result.StreamCapture.Dispose();
                input.Dispose();
            }
            foreach (string path in paths)
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            observation.FilesReleased = true;
            return observation;
        }

        private static void Reject(GuiSmokeLedgerStreamCapture capture, GuiSmokeLedgerStreamingResult result,
            Stream input, string source, TerminalCaptureTimingObservation observation)
        {
            long before, after;
            Require(!capture.CaptureTerminalNativeForTest(result, input, source, out before, out after) && before == -1 && after == -1,
                "invalid_or_repeated_owner_reached_factory");
            observation.RejectedOwnershipControls++;
        }

        private sealed class DelayedReadStream : MemoryStream
        {
            private int readDelay, metadataDelay;
            public int ReadDelayApplied, MetadataDelayApplied;
            internal DelayedReadStream(byte[] bytes, int readDelay, int metadataDelay) : base(bytes, false)
            { this.readDelay = readDelay; this.metadataDelay = metadataDelay; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (readDelay != 0) { int delay = readDelay; readDelay = 0; Thread.Sleep(delay); ReadDelayApplied = delay; }
                return base.Read(buffer, offset, count);
            }
            public override long Position
            {
                get
                {
                    // ProcessCore reads EOF position after native metadata capture
                    // but before constructing/returning its stream capture.
                    if (metadataDelay != 0 && base.Position == Length)
                    { int delay = metadataDelay; metadataDelay = 0; Thread.Sleep(delay); MetadataDelayApplied = delay; }
                    return base.Position;
                }
                set { base.Position = value; }
            }
        }
    }
}
#endif
