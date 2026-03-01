using System;
using System.Collections.Generic;

namespace NLink.App.Services;

public static class SessionBannerPolicy
{
    public static bool ShouldShowStatusBanner(SessionUiPhase phase)
    {
        return phase is SessionUiPhase.Connecting
            or SessionUiPhase.Recovering
            or SessionUiPhase.Failed
            or SessionUiPhase.Ended;
    }

    public static bool ShouldForceVisible(SessionUiPhase phase)
    {
        return phase is SessionUiPhase.Recovering
            or SessionUiPhase.Failed
            or SessionUiPhase.Ended;
    }

    public static UserFacingStatus? BuildPhaseStatusOverride(
        SessionUiPhase phase,
        UserFacingStatus presenterStatus,
        SessionUxContext? context,
        string? fallbackMessage)
    {
        switch (phase)
        {
            case SessionUiPhase.Failed:
                var failedTitle = Coalesce(
                    context?.FailureTitle,
                    string.IsNullOrWhiteSpace(presenterStatus.Title) ? null : presenterStatus.Title,
                    "Connection issue");
                var failedMessage = Coalesce(
                    context?.FailureMessage,
                    string.IsNullOrWhiteSpace(presenterStatus.Message) ? null : presenterStatus.Message,
                    fallbackMessage,
                    "Connection lost.");
                if (presenterStatus.Kind == UserStatusKind.Failed)
                {
                    // Keep presenter-provided diagnostics metadata but enforce consistent failed UX
                    // and ensure the diagnostics affordance remains available.
                    return presenterStatus with
                    {
                        Title = failedTitle,
                        Message = failedMessage,
                        CanCopyDiagnostics = true
                    };
                }

                return UserFacingStatus.FailedStatus(failedTitle, failedMessage, presenterStatus.CorrelationId);

            case SessionUiPhase.Recovering:
                if (presenterStatus.Kind is UserStatusKind.Reconnecting or UserStatusKind.Failed)
                {
                    return null;
                }

                return new UserFacingStatus(
                    Kind: UserStatusKind.Reconnecting,
                    Title: "Reconnecting",
                    Message: Coalesce(
                        string.IsNullOrWhiteSpace(presenterStatus.Message) ? null : presenterStatus.Message,
                        fallbackMessage,
                        "Reconnecting…"),
                    Severity: FailureSeverity.Warning,
                    Attempt: presenterStatus.Attempt,
                    NextRetryInSeconds: presenterStatus.NextRetryInSeconds,
                    CanCancel: presenterStatus.CanCancel,
                    CanCopyDiagnostics: false,
                    CorrelationId: presenterStatus.CorrelationId);

            case SessionUiPhase.Ended:
                return new UserFacingStatus(
                    Kind: UserStatusKind.Degraded,
                    Title: "Session ended",
                    Message: Coalesce(fallbackMessage, "Session ended."),
                    Severity: FailureSeverity.Info,
                    CanCancel: false,
                    CanCopyDiagnostics: false,
                    CorrelationId: presenterStatus.CorrelationId);

            default:
                return null;
        }
    }

    public static string? BuildDetailsText(
        SessionUiPhase phase,
        string? failureCategory,
        string? sessionCorrelationId,
        string? lastConnectDuration,
        string? lastHandshakeDuration,
        string? bridgeState,
        SessionUxContext? context)
    {
        if (phase is not (SessionUiPhase.Failed or SessionUiPhase.Recovering or SessionUiPhase.Ended))
        {
            return null;
        }

        var lines = new List<string>(6);
        if (!string.IsNullOrWhiteSpace(failureCategory))
        {
            lines.Add($"Failure category: {failureCategory}");
        }

        if (!string.IsNullOrWhiteSpace(sessionCorrelationId))
        {
            lines.Add($"Session ID: {sessionCorrelationId}");
        }

        if (!string.IsNullOrWhiteSpace(lastConnectDuration))
        {
            lines.Add($"Last connect time: {lastConnectDuration}");
        }

        if (!string.IsNullOrWhiteSpace(lastHandshakeDuration))
        {
            lines.Add($"Last handshake time: {lastHandshakeDuration}");
        }

        if (!string.IsNullOrWhiteSpace(bridgeState))
        {
            lines.Add($"Bridge state: {bridgeState}");
        }

        if (lines.Count == 0 && !string.IsNullOrWhiteSpace(context?.FailureMessage))
        {
            lines.Add(context.FailureMessage!);
        }

        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    private static string Coalesce(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }
}
