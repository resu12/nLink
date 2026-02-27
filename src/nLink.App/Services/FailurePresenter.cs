namespace NLink.App.Services;

public readonly record struct FailurePresentation(
    string Title,
    string Message,
    string RecommendedAction);

public static class FailurePresenter
{
    public static FailurePresentation Present(TransportFailureCategory category)
    {
        return category switch
        {
            TransportFailureCategory.BridgeStartFailure => new(
                "Connection system unavailable",
                "We couldn't start the connection system.",
                "Please reinstall. If it keeps happening, use Copy Diagnostics."),

            TransportFailureCategory.BridgeUnresponsive => new(
                "Connection system not responding",
                "The connection system stopped responding.",
                "Try again. If it keeps happening, restart nLink and use Copy Diagnostics."),

            TransportFailureCategory.BridgeCrashed => new(
                "Connection system closed",
                "The connection system closed unexpectedly.",
                "Try again. If it keeps happening, restart nLink and use Copy Diagnostics."),

            TransportFailureCategory.HandshakeTimeout => new(
                "No response yet",
                "The other person did not respond in time.",
                "Try again. Ask them to keep nLink open. If it keeps happening, use Copy Diagnostics."),

            TransportFailureCategory.PeerUnreachable => new(
                "No one found with that code",
                "We couldn't find anyone using that code.",
                "Check the 6-digit code and try again. If it keeps happening, use Copy Diagnostics."),

            TransportFailureCategory.NknSendFailure => new(
                "Connection problem",
                "We couldn't send the connection request.",
                "Try again. If it keeps happening, use Copy Diagnostics."),

            TransportFailureCategory.JsonProtocolError => new(
                "Connection problem",
                "The connection system returned an invalid response.",
                "Restart nLink. If it keeps happening, use Copy Diagnostics."),

            TransportFailureCategory.UnexpectedProcessExit => new(
                "Connection system closed",
                "The connection system closed unexpectedly.",
                "Try again. If it keeps happening, restart nLink and use Copy Diagnostics."),

            TransportFailureCategory.UserCancelled => new(
                "Session ended",
                "The session was ended.",
                "You can start again. If you need help, use Copy Diagnostics."),

            TransportFailureCategory.Unknown => new(
                "Connection problem",
                "Something went wrong while connecting.",
                "Try again. If it keeps happening, use Copy Diagnostics."),

            _ => new(
                "Connection problem",
                "Something went wrong while connecting.",
                "Try again. If it keeps happening, use Copy Diagnostics.")
        };
    }
}

