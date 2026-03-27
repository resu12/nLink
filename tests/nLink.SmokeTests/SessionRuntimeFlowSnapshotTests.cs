using NLink.App.Services;
using NLink.Core;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using System.Reflection;

namespace NLink.SmokeTests;

public sealed class SessionRuntimeFlowSnapshotTests
{
    [Fact]
    public async Task StartHelpeeAsync_ProjectsHelpeeWaitingSnapshot()
    {
        using var transport = new TestSessionSecurityTransport("helpee.flow.waiting");
        using var runtime = new SessionRuntime(() => transport);

        await runtime.StartHelpeeAsync(CancellationToken.None);

        Assert.Equal(SessionRuntimeRole.Helpee, runtime.Role);
        Assert.Equal(SessionFlowPhase.HelpeeWaiting, runtime.FlowSnapshot.Phase);
        Assert.Equal(SessionUiPhase.Waiting, runtime.FlowSnapshot.UiPhase);
        Assert.Equal(SessionRuntimeState.Waiting, runtime.FlowSnapshot.RuntimeState);
        Assert.False(runtime.FlowSnapshot.ApprovalActive);
    }

    [Fact]
    public async Task StaleTransportSecurityDowngrade_DoesNotInvalidateActiveGrant()
    {
        using var transport = new TestSessionSecurityTransport("helpee.stale.guard");
        using var runtime = new SessionRuntime(() => transport);
        await runtime.StartHelpeeAsync(CancellationToken.None);

        var approvedSessionId = new SessionId("session-approved");
        var approvedHelper = new PeerAddress("helper.identity.approved");
        var approvedState = CreateApprovedSecurityState(
            new PeerAddress(transport.LocalPeerAddress),
            approvedHelper,
            approvedSessionId,
            CapabilityGrant.Chat | CapabilityGrant.ScreenShare | CapabilityGrant.FileTransfer);

        transport.SetSessionSecurityStateForTests(approvedState);
        await WaitUntilAsync(() => runtime.CurrentSessionGrant is not null, TimeSpan.FromSeconds(2));

        Assert.True(runtime.CanPerform(SessionCapability.Chat));
        Assert.True(runtime.CanPerform(SessionCapability.ScreenShare));
        Assert.True(runtime.CanPerform(SessionCapability.FileTransfer));

        var staleFailedState = SessionSecurityState.Empty with
        {
            SessionId = new SessionId("session-stale"),
            HelpeeAddress = new PeerAddress(transport.LocalPeerAddress),
            HelperAddress = new PeerAddress("helper.identity.stale"),
            InviteValidated = false,
            HandshakeState = SessionHandshakeState.Failed,
            HandshakeFailureReason = "invite_revoked",
            ApprovalGranted = false,
            ApprovedCapabilities = CapabilityGrant.None,
            ApprovalExpiresAt = null,
        };

        transport.SetSessionSecurityStateForTests(staleFailedState);

        Assert.NotNull(runtime.CurrentSessionGrant);
        Assert.Equal(approvedSessionId, runtime.CurrentSessionGrant!.SessionId);
        Assert.Equal(approvedHelper, runtime.CurrentSessionGrant.HelperIdentity);
        Assert.True(runtime.CanPerform(SessionCapability.Chat));
        Assert.True(runtime.CanPerform(SessionCapability.ScreenShare));
        Assert.True(runtime.CanPerform(SessionCapability.FileTransfer));
        Assert.True(runtime.FlowSnapshot.ApprovalActive);
    }

