using System;

namespace NLink.App.Services;

public sealed record FailurePresentationDefaults(
    string Title,
    string Message,
    string ActionText);

public static class FailurePresentationPolicy
{
    private static readonly FailurePresentationDefaults Rejected = new(
        Title: "Request was rejected",
        Message: "The request was rejected. Start a new session to try again.",
        ActionText: "Start new session");

    private static readonly FailurePresentationDefaults Failed = new(
        Title: "Couldn't connect",
        Message: "Couldn't connect. Check the code and try again.",
        ActionText: "Retry");

    private static readonly FailurePresentationDefaults Disconnected = new(
        Title: "Connection lost",
        Message: "Connection lost. Check the connection and try again.",
        ActionText: "Retry");

    public static FailurePresentationDefaults? Resolve(
        SessionRuntimeState runtimeState,
        string? connectionState,
        UserFacingStatus? bannerStatus = null)
    {
        if (runtimeState == SessionRuntimeState.Rejected ||
            string.Equals(connectionState, "Rejected", StringComparison.Ordinal))
        {
            return Rejected;
        }

        if (runtimeState == SessionRuntimeState.Disconnected ||
            string.Equals(connectionState, "Disconnected", StringComparison.Ordinal) ||
            IsDisconnectedHint(bannerStatus))
        {
            return Disconnected;
        }

        if (runtimeState == SessionRuntimeState.Failed ||
            string.Equals(connectionState, "Failed", StringComparison.Ordinal) ||
            bannerStatus?.Kind == UserStatusKind.Failed)
        {
            return Failed;
        }

        return null;
    }

    private static bool IsDisconnectedHint(UserFacingStatus? bannerStatus)
    {
        if (bannerStatus is null)
        {
            return false;
        }

        return bannerStatus.Kind == UserStatusKind.Reconnecting ||
               (!string.IsNullOrWhiteSpace(bannerStatus.Message) &&
                bannerStatus.Message.IndexOf("lost", StringComparison.OrdinalIgnoreCase) >= 0);
    }
}
