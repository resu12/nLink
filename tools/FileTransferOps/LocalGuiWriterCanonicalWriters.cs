using System;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class FrontierLaunchOwner
        {
            internal LocalFrontierFileManifest GetAttemptManifest(FrontierAppRequest request, FrontierAppReports reports)
            {
                lock (sync)
                {
                    if (!IsCurrent || !FinalSealScopeCurrent() || request == null || reports == null ||
                        !request.IsOriginalFor(this) || !appRequests.Contains(request) ||
                        !Object.ReferenceEquals(request.launch, reports.Launch) || !reports.NativeLaunchReady ||
                        !reports.Stdout.IsCompleted || !reports.Stderr.IsCompleted ||
                        !request.launch.OutputDrainedSuccessfully) return null;
                    return fileAdoption.GetAttemptManifest(id, request.RequestId,
                        request.launch.AppProcessId, request.launch.AppProcessCreationFileTime);
                }
            }
        }

        public sealed partial class FrontierFinalizerBinding
        {
            internal bool OwnsAttemptWriterMatch(FrontierLaunchOwner owner, FrontierWriterMatch match)
            {
                return IsCurrent && match != null && Object.ReferenceEquals(files, owner.GetOriginalSealedFiles()) &&
                    Object.ReferenceEquals(WriterMatch, match) && match.IsCurrent;
            }
        }

        public sealed partial class CanonicalAttemptDispatch
        {
            partial void ReportRoleWriterRejection(string stage);
            internal CanonicalAttemptWriters roleWriters;
            public CanonicalAttemptWriters TryBindRoleWriters(FrontierFinalizerBinding binding, FrontierWriterMatch match)
            {
                lock (rolesSync)
                {
                    if (!IsCurrent || consumed != 1 || roleWriters != null || binding == null ||
                        !binding.OwnsAttemptWriterMatch(owner, match) || !helpee.Started || !helper.Started) return null;
                    var first = owner.GetAttemptManifest(helpee.Request, helpee.Reports);
                    var second = owner.GetAttemptManifest(helper.Request, helper.Reports);
                    if (first == null || second == null || first.Writer == second.Writer) return null;
                    var candidate = new CanonicalAttemptWriters(this, binding, match, first, second);
                    if (!RoleWriterInputsCurrent(candidate)) return null;
                    roleWriters = candidate; return candidate;
                }
            }
            private bool RoleWriterInputsCurrent(CanonicalAttemptWriters value)
            {
                bool accepted = false;
                string stage = "attempt";
                try
                {
                    if (!IsCurrent) return false;
                    stage = "consumed";
                    if (consumed != 1) return false;
                    stage = "binding_match";
                    if (!value.Binding.OwnsAttemptWriterMatch(owner, value.Match)) return false;
                    stage = "helpee_manifest";
                    if (!Object.ReferenceEquals(value.Helpee, owner.GetAttemptManifest(helpee.Request, helpee.Reports))) return false;
                    stage = "helper_manifest";
                    if (!Object.ReferenceEquals(value.Helper, owner.GetAttemptManifest(helper.Request, helper.Reports))) return false;
                    stage = "helpee_closed_writer";
                    if (!value.MatchesClosedWriter(value.Helpee)) return false;
                    stage = "helper_closed_writer";
                    accepted = value.MatchesClosedWriter(value.Helper);
                    return accepted;
                }
                finally { if (!accepted) ReportRoleWriterRejection(stage); }
            }
            internal bool OwnsRoleWriters(CanonicalAttemptWriters value)
            {
                lock (rolesSync)
                {
                    if (!Object.ReferenceEquals(roleWriters, value) || value.Retired) return false;
                    if (RoleWriterInputsCurrent(value)) return true;
                    value.Retired = true; return false;
                }
            }
            internal bool MatchesRoleWriter(CanonicalAttemptWriters value, string role, FrontierAppRequest request, string writer)
            {
                lock (rolesSync)
                {
                    var slot = Role(role);
                    return OwnsRoleWriters(value) && slot != null && Object.ReferenceEquals(slot.Request, request) &&
                        writer == (role == "helpee" ? value.Helpee.Writer : value.Helper.Writer);
                }
            }
            internal void RetireRoleWriters(CanonicalAttemptWriters value)
            { lock (rolesSync) { if (Object.ReferenceEquals(roleWriters, value)) value.Retired = true; } }
        }

        // Borrowed role-to-closed-writer join only; no ownership import,
        // file-handle export, transfer integrity or publication permission.
        public sealed class CanonicalAttemptWriters : IDisposable
        {
            private readonly CanonicalAttemptDispatch owner;
            internal readonly FrontierFinalizerBinding Binding;
            internal readonly FrontierWriterMatch Match;
            internal readonly LocalFrontierFileManifest Helpee, Helper;
            internal bool Retired;
            internal CanonicalAttemptWriters(CanonicalAttemptDispatch owner, FrontierFinalizerBinding binding,
                FrontierWriterMatch match, LocalFrontierFileManifest helpee, LocalFrontierFileManifest helper)
            { this.owner = owner; Binding = binding; Match = match; Helpee = helpee; Helper = helper; }
            public bool IsCurrent { get { return owner.OwnsRoleWriters(this); } }
            public string HelpeeWriterId { get { return IsCurrent ? Helpee.Writer : null; } }
            public string HelperWriterId { get { return IsCurrent ? Helper.Writer : null; } }
            public bool MatchesRole(string role, FrontierAppRequest request, string writer)
            { return owner.MatchesRoleWriter(this, role, request, writer); }
            internal bool MatchesClosedWriter(LocalFrontierFileManifest manifest)
            {
                var validation = Match.Validation;
                if (!validation.IsCurrent || validation.HasWriterClosureErrors || validation.Writers.Count != 2) return false;
                foreach (var writer in validation.Writers)
                    if (writer.WriterId == manifest.Writer && writer.LastSequence == manifest.Drain.Last) return true;
                return false;
            }
            public bool LiveProofEligible { get { return false; } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            public void Dispose() { owner.RetireRoleWriters(this); }
        }
    }

    internal sealed partial class LocalFrontierFileAdoption
    {
        internal LocalFrontierFileManifest GetAttemptManifest(Guid guiRequest, Guid appRequest, int pid, long creation)
        {
            lock (sync)
            {
                if (!IsSourceVerified || adopted.Count != 2) return null;
                LocalFrontierFileManifest found = null;
                foreach (var item in adopted)
                {
                    var manifest = item.Manifest;
                    if (manifest.GuiRequest != guiRequest || manifest.AppRequest != appRequest) continue;
                    if (found != null || manifest.AppId != pid || manifest.AppCreation != creation) return null;
                    found = manifest;
                }
                return found;
            }
        }
    }
}

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class CanonicalAttemptDispatch
        {
            partial void ValidateSourceAssociation(CanonicalAttemptSource value, ref bool current)
            {
                current = value.Writers == null ||
                    (Object.ReferenceEquals(roleWriters, value.Writers) && value.Writers.IsCurrent);
            }

            internal bool BindSourceWriters(CanonicalAttemptSource value, CanonicalAttemptWriters writers)
            {
                lock (rolesSync)
                {
                    if (!SourceCurrent(value) || value.Writers != null || writers == null ||
                        !Object.ReferenceEquals(roleWriters, writers) || !writers.IsCurrent) return false;
                    value.Writers = writers; return true;
                }
            }
        }

        public sealed partial class CanonicalAttemptSource
        {
            internal CanonicalAttemptWriters Writers;
            public bool TryBindClosedWriters(CanonicalAttemptWriters writers)
            { return owner.BindSourceWriters(this, writers); }
            public bool ClosedWritersBound { get { return IsCurrent && Writers != null; } }
        }
    }
}

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class CanonicalAttemptDispatch
        {
            partial void ValidateDestinationAssociation(CanonicalAttemptDestination candidate, ref bool current)
            {
                current = candidate.writers == null ||
                    (Object.ReferenceEquals(roleWriters, candidate.writers) && candidate.writers.IsCurrent);
            }

            internal bool BindDestinationWriters(CanonicalAttemptDestination candidate, CanonicalAttemptWriters writers)
            {
                lock (rolesSync)
                {
                    if (!DestinationCurrent(candidate) || candidate.writers != null || writers == null ||
                        !Object.ReferenceEquals(roleWriters, writers) || !writers.IsCurrent || !helper.Process.HasExited) return false;
                    candidate.writers = writers; return true;
                }
            }
        }

        public sealed partial class CanonicalAttemptDestination
        {
            internal CanonicalAttemptWriters writers;
            public bool TryBindClosedWriters(CanonicalAttemptWriters value)
            { return IsOriginal && owner.BindDestinationWriters(this, value); }
        }
    }
}
