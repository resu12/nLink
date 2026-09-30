#if NLINK_TEST_ATTEMPT_DISPATCH
using System;
using System.Security.Cryptography;
using System.Threading;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public static string SmallDestinationHashForTest()
        {
            var bytes = new byte[2 * 1024 * 1024 + 23];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);
            using (var hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(bytes));
        }

        public sealed partial class CanonicalAttemptDispatch
        {
            private string destinationFault;
            public CanonicalAttemptDestination GetDestinationForTest()
            { lock (rolesSync) return IsCurrent && destination != null && destination.IsCurrent ? destination : null; }

            public CanonicalAttemptDestination TryAdoptDestinationForTest(FrontierAppRequest request,
                long expectedBytes, string expectedHash, string fault)
            {
                destinationFault = fault;
                try { return AdoptDestinationCore(request, null, expectedBytes, expectedHash, CancellationToken.None); }
                finally { destinationFault = null; }
            }

            partial void InspectDestinationOffer(CanonicalAttemptDestination candidate)
            { if (destinationFault == "foreign-identity") candidate.identity += "-foreign"; }

            partial void PrepareDestinationAcknowledgment(byte[] acknowledgment, ref bool cancel)
            {
                cancel = destinationFault == "cancel-offer";
                if (destinationFault == "bad-ack") acknowledgment[0] ^= 1;
            }
        }
    }
}
#endif