    [Fact]
    public async Task MatchingTransportSecurityDowngrade_InvalidatesActiveGrant()
    {
        using var transport = new TestSessionSecurityTransport("helpee.matching.guard");
        using var runtime = new SessionRuntime(() => transport);
        await runtime.StartHelpeeAsync(CancellationToken.None);

        var approvedSessionId = new SessionId("session-active");
        var approvedHelper = new PeerAddress("helper.identity.active");
        var approvedState = CreateApprovedSecurityState(
            new PeerAddress(transport.LocalPeerAddress),
            approvedHelper,
            approvedSessionId,
            CapabilityGrant.Chat | CapabilityGrant.ScreenShare);

        transport.SetSessionSecurityStateForTests(approvedState);
        await WaitUntilAsync(() => runtime.CurrentSessionGrant is not null, TimeSpan.FromSeconds(2));

        var matchingInvalidatedState = approvedState.Invalidate("security_context_changed");
        transport.SetSessionSecurityStateForTests(matchingInvalidatedState);

        await WaitUntilAsync(() => runtime.CurrentSessionGrant is null, TimeSpan.FromSeconds(2));
        Assert.False(runtime.CanPerform(SessionCapability.Chat));
        Assert.False(runtime.CanPerform(SessionCapability.ScreenShare));
        Assert.False(runtime.FlowSnapshot.ApprovalActive);
    }

    [Fact]
    public async Task MatchingLateHandshakeFailure_DoesNotInvalidateActiveGrant()
    {
        using var transport = new TestSessionSecurityTransport("helpee.matching.invite.revoked");
        using var runtime = new SessionRuntime(() => transport);
        await runtime.StartHelpeeAsync(CancellationToken.None);

        var approvedSessionId = new SessionId("session-active");
        var approvedHelper = new PeerAddress("helper.identity.active");
        var approvedState = CreateApprovedSecurityState(
            new PeerAddress(transport.LocalPeerAddress),
            approvedHelper,
            approvedSessionId,
            CapabilityGrant.Chat | CapabilityGrant.ScreenShare | CapabilityGrant.FileTransfer);

        transport.SetSessionSecurityStateForTests(approvedState);
        await WaitUntilAsync(() => runtime.CurrentSessionGrant is not null, TimeSpan.FromSeconds(2));

        var lateInviteRevokedState = approvedState.WithHandshakeFailure(SessionHandshakeState.Failed, "invite_revoked");
        transport.SetSessionSecurityStateForTests(lateInviteRevokedState);

        Assert.NotNull(runtime.CurrentSessionGrant);
        Assert.Equal(approvedSessionId, runtime.CurrentSessionGrant!.SessionId);
        Assert.Equal(approvedHelper, runtime.CurrentSessionGrant.HelperIdentity);
        Assert.True(runtime.CanPerform(SessionCapability.Chat));
        Assert.True(runtime.CanPerform(SessionCapability.ScreenShare));
        Assert.True(runtime.CanPerform(SessionCapability.FileTransfer));
        Assert.True(runtime.FlowSnapshot.ApprovalActive);
    }

