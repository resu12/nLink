using System;

namespace NLink.App.Services;

public enum SessionUiPhase
{
    Idle,
    Waiting,
    Connecting,
    Connected,
    Recovering,
    Ended,
    Failed,
}

public enum Role
{
    None,
    Helpee,
    Helper,
}

public sealed record SessionUxContext(
    string? FailureTitle = null,
    string? FailureMessage = null,
    string? FailureActionText = null);

public static class SessionUxPhaseMapper
{
    public static SessionUiPhase FromRuntimeState(SessionRuntimeState state, bool isHelper)
    {
        return state switch
        {
            SessionRuntimeState.Idle => isHelper ? SessionUiPhase.Waiting : SessionUiPhase.Idle,
            SessionRuntimeState.Waiting => SessionUiPhase.Waiting,
            SessionRuntimeState.IncomingJoinRequest => SessionUiPhase.Waiting,
            SessionRuntimeState.Connecting => SessionUiPhase.Connecting,
            SessionRuntimeState.Connected => SessionUiPhase.Connected,
            SessionRuntimeState.Rejected => SessionUiPhase.Failed,
            SessionRuntimeState.Disconnected => SessionUiPhase.Failed,
            SessionRuntimeState.Failed => SessionUiPhase.Failed,
            _ => isHelper ? SessionUiPhase.Waiting : SessionUiPhase.Idle,
        };
    }

    public static SessionUiPhase FromRuntimeState(SessionRuntimeState state, Role role)
        => FromRuntimeState(state, isHelper: role == Role.Helper);

    public static SessionUiPhase? FromBannerStatus(UserFacingStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        return status.Kind switch
        {
            UserStatusKind.Failed => IsSessionEndedStatus(status) ? SessionUiPhase.Ended : SessionUiPhase.Failed,
            UserStatusKind.Reconnecting => SessionUiPhase.Recovering,
            _ => null,
        };
    }

    private static bool IsSessionEndedStatus(UserFacingStatus status)
    {
        return ContainsEndedPhrase(status.Title) || ContainsEndedPhrase(status.Message);
    }

    private static bool ContainsEndedPhrase(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("ended the session", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("session ended", StringComparison.OrdinalIgnoreCase);
    }
}
