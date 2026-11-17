#if NLINK_TEST_ATTEMPT_DISPATCH
namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class CanonicalAttemptDispatch
        {
            public CanonicalAttemptSource TryHoldSourceForTest(FrontierAppRequest sender, string path)
            { return TryHoldSource(sender, path); }

            internal bool SourceCurrentForTest(CanonicalAttemptSource value)
            { return SourceCurrent(value); }
        }
    }
}
#endif