    [Fact]
    public async Task RepeatedApprovedSessions_StalePriorSessionFailure_DoesNotInvalidateCurrentGrant()
    {
        using var transport = new TestSessionSecurityTransport("helpee.multi.session.guard");
        using var runtime = new SessionRuntime(() => transport);
        await runtime.StartHelpeeAsync(CancellationToken.None);

        var firstSessionId = new SessionId("session-first");
        var firstHelper = new PeerAddress("helper.identity.first");
        var firstApprovedState = CreateApprovedSecurityState(
            new PeerAddress(transport.LocalPeerAddress),
            firstHelper,
            firstSessionId,
            CapabilityGrant.Chat | CapabilityGrant.ScreenShare);

        transport.SetSessionSecurityStateForTests(firstApprovedState);
        MarkTransportApproved(runtime);
        await WaitUntilAsync(() => runtime.CurrentSessionGrant?.SessionId == firstSessionId, TimeSpan.FromSeconds(2));

        var secondSessionId = new SessionId("session-second");
        var secondHelper = new PeerAddress("helper.identity.second");
        var secondApprovedState = CreateApprovedSecurityState(
            new PeerAddress(transport.LocalPeerAddress),
            secondHelper,
            secondSessionId,
            CapabilityGrant.Chat | CapabilityGrant.ScreenShare | CapabilityGrant.FileTransfer | CapabilityGrant.Clipboard);

        transport.SetSessionSecurityStateForTests(secondApprovedState);
        MarkTransportApproved(runtime);
        await WaitUntilAsync(() => runtime.CurrentSessionGrant?.SessionId == secondSessionId, TimeSpan.FromSeconds(2));

        var staleFirstFailure = SessionSecurityState.Empty with
        {
            SessionId = firstSessionId,
            HelpeeAddress = new PeerAddress(transport.LocalPeerAddress),
            HelperAddress = firstHelper,
            InviteValidated = false,
            HandshakeState = SessionHandshakeState.Failed,
            HandshakeFailureReason = "invite_revoked",
            ApprovalGranted = false,
            ApprovedCapabilities = CapabilityGrant.None,
            ApprovalExpiresAt = null,
        };

        transport.SetSessionSecurityStateForTests(staleFirstFailure);

        Assert.NotNull(runtime.CurrentSessionGrant);
        Assert.Equal(secondSessionId, runtime.CurrentSessionGrant!.SessionId);
        Assert.Equal(secondHelper, runtime.CurrentSessionGrant.HelperIdentity);
        Assert.True(runtime.CanPerform(SessionCapability.Chat));
        Assert.True(runtime.CanPerform(SessionCapability.ScreenShare));
        Assert.True(runtime.CanPerform(SessionCapability.FileTransfer));
        Assert.True(runtime.CanPerform(SessionCapability.Clipboard));
        Assert.True(Assert.IsType<bool>(InvokePrivateMethod(runtime, "TryAuthorizeFileTransferSend")!));
        Assert.True(Assert.IsType<bool>(InvokePrivateMethod(runtime, "TryAuthorizeClipboardSync")!));
        Assert.True(runtime.FlowSnapshot.ApprovalActive);
        Assert.Equal(SessionFlowPhase.ActiveSession, runtime.FlowSnapshot.Phase);
        Assert.Equal(secondSessionId.Value, runtime.FlowSnapshot.SessionId);
        Assert.Equal(secondHelper.Value, runtime.FlowSnapshot.HelperIdentity);
        Assert.Equal(
            CapabilityGrant.Chat | CapabilityGrant.ScreenShare | CapabilityGrant.FileTransfer | CapabilityGrant.Clipboard,
            runtime.FlowSnapshot.ApprovedCapabilities);
    }

    [Fact]
    public async Task ActiveGrantSnapshot_UsesCurrentApprovedSessionIdentity()
    {
        using var transport = new TestSessionSecurityTransport("helpee.snapshot.identity");
        using var runtime = new SessionRuntime(() => transport);
        await runtime.StartHelpeeAsync(CancellationToken.None);

        var activeSessionId = new SessionId("session-snapshot");
        var activeHelper = new PeerAddress("helper.identity.snapshot");
        var approvedState = CreateApprovedSecurityState(
            new PeerAddress(transport.LocalPeerAddress),
            activeHelper,
            activeSessionId,
            CapabilityGrant.Chat | CapabilityGrant.ScreenShare);

        transport.SetSessionSecurityStateForTests(approvedState);
        MarkTransportApproved(runtime);
        await WaitUntilAsync(() => runtime.CurrentSessionGrant?.SessionId == activeSessionId, TimeSpan.FromSeconds(2));

        Assert.Equal(SessionFlowPhase.ActiveSession, runtime.FlowSnapshot.Phase);
        Assert.Equal(activeSessionId.Value, runtime.FlowSnapshot.SessionId);
        Assert.Equal(activeHelper.Value, runtime.FlowSnapshot.HelperIdentity);
        Assert.Equal(CapabilityGrant.Chat | CapabilityGrant.ScreenShare, runtime.FlowSnapshot.ApprovedCapabilities);
    }

