#if NLINK_TEST_ATTEMPT_DISPATCH
namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        // Keep fixture entry points on the same production objects and checks.
        public bool TryBindAttemptForTest(int attempt, string[] projection)
        { return TryBindAttemptProjection(attempt, projection); }

        public sealed partial class Relay
        {
            public bool TryForwardAttemptForTest(LocalGuiWriterRequest target, int attempt, string[] projection)
            { return TryForwardAttemptProjection(target, attempt, projection); }
        }

        public sealed partial class FrontierLaunchOwner
        {
            public CanonicalAttemptDispatch TryOpenAttemptDispatchForTest(int attempt, string[] projection)
            { return TryOpenCanonicalAttemptDispatch(attempt, projection); }
        }

    }
}
#endif
