using System;
using NLink.Core;
using NLink.Core.SessionSecurity;

namespace NLink.App.Services;

internal readonly record struct SessionFlowProjectionInput(
    SessionFlowState Reduced,
    SessionRuntimeRole Role,
    SessionRuntimeState RuntimeState,
    TransportState TransportState,
    string StatusText,
    bool Disposed,
    bool IsTransientStatusVisible,
    bool IsHelperListenerMode,
    bool ShouldReturnHelperListenerToWaiting,
    bool HasPendingRequest,
    bool HasPendingApproval,
    bool ApprovalActive,
    CapabilityGrant ApprovedCapabilities,
    string? SessionId,
    string? HelperIdentity,
    string? RemoteEndpoint,
    SessionVerificationCode? VerificationCode,
    HelperConnectOrigin HelperConnectOrigin,
    TransportFailure? LastTransportFailure);

internal static class SessionFlowProjector
{
    public static SessionFlowSnapshot Project(SessionFlowProjectionInput input)
    {
        var projectedPhase = ResolveProjectedSessionFlowPhase(input);
        var terminalPresentation = BuildProjectedTerminalPresentation(input, projectedPhase);
        var projectedUiPhase = ResolveProjectedUiPhase(projectedPhase, terminalPresentation, input.IsTransientStatusVisible, input.Role);
        var displayProjection = BuildDisplayProjection(input, projectedPhase, projectedUiPhase, terminalPresentation);

        return new SessionFlowSnapshot(
            projectedPhase,
            projectedUiPhase,
            input.Role,
            input.RuntimeState,
            input.TransportState,
            input.Reduced.LastEndOrigin,
            input.Reduced.LocalEndInProgress,
            input.HasPendingRequest,
            input.HasPendingApproval,
            input.ApprovalActive,
            input.ApprovedCapabilities,
            terminalPresentation.SuppressConnectedControls,
            terminalPresentation.Kind,
            terminalPresentation.StatusText,
            terminalPresentation.FailureTitle,
            terminalPresentation.FailureMessage,
            terminalPresentation.FailureActionText,
            terminalPresentation.ShowPeerEndedNotice,
            terminalPresentation.ClearConversationUi,
            input.StatusText,
            input.Reduced.FailureReason,
            input.SessionId,
            input.HelperIdentity,
            input.RemoteEndpoint,
            input.VerificationCode,
            displayProjection.StatusText,
            displayProjection.ConnectionState,
            displayProjection.ShowRetryAction,
            displayProjection.ShowDiagnosticsAction,
            displayProjection.ShowIncomingApproval,
            displayProjection.PostTerminalAction);
    }

    private static SessionFlowPhase ResolveProjectedSessionFlowPhase(SessionFlowProjectionInput input)
    {
        if (input.Disposed)
        {
            return SessionFlowPhase.NoSession;
        }

        if (input.Reduced.LocalEndInProgress &&
            (input.RuntimeState is SessionRuntimeState.Connected or SessionRuntimeState.Connecting ||
             input.TransportState is TransportState.Connected or TransportState.Reconnecting or TransportState.Handshake))
        {
            return SessionFlowPhase.TearingDown;
        }

        if (input.RuntimeState == SessionRuntimeState.Connected)
        {
            return SessionFlowPhase.ActiveSession;
        }

        if (input.RuntimeState == SessionRuntimeState.IncomingJoinRequest || input.HasPendingApproval)
        {
            return SessionFlowPhase.PendingApproval;
        }

        if (input.HasPendingRequest)
        {
            return SessionFlowPhase.PendingRequest;
        }

        if (input.RuntimeState == SessionRuntimeState.Waiting)
        {
            return input.Role == SessionRuntimeRole.Helper
                ? SessionFlowPhase.ListenerWaiting
                : SessionFlowPhase.HelpeeWaiting;
        }

        if (input.RuntimeState == SessionRuntimeState.Connecting ||
            input.TransportState is TransportState.Connecting or TransportState.Handshake or TransportState.BridgeStarting or TransportState.TransportInitializing)
        {
            return SessionFlowPhase.Connecting;
        }

        if (input.RuntimeState is SessionRuntimeState.Failed or SessionRuntimeState.Rejected or SessionRuntimeState.Disconnected)
        {
            if (input.RuntimeState == SessionRuntimeState.Rejected)
            {
                return SessionFlowPhase.Failed;
            }

            if (input.Role == SessionRuntimeRole.Helper &&
                input.ShouldReturnHelperListenerToWaiting)
            {
                return SessionFlowPhase.ListenerWaiting;
            }

            if (input.Role == SessionRuntimeRole.Helpee &&
                !input.Reduced.HadActiveSession)
            {
                return SessionFlowPhase.HelpeeWaiting;
            }

            if (input.Reduced.LastEndOrigin == SessionFlowEndOrigin.Remote &&
                input.Reduced.HadActiveSession &&
                input.LastTransportFailure is null)
            {
                return SessionFlowPhase.Ended;
            }

            return SessionFlowPhase.Failed;
        }

        if (input.RuntimeState == SessionRuntimeState.Idle)
        {
            if (input.Reduced.Phase is SessionFlowPhase.ListenerWaiting or SessionFlowPhase.HelpeeWaiting)
            {
                return input.Reduced.Phase;
            }

            return input.Role switch
            {
                SessionRuntimeRole.Helper => SessionFlowPhase.ListenerWaiting,
                SessionRuntimeRole.Helpee => SessionFlowPhase.HelpeeWaiting,
                _ => SessionFlowPhase.NoSession,
            };
        }

        return input.Reduced.Phase;
    }