    [Fact]
    public async Task NotifyLocalEndRequested_ProjectsLocalEndedWaitingSnapshot()
    {
        using var transport = new TestSessionSecurityTransport("helpee.local.end.snapshot");
        using var runtime = new SessionRuntime(() => transport);

        await runtime.StartHelpeeAsync(CancellationToken.None);
        runtime.NotifyLocalEndRequested();

        Assert.Equal(SessionTerminalKind.LocalEnded, runtime.FlowSnapshot.TerminalKind);
        Assert.Equal(SessionUiPhase.Waiting, runtime.FlowSnapshot.UiPhase);
        Assert.Equal(SessionFlowPhase.HelpeeWaiting, runtime.FlowSnapshot.Phase);
        Assert.True(runtime.FlowSnapshot.ShouldSuppressConnectedControls);
        Assert.True(runtime.FlowSnapshot.ShouldClearConversationUi);
        Assert.False(runtime.FlowSnapshot.ShouldShowPeerEndedNotice);
        Assert.Equal("Waiting for helper…", runtime.FlowSnapshot.DisplayStatusText);
        Assert.Equal("Waiting", runtime.FlowSnapshot.DisplayConnectionState);
        Assert.Equal(SessionFlowPostTerminalAction.ReturnToWaiting, runtime.FlowSnapshot.PostTerminalAction);
    }

    [Fact]
    public async Task RemoteEndReceived_AfterActiveSession_ProjectsPeerEndedWaitingSnapshot()
    {
        using var transport = new TestSessionSecurityTransport("helpee.remote.end.snapshot");
        using var runtime = new SessionRuntime(() => transport);
        await runtime.StartHelpeeAsync(CancellationToken.None);

        var sessionId = new SessionId("session-remote-ended");
        var helperIdentity = new PeerAddress("helper.identity.remote-ended");
        transport.SetSessionSecurityStateForTests(CreateApprovedSecurityState(
            new PeerAddress(transport.LocalPeerAddress),
            helperIdentity,
            sessionId,
            CapabilityGrant.Chat | CapabilityGrant.ScreenShare));
        MarkTransportApproved(runtime);
        await WaitUntilAsync(() => runtime.FlowSnapshot.Phase == SessionFlowPhase.ActiveSession, TimeSpan.FromSeconds(2));

        SetPrivateField(runtime, "state", SessionRuntimeState.Disconnected);
        SetPrivateField(runtime, "statusText", "The other side ended the session.");
        SetPrivateField<TransportFailure?>(runtime, "lastTransportFailure", null);
        InvokePrivateMethod(
            runtime,
            "PublishSessionFlowEvent",
            new SessionFlowEvent(
                SessionFlowEventKind.RemoteEndReceived,
                runtime.Role,
                SessionRuntimeState.Disconnected,
                runtime.FlowSnapshot.TransportState,
                "remote_end"));

        Assert.Equal(SessionTerminalKind.PeerEnded, runtime.FlowSnapshot.TerminalKind);
        Assert.Equal(SessionUiPhase.Waiting, runtime.FlowSnapshot.UiPhase);
        Assert.Equal(SessionFlowPhase.Ended, runtime.FlowSnapshot.Phase);
        Assert.Equal("The other side ended the session.", runtime.FlowSnapshot.TerminalStatusText);
        Assert.True(runtime.FlowSnapshot.ShouldShowPeerEndedNotice);
        Assert.True(runtime.FlowSnapshot.ShouldClearConversationUi);
        Assert.True(runtime.FlowSnapshot.ShouldSuppressConnectedControls);
    }

    [Fact]
    public async Task TransportRejected_ProjectsRejectedTerminalSnapshot()
    {
        using var transport = new TestSessionSecurityTransport("helpee.rejected.snapshot");
        using var runtime = new SessionRuntime(() => transport);
        await runtime.StartHelpeeAsync(CancellationToken.None);

        SetPrivateField(runtime, "state", SessionRuntimeState.Rejected);
        SetPrivateField(runtime, "statusText", "Request was rejected.");
        InvokePrivateMethod(
            runtime,
            "PublishSessionFlowEvent",
            new SessionFlowEvent(
                SessionFlowEventKind.TransportRejected,
                runtime.Role,
                SessionRuntimeState.Rejected,
                runtime.FlowSnapshot.TransportState,
                "request_rejected"));

        Assert.Equal(SessionTerminalKind.Rejected, runtime.FlowSnapshot.TerminalKind);
        Assert.Equal(SessionUiPhase.Failed, runtime.FlowSnapshot.UiPhase);
        Assert.Equal("Request rejected", runtime.FlowSnapshot.FailureTitle);
        Assert.Equal("The helper declined the request.", runtime.FlowSnapshot.FailureMessage);
        Assert.Equal("Request was rejected.", runtime.FlowSnapshot.TerminalStatusText);
        Assert.True(runtime.FlowSnapshot.ShouldClearConversationUi);
        Assert.True(runtime.FlowSnapshot.ShouldSuppressConnectedControls);
        Assert.Equal(SessionFlowPostTerminalAction.ReturnToWaitingPreserveBootstrap, runtime.FlowSnapshot.PostTerminalAction);
    }

