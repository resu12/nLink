using NLink.App.Services;
using NLink.App.ViewModels;
using NLink.Core;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.DevLocal;
using Xunit;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Gui")]
public sealed class HelperPageViewModelLifecycleTests : CoreSmokeTestsBase
{
    [Trait("Category", "Smoke")]
    [Fact]
    public void HelperPageViewModel_UsesHelperRemoteViewerRole()
    {
        using var runtime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            CreateDevLocalTestConfig(),
            runtime);

        Assert.Equal("helper_remote", helper.ScreenShareViewer.ViewerRoleForDiagnostics);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelperPageViewModel_EndSession_ReturnsToWaitingScreenImmediately()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            transportConfig,
            helperRuntime);

        SetPrivateField(helper, "connectionState", "Connected");
        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helper, "wasConnected", true);
        SetPrivateField(helper, "canEndSession", true);
        SetPrivateField(helper, "isChatInputEnabled", true);

        helper.EndSessionCommand.Execute(null);

        Assert.False(helper.IsConnectedView);
        Assert.True(helper.ShowMainControls);
        Assert.False(helper.ShowConnectedPanel);

        Assert.False(helper.IsConnectedView);
        Assert.True(helper.ShowMainControls);
        Assert.False(helper.IsChatInputEnabled);
        Assert.False(helper.CanEndSession);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelperPageViewModel_PreConnectFailure_InBootstrapMode_ReturnsToWaitingScreen()
    {
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport());
        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            CreateNknTestConfig(),
            runtime,
            connectFailureCooldown: TimeSpan.Zero);

        SetPrivateField(
            runtime,
            "currentFlowSnapshot",
            runtime.FlowSnapshot with
            {
                Phase = SessionFlowPhase.ListenerWaiting,
                UiPhase = SessionUiPhase.Waiting,
                Role = SessionRuntimeRole.Helper,
                RuntimeState = SessionRuntimeState.Waiting,
                StatusText = "Waiting for help requests…",
                DisplayStatusText = "Waiting for help requests…",
                DisplayConnectionState = "Waiting"
            });
        InvokePrivateMethod(helper, "SyncFromRuntime");

        Assert.Equal("Waiting for help requests…", helper.StatusText);
        Assert.False(helper.IsConnectedView);
        Assert.True(string.IsNullOrWhiteSpace(helper.FailureTitle));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelperPageViewModel_LocalApprovalTimeout_FromIncomingHelpRequest_ReturnsToWaitingScreen()
    {
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport());
        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            CreateNknTestConfig(),
            runtime,
            connectFailureCooldown: TimeSpan.Zero);

        SetPrivateField(runtime, "helperConnectOrigin", HelperConnectOrigin.IncomingHelpRequest);
        var timeoutTask = Assert.IsAssignableFrom<Task>(InvokePrivateMethod(helper, "HandlePendingApprovalTimeoutAsync"));
        await timeoutTask;
        await WaitUntilAsync(
            () => runtime.Role == SessionRuntimeRole.Helper &&
                  runtime.State == SessionRuntimeState.Waiting,
            TimeSpan.FromSeconds(2));

        Assert.False(helper.ShowFailurePanel);
        Assert.True(helper.ShowMainControls);
        Assert.False(helper.ShowRetryAction);
        Assert.False(helper.IsConnectedView);
        Assert.Equal("Waiting", helper.ConnectionState);
        Assert.NotEqual("Connection failed", helper.HeaderStatusText);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelperPageViewModel_ReturnToListenerWaiting_FromFlowAction_KeepsWaitingShell()
    {
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport());
        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            CreateNknTestConfig(),
            runtime,
            connectFailureCooldown: TimeSpan.Zero);

        InvokePrivateMethod(helper, "ReturnToListenerWaiting", false);

        Assert.Equal("Waiting", helper.ConnectionState);
        Assert.True(helper.ShowMainControls);
        Assert.False(helper.ShowRetryAction);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelperPageViewModel_OnRejected_DoesNotRepaintFailedShell_AfterReturnToListenerWaiting()
    {
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport());
        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            CreateNknTestConfig(),
            runtime,
            connectFailureCooldown: TimeSpan.Zero);

        SetPrivateField(runtime, "state", SessionRuntimeState.Failed);
        SetPrivateField(runtime, "statusText", UserErrorMapper.HelperApprovalTimeout());
        SetPrivateField(
            runtime,
            "lastTransportFailure",
            TransportFailure.Create(TransportFailureCategory.HandshakeTimeout, "Timed out", isTransient: true));
        SetPrivateField(
            runtime,
            "currentFlowSnapshot",
            runtime.FlowSnapshot with
            {
                Phase = SessionFlowPhase.Failed,
                UiPhase = SessionUiPhase.Failed,
                Role = SessionRuntimeRole.Helper,
                RuntimeState = SessionRuntimeState.Failed,
                LastEndOrigin = SessionFlowEndOrigin.Failed,
                TerminalKind = SessionTerminalKind.Failed,
                TerminalStatusText = UserErrorMapper.HelperApprovalTimeout(),
                FailureTitle = "No response yet",
                FailureMessage = "The other person did not respond in time.",
                FailureActionText = "Retry",
                ShouldClearConversationUi = true,
                ShouldSuppressConnectedControls = true,
                DisplayStatusText = UserErrorMapper.HelperApprovalTimeout(),
                DisplayConnectionState = "Failed",
                PostTerminalAction = SessionFlowPostTerminalAction.ReturnToListenerWaiting,
            });

        InvokePrivateMethod(helper, "SyncFromRuntime");
        InvokePrivateMethod(helper, "OnRejected", null, EventArgs.Empty);

        Assert.False(helper.ShowFailurePanel);
        Assert.False(helper.ShowRetryAction);
        Assert.True(helper.ShowMainControls);
        Assert.Equal("Waiting", helper.ConnectionState);
        Assert.NotEqual("Connection failed", helper.HeaderStatusText);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelperPageViewModel_IdleSnapshot_DoesNotAutoStartListening()
    {
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport());
        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            CreateNknTestConfig(),
            runtime,
            connectFailureCooldown: TimeSpan.Zero);

        await runtime.ResetAsync();
        SetPrivateField(runtime, "role", SessionRuntimeRole.None);
        SetPrivateField(runtime, "state", SessionRuntimeState.Idle);
        SetPrivateField(runtime, "transportState", TransportState.Idle);
        SetPrivateField(
            runtime,
            "currentFlowSnapshot",
            runtime.FlowSnapshot with
            {
                Phase = SessionFlowPhase.NoSession,
                UiPhase = SessionUiPhase.Waiting,
                Role = SessionRuntimeRole.None,
                RuntimeState = SessionRuntimeState.Idle,
                DisplayStatusText = "Waiting for help requests…",
                DisplayConnectionState = "Waiting",
                ShowRetryAction = false,
            });

        InvokePrivateMethod(helper, "SyncFromRuntime");
        await Task.Delay(250);

        Assert.Equal(SessionRuntimeRole.None, runtime.Role);
        Assert.Equal(SessionRuntimeState.Idle, runtime.State);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelperPageViewModel_IncomingHelpRequest_ClearsLocalEndGuardBeforeNextSession()
    {
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport());
        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            CreateNknTestConfig(),
            runtime,
            connectFailureCooldown: TimeSpan.Zero);

        SetPrivateField(helper, "localEndCommandInFlight", true);

        InvokePrivateMethod(helper, "OnIncomingHelpRequestAvailable", null, EventArgs.Empty);

        Assert.False(Assert.IsType<bool>(GetPrivateField(helper, "localEndCommandInFlight")));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelperPageViewModel_IncomingHelpRequest_EnablesAcceptEvenAfterConnectingState()
    {
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport());
        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            CreateNknTestConfig(),
            runtime,
            connectFailureCooldown: TimeSpan.Zero);
        var request = new HelpRequestMessage(
            "request-accept-enabled",
            new PeerAddress("nlink-test-helpee.abc"),
            new PeerAddress("nlink-test-helper.def"),
            "invite-token");
        var pendingRequestType = typeof(SessionRuntime).GetNestedType("PendingIncomingHelpRequest", System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(pendingRequestType);
        var pendingRequest = Activator.CreateInstance(pendingRequestType!, request);
        Assert.NotNull(pendingRequest);
        SetPrivateField(runtime, "pendingIncomingHelpRequest", pendingRequest);
        SetPrivateField(helper, "isConnecting", true);

        InvokePrivateMethod(helper, "OnIncomingHelpRequestAvailable", null, EventArgs.Empty);

        Assert.False(helper.IsConnecting);
        Assert.True(helper.HasPendingHelpRequest);
        Assert.True(helper.AcceptHelpRequestCommand.CanExecute(null));
        Assert.True(helper.RejectHelpRequestCommand.CanExecute(null));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_AcceptIncomingHelpRequest_WhenDecisionSendFails_ReturnsToWaiting()
    {
        var decisionAttempted = false;
        var scriptedTransport = new ScriptedSignalingTransport(
            onSendHelpRequestDecisionAsync: (_, _) =>
            {
                decisionAttempted = true;
                throw new TimeoutException("Ack was not received.");
            });
        using var runtime = new SessionRuntime(() => scriptedTransport);
        var request = new HelpRequestMessage(
            "request-stale-helpee",
            new PeerAddress("nlink-test-helpee.closed"),
            new PeerAddress("nlink-test-helper.listener"),
            "invite-token");
        var pendingRequestType = typeof(SessionRuntime).GetNestedType("PendingIncomingHelpRequest", System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(pendingRequestType);
        var pendingRequest = Activator.CreateInstance(pendingRequestType!, request);
        Assert.NotNull(pendingRequest);
        SetPrivateField(runtime, "transport", scriptedTransport);
        SetPrivateField(runtime, "role", SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "helperConnectOrigin", HelperConnectOrigin.Listener);
        SetPrivateField(runtime, "state", SessionRuntimeState.Waiting);
        SetPrivateField(runtime, "statusText", "Incoming help request.");
        SetPrivateField(runtime, "pendingIncomingHelpRequest", pendingRequest);

        await runtime.AcceptIncomingHelpRequestAsync(CancellationToken.None);

        Assert.True(decisionAttempted);
        Assert.False(runtime.HasPendingHelpRequest);
        Assert.Equal(SessionRuntimeState.Waiting, runtime.State);
        Assert.Equal("Waiting for help requests…", runtime.StatusText);
        Assert.True(runtime.IsTransientStatusVisible);
        Assert.Equal("The help request is no longer available.", runtime.TransientStatusText);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelperPageViewModel_IncomingHelpRequest_ExpiresAndReturnsToWaiting()
    {
        HelpRequestDecisionMessage? timeoutDecision = null;
        var scriptedTransport = new ScriptedSignalingTransport(
            onSendHelpRequestDecisionAsync: (decision, _) =>
            {
                timeoutDecision = decision;
                return Task.CompletedTask;
            });
        using var runtime = new SessionRuntime(() => scriptedTransport);
        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            CreateNknTestConfig(),
            runtime,
            approvalTimeout: TimeSpan.FromMilliseconds(80),
            connectFailureCooldown: TimeSpan.Zero);
        var request = new HelpRequestMessage(
            "request-timeout",
            new PeerAddress("nlink-test-helpee.timeout"),
            new PeerAddress("nlink-test-helper.listener"),
            "invite-token");
        var pendingRequestType = typeof(SessionRuntime).GetNestedType("PendingIncomingHelpRequest", System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(pendingRequestType);
        var pendingRequest = Activator.CreateInstance(pendingRequestType!, request);
        Assert.NotNull(pendingRequest);
        SetPrivateField(runtime, "transport", scriptedTransport);
        SetPrivateField(runtime, "role", SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "state", SessionRuntimeState.Waiting);
        SetPrivateField(runtime, "pendingIncomingHelpRequest", pendingRequest);

        InvokePrivateMethod(helper, "ApplyIncomingHelpRequestAvailable");

        Assert.True(helper.HasPendingHelpRequest);
        Assert.True(helper.ShowIncomingHelpRequestTimeout);
        Assert.StartsWith("Request expires in ", helper.IncomingHelpRequestTimeoutText, StringComparison.Ordinal);

        await WaitUntilAsync(
            () => !runtime.HasPendingHelpRequest &&
                  timeoutDecision is not null,
            TimeSpan.FromSeconds(3));

        Assert.NotNull(timeoutDecision);
        Assert.False(timeoutDecision!.Accepted);
        Assert.Equal("request_timeout", timeoutDecision.Reason);
        Assert.Equal(SessionRuntimeState.Waiting, runtime.State);
        Assert.Equal("Waiting for help requests…", runtime.StatusText);
        Assert.False(helper.HasPendingHelpRequest);
        Assert.False(helper.ShowIncomingHelpRequestTimeout);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_DisconnectWithPendingOutboundHelpRequest_NotifiesHelperCancellation()
    {
        HelpRequestMessage? canceledRequest = null;
        string? cancelReason = null;
        var scriptedTransport = new ScriptedSignalingTransport(
            onSendHelpRequestCancellationAsync: (request, reason, _) =>
            {
                canceledRequest = request;
                cancelReason = reason;
                return Task.CompletedTask;
            });
        using var runtime = new SessionRuntime(() => scriptedTransport);
        var request = new HelpRequestMessage(
            "request-close-cancel",
            new PeerAddress("nlink-test-helpee.close"),
            new PeerAddress("nlink-test-helper.listener"),
            "invite-token");
        var pendingRequestType = typeof(SessionRuntime).GetNestedType("PendingOutboundHelpRequest", System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(pendingRequestType);
        var pendingRequest = Activator.CreateInstance(pendingRequestType!, request);
        Assert.NotNull(pendingRequest);
        SetPrivateField(runtime, "transport", scriptedTransport);
        SetPrivateField(runtime, "role", SessionRuntimeRole.Helpee);
        SetPrivateField(runtime, "state", SessionRuntimeState.Waiting);
        SetPrivateField(runtime, "transportState", TransportState.BridgeReady);
        SetPrivateField(runtime, "pendingOutboundHelpRequest", pendingRequest);

        await runtime.DisconnectAsync();

        Assert.NotNull(canceledRequest);
        Assert.Equal("request-close-cancel", canceledRequest!.RequestId);
        Assert.Equal("helpee_closed", cancelReason);
        Assert.False(runtime.HasPendingOutboundHelpRequest);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_IncomingHelpRequestCancellation_ClearsHelperAcceptPrompt()
    {
        var scriptedTransport = new ScriptedSignalingTransport();
        using var runtime = new SessionRuntime(() => scriptedTransport);
        var request = new HelpRequestMessage(
            "request-helper-clear",
            new PeerAddress("nlink-test-helpee.close"),
            new PeerAddress("nlink-test-helper.listener"),
            "invite-token");
        var pendingRequestType = typeof(SessionRuntime).GetNestedType("PendingIncomingHelpRequest", System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(pendingRequestType);
        var pendingRequest = Activator.CreateInstance(pendingRequestType!, request);
        Assert.NotNull(pendingRequest);
        SetPrivateField(runtime, "transport", scriptedTransport);
        SetPrivateField(runtime, "role", SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "state", SessionRuntimeState.Waiting);
        SetPrivateField(runtime, "pendingIncomingHelpRequest", pendingRequest);

        InvokePrivateMethod(
            runtime,
            "OnHelpRequestDecisionReceived",
            scriptedTransport,
            new HelpRequestDecisionEventArgs(
                new HelpRequestDecisionMessage(
                    request.RequestId,
                    request.HelpeeAddress,
                    request.HelperAddress,
                    Accepted: false,
                    Reason: "helpee_closed")));

        Assert.False(runtime.HasPendingHelpRequest);
        Assert.Equal(SessionRuntimeState.Waiting, runtime.State);
        Assert.Equal("Waiting for help requests…", runtime.StatusText);
        Assert.True(runtime.IsTransientStatusVisible);
        Assert.Equal("The help request is no longer available.", runtime.TransientStatusText);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_DisconnectWithPendingIncomingHelpRequest_NotifiesHelpeeCancellation()
    {
        HelpRequestDecisionMessage? cancellationDecision = null;
        var scriptedTransport = new ScriptedSignalingTransport(
            onSendHelpRequestDecisionAsync: (decision, _) =>
            {
                cancellationDecision = decision;
                return Task.CompletedTask;
            });
        using var runtime = new SessionRuntime(() => scriptedTransport);
        var request = new HelpRequestMessage(
            "request-helper-close-cancel",
            new PeerAddress("nlink-test-helpee.waiting"),
            new PeerAddress("nlink-test-helper.close"),
            "invite-token");
        var pendingRequestType = typeof(SessionRuntime).GetNestedType("PendingIncomingHelpRequest", System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(pendingRequestType);
        var pendingRequest = Activator.CreateInstance(pendingRequestType!, request);
        Assert.NotNull(pendingRequest);
        SetPrivateField(runtime, "transport", scriptedTransport);
        SetPrivateField(runtime, "role", SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "state", SessionRuntimeState.Waiting);
        SetPrivateField(runtime, "transportState", TransportState.BridgeReady);
        SetPrivateField(runtime, "pendingIncomingHelpRequest", pendingRequest);

        await runtime.DisconnectAsync();

        Assert.NotNull(cancellationDecision);
        Assert.Equal("request-helper-close-cancel", cancellationDecision!.RequestId);
        Assert.False(cancellationDecision.Accepted);
        Assert.Equal("helper_closed", cancellationDecision.Reason);
        Assert.False(runtime.HasPendingHelpRequest);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelperPageViewModel_OnDisconnected_ShowsPeerEndedStatusImmediately()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            transportConfig,
            helperRuntime);

        helper.ChatMessages.Add(new ChatLineViewModel { Text = "old", IsLocal = true });

        SetPrivateField(helper, "connectionState", "Connected");
        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helperRuntime, "state", SessionRuntimeState.Disconnected);
        SetPrivateField(helperRuntime, "statusText", "The other person ended the session.");
        SetPrivateField(helperRuntime, "lastTransportFailure", null);
        SetPrivateField(
            helperRuntime,
            "currentFlowSnapshot",
            helperRuntime.FlowSnapshot with
            {
                Phase = SessionFlowPhase.Ended,
                UiPhase = SessionUiPhase.Waiting,
                Role = SessionRuntimeRole.Helper,
                RuntimeState = SessionRuntimeState.Disconnected,
                LastEndOrigin = SessionFlowEndOrigin.Remote,
                ShouldSuppressConnectedControls = true,
                TerminalKind = SessionTerminalKind.PeerEnded,
                TerminalStatusText = "The other person ended the session.",
                FailureTitle = string.Empty,
                FailureMessage = string.Empty,
                FailureActionText = string.Empty,
                ShouldShowPeerEndedNotice = true,
                ShouldClearConversationUi = true,
                StatusText = "The other person ended the session."
            });

        InvokePrivateMethod(helper, "SyncFromRuntime");

        Assert.Equal("The other person ended the session.", helper.HeaderStatusText);
        Assert.True(helper.ShowTransientBanner);
        Assert.Equal("The other person ended the session.", helper.TransientBannerText);
        Assert.False(helper.ShowRemoteScreenShareFrame);
        Assert.False(helper.ShowStopControlAction);
        Assert.False(helper.ShowRemoteControlActiveStatus);
        Assert.False(helper.IsChatInputEnabled);
        Assert.False(helper.ShowFailurePanel);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelperPageViewModel_RejectReturnAction_GoesBackToMainScreen()
    {
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            CreateDevLocalTestConfig(),
            helperRuntime);

        SetPrivateField(
            helperRuntime,
            "currentFlowSnapshot",
            helperRuntime.FlowSnapshot with
            {
                Phase = SessionFlowPhase.Failed,
                UiPhase = SessionUiPhase.Failed,
                Role = SessionRuntimeRole.Helper,
                RuntimeState = SessionRuntimeState.Rejected,
                LastEndOrigin = SessionFlowEndOrigin.Rejected,
                TerminalKind = SessionTerminalKind.Rejected,
                TerminalStatusText = UserErrorMapper.HelperRejected(),
                FailureTitle = "Request rejected",
                FailureMessage = "The other side declined the session.",
                FailureActionText = "Start new session",
                ShouldClearConversationUi = true,
                ShouldSuppressConnectedControls = true,
                DisplayStatusText = UserErrorMapper.HelperRejected(),
                DisplayConnectionState = "Rejected",
                PostTerminalAction = SessionFlowPostTerminalAction.ReturnToListenerWaiting,
            });

        InvokePrivateMethod(helper, "SyncFromRuntime");

        Assert.False(helper.ShowFailurePanel);
        Assert.True(helper.ShowMainControls);
        Assert.False(helper.IsConnectedView);
        Assert.Equal("Waiting", helper.ConnectionState);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelperPageViewModel_NewConnectedSession_ClearsPreviousConversationUi()
    {
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            CreateDevLocalTestConfig(),
            helperRuntime);

        helper.ChatMessages.Add(new ChatLineViewModel { Text = "old", IsLocal = true });
        SetPrivateField(helper, "lastConversationSessionId", "session-old");
        SetPrivateField(
            helperRuntime,
            "currentFlowSnapshot",
            helperRuntime.FlowSnapshot with
            {
                Phase = SessionFlowPhase.ActiveSession,
                UiPhase = SessionUiPhase.Connected,
                Role = SessionRuntimeRole.Helper,
                RuntimeState = SessionRuntimeState.Connected,
                SessionId = "session-new",
                DisplayStatusText = "Connected",
                DisplayConnectionState = "Connected",
                ApprovalActive = true,
            });

        InvokePrivateMethod(helper, "SyncFromRuntime");

        Assert.Empty(helper.ChatMessages);
        Assert.Equal("session-new", Assert.IsType<string>(GetPrivateField(helper, "lastConversationSessionId")));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelperPageViewModel_PendingHelpRequest_SuppressesConnectingTransientBanner()
    {
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport());
        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            CreateNknTestConfig(),
            runtime,
            connectFailureCooldown: TimeSpan.Zero);

        SetPrivateField(runtime, "transientStatusVisible", true);
        SetPrivateField(runtime, "transientStatusText", "Connecting... (attempt 1)");
        SetPrivateField(runtime, "transientStatusCanCancel", true);
        var request = new HelpRequestMessage(
            "request-1",
            new PeerAddress("nlink-test-helpee.abc"),
            new PeerAddress("nlink-test-helper.def"),
            "invite-token");
        var pendingRequestType = typeof(SessionRuntime).GetNestedType("PendingIncomingHelpRequest", System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(pendingRequestType);
        var pendingRequest = Activator.CreateInstance(pendingRequestType!, request);
        Assert.NotNull(pendingRequest);
        SetPrivateField(
            runtime,
            "pendingIncomingHelpRequest",
            pendingRequest);
        SetPrivateField(helper, "connectionState", "Waiting");
        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Waiting);

        InvokePrivateMethod(helper, "SyncTransientStatusFromRuntime");

        Assert.True(helper.HasPendingHelpRequest);
        Assert.False(helper.ShowTransientBanner);
        Assert.True(string.IsNullOrWhiteSpace(helper.TransientBannerText));
        Assert.False(helper.CanCancelTransient);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelperPageViewModel_RuntimeBootstrapSnapshot_PopulatesVerificationCode_AndQr()
    {
        var transportConfig = CreateNknTestConfig();
        var localAddress = new PeerAddress("nlink-helper.bootstrap.actual.1234567890");
        using var runtime = new SessionRuntime(() => new FixedLocalPeerAddressTransport(localAddress.Value));
        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            transportConfig,
            runtime);

        SetPrivateField(runtime, "role", SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "state", SessionRuntimeState.Waiting);
        SetPrivateField(runtime, "transport", new FixedLocalPeerAddressTransport(localAddress.Value));
        SetPrivateField(
            runtime,
            "helperListenerBootstrapSnapshot",
            new HelperListenerBootstrapSnapshot(
                localAddress,
                RunId: "test-run",
                ListenerGeneration: 1,
                PublishedUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                HostReady: true));

        InvokePrivateMethod(helper, "SyncFromRuntime");

        Assert.True(helper.HasReadyHelperIdentityBootstrapText);
        Assert.True(helper.HasHelperIdentityBootstrapVerificationCode);
        Assert.StartsWith("nlinkh1.", helper.HelperIdentityBootstrapText, StringComparison.Ordinal);
        Assert.True(HelperBootstrapQrPayload.TryParse(helper.HelperIdentityBootstrapText, out var parsedBootstrap));
        Assert.NotNull(parsedBootstrap);
        Assert.Equal(localAddress.Value, parsedBootstrap!.HelperAddress.Value);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelperPageViewModel_SessionVerificationCode_ShowsOnlyBeforeApproval()
    {
        using var runtime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            CreateDevLocalTestConfig(),
            runtime);
        var sessionId = new SessionId("session-verification-helper-vm");
        var helpeeAddress = new PeerAddress("verification.helpee.helper-vm");
        var helperAddress = new PeerAddress("verification.helper.helper-vm");
        var verificationCode = CreateTestSessionVerificationCode();
        var verifiedState = CreateVerifiedSecurityState(helpeeAddress, helperAddress, sessionId)
            .WithVerificationCode(verificationCode);

        SetPrivateField(runtime, "role", SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connecting);
        SetPrivateField(runtime, "transportState", TransportState.Handshake);
        SetPrivateField(runtime, "sessionSecurityState", verifiedState);
        SetPrivateField(
            runtime,
            "currentFlowSnapshot",
            runtime.FlowSnapshot with
            {
                Phase = SessionFlowPhase.Connecting,
                UiPhase = SessionUiPhase.Connecting,
                Role = SessionRuntimeRole.Helper,
                RuntimeState = SessionRuntimeState.Connecting,
                TransportState = TransportState.Handshake,
                StatusText = "Waiting for approval…",
                DisplayStatusText = "Waiting for approval…",
                DisplayConnectionState = "Connecting",
                SessionId = sessionId.Value,
                HelperIdentity = helperAddress.Value,
                VerificationCode = verificationCode,
            });

        InvokePrivateMethod(helper, "SyncFromRuntime");

        Assert.True(helper.HasSessionVerificationCode);
        Assert.True(helper.ShowSessionVerificationCode);
        Assert.Equal(verificationCode.EmojiSequence, helper.SessionVerificationEmojiSequence);
        Assert.Equal(verificationCode.FallbackCode, helper.SessionVerificationFallbackCode);

        var grant = new SessionGrant(
            helperAddress,
            CapabilityGrant.Chat,
            sessionId,
            DateTimeOffset.UtcNow.Add(SessionSecurityDefaults.GrantLifetime));
        var approvedState = verifiedState.WithApproval(grant);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "transportState", TransportState.Connected);
        SetPrivateField(runtime, "sessionSecurityState", approvedState);
        SetPrivateField(
            runtime,
            "currentFlowSnapshot",
            runtime.FlowSnapshot with
            {
                Phase = SessionFlowPhase.ActiveSession,
                UiPhase = SessionUiPhase.Connected,
                RuntimeState = SessionRuntimeState.Connected,
                TransportState = TransportState.Connected,
                ApprovalActive = true,
                ApprovedCapabilities = CapabilityGrant.Chat,
                DisplayStatusText = "Connected",
                DisplayConnectionState = "Connected",
                VerificationCode = verificationCode,
            });

        InvokePrivateMethod(helper, "SyncFromRuntime");

        Assert.True(helper.HasSessionVerificationCode);
        Assert.False(helper.ShowSessionVerificationCode);
    }

    [Trait("Category", "Smoke")]
    [Theory]
    [InlineData(SessionFlowPhase.Ended, SessionUiPhase.Ended, SessionRuntimeState.Disconnected, TransportState.Failed)]
    [InlineData(SessionFlowPhase.Failed, SessionUiPhase.Failed, SessionRuntimeState.Rejected, TransportState.Failed)]
    public void HelperPageViewModel_SessionVerificationCode_HidesAfterConnectingStateEnds(
        SessionFlowPhase phase,
        SessionUiPhase uiPhase,
        SessionRuntimeState runtimeState,
        TransportState transportState)
    {
        using var runtime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            CreateDevLocalTestConfig(),
            runtime);
        var sessionId = new SessionId("session-verification-helper-clears");
        var helpeeAddress = new PeerAddress("verification.helpee.helper-clears");
        var helperAddress = new PeerAddress("verification.helper.helper-clears");
        var verificationCode = CreateTestSessionVerificationCode();

        SetHelperConnectingVerificationState(runtime, sessionId, helpeeAddress, helperAddress, verificationCode);
        InvokePrivateMethod(helper, "SyncFromRuntime");
        Assert.True(helper.ShowSessionVerificationCode);

        SetPrivateField(runtime, "state", runtimeState);
        SetPrivateField(runtime, "transportState", transportState);
        SetPrivateField(
            runtime,
            "currentFlowSnapshot",
            runtime.FlowSnapshot with
            {
                Phase = phase,
                UiPhase = uiPhase,
                RuntimeState = runtimeState,
                TransportState = transportState,
                DisplayStatusText = "Connection ended",
                DisplayConnectionState = "Disconnected",
                VerificationCode = verificationCode,
            });

        InvokePrivateMethod(helper, "SyncFromRuntime");

        Assert.True(helper.HasSessionVerificationCode);
        Assert.False(helper.ShowSessionVerificationCode);
    }

    private static void SetHelperConnectingVerificationState(
        SessionRuntime runtime,
        SessionId sessionId,
        PeerAddress helpeeAddress,
        PeerAddress helperAddress,
        SessionVerificationCode verificationCode)
    {
        var verifiedState = CreateVerifiedSecurityState(helpeeAddress, helperAddress, sessionId)
            .WithVerificationCode(verificationCode);

        SetPrivateField(runtime, "role", SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connecting);
        SetPrivateField(runtime, "transportState", TransportState.Handshake);
        SetPrivateField(runtime, "sessionSecurityState", verifiedState);
        SetPrivateField(
            runtime,
            "currentFlowSnapshot",
            runtime.FlowSnapshot with
            {
                Phase = SessionFlowPhase.Connecting,
                UiPhase = SessionUiPhase.Connecting,
                Role = SessionRuntimeRole.Helper,
                RuntimeState = SessionRuntimeState.Connecting,
                TransportState = TransportState.Handshake,
                StatusText = "Waiting for approval…",
                DisplayStatusText = "Waiting for approval…",
                DisplayConnectionState = "Connecting",
                SessionId = sessionId.Value,
                HelperIdentity = helperAddress.Value,
                VerificationCode = verificationCode,
            });
    }

    private static SessionVerificationCode CreateTestSessionVerificationCode()
    {
        return new SessionVerificationCode(
            "sun moon star cloud leaf fire key",
            "FACE-B00C-1234",
            SessionVerificationCodeDerivation.SourceHandshakeTranscriptV1);
    }
}
