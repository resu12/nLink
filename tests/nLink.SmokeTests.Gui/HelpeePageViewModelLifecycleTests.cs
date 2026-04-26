using NLink.App.Services;
using NLink.App.Services.ScreenCapture;
using NLink.App.ViewModels;
using NLink.Core;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.DevLocal;
using NLink.Infra.Nkn;
using Xunit;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Gui")]
public sealed class HelpeePageViewModelLifecycleTests : CoreSmokeTestsBase
{
    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelpeePageViewModel_OnRemoteSessionEnded_ShowsPeerEndedStatusImmediately()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helpeeRuntime = new SessionRuntime(() => new DevLocalTransport());
        var uiStateStore = new SessionUiStateStore();
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, transportConfig, helpeeRuntime, uiStateStore: uiStateStore);

        helpee.ChatMessages.Add(new ChatLineViewModel { Text = "old", IsLocal = true });

        SetPrivateField(helpee, "connectionState", "Connected");
        SetPrivateField(helpee, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateProperty(helpee, "ScreenSharePreviewStatus", new ScreenShareStatus(ScreenShareState.Active, null, DateTimeOffset.UtcNow));
        SetPrivateProperty(helpee, "ScreenSharePreviewFrame", CreateTestBitmap(2, 1));
        SetPrivateProperty(helpee, "IsScreenSharingPreviewActive", true);
        SetPrivateField(helpeeRuntime, "state", SessionRuntimeState.Disconnected);
        SetPrivateField(helpeeRuntime, "statusText", "The other side ended the session.");
        SetPrivateField(helpeeRuntime, "lastTransportFailure", null);
        SetPrivateField(
            helpeeRuntime,
            "currentFlowSnapshot",
            helpeeRuntime.FlowSnapshot with
            {
                Phase = SessionFlowPhase.Ended,
                UiPhase = SessionUiPhase.Waiting,
                Role = SessionRuntimeRole.Helpee,
                RuntimeState = SessionRuntimeState.Disconnected,
                LastEndOrigin = SessionFlowEndOrigin.Remote,
                ShouldSuppressConnectedControls = true,
                TerminalKind = SessionTerminalKind.PeerEnded,
                TerminalStatusText = "The other side ended the session.",
                FailureTitle = string.Empty,
                FailureMessage = string.Empty,
                FailureActionText = string.Empty,
                ShouldShowPeerEndedNotice = true,
                ShouldClearConversationUi = true,
                StatusText = "The other side ended the session."
            });

        InvokePrivateMethod(helpee, "OnRemoteSessionEnded", helpeeRuntime, EventArgs.Empty);

        await WaitUntilAsync(
            () => helpee.EffectivePhase == SessionUiPhase.Waiting &&
                  string.Equals(helpee.ConnectionState, "Waiting", StringComparison.Ordinal) &&
                  helpee.ShowTransientBanner &&
                  string.Equals(helpee.TransientBannerText, "The other side ended the session.", StringComparison.Ordinal) &&
                  !helpee.IsScreenSharingPreviewActive,
            TimeSpan.FromSeconds(5));

        Assert.Equal("The other side ended the session.", helpee.HeaderStatusText);
        Assert.True(helpee.ShowTransientBanner);
        Assert.Equal("The other side ended the session.", helpee.TransientBannerText);
        Assert.False(helpee.IsChatInputEnabled);
        Assert.False(helpee.ShowFailurePanel);
        Assert.Null(helpee.ScreenSharePreviewFrame);
        Assert.Equal(ScreenShareState.Off, helpee.ScreenSharePreviewStatus.State);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelpeePageViewModel_EndSession_ReturnsToWaitingScreenImmediately()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helpeeRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helpee = new HelpeePageViewModel(
            cancelAction: static () => { },
            transportConfig,
            helpeeRuntime);

        SetPrivateField(helpee, "connectionState", "Connected");
        SetPrivateField(helpee, "wasConnected", true);
        SetPrivateField(helpee, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helpee, "isChatInputEnabled", true);
        SetPrivateField(helpeeRuntime, "state", SessionRuntimeState.Connected);
        SetPrivateField(helpeeRuntime, "transportState", TransportState.Connected);
        SetPrivateField(
            helpeeRuntime,
            "currentFlowSnapshot",
            helpeeRuntime.FlowSnapshot with
            {
                Phase = SessionFlowPhase.ActiveSession,
                UiPhase = SessionUiPhase.Connected,
                Role = SessionRuntimeRole.Helpee,
                RuntimeState = SessionRuntimeState.Connected,
                TransportState = TransportState.Connected,
                ApprovalActive = true,
                DisplayStatusText = "Connected",
                DisplayConnectionState = "Connected",
            });
        InvokePrivateMethod(helpee, "UpdateUiFromSnapshot");
        Assert.True(helpee.CanEndSession);
        Assert.True(helpee.EndSessionCommand.CanExecute(null));

        helpee.EndSessionCommand.Execute(null);

        Assert.False(helpee.IsConnectedView);
        Assert.True(helpee.ShowWaitingPanel);
        Assert.False(helpee.ShowConnectedPanel);

        Assert.Contains(helpee.EffectivePhase, new[] { SessionUiPhase.Waiting, SessionUiPhase.Idle });
        Assert.False(helpee.IsConnectedView);
        Assert.True(helpee.ShowWaitingPanel);
        Assert.False(helpee.IsChatInputEnabled);
        Assert.False(helpee.CanEndSession);

        SetPrivateField(helpee, "localEndCommandInFlight", false);
        SetPrivateField(
            helpeeRuntime,
            "currentFlowSnapshot",
            helpeeRuntime.FlowSnapshot with
            {
                Phase = SessionFlowPhase.ActiveSession,
                UiPhase = SessionUiPhase.Connected,
                RuntimeState = SessionRuntimeState.Connected,
                TransportState = TransportState.Connected,
                ApprovalActive = true,
                ShouldSuppressConnectedControls = false,
                DisplayStatusText = "Connected",
                DisplayConnectionState = "Connected",
            });

        InvokePrivateMethod(helpee, "UpdateUiFromSnapshot");

        Assert.False(helpee.CanEndSession);
        Assert.False(helpee.EndSessionCommand.CanExecute(null));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelpeePageViewModel_RestartWaitingSession_PreservesPeerEndedNotice()
    {
        using var runtime = new SessionRuntime(() => new DevLocalTransport());
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, CreateDevLocalTestConfig(), runtime);

        InvokePrivateMethod(helpee, "RestartWaitingSession", false, true, "The other side ended the session.");

        Assert.True(Assert.IsType<bool>(GetPrivateField(helpee, "showPeerEndedNotice")));
        Assert.Equal("The other side ended the session.", Assert.IsType<string>(GetPrivateField(helpee, "peerEndedNoticeText")));
        Assert.Equal("The other side ended the session.", helpee.HeaderStatusText);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelpeePageViewModel_RestartWaitingSession_BeforeConnected_PreservesHelperIdentityForRetry()
    {
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport());
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, CreateNknTestConfig(), runtime);

        var helperIdentity = new PeerAddress("nlink-helper.retry.identity.keep");
        var helperTarget = new PeerAddress("nlink-helper.retry.target.keep");
        var helperBootstrap = HelperBootstrapQrPayload.Format(
            HelperBootstrapPayload.Create(
                helperTarget,
                helperId: HelperIdentityTokenCodec.Encode(helperIdentity)));

        helpee.SetVerifiedInviteHelperIdentity(
            helperIdentity,
            helperTargetAddress: helperTarget,
            refreshInvite: false,
            normalizedInputOverride: helperBootstrap);

        InvokePrivateMethod(helpee, "RestartWaitingSession", true, false, null);

        Assert.Equal(helperBootstrap, helpee.InviteHelperIdentityInput);
        Assert.True(helpee.HasVerifiedInviteHelperIdentity);
        Assert.Equal("Waiting for helper…", helpee.ConnectionStatus);
        Assert.Equal("Waiting", helpee.ConnectionState);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelpeePageViewModel_PendingOutboundHelpRequest_DisablesRequestHelp()
    {
        var scriptedTransport = new ScriptedSignalingTransport(
            onHostByAddressAsync: _ => Task.CompletedTask,
            localPeerAddress: "helpee.pending.request");

        using var runtime = new SessionRuntime(() => scriptedTransport);
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, CreateNknTestConfig(), runtime);

        const string helperInput = "nhid1-DSP6-JVKB-5MW3-GE9M-71GK-ADHE-60VK-CE36-70WP-ARB2-CMRK-4DB1-60VP-4E9Q-75GK-4D33-6WS3-AE1N-CHJ6-2CV5-6XJ3-2E9H-6XHP-8E1Q-6GSK-GE35-74VK-8DV2-6MWP-8D9M-68W6-ASAN-JYRC-J";
        var helperTarget = new PeerAddress("nlink-helper.target");

        WaitUntilAsync(() => runtime.State == SessionRuntimeState.Waiting && helpee.HasShareInvite, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();

        helpee.InviteHelperIdentityInput = helperInput;
        helpee.SetVerifiedInviteHelperIdentity(
            new PeerAddress(helperInput),
            helperTargetAddress: helperTarget,
            refreshInvite: true,
            normalizedInputOverride: helperInput);
        WaitUntilAsync(() => helpee.RequestHelpCommand.CanExecute(null), TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();

        var requestTask = Assert.IsAssignableFrom<Task>(InvokePrivateMethod(helpee, "RequestHelpAsync"));
        requestTask.GetAwaiter().GetResult();

        Assert.True(runtime.HasPendingOutboundHelpRequest);
        Assert.False(helpee.RequestHelpCommand.CanExecute(null));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelpeePageViewModel_HelperReject_ReturnsToWaitingAndReEnablesRequestHelp()
    {
        var scriptedTransport = new ScriptedSignalingTransport(
            onHostByAddressAsync: _ => Task.CompletedTask,
            localPeerAddress: "helpee.reject.retry");

        using var runtime = new SessionRuntime(() => scriptedTransport);
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, CreateNknTestConfig(), runtime);

        var helperIdentity = new PeerAddress("nlink-helper.reject.identity");
        var helperTarget = new PeerAddress("nlink-helper.reject.target");
        var helperBootstrap = HelperBootstrapQrPayload.Format(
            HelperBootstrapPayload.Create(
                helperTarget,
                helperId: HelperIdentityTokenCodec.Encode(helperIdentity)));

        await WaitUntilAsync(() => runtime.State == SessionRuntimeState.Waiting && helpee.HasShareInvite, TimeSpan.FromSeconds(2));

        helpee.SetVerifiedInviteHelperIdentity(
            helperIdentity,
            helperTargetAddress: helperTarget,
            refreshInvite: true,
            normalizedInputOverride: helperBootstrap);

        await WaitUntilAsync(() => helpee.RequestHelpCommand.CanExecute(null), TimeSpan.FromSeconds(2));

        SetPrivateField(runtime, "state", SessionRuntimeState.Rejected);
        SetPrivateField(runtime, "statusText", "Request was rejected.");
        SetPrivateField(
            runtime,
            "currentFlowSnapshot",
            runtime.FlowSnapshot with
            {
                Phase = SessionFlowPhase.Failed,
                UiPhase = SessionUiPhase.Failed,
                Role = SessionRuntimeRole.Helpee,
                RuntimeState = SessionRuntimeState.Rejected,
                LastEndOrigin = SessionFlowEndOrigin.Rejected,
                TerminalKind = SessionTerminalKind.Rejected,
                TerminalStatusText = "Request was rejected.",
                FailureTitle = "Request rejected",
                FailureMessage = "The helper declined the request.",
                FailureActionText = "Retry",
                ShouldClearConversationUi = true,
                ShouldSuppressConnectedControls = true,
                DisplayStatusText = "Request was rejected.",
                DisplayConnectionState = "Failed",
                ShowRetryAction = true,
                ShowDiagnosticsAction = true,
                PostTerminalAction = SessionFlowPostTerminalAction.ReturnToWaitingPreserveBootstrap,
            });

        InvokePrivateMethod(helpee, "SyncFromRuntime");

        await WaitUntilAsync(
            () => helpee.EffectivePhase == SessionUiPhase.Waiting &&
                  string.Equals(helpee.ConnectionState, "Waiting", StringComparison.Ordinal) &&
                  helpee.HasVerifiedInviteHelperIdentity &&
                  string.Equals(helpee.InviteHelperIdentityInput, helperBootstrap, StringComparison.Ordinal) &&
                  helpee.HasShareInvite &&
                  helpee.RequestHelpCommand.CanExecute(null),
            TimeSpan.FromSeconds(5));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelpeePageViewModel_HelperAccepted_FinalizingSecureConnection_KeepsRequestHelpDisabled()
    {
        var scriptedTransport = new ScriptedSignalingTransport(
            onHostByAddressAsync: _ => Task.CompletedTask,
            localPeerAddress: "helpee.accepted.finalizing");

        using var runtime = new SessionRuntime(() => scriptedTransport);
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, CreateNknTestConfig(), runtime);

        var helperIdentity = new PeerAddress("nlink-helper.accepted.identity");
        var helperTarget = new PeerAddress("nlink-helper.accepted.target");
        var helperBootstrap = HelperBootstrapQrPayload.Format(
            HelperBootstrapPayload.Create(
                helperTarget,
                helperId: HelperIdentityTokenCodec.Encode(helperIdentity)));

        await WaitUntilAsync(() => runtime.State == SessionRuntimeState.Waiting && helpee.HasShareInvite, TimeSpan.FromSeconds(2));

        helpee.SetVerifiedInviteHelperIdentity(
            helperIdentity,
            helperTargetAddress: helperTarget,
            refreshInvite: true,
            normalizedInputOverride: helperBootstrap);

        await WaitUntilAsync(() => helpee.RequestHelpCommand.CanExecute(null), TimeSpan.FromSeconds(2));

        SetPrivateField(
            runtime,
            "<PendingOutboundHelpRequestDecision>k__BackingField",
            new HelpRequestDecisionMessage(
                "hr_accept",
                new PeerAddress("helpee.accepted.finalizing"),
                helperTarget,
                Accepted: true));
        SetPrivateField(runtime, "state", SessionRuntimeState.Waiting);
        SetPrivateField(runtime, "statusText", "Helper accepted. Finalizing secure connection…");
        SetPrivateField(
            runtime,
            "currentFlowSnapshot",
            runtime.FlowSnapshot with
            {
                Phase = SessionFlowPhase.Connecting,
                UiPhase = SessionUiPhase.Waiting,
                Role = SessionRuntimeRole.Helpee,
                RuntimeState = SessionRuntimeState.Waiting,
                DisplayStatusText = "Helper accepted. Finalizing secure connection…",
                DisplayConnectionState = "Waiting",
            });

        InvokePrivateMethod(helpee, "OnHelpRequestDecisionAvailable", runtime, EventArgs.Empty);

        await WaitUntilAsync(
            () => !helpee.RequestHelpCommand.CanExecute(null) &&
                  !helpee.CanRequestHelpAction &&
                  string.Equals(helpee.ConnectionStatus, "Helper accepted. Establishing secure session…", StringComparison.Ordinal),
            TimeSpan.FromSeconds(2));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelpeePageViewModel_ReusingSameVerifiedHelperIdentity_RefreshesInviteForRetry()
    {
        var scriptedTransport = new ScriptedSignalingTransport(
            onHostByAddressAsync: _ => Task.CompletedTask,
            localPeerAddress: "helpee.same-helper.retry");

        using var runtime = new SessionRuntime(() => scriptedTransport);
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, CreateNknTestConfig(), runtime);

        var helperIdentity = new PeerAddress("nlink-helper.same.identity");
        var helperTarget = new PeerAddress("nlink-helper.same.target");
        var helperBootstrap = HelperBootstrapQrPayload.Format(
            HelperBootstrapPayload.Create(
                helperTarget,
                helperId: HelperIdentityTokenCodec.Encode(helperIdentity)));

        await WaitUntilAsync(() => runtime.State == SessionRuntimeState.Waiting && helpee.HasShareInvite, TimeSpan.FromSeconds(2));

        helpee.SetVerifiedInviteHelperIdentity(
            helperIdentity,
            helperTargetAddress: helperTarget,
            refreshInvite: true,
            normalizedInputOverride: helperBootstrap);

        await WaitUntilAsync(() => helpee.HasShareInvite, TimeSpan.FromSeconds(2));
        var initialInvite = helpee.ShareInvite;

        InvokePrivateMethod(helpee, "UpdateShareInviteText", string.Empty);
        InvokePrivateMethod(helpee, "UpdateShareInviteRawTokenText", string.Empty);
        InvokePrivateMethod(helpee, "UpdateShareInviteStatusText", "Preparing invite…");

        helpee.SetVerifiedInviteHelperIdentity(
            helperIdentity,
            helperTargetAddress: helperTarget,
            refreshInvite: true,
            normalizedInputOverride: helperBootstrap);

        await WaitUntilAsync(
            () => helpee.HasShareInvite &&
                  !string.IsNullOrWhiteSpace(helpee.ShareInvite) &&
                  !string.Equals(helpee.ShareInvite, initialInvite, StringComparison.Ordinal),
            TimeSpan.FromSeconds(3));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelpeePageViewModel_UsesSecurityStateHelpeeAddress_WhenAuthoritativeLocalAddressIsSuppressed()
    {
        using var runtime = new SessionRuntime(() => new AuthoritativeAddressSuppressedHelpeeTransport("helpee.securitystate.address"));
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, CreateNknTestConfig(), runtime);

        var helperIdentity = new PeerAddress("nlink-helper.identity.securitystate");
        var helperTarget = new PeerAddress("nlink-helper.target.securitystate");
        var helperBootstrap = HelperBootstrapQrPayload.Format(
            HelperBootstrapPayload.Create(
                helperTarget,
                helperId: HelperIdentityTokenCodec.Encode(helperIdentity)));

        helpee.SetVerifiedInviteHelperIdentity(
            helperIdentity,
            helperTargetAddress: helperTarget,
            refreshInvite: true,
            normalizedInputOverride: helperBootstrap);

        await WaitUntilAsync(
            () => runtime.State == SessionRuntimeState.Waiting &&
                  helpee.HasVerifiedInviteHelperIdentity &&
                  helpee.HasShareInvite &&
                  helpee.RequestHelpCommand.CanExecute(null),
            TimeSpan.FromSeconds(3));

        Assert.Equal("Invite ready", helpee.ShareInviteStatusText);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelpeePageViewModel_NewConnectedSession_ClearsStalePeerEndedHeaderNotice()
    {
        using var runtime = new SessionRuntime(() => new DevLocalTransport());
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, CreateDevLocalTestConfig(), runtime);

        SetPrivateField(helpee, "showPeerEndedNotice", true);
        SetPrivateField(helpee, "peerEndedNoticeText", "The other side ended the session.");
        SetPrivateField(
            runtime,
            "currentFlowSnapshot",
            runtime.FlowSnapshot with
            {
                Phase = SessionFlowPhase.ActiveSession,
                UiPhase = SessionUiPhase.Connected,
                Role = SessionRuntimeRole.Helpee,
                RuntimeState = SessionRuntimeState.Connected,
                DisplayStatusText = "Connected",
                DisplayConnectionState = "Connected",
                ApprovalActive = true,
            });

        InvokePrivateMethod(helpee, "SyncFromRuntime");

        Assert.False(Assert.IsType<bool>(GetPrivateField(helpee, "showPeerEndedNotice")));
        Assert.Equal("Connected", helpee.HeaderStatusText);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelpeePageViewModel_NewWaitingStatus_PreservesPeerEndedHeaderNoticeUntilTimeout()
    {
        using var runtime = new SessionRuntime(() => new DevLocalTransport());
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, CreateDevLocalTestConfig(), runtime);

        SetPrivateField(helpee, "showPeerEndedNotice", true);
        SetPrivateField(helpee, "peerEndedNoticeText", "The other side ended the session.");
        SetPrivateField(
            runtime,
            "currentFlowSnapshot",
            runtime.FlowSnapshot with
            {
                Phase = SessionFlowPhase.HelpeeWaiting,
                UiPhase = SessionUiPhase.Waiting,
                Role = SessionRuntimeRole.Helpee,
                RuntimeState = SessionRuntimeState.Waiting,
                DisplayStatusText = "Waiting for helper…",
                DisplayConnectionState = "Waiting",
            });

        InvokePrivateMethod(helpee, "SyncFromRuntime");

        Assert.True(Assert.IsType<bool>(GetPrivateField(helpee, "showPeerEndedNotice")));
        Assert.Equal("The other side ended the session.", helpee.HeaderStatusText);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelpeePageViewModel_IncomingApproval_ClearsStalePeerEndedHeaderNotice()
    {
        using var runtime = new SessionRuntime(() => new DevLocalTransport());
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, CreateDevLocalTestConfig(), runtime);

        SetPrivateField(helpee, "showPeerEndedNotice", true);
        SetPrivateField(helpee, "peerEndedNoticeText", "The other side ended the session.");
        SetPrivateField(
            runtime,
            "currentFlowSnapshot",
            runtime.FlowSnapshot with
            {
                Phase = SessionFlowPhase.PendingApproval,
                UiPhase = SessionUiPhase.Waiting,
                Role = SessionRuntimeRole.Helpee,
                RuntimeState = SessionRuntimeState.Waiting,
                DisplayStatusText = "Waiting for your approval…",
                DisplayConnectionState = "IncomingRequest",
                ShowIncomingApproval = true,
            });

        InvokePrivateMethod(helpee, "SyncFromRuntime");

        Assert.False(Assert.IsType<bool>(GetPrivateField(helpee, "showPeerEndedNotice")));
        Assert.Equal("Waiting for your approval…", helpee.HeaderStatusText);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelpeePageViewModel_IncomingApproval_WinsOverStalePeerEndedTerminalPresentation()
    {
        using var runtime = new SessionRuntime(() => new DevLocalTransport());
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, CreateDevLocalTestConfig(), runtime);

        SetPrivateField(helpee, "showPeerEndedNotice", true);
        SetPrivateField(helpee, "peerEndedNoticeText", "The other side ended the session.");
        SetPrivateField(
            runtime,
            "currentFlowSnapshot",
            runtime.FlowSnapshot with
            {
                Phase = SessionFlowPhase.PendingApproval,
                UiPhase = SessionUiPhase.Waiting,
                Role = SessionRuntimeRole.Helpee,
                RuntimeState = SessionRuntimeState.Waiting,
                TerminalKind = SessionTerminalKind.PeerEnded,
                TerminalStatusText = "The other side ended the session.",
                ShouldShowPeerEndedNotice = true,
                DisplayStatusText = "Waiting for your approval…",
                DisplayConnectionState = "IncomingRequest",
                ShowIncomingApproval = true,
            });

        InvokePrivateMethod(helpee, "SyncFromRuntime");

        Assert.False(Assert.IsType<bool>(GetPrivateField(helpee, "showPeerEndedNotice")));
        Assert.True(helpee.IsIncomingRequestView);
        Assert.Equal("Waiting for your approval…", helpee.HeaderStatusText);
    }

    private sealed class AuthoritativeAddressSuppressedHelpeeTransport :
        ISignalingTransport,
        IAddressHostSignalingTransport,
        IHostReadySignalingTransport,
        ILocalPeerAddressSignalingTransport,
        ISessionSecuritySignalingTransport,
        IAuthoritativeConnectedAddressSource
    {
        private readonly TaskCompletionSource<bool> hostReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private SessionSecurityState currentSessionSecurityState = SessionSecurityState.Empty;

        public AuthoritativeAddressSuppressedHelpeeTransport(string localPeerAddress)
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
        bool IAuthoritativeConnectedAddressSource.HasAuthoritativeConnectedAddress => false;

        public Task HostByAddressAsync(CancellationToken ct)
        {
            currentSessionSecurityState = SessionSecurityState.CreateHelpeeWaiting(new PeerAddress(LocalPeerAddress));
            SessionSecurityStateChanged?.Invoke(this, new TransportSessionSecurityStateChangedEventArgs(currentSessionSecurityState));
            hostReadyTcs.TrySetResult(true);
            return Task.Delay(Timeout.Infinite, ct);
        }

        public Task WaitUntilHostReadyAsync(CancellationToken ct) => hostReadyTcs.Task.WaitAsync(ct);

        public Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
