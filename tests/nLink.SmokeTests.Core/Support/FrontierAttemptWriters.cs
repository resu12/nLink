#if NLINK_TEST_ATTEMPT_DISPATCH
using System;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class FrontierLaunchOwner
        {
            internal void ReportRoleWriterRejectionForTest(string stage)
            {
                // Observe the original clock/state only; never re-evaluate custody.
                try
                {
                    lock (sync)
                        Console.Error.WriteLine("role-writer-inputs-rejected; stage=" + stage +
                            "; gui_disposed=" + disposed + "; active_ms=" + frontierWork.ElapsedMilliseconds +
                            "; work_running=" + frontierWork.IsRunning + "; utc=" + DateTime.UtcNow.ToString("O"));
                }
                catch { }
            }
        }

        public sealed partial class CanonicalAttemptDispatch
        {
            partial void ReportRoleWriterRejection(string stage)
            { owner.ReportRoleWriterRejectionForTest(stage); }
        }
    }
}
#endif
