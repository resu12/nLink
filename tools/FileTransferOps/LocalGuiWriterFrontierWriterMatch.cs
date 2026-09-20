using System;
using NLink.Tools.FrontierLedger;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class FrontierFinalizerBinding
        {
            private bool writerMatchAttempted;
            private FrontierWriterMatch writerMatch;
            public FrontierWriterMatch WriterMatch
            { get { lock (sourceSync) { return OwnsWriterMatch(writerMatch) ? writerMatch : null; } } }

            public FrontierWriterMatch TryMatchProcessedWriters(GuiSmokeLedgerStreamingResult result)
            {
                lock (sourceSync)
                {
                    // Copies/foreign results cannot consume or retire this
                    // original binding's one matching attempt.
                    if (!IsCurrent || sourceReader == null || result == null || !result.OwnsStreamCapture(result.StreamCapture)) return null;
                    var capture = result.StreamCapture;
                    if (!capture.MatchesOriginalSource(sourceReader)) return null;
                    bool success = false;
                    try
                    {
                        if (writerMatchAttempted) return null;
                        writerMatchAttempted = true;
                        var snapshot = capture.Validation;
                        if (!capture.OwnsValidation(snapshot) || !snapshot.IsCurrent || !capture.HasRetainedOutputs ||
                            capture.AcceptanceRunId != finalizer.run || capture.ScenarioRunId != finalizer.scenario ||
                            snapshot.HasWriterClosureErrors || snapshot.Writers.Count != 2) return null;
                        var first = snapshot.Writers[0]; var second = snapshot.Writers[1];
                        if (!sourceReader.MatchWriterFacts(new[] { first.WriterId, second.WriterId },
                            new[] { first.LastSequence, second.LastSequence })) return null;
                        writerMatch = new FrontierWriterMatch(this, result, capture, snapshot);
                        success = OwnsWriterMatch(writerMatch); return success ? writerMatch : null;
                    }
                    finally { if (!success) Dispose(); }
                }
            }

            internal bool OwnsWriterMatch(FrontierWriterMatch value)
            {
                lock (sourceSync)
                {
                    if (value == null || !Object.ReferenceEquals(writerMatch, value) || !IsCurrent) return false;
                    if (value.InputsCurrent(sourceReader)) return true;
                    Dispose(); return false;
                }
            }
            internal void DisposeWriterMatch(FrontierWriterMatch value)
            { lock (sourceSync) { if (Object.ReferenceEquals(writerMatch, value) && IsCurrent) Dispose(); } }
        }

        // Original membership/terminal-sequence join only, not full finalizer
        // approval, recovery validation, persistence or publication authority.
        public sealed class FrontierWriterMatch : IDisposable
        {
            private readonly FrontierFinalizerBinding owner;
            private readonly GuiSmokeLedgerStreamingResult result;
            private readonly GuiSmokeLedgerStreamCapture capture;
            public GuiSmokeLedgerValidationSnapshot Validation { get; private set; }
            public int WriterCount { get { return 2; } }
            public bool IsCurrent { get { return owner.OwnsWriterMatch(this); } }
            public bool LiveProofEligible { get { return false; } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            internal FrontierWriterMatch(FrontierFinalizerBinding owner, GuiSmokeLedgerStreamingResult result,
                GuiSmokeLedgerStreamCapture capture, GuiSmokeLedgerValidationSnapshot snapshot)
            { this.owner = owner; this.result = result; this.capture = capture; Validation = snapshot; }
            internal bool InputsCurrent(FrontierSourceRead source)
            {
                return result.OwnsStreamCapture(capture) && capture.OwnsValidation(Validation) &&
                    capture.MatchesOriginalSource(source) && capture.HasRetainedOutputs && Validation.IsCurrent;
            }
            public bool MatchesResult(GuiSmokeLedgerStreamingResult value)
            { return Object.ReferenceEquals(result, value) && IsCurrent; }
            public void Dispose() { owner.DisposeWriterMatch(this); }
        }
    }
}
