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
        { work = originalClock; samples.Clear(); recoveryCalls = recoveryTicks = recoveryFailedCalls = 0; AttemptPassiveDiagnosticsForTest.Start(originalClock); AttemptParentFactsDiagnosticsForTest.Start(originalClock); }
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
            finally { AttemptPassiveDiagnosticsForTest.WriteAfterCleanup(); AttemptParentFactsDiagnosticsForTest.WriteAfterCleanup(); work = null; samples.Clear(); recoveryCalls = recoveryTicks = recoveryFailedCalls = 0; }
        }
    }

    // Fixed-size, passive diagnostics for fixture-projected copies of unchanged tools.
    // Check returns its already-evaluated operand; it never supplies validation.
    public static class AttemptPassiveDiagnosticsForTest
    {
        private static readonly string[] names = { "parser_loop", "parser_rows_done", "parser_flush_begin", "parser_flush_end",
            "parser_capture_done", "parser_seal_done", "copy_begin", "copy_loop", "copy_bytes_done", "copy_hash_done",
            "copy_flush_begin", "copy_flush_end", "copy_capture_done", "copy_duplicate_done", "copy_owned_check_done", "copy_end" };
        private static readonly long[] active = new long[16], wall = new long[16];
        private static readonly bool[] seen = new bool[16];
        private static Stopwatch work;
        private static bool copying, accepted, sampledRow;
        private static string rejected;
        private static long rejectedActive, rejectedWall, rows, readCalls, readTicks, parseCalls, parseTicks, started, bytes, expected;

        public static void Start(Stopwatch originalClock)
        {
            work = originalClock; copying = accepted = sampledRow = false; rejected = null;
            rejectedActive = rejectedWall = rows = readCalls = readTicks = parseCalls = parseTicks = started = bytes = expected = 0;
            Array.Clear(seen, 0, seen.Length); Array.Clear(active, 0, active.Length); Array.Clear(wall, 0, wall.Length);
        }
        public static void Mark(int slot)
        {
            if (work == null || slot < 0 || slot >= seen.Length || seen[slot]) return;
            active[slot] = work.ElapsedTicks; wall[slot] = Stopwatch.GetTimestamp(); seen[slot] = true;
        }
        public static void ReadStart(long index)
        {
            sampledRow = work != null && (index < 4 || (index & 255) == 0);
            if (work != null) rows++;
            if (sampledRow) started = Stopwatch.GetTimestamp();
        }
        public static void ReadEnd()
        { if (sampledRow) { readTicks += Stopwatch.GetTimestamp() - started; readCalls++; } }
        public static void ParseStart() { if (sampledRow) started = Stopwatch.GetTimestamp(); }
        public static void ParseEnd()
        { if (sampledRow) { parseTicks += Stopwatch.GetTimestamp() - started; parseCalls++; } }
        public static void CopyBegin() { if (work != null) { copying = true; Mark(6); } }
        public static bool Check(string predicate, bool value)
        {
            if (copying && !value && rejected == null)
            { rejected = predicate; rejectedActive = work.ElapsedTicks; rejectedWall = Stopwatch.GetTimestamp(); }
            return AttemptParentFactsDiagnosticsForTest.Check(predicate, value);
        }
        public static void CopyProgress(long copied, long total)
        { if (copying) { bytes = copied; expected = total; } }
        public static void CopyEnd(bool success)
        { if (copying) { accepted = success; Mark(15); copying = false; } }
        public static void WriteAfterCleanup()
        {
            try
            {
                if (work == null) return;
                for (int i = 0; i < seen.Length; i++) if (seen[i])
                    Console.Error.WriteLine("attempt-passive-phase; stage=" + names[i] + "; active_ticks=" + active[i] +
                        "; wall_ticks=" + wall[i] + "; frequency=" + Stopwatch.Frequency);
                Console.Error.WriteLine("attempt-passive-samples; rows=" + rows + "; read_sample_calls=" + readCalls +
                    "; read_sample_ticks=" + readTicks + "; parse_sample_calls=" + parseCalls + "; parse_sample_ticks=" + parseTicks +
                    "; sampling=first4_then_every256; frequency=" + Stopwatch.Frequency);
                Console.Error.WriteLine("attempt-passive-copy; accepted=" + accepted + "; first_rejected=" + (rejected ?? "none_observed") +
                    "; rejected_active_ticks=" + rejectedActive + "; rejected_wall_ticks=" + rejectedWall +
                    "; copied_bytes=" + bytes + "; expected_bytes=" + expected + "; frequency=" + Stopwatch.Frequency);
            }
            catch { }
            finally { Start(null); }
        }
    }

    // Separate fixed-size facts/rendezvous observer. Never starts/stops the borrowed clock.
    public static class AttemptParentFactsDiagnosticsForTest
    {
        private static readonly string[] names = { "prepare_enter", "prepare_attached", "prepare_files_ready", "prepare_files_published",
            "prepare_files_offered", "prepare_files_pulled", "facts_enter", "facts_owned_submission", "facts_adoption_ready",
            "facts_recovery_enter", "facts_encode_begin", "facts_encode_end", "facts_decode_begin", "facts_decode_end",
            "facts_channel_begin", "facts_channel_end", "facts_recovery_end", "facts_assigned", "facts_exit", "prepare_facts_published",
            "prepare_facts_offered", "prepare_ack_offered", "ack_enter", "ack_owned_submission", "ack_recovery_enter",
            "ack_channel_open", "ack_recovery_end", "ack_exit", "prepare_ack_confirmed", "prepare_commit_ready", "prepare_exit" };
        private static readonly long[] ticks = new long[31], wall = new long[31];
        private static readonly bool[] seen = new bool[31];
        private static Stopwatch work;
        private static bool active, factsAccepted, ackAccepted, prepareAccepted;
        private static string rejected;
        private static int phase, rejectedPhase, encodedBytes;
        private static long rejectedTicks, rejectedWall, checks, sourceChecks, pinChecks;
        public static void Start(Stopwatch originalClock)
        {
            work = originalClock; active = factsAccepted = ackAccepted = prepareAccepted = false; rejected = null;
            phase = rejectedPhase = encodedBytes = 0; rejectedTicks = rejectedWall = checks = sourceChecks = pinChecks = 0;
            Array.Clear(seen, 0, seen.Length); Array.Clear(ticks, 0, ticks.Length); Array.Clear(wall, 0, wall.Length);
        }
        public static void Begin() { active = work != null; Mark(0); }
        public static void Mark(int slot)
        {
            if (!active || slot < 0 || slot >= seen.Length) return;
            phase = slot; if (seen[slot]) return;
            ticks[slot] = work.ElapsedTicks; wall[slot] = Stopwatch.GetTimestamp(); seen[slot] = true;
        }
        public static bool Check(string predicate, bool value)
        {
            if (active)
            {
                checks++; if (predicate == "facts_callback_source") sourceChecks++; if (predicate == "facts_callback_pins") pinChecks++;
                if (!value && rejected == null)
                { rejected = predicate; rejectedPhase = phase; rejectedTicks = work.ElapsedTicks; rejectedWall = Stopwatch.GetTimestamp(); }
            }
            return value;
        }
        public static bool Step(int slot, string predicate, bool value) { Mark(slot); return Check(predicate, value); }
        public static void EncodedBytes(int count) { if (active) encodedBytes = count; }
        public static void FactsEnd(bool success) { if (active) { factsAccepted = success; Mark(18); } }
        public static void AckEnd(bool success) { if (active) { ackAccepted = success; Mark(27); } }
        public static void End(bool success) { if (active) { prepareAccepted = success; Mark(30); active = false; } }
        public static void WriteAfterCleanup()
        {
            try
            {
                if (work == null) return;
                for (int i = 0; i < seen.Length; i++) if (seen[i])
                    Console.Error.WriteLine("attempt-parent-phase; stage=" + names[i] + "; active_ticks=" + ticks[i] +
                        "; wall_ticks=" + wall[i] + "; frequency=" + Stopwatch.Frequency);
                Console.Error.WriteLine("attempt-parent-summary; first_rejected=" + (rejected ?? "none_observed") +
                    "; rejected_stage=" + names[rejectedPhase] + "; rejected_active_ticks=" + rejectedTicks + "; rejected_wall_ticks=" + rejectedWall +
                    "; facts_accepted=" + factsAccepted + "; ack_accepted=" + ackAccepted + "; prepare_accepted=" + prepareAccepted +
                    "; encoded_bytes=" + encodedBytes + "; checks=" + checks + "; facts_source_checks=" + sourceChecks +
                    "; facts_pin_checks=" + pinChecks + "; frequency=" + Stopwatch.Frequency);
            }
            catch { }
            finally { Start(null); }
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
