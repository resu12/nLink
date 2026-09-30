#if NLINK_TEST_TERMINAL_REQUEST && NLINK_FRONTIER_LEDGER
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace NLink.Tools.FrontierLedger
{
    public sealed partial class GuiSmokeLedgerStreamCapture
    {
        private Stopwatch terminalCaptureClock;
        private int terminalClockBound, terminalCaptureAttempted;

        // Only the processor supplies its original metadata clock. No consumer
        // receives the mutable clock, and even a missing clock cannot be rebound.
        internal bool BindTerminalCaptureClockForTest(GuiSmokeLedgerStreamingResult result, Stopwatch clock)
        {
            if (!Object.ReferenceEquals(original, this) || !Object.ReferenceEquals(owner, result) || !IsCurrent ||
                Interlocked.CompareExchange(ref terminalClockBound, 1, 0) != 0) return false;
            terminalCaptureClock = clock;
            return clock != null && clock.IsRunning;
        }

        internal bool CaptureTerminalNativeForTest(GuiSmokeLedgerStreamingResult result, Stream expectedSource,
            string sourcePath, out long beforeMilliseconds, out long afterMilliseconds)
        {
            beforeMilliseconds = afterMilliseconds = -1;
            if (!Object.ReferenceEquals(original, this) || !Object.ReferenceEquals(owner, result) ||
                !Object.ReferenceEquals(source, expectedSource) || !IsCurrent || terminalCaptureClock == null ||
                !terminalCaptureClock.IsRunning || Interlocked.CompareExchange(ref terminalCaptureAttempted, 1, 0) != 0)
                return false;
            beforeMilliseconds = terminalCaptureClock.ElapsedMilliseconds;
            try
            {
                owner.CaptureNativeOutputs(sourcePath, AcceptanceRunId, ScenarioRunId, SourceLines, Bookmark,
                    Retained, Writer, Semantic, terminalCaptureClock);
                return IsCurrent && owner.OwnsNativeCapture(owner.NativeCapture);
            }
            finally { afterMilliseconds = terminalCaptureClock.ElapsedMilliseconds; }
        }
    }
}
#endif
