using System;
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
                private bool witnessesAttempted;
                private NativeWitnessBatch witnessBatch;
                private bool resolutionAttempted;

                // Run the trusted primary's diagnostic selection inside the
                // same native lifetime and cumulative work/record budget.
                // The callback is not an evaluator or publication authority.
                public NativeWitnessBatch TryResolveWitnesses(EvaluationPair pair, object candidateContext, string[] candidateFull,
                    string[] candidateCompact, object candidateFullView, object candidateCompactView,
                    NativePayloadSet payloads, object failure, object candidates, object proof, object index,
                    Func<Action, NativeWitnessSelection> resolve)
                {
                    lock (inputs.owner.frontierWorkerSync) lock (inputs.sync)
                    {
                        if (!Original || state != 3 || pair == null || !Object.ReferenceEquals(issued, pair) ||
                            !Matches(candidateContext, candidateFull, candidateCompact, candidateFullView, candidateCompactView) ||
                            payloads == null || !OriginalPayloads(payloads)) return null;
                        bool accepted = false, resolving = false; inputs.work.Start();
                        try
                        {
                            if (resolutionAttempted || witnessesAttempted || resolve == null || !Current()) return null;
                            resolutionAttempted = true; resolving = true;
                            var originalState = LocalFrontierDiagnosticState.Capture(failure, candidates, proof, index,
                                fullView, compactView, full.Length, inputs.VisitEvaluation);
                            var selected = resolve(delegate
                            {
                                if (!resolving) throw new InvalidOperationException("frontier_resolution_ended");
                                inputs.VisitEvaluation();
                            });
                            resolving = false;
                            if (selected == null || !payloads.Contains(selected.Payload) || String.IsNullOrEmpty(selected.Snapshot) ||
                                selected.Snapshot.Length > 16384 || !originalState.IsCurrent() || !Current()) return null;
                            var result = TryCaptureWitnesses(pair, candidateContext, candidateFull, candidateCompact,
                                candidateFullView, candidateCompactView, payloads, selected.Payload, selected.Indices);
                            inputs.work.Start();
                            if (result != null)
                            {
                                if (!originalState.IsCurrent() || !Current()) return null;
                                result.BindSnapshot(selected.Snapshot, originalState);
                            }
                            accepted = result != null; return result;
                        }
                        catch { return null; }
                        finally { resolving = false; inputs.work.Stop(); if (!accepted) inputs.Close(); }
                    }
                }

                // A bounded native-origin selection, NOT a receipt/failure match
                // or a publication capability. The primary chooses the indices.
                public NativeWitnessBatch TryCaptureWitnesses(EvaluationPair pair, object candidateContext, string[] candidateFull,
                    string[] candidateCompact, object candidateFullView, object candidateCompactView,
                    NativePayloadSet payloads, NativePayload payload, int[] oneBasedFullIndices)
                {
                    lock (inputs.owner.frontierWorkerSync) lock (inputs.sync)
                    {
                        if (!Original || state != 3 || pair == null || !Object.ReferenceEquals(issued, pair) ||
                            !Matches(candidateContext, candidateFull, candidateCompact, candidateFullView, candidateCompactView) ||
                            payloads == null || !OriginalPayloads(payloads) || !payloads.Contains(payload)) return null;
                        bool accepted = false; NativeWitness[] rows = null; inputs.work.Start();
                        try
                        {
                            if (witnessesAttempted || !Current()) return null;
                            witnessesAttempted = true;
                            if (oneBasedFullIndices == null || oneBasedFullIndices.Length < 1 || oneBasedFullIndices.Length > 64) return null;
                            // Copy caller-owned selection once. Neither it nor an
                            // exported row array remains the batch's authority.
                            var indices = (int[])oneBasedFullIndices.Clone();
                            string writer; var fact = inputs.facts.PayloadAt(payloads.IndexOf(payload), out writer);
                            if (fact == null) return null;
                            var completion = CaptureRange(fact.Completion); var marker = CaptureRange(fact.Marker);
                            if (completion == null || marker == null) return null;
                            rows = new NativeWitness[indices.Length]; int previous = 0; long units = 0;
                            using (var sha = SHA256.Create())
                            {
                                for (int i = 0; i < indices.Length; i++)
                                {
                                    inputs.VisitEvaluation(); int index = indices[i];
                                    if (index <= previous || index > fullCopy.Length) return null;
                                    string text = fullCopy[index - 1];
                                    if (text == null || text.Length > 262144 ||
                                        text.Length > LocalPublicationInputLimits.ProducerCodeUnits - units) return null;
                                    units += text.Length;
                                    int physical = fullOrigins[index - 1], compactIndex = Array.BinarySearch(compactOrigins, physical);
                                    long start = inputs.facts.LineStart(physical), end = inputs.facts.LineEnd(physical);
                                    if (start < 0 || end <= start) return null;
                                    var bytes = new UTF8Encoding(false, true).GetBytes(text);
                                    string hash;
                                    try { hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
                                    finally { Array.Clear(bytes, 0, bytes.Length); }
                                    rows[i] = new NativeWitness(index, compactIndex < 0 ? 0 : compactIndex + 1, inputs.facts.SemanticIndex(physical), physical, start, end - start, text, hash);
                                    previous = index;
                                }
                            }
                            if (!Current()) return null;
                            witnessBatch = new NativeWitnessBatch(this, pair, payloads, payload, rows, completion, marker);
                            accepted = true; return witnessBatch;
                        }
                        catch { return null; }
                        finally
                        {
                            inputs.work.Stop();
                            if (!accepted)
                            {
                                if (rows != null) foreach (var row in rows) if (row != null) row.Clear();
                                inputs.Close();
                            }
                        }
                    }
                }
                private NativeWitnessRange CaptureRange(LocalFrontierFileManifest.Range range)
                {
                    inputs.VisitEvaluation();
                    // Windows writer receipts hash PersistedText, excluding the
                    // final CRLF appended by RollingFileLogger. Cursor offsets
                    // include that exact two-byte delimiter (not a tolerance).
                    int first = inputs.facts.LineAt(range.Offset), last = inputs.facts.RangeLastLine(checked(range.Offset + range.Length + 2));
                    if (first < 1 || last < first || (long)last - first != range.Last - range.First) return null;
                    return new NativeWitnessRange(range, first, last);
                }
                private bool OriginalWitnesses(NativeWitnessBatch candidate)
                {
                    return Original && state == 3 && candidate != null && Object.ReferenceEquals(witnessBatch, candidate) &&
                        Object.ReferenceEquals(issued, candidate.Pair) && OriginalPayloads(candidate.Payloads) && candidate.Payloads.Contains(candidate.Payload);
                }
                internal bool WitnessesCurrent(NativeWitnessBatch candidate)
                { lock (inputs.owner.frontierWorkerSync) lock (inputs.sync) { return OriginalWitnesses(candidate) && CheckCurrent(); } }
                internal NativeWitness[] CopyWitnesses(NativeWitnessBatch candidate)
                { lock (inputs.owner.frontierWorkerSync) lock (inputs.sync) { return OriginalWitnesses(candidate) && CheckCurrent() ? candidate.CopyRows() : null; } }
                internal bool OwnsWitness(NativeWitnessBatch candidate, NativeWitness row)
                { lock (inputs.owner.frontierWorkerSync) lock (inputs.sync) { return OriginalWitnesses(candidate) && candidate.Contains(row) && CheckCurrent(); } }
                internal void DisposeWitnesses(NativeWitnessBatch candidate)
                { lock (inputs.owner.frontierWorkerSync) lock (inputs.sync) { if (OriginalWitnesses(candidate)) inputs.Close(); } }
                private void CloseWitnesses() { if (witnessBatch != null) witnessBatch.Clear(); }
            }

            // Callback input data only. Constructing it grants no custody.
            public sealed class NativeWitnessSelection
            {
                internal readonly NativePayload Payload;
                internal readonly int[] Indices;
                internal readonly string Snapshot;
                public NativeWitnessSelection(NativePayload payload, int[] indices, string snapshot)
                { Payload = payload; Indices = indices == null ? null : (int[])indices.Clone(); Snapshot = snapshot; }
            }

            public sealed partial class NativeWitnessBatch : IDisposable
            {
                private readonly EvaluationCustody owner;
                internal readonly EvaluationPair Pair;
                internal readonly NativePayloadSet Payloads;
                private readonly NativeWitness[] rows;
                private LocalFrontierDiagnosticState originalState;
                public NativePayload Payload { get; private set; }
                public NativeWitnessRange Completion { get; private set; }
                public NativeWitnessRange Marker { get; private set; }
                public string DiagnosticSnapshot { get; private set; }
                internal NativeWitnessBatch(EvaluationCustody owner, EvaluationPair pair, NativePayloadSet payloads, NativePayload payload,
                    NativeWitness[] rows, NativeWitnessRange completion, NativeWitnessRange marker)
                { this.owner = owner; Pair = pair; Payloads = payloads; Payload = payload; this.rows = rows; Completion = completion; Marker = marker; }
                public bool IsCurrent { get { return owner.WitnessesCurrent(this); } }
                public bool LiveProofEligible { get { return false; } }
                public bool NativeWriterCustodyEstablished { get { return false; } }
                public bool PublicationCompletionIssued { get { return false; } }
                public NativeWitness[] GetWitnesses() { return owner.CopyWitnesses(this); }
                public bool Owns(NativeWitness row) { return owner.OwnsWitness(this, row); }
                internal NativeWitness[] CopyRows() { return (NativeWitness[])rows.Clone(); }
                internal bool Contains(NativeWitness row)
                { if (row == null) return false; foreach (var original in rows) if (Object.ReferenceEquals(original, row)) return true; return false; }
                internal bool StateCurrent() { return originalState == null || originalState.IsCurrent(); }
                internal bool HasDiagnosticState { get { return originalState != null; } }
                internal string[] TerminalCatalog { get { return originalState == null ? null : originalState.TerminalCatalog; } }
                internal void BindSnapshot(string snapshot, LocalFrontierDiagnosticState state) { DiagnosticSnapshot = snapshot; originalState = state; }
                internal void Clear() { ClearPreparation(); originalState = null; DiagnosticSnapshot = null; foreach (var row in rows) row.Clear(); }
                public void Dispose() { owner.DisposeWitnesses(this); }
            }

            // Immutable diagnostic values; copied fields do not carry custody.
            public sealed class NativeWitness
            {
                public int FullIndex { get; private set; }
                public int CompactIndex { get; private set; }
                public int SemanticIndex { get; private set; }
                public int SourceLine { get; private set; }
                public long ByteOffset { get; private set; }
                public long RawByteCount { get; private set; }
                public string Text { get; private set; }
                public string LineSha256 { get; private set; }
                internal NativeWitness(int full, int compact, int semantic, int source, long offset, long bytes, string text, string hash)
                { FullIndex = full; CompactIndex = compact; SemanticIndex = semantic; SourceLine = source; ByteOffset = offset; RawByteCount = bytes; Text = text; LineSha256 = hash; }
                internal void Clear() { Text = null; }
            }
            public sealed class NativeWitnessRange
            {
                public int FirstSourceLine { get; private set; }
                public int LastSourceLine { get; private set; }
                public long ByteOffset { get; private set; }
                public long ByteCount { get; private set; }
                public long BatchGeneration { get; private set; }
                public long FirstSequence { get; private set; }
                public long LastSequence { get; private set; }
                public string RawSha256 { get; private set; }
                public string BatchSha256 { get; private set; }
                internal NativeWitnessRange(LocalFrontierFileManifest.Range range, int first, int last)
                {
                    FirstSourceLine = first; LastSourceLine = last; ByteOffset = range.Offset; ByteCount = range.Length;
                    BatchGeneration = range.Generation; FirstSequence = range.First; LastSequence = range.Last;
                    RawSha256 = BitConverter.ToString(range.RawHash).Replace("-", "").ToLowerInvariant();
                    BatchSha256 = BitConverter.ToString(range.BatchHash).Replace("-", "").ToLowerInvariant();
                }
            }
        }
    }
}
