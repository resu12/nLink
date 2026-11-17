#if NLINK_TEST_NATIVE_SELECTION_PROFILE
using System;
using System.Diagnostics;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        public sealed partial class FrontierWorkerInputs
        {
            internal long ContentSelectionWorkTicksForTest { get { return work.ElapsedTicks; } }
            internal string ContentSelectionSnapshotForTest()
            {
                // Passive fields only, not currentness or custody decisions.
                return "work_ticks=" + work.ElapsedTicks + "; work_running=" + work.IsRunning +
                    "; evaluation_visits=" + evaluationVisits + "; source_units=" + sourceUnits +
                    "; source_lines=" + sourceLines.Count + "; source_ended=" + sourceEnded + "; closed=" + closed
#if NLINK_TEST_NATIVE_SELECTION_COMPONENT_PROFILE
                    + NLink.Tests.NativeContentSelectionProfileForTest.ComponentFields()
#endif
                    ;
            }
            internal bool ContentSelectionResourceCurrentForTest(int maximumMilliseconds)
            {
                long started = NLink.Tests.NativeContentSelectionProfileForTest.ResourceStarted();
                try { return ResourceCurrentWithin(maximumMilliseconds); }
                finally { NLink.Tests.NativeContentSelectionProfileForTest.ResourceReturned(started); }
            }
#if NLINK_TEST_NATIVE_SELECTION_COMPONENT_PROFILE
            private bool ContentSelectionComponentCurrentForTest(int component)
            {
                long started = NLink.Tests.NativeContentSelectionProfileForTest.ComponentStarted(this, component);
                try
                {
                    switch (component)
                    {
                        case 0: return owner.FrontierBaseCurrent;
                        case 1: return scope.IsCurrent;
                        case 2: return files == null || files.IsCurrent;
                        default: return pins == null || pins.IsCurrent;
                    }
                }
                finally { NLink.Tests.NativeContentSelectionProfileForTest.ComponentReturned(component, started); }
            }
#endif
        }
    }
}

namespace NLink.Tests
{
    public static class NativeContentSelectionProfileForTest
    {
        private static NLink.Tools.LocalRetainedSourceRequest.FrontierWorkerInputs inputs;
        private static long entryWorkTicks, started, scanCalls, scanTicks, scanCompleted, fullRows, passRows, expectedRows;
        private static long checkCalls, checkTicks, resourceCalls, resourceTicks;
        private static int records;
#if NLINK_TEST_NATIVE_SELECTION_COMPONENT_PROFILE
        private static bool componentsActive;
        private static readonly long[] componentCalls = new long[4], componentSamples = new long[4], componentTicks = new long[4];
        public static long ComponentStarted(NLink.Tools.LocalRetainedSourceRequest.FrontierWorkerInputs owned, int component)
        {
            if (!componentsActive || !Object.ReferenceEquals(inputs, owned)) return -1;
            componentCalls[component]++;
            if ((resourceCalls - 1) % 128 != 0) return -1;
            componentSamples[component]++; return Stopwatch.GetTimestamp();
        }
        public static void ComponentReturned(int component, long value)
        { if (value >= 0) componentTicks[component] += Stopwatch.GetTimestamp() - value; }
        public static string ComponentFields()
        {
            string fields = "; components_enabled=True; component_sample_stride=128";
            string[] names = { "base", "scope", "files", "pins" };
            for (int i = 0; i < names.Length; i++)
                fields += "; " + names[i] + "_calls=" + componentCalls[i] + "; " + names[i] + "_samples=" + componentSamples[i] +
                    "; " + names[i] + "_ticks=" + componentTicks[i];
            return fields;
        }
#endif
        public static void Begin(NLink.Tools.LocalRetainedSourceRequest.FrontierWorkerInputs owned)
        {
            if (inputs != null || owned == null) throw new InvalidOperationException("content_selection_profile_replay");
            inputs = owned; entryWorkTicks = owned.ContentSelectionWorkTicksForTest; started = Stopwatch.GetTimestamp();
#if NLINK_TEST_NATIVE_SELECTION_COMPONENT_PROFILE
            componentsActive = true;
#endif
            Write("entry");
        }
        public static void End()
        {
#if NLINK_TEST_NATIVE_SELECTION_COMPONENT_PROFILE
            componentsActive = false;
#endif
            Write("returned");
        }
        private static void Write(string phase)
        {
            if (inputs == null || records >= 2) return;
            records++;
            Console.Out.WriteLine("content-native-selection-profile=diagnostic_only=True; sequence=" + records +
                "; phase=" + phase + "; frequency=" + Stopwatch.Frequency + "; elapsed_ticks=" + (Stopwatch.GetTimestamp() - started) +
                "; entry_work_ticks=" + entryWorkTicks + "; active_delta_ticks=" + (inputs.ContentSelectionWorkTicksForTest - entryWorkTicks) +
                "; scan_calls=" + scanCalls + "; scan_completed=" + scanCompleted + "; scan_ticks=" + scanTicks +
                "; full_rows=" + fullRows + "; pass_rows=" + passRows + "; expected_rows=" + expectedRows +
                "; check_calls=" + checkCalls + "; check_ticks=" + checkTicks + "; resource_calls=" + resourceCalls +
                "; resource_ticks=" + resourceTicks + "; " + inputs.ContentSelectionSnapshotForTest());
            Console.Out.Flush();
        }
        public static long CheckStarted() { checkCalls++; return Stopwatch.GetTimestamp(); }
        public static void CheckReturned(long value) { checkTicks += Stopwatch.GetTimestamp() - value; }
        public static long ResourceStarted() { resourceCalls++; return Stopwatch.GetTimestamp(); }
        public static void ResourceReturned(long value) { resourceTicks += Stopwatch.GetTimestamp() - value; }
        public static long ScanStarted(int count)
        { scanCalls++; passRows = 0; expectedRows = count; return Stopwatch.GetTimestamp(); }
        public static void RowCompleted() { fullRows++; passRows++; }
        public static void ScanCompleted() { scanCompleted++; }
        public static void ScanReturned(long value) { scanTicks += Stopwatch.GetTimestamp() - value; }
    }
}
#endif
