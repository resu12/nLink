using System;
using System.Diagnostics;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class CanonicalAttemptDispatch
        {
            private sealed class RoleApp
            {
                internal FrontierAppRequest Request;
                internal FrontierAppReports Reports;
                internal Process Process;
                internal bool Started;
            }
            private readonly object rolesSync = new object();
            private readonly RoleApp helpee = new RoleApp(), helper = new RoleApp();
            private RoleApp Role(string role) { return role == "helpee" ? helpee : role == "helper" ? helper : null; }

            public FrontierAppRequest TryCreateRoleRequest(string role, FrontierLaunchOwner candidate)
            {
                lock (rolesSync)
                {
                    var slot = Role(role);
                    if (!Object.ReferenceEquals(owner, candidate) || !IsCurrent || consumed != 1 || slot == null || slot.Request != null) return null;
                    slot.Request = owner.TryCreateAppRequest();
                    return IsCurrent ? slot.Request : null;
                }
            }

            public FrontierAppReports StartRoleApp(string role, FrontierAppRequest request, Process process,
                Func<int, string[]> createReports)
            {
                lock (rolesSync)
                {
                    var slot = Role(role);
                    if (!IsCurrent || consumed != 1 || slot == null || request == null ||
                        !Object.ReferenceEquals(slot.Request, request) || !request.IsOriginalFor(owner) ||
                        !request.IsCurrent || slot.Started)
                        throw new InvalidOperationException("attempt_role_launch_rejected");
                    slot.Started = true;
                    slot.Process = process;
                    if (process != null)
                        process.StartInfo.EnvironmentVariables["NLINK_APP_CANONICAL_DESTINATION_V1"] = role == "helper" ? "1" : null;
                    if (role == "helpee" && process != null)
                    {
                        process.StartInfo.RedirectStandardInput = true;
                        // Opt-in only; the original app lease and native packet
                        // still have to establish custody at selection time.
                        process.StartInfo.EnvironmentVariables["NLINK_APP_CANONICAL_SOURCE_V1"] = "1";
                    }
                    slot.Reports = request.StartAppWithReports(process, createReports);
                    return slot.Reports;
                }
            }

            // Launch/attachment evidence only. In particular this does not
            // certify Core writer drain, transfer payloads, or a verdict.
            public bool MatchesRoleLaunch(string role, FrontierAppRequest request,
                FrontierAppReports reports, Process process)
            {
                lock (rolesSync)
                {
                    try
                    {
                        var slot = Role(role);
                        if (!IsCurrent || consumed != 1 || slot == null || !slot.Started || request == null || reports == null || process == null ||
                            !Object.ReferenceEquals(slot.Request, request) || !Object.ReferenceEquals(slot.Reports, reports) ||
                            !Object.ReferenceEquals(slot.Process, process) || !request.IsOriginalFor(owner) || !request.IsCurrent ||
                            !reports.NativeLaunchReady || !Object.ReferenceEquals(request.launch, reports.Launch) ||
                            reports.Launch == null || !reports.Launch.IsCurrent || !request.native.GuiAttachmentOpen || process.HasExited) return false;
                        return reports.Launch.AppProcessId == process.Id &&
                            reports.Launch.AppProcessCreationFileTime == process.StartTime.ToUniversalTime().ToFileTimeUtc() && IsCurrent;
                    }
                    catch { return false; }
                }
            }
        }
    }
}
