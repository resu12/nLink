#if NLINK_TEST_ATTEMPT_DISPATCH
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace NLink.Tests
{
    // Passive samples of the original clock; no authority checks or work allowance.
    public static class AttemptWorkProfileForTest
    {
        private static Stopwatch work;
        private static long recoveryCalls, recoveryTicks, recoveryFailedCalls;
        private static readonly List<Tuple<string, long, long, bool, long, long, long>> samples = new List<Tuple<string, long, long, bool, long, long, long>>();
        public static void Start(Stopwatch originalClock)
        { work = originalClock; samples.Clear(); recoveryCalls = recoveryTicks = recoveryFailedCalls = 0; }
        public static void RecordRecoveryCheck(long started, bool completed)
        {
            if (work == null) return;
            recoveryTicks += Stopwatch.GetTimestamp() - started;
            recoveryCalls++;
            if (!completed) recoveryFailedCalls++;
        }
        public static void Mark(string checkpoint)
        {
            if (work != null) samples.Add(Tuple.Create(checkpoint, work.ElapsedTicks, Stopwatch.GetTimestamp(), work.IsRunning,
                recoveryCalls, recoveryTicks, recoveryFailedCalls));
        }
        public static void WriteAfterCleanup()
        {
            try
            {
                foreach (var row in samples)
                    Console.Error.WriteLine("role-work-profile; stage=" + row.Item1 + "; active_ticks=" + row.Item2 +
                        "; wall_ticks=" + row.Item3 + "; work_running=" + row.Item4 + "; frequency=" + Stopwatch.Frequency +
                        "; recovery_calls=" + row.Item5 + "; recovery_ticks=" + row.Item6 + "; recovery_failed_calls=" + row.Item7);
            }
            catch { }
            finally { work = null; samples.Clear(); recoveryCalls = recoveryTicks = recoveryFailedCalls = 0; }
        }
    }

    // Offline fixture diagnostic only; never caches or supplies validation.
    public static class OwnedSourceCheckProfile
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static Func<bool> Getter(object value, string name)
        { return (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), value, value.GetType().GetProperty(name, Flags).GetGetMethod(true)); }
        private static Func<bool> Method(object value, string name)
        { return (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), value, value.GetType().GetMethod(name, Flags)); }
        private static void Measure(string name, Func<bool> check)
        {
            if (!check()) throw new InvalidOperationException("profile_scope_not_current:" + name);
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < 32; i++)
                if (!check()) throw new InvalidOperationException("profile_scope_not_current:" + name);
            clock.Stop();
            Console.WriteLine("owned-source-profile; check=" + name + "; calls=32; milliseconds=" +
                clock.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture));
        }
        public static void Run(Stream source, object binding, object owner, object adoption)
        {
            Measure("gui-registration", Method(owner, "MatchesRequest"));
            Measure("app-requests", Method(owner, "AppRequestsCurrent"));
            Measure("file-channels", Method(owner, "FileChannelsCurrent"));
            Measure("ledger-origin", Method(owner, "LedgerOriginCurrent"));
            Measure("parent-registration", Method(owner, "ParentRegistrationCurrent"));
            Measure("launch-owner", Getter(owner, "IsCurrent"));
            Measure("final-seal-scope", Method(owner, "FinalSealScopeCurrent"));
            Measure("adoption", Getter(adoption, "IsRetained"));
            Measure("binding", Getter(binding, "IsCurrent"));
            Measure("source-can-read", () => source.CanRead);
        }
    }
}
#endif
