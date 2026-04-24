using NLink.App.Services;
using NLink.Core.SessionSecurity;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class SessionFlowReducerTests
{
    [Fact]
    public void HelperListener_Approval_Connect_End_Reset_FollowsExpectedPhases()
    {
        var state = SessionFlowState.Initial;

        state = SessionFlowReducer.Reduce(
            state,
            new SessionFlowEvent(SessionFlowEventKind.StartHelperListener, SessionRuntimeRole.Helper));
        Assert.Equal(SessionFlowPhase.ListenerWaiting, state.Phase);
        Assert.False(state.LocalEndInProgress);

        state = SessionFlowReducer.Reduce(
            state,
            new SessionFlowEvent(SessionFlowEventKind.InboundHelpRequestReceived, SessionRuntimeRole.Helper));
        Assert.Equal(SessionFlowPhase.PendingRequest, state.Phase);

        state = SessionFlowReducer.Reduce(
            state,
            new SessionFlowEvent(SessionFlowEventKind.LocalApprovalStarted, SessionRuntimeRole.Helper));
        Assert.Equal(SessionFlowPhase.PendingApproval, state.Phase);

        state = SessionFlowReducer.Reduce(
            state,
            new SessionFlowEvent(SessionFlowEventKind.TransportApproved, SessionRuntimeRole.Helper));
        Assert.Equal(SessionFlowPhase.ActiveSession, state.Phase);
        Assert.Equal(SessionFlowEndOrigin.None, state.LastEndOrigin);

        state = SessionFlowReducer.Reduce(
            state,
            new SessionFlowEvent(SessionFlowEventKind.LocalEndRequested, SessionRuntimeRole.Helper, Reason: "user_end"));
        Assert.Equal(SessionFlowPhase.TearingDown, state.Phase);
        Assert.True(state.LocalEndInProgress);
        Assert.Equal(SessionFlowEndOrigin.Local, state.LastEndOrigin);

        state = SessionFlowReducer.Reduce(
            state,
            new SessionFlowEvent(SessionFlowEventKind.ResetCompleted, SessionRuntimeRole.Helper, Reason: "disconnect_complete"));
        Assert.Equal(SessionFlowPhase.ListenerWaiting, state.Phase);
        Assert.False(state.LocalEndInProgress);
        Assert.Equal(string.Empty, state.FailureReason);
    }

    [Fact]
    public void HelpeeRequest_Reject_RecordsRejectedFailure()
    {
        var state = SessionFlowReducer.Reduce(
            SessionFlowState.Initial,
            new SessionFlowEvent(SessionFlowEventKind.StartHelpee, SessionRuntimeRole.Helpee));

        state = SessionFlowReducer.Reduce(
            state,
            new SessionFlowEvent(SessionFlowEventKind.OutboundHelpRequestSent, SessionRuntimeRole.Helpee));
        Assert.Equal(SessionFlowPhase.PendingRequest, state.Phase);

        state = SessionFlowReducer.Reduce(
            state,
            new SessionFlowEvent(SessionFlowEventKind.TransportRejected, SessionRuntimeRole.Helpee, Reason: "helper_rejected"));
        Assert.Equal(SessionFlowPhase.Failed, state.Phase);
        Assert.Equal(SessionFlowEndOrigin.Rejected, state.LastEndOrigin);
        Assert.Equal("helper_rejected", state.FailureReason);
    }

    [Fact]
    public void RemoteEnd_TransitionsToEnded_WithRemoteOrigin()
    {
        var state = SessionFlowReducer.Reduce(
            SessionFlowState.Initial,
            new SessionFlowEvent(SessionFlowEventKind.TransportApproved, SessionRuntimeRole.Helpee));

        state = SessionFlowReducer.Reduce(
            state,
            new SessionFlowEvent(SessionFlowEventKind.RemoteEndReceived, SessionRuntimeRole.Helpee, Reason: "remote_session_end"));

        Assert.Equal(SessionFlowPhase.Ended, state.Phase);
        Assert.Equal(SessionFlowEndOrigin.Remote, state.LastEndOrigin);
        Assert.False(state.LocalEndInProgress);
        Assert.Equal("remote_session_end", state.FailureReason);
    }

    [Theory]
    [InlineData(SessionFlowPhase.ListenerWaiting, SessionRuntimeRole.Helper, SessionUiPhase.Waiting)]
    [InlineData(SessionFlowPhase.HelpeeWaiting, SessionRuntimeRole.Helpee, SessionUiPhase.Waiting)]
    [InlineData(SessionFlowPhase.PendingRequest, SessionRuntimeRole.Helpee, SessionUiPhase.Waiting)]
    [InlineData(SessionFlowPhase.PendingApproval, SessionRuntimeRole.Helpee, SessionUiPhase.Waiting)]
    [InlineData(SessionFlowPhase.Connecting, SessionRuntimeRole.Helper, SessionUiPhase.Connecting)]
    [InlineData(SessionFlowPhase.ActiveSession, SessionRuntimeRole.Helpee, SessionUiPhase.Connected)]
    [InlineData(SessionFlowPhase.TearingDown, SessionRuntimeRole.Helper, SessionUiPhase.Waiting)]
    [InlineData(SessionFlowPhase.Ended, SessionRuntimeRole.Helper, SessionUiPhase.Ended)]
    [InlineData(SessionFlowPhase.Failed, SessionRuntimeRole.Helper, SessionUiPhase.Failed)]
    public void ToUiPhase_MapsExpectedShellPhase(
        SessionFlowPhase phase,
        SessionRuntimeRole role,
        SessionUiPhase expected)
    {
        Assert.Equal(expected, SessionFlowReducer.ToUiPhase(phase, role));
    }
}
