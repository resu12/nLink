using System;
using System.IO;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class FrontierParentCustody
        {
            // Consume the original parent source passes before a primary exists.
            // Foreign arguments cannot consume or retire the original custody.
            public LocalRetainedSourceRequest.FrontierPrimaryPreparation TryPreparePrimarySource(Guid attempt, string primaryOwner,
                string run, string scenario, string path, Completion completion)
            {
                lock (owner.frontierParentSync)
                {
                    if (!Object.ReferenceEquals(owner.frontierParentCustody, this)) return null;
                    lock (sync)
                    {
                        try
                        {
                            if (closed || !accepted || primaryHashAttempted || attempt == Guid.Empty ||
                                (primaryOwner != "Wrapper" && primaryOwner != "RouteAcceptance") ||
                                !Object.ReferenceEquals(acceptedCompletion, completion) || completion.AcceptanceRunId != run ||
                                completion.ScenarioRunId != scenario ||
                                !String.Equals(Path.GetFullPath(path), path, StringComparison.Ordinal) ||
                                !String.Equals(path, completion.Outputs[3].Path, StringComparison.Ordinal)) return null;
                        }
                        catch { return null; }
                        LocalInputReadLease.HashReadResult hash = null;
                        LocalRetainedSourceRequest request = null;
                        bool success = false;
                        var work = owner.frontierParentRegistration.parentWork;
                        try
                        {
                            hash = TryHashPrimarySource();
                            if (hash == null || hash.Lease == null) return null;
                            long lines;
                            work.Start();
                            try
                            {
                                lines = hash.Lease.CountFrontierSourceLines(path);
                                if (work.ElapsedMilliseconds > 5000 || !IsCurrent) return null;
                                request = LocalRetainedSourceRequest.TryCreateWithWriter(attempt, primaryOwner, run, scenario,
                                    hash.Lease, lines, completion);
                            }
                            finally { work.Stop(); }
                            if (request == null) return null;
                            var inputs = TryTransferToPrimary(request);
                            if (inputs == null || !inputs.IsCurrent) return null;
                            var result = new LocalRetainedSourceRequest.FrontierPrimaryPreparation(request, inputs, hash.Sha256, lines, primaryOwner);
                            success = true; return result;
                        }
                        catch { return null; }
                        finally
                        {
                            if (hash != null && hash.Lease != null) hash.Lease.Dispose();
                            if (!success) { if (request != null) request.Dispose(); Close(); }
                        }
                    }
                }
            }
        }
    }

    public sealed partial class LocalRetainedSourceRequest
    {
        // Original parent-side resources only. A copied wrapper cannot expose or
        // dispose them; metadata and currentness grant no evaluation authority.
        public sealed partial class FrontierPrimaryPreparation : IDisposable
        {
            private readonly FrontierPrimaryPreparation original;
            private readonly LocalRetainedSourceRequest request;
            private readonly FrontierPrimaryInputs inputs;
            private readonly string sha256;
            private readonly long lines;
            private readonly string primaryOwner;
            private bool disposed;
            private bool analysisClaimed;
            private int analysisInputState; // 0 untouched, 1 reserved, 2 hashed, 3 receipt persisted
            private LocalInputReadLease analysisInput;
            internal FrontierPrimaryPreparation(LocalRetainedSourceRequest request, FrontierPrimaryInputs inputs, string sha256, long lines, string primaryOwner)
            { original = this; this.request = request; this.inputs = inputs; this.sha256 = sha256; this.lines = lines; this.primaryOwner = primaryOwner; }
            private bool Owned { get { return Object.ReferenceEquals(original, this) && !disposed; } }
            public LocalRetainedSourceRequest Request { get { return Owned ? request : null; } }
            public FrontierPrimaryInputs Inputs { get { return Owned ? inputs : null; } }
            public string InputSha256 { get { return Owned ? sha256 : null; } }
            public long InputLineCount { get { return Owned ? lines : -1; } }
            public bool IsCurrent { get { return Owned && inputs.IsCurrent; } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool LiveProofEligible { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            private bool MatchesAnalysisScope(string owner, string run, string scenario, string path, LocalGuiWriterRequest.Completion completion)
            {
                if (!Owned || analysisClaimed || primaryOwner != owner ||
                    !request.FrontierScopeCurrent(completion, true)) return false;
                try
                {
                    return completion.AcceptanceRunId == run && completion.ScenarioRunId == scenario &&
                        String.Equals(Path.GetFullPath(path), path, StringComparison.Ordinal) &&
                        String.Equals(path, completion.Outputs[3].Path, StringComparison.Ordinal) && inputs.IsCurrent;
                }
                catch { return false; }
            }
            // Reserve before the ordinary merge. While pending, this preparation
            // cannot launch a native primary. Foreign/repeated calls do no work.
            public bool TryBeginAnalysisInput(string run, string scenario, string path)
            {
                lock (request.frontierInputSync)
                {
                    if (analysisInputState != 0 || !MatchesAnalysisScope("Wrapper", run, scenario, path, request.frontierWriter)) return false;
                    analysisInputState = 1; return true;
                }
            }
            // Replaces the derived receipt's existing hash pass, never a second
            // source scan. The ordinary path is pinned until primary cleanup.
            public string HashAndHoldAnalysisInput()
            {
                lock (request.frontierInputSync)
                {
                    if (!Owned || analysisInputState != 1 || analysisClaimed)
                        throw new InvalidOperationException("frontier_analysis_input_unavailable_or_repeated");
                    analysisInputState = 2;
                    try
                    {
                        var result = LocalInputReadLease.HashAndHold(request.frontierWriter.Outputs[3].Path);
                        analysisInput = result.Lease;
                        return result.Sha256;
                    }
                    catch { Dispose(); throw; }
                }
            }
            private bool ExactAnalysisInput
            {
                get { return analysisInput != null && analysisInput.IsVerified && analysisInput.ExpectedSha256 == sha256 &&
                    analysisInput.ByteCount == request.frontierWriter.Outputs[3].ByteCount; }
            }
            private bool AnalysisInputReady
            { get { return analysisInputState == 0 || (analysisInputState == 3 && ExactAnalysisInput); } }
            public bool TryCompleteAnalysisInput()
            {
                lock (request.frontierInputSync)
                {
                    if (!Owned || analysisClaimed || analysisInputState != 2 || !ExactAnalysisInput ||
                        !request.FrontierScopeCurrent(request.frontierWriter, true) || !inputs.IsCurrent) return false;
                    analysisInputState = 3; return true;
                }
            }
            // One in-process consumer may claim the original, still-unbound request.
            // Rejected copies/scope arguments neither consume nor retire its owner.
            public bool TryClaimForAnalysis(string owner, string run, string scenario, string path, LocalGuiWriterRequest.Completion completion)
            {
                lock (request.frontierInputSync)
                {
                    if (!MatchesAnalysisScope(owner, run, scenario, path, completion) || !AnalysisInputReady) return false;
                    analysisClaimed = true;
                    return true;
                }
            }
            public void Dispose()
            {
                lock (request.frontierInputSync)
                {
                    if (!Owned) return; disposed = true;
                    try { request.Dispose(); }
                    finally { if (analysisInput != null) analysisInput.Dispose(); }
                }
            }
        }
    }
}
