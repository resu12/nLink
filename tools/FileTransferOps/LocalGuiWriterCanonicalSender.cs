using System;
using System.IO;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class CanonicalAttemptDispatch
        {
            internal bool SendSourceToHelpee(CanonicalAttemptSource value, FrontierAppRequest request)
            {
                return SendSourceToHelpeeCore(value, request, read => LocalCanonicalSenderSource.Send(read,
                    request.launch.Exit.SafeWaitHandle, helpee.Process.StandardInput.BaseStream,
                    request.RequestId, request.launch.AppProcessId, request.launch.AppProcessCreationFileTime,
                    value.Held.FileIdentity, value.Held.ExpectedSha256));
            }

            // The optional fixture callback changes packet evidence, never the
            // original source/role admission or its one-shot delivery claim.
            internal bool SendSourceToHelpeeCore(CanonicalAttemptSource value, FrontierAppRequest request,
                Func<FileStream, bool> send)
            {
                lock (rolesSync)
                {
                    if (!SourceCurrent(value) || value.SenderAttempted ||
                        !Object.ReferenceEquals(request, helpee.Request) ||
                        !MatchesRoleLaunch("helpee", request, helpee.Reports, helpee.Process)) return false;
                    value.SenderAttempted = true;
                    try
                    {
                        using (var read = value.Held.TryOpenVerifiedRead())
                            return read != null && send(read);
                    }
                    catch (IOException) { return false; }
                    catch (InvalidOperationException) { return false; }
                }
            }
        }

        public sealed partial class CanonicalAttemptSource
        {
            internal bool SenderAttempted;
            // Delivery only; neither sender reads nor route acceptance follow.
            public bool TrySendToHelpee(FrontierAppRequest request)
            { return owner.SendSourceToHelpee(this, request); }
        }
    }
}
