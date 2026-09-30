#if NLINK_TEST_ATTEMPT_DISPATCH
using System;
using System.Threading;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        private string NativePullDiagnosticForTest = "not-created";
        private bool NativePullRejectForTest(string reason, bool rejected)
        { if (rejected) NativePullDiagnosticForTest = reason; return rejected; }
        public sealed partial class FrontierRelayRegistration
        {
            internal WaitHandle NativeAfterPullForTest = null;
            private bool ObserveNativePullForTest(bool pulled)
            {
                if (pulled && NativeAfterPullForTest != null && !NativeAfterPullForTest.WaitOne(2000))
                    throw new TimeoutException("native_pull_order_fixture_forward_timeout");
                return pulled;
            }
        }
        public sealed partial class FrontierRelayCustody
        {
            internal string NativeBeginDiagnosticForTest = "not-called", NativeAcceptDiagnosticForTest = "not-called", NativeCloseDiagnosticForTest = "open";
            internal string NativeRetirementDiagnosticForTest = "open", NativeExitDiagnosticForTest = "not-rejected";
            private bool NativeExitRejectForTest(string reason, bool rejected)
            { if (rejected) NativeExitDiagnosticForTest = reason; return rejected; }
            private bool NativeRejectForTest(string stage, string reason, bool rejected)
            {
                if (rejected)
                {
                    if (stage == "begin") NativeBeginDiagnosticForTest = reason;
                    else NativeAcceptDiagnosticForTest = reason;
                }
                return rejected;
            }
            internal string LastParentMatchDiagnosticForTest { get; private set; }
            partial void ObserveParentMatch(string reason)
            { LastParentMatchDiagnosticForTest = reason; }
        }
        public sealed partial class FrontierRelayHandoff
        {
            public string NativeForwardDiagnosticForTest = "not-called", NativeTransferCloseDiagnosticForTest = "open";
            private bool NativeForwardCheckForTest(string reason, bool accepted)
            { if (!accepted) NativeForwardDiagnosticForTest = reason; return accepted; }
            public bool CancelPayloadCommitForTest;
            partial void BeforePayloadCommit(ref bool cancelled)
            { cancelled = CancelPayloadCommitForTest; }
        }
        public sealed partial class CanonicalPayloadParent
        {
            // Diagnostic only: read after the existing take, never reevaluate its guards.
            public string LastTakeDiagnosticForTest { get; private set; }
            public string LastParentMatchDiagnosticForTest { get; private set; }
            public string LastNativeBeginDiagnosticForTest { get; private set; }
            public string LastNativeAcceptDiagnosticForTest { get; private set; }
            public string LastNativeCloseDiagnosticForTest { get; private set; }
            public string LastNativeRetirementDiagnosticForTest { get; private set; }
            partial void ObservePayloadTake(string reason)
            { LastTakeDiagnosticForTest = reason; }
            partial void BeginPayloadTakeDiagnostics()
            {
                LastTakeDiagnosticForTest = "evaluating";
                LastParentMatchDiagnosticForTest = "not-evaluated";
                LastNativeBeginDiagnosticForTest = LastNativeAcceptDiagnosticForTest = LastNativeCloseDiagnosticForTest = "not-evaluated";
                LastNativeRetirementDiagnosticForTest = "not-evaluated";
            }
            partial void ObserveNativePayloadTake(FrontierRelayCustody native)
            {
                if (native != null)
                {
                    LastNativeBeginDiagnosticForTest = native.NativeBeginDiagnosticForTest;
                    LastNativeAcceptDiagnosticForTest = native.NativeAcceptDiagnosticForTest;
                    LastNativeCloseDiagnosticForTest = native.NativeCloseDiagnosticForTest;
                    LastNativeRetirementDiagnosticForTest = native.NativeRetirementDiagnosticForTest;
                }
            }
            partial void ObserveParentPayloadMatch(FrontierRelayCustody native)
            { LastParentMatchDiagnosticForTest = native.LastParentMatchDiagnosticForTest; }
        }
    }
}
#endif