    [Fact]
    public async Task HelperTransportRejected_DirectInvite_ProjectsRejectedTerminalSnapshot()
    {
        using var transport = new TestSessionSecurityTransport("helper.rejected.snapshot");
        using var runtime = new SessionRuntime(() => transport);
        await runtime.StartHelperListeningAsync(CancellationToken.None);

        SetPrivateField(runtime, "role", SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "helperConnectOrigin", HelperConnectOrigin.DirectInvite);
        SetPrivateField(runtime, "state", SessionRuntimeState.Rejected);
        SetPrivateField(runtime, "statusText", UserErrorMapper.HelperRejected());
        InvokePrivateMethod(
            runtime,
            "PublishSessionFlowEvent",
            new SessionFlowEvent(
                SessionFlowEventKind.TransportRejected,
                SessionRuntimeRole.Helper,
                SessionRuntimeState.Rejected,
                runtime.FlowSnapshot.TransportState,
                "request_rejected"));

        Assert.Equal(SessionTerminalKind.Rejected, runtime.FlowSnapshot.TerminalKind);
        Assert.Equal(SessionFlowPostTerminalAction.None, runtime.FlowSnapshot.PostTerminalAction);
    }

    [Fact]
    public async Task HelperApprovalTimeout_DirectInvite_ProjectsFailedTerminalSnapshot()
    {
        using var transport = new TestSessionSecurityTransport("helper.timeout.snapshot");
        using var runtime = new SessionRuntime(() => transport);
        await runtime.StartHelpeeAsync(CancellationToken.None);

        SetPrivateField(runtime, "role", SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "helperConnectOrigin", HelperConnectOrigin.DirectInvite);
        SetPrivateField(runtime, "state", SessionRuntimeState.Failed);
        SetPrivateField(runtime, "statusText", UserErrorMapper.HelperApprovalTimeout());
        SetPrivateField(
            runtime,
            "lastTransportFailure",
            TransportFailure.Create(TransportFailureCategory.HandshakeTimeout, "Timed out", isTransient: true));
        InvokePrivateMethod(
            runtime,
            "PublishSessionFlowEvent",
            new SessionFlowEvent(
                SessionFlowEventKind.FailureObserved,
                SessionRuntimeRole.Helper,
                SessionRuntimeState.Failed,
                runtime.FlowSnapshot.TransportState,
                "approval_timeout"));

        Assert.Equal(SessionTerminalKind.Failed, runtime.FlowSnapshot.TerminalKind);
        Assert.Equal("No response yet", runtime.FlowSnapshot.FailureTitle);
        Assert.Equal("The other person did not respond in time.", runtime.FlowSnapshot.FailureMessage);
        Assert.Equal(UserErrorMapper.HelperApprovalTimeout(), runtime.FlowSnapshot.TerminalStatusText);
        Assert.True(runtime.FlowSnapshot.ShouldClearConversationUi);
        Assert.True(runtime.FlowSnapshot.ShouldSuppressConnectedControls);
        Assert.Equal(SessionFlowPostTerminalAction.None, runtime.FlowSnapshot.PostTerminalAction);
    }

