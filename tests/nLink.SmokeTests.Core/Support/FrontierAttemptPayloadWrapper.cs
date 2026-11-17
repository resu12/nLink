#if NLINK_TEST_ATTEMPT_DISPATCH
using System;
using System.IO;
using System.Threading;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class CanonicalAttemptDispatch
        {
            partial void WithholdPayloadCommitForTest(Stream pipe, ref string stage, ref bool withheld)
            {
                if (Environment.GetEnvironmentVariable("NLINK_TEST_PAYLOAD_WRAPPER_CASE") != "stall-commit") return;
                // Negative fixture peer: withhold commit until the original server closes.
                stage = "commit_stall";
                pipe.ReadAsync(new byte[1], 0, 1, CancellationToken.None).GetAwaiter().GetResult();
                withheld = true;
            }
        }
        public sealed partial class CanonicalPayloadWrapper
        {
            private string fault;
            partial void InitializeFaultForTest()
            { fault = Environment.GetEnvironmentVariable("NLINK_TEST_PAYLOAD_WRAPPER_CASE"); }
            partial void PrepareAcknowledgmentForTest(byte[] ack, ref bool cancel)
            {
                cancel = fault == "cancel-before-ack";
                if (fault == "bad-ack") ack[0] ^= 1;
            }
            partial void AlterPayloadIdentityForTest(int slot)
            { if (fault == "foreign-identity" && slot == 1) identities[slot] += "-foreign"; }
        }
    }
}
#endif
