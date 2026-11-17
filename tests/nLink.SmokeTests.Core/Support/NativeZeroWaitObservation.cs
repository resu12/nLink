#if NLINK_TEST_NATIVE_CONTENT_ACQUISITION
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace NLink.Tests
{
    // Standalone events only: no original owner, files, custody or verdict.
    public static class NativeZeroWaitObservation
    {
        public sealed class Measurement
        {
            public bool Signaled { get; set; }
            public long ManagedTicks { get; set; }
            public long NativeTicks { get; set; }
            public int ManagedMatches { get; set; }
            public int NativeMatches { get; set; }
        }
        public static Measurement[] Run()
        {
            var rows = new Measurement[6];
            for (int index = 0; index < rows.Length; index++)
            {
                bool signaled = index >= 3;
                using (var signal = new EventWaitHandle(signaled, EventResetMode.ManualReset))
                {
                    var row = new Measurement { Signaled = signaled }; rows[index] = row;
                    for (int warm = 0; warm < 1000; warm++) { signal.WaitOne(0); WaitForSingleObject(signal.SafeWaitHandle, 0); }
                    var clock = Stopwatch.StartNew();
                    for (int i = 0; i < 20000; i++) if (signal.WaitOne(0) == signaled) row.ManagedMatches++;
                    clock.Stop(); row.ManagedTicks = clock.ElapsedTicks; clock.Restart();
                    for (int i = 0; i < 20000; i++) if (WaitForSingleObject(signal.SafeWaitHandle, 0) == (signaled ? 0u : 258u)) row.NativeMatches++;
                    clock.Stop(); row.NativeTicks = clock.ElapsedTicks;
                }
            }
            return rows;
        }
        public static string PendingInterrupt(bool native)
        {
            using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset))
            using (var signal = new EventWaitHandle(false, EventResetMode.ManualReset))
            {
                int go = 0; string result = null;
                var worker = new Thread(() =>
                {
                    ready.Set();
                    while (Volatile.Read(ref go) == 0) Thread.SpinWait(1);
                    try { result = native ? "native_" + WaitForSingleObject(signal.SafeWaitHandle, 0) : "managed_" + signal.WaitOne(0); }
                    catch (Exception error) { result = error.GetType().Name; }
                });
                worker.IsBackground = true; worker.Start();
                if (!ready.WaitOne(5000)) throw new InvalidOperationException("zero_wait_observation_ready");
                worker.Interrupt(); Volatile.Write(ref go, 1);
                if (!worker.Join(5000)) throw new InvalidOperationException("zero_wait_observation_end");
                return result;
            }
        }
        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
    }
}
#endif
