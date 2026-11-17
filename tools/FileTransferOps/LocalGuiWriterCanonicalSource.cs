using System;
using System.IO;
using System.Security.Cryptography;

namespace NLink.Tools
{
    public sealed partial class LocalInputReadLease
    {
        // Separate transfer-source acquisition. Diagnostic catalog limits and
        // source/writer verification budgets are not used or changed here.
        internal static LocalInputReadLease HoldCanonicalAttemptSource(string path)
        {
            FileStream stream = null;
            try
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length != 268435456) return null;
                string digest;
                using (var sha = SHA256.Create()) digest = Hex(sha.ComputeHash(stream));
                var lease = new LocalInputReadLease(stream, path, digest, -1, true);
                stream = null; return lease;
            }
            catch { return null; }
            finally { if (stream != null) stream.Dispose(); }
        }
    }

    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class CanonicalAttemptDispatch
        {
            private bool sourceAttempted;
            private CanonicalAttemptSource source;

            public CanonicalAttemptSource TryHoldSource(FrontierAppRequest sender, string path)
            {
                lock (rolesSync)
                {
                    if (!IsCurrent || consumed != 1 || sourceAttempted || sender == null ||
                        !Object.ReferenceEquals(sender, helpee.Request) ||
                        !MatchesRoleLaunch("helpee", sender, helpee.Reports, helpee.Process) ||
                        !MatchesRoleLaunch("helper", helper.Request, helper.Reports, helper.Process)) return null;
                    sourceAttempted = true;
                    var lease = LocalInputReadLease.HoldCanonicalAttemptSource(path);
                    if (lease == null) return null;
                    if (!IsCurrent) { lease.Dispose(); return null; }
                    source = new CanonicalAttemptSource(this, lease);
                    return source;
                }
            }

            internal bool SourceCurrent(CanonicalAttemptSource value)
            {
                lock (rolesSync)
                {
                    if (value == null || !Object.ReferenceEquals(source, value) || value.Retired) return false;
                    bool current = IsCurrent && consumed == 1 && value.Held.IsVerified;
                    if (current) ValidateSourceAssociation(value, ref current);
                    if (current)
                    {
                        using (var read = value.Held.TryOpenVerifiedRead())
                            if (read != null && read.Length == 268435456) return true;
                    }
                    RetireSource(value); return false;
                }
            }

            partial void ValidateSourceAssociation(CanonicalAttemptSource value, ref bool current);

            internal void RetireSource(CanonicalAttemptSource value)
            {
                lock (rolesSync)
                {
                    if (value == null || !Object.ReferenceEquals(source, value) || value.Retired) return;
                    value.Retired = true; value.Held.Dispose();
                }
            }
        }

        // GUI-local protected source interval only. Holding these bytes does not
        // prove a sender read, destination ownership or transfer completion.
        public sealed partial class CanonicalAttemptSource : IDisposable
        {
            private readonly CanonicalAttemptDispatch owner;
            internal readonly LocalInputReadLease Held;
            internal bool Retired;
            internal CanonicalAttemptSource(CanonicalAttemptDispatch owner, LocalInputReadLease held)
            { this.owner = owner; Held = held; }
            public bool IsCurrent { get { return owner.SourceCurrent(this); } }
            public long ByteCount { get { return IsCurrent ? Held.ByteCount : 0; } }
            public string Sha256 { get { return IsCurrent ? Held.ExpectedSha256 : null; } }
            public string FileIdentity { get { return IsCurrent ? Held.FileIdentity : null; } }
            public bool LiveProofEligible { get { return false; } }
            public bool TransferIntegrityEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            public void Dispose() { owner.RetireSource(this); }
        }
    }
}
