#if NLINK_TEST_AUTHENTICATED_PAYLOAD_HANDOFF && NLINK_TEST_SELECTED_NATIVE_WRITER
using System;
using System.Threading;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        public sealed partial class NativeMembership
        {
            internal CollectorLineOriginForTest TerminalOrigin(int at, object terminal)
            {
                if (at < 0 || at >= borrowerFull.Length ||
                    !Object.ReferenceEquals(borrowerEvents[at], terminal) ||
                    !Object.ReferenceEquals(borrowerFull[at], borrowerFullOriginal[at]) ||
                    !Object.ReferenceEquals(Property(terminal, "RawLine"), borrowerFullOriginal[at]) ||
                    !Object.Equals(Property(terminal, "LineNumber"), borrowerOrigins[at]) ||
                    !Object.Equals(Property(terminal, "FilePath"), borrowerOwner.SourcePath) ||
                    !OwnsCollectorOriginInputs(borrowerWriter, borrowerFull, borrowerFullOriginal, borrowerWork) ||
                    !Object.ReferenceEquals(collectorWriterPositions, borrowerPositions)) return null;
                var row = borrowerCollector.DescribeLineOriginForTest(borrowerSource, borrowerOrigins[at]);
                return row != null && row.SemanticLine == borrowerOrigins[at] && row.ProducerLine > 0 &&
                    row.RetainedLine > 0 && row.WriterLine > 0 &&
                    MatchesCollectorWriterPosition(at, row.WriterLine) ? row : null;
            }
            internal object TerminalEvent(int at) { return borrowerEvents[at]; }
        }

        public sealed partial class MembershipBorrowerForTest
        {
            private int selectedTerminalAttempted;
            private long selectedTerminalVisits, selectedTerminalUnits;
            private SelectedTerminalJoinForTest selectedTerminals;

            // Post-report only: reuse the original index, membership positions,
            // native binding and cumulative clock. Supplied DTOs are not authority.
            public SelectedTerminalJoinForTest TryJoinSelectedTerminalsForTest(SelectedPayloadOwnerForTest payload, object[] terminals)
            {
                if (!IsCurrent || !Object.ReferenceEquals(payloadOwner, payload) || payload == null || !payload.IsCurrent ||
                    !work.IsRunning || terminals == null || terminals.Length != 2 ||
                    full.Length + 2L > 4096 - CumulativeRecordVisits ||
                    CumulativeCodeUnits > LocalPublicationInputLimits.ProducerCodeUnits ||
                    Interlocked.CompareExchange(ref selectedTerminalAttempted, 1, 0) != 0) return null;
                var files = owner.TerminalFilesForTest;
                var binding = files.OwnedTransfer;
                if (binding == null) return null;
                int inbound = -1, outbound = -1;
                CollectorLineOriginForTest inboundOrigin = null, outboundOrigin = null;
                for (int at = 0; at < full.Length; at++)
                {
                    selectedTerminalVisits++;
                    if (!payload.IsCurrent || !work.IsRunning) return null;
                    var row = membership.TerminalOrigin(at, membership.TerminalEvent(at));
                    if (row == null) return null;
                    if (row.ProducerLine == binding.InboundLine)
                    { if (inbound >= 0) return null; inbound = at; inboundOrigin = row; }
                    if (row.ProducerLine == binding.OutboundLine)
                    { if (outbound >= 0) return null; outbound = at; outboundOrigin = row; }
                }
                if (inbound < 0 || outbound < 0 || inbound == outbound) return null;
                bool sawInbound = false, sawOutbound = false;
                foreach (object terminal in terminals)
                {
                    selectedTerminalVisits++;
                    if (!payload.IsCurrent || !work.IsRunning) return null;
                    int at = Object.ReferenceEquals(terminal, membership.TerminalEvent(inbound)) ? inbound :
                        Object.ReferenceEquals(terminal, membership.TerminalEvent(outbound)) ? outbound : -1;
                    if (at < 0 || membership.TerminalOrigin(at, terminal) == null) return null;
                    string raw = full[at];
                    if (raw.Length > LocalPublicationInputLimits.LineCodeUnits ||
                        4L * raw.Length > LocalPublicationInputLimits.ProducerCodeUnits - CumulativeCodeUnits) return null;
                    selectedTerminalUnits += 4L * raw.Length;
                    if (!CompletedTerminal(raw, at == inbound ? "inbound" : "outbound", binding.Session, binding.Transfer)) return null;
                    if (at == inbound) { if (sawInbound) return null; sawInbound = true; }
                    else { if (sawOutbound) return null; sawOutbound = true; }
                }
                if (!sawInbound || !sawOutbound || !payload.IsCurrent || !files.PayloadsMatch(262144)) return null;
                selectedTerminals = new SelectedTerminalJoinForTest(this, payload, binding.Session, binding.Transfer,
                    inbound, outbound, membership.TerminalEvent(inbound), membership.TerminalEvent(outbound),
                    full[inbound], full[outbound], inboundOrigin, outboundOrigin);
                return selectedTerminals.IsCurrent ? selectedTerminals : null;
            }

            internal static bool CompletedTerminal(string raw, string direction, string session, string transfer)
            {
                var fields = CollectorMembershipForTest.TerminalFields(raw);
                return fields != null && CollectorMembershipForTest.Value(fields, "event") == "transfer_terminal" &&
                    CollectorMembershipForTest.Value(fields, "direction") == direction &&
                    CollectorMembershipForTest.Value(fields, "terminal_session_id") == session &&
                    CollectorMembershipForTest.Value(fields, "terminal_transfer_id") == transfer &&
                    CollectorMembershipForTest.Value(fields, "error_code") == "(none)" &&
                    CollectorMembershipForTest.Value(fields, "reason") == "Transfer complete." &&
                    CollectorMembershipForTest.Value(fields, "file_size_bytes") == "262144" &&
                    CollectorMembershipForTest.Value(fields, "bytes_transferred") == "262144";
            }

            internal bool OwnsSelectedTerminals(SelectedTerminalJoinForTest result, SelectedPayloadOwnerForTest payload,
                string session, string transfer, int inbound, int outbound, object inboundEvent, object outboundEvent,
                string inboundRaw, string outboundRaw, CollectorLineOriginForTest inboundOrigin, CollectorLineOriginForTest outboundOrigin)
            {
                if (!Object.ReferenceEquals(selectedTerminals, result) || !Object.ReferenceEquals(payloadOwner, payload) ||
                    !payload.IsCurrent || !work.IsRunning || !Object.ReferenceEquals(full[inbound], inboundRaw) ||
                    !Object.ReferenceEquals(full[outbound], outboundRaw)) return false;
                var files = owner.TerminalFilesForTest;
                var binding = files.OwnedTransfer;
                return binding != null && binding.Session == session && binding.Transfer == transfer &&
                    binding.InboundLine == inboundOrigin.ProducerLine && binding.OutboundLine == outboundOrigin.ProducerLine &&
                    SameTerminalOrigin(membership.TerminalOrigin(inbound, inboundEvent), inboundOrigin) &&
                    SameTerminalOrigin(membership.TerminalOrigin(outbound, outboundEvent), outboundOrigin) && files.PayloadsMatch(262144);
            }
            private static bool SameTerminalOrigin(CollectorLineOriginForTest current, CollectorLineOriginForTest original)
            { return current != null && current.SemanticLine == original.SemanticLine && current.ProducerLine == original.ProducerLine &&
                current.RetainedLine == original.RetainedLine && current.WriterLine == original.WriterLine; }
            internal bool SelectedTerminalsRetired { get { return selectedTerminals == null || !selectedTerminals.IsCurrent; } }
        }

        // Non-owning association, never consumed by acceptance or route logic.
        public sealed class SelectedTerminalJoinForTest : IDisposable
        {
            private readonly SelectedTerminalJoinForTest original;
            private readonly MembershipBorrowerForTest borrower;
            private readonly SelectedPayloadOwnerForTest payload;
            private readonly string session, transfer, inboundRaw, outboundRaw;
            private readonly int inbound, outbound;
            private readonly object inboundEvent, outboundEvent;
            private readonly CollectorLineOriginForTest inboundOrigin, outboundOrigin;
            private int disposed;
            internal SelectedTerminalJoinForTest(MembershipBorrowerForTest borrower, SelectedPayloadOwnerForTest payload,
                string session, string transfer, int inbound, int outbound, object inboundEvent, object outboundEvent,
                string inboundRaw, string outboundRaw, CollectorLineOriginForTest inboundOrigin, CollectorLineOriginForTest outboundOrigin)
            {
                original = this; this.borrower = borrower; this.payload = payload; this.session = session; this.transfer = transfer;
                this.inbound = inbound; this.outbound = outbound; this.inboundEvent = inboundEvent; this.outboundEvent = outboundEvent;
                this.inboundRaw = inboundRaw; this.outboundRaw = outboundRaw; this.inboundOrigin = inboundOrigin; this.outboundOrigin = outboundOrigin;
            }
            public bool IsCurrent { get { return Object.ReferenceEquals(original, this) && disposed == 0 &&
                borrower.OwnsSelectedTerminals(this, payload, session, transfer, inbound, outbound, inboundEvent, outboundEvent,
                    inboundRaw, outboundRaw, inboundOrigin, outboundOrigin); } }
            public CollectorLineOriginForTest InboundOrigin { get { return inboundOrigin; } }
            public CollectorLineOriginForTest OutboundOrigin { get { return outboundOrigin; } }
            public void Dispose() { if (Object.ReferenceEquals(original, this)) Interlocked.Exchange(ref disposed, 1); }
        }
        public sealed partial class Worker
        {
            public bool SelectedTerminalsRetiredForTest
            { get { return Object.ReferenceEquals(frontierOriginal, this) && (membershipBorrower == null || membershipBorrower.SelectedTerminalsRetired); } }
        }
    }
}
#endif
