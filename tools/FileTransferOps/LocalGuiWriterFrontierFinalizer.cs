using System;
using System.Threading;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class Finalizer
        {
            private readonly object frontierSync = new object();
            private bool frontierBindingAttempted;
            private FrontierFinalizerBinding frontierBinding;

            // Ownership only. This does not attest to the finalizer's later
            // validation, semantic copy, persistence, or publication gates.
            public FrontierFinalizerBinding TryTakeFrontierBinding()
            {
                if (!Object.ReferenceEquals(original, this)) return null;
                lock (frontierSync)
                {
                    if (frontierBindingAttempted) { RetireFrontierBinding(); return null; }
                    frontierBindingAttempted = true;
                    var owner = MatchingFrontierOwner();
                    var files = owner == null ? null : owner.GetOriginalSealedFiles();
                    if (files == null || !FrontierScopeCurrent(owner)) { RetireFrontierBinding(); return null; }
                    frontierBinding = new FrontierFinalizerBinding(this, files);
                    return frontierBinding;
                }
            }

            private FrontierLaunchOwner MatchingFrontierOwner()
            {
                var owner = frontierLaunchOwner;
                return !relayMode && owner != null && endpointName != null && owner.endpointName == endpointName &&
                    owner.id == id && owner.run == run && owner.scenario == scenario && owner.artifactRoot == root ? owner : null;
            }
            private bool FrontierScopeCurrent(FrontierLaunchOwner owner)
            {
                try
                {
                    return owner != null && Object.ReferenceEquals(original, this) && Volatile.Read(ref attempted) == 0 &&
                        IsCurrent && owner.IsCurrent && FrontierHeaderCurrent(owner);
                }
                catch { return false; }
            }
            private bool FrontierHeaderCurrent(FrontierLaunchOwner owner)
            {
                return view.ReadInt32(48) == 1 && view.ReadInt32(52) == 0 && view.ReadInt64(56) == 0 &&
                    ReadId(view) == id && view.ReadInt32(4) == owner.parentId && view.ReadInt64(8) == owner.parentCreation &&
                    view.ReadInt32(16) == owner.guiId && view.ReadInt64(40) == owner.guiCreation;
            }
            // Called only within OwnsFrontierBinding's existing frontier lock;
            // no callback into sourceSync or recursive GUI/adopter validation.
            internal bool ProcessingScopeCurrent(FrontierLaunchOwner owner, FrontierFinalizerBinding binding,
                FrontierSealedFiles files, FrontierSourceRead reader)
            {
                return Object.ReferenceEquals(original, this) && Object.ReferenceEquals(frontierBinding, binding) &&
                    Object.ReferenceEquals(MatchingFrontierOwner(), owner) &&
                    Object.ReferenceEquals(binding.ProcessingReaderFor(this, files), reader) &&
                    Volatile.Read(ref attempted) == 0 && IsCurrent && FrontierHeaderCurrent(owner);
            }
            internal bool OwnsFrontierBinding(FrontierFinalizerBinding value, FrontierSealedFiles files)
            {
                lock (frontierSync)
                {
                    // A copy cannot retire or consume the original capability.
                    if (!Object.ReferenceEquals(original, this) || !Object.ReferenceEquals(frontierBinding, value)) return false;
                    var owner = MatchingFrontierOwner();
                    bool? processing = owner == null ? (bool?)null : owner.CheckProcessingFiles(this, value, files);
                    if (processing.HasValue)
                    {
                        if (processing.Value) return true;
                        RetireFrontierBinding(); return false;
                    }
#if NLINK_TEST_BUFFERED_LEDGER
                    NLink.Tests.ProcessingCustodyProbe.Boundary("binding-legacy");
#endif
                    if (FrontierScopeCurrent(owner) && files.IsCurrent) return true;
                    RetireFrontierBinding(); return false;
                }
            }
            internal void DisposeFrontierBinding(FrontierFinalizerBinding value)
            {
                lock (frontierSync) { if (Object.ReferenceEquals(frontierBinding, value)) RetireFrontierBinding(); }
            }
            private void RetireFrontierBinding()
            {
                lock (frontierSync)
                {
                    frontierBinding = null;
                    var owner = MatchingFrontierOwner();
                    if (owner != null) owner.Dispose();
                }
            }
        }

        // Ownership alone exports nothing. Source access and prepared-parent
        // transport are separately one-shot; no scalar completion issuer exists.
        // The lock order is finalizer -> launch owner -> adopter; no callback
        // from either owner or adopter acquires the finalizer lock.
        public sealed partial class FrontierFinalizerBinding : IDisposable
        {
            private readonly Finalizer finalizer;
            private readonly FrontierSealedFiles files;
            internal FrontierFinalizerBinding(Finalizer finalizer, FrontierSealedFiles files)
            { this.finalizer = finalizer; this.files = files; }
            internal FrontierSourceRead ProcessingReaderFor(Finalizer owner, FrontierSealedFiles expected)
            {
                return Object.ReferenceEquals(finalizer, owner) && Object.ReferenceEquals(files, expected) &&
                    !Volatile.Read(ref parentHandoffAttempted) ? sourceReader : null;
            }
            public bool IsCurrent { get { return finalizer.OwnsFrontierBinding(this, files); } }
            public string AcceptanceRunId { get { return IsCurrent ? finalizer.run : null; } }
            public string ScenarioRunId { get { return IsCurrent ? finalizer.scenario : null; } }
            public string ArtifactRoot { get { return IsCurrent ? finalizer.root : null; } }
            public FrontierLedgerOrigin LedgerOrigin { get { return IsCurrent ? files.GetOriginalLedgerOrigin() : null; } }
            public bool LiveProofEligible { get { return false; } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            public void Dispose() { finalizer.DisposeFrontierBinding(this); }
        }
    }
}
