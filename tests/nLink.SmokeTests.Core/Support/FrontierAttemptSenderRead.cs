#if NLINK_TEST_ATTEMPT_DISPATCH
using System;
using System.IO;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class CanonicalAttemptDispatch
        {
            private bool peersSent;
            public bool TrySendPeerProcessesForTest(FrontierAppRequest sender, FrontierAppRequest receiver, string fault)
            {
                lock (rolesSync)
                {
                    if (peersSent || !MatchesRoleLaunchForTest("helpee", sender, helpee.Reports, helpee.Process) ||
                        !MatchesRoleLaunchForTest("helper", receiver, helper.Reports, helper.Process)) return false;
                    peersSent = true;
                    return SendPeer(receiver, sender, helpee.Process.StandardInput.BaseStream, fault) &&
                        SendPeer(sender, receiver, helper.Process.StandardInput.BaseStream, fault);
                }
            }
            private static bool SendPeer(FrontierAppRequest peer, FrontierAppRequest target, Stream output, string fault)
            {
                return NLink.Tests.AttemptPeerProcessChannel.Send(peer.launch.Exit.SafeWaitHandle, target.launch.Exit.SafeWaitHandle,
                    output, target.RequestId, target.launch.AppProcessId, target.launch.AppProcessCreationFileTime,
                    peer.RequestId, peer.launch.AppProcessId, peer.launch.AppProcessCreationFileTime, fault);
            }

            internal bool SendSourceToHelpeeForTest(CanonicalAttemptSource value, FrontierAppRequest request)
            {
                if (value.SenderFault == "valid") return value.TrySendToHelpee(request);
                return SendSourceToHelpeeCore(value, request, read =>
                {
                    // Packet/file faults stay exclusively in the explicit fixture.
                    FileStream foreign = null;
                    try
                    {
                        if (value.SenderFault == "foreign-file")
                        {
                            var path = Path.Combine(owner.artifactRoot, "foreign-source.bin");
                            File.Copy(value.Held.Path, path);
                            foreign = File.OpenRead(path);
                        }
                        return NLink.Tests.AttemptSenderSourceChannel.Send(foreign ?? read,
                            request.launch.Exit.SafeWaitHandle, helpee.Process.StandardInput.BaseStream,
                            request.RequestId, request.launch.AppProcessId, request.launch.AppProcessCreationFileTime,
                            value.Held.FileIdentity, value.Held.ExpectedSha256, value.SenderFault);
                    }
                    finally { if (foreign != null) foreign.Dispose(); }
                });
            }
        }

        public sealed partial class CanonicalAttemptSource
        {
            internal string SenderFault = null;
            // Delivery only, never a receipt proving the child read the file.
            public bool TrySendToHelpeeForTest(FrontierAppRequest request)
            { return owner.SendSourceToHelpeeForTest(this, request); }
        }
    }
}
#endif
