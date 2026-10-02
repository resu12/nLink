#if NLINK_TEST_NATIVE_CONTENT_ACQUISITION
using System;
using System.Management.Automation;
using System.Security.Cryptography;
using System.Text;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        public sealed partial class FrontierWorkerInputs
        {
            public sealed partial class EvaluationCustody
            {
                private const int ContentInputMillisecondsForTest = 150000;
                private bool contentAcquisitionAttemptedForTest;

                // Test profile only. Input selection is not content/publication
                // proof and never consumes the existing receipt witness slot.
                public ContentInputSelectionForTest TryAcquireContentInputsForTest(bool enabled, EvaluationPair pair,
                    object candidateContext, string[] candidateFull, string[] candidateCompact,
                    object candidateFullView, object candidateCompactView, object[] events, object selectedEvent,
                    Func<ContentInputCursorForTest, int[]> select)
                {
                    if (!enabled) return null;
                    lock (inputs.owner.frontierWorkerSync) lock (inputs.sync)
                    {
                        if (!Original || state != 3 || pair == null || !Object.ReferenceEquals(issued, pair) ||
                            !Matches(candidateContext, candidateFull, candidateCompact, candidateFullView, candidateCompactView)) return null;
                        if (contentAcquisitionAttemptedForTest) return null;
                        contentAcquisitionAttemptedForTest = true;
                        inputs.work.Start();
                        var cursor = new ContentInputCursorForTest(this, events, selectedEvent);
                        try
                        {
                            cursor.CheckInputs();
                            if (select == null) throw new InvalidOperationException("content_selection_missing");
                            int[] indices = select(cursor);
                            if (indices == null || indices.Length < 1 || indices.Length > 64)
                                throw new InvalidOperationException("content_witness_limit");
                            indices = (int[])indices.Clone();
                            var rows = new ContentInputWitnessForTest[indices.Length];
                            int previous = 0; bool selected = false;
                            using (var sha = SHA256.Create())
                            {
                                for (int i = 0; i < indices.Length; i++)
                                {
                                    int ordinal = indices[i];
                                    if (ordinal <= previous || ordinal > fullCopy.Length) throw new InvalidOperationException("content_selection_order");
                                    string text = cursor.Line(ordinal);
                                    int physical = cursor.PhysicalOrigin(ordinal);
                                    int compactIndex = cursor.CompactIndex(physical);
                                    byte[] bytes = new UTF8Encoding(false, true).GetBytes(text);
                                    string hash;
                                    try { hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
                                    finally { Array.Clear(bytes, 0, bytes.Length); }
                                    rows[i] = new ContentInputWitnessForTest(ordinal, compactIndex < 0 ? 0 : compactIndex + 1, physical, hash);
                                    previous = ordinal; selected |= ordinal == cursor.SelectedOrdinal;
                                }
                            }
                            if (!selected) throw new InvalidOperationException("content_selected_event_omitted");
                            cursor.CheckInputs();
                            return new ContentInputSelectionForTest(rows, cursor.RecordVisits, cursor.CodeUnitVisits,
                                inputs.work.ElapsedMilliseconds, cursor.SelectedOrdinal);
                        }
                        catch (Exception error)
                        {
                            return new ContentInputSelectionForTest(error.Message, cursor.RecordVisits, cursor.CodeUnitVisits,
                                inputs.work.ElapsedMilliseconds);
                        }
                        finally { cursor.End(); inputs.work.Stop(); }
                    }
                }
                public sealed class ContentInputCursorForTest
                {
                    private readonly EvaluationCustody custody;
                    private readonly object[] events;
                    private readonly object[] eventCopy;
                    private readonly object selected;
                    private bool active = true;
                    public int SelectedOrdinal { get; private set; }
                    public int Count { get { Check(); return custody.fullCopy.Length; } }
                    public long RecordVisits { get; private set; }
                    public long CodeUnitVisits { get; private set; }
                    internal ContentInputCursorForTest(EvaluationCustody custody, object[] events, object selected)
                    {
                        this.custody = custody; this.events = events; this.selected = selected;
                        eventCopy = events == null || events.Length > LocalPublicationInputLimits.InputRecords ? null : (object[])events.Clone();
                        // Reuse all prior native inspections, the original source
                        // character accounting and the shared cumulative clock.
                        RecordVisits = custody.inputs.evaluationVisits;
                        CodeUnitVisits = custody.inputs.sourceUnits;
                    }
                    private void Check()
                    {
#if NLINK_TEST_NATIVE_SELECTION_PROFILE
                        long profileStart = NLink.Tests.NativeContentSelectionProfileForTest.CheckStarted();
                        try {
#endif
                        if (!active) throw new InvalidOperationException("content_cursor_ended");
#if NLINK_TEST_NATIVE_CONTENT_TERMINAL_SELECTION
                        int maximumMilliseconds = custody.terminalContentAcquisitionForTest ? TerminalContentMillisecondsForTest : ContentInputMillisecondsForTest;
#else
                        int maximumMilliseconds = ContentInputMillisecondsForTest;
#endif
                        if (RecordVisits > LocalPublicationInputLimits.ProducerRecords ||
                            CodeUnitVisits > LocalPublicationInputLimits.ProducerCodeUnits ||
                            custody.inputs.work.ElapsedMilliseconds > maximumMilliseconds)
                            throw new InvalidOperationException("content_input_budget");
                        if (!custody.Original || custody.state != 3 || !custody.inputs.sourceEnded ||
#if NLINK_TEST_NATIVE_SELECTION_PROFILE
                            !custody.inputs.ContentSelectionResourceCurrentForTest(maximumMilliseconds))
#else
                            !custody.inputs.ResourceCurrentWithin(maximumMilliseconds))
#endif
                            throw new InvalidOperationException("content_native_custody");
                        if (custody.inputs.work.ElapsedMilliseconds > maximumMilliseconds)
                            throw new InvalidOperationException("content_input_budget");
#if NLINK_TEST_NATIVE_SELECTION_PROFILE
                        } finally { NLink.Tests.NativeContentSelectionProfileForTest.CheckReturned(profileStart); }
#endif
                    }
                    private void Visit()
                    {
                        Check();
                        if (RecordVisits >= LocalPublicationInputLimits.ProducerRecords) throw new InvalidOperationException("content_input_budget");
                        RecordVisits++;
#if NLINK_TEST_NATIVE_CONTENT_TERMINAL_SELECTION
                        if (custody.terminalContentAcquisitionForTest) custody.VisitTerminalContentInputForTest(); else
#endif
                        custody.inputs.VisitEvaluation(); Check();
                    }
                    private void ChargeUnits(long units)
                    {
                        Check();
                        if (units < 0 || units > LocalPublicationInputLimits.ProducerCodeUnits - CodeUnitVisits)
                            throw new InvalidOperationException("content_input_budget");
                        CodeUnitVisits += units; Check();
                    }
                    internal void CheckInputs()
                    {
#if NLINK_TEST_NATIVE_SELECTION_PROFILE
                        long profileStart = NLink.Tests.NativeContentSelectionProfileForTest.ScanStarted(events == null ? 0 : events.Length);
                        try {
#endif
                        Check();
                        if (events == null || events.Length != custody.fullCopy.Length ||
                            events.Length > LocalPublicationInputLimits.InputRecords || selected == null)
                            throw new InvalidOperationException("content_original_index");
                        int matches = 0;
                        for (int i = 0; i < events.Length; i++)
                        {
                            Visit();
                            var row = PSObject.AsPSObject(events[i]);
                            var raw = row.Properties["RawLine"] as PSNoteProperty;
                            var path = row.Properties["FilePath"] as PSNoteProperty;
                            if (path != null && path.GetType() == typeof(PSNoteProperty))
                            {
                                var pathText = path.Value as string;
                                if (pathText != null) ChargeUnits(pathText.Length);
                            }
                            if (!Object.ReferenceEquals(events[i], eventCopy[i]) || raw == null || path == null ||
                                raw.GetType() != typeof(PSNoteProperty) || path.GetType() != typeof(PSNoteProperty) ||
                                !Object.ReferenceEquals(raw.Value, custody.fullCopy[i]) ||
                                !Object.ReferenceEquals(custody.full[i], custody.fullCopy[i]) ||
                                !String.Equals(path.Value as string, custody.inputs.owner.SourcePath, StringComparison.Ordinal))
                                throw new InvalidOperationException("content_original_index");
                            if (Object.ReferenceEquals(events[i], selected)) { SelectedOrdinal = i + 1; matches++; }
#if NLINK_TEST_NATIVE_SELECTION_PROFILE
                            NLink.Tests.NativeContentSelectionProfileForTest.RowCompleted();
#endif
                        }
                        if (matches != 1) throw new InvalidOperationException("content_selected_event_ambiguous");
                        for (int i = 0; i < custody.compact.Length; i++)
                        {
                            Visit();
                            if (!Object.ReferenceEquals(custody.compact[i], custody.compactCopy[i]))
                                throw new InvalidOperationException("content_compact_changed");
                        }
                        Check();
#if NLINK_TEST_NATIVE_SELECTION_PROFILE
                        NLink.Tests.NativeContentSelectionProfileForTest.ScanCompleted();
                        } finally { NLink.Tests.NativeContentSelectionProfileForTest.ScanReturned(profileStart); }
#endif
                    }
                    internal int PhysicalOrigin(int ordinal)
                    { Visit(); return custody.fullOrigins[ordinal - 1]; }
                    internal int CompactIndex(int physical)
                    {
                        int low = 0, high = custody.compactOrigins.Length - 1;
                        while (low <= high)
                        {
                            Visit(); int middle = low + (high - low) / 2;
                            int value = custody.compactOrigins[middle];
                            if (value == physical) return middle;
                            if (value < physical) low = middle + 1; else high = middle - 1;
                        }
                        return -1;
                    }
                    public string Line(int oneBasedOrdinal)
                    {
                        Visit();
                        if (oneBasedOrdinal < 1 || oneBasedOrdinal > custody.fullCopy.Length)
                            throw new InvalidOperationException("content_original_ordinal");
                        string text = custody.fullCopy[oneBasedOrdinal - 1];
                        if (text == null || text.Length > LocalPublicationInputLimits.LineCodeUnits)
                            throw new InvalidOperationException("content_input_budget");
                        ChargeUnits(text.Length); return text;
                    }
                    internal void End() { active = false; }
                }
            }

            public sealed class ContentInputWitnessForTest
            {
                public int FullOrdinal { get; private set; }
                public int CompactOrdinal { get; private set; }
                public int PhysicalLine { get; private set; }
                public string SourceSha256 { get; private set; }
                internal ContentInputWitnessForTest(int full, int compact, int physical, string hash)
                { FullOrdinal = full; CompactOrdinal = compact; PhysicalLine = physical; SourceSha256 = hash; }
            }
            public sealed class ContentInputSelectionForTest
            {
                public string Status { get; private set; }
                public string FirstMissingEdge { get; private set; }
                public long RecordVisits { get; private set; }
                public long CodeUnitVisits { get; private set; }
                public long CumulativeMilliseconds { get; private set; }
                public int SelectedOrdinal { get; private set; }
                public bool ContentCoverageProven { get { return false; } }
                public string ContentCoverageMissingEdge { get { return "content_family_coverage_unavailable"; } }
                private readonly ContentInputWitnessForTest[] rows;
                public ContentInputWitnessForTest[] GetWitnesses() { return (ContentInputWitnessForTest[])rows.Clone(); }
                internal ContentInputSelectionForTest(ContentInputWitnessForTest[] rows, long records, long units, long ms, int selected)
                { this.rows = rows; Status = "prepared_native_input_selection"; FirstMissingEdge = "(none)";
                    RecordVisits = records; CodeUnitVisits = units; CumulativeMilliseconds = ms; SelectedOrdinal = selected; }
                internal ContentInputSelectionForTest(string reason, long records, long units, long ms)
                { rows = new ContentInputWitnessForTest[0]; Status = "unproven"; FirstMissingEdge = reason;
                    RecordVisits = records; CodeUnitVisits = units; CumulativeMilliseconds = ms; }
            }
        }
    }
}
#endif
