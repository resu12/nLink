using NLink.App.Services;
using NLink.App.ViewModels;
using NLink.Core;
using NLink.Core.SessionConnect;
using NLink.Infra.DevLocal;
using Xunit;

namespace NLink.SmokeTests;

public partial class SmokeTests
{
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
}
