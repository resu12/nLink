using System;
using System.IO;
using NLink.Tools.FrontierLedger;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class FrontierLedgerOrigin
        {
            internal FrontierLedgerObservation GetObservation(bool final)
            { if (!IsCurrent) return null; lock (sync) { return final ? finalObservation : initialObservation; } }
            internal bool MatchesCursor(GuiSmokeLedgerStreamCapture capture)
            {
                if (!IsCurrent) return false;
                lock (sync)
                {
                    if (!ResourceCurrent || initialObservation == null || finalObservation == null || capture.CursorLineStarts == null ||
                        capture.CursorLineStarts.Count != lineStarts.Count || capture.SourceLines != finalObservation.LineCount ||
                        capture.SourceBytes != finalObservation.CommittedBytes || capture.Bookmark != initialObservation.Bookmark ||
                        initialObservation.CommittedBytes > finalObservation.CommittedBytes || initialObservation.BytesRead > finalObservation.BytesRead ||
                        initialObservation.LineCount > finalObservation.LineCount || initialObservation.GenerationId != finalObservation.GenerationId ||
                        initialObservation.LedgerId != finalObservation.LedgerId) return false;
                    // Exact owned byte indices, not imported zero-gap counters.
                    for (int i = 0; i < lineStarts.Count; i++)
                        if (lineStarts[i] != capture.CursorLineStarts[i]) return false;
                    return true;
                }
            }
        }
        public sealed partial class FrontierFinalizerBinding
        {
            private bool cursorMatchAttempted;
            private FrontierCursorMatch cursorMatch;
            public FrontierCursorMatch CursorMatch
            { get { lock (sourceSync) { return OwnsCursorMatch(cursorMatch) ? cursorMatch : null; } } }

            public FrontierCursorMatch TryMatchProcessedCursor(GuiSmokeLedgerStreamingResult result)
            {
                lock (sourceSync)
                {
                    if (!IsCurrent || sourceReader == null || result == null || !result.OwnsStreamCapture(result.StreamCapture) ||
                        !result.StreamCapture.MatchesOriginalSource(sourceReader)) return null;
                    bool success = false;
                    try
                    {
                        if (cursorMatchAttempted) return null; cursorMatchAttempted = true;
                        var origin = LedgerOrigin; var capture = result.StreamCapture; var validation = capture.Validation;
                        if (!sourceReader.ProcessingWorkActive || !sourceReader.ReachedEnd || origin == null ||
                            writerMatch == null || !writerMatch.MatchesResult(result) || recoveryMatch == null || !recoveryMatch.MatchesResult(result) ||
                            !capture.OwnsValidation(validation) || !validation.IsCurrent || validation.SourceLineVisitCount != capture.SourceLines ||
                            !origin.MatchesCursor(capture) || !PrefixMatches(validation)) return null;
                        cursorMatch = new FrontierCursorMatch(this, result, origin.GetObservation(false), origin.GetObservation(true));
                        success = sourceReader.ProcessingWorkActive && OwnsCursorMatch(cursorMatch);
                        return success ? cursorMatch : null;
                    }
                    catch { return null; }
                    finally { if (!success) Dispose(); }
                }
            }
            private static bool PrefixMatches(GuiSmokeLedgerValidationSnapshot validation)
            {
                if (validation.InitialWriters == null || validation.InitialWriters.Count > validation.Writers.Count || validation.HasWriterClosureErrors) return false;
                foreach (var initial in validation.InitialWriters)
                {
                    GuiSmokeLedgerValidationWriter final = null;
                    foreach (var writer in validation.Writers)
                        if (String.Equals(initial.WriterId, writer.WriterId, StringComparison.Ordinal)) { final = writer; break; }
                    if (final == null || initial.FirstSequence != 1 || initial.LastSequence < 1 || initial.ObservedCount != initial.LastSequence ||
                        initial.FirstSequence != final.FirstSequence || initial.LastSequence > final.LastSequence || initial.ObservedCount > final.ObservedCount ||
                        initial.AcceptanceRunId != final.AcceptanceRunId || initial.ScenarioRunId != final.ScenarioRunId) return false;
                }
                return true;
            }
            internal bool OwnsCursorMatch(FrontierCursorMatch value)
            {
                lock (sourceSync)
                {
                    if (value == null || !Object.ReferenceEquals(cursorMatch, value) || !IsCurrent) return false;
                    if (value.InputsCurrent() && writerMatch != null && writerMatch.MatchesResult(value.Result) &&
                        recoveryMatch != null && recoveryMatch.MatchesResult(value.Result)) return true;
                    Dispose(); return false;
                }
            }
            internal void DisposeCursorMatch(FrontierCursorMatch value)
            { lock (sourceSync) { if (Object.ReferenceEquals(cursorMatch, value)) Dispose(); } }

            // Explicit optional path. The binding chooses its actual bookmark,
            // line count, source and destinations; none are public scalar imports.
            public GuiSmokeLedgerStreamingResult TryProcessObservedLedger(string[] semanticEventNames)
            {
                lock (sourceSync)
                {
                    if (!IsCurrent) return null;
                    bool success = false; GuiSmokeLedgerStreamingResult result = null;
                    try
                    {
                        if (semanticEventNames != null && semanticEventNames.Length > GuiSmokeLogLedgerStreamingProcessor.MaximumSemanticEventNames) return null;
                        var events = semanticEventNames == null ? new string[0] : (string[])semanticEventNames.Clone();
                        var source = TryOpenSource();
                        if (source == null || !source.TryBeginProcessingWork()) return null;
                        var final = TryCaptureLedgerFinal(); var markers = TryCaptureRecoveryMarkers();
                        if (final == null || markers == null) return null;
                        string prefix = Path.Combine(finalizer.root, "frontier-cursor-" + Guid.NewGuid().ToString("N"));
                        var keys = new string[markers.DeferredBatchKeys.Count]; markers.DeferredBatchKeys.CopyTo(keys, 0);
                        result = GuiSmokeLogLedgerStreamingProcessor.ProcessOwnedCursor(source, final.LineCount, final.Bookmark,
                            prefix + "-retained.tmp", prefix + "-writer.tmp", finalizer.run, finalizer.scenario,
                            keys, prefix + "-semantic.tmp", events, source.ProcessingLifetime);
                        if (TryMatchProcessedWriters(result) == null || TryMatchRecovery(result) == null ||
                            TryMatchProcessedCursor(result) == null || !source.TryFinishProcessingWork()) return null;
                        success = OwnsCursorMatch(cursorMatch); return success ? result : null;
                    }
                    catch { return null; }
                    finally
                    {
                        if (!success)
                        {
                            if (result != null && result.StreamCapture != null) result.StreamCapture.Dispose();
                            Dispose();
                        }
                    }
                }
            }
        }
        // Cursor subgate only. Copy, persistence and parent/primary custody
        // require their own operations; this is never runtime/publication authority.
        public sealed class FrontierCursorMatch : IDisposable
        {
            private readonly FrontierFinalizerBinding owner;
            internal readonly GuiSmokeLedgerStreamingResult Result;
            public FrontierLedgerObservation Initial { get; private set; }
            public FrontierLedgerObservation Final { get; private set; }
            public bool IsCurrent { get { return owner.OwnsCursorMatch(this); } }
            public bool CursorHistoryValidated { get { return IsCurrent; } }
            public bool LiveProofEligible { get { return false; } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            internal FrontierCursorMatch(FrontierFinalizerBinding owner, GuiSmokeLedgerStreamingResult result,
                FrontierLedgerObservation initial, FrontierLedgerObservation final)
            { this.owner = owner; Result = result; Initial = initial; Final = final; }
            internal bool InputsCurrent() { return Initial != null && Final != null && Initial.IsCurrent && Final.IsCurrent; }
            public bool MatchesResult(GuiSmokeLedgerStreamingResult value) { return Object.ReferenceEquals(Result, value) && IsCurrent; }
            public void Dispose() { owner.DisposeCursorMatch(this); }
        }
    }
}