    [Fact]
    public async Task HelperApprovalTimeout_AfterPreviousSession_IncomingHelpRequest_RuntimeRecoveryReturnsWaiting()
    {
        using var transport = new TestSessionSecurityTransport("helper.timeout.previous.session.snapshot");
        using var runtime = new SessionRuntime(() => transport);
        await runtime.StartHelperListeningAsync(CancellationToken.None);

        SetPrivateField(
            runtime,
            "sessionFlowState",
            new SessionFlowState(
                Phase: SessionFlowPhase.Failed,
                LastEndOrigin: SessionFlowEndOrigin.Failed,
                LocalEndInProgress: false,
                HadActiveSession: true,
                FailureReason: "approval_timeout"));
        SetPrivateField(runtime, "role", SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "helperConnectOrigin", HelperConnectOrigin.IncomingHelpRequest);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connecting);
        SetPrivateField(runtime, "transportState", TransportState.Connecting);

        await runtime.HandleHelperApprovalTimeoutAsync();
        await WaitUntilAsync(
            () => runtime.Role == SessionRuntimeRole.Helper &&
                  runtime.State == SessionRuntimeState.Waiting,
            TimeSpan.FromSeconds(2));

        Assert.Equal(SessionRuntimeState.Waiting, runtime.State);
        Assert.Equal("Waiting for help requests…", runtime.StatusText);
        Assert.Equal(SessionTerminalKind.None, runtime.FlowSnapshot.TerminalKind);
        Assert.Equal(SessionUiPhase.Waiting, runtime.FlowSnapshot.UiPhase);
    }

    [Fact]
    public async Task HelperApprovalTimeout_AfterPreviousSession_IncomingHelpRequest_FailedRuntimeStillProjectsWaiting()
    {
        using var transport = new TestSessionSecurityTransport("helper.timeout.previous.session.projected.waiting");
        using var runtime = new SessionRuntime(() => transport);
        await runtime.StartHelperListeningAsync(CancellationToken.None);

        SetPrivateField(
            runtime,
            "sessionFlowState",
            new SessionFlowState(
                Phase: SessionFlowPhase.Failed,
                LastEndOrigin: SessionFlowEndOrigin.Failed,
                LocalEndInProgress: false,
                HadActiveSession: true,
                FailureReason: "approval_timeout"));
        SetPrivateField(runtime, "role", SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "helperConnectOrigin", HelperConnectOrigin.IncomingHelpRequest);
        SetPrivateField(runtime, "helperShouldReturnToListenerWaiting", true);
        SetPrivateField(runtime, "state", SessionRuntimeState.Failed);
        SetPrivateField(runtime, "transportState", TransportState.Failed);
        SetPrivateField(runtime, "statusText", UserErrorMapper.HelperApprovalTimeout());
        SetPrivateField(
            runtime,
            "lastTransportFailure",
            TransportFailureMapper.CreateTimeout("approval_timeout"));

        InvokePrivateMethod(
            runtime,
            "PublishSessionFlowEvent",
            new SessionFlowEvent(
                SessionFlowEventKind.FailureObserved,
                SessionRuntimeRole.Helper,
                SessionRuntimeState.Failed,
                TransportState.Failed,
                "approval_timeout"));

        Assert.Equal(SessionFlowPhase.ListenerWaiting, runtime.FlowSnapshot.Phase);
        Assert.Equal(SessionUiPhase.Waiting, runtime.FlowSnapshot.UiPhase);
        Assert.Equal("Waiting", runtime.FlowSnapshot.DisplayConnectionState);
        Assert.False(runtime.FlowSnapshot.ShowRetryAction);
        Assert.Equal(SessionTerminalKind.None, runtime.FlowSnapshot.TerminalKind);
    }

    [Fact]
    public async Task HelperApprovalTimeout_PublishAfterRuntimeStateUpdate_DirectInvite_ProjectsTimeoutSnapshot()
    {
        using var transport = new TestSessionSecurityTransport("helper.timeout.transport.rejected");
        using var runtime = new SessionRuntime(() => transport);
        await runtime.StartHelperListeningAsync(CancellationToken.None);

        SetPrivateField(runtime, "role", SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "helperConnectOrigin", HelperConnectOrigin.DirectInvite);
        SetPrivateField(runtime, "transportState", TransportState.Failed);
        SetPrivateField(runtime, "lastTransportFailure", TransportFailureMapper.CreateTimeout("approval_timeout"));
        InvokePrivateMethod(runtime, "SetState", SessionRuntimeState.Failed, UserErrorMapper.HelperApprovalTimeout());
        InvokePrivateMethod(
            runtime,
            "PublishSessionFlowEvent",
            new SessionFlowEvent(
                SessionFlowEventKind.TransportRejected,
                SessionRuntimeRole.Helper,
                runtime.State,
                runtime.TransportLifecycleState,
                "approval_timeout"));

        Assert.Equal(SessionRuntimeState.Failed, runtime.State);
        Assert.Equal(UserErrorMapper.HelperApprovalTimeout(), runtime.StatusText);
        Assert.Equal(SessionTerminalKind.Failed, runtime.FlowSnapshot.TerminalKind);
        Assert.Equal(UserErrorMapper.HelperApprovalTimeout(), runtime.FlowSnapshot.TerminalStatusText);
        Assert.Equal("No response yet", runtime.FlowSnapshot.FailureTitle);
        Assert.Equal(SessionFlowPostTerminalAction.None, runtime.FlowSnapshot.PostTerminalAction);
    }

    [Fact]
    public async Task HandleHelperApprovalTimeoutAsync_IncomingHelpRequest_RestartsListener()
    {
        using var transport = new TestSessionSecurityTransport("helper.timeout.runtime.recovery");
        using var runtime = new SessionRuntime(() => transport);
        await runtime.StartHelperListeningAsync(CancellationToken.None);

        SetPrivateField(runtime, "helperConnectOrigin", HelperConnectOrigin.IncomingHelpRequest);
        SetPrivateField(runtime, "role", SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connecting);
        SetPrivateField(runtime, "transportState", TransportState.Connecting);

        await runtime.HandleHelperApprovalTimeoutAsync();
        await WaitUntilAsync(
            () => runtime.Role == SessionRuntimeRole.Helper &&
                  runtime.State == SessionRuntimeState.Waiting,
            TimeSpan.FromSeconds(2));

        Assert.Equal(SessionRuntimeRole.Helper, runtime.Role);
        Assert.Equal(SessionRuntimeState.Waiting, runtime.State);
        Assert.Equal("Waiting for help requests…", runtime.StatusText);
    }

    [Fact]
    public async Task HelperHandshakeWatchdogTimeout_IncomingHelpRequest_RestartsListener()
    {
        using var transport = new TestSessionSecurityTransport("helper.timeout.watchdog.recovery");
        using var runtime = new SessionRuntime(() => transport);
        await runtime.StartHelperListeningAsync(CancellationToken.None);

        SetPrivateField(runtime, "helperConnectOrigin", HelperConnectOrigin.IncomingHelpRequest);
        SetPrivateField(runtime, "role", SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connecting);
        SetPrivateField(runtime, "transportState", TransportState.Handshake);
        SetPrivateField(runtime, "connectAttempt", 1L);
        SetPrivateField(runtime, "watchdogGeneration", 1L);

        var task = (Task)InvokePrivateMethod(
            runtime,
            "HandleWatchdogTimeoutAsync",
            TransportState.Handshake,
            1L,
            1L,
            TimeSpan.FromSeconds(30));
        await task;

        await WaitUntilAsync(
            () => runtime.Role == SessionRuntimeRole.Helper &&
                  runtime.State == SessionRuntimeState.Waiting,
            TimeSpan.FromSeconds(2));

        Assert.Equal(SessionRuntimeRole.Helper, runtime.Role);
        Assert.Equal(SessionRuntimeState.Waiting, runtime.State);
        Assert.Equal("Waiting for help requests…", runtime.StatusText);
        Assert.Equal(SessionTerminalKind.None, runtime.FlowSnapshot.TerminalKind);
        Assert.Equal(SessionUiPhase.Waiting, runtime.FlowSnapshot.UiPhase);
    }

    [Fact]
    public async Task HelpeePreConnectFailure_ProjectsWaitingRetryDispositionWithPreservedBootstrap()
    {
        using var transport = new TestSessionSecurityTransport("helpee.preconnect.failure.snapshot");
        using var runtime = new SessionRuntime(() => transport);
        await runtime.StartHelpeeAsync(CancellationToken.None);

        SetPrivateField(runtime, "state", SessionRuntimeState.Failed);
        SetPrivateField(runtime, "statusText", "Couldn't reach the helper. Check the helper ID and try again.");
        SetPrivateField(
            runtime,
            "lastTransportFailure",
            TransportFailure.Create(TransportFailureCategory.PeerUnreachable, "No route", isTransient: true));
        InvokePrivateMethod(
            runtime,
            "PublishSessionFlowEvent",
            new SessionFlowEvent(
                SessionFlowEventKind.FailureObserved,
                SessionRuntimeRole.Helpee,
                SessionRuntimeState.Failed,
                runtime.FlowSnapshot.TransportState,
                "preconnect_failed"));

        Assert.Equal(SessionTerminalKind.Failed, runtime.FlowSnapshot.TerminalKind);
        Assert.Equal(SessionUiPhase.Recovering, runtime.FlowSnapshot.UiPhase);
        Assert.Equal("Couldn't reach the helper. Check the helper ID and try again.", runtime.FlowSnapshot.TerminalStatusText);
        Assert.Equal(SessionFlowPostTerminalAction.ReturnToWaitingPreserveBootstrap, runtime.FlowSnapshot.PostTerminalAction);
    }

    private static SessionSecurityState CreateApprovedSecurityState(
        PeerAddress helpeeIdentity,
        PeerAddress helperIdentity,
        SessionId sessionId,
        CapabilityGrant capabilities)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        return (SessionSecurityState.Empty with
        {
            SessionId = sessionId,
            HelpeeAddress = helpeeIdentity,
            HelperAddress = helperIdentity,
            InviteValidated = true,
            HandshakeCompleted = true,
            HandshakeState = SessionHandshakeState.Verified,
        }).WithApproval(new SessionGrant(
            helperIdentity,
            capabilities,
            sessionId,
            nowUtc.AddMinutes(5)));
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(predicate(), "Condition was not satisfied before the timeout elapsed.");
    }

    private static void MarkTransportApproved(SessionRuntime runtime)
    {
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "transportState", TransportState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(
            runtime,
            "PublishSessionFlowEvent",
            new SessionFlowEvent(
                SessionFlowEventKind.TransportApproved,
                runtime.Role,
                SessionRuntimeState.Connected,
                TransportState.Connected,
                "transport_approved"));
    }

    private static object? InvokePrivateMethod(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method!.Invoke(target, args);
    }

    private static void SetPrivateField<T>(object target, string fieldName, T value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private sealed class TestSessionSecurityTransport :
        ISignalingTransport,
        IAddressHostSignalingTransport,
        ILocalPeerAddressSignalingTransport,
        ISessionSecuritySignalingTransport
    {
        private SessionSecurityState currentSessionSecurityState = SessionSecurityState.Empty;

        public TestSessionSecurityTransport(string localPeerAddress)
        {
            LocalPeerAddress = localPeerAddress;
        }

        public string LocalPeerAddress { get; }

        public event EventHandler<IncomingJoinRequestEventArgs>? IncomingJoinRequest;
        public event EventHandler<TransportSessionKeyReadyEventArgs>? SessionKeyReady;
        public event EventHandler<TransportChatMessageEventArgs>? ChatMessageReceived;
        public event EventHandler? Approved;
        public event EventHandler? Rejected;
        public event EventHandler? Disconnected;
        public event EventHandler<TransportSessionSecurityStateChangedEventArgs>? SessionSecurityStateChanged;

        public SessionSecurityState CurrentSessionSecurityState => currentSessionSecurityState;

        public void Dispose()
        {
        }

        public Task HostByAddressAsync(CancellationToken ct) => Task.CompletedTask;

        public Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => Task.CompletedTask;

        public void SetSessionSecurityStateForTests(SessionSecurityState nextState)
        {
            currentSessionSecurityState = nextState;
            SessionSecurityStateChanged?.Invoke(this, new TransportSessionSecurityStateChangedEventArgs(nextState));
        }
    }
}
