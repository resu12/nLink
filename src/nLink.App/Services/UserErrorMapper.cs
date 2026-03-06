using System;

namespace NLink.App.Services;

public static class UserErrorMapper
{
    public static string HelperDiscoveryTimeout() => "No response from target address.";

    public static string HelperApprovalTimeout() => "No response yet.";

    public static string HelperGenericConnectFailure() => "Connection lost.";

    public static string NknStartFailedReinstall() => "Please reinstall.";

    public static string HelperDisconnected() => "Connection lost.";

    public static string HelperRejected() => "Permission was declined.";

    public static string HelperInvalidCode() => "Enter a valid invite token.";

    public static string HelperInvalidConnectInput() => "Enter a valid invite token.";

    public static string HelperInviteRequired() => "Use the helpee invite token.";

    public static string HelpeeHostStartFailure() => "Please reinstall.";

    public static bool IsNknStartFailure(string? lastError)
    {
        return !string.IsNullOrWhiteSpace(lastError) &&
               lastError.StartsWith("NKN_START_FAILED:", StringComparison.OrdinalIgnoreCase);
    }

    public static string FromHelperTimeoutException(TimeoutException ex)
    {
        if (ex is null)
        {
            return HelperDiscoveryTimeout();
        }

        return HelperDiscoveryTimeout();
    }
}
