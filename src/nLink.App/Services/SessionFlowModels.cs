using System;
using NLink.Core;
using NLink.Core.SessionSecurity;

namespace NLink.App.Services;

public enum SessionFlowPhase
{
    NoSession,
    ListenerWaiting,
    HelpeeWaiting,
    PendingRequest,
    PendingApproval,
    Connecting,
    ActiveSession,
    TearingDown,
    Ended,
    Failed,
}

public enum SessionFlowEndOrigin
{
    None,
    Local,
    Remote,
    Rejected,
    Failed,
}

public enum SessionTerminalKind
{
    None,
    LocalEnded,
    PeerEnded,
    Rejected,
    Failed,
}

public enum SessionFlowPostTerminalAction
{
    None,
    ReturnToWaiting,
    ReturnToWaitingPreserveBootstrap,
    ReturnToListenerWaiting,
}

internal enum HelperConnectOrigin
{
    None,
    Listener,
    DirectInvite,
    IncomingHelpRequest,
}

internal enum SessionFlowEventKind
{
    None,
    StartHelpee,
    StartHelperListener,
    StartHelperConnect,
    OutboundHelpRequestSent,
    InboundHelpRequestReceived,
    InboundJoinRequestReceived,
    LocalApprovalStarted,
    TransportApproved,
    LocalEndRequested,
    RemoteEndReceived,
    TransportRejected,
    TransportDisconnected,
    ResetRequested,
    ResetCompleted,
    FailureObserved,
}

internal readonly record struct SessionFlowEvent(
    SessionFlowEventKind Kind,
    SessionRuntimeRole Role = SessionRuntimeRole.None,
    SessionRuntimeState RuntimeState = SessionRuntimeState.Idle,
    TransportState TransportState = TransportState.Idle,
    string? Reason = null);

internal sealed record SessionFlowState(
    SessionFlowPhase Phase,
    SessionFlowEndOrigin LastEndOrigin,
    bool LocalEndInProgress,
    bool HadActiveSession,
    string FailureReason)
{
    public static SessionFlowState Initial { get; } = new(
        SessionFlowPhase.NoSession,
        SessionFlowEndOrigin.None,
        LocalEndInProgress: false,
        HadActiveSession: false,
        FailureReason: string.Empty);
}

public sealed record SessionFlowSnapshot(
    SessionFlowPhase Phase,
    SessionUiPhase UiPhase,
    SessionRuntimeRole Role,
    SessionRuntimeState RuntimeState,
    TransportState TransportState,
    SessionFlowEndOrigin LastEndOrigin,
    bool LocalEndInProgress,
    bool HasPendingRequest,
    bool HasPendingApproval,
    bool ApprovalActive,
    CapabilityGrant ApprovedCapabilities,
    bool ShouldSuppressConnectedControls,
    SessionTerminalKind TerminalKind,
    string TerminalStatusText,
    string FailureTitle,
    string FailureMessage,
    string FailureActionText,
    bool ShouldShowPeerEndedNotice,
    bool ShouldClearConversationUi,
    string StatusText,
    string FailureReason,
    string? SessionId,
    string? HelperIdentity,
    string? RemoteEndpoint,
    SessionVerificationCode? VerificationCode = null,
    string DisplayStatusText = "",
    string DisplayConnectionState = "",
    bool ShowRetryAction = false,
    bool ShowDiagnosticsAction = false,
    bool ShowIncomingApproval = false,
    SessionFlowPostTerminalAction PostTerminalAction = SessionFlowPostTerminalAction.None)
{
    public bool IsConnectedShellVisible => Phase == SessionFlowPhase.ActiveSession;
    public bool SuppressConnectedControls => ShouldSuppressConnectedControls;
    public bool CanUseChatControls => IsConnectedShellVisible && !SuppressConnectedControls && ApprovalActive;
}

internal readonly record struct SessionTerminalPresentation(
    SessionTerminalKind Kind,
    string StatusText,
    string FailureTitle,
    string FailureMessage,
    string FailureActionText,
    bool ShowPeerEndedNotice,
    bool ClearConversationUi,
    bool SuppressConnectedControls);

internal readonly record struct SessionFlowDisplayProjection(
    string StatusText,
    string ConnectionState,
    bool ShowRetryAction,
    bool ShowDiagnosticsAction,
    bool ShowIncomingApproval,
    SessionFlowPostTerminalAction PostTerminalAction);

