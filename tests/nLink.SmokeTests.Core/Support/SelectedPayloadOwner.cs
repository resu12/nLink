#if NLINK_TEST_AUTHENTICATED_PAYLOAD_HANDOFF && NLINK_TEST_SELECTED_NATIVE_WRITER
using System;
using System.Threading;
using NLink.Tests;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        public sealed partial class NativeMembership
        {
            internal bool SelectedPayloadScopeMatches(HeldFileNativeParent.TransferBinding binding)
            {
                if (binding == null) return false;
                string session = null, transfer = null;
                foreach (string part in borrowerFullOriginal[SelectedIndex].Split(';'))
                {
                    int equals = part.IndexOf('=');
                    if (equals < 1) continue;
                    string key = part.Substring(0, equals).Trim();
                    if (key == "publication_evidence_session_id")
                    { if (session != null) return false; session = part.Substring(equals + 1).Trim(); }
                    if (key == "publication_evidence_transfer_id")
                    { if (transfer != null) return false; transfer = part.Substring(equals + 1).Trim(); }
                }
                return session == binding.Session && transfer == binding.Transfer;
            }
        }

        public sealed partial class MembershipBorrowerForTest
        {
            private int payloadOwnerAttempted;
            private long payloadOwnerUnits;
            private SelectedPayloadOwnerForTest payloadOwner;
            public SelectedPayloadOwnerForTest TryBindSelectedPayloadForTest(TerminalWorkerFilesForTest files,
                SelectedCollectorOriginForTest origin, PreparedPublication completion)
            {
                if (!IsCurrent || consumed != 1 || !work.IsRunning || files == null || origin == null ||
                    !Object.ReferenceEquals(owner.TerminalFilesForTest, files) ||
                    !Object.ReferenceEquals(selectedOrigin, origin) ||
                    !origin.Owns(owner, this, owner.CollectorOutputsForTest, completion) ||
                    !files.OwnsPrimary(owner, NativeMembership.Property(context, "SourceReader") as SourceReader,
                        custody, pair, context, full, compact, fullView, compactView) ||
                    CumulativeRecordVisits >= 4096 || full[membership.SelectedIndex].Length > LocalPublicationInputLimits.LineCodeUnits ||
                    4L * full[membership.SelectedIndex].Length > LocalPublicationInputLimits.ProducerCodeUnits - CumulativeCodeUnits ||
                    Interlocked.CompareExchange(ref payloadOwnerAttempted, 1, 0) != 0) return null;
                payloadOwnerUnits = 4L * full[membership.SelectedIndex].Length;
                if (!membership.SelectedPayloadScopeMatches(files.OwnedTransfer) || !files.PayloadsMatch(262144) || !IsCurrent) return null;
                payloadOwner = new SelectedPayloadOwnerForTest(this, owner, files, origin, completion);
                return payloadOwner.IsCurrent ? payloadOwner : null;
            }
            internal bool OwnsPayloadOwner(SelectedPayloadOwnerForTest result, TerminalWorkerFilesForTest files,
                SelectedCollectorOriginForTest origin, PreparedPublication completion)
            {
                return Object.ReferenceEquals(payloadOwner, result) && IsCurrent && consumed == 1 &&
                    CumulativeRecordVisits <= 4096 && CumulativeCodeUnits <= LocalPublicationInputLimits.ProducerCodeUnits &&
                    Object.ReferenceEquals(owner.TerminalFilesForTest, files) &&
                    Object.ReferenceEquals(selectedOrigin, origin) &&
                    origin.Owns(owner, this, owner.CollectorOutputsForTest, completion) &&
                    files.OwnsPrimary(owner, NativeMembership.Property(context, "SourceReader") as SourceReader,
                        custody, pair, context, full, compact, fullView, compactView);
            }
            internal bool PayloadOwnerRetired { get { return payloadOwner == null || !payloadOwner.IsCurrent; } }
        }

        // Non-owning, post-report association only. No terminal scan, payload hash,
        // integrity result, route authority or acceptance capability is issued.
        public sealed class SelectedPayloadOwnerForTest : IDisposable
        {
            private readonly SelectedPayloadOwnerForTest original;
            private readonly MembershipBorrowerForTest borrower;
            private readonly Worker owner;
            private readonly TerminalWorkerFilesForTest files;
            private readonly SelectedCollectorOriginForTest origin;
            private readonly PreparedPublication completion;
            private int disposed;
            internal SelectedPayloadOwnerForTest(MembershipBorrowerForTest borrower, Worker owner,
                TerminalWorkerFilesForTest files, SelectedCollectorOriginForTest origin, PreparedPublication completion)
            { original = this; this.borrower = borrower; this.owner = owner; this.files = files; this.origin = origin; this.completion = completion; }
            public bool IsCurrent
            { get { return Object.ReferenceEquals(original, this) && disposed == 0 && borrower.OwnsPayloadOwner(this, files, origin, completion); } }
            public bool Owns(Worker candidate, MembershipBorrowerForTest candidateBorrower, TerminalWorkerFilesForTest candidateFiles,
                SelectedCollectorOriginForTest candidateOrigin, PreparedPublication candidateCompletion)
            { return IsCurrent && Object.ReferenceEquals(owner, candidate) && Object.ReferenceEquals(borrower, candidateBorrower) &&
                Object.ReferenceEquals(files, candidateFiles) && Object.ReferenceEquals(origin, candidateOrigin) && Object.ReferenceEquals(completion, candidateCompletion); }
            public void Dispose() { if (Object.ReferenceEquals(original, this)) Interlocked.Exchange(ref disposed, 1); }
        }
        public sealed partial class Worker
        {
            public bool SelectedPayloadOwnerRetiredForTest
            { get { return Object.ReferenceEquals(frontierOriginal, this) && (membershipBorrower == null || membershipBorrower.PayloadOwnerRetired); } }
        }
    }
}
#endif
