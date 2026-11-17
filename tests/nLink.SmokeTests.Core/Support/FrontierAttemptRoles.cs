#if NLINK_TEST_ATTEMPT_DISPATCH
using System;
using System.Diagnostics;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class FrontierLaunchOwner
        {
            public FrontierAppRequest TryCreateAttemptRoleRequestForTest(string role)
            {
                return IsCurrent && attemptDispatch != null ? attemptDispatch.TryCreateRoleRequest(role, this) : null;
            }
        }

        // Only the test copy of Start-AppInstance uses this entry. It selects
        // the original in-process owner, never one reconstructed from labels.
        public static FrontierAppReports StartAttemptRoleAppForTest(string role, FrontierAppRequest request,
            Process process, Func<int, string[]> createReports)
        {
            var owner = frontierLaunchOwner;
            if (owner == null || owner.attemptDispatch == null)
                throw new InvalidOperationException("attempt_role_launch_rejected");
            return owner.attemptDispatch.StartRoleApp(role, request, process, createReports);
        }

        public sealed partial class CanonicalAttemptDispatch
        {
            public bool MatchesRoleLaunchForTest(string role, FrontierAppRequest request,
                FrontierAppReports reports, Process process)
            { return MatchesRoleLaunch(role, request, reports, process); }
        }
    }
}
#endif
