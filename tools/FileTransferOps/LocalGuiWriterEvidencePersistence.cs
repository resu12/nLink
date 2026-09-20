using System;
using System.Globalization;
using System.IO;
using System.Text;
using NLink.Tools.FrontierLedger;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class FrontierFinalizerBinding
        {
            private bool evidenceAttempted;
            private FrontierPersistedEvidence persistedEvidence;
            public FrontierPersistedEvidence PersistedEvidence
            { get { lock (sourceSync) { return OwnsPersistedEvidence(persistedEvidence) ? persistedEvidence : null; } } }

            // A distinct optional evidence record, not legacy JSON/completion.
            // No caller path, JSON, snapshot or success verdict is imported.
            public FrontierPersistedEvidence TryPersistEvidence()
            {
                lock (sourceSync)
                {
                    if (!IsCurrent) return null;
                    bool success = false; LocalFrontierEvidenceFile resource = null;
                    try
                    {
                        if (evidenceAttempted) return null; evidenceAttempted = true;
                        if (!OwnsSemanticCopy(semanticCopy) || sourceReader == null || sourceReader.ProcessingWorkActive) return null;
                        var adoption = files.GetOriginalAdoption();
                        if (adoption == null) return null;
                        resource = new LocalFrontierEvidenceFile(adoption.FinalizationLifetime(sourceReader));
                        string destination = Path.Combine(finalizer.root, "filetransfer-frontier-finalizer-evidence.json");
                        // Only immutable facts are read inside this callback. It
                        // never enters a GUI/finalizer ownership lock.
                        if (!adoption.TryRunRecoveryRead(() => resource.TryPersist(destination, SerializeEvidence(), () => adoption.IsSourceVerified))) return null;
                        persistedEvidence = new FrontierPersistedEvidence(this, semanticCopy, resource);
                        success = OwnsPersistedEvidence(persistedEvidence); return success ? persistedEvidence : null;
                    }
                    catch { return null; }
                    finally { if (!success) { Dispose(); if (resource != null) resource.Dispose(); } }
                }
            }
            private byte[] SerializeEvidence()
            {
                var capture = cursorMatch.Result.StreamCapture; var json = new FrontierEvidenceJson();
                json.Add("{\"schemaVersion\":1,\"event\":\"filetransfer_frontier_finalizer_evidence\",\"diagnosticOnly\":true,\"acceptanceRunId\":"); json.Text(finalizer.run);
                json.Add(",\"scenarioRunId\":"); json.Text(finalizer.scenario);
                json.Add(",\"ledgerId\":"); json.Text(cursorMatch.Final.LedgerId);
                json.Add(",\"generationId\":"); json.Text(cursorMatch.Final.GenerationId);
                json.Add(",\"source\":{\"sha256\":"); json.Text(files.SourceSha256);
                json.Add(",\"byteCount\":"); json.Number(capture.SourceBytes);
                json.Add(",\"lineCount\":"); json.Number(capture.SourceLines);
                json.Add(",\"bookmark\":"); json.Number(capture.Bookmark); json.Add("}");
                var observations = new[] { cursorMatch.Initial, cursorMatch.Final };
                for (int i = 0; i < observations.Length; i++)
                {
                    var observation = observations[i]; json.Add(i == 0 ? ",\"initial\":{" : ",\"final\":{");
                    json.Add("\"lineCount\":"); json.Number(observation.LineCount);
                    json.Add(",\"committedBytes\":"); json.Number(observation.CommittedBytes);
                    json.Add(",\"synchronizationCount\":"); json.Number(observation.SynchronizationCount); json.Add("}");
                }
                json.Add(",\"outputs\":[");
                var outputs = new[] { capture.Retained, capture.Writer, capture.Semantic, semanticCopy.Output };
                var roles = new[] { "retained", "writer", "semantic", "analyzer" };
                for (int i = 0; i < outputs.Length; i++)
                {
                    var output = outputs[i]; if (i != 0) json.Add(",");
                    json.Add("{\"role\":"); json.Text(roles[i]); json.Add(",\"path\":"); json.Text(output.CreationPath);
                    json.Add(",\"fileIdentity\":"); json.Text(output.FileIdentity); json.Add(",\"sha256\":"); json.Text(output.Sha256);
                    json.Add(",\"byteCount\":"); json.Number(output.ByteCount); json.Add(",\"lineCount\":"); json.Number(output.LineCount); json.Add("}");
                }
                json.Add("],\"writers\":["); bool first = true;
                foreach (var writer in capture.Validation.Writers)
                {
                    if (!first) json.Add(","); first = false;
                    json.Add("{\"writerId\":"); json.Text(writer.WriterId);
                    json.Add(",\"firstSequence\":"); json.Number(writer.FirstSequence);
                    json.Add(",\"lastSequence\":"); json.Number(writer.LastSequence);
                    json.Add(",\"observedCount\":"); json.Number(writer.ObservedCount);
                    json.Add(",\"drainSequence\":"); json.Number(writer.DrainSequence); json.Add("}");
                }
                json.Add("],\"recovery\":{\"batchCount\":"); json.Number(recoveryMarkers.DeferredBatchKeys.Count);
                json.Add(",\"markerCount\":"); json.Number(recoveryMarkers.Markers.Count);
                json.Add(",\"sourceLineVisitCount\":"); json.Number(recoveryMarkers.SourceLineVisitCount);
                json.Add("},\"liveProofEligible\":false,\"nativeWriterCustodyEstablished\":false,\"publicationCompletionIssued\":false}");
                return json.Encode();
            }
            internal bool OwnsPersistedEvidence(FrontierPersistedEvidence value)
            {
                lock (sourceSync)
                {
                    if (value == null || !Object.ReferenceEquals(persistedEvidence, value) || !IsCurrent) return false;
                    if (Object.ReferenceEquals(value.Copy, semanticCopy) && OwnsSemanticCopy(semanticCopy) && value.Resource.IsCurrent) return true;
                    Dispose(); return false;
                }
            }
            internal void DisposePersistedEvidence(FrontierPersistedEvidence value)
            {
                lock (sourceSync)
                { if (Object.ReferenceEquals(persistedEvidence, value)) { Dispose(); value.Resource.Dispose(); } }
            }
        }
        public sealed class FrontierPersistedEvidence : IDisposable
        {
            private readonly FrontierFinalizerBinding owner;
            internal readonly FrontierSemanticCopy Copy;
            internal readonly LocalFrontierEvidenceFile Resource;
            public GuiSmokeLedgerCapturedOutput Output { get; private set; }
            public bool IsCurrent { get { return owner.OwnsPersistedEvidence(this); } }
            public bool EvidencePersisted { get { return IsCurrent; } }
            public bool LiveProofEligible { get { return false; } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            internal FrontierPersistedEvidence(FrontierFinalizerBinding owner, FrontierSemanticCopy copy, LocalFrontierEvidenceFile resource)
            { this.owner = owner; Copy = copy; Resource = resource; Output = resource.Output; }
            public void Dispose() { owner.DisposePersistedEvidence(this); }
        }
    }

    // Fixed-schema, bounded JSON encoding. No public serialization/approval API.
    internal sealed class FrontierEvidenceJson
    {
        private readonly StringBuilder text = new StringBuilder();
        internal void Add(string value)
        {
            if (value.Length > LocalFrontierEvidenceFile.MaximumBytes - text.Length) throw new InvalidDataException("frontier_evidence_limit");
            text.Append(value);
        }
        internal void Number(long value) { Add(value.ToString(CultureInfo.InvariantCulture)); }
        internal void Text(string value)
        {
            if (value == null || value.Length > 4096) throw new InvalidDataException("frontier_evidence_text_limit");
            Add("\"");
            foreach (char c in value)
            {
                if (c == '\"' || c == '\\') Add("\\" + c);
                else if (c < 32) Add("\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else Add(c.ToString());
            }
            Add("\"");
        }
        internal byte[] Encode()
        {
            var encoding = new UTF8Encoding(false, true); string value = text.ToString();
            if (encoding.GetByteCount(value) > LocalFrontierEvidenceFile.MaximumBytes) throw new InvalidDataException("frontier_evidence_byte_limit");
            return encoding.GetBytes(value);
        }
    }
}
