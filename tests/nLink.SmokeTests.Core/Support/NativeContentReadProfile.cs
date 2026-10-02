#if NLINK_TEST_NATIVE_READ_PROFILE
using System;
using System.Diagnostics;
using System.Text;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        public sealed partial class FrontierWorkerInputs
        {
            internal string ContentReadSnapshotForTest()
            {
                // Passive fields only. Never evaluate currentness or authority.
                var text = new StringBuilder("work_ticks=" + work.ElapsedTicks + "; work_running=" + work.IsRunning +
                    "; read_calls=" + PrimaryReadCallsForTest + "; read_ticks=" + PrimaryReadWorkTicksForTest +
                    "; mapped_calls=" + (nativeStream == null ? 0 : nativeStream.ReadCallsForTest) +
                    "; mapped_ticks=" + (nativeStream == null ? 0 : nativeStream.ReadTicksForTest) +
                    "; source_lines=" + sourceLines.Count + "; source_units=" + sourceUnits +
                    "; source_started=" + sourceStarted + "; source_ended=" + sourceEnded +
                    "; rejected=" + PrimarySourceRejectionForTest + "; close=" + PrimarySourceCloseForTest +
                    "; exception=" + PrimarySourceExceptionForTest);
                text.Append("; check_calls=");
                for (int i = 0; i < 12; i++) text.Append((i == 0 ? "" : ",") + PrimaryCheckCallsForTest[i]);
                text.Append("; check_ticks=");
                for (int i = 0; i < 12; i++) text.Append((i == 0 ? "" : ",") + PrimaryCheckTicksForTest[i]);
                return text.ToString();
            }
        }
    }
}

namespace NLink.Tests
{
    public static class NativeContentReadProfileForTest
    {
        private static NLink.Tools.LocalRetainedSourceRequest.FrontierWorkerInputs inputs;
        private static long started, parserStart, parserTicks, parserStarted, parserReturned;
        private static int records;
        private static string nativeStage = "none";
        public static void Begin(NLink.Tools.LocalRetainedSourceRequest.FrontierWorkerInputs owned)
        {
            if (inputs != null || owned == null || owned.SourceReadProbeForTest != null) throw new InvalidOperationException("content_read_profile_replay");
            inputs = owned; started = Stopwatch.GetTimestamp();
            owned.SourceReadProbeForTest = stage =>
            {
                nativeStage = stage;
                if (stage == "entry" && Sample(owned.PrimaryReadCallsForTest)) Observe("native_read_entry");
            };
        }
        private static bool Sample(long ordinal) { return ordinal == 1 || ordinal == 256 || ordinal == 4096 || (ordinal > 0 && (ordinal & 16383) == 0); }
        public static void Observe(string phase)
        {
            if (inputs == null || records >= 32) return;
            records++;
            Console.Out.WriteLine("content-native-read-profile=diagnostic_only=True; sequence=" + records +
                "; phase=" + phase + "; elapsed_ticks=" + (Stopwatch.GetTimestamp() - started) + "; frequency=" + Stopwatch.Frequency +
                "; native_stage=" + nativeStage + "; parser_started=" + parserStarted + "; parser_returned=" + parserReturned +
                "; parser_ticks=" + parserTicks + "; " + inputs.ContentReadSnapshotForTest());
            Console.Out.Flush();
        }
        public static void ParserStart(long ordinal)
        {
            parserStarted++;
            if (Sample(ordinal)) Observe("parser_started");
            parserStart = Stopwatch.GetTimestamp();
        }
        public static void ParserReturned(long ordinal)
        {
            parserTicks += Stopwatch.GetTimestamp() - parserStart; parserReturned++;
            if (Sample(ordinal)) Observe("parser_returned");
        }
    }
}
#endif
