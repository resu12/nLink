using System;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        public sealed partial class FrontierWorkerInputs
        {
            public sealed partial class EvaluationCustody
            {
                internal NativeDiagnosticPreparation PrepareDiagnostic(NativeWitnessBatch batch)
                {
                    lock (inputs.owner.frontierWorkerSync) lock (inputs.sync)
                    {
                        if (!OriginalWitnesses(batch)) return null;
                        bool accepted = false; inputs.work.Start();
                        try
                        {
                            if (!batch.HasDiagnosticState || !batch.BeginPreparation() || !Current()) return null;
                            var descriptors = new NativeInputDescriptor[inputs.pins.Count];
                            for (int i = 0; i < descriptors.Length; i++)
                            { inputs.VisitEvaluation(); descriptors[i] = new NativeInputDescriptor(inputs.pins.EntryAt(i)); }
                            int payloadIndex = 10 + batch.Payloads.IndexOf(batch.Payload);
                            if (payloadIndex < 10 || payloadIndex >= descriptors.Length ||
                                descriptors[payloadIndex].ObservationId != batch.Payload.ObservationId ||
                                descriptors[payloadIndex].ByteCount != batch.Payload.ByteCount) return null;
                            var result = new NativeDiagnosticPreparation(this, batch, descriptors, inputs.owner,
                                Hex(inputs.scope.Binding), Hex(inputs.files.OfferHash), Hex(inputs.files.Facts.ReceiptHash));
                            if (!Current()) return null;
                            batch.SetPreparation(result); accepted = true; return result;
                        }
                        catch { return null; }
                        finally { inputs.work.Stop(); if (!accepted) inputs.Close(); }
                    }
                }
                private static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
                private bool OriginalPreparation(NativeDiagnosticPreparation candidate)
                { return candidate != null && OriginalWitnesses(candidate.Batch) && candidate.Batch.OwnsPreparation(candidate); }
                internal bool PreparationCurrent(NativeDiagnosticPreparation candidate)
                { lock (inputs.owner.frontierWorkerSync) lock (inputs.sync) { return OriginalPreparation(candidate) && CheckCurrent(); } }
                internal NativeInputDescriptor[] PreparationInputs(NativeDiagnosticPreparation candidate)
                { lock (inputs.owner.frontierWorkerSync) lock (inputs.sync) { return OriginalPreparation(candidate) && CheckCurrent() ? candidate.CopyInputs() : null; } }
                internal void DisposePreparation(NativeDiagnosticPreparation candidate)
                { lock (inputs.owner.frontierWorkerSync) lock (inputs.sync) { if (OriginalPreparation(candidate)) inputs.Close(); } }
            }

            public sealed partial class NativeWitnessBatch
            {
                private bool preparationAttempted;
                private NativeDiagnosticPreparation preparation;
                public NativeDiagnosticPreparation TryPrepareDiagnostic() { return owner.PrepareDiagnostic(this); }
                internal bool BeginPreparation() { if (preparationAttempted) return false; preparationAttempted = true; return true; }
                internal void SetPreparation(NativeDiagnosticPreparation value) { preparation = value; }
                internal bool OwnsPreparation(NativeDiagnosticPreparation value) { return Object.ReferenceEquals(preparation, value); }
                private void ClearPreparation() { if (preparation != null) preparation.Clear(); }
            }

            // Only this original preparation may offer the separate diagnostic
            // transport. No serialization import or publication issuer exists.
            public sealed partial class NativeDiagnosticPreparation : IDisposable
            {
                private readonly EvaluationCustody owner;
                internal readonly NativeWitnessBatch Batch;
                private NativeInputDescriptor[] inputs;
                public Guid RequestId { get; private set; }
                public Guid FullInvocationId { get; private set; }
                public Guid CompactInvocationId { get; private set; }
                public int WorkerProcessId { get; private set; }
                public long WorkerCreationFileTime { get; private set; }
                public string ScopeSha256 { get; private set; }
                public string OfferSha256 { get; private set; }
                public string FactsReceiptSha256 { get; private set; }
                public string SourceSha256 { get; private set; }
                public string DiagnosticSnapshot { get; private set; }
                internal NativeDiagnosticPreparation(EvaluationCustody owner, NativeWitnessBatch batch, NativeInputDescriptor[] inputs,
                    Worker worker, string scope, string offer, string facts)
                {
                    this.owner = owner; Batch = batch; this.inputs = inputs;
                    RequestId = batch.Pair.RequestId; FullInvocationId = batch.Pair.FullInvocationId; CompactInvocationId = batch.Pair.CompactInvocationId;
                    WorkerProcessId = worker.ProcessId; WorkerCreationFileTime = worker.ProcessCreationFileTime;
                    ScopeSha256 = scope; OfferSha256 = offer; FactsReceiptSha256 = facts; SourceSha256 = worker.SourceSha256;
                    DiagnosticSnapshot = batch.DiagnosticSnapshot;
                }
                public bool IsCurrent { get { return owner.PreparationCurrent(this); } }
                public bool LiveProofEligible { get { return false; } }
                public bool NativeWriterCustodyEstablished { get { return false; } }
                public bool PublicationCompletionIssued { get { return false; } }
                public NativeInputDescriptor[] GetInputDescriptors() { return owner.PreparationInputs(this); }
                internal NativeInputDescriptor[] CopyInputs() { return (NativeInputDescriptor[])inputs.Clone(); }
                internal void Clear() { inputs = null; DiagnosticSnapshot = null; }
                public void Dispose() { owner.DisposePreparation(this); }
            }

            // Immutable diagnostic metadata; exported rows carry no ownership.
            public sealed class NativeInputDescriptor
            {
                public int Role { get; private set; }
                public int WriterSlot { get; private set; }
                public Guid ObservationId { get; private set; }
                public string FileIdentity { get; private set; }
                public long ByteCount { get; private set; }
                internal NativeInputDescriptor(LocalFrontierParentFilePacket.Entry entry)
                {
                    Role = entry.Role; WriterSlot = entry.Slot; ObservationId = entry.Observation;
                    FileIdentity = entry.File.Volume.ToString("x8") + ":" + entry.File.IndexHigh.ToString("x8") + entry.File.IndexLow.ToString("x8");
                    ByteCount = entry.File.Length;
                }
            }
        }
    }
}