public sealed class SessionFlowSnapshotChangedEventArgs : EventArgs
{
    public SessionFlowSnapshotChangedEventArgs(SessionFlowSnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    public SessionFlowSnapshot Snapshot { get; }
}

internal static class SessionFlowReducer
{
    public static SessionFlowState Reduce(SessionFlowState current, SessionFlowEvent flowEvent)
    {
        var next = current;
        switch (flowEvent.Kind)
        {
            case SessionFlowEventKind.StartHelpee:
                next = current with
                {
                    Phase = SessionFlowPhase.HelpeeWaiting,
                    LastEndOrigin = SessionFlowEndOrigin.None,
                    LocalEndInProgress = false,
                    HadActiveSession = false,
                    FailureReason = string.Empty,
                };
                break;

            case SessionFlowEventKind.StartHelperListener:
                next = current with
                {
                    Phase = SessionFlowPhase.ListenerWaiting,
                    LastEndOrigin = SessionFlowEndOrigin.None,
                    LocalEndInProgress = false,
                    HadActiveSession = false,
                    FailureReason = string.Empty,
                };
                break;

            case SessionFlowEventKind.StartHelperConnect:
                next = current with
                {
                    Phase = SessionFlowPhase.Connecting,
                    LastEndOrigin = SessionFlowEndOrigin.None,
                    LocalEndInProgress = false,
                    HadActiveSession = false,
                    FailureReason = string.Empty,
                };
                break;

            case SessionFlowEventKind.OutboundHelpRequestSent:
            case SessionFlowEventKind.InboundHelpRequestReceived:
                next = current with
                {
                    Phase = SessionFlowPhase.PendingRequest,
                    LastEndOrigin = SessionFlowEndOrigin.None,
                    LocalEndInProgress = false,
                    HadActiveSession = false,
                    FailureReason = string.Empty,
                };
                break;

            case SessionFlowEventKind.InboundJoinRequestReceived:
            case SessionFlowEventKind.LocalApprovalStarted:
                next = current with
                {
                    Phase = SessionFlowPhase.PendingApproval,
                    LastEndOrigin = SessionFlowEndOrigin.None,
                    LocalEndInProgress = false,
                    FailureReason = string.Empty,
                };
                break;

            case SessionFlowEventKind.TransportApproved:
                next = current with
                {
                    Phase = SessionFlowPhase.ActiveSession,
                    LastEndOrigin = SessionFlowEndOrigin.None,
                    LocalEndInProgress = false,
                    HadActiveSession = true,
                    FailureReason = string.Empty,
                };
                break;

            case SessionFlowEventKind.LocalEndRequested:
                next = current with
                {
                    Phase = SessionFlowPhase.TearingDown,
                    LastEndOrigin = SessionFlowEndOrigin.Local,
                    LocalEndInProgress = true,
                    FailureReason = string.Empty,
                };
                break;

            case SessionFlowEventKind.RemoteEndReceived:
                next = current with
                {
                    Phase = SessionFlowPhase.Ended,
                    LastEndOrigin = SessionFlowEndOrigin.Remote,
                    LocalEndInProgress = false,
                    FailureReason = string.IsNullOrWhiteSpace(flowEvent.Reason) ? "remote_session_end" : flowEvent.Reason.Trim(),
                };
                break;

            case SessionFlowEventKind.TransportRejected:
                next = current with
                {
                    Phase = SessionFlowPhase.Failed,
                    LastEndOrigin = SessionFlowEndOrigin.Rejected,
                    LocalEndInProgress = false,
                    FailureReason = string.IsNullOrWhiteSpace(flowEvent.Reason) ? "transport_rejected" : flowEvent.Reason.Trim(),
                };
                break;

            case SessionFlowEventKind.TransportDisconnected:
            case SessionFlowEventKind.FailureObserved:
                next = current with
                {
                    Phase = SessionFlowPhase.Failed,
                    LastEndOrigin = flowEvent.Kind == SessionFlowEventKind.TransportDisconnected
                        ? SessionFlowEndOrigin.Remote
                        : SessionFlowEndOrigin.Failed,
                    LocalEndInProgress = false,
                    FailureReason = string.IsNullOrWhiteSpace(flowEvent.Reason) ? "transport_disconnected" : flowEvent.Reason.Trim(),
                };
                break;

            case SessionFlowEventKind.ResetCompleted:
                next = current with
                {
                    Phase = flowEvent.Role switch
                    {
                        SessionRuntimeRole.Helper => SessionFlowPhase.ListenerWaiting,
                        SessionRuntimeRole.Helpee => SessionFlowPhase.HelpeeWaiting,
                        _ => SessionFlowPhase.NoSession,
                    },
                    LastEndOrigin = SessionFlowEndOrigin.None,
                    LocalEndInProgress = false,
                    HadActiveSession = false,
                    FailureReason = string.Empty,
                };
                break;
        }

        return next;
    }

    public static SessionUiPhase ToUiPhase(SessionFlowPhase phase, SessionRuntimeRole role)
    {
        return phase switch
        {
            SessionFlowPhase.NoSession => role == SessionRuntimeRole.Helper ? SessionUiPhase.Waiting : SessionUiPhase.Idle,
            SessionFlowPhase.ListenerWaiting or SessionFlowPhase.HelpeeWaiting or SessionFlowPhase.PendingRequest or SessionFlowPhase.PendingApproval => SessionUiPhase.Waiting,
            SessionFlowPhase.Connecting => SessionUiPhase.Connecting,
            SessionFlowPhase.ActiveSession => SessionUiPhase.Connected,
            SessionFlowPhase.TearingDown => SessionUiPhase.Waiting,
            SessionFlowPhase.Ended => SessionUiPhase.Ended,
            SessionFlowPhase.Failed => SessionUiPhase.Failed,
            _ => role == SessionRuntimeRole.Helper ? SessionUiPhase.Waiting : SessionUiPhase.Idle,
        };
    }
}