    private static SessionUiPhase ResolveProjectedUiPhase(
        SessionFlowPhase projectedPhase,
        SessionTerminalPresentation terminalPresentation,
        bool isTransientStatusVisible,
        SessionRuntimeRole role)
    {
        return terminalPresentation.Kind switch
        {
            SessionTerminalKind.LocalEnded => SessionUiPhase.Waiting,
            SessionTerminalKind.PeerEnded => SessionUiPhase.Waiting,
            SessionTerminalKind.Rejected => SessionUiPhase.Failed,
            SessionTerminalKind.Failed => isTransientStatusVisible ? SessionUiPhase.Recovering : SessionUiPhase.Failed,
            _ => SessionFlowReducer.ToUiPhase(projectedPhase, role),
        };
    }

    private static SessionTerminalPresentation BuildProjectedTerminalPresentation(
        SessionFlowProjectionInput input,
        SessionFlowPhase projectedPhase)
    {
        var waitingStatus = GetWaitingStatusTextForRole(input.Role);

        if (input.Reduced.LocalEndInProgress || projectedPhase == SessionFlowPhase.TearingDown)
        {
            return new SessionTerminalPresentation(
                SessionTerminalKind.LocalEnded,
                waitingStatus,
                string.Empty,
                string.Empty,
                string.Empty,
                ShowPeerEndedNotice: false,
                ClearConversationUi: true,
                SuppressConnectedControls: true);
        }

        if (projectedPhase is SessionFlowPhase.ListenerWaiting or SessionFlowPhase.HelpeeWaiting &&
            input.Reduced.LastEndOrigin == SessionFlowEndOrigin.Failed &&
            ((input.Role == SessionRuntimeRole.Helper && input.ShouldReturnHelperListenerToWaiting) ||
             (input.Role != SessionRuntimeRole.Helper && !input.Reduced.HadActiveSession)))
        {
            return SessionTerminalPresentationNone;
        }

        return input.Reduced.LastEndOrigin switch
        {
            SessionFlowEndOrigin.Local => new SessionTerminalPresentation(
                SessionTerminalKind.LocalEnded,
                waitingStatus,
                string.Empty,
                string.Empty,
                string.Empty,
                ShowPeerEndedNotice: false,
                ClearConversationUi: true,
                SuppressConnectedControls: true),
            SessionFlowEndOrigin.Rejected => BuildRejectedTerminalPresentation(input),
            SessionFlowEndOrigin.Failed => BuildFailedTerminalPresentation(input),
            SessionFlowEndOrigin.Remote => BuildRemoteTerminalPresentation(input, projectedPhase),
            _ => SessionTerminalPresentationNone,
        };
    }

    private static SessionTerminalPresentation BuildRemoteTerminalPresentation(
        SessionFlowProjectionInput input,
        SessionFlowPhase projectedPhase)
    {
        if (!input.Reduced.HadActiveSession)
        {
            return SessionTerminalPresentationNone;
        }

        if (input.LastTransportFailure is not null &&
            projectedPhase == SessionFlowPhase.Failed)
        {
            return BuildFailedTerminalPresentation(input);
        }

        return new SessionTerminalPresentation(
            SessionTerminalKind.PeerEnded,
            input.Role switch
            {
                SessionRuntimeRole.Helper => "The other person ended the session.",
                SessionRuntimeRole.Helpee => "The other side ended the session.",
                _ => "The session ended."
            },
            string.Empty,
            string.Empty,
            string.Empty,
            ShowPeerEndedNotice: true,
            ClearConversationUi: true,
            SuppressConnectedControls: true);
    }

