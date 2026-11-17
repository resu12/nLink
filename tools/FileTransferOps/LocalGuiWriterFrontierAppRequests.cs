using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class FrontierLaunchOwner
        {
            private readonly List<FrontierAppRequest> appRequests = new List<FrontierAppRequest>();

            // Created before launch, using only the original GUI registration.
            // Endpoint names are locators for the app, not import capabilities.
            public FrontierAppRequest TryCreateAppRequest()
            {
                lock (sync)
                {
                    if (!IsCurrent) return null;
                    LocalFrontierAppRequest native = null;
                    bool retained = false;
                    try
                    {
                        if (sealedLaunches || appRequests.Count == MaximumAppLaunches ||
                            (apps.Count != 0 && appRequests.Count == 0)) return null;
                        native = LocalFrontierAppRequest.TryCreate(run, scenario);
                        if (native == null) return null;
                        var request = new FrontierAppRequest(this, native);
                        appRequests.Add(request); native = null;
                        if (!IsCurrent) return null;
                        retained = true; return request;
                    }
                    catch { return null; }
                    finally { if (native != null) native.Dispose(); if (!retained) Dispose(); }
                }
            }

            private bool AppRequestsCurrent()
            {
                foreach (var request in appRequests)
                {
                    var launch = request.launch;
                    if (!request.native.MatchesGuiLaunch(guiId, guiCreation,
                        launch == null ? 0 : launch.AppProcessId, launch == null ? 0 : launch.AppProcessCreationFileTime)) return false;
                }
                return true;
            }
            internal bool OwnsAppRequest(FrontierAppRequest request)
            { lock (sync) { return appRequests.Contains(request) && IsCurrent; } }
            internal void DisposeAppRequest(FrontierAppRequest request)
            {
                lock (sync)
                {
                    // A copied/foreign wrapper cannot abandon the original set.
                    if (appRequests.Contains(request)) Dispose();
                }
            }

            // Attachment completion + exit only. A future handoff must also
            // prove every original writer drain, output EOF and native file.
            public bool AllAppRequestsCompletedSuccessfully
            {
                get
                {
                    lock (sync)
                    {
                        if (!IsCurrent || !sealedLaunches || appRequests.Count != MaximumAppLaunches || !AllAppsExitedSuccessfully) return false;
                        foreach (var request in appRequests) if (!request.native.GuiAttachmentCompleted) return false;
                        return IsCurrent;
                    }
                }
            }
        }

        public sealed class FrontierAppRequest : IDisposable
        {
            private readonly FrontierAppRequest original;
            private readonly FrontierLaunchOwner owner;
            internal readonly LocalFrontierAppRequest native;
            internal FrontierAppLaunch launch;
            internal bool outputLaunchAttempted;
            internal FrontierAppRequest(FrontierLaunchOwner owner, LocalFrontierAppRequest native)
            { original = this; this.owner = owner; this.native = native; }
            internal bool IsOriginalFor(FrontierLaunchOwner candidate)
            { return Object.ReferenceEquals(original, this) && Object.ReferenceEquals(owner, candidate); }
            public string Name { get { return native.Name; } }
            public Guid RequestId { get { return native.RequestId; } }
            public bool IsCurrent { get { return owner.OwnsAppRequest(this); } }
            public FrontierAppLaunch BindApp(Process process) { return owner.BindAppCore(process, this); }
            public FrontierAppLaunch StartAppWithOutput(Process process) { return owner.StartAppWithOutput(process, this); }
            public FrontierAppReports StartAppWithReports(Process process, Func<int, string[]> createReports)
            { return owner.StartAppWithReports(process, this, createReports); }
            public bool FileSubmissionAvailable { get { return owner.FileSubmissionAvailable(this); } }
            public bool TryAdoptFiles() { return owner.TryAdoptFiles(this); }
            public bool LiveProofEligible { get { return false; } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            public void Dispose() { owner.DisposeAppRequest(this); }
        }
    }
}
