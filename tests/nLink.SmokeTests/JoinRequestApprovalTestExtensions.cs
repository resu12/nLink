using NLink.Core;
using NLink.Core.SessionSecurity;

namespace NLink.SmokeTests;

internal static class JoinRequestApprovalTestExtensions
{
    public static ApprovalDecision CreateApprovalDecision(this IncomingJoinRequestEventArgs joinRequest)
    {
        ArgumentNullException.ThrowIfNull(joinRequest);
        if (joinRequest.ApprovalRequest is not ApprovalRequest approvalRequest)
        {
            throw new InvalidOperationException("ApprovalRequest is required to create an explicit approval decision.");
        }

        var nowUtc = DateTimeOffset.UtcNow;
        return approvalRequest.CreateDecision(
            approvalRequest.RequestedCapabilities,
            nowUtc.Add(SessionSecurityDefaults.GrantLifetime),
            nowUtc);
    }
}