    private static SessionTerminalPresentation BuildRejectedTerminalPresentation(SessionFlowProjectionInput input)
    {
        return input.Role switch
        {
            SessionRuntimeRole.Helper => new SessionTerminalPresentation(
                SessionTerminalKind.Rejected,
                string.IsNullOrWhiteSpace(input.StatusText) ? UserErrorMapper.HelperRejected() : input.StatusText,
                "Request rejected",
                "The other side declined the session.",
                "Start new session",
                ShowPeerEndedNotice: false,
                ClearConversationUi: true,
                SuppressConnectedControls: true),
            SessionRuntimeRole.Helpee => new SessionTerminalPresentation(
                SessionTerminalKind.Rejected,
                string.IsNullOrWhiteSpace(input.StatusText) ? "Request was rejected." : input.StatusText,
                "Request rejected",
                "The helper declined the request.",
                "Start new session",
                ShowPeerEndedNotice: false,
                ClearConversationUi: true,
                SuppressConnectedControls: true),
            _ => new SessionTerminalPresentation(
                SessionTerminalKind.Rejected,
                string.IsNullOrWhiteSpace(input.StatusText) ? "Request was rejected." : input.StatusText,
                "Request rejected",
                "The request was declined.",
                "Retry",
                ShowPeerEndedNotice: false,
                ClearConversationUi: true,
                SuppressConnectedControls: true),
        };
    }

    private static SessionTerminalPresentation BuildFailedTerminalPresentation(SessionFlowProjectionInput input)
    {
        if (input.Role == SessionRuntimeRole.Helper &&
            (string.Equals(input.StatusText, UserErrorMapper.HelperApprovalTimeout(), StringComparison.Ordinal) ||
             input.LastTransportFailure?.Category == TransportFailureCategory.HandshakeTimeout))
        {
            return new SessionTerminalPresentation(
                SessionTerminalKind.Failed,
                string.IsNullOrWhiteSpace(input.StatusText) ? UserErrorMapper.HelperApprovalTimeout() : input.StatusText,
                "No response yet",
                "The other person did not respond in time.",
                "Retry",
                ShowPeerEndedNotice: false,
                ClearConversationUi: true,
                SuppressConnectedControls: true);
        }

        return new SessionTerminalPresentation(
            SessionTerminalKind.Failed,
            string.IsNullOrWhiteSpace(input.StatusText)
                ? input.Role == SessionRuntimeRole.Helpee ? "Connection lost." : UserErrorMapper.HelperDisconnected()
                : input.StatusText,
            "Connection failed",
            "The session ended due to a connection problem.",
            "Retry",
            ShowPeerEndedNotice: false,
            ClearConversationUi: true,
            SuppressConnectedControls: true);
    }

