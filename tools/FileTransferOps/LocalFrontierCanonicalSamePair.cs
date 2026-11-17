using System;
using System.Security.Cryptography;
using System.Text;

namespace NLink.Tools
{
    internal sealed partial class LocalFrontierParentFacts
    {
        internal bool HasReceiverPayload(LocalFrontierFileManifest.Payload payload, Guid request,
            int process, long creation, string writer, long lastSequence)
        {
            foreach (var manifest in manifests)
            {
                if (manifest.AppRequest != request) continue;
                if (manifest.AppId != process || manifest.AppCreation != creation || manifest.Writer != writer ||
                    manifest.Drain.Last != lastSequence) return false;
                foreach (var original in manifest.Payloads)
                    if (Object.ReferenceEquals(original, payload))
                        return payload.Completion.First == payload.Completion.Last &&
                            payload.Marker.First == payload.Marker.Last && payload.Completion.Last < payload.Marker.First &&
                            payload.Marker.Last < manifest.Drain.First;
                return false;
            }
            return false;
        }
    }

    internal sealed partial class CanonicalTransferPair
    {
        // Scope values only. The custody method below supplies native currentness
        // before/after this comparison; this method cannot create that ownership.
        internal bool MatchesCanonicalReceiptScope(LocalFrontierParentFacts facts, int payloadIndex,
            string receiptSession, string receiptTransfer, int receiptProcess, long receiptCreation)
        {
            if (Sha256 != "3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699" ||
                String.IsNullOrWhiteSpace(Session) || Session == "(none)" ||
                String.IsNullOrWhiteSpace(Transfer) || Transfer == "(none)" || identities[0] == identities[1] ||
                receiptSession != ReceiptEvidence(Session) || receiptTransfer != ReceiptEvidence(Transfer) ||
                receiptProcess != processes[1] || receiptCreation != creations[1] ||
                !facts.MatchesPayloadWriters(requests, processes, creations, writers, sequences)) return false;
            string writer;
            var payload = facts.PayloadAt(payloadIndex, out writer);
            // The held pair is ordered helpee sender / helper receiver. Merely
            // belonging to either peer's valid manifest is not sufficient.
            return payload != null && writer == writers[1] && facts.HasReceiverPayload(payload,
                requests[1], processes[1], creations[1], writers[1], sequences[1]);
        }

        private static string ReceiptEvidence(string value)
        {
            using (var sha = SHA256.Create())
                return "fte1-" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value)))
                    .Replace("-", "").ToLowerInvariant().Substring(0, 24);
        }
    }

    public sealed partial class LocalRetainedSourceRequest
    {
        public sealed partial class FrontierWorkerInputs
        {
            public sealed partial class EvaluationCustody
            {
                private bool canonicalSamePairAttempted;

                // Diagnostic evidence only, never a new capability or receipt parser.
                // The trusted primary passes scalar scope from its already
                // decoded original payload, not JSON reopened from an artifact.
                public bool TryCheckCanonicalSamePairWitness(CanonicalTransferEvaluation transfer,
                    NativeWitnessBatch batch, NativePayload receiptPayload, string receiptSession,
                    string receiptTransfer, int receiptProcess, long receiptCreation)
                {
                    lock (inputs.owner.frontierWorkerSync) lock (inputs.sync)
                    {
                        if (!Original || state != 3 || canonicalSamePairAttempted || transfer == null ||
                            !Object.ReferenceEquals(transferEvaluation, transfer) ||
                            !Object.ReferenceEquals(issued, transfer.Pair) || !OriginalWitnesses(batch) ||
                            !batch.HasDiagnosticState || !Object.ReferenceEquals(batch.Payload, receiptPayload)) return false;
                        canonicalSamePairAttempted = true;
                        bool accepted = false; inputs.work.Start();
                        try
                        {
                            inputs.VisitEvaluation();
                            if (!Current() || !inputs.TransferPairCurrent) return false;
                            if (!inputs.transferPair.MatchesCanonicalReceiptScope(inputs.facts,
                                batch.Payloads.IndexOf(receiptPayload), receiptSession, receiptTransfer,
                                receiptProcess, receiptCreation)) return false;
                            inputs.VisitEvaluation();
                            accepted = Current() && inputs.TransferPairCurrent;
                            return accepted;
                        }
                        catch { return false; }
                        finally { inputs.work.Stop(); if (!accepted) inputs.Close(); }
                    }
                }
            }
        }
    }
}
