using System;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        public sealed partial class Worker
        {
            public FrontierWorkerInputs.EvaluationCustody TryBeginFrontierPrimary(SourceReader source, object context,
                string[] full, int[] fullOrdinals, string[] compact, int[] compactOrdinals)
            {
                lock (frontierWorkerSync)
                {
                    if (!Object.ReferenceEquals(frontierOriginal, this) || frontierWorkerInputs == null ||
                        !Object.ReferenceEquals(sourceReader, source)) return null;
                    return frontierWorkerInputs.BeginEvaluation(source, context, full, fullOrdinals, compact, compactOrdinals);
                }
            }
        }

        public sealed partial class FrontierWorkerInputs
        {
            private EvaluationCustody evaluation;
            private bool evaluationAttempted;
            private long evaluationVisits;

            internal EvaluationCustody BeginEvaluation(SourceReader source, object context,
                string[] full, int[] fullOrdinals, string[] compact, int[] compactOrdinals)
            {
                lock (sync)
                {
                    if (!OriginalSource(source)) return null;
                    bool accepted = false; work.Start();
                    try
                    {
                        if (evaluationAttempted) return null;
                        evaluationAttempted = true;
                        if (!ResourceCurrent || !sourceEnded || context == null || full == null || compact == null ||
                            fullOrdinals == null || compactOrdinals == null || full.Length > LocalPublicationInputLimits.InputRecords ||
                            compact.Length > full.Length || fullOrdinals.Length != full.Length || compactOrdinals.Length != compact.Length) return null;
                        evaluationVisits = sourceLines.Count;
                        var fullCopy = new string[full.Length]; var compactCopy = new string[compact.Length];
                        var fullOrigins = CaptureOrigins(full, fullOrdinals, fullCopy);
                        var compactOrigins = CaptureOrigins(compact, compactOrdinals, compactCopy);
                        if (fullOrigins == null || compactOrigins == null) return null;
                        int position = 0;
                        for (int i = 0; i < compactOrigins.Length; i++)
                        {
                            VisitEvaluation();
                            while (position < fullOrigins.Length && fullOrigins[position] < compactOrigins[i]) { VisitEvaluation(); position++; }
                            if (position == fullOrigins.Length || fullOrigins[position] != compactOrigins[i] ||
                                !Object.ReferenceEquals(fullCopy[position], compactCopy[i])) return null;
                        }
                        CheckSource();
                        evaluation = new EvaluationCustody(this, context, full, fullCopy, fullOrigins, compact, compactCopy, compactOrigins);
                        accepted = true; return evaluation;
                    }
                    catch { return null; }
                    finally { work.Stop(); if (!accepted) Close(); }
                }
            }

            private void VisitEvaluation()
            {
                if (++evaluationVisits > LocalPublicationInputLimits.EvaluationVisits ||
                    work.ElapsedMilliseconds > (evaluation == null ? LocalPublicationInputLimits.ProducerMilliseconds : LocalPublicationInputLimits.EvaluationMilliseconds))
                    throw new InvalidOperationException("frontier_evaluation_budget");
            }
            private int[] CaptureOrigins(string[] lines, int[] ordinals, string[] snapshot)
            {
                var origins = new int[lines.Length]; int previous = 0;
                for (int i = 0; i < lines.Length; i++)
                {
                    VisitEvaluation(); int ordinal = ordinals[i]; string line = lines[i];
                    if (ordinal <= previous || ordinal > sourceLines.Count ||
                        !Object.ReferenceEquals(sourceLines[ordinal - 1], line)) return null;
                    snapshot[i] = line; origins[i] = facts.SemanticOrigin(ordinal - 1); previous = ordinal;
                }
                return origins;
            }
            private void CloseEvaluation() { if (evaluation != null) evaluation.Clear(); }

            // This private-primary capability binds supplied original assembly
            // arrays and result objects. It neither evaluates nor publishes.
            public sealed partial class EvaluationCustody : IDisposable
            {
                private readonly FrontierWorkerInputs inputs;
                private object context, fullView, compactView;
                private string[] full, compact, fullCopy, compactCopy;
                private int[] fullOrigins, compactOrigins;
                private Guid fullInvocation, compactInvocation;
                private EvaluationPair issued;
                private int state; // full, compact, take, taken, retired

                internal EvaluationCustody(FrontierWorkerInputs inputs, object context, string[] full, string[] fullCopy,
                    int[] fullOrigins, string[] compact, string[] compactCopy, int[] compactOrigins)
                {
                    this.inputs = inputs; this.context = context; this.full = full; this.fullCopy = fullCopy;
                    this.fullOrigins = fullOrigins; this.compact = compact; this.compactCopy = compactCopy; this.compactOrigins = compactOrigins;
                }
                private bool Original { get { return Object.ReferenceEquals(inputs.evaluation, this); } }
                private bool Current()
                {
                    if (state == 4 || !inputs.ResourceCurrentWithin(LocalPublicationInputLimits.EvaluationMilliseconds) || !inputs.sourceEnded) return false;
                    for (int i = 0; i < full.Length; i++)
                    { inputs.VisitEvaluation(); if (!Object.ReferenceEquals(full[i], fullCopy[i])) return false; }
                    for (int i = 0; i < compact.Length; i++)
                    { inputs.VisitEvaluation(); if (!Object.ReferenceEquals(compact[i], compactCopy[i])) return false; }
                    return (witnessBatch == null || witnessBatch.StateCurrent()) && inputs.ResourceCurrentWithin(LocalPublicationInputLimits.EvaluationMilliseconds);
                }
                public bool RecordFull(string[] lines, object view) { return Record(false, lines, view); }
                public bool RecordCompact(string[] lines, object view) { return Record(true, lines, view); }
                private bool Record(bool isCompact, string[] lines, object view)
                {
                    lock (inputs.owner.frontierWorkerSync) lock (inputs.sync)
                    {
                        if (!Original) return false;
                        bool accepted = false; inputs.work.Start();
                        try
                        {
                            if (state != (isCompact ? 1 : 0) || view == null ||
                                !Object.ReferenceEquals(lines, isCompact ? compact : full) || !Current()) return false;
                            if (isCompact) { compactView = view; compactInvocation = Guid.NewGuid(); }
                            else { fullView = view; fullInvocation = Guid.NewGuid(); }
                            state++; accepted = true; return true;
                        }
                        catch { return false; }
                        finally { inputs.work.Stop(); if (!accepted) inputs.Close(); }
                    }
                }
                private bool Matches(object candidateContext, string[] candidateFull, string[] candidateCompact, object candidateFullView, object candidateCompactView)
                {
                    return Object.ReferenceEquals(context, candidateContext) && Object.ReferenceEquals(full, candidateFull) &&
                        Object.ReferenceEquals(compact, candidateCompact) && Object.ReferenceEquals(fullView, candidateFullView) &&
                        Object.ReferenceEquals(compactView, candidateCompactView);
                }
                public EvaluationPair Take(object candidateContext, string[] candidateFull, string[] candidateCompact, object candidateFullView, object candidateCompactView)
                {
                    lock (inputs.owner.frontierWorkerSync) lock (inputs.sync)
                    {
                        if (!Original) return null;
                        bool accepted = false; inputs.work.Start();
                        try
                        {
                            if (state != 2 || !Matches(candidateContext, candidateFull, candidateCompact, candidateFullView, candidateCompactView) || !Current()) return null;
                            issued = new EvaluationPair(inputs.owner.RequestId, fullInvocation, compactInvocation);
                            state = 3; accepted = true; return issued;
                        }
                        catch { return null; }
                        finally { inputs.work.Stop(); if (!accepted) inputs.Close(); }
                    }
                }
                public bool Owns(EvaluationPair pair, object candidateContext, string[] candidateFull, string[] candidateCompact, object candidateFullView, object candidateCompactView)
                {
                    lock (inputs.owner.frontierWorkerSync) lock (inputs.sync)
                    {
                        if (!Original || state != 3 || pair == null || !Object.ReferenceEquals(issued, pair) ||
                            !Matches(candidateContext, candidateFull, candidateCompact, candidateFullView, candidateCompactView)) return false;
                        return CheckCurrent();
                    }
                }
                public int GetPhysicalOrigin(EvaluationPair pair, string[] lines, object view, int oneBasedIndex)
                {
                    lock (inputs.owner.frontierWorkerSync) lock (inputs.sync)
                    {
                        if (!Original || state != 3 || pair == null || !Object.ReferenceEquals(issued, pair)) return 0;
                        int[] origins = Object.ReferenceEquals(full, lines) && Object.ReferenceEquals(fullView, view) ? fullOrigins :
                            Object.ReferenceEquals(compact, lines) && Object.ReferenceEquals(compactView, view) ? compactOrigins : null;
                        return origins != null && oneBasedIndex > 0 && oneBasedIndex <= origins.Length && CheckCurrent() ? origins[oneBasedIndex - 1] : 0;
                    }
                }
                private bool CheckCurrent()
                {
                    bool current = false; inputs.work.Start();
                    try { current = Current(); return current; }
                    catch { return false; }
                    finally { inputs.work.Stop(); if (!current) inputs.Close(); }
                }
                internal void Clear()
                {
                    CloseWitnesses();
                    ClosePayloads();
                    state = 4; issued = null; context = fullView = compactView = null;
                    full = compact = fullCopy = compactCopy = null; fullOrigins = compactOrigins = null;
                }
                public void Dispose()
                { lock (inputs.owner.frontierWorkerSync) lock (inputs.sync) { if (Original) inputs.Close(); } }
            }

            public sealed class EvaluationPair
            {
                public Guid RequestId { get; private set; }
                public Guid FullInvocationId { get; private set; }
                public Guid CompactInvocationId { get; private set; }
                internal EvaluationPair(Guid request, Guid full, Guid compact)
                { RequestId = request; FullInvocationId = full; CompactInvocationId = compact; }
            }
        }
    }
}