    private static SessionFlowDisplayProjection BuildDisplayProjection(
        SessionFlowProjectionInput input,
        SessionFlowPhase projectedPhase,
        SessionUiPhase projectedUiPhase,
        SessionTerminalPresentation terminalPresentation)
    {
        if (terminalPresentation.Kind != SessionTerminalKind.None)
        {
            var terminalConnectionState = terminalPresentation.Kind switch
            {
                SessionTerminalKind.Rejected when input.Role == SessionRuntimeRole.Helper => "Rejected",
                SessionTerminalKind.Rejected => "Failed",
                SessionTerminalKind.Failed => "Failed",
                _ => "Waiting",
            };

            return new SessionFlowDisplayProjection(
                terminalPresentation.Kind is SessionTerminalKind.LocalEnded or SessionTerminalKind.PeerEnded
                    ? GetWaitingStatusTextForRole(input.Role)
                    : terminalPresentation.StatusText,
                terminalConnectionState,
                ShowRetryAction: terminalPresentation.Kind is SessionTerminalKind.Rejected or SessionTerminalKind.Failed,
                ShowDiagnosticsAction: terminalPresentation.Kind is SessionTerminalKind.Rejected or SessionTerminalKind.Failed,
                ShowIncomingApproval: false,
                PostTerminalAction: BuildPostTerminalAction(input, terminalPresentation));
        }

        var waitingStatus = string.IsNullOrWhiteSpace(input.StatusText)
            ? GetWaitingStatusTextForRole(input.Role)
            : input.StatusText;

        return projectedPhase switch
        {
            SessionFlowPhase.PendingApproval when input.Role == SessionRuntimeRole.Helpee => new SessionFlowDisplayProjection(
                string.IsNullOrWhiteSpace(input.StatusText)
                    ? "Helper on this PC wants to connect. Click Allow."
                    : input.StatusText,
                "IncomingRequest",
                ShowRetryAction: false,
                ShowDiagnosticsAction: false,
                ShowIncomingApproval: true,
                PostTerminalAction: SessionFlowPostTerminalAction.None),
            SessionFlowPhase.ActiveSession => new SessionFlowDisplayProjection(
                string.IsNullOrWhiteSpace(input.StatusText) ? "Connected" : input.StatusText,
                "Connected",
                ShowRetryAction: false,
                ShowDiagnosticsAction: true,
                ShowIncomingApproval: false,
                PostTerminalAction: SessionFlowPostTerminalAction.None),
            SessionFlowPhase.Connecting or SessionFlowPhase.PendingApproval => new SessionFlowDisplayProjection(
                string.IsNullOrWhiteSpace(input.StatusText) ? "Connecting…" : input.StatusText,
                "Connecting",
                ShowRetryAction: false,
                ShowDiagnosticsAction: true,
                ShowIncomingApproval: false,
                PostTerminalAction: SessionFlowPostTerminalAction.None),
            _ => new SessionFlowDisplayProjection(
                waitingStatus,
                "Waiting",
                ShowRetryAction: projectedUiPhase == SessionUiPhase.Failed,
                ShowDiagnosticsAction: projectedUiPhase is SessionUiPhase.Connecting or SessionUiPhase.Connected or SessionUiPhase.Recovering or SessionUiPhase.Failed or SessionUiPhase.Ended,
                ShowIncomingApproval: false,
                PostTerminalAction: SessionFlowPostTerminalAction.None),
        };
    }

    private static SessionFlowPostTerminalAction BuildPostTerminalAction(
        SessionFlowProjectionInput input,
        SessionTerminalPresentation terminalPresentation)
    {
        var helperShouldReturnToListenerWaiting =
            input.Role == SessionRuntimeRole.Helper &&
            input.ShouldReturnHelperListenerToWaiting;
        var helperApprovalTimedOut =
            helperShouldReturnToListenerWaiting &&
            terminalPresentation.Kind == SessionTerminalKind.Failed &&
            (string.Equals(terminalPresentation.StatusText, UserErrorMapper.HelperApprovalTimeout(), StringComparison.Ordinal) ||
             input.LastTransportFailure?.Category == TransportFailureCategory.HandshakeTimeout);

        return input.Role switch
        {
            SessionRuntimeRole.Helper when helperShouldReturnToListenerWaiting &&
                                           terminalPresentation.Kind == SessionTerminalKind.Rejected
                => SessionFlowPostTerminalAction.ReturnToListenerWaiting,
            SessionRuntimeRole.Helper when helperApprovalTimedOut
                => SessionFlowPostTerminalAction.ReturnToListenerWaiting,
            SessionRuntimeRole.Helper when input.HelperConnectOrigin == HelperConnectOrigin.Listener &&
                                           terminalPresentation.Kind == SessionTerminalKind.Failed &&
                                           !input.Reduced.HadActiveSession
                => SessionFlowPostTerminalAction.ReturnToListenerWaiting,
            SessionRuntimeRole.Helpee when terminalPresentation.Kind == SessionTerminalKind.Rejected
                => SessionFlowPostTerminalAction.ReturnToWaitingPreserveBootstrap,
            SessionRuntimeRole.Helpee when terminalPresentation.Kind is SessionTerminalKind.LocalEnded or SessionTerminalKind.PeerEnded
                => SessionFlowPostTerminalAction.ReturnToWaiting,
            SessionRuntimeRole.Helpee when terminalPresentation.Kind == SessionTerminalKind.Failed && !input.Reduced.HadActiveSession
                => SessionFlowPostTerminalAction.ReturnToWaitingPreserveBootstrap,
            _ => SessionFlowPostTerminalAction.None,
        };
    }

    private static string GetWaitingStatusTextForRole(SessionRuntimeRole role)
    {
        return role switch
        {
            SessionRuntimeRole.Helper => "Waiting for help requests…",
            SessionRuntimeRole.Helpee => "Waiting for helper…",
            _ => string.Empty,
        };
    }

    private static SessionTerminalPresentation SessionTerminalPresentationNone =>
        new(
            SessionTerminalKind.None,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            ShowPeerEndedNotice: false,
            ClearConversationUi: false,
            SuppressConnectedControls: false);
}
