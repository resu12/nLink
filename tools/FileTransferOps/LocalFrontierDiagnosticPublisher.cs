using System;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        public sealed partial class FrontierWorkerInputs
        {
            private LocalFrontierDiagnosticChannel diagnosticOutput;
            private void CloseDiagnosticOutput() { if (diagnosticOutput != null) diagnosticOutput.CloseWorker(); }

            public sealed partial class EvaluationCustody
            {
                private bool diagnosticOffered, diagnosticOfferAttempted, diagnosticCommitAttempted;
                private void RequireDiagnosticCurrent()
                { if (!Current()) throw new InvalidOperationException("frontier_diagnostic_worker_custody"); }
                internal bool OfferDiagnostic(NativeDiagnosticPreparation candidate)
                {
                    lock (inputs.owner.frontierWorkerSync) lock (inputs.sync)
                    {
                        if (!OriginalPreparation(candidate)) return false;
                        bool success = false; byte[] bytes = null; inputs.work.Start();
                        try
                        {
                            if (diagnosticOfferAttempted) return false; diagnosticOfferAttempted = true;
                            RequireDiagnosticCurrent();
                            inputs.diagnosticOutput = LocalFrontierDiagnosticChannel.Open(inputs.files.Endpoint,
                                LocalFrontierDiagnosticChannel.Binding(inputs.scope.Binding, inputs.files.OfferHash, inputs.files.Facts.ReceiptHash), false);
                            if (inputs.diagnosticOutput == null) return false;
                            bytes = LocalFrontierDiagnosticPacket.Encode(candidate, RequireDiagnosticCurrent);
                            success = inputs.diagnosticOutput.Offer(bytes, RequireDiagnosticCurrent);
                            if (success) { bytes = null; diagnosticOffered = true; }
                            return success;
                        }
                        catch { return false; }
                        finally
                        {
                            if (bytes != null) Array.Clear(bytes, 0, bytes.Length);
                            inputs.work.Stop(); if (!success) RejectDiagnosticOutput();
                        }
                    }
                }
                internal bool CommitDiagnostic(NativeDiagnosticPreparation candidate)
                {
                    lock (inputs.owner.frontierWorkerSync) lock (inputs.sync)
                    {
                        if (!OriginalPreparation(candidate)) return false;
                        bool success = false; inputs.work.Start();
                        try
                        {
                            if (!diagnosticOffered || diagnosticCommitAttempted) return false; diagnosticCommitAttempted = true;
                            RequireDiagnosticCurrent();
                            success = inputs.diagnosticOutput.Commit(RequireDiagnosticCurrent); return success;
                        }
                        catch { return false; }
                        finally { inputs.work.Stop(); if (!success) RejectDiagnosticOutput(); }
                    }
                }
                private void RejectDiagnosticOutput()
                {
                    if (inputs.diagnosticOutput != null) inputs.diagnosticOutput.Cancel();
                    inputs.Close();
                }
            }

            public sealed partial class NativeDiagnosticPreparation
            {
                public bool TryOfferOutput() { return owner.OfferDiagnostic(this); }
                public bool TryCommitOutput() { return owner.CommitDiagnostic(this); }
            }
        }
    }
}
