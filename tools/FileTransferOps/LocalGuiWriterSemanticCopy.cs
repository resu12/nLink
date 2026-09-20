using System;
using System.Collections.ObjectModel;
using System.IO;
using NLink.Tools.FrontierLedger;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class FrontierFinalizerBinding
        {
            private bool semanticCopyAttempted;
            private FrontierSemanticCopy semanticCopy;
            public FrontierSemanticCopy SemanticCopy
            { get { lock (sourceSync) { return OwnsSemanticCopy(semanticCopy) ? semanticCopy : null; } } }

            // No caller result, destination, bytes, hash or success flag. Consume
            // the original cursor match and share the original cumulative clock.
            public FrontierSemanticCopy TryCopySemanticEvidence()
            {
                lock (sourceSync)
                {
                    if (!IsCurrent) return null;
                    bool success = false;
                    try
                    {
                        if (semanticCopyAttempted) return null; semanticCopyAttempted = true;
                        if (!OwnsCursorMatch(cursorMatch) || sourceReader == null || sourceReader.ProcessingWorkActive || !sourceReader.ReachedEnd) return null;
                        var adoption = files.GetOriginalAdoption();
                        if (adoption == null) return null;
                        var capture = cursorMatch.Result.StreamCapture;
                        // The legacy finalizer owns its separate analyzer path.
                        var path = Path.Combine(finalizer.root, "filetransfer-frontier-retained-log-slice.log");
                        GuiSmokeLedgerCapturedOutput output = null;
                        if (!adoption.TryRunRecoveryRead(() =>
                        {
                            output = capture.CopySemantic(path, () => adoption.IsSourceVerified);
                            return output != null;
                        })) return null;
                        semanticCopy = new FrontierSemanticCopy(this, cursorMatch, output, capture.SemanticOrigins);
                        success = OwnsSemanticCopy(semanticCopy); return success ? semanticCopy : null;
                    }
                    catch { return null; }
                    finally { if (!success) Dispose(); }
                }
            }
            internal bool OwnsSemanticCopy(FrontierSemanticCopy value)
            {
                lock (sourceSync)
                {
                    if (value == null || !Object.ReferenceEquals(semanticCopy, value) || !IsCurrent) return false;
                    if (Object.ReferenceEquals(value.Cursor, cursorMatch) && OwnsCursorMatch(cursorMatch) &&
                        cursorMatch.Result.StreamCapture.OwnsSemanticCopy(value.Output)) return true;
                    Dispose(); return false;
                }
            }
            internal void DisposeSemanticCopy(FrontierSemanticCopy value)
            { lock (sourceSync) { if (Object.ReferenceEquals(semanticCopy, value)) Dispose(); } }
        }

        // Copy subgate only. Persistence and parent/primary custody need separate operations.
        public sealed class FrontierSemanticCopy : IDisposable
        {
            private readonly FrontierFinalizerBinding owner;
            internal readonly FrontierCursorMatch Cursor;
            public GuiSmokeLedgerCapturedOutput Output { get; private set; }
            public ReadOnlyCollection<int> Origins { get; private set; }
            public bool IsCurrent { get { return owner.OwnsSemanticCopy(this); } }
            public bool SemanticCopyValidated { get { return IsCurrent; } }
            public bool LiveProofEligible { get { return false; } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            internal FrontierSemanticCopy(FrontierFinalizerBinding owner, FrontierCursorMatch cursor,
                GuiSmokeLedgerCapturedOutput output, ReadOnlyCollection<int> origins)
            { this.owner = owner; Cursor = cursor; Output = output; Origins = origins; }
            public void Dispose() { owner.DisposeSemanticCopy(this); }
        }
    }
}
