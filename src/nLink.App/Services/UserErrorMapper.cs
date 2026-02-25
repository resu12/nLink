using System;

namespace NLink.App.Services;

public static class UserErrorMapper
{
    public static string HelperDiscoveryTimeout() => "No one found with that code.";

    public static string HelperApprovalTimeout() => "No response yet.";

    public static string HelperGenericConnectFailure() => "Connection lost.";

    public static string NknStartFailedReinstall() => "Please reinstall.";

    public static string HelperDisconnected() => "Connection lost.";

    public static string HelperRejected() => "Permission was declined.";

    public static string HelperInvalidCode() => "Enter a valid 6-digit code.";

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

        if (ex.Message.Contains("session for code", StringComparison.OrdinalIgnoreCase))
        {
            return HelperDiscoveryTimeout();
        }

        return HelperDiscoveryTimeout();
    }
}
