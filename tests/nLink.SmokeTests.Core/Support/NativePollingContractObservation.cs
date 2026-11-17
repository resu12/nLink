#if NLINK_TEST_NATIVE_CONTENT_ACQUISITION
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace NLink.Tests
{
    // Isolated review fixture: no owner, file, custody, process lease or verdict.
    public static class NativePollingContractObservation
    {
        public static string[] Run()
        {
            var rows = new List<string>();
            for (int mode = 0; mode < 3; mode++)
            {
                foreach (bool signaled in new[] { false, true })
                {
                    using (var signal = new EventWaitHandle(signaled, EventResetMode.ManualReset))
                        Check(rows, "state_" + mode + "_" + signaled, Capture(() => Poll(signal, mode)),
                            mode == 0 ? "managed_" + signaled : "native_" + (signaled ? 0u : 258u));
                }
                var closed = new EventWaitHandle(false, EventResetMode.ManualReset);
                closed.Dispose();
                Check(rows, "disposed_" + mode, Capture(() => Poll(closed, mode)), "ObjectDisposedException");
                Check(rows, "pending_interrupt_" + mode, Interrupted(mode, false),
                    mode == 1 ? "native_258/followup_ThreadInterruptedException" : "ThreadInterruptedException/followup_managed_False");
                rows.Add("wait_context_" + mode + "=" + ContextWait(mode));
            }
            Check(rows, "interrupt_after_probe_managed", Interrupted(0, true), "ThreadInterruptedException/followup_managed_False");
            Check(rows, "interrupt_after_probe_sleep_native", Interrupted(2, true), "native_258/followup_ThreadInterruptedException");
            using (var invalid = new SafeWaitHandle(IntPtr.Zero, false))
            {
                uint result = WaitForSingleObject(invalid, 0);
                int error = Marshal.GetLastWin32Error();
                Check(rows, "invalid_native", result + "/error_" + error, "4294967295/error_6");
            }
            using (var invalid = new EventWaitHandle(false, EventResetMode.ManualReset))
            {
                invalid.SafeWaitHandle.Dispose();
                invalid.SafeWaitHandle = new SafeWaitHandle(IntPtr.Zero, false);
                string result = Capture(() => Poll(invalid, 0));
                rows.Add("invalid_managed=" + result);
            }
            return rows.ToArray();
        }

        private static string Poll(EventWaitHandle signal, int mode)
        {
            if (mode == 0) return "managed_" + signal.WaitOne(0);
            if (mode == 2) Thread.Sleep(0);
            return "native_" + WaitForSingleObject(signal.SafeWaitHandle, 0);
        }

        private static string Interrupted(int mode, bool afterProbe)
        {
            using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset))
            using (var signal = new EventWaitHandle(false, EventResetMode.ManualReset))
            {
                int go = 0;
                string result = null;
                var worker = new Thread(() =>
                {
                    if (afterProbe) Thread.Sleep(0);
                    ready.Set();
                    while (Volatile.Read(ref go) == 0) Thread.SpinWait(1);
                    result = Capture(() => Poll(signal, afterProbe && mode == 2 ? 1 : mode));
                    result += "/followup_" + Capture(() => Poll(signal, 0));
                });
                worker.IsBackground = true;
                worker.Start();
                if (!ready.WaitOne(5000)) throw new InvalidOperationException("polling_contract_ready");
                worker.Interrupt();
                Volatile.Write(ref go, 1);
                if (!worker.Join(5000)) throw new InvalidOperationException("polling_contract_end");
                return result;
            }
        }

        private sealed class ObservedContext : SynchronizationContext
        {
            internal int Calls;
            internal string Arguments;
            internal ObservedContext() { SetWaitNotificationRequired(); }
            public override int Wait(IntPtr[] handles, bool waitAll, int millisecondsTimeout)
            {
                Calls++;
                Arguments = handles.Length + "/" + waitAll + "/" + millisecondsTimeout;
                return 258;
            }
        }

        private static string ContextWait(int mode)
        {
            using (var signal = new EventWaitHandle(true, EventResetMode.ManualReset))
            {
                var original = SynchronizationContext.Current;
                var observed = new ObservedContext();
                try
                {
                    SynchronizationContext.SetSynchronizationContext(observed);
                    return Capture(() => Poll(signal, mode)) + "/calls_" + observed.Calls + "/arguments_" + observed.Arguments;
                }
                finally { SynchronizationContext.SetSynchronizationContext(original); }
            }
        }

        private static string Capture(Func<string> action)
        {
            try { return action(); }
            catch (Exception error) { return error.GetType().Name; }
        }

        private static void Check(List<string> rows, string name, string actual, string expected)
        {
            rows.Add(name + "=" + actual);
            if (actual != expected) throw new InvalidOperationException(name + ": " + actual + " != " + expected);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
    }
}
#endif
