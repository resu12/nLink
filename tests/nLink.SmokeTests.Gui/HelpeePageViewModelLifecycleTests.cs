using System.Reflection;
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
    private static async Task BindHelperIdentityAndWaitForShareInviteAsync(
        SessionRuntime runtime,
        HelpeePageViewModel helpee,
        PeerAddress? helperIdentity = null,
        PeerAddress? helperTarget = null,
        string? normalizedInputOverride = null)
    {
        await WaitUntilAsync(
            () => runtime.State == SessionRuntimeState.Waiting && runtime.CurrentLocalPeerAddress is not null,
            TimeSpan.FromSeconds(3));

        var boundIdentity = helperIdentity ?? new PeerAddress("nlink-helper.gui-smoke." + Guid.NewGuid().ToString("N"));
        var boundTarget = helperTarget ?? boundIdentity;
        helpee.SetVerifiedInviteHelperIdentity(
            boundIdentity,
            helperTargetAddress: boundTarget,
            refreshInvite: true,
            normalizedInputOverride: normalizedInputOverride);

        await WaitUntilAsync(() => helpee.HasShareInvite, TimeSpan.FromSeconds(3));
    }

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

        helpee.InviteHelperIdentityInput = helperInput;
        BindHelperIdentityAndWaitForShareInviteAsync(
            runtime,
            helpee,
            new PeerAddress(helperInput),
            helperTarget: helperTarget,
            normalizedInputOverride: helperInput).GetAwaiter().GetResult();
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

        await BindHelperIdentityAndWaitForShareInviteAsync(
            runtime,
            helpee,
            helperIdentity,
            helperTarget: helperTarget,
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

        await BindHelperIdentityAndWaitForShareInviteAsync(
            runtime,
            helpee,
            helperIdentity,
            helperTarget: helperTarget,
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
    public async Task HelpeePageViewModel_PastedHelperBootstrap_PreservesDisplayedFormat()
    {
        var scriptedTransport = new ScriptedSignalingTransport(
            onHostByAddressAsync: _ => Task.CompletedTask,
            localPeerAddress: "helpee.bootstrap.format");

        using var runtime = new SessionRuntime(() => scriptedTransport);
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, CreateNknTestConfig(), runtime);

        var helperIdentity = new PeerAddress("nlink-helper.format.identity");
        var helperTarget = new PeerAddress("nlink-helper.format.target");
        var helperBootstrap = HelperBootstrapQrPayload.Format(
            HelperBootstrapPayload.Create(
                helperTarget,
                helperId: HelperIdentityTokenCodec.Encode(helperIdentity)));

        await WaitUntilAsync(
            () => runtime.State == SessionRuntimeState.Waiting && runtime.CurrentLocalPeerAddress is not null,
            TimeSpan.FromSeconds(3));

        helpee.InviteHelperIdentityInput = helperBootstrap;
        await WaitUntilAsync(
            () => helpee.HasVerifiedInviteHelperIdentity &&
                  helpee.HasShareInvite &&
                  string.Equals(helpee.InviteHelperIdentityInput, helperBootstrap, StringComparison.Ordinal) &&
                  helpee.RequestHelpCommand.CanExecute(null),
            TimeSpan.FromSeconds(2));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelpeePageViewModel_ClearingHelperAddress_DisablesRequestHelp()
    {
        var scriptedTransport = new ScriptedSignalingTransport(
            onHostByAddressAsync: _ => Task.CompletedTask,
            localPeerAddress: "helpee.clear.helper");

        using var runtime = new SessionRuntime(() => scriptedTransport);
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, CreateNknTestConfig(), runtime);

        const string helperInput = "nhid1-DSP6-JVKB-5MW3-GE9M-71GK-ADHE-60VK-CE36-70WP-ARB2-CMRK-4DB1-60VP-4E9Q-75GK-4D33-6WS3-AE1N-CHJ6-2CV5-6XJ3-2E9H-6XHP-8E1Q-6GSK-GE35-74VK-8DV2-6MWP-8D9M-68W6-ASAN-JYRC-J";

        helpee.InviteHelperIdentityInput = helperInput;
        await WaitUntilAsync(
            () => helpee.RequestHelpCommand.CanExecute(null) && helpee.HasShareInvite,
            TimeSpan.FromSeconds(3));

        helpee.InviteHelperIdentityInput = string.Empty;

        await WaitUntilAsync(
            () => !helpee.RequestHelpCommand.CanExecute(null) &&
                  !helpee.CanRequestHelpAction,
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

        await BindHelperIdentityAndWaitForShareInviteAsync(
            runtime,
            helpee,
            helperIdentity,
            helperTarget: helperTarget,
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Smoke")]
    public async Task HelpeePageViewModel_PostTerminalRestart_IsBlockedOnlyForWindowClose(bool closing)
    {
        var createdTransports = 0;
        using var runtime = new SessionRuntime(() =>
        {
            Interlocked.Increment(ref createdTransports);
            return new ScriptedSignalingTransport(onHostByAddressAsync: _ => Task.CompletedTask);
        });
        using var helpee = new HelpeePageViewModel(static () => { }, CreateNknTestConfig(), runtime);
        await WaitUntilAsync(() => runtime.State == SessionRuntimeState.Waiting, TimeSpan.FromSeconds(3));

        if (closing)
        {
            await helpee.PrepareForWindowCloseAsync();
        }

        var generation = Assert.IsType<long>(GetPrivateField(helpee, "inviteBootstrapGeneration"));
        var flow = runtime.FlowSnapshot with
        {
            TerminalKind = SessionTerminalKind.PeerEnded,
            PostTerminalAction = SessionFlowPostTerminalAction.ReturnToWaiting,
            TerminalStatusText = "The other side ended the session."
        };
        InvokePrivateMethod(helpee, "TryApplyPostTerminalAction", flow);

        Assert.Equal(generation + (closing ? 0 : 1),
            Assert.IsType<long>(GetPrivateField(helpee, "inviteBootstrapGeneration")));
        if (!closing)
        {
            await WaitUntilAsync(() => Volatile.Read(ref createdTransports) == 2, TimeSpan.FromSeconds(3));
        }
        Assert.Equal(closing ? 1 : 2, Volatile.Read(ref createdTransports));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeePageViewModel_QueuedHosting_DoesNotStartAfterWindowClose()
    {
        var createdTransports = 0;
        using var runtime = new SessionRuntime(() =>
        {
            Interlocked.Increment(ref createdTransports);
            return new ScriptedSignalingTransport(onHostByAddressAsync: _ => Task.CompletedTask);
        });
        using var helpee = new HelpeePageViewModel(static () => { }, CreateNknTestConfig(), runtime);
        await WaitUntilAsync(() => runtime.State == SessionRuntimeState.Waiting, TimeSpan.FromSeconds(3));

        var gate = Assert.IsType<SemaphoreSlim>(GetPrivateField(runtime, "lifecycleGate"));
        await gate.WaitAsync();
        Task hosting;
        Task closing;
        try
        {
            hosting = Assert.IsAssignableFrom<Task>(InvokePrivateMethod(helpee, "StartHostingAsync"));
            Assert.False(hosting.IsCompleted);
            closing = helpee.PrepareForWindowCloseAsync();
        }
        finally
        {
            gate.Release();
        }

        await Task.WhenAll(hosting, closing).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, Volatile.Read(ref createdTransports));
        Assert.Equal(SessionRuntimeState.Idle, runtime.State);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelpeePageViewModel_FailedBootstrap_ReplacesStaleInvitePreparationStatus()
    {
        using var runtime = new SessionRuntime(() => new DevLocalTransport());
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, CreateDevLocalTestConfig(), runtime);

        InvokePrivateMethod(helpee, "UpdateShareInviteText", "stale-invite-token");
        InvokePrivateMethod(helpee, "UpdateShareInviteStatusText", "Preparing invite…");
        Assert.True(helpee.HasShareInvite);

        SetPrivateField(runtime, "role", SessionRuntimeRole.Helpee);
        SetPrivateField(runtime, "state", SessionRuntimeState.Failed);
        SetPrivateField(runtime, "transportState", TransportState.Failed);
        SetPrivateField(runtime, "statusText", "Could not start the connection system");
        SetPrivateField(
            runtime,
            "currentFlowSnapshot",
            runtime.FlowSnapshot with
            {
                Phase = SessionFlowPhase.Failed,
                UiPhase = SessionUiPhase.Failed,
                Role = SessionRuntimeRole.Helpee,
                RuntimeState = SessionRuntimeState.Failed,
                TransportState = TransportState.Failed,
                LastEndOrigin = SessionFlowEndOrigin.Failed,
                ShouldSuppressConnectedControls = true,
                TerminalKind = SessionTerminalKind.Failed,
                TerminalStatusText = "Could not start the connection system",
                FailureTitle = "Connection failed",
                FailureMessage = "The session ended due to a connection problem.",
                FailureActionText = "Retry",
                ShouldClearConversationUi = true,
                StatusText = "Could not start the connection system",
                DisplayStatusText = "Connection failed",
                DisplayConnectionState = "Failed",
                ShowRetryAction = true,
            });

        InvokePrivateMethod(helpee, "SyncFromRuntime");

        Assert.Equal("Connection failed", helpee.HeaderStatusText);
        Assert.Equal("Could not start the connection system", helpee.ShareInviteStatusText);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelpeePageViewModel_NknDefaultInviteBootstrapTimeout_CoversBridgePreflightWindow()
    {
        using var runtime = new SessionRuntime(() => new NeverReadyHelpeeHostTransport("helpee.nkn.timeout.default"));
        using var helpee = new HelpeePageViewModel(
            cancelAction: static () => { },
            CreateNknTestConfig(),
            runtime);

        var timeout = Assert.IsType<TimeSpan>(typeof(HelpeePageViewModel)
            .GetField("inviteBootstrapTimeout", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(helpee));

        Assert.True(timeout >= TimeSpan.FromSeconds(60), $"timeout={timeout}");
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelpeePageViewModel_HostReadyTimeout_ReplacesPreparingInviteStatus()
    {
        using var runtime = new SessionRuntime(() => new NeverReadyHelpeeHostTransport("helpee.never.ready"));
        using var helpee = new HelpeePageViewModel(
            cancelAction: static () => { },
            CreateDevLocalTestConfig(),
            runtime,
            inviteBootstrapTimeout: TimeSpan.FromMilliseconds(75));
        var helperIdentity = new PeerAddress("nlink-helper.bootstrap.timeout");

        helpee.SetVerifiedInviteHelperIdentity(
            helperIdentity,
            helperTargetAddress: helperIdentity,
            refreshInvite: true,
            normalizedInputOverride: helperIdentity.Value);

        try
        {
            await WaitUntilAsync(
                () => runtime.State == SessionRuntimeState.Failed &&
                      string.Equals(helpee.ShareInviteStatusText, "Could not prepare invite. Refresh invite and try again.", StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"state={runtime.State}; transport_state={runtime.TransportLifecycleState}; status={runtime.StatusText}; invite_status={helpee.ShareInviteStatusText}; has_invite={helpee.HasShareInvite}; connection_state={helpee.ConnectionState}",
                ex);
        }

        Assert.False(helpee.HasShareInvite);
        Assert.False(helpee.RequestHelpCommand.CanExecute(null));
        Assert.Equal("Failed", helpee.ConnectionState);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelpeePageViewModel_BridgeBootstrapNotReady_RehostsBeforeVisibleFailure()
    {
        var createdTransports = 0;
        var hostCalls = 0;
        using var runtime = new SessionRuntime(() =>
        {
            var attempt = Interlocked.Increment(ref createdTransports);
            return new BootstrapRetryHelpeeHostTransport(
                $"helpee.bootstrap.retry.{attempt}",
                failBeforeReady: attempt == 1,
                onHostCall: () => Interlocked.Increment(ref hostCalls));
        });
        using var helpee = new HelpeePageViewModel(
            cancelAction: static () => { },
            CreateDevLocalTestConfig(),
            runtime,
            inviteBootstrapTimeout: TimeSpan.FromSeconds(5));

        try
        {
            await WaitUntilAsync(
                () => Volatile.Read(ref hostCalls) >= 2 &&
                      runtime.State == SessionRuntimeState.Waiting &&
                      runtime.CurrentLocalPeerAddress is not null,
                TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"created_transports={Volatile.Read(ref createdTransports)}; host_calls={Volatile.Read(ref hostCalls)}; state={runtime.State}; transport_state={runtime.TransportLifecycleState}; status={runtime.StatusText}; connection_state={helpee.ConnectionState}; invite_status={helpee.ShareInviteStatusText}",
                ex);
        }

        Assert.NotEqual("Failed", helpee.ConnectionState);
        Assert.DoesNotContain("Could not prepare invite", helpee.ShareInviteStatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelpeePageViewModel_BridgeBootstrapNotReady_RetriesWithinNknInviteWindow()
    {
        var createdTransports = 0;
        var hostCalls = 0;
        using var runtime = new SessionRuntime(() =>
        {
            var attempt = Interlocked.Increment(ref createdTransports);
            return new BootstrapRetryHelpeeHostTransport(
                $"helpee.bootstrap.window.{attempt}",
                failBeforeReady: attempt <= 4,
                onHostCall: () => Interlocked.Increment(ref hostCalls));
        });
        using var helpee = new HelpeePageViewModel(
            cancelAction: static () => { },
            CreateNknTestConfig(),
            runtime,
            inviteBootstrapTimeout: TimeSpan.FromSeconds(10));

        try
        {
            await WaitUntilAsync(
                () => Volatile.Read(ref hostCalls) >= 5 &&
                      runtime.State == SessionRuntimeState.Waiting &&
                      runtime.CurrentLocalPeerAddress is not null,
                TimeSpan.FromSeconds(8));
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"created_transports={Volatile.Read(ref createdTransports)}; host_calls={Volatile.Read(ref hostCalls)}; state={runtime.State}; transport_state={runtime.TransportLifecycleState}; status={runtime.StatusText}; connection_state={helpee.ConnectionState}; invite_status={helpee.ShareInviteStatusText}",
                ex);
        }

        Assert.NotEqual("Failed", helpee.ConnectionState);
        Assert.DoesNotContain("Could not prepare invite", helpee.ShareInviteStatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelpeePageViewModel_HostReadyTimeoutBeforeBridgeReady_RetriesWithinNknInviteWindow()
    {
        var createdTransports = 0;
        var hostCalls = 0;
        using var runtime = new SessionRuntime(() =>
        {
            var attempt = Interlocked.Increment(ref createdTransports);
            return new BootstrapRetryHelpeeHostTransport(
                $"helpee.bootstrap.timeout.window.{attempt}",
                failBeforeReady: attempt <= 4,
                onHostCall: () => Interlocked.Increment(ref hostCalls),
                failAsHostReadyTimeout: true);
        });
        using var helpee = new HelpeePageViewModel(
            cancelAction: static () => { },
            CreateNknTestConfig(),
            runtime,
            inviteBootstrapTimeout: TimeSpan.FromSeconds(10));

        try
        {
            await WaitUntilAsync(
                () => Volatile.Read(ref hostCalls) >= 5 &&
                      runtime.State == SessionRuntimeState.Waiting &&
                      runtime.CurrentLocalPeerAddress is not null,
                TimeSpan.FromSeconds(8));
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"created_transports={Volatile.Read(ref createdTransports)}; host_calls={Volatile.Read(ref hostCalls)}; state={runtime.State}; transport_state={runtime.TransportLifecycleState}; status={runtime.StatusText}; connection_state={helpee.ConnectionState}; invite_status={helpee.ShareInviteStatusText}",
                ex);
        }

        Assert.NotEqual("Failed", helpee.ConnectionState);
        Assert.DoesNotContain("Could not prepare invite", helpee.ShareInviteStatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task HelpeePageViewModel_TransportUnavailableBeforeBridgeReady_SurfacesRetryablePreflightFailure()
    {
        var createdTransports = 0;
        var hostCalls = 0;
        using var runtime = new SessionRuntime(() =>
        {
            var attempt = Interlocked.Increment(ref createdTransports);
            return new BootstrapRetryHelpeeHostTransport(
                $"helpee.bootstrap.transport-unavailable.window.{attempt}",
                failBeforeReady: attempt <= 2,
                onHostCall: () => Interlocked.Increment(ref hostCalls),
                failAsTransportUnavailable: true);
        });
        using var helpee = new HelpeePageViewModel(
            cancelAction: static () => { },
            CreateNknTestConfig(),
            runtime,
            inviteBootstrapTimeout: TimeSpan.FromSeconds(10));

        try
        {
            await WaitUntilAsync(
                () => runtime.State == SessionRuntimeState.Failed &&
                      string.Equals(helpee.ShareInviteStatusText, "Could not prepare invite. Refresh invite and try again.", StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"created_transports={Volatile.Read(ref createdTransports)}; host_calls={Volatile.Read(ref hostCalls)}; state={runtime.State}; transport_state={runtime.TransportLifecycleState}; status={runtime.StatusText}; connection_state={helpee.ConnectionState}; invite_status={helpee.ShareInviteStatusText}",
                ex);
        }

        Assert.Equal(1, Volatile.Read(ref hostCalls));
        Assert.False(helpee.HasShareInvite);
        Assert.Equal("Failed", helpee.ConnectionState);
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

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelpeePageViewModel_SessionVerificationCode_RequiresHandshakeVerificationMaterial()
    {
        using var runtime = new SessionRuntime(() => new DevLocalTransport());
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, CreateDevLocalTestConfig(), runtime);
        var sessionId = new SessionId("session-verification-helpee-vm");
        var helpeeAddress = new PeerAddress("verification.helpee.vm");
        var helperAddress = new PeerAddress("verification.helper.vm");
        var pendingApproval = new ApprovalRequest(helperAddress, CapabilityGrant.Chat, sessionId);
        var verifiedWithoutCode = CreateVerifiedSecurityState(helpeeAddress, helperAddress, sessionId);

        SetPrivateField(runtime, "role", SessionRuntimeRole.Helpee);
        SetPrivateField(runtime, "state", SessionRuntimeState.IncomingJoinRequest);
        SetPrivateField(runtime, "transportState", TransportState.Handshake);
        SetPrivateField(runtime, "pendingApprovalRequest", pendingApproval);
        SetPrivateField(runtime, "sessionSecurityState", verifiedWithoutCode);
        SetPrivateField(
            runtime,
            "currentFlowSnapshot",
            runtime.FlowSnapshot with
            {
                Phase = SessionFlowPhase.PendingApproval,
                UiPhase = SessionUiPhase.Waiting,
                Role = SessionRuntimeRole.Helpee,
                RuntimeState = SessionRuntimeState.IncomingJoinRequest,
                TransportState = TransportState.Handshake,
                HasPendingApproval = true,
                StatusText = "Waiting for your approval…",
                DisplayStatusText = "Waiting for your approval…",
                DisplayConnectionState = "IncomingRequest",
                ShowIncomingApproval = true,
                SessionId = sessionId.Value,
                HelperIdentity = helperAddress.Value,
                VerificationCode = null,
            });

        InvokePrivateMethod(helpee, "SyncFromRuntime");

        Assert.True(helpee.HasIncomingHelperVerificationCode);
        Assert.False(helpee.HasSessionVerificationCode);
        Assert.False(helpee.ShowSessionVerificationCode);
        Assert.Equal(string.Empty, helpee.SessionVerificationEmojiSequence);
        Assert.Equal(string.Empty, helpee.SessionVerificationFallbackCode);

        var verificationCode = CreateTestSessionVerificationCode();
        SetPrivateField(runtime, "sessionSecurityState", verifiedWithoutCode.WithVerificationCode(verificationCode));
        SetPrivateField(
            runtime,
            "currentFlowSnapshot",
            runtime.FlowSnapshot with
            {
                VerificationCode = verificationCode,
            });

        InvokePrivateMethod(helpee, "SyncFromRuntime");

        Assert.True(helpee.HasSessionVerificationCode);
        Assert.True(helpee.ShowSessionVerificationCode);
        Assert.Equal(verificationCode.EmojiSequence, helpee.SessionVerificationEmojiSequence);
        Assert.Equal(verificationCode.FallbackCode, helpee.SessionVerificationFallbackCode);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void HelpeePageViewModel_SessionVerificationCode_ReplacesStalePendingApprovalCode()
    {
        using var runtime = new SessionRuntime(() => new DevLocalTransport());
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, CreateDevLocalTestConfig(), runtime);
        var firstSessionId = new SessionId("session-verification-helpee-first");
        var secondSessionId = new SessionId("session-verification-helpee-second");
        var helpeeAddress = new PeerAddress("verification.helpee.freshness");
        var firstHelperAddress = new PeerAddress("verification.helper.first");
        var secondHelperAddress = new PeerAddress("verification.helper.second");
        var firstCode = CreateTestSessionVerificationCode("sun moon star cloud leaf", "1111-2222-3333");
        var secondCode = CreateTestSessionVerificationCode("key lock wave spark map", "4444-5555-6666");

        SetPendingApprovalState(runtime, firstSessionId, helpeeAddress, firstHelperAddress, firstCode);
        InvokePrivateMethod(helpee, "SyncFromRuntime");

        Assert.True(helpee.ShowSessionVerificationCode);
        Assert.Equal(firstCode.EmojiSequence, helpee.SessionVerificationEmojiSequence);

        SetPendingApprovalState(runtime, secondSessionId, helpeeAddress, secondHelperAddress, secondCode);
        InvokePrivateMethod(helpee, "SyncFromRuntime");

        Assert.True(helpee.ShowSessionVerificationCode);
        Assert.Equal(secondCode.EmojiSequence, helpee.SessionVerificationEmojiSequence);
        Assert.DoesNotContain(firstCode.EmojiSequence, helpee.SessionVerificationEmojiSequence, StringComparison.Ordinal);
    }

    [Trait("Category", "Smoke")]
    [Theory]
    [InlineData(SessionFlowPhase.Ended, SessionUiPhase.Ended, SessionRuntimeState.Rejected, TransportState.Failed)]
    [InlineData(SessionFlowPhase.Failed, SessionUiPhase.Failed, SessionRuntimeState.Disconnected, TransportState.Failed)]
    public void HelpeePageViewModel_SessionVerificationCode_HidesAfterPendingApprovalEnds(
        SessionFlowPhase phase,
        SessionUiPhase uiPhase,
        SessionRuntimeState runtimeState,
        TransportState transportState)
    {
        using var runtime = new SessionRuntime(() => new DevLocalTransport());
        using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, CreateDevLocalTestConfig(), runtime);
        var sessionId = new SessionId("session-verification-helpee-clears");
        var helpeeAddress = new PeerAddress("verification.helpee.clears");
        var helperAddress = new PeerAddress("verification.helper.clears");
        var verificationCode = CreateTestSessionVerificationCode();

        SetPendingApprovalState(runtime, sessionId, helpeeAddress, helperAddress, verificationCode);
        InvokePrivateMethod(helpee, "SyncFromRuntime");
        Assert.True(helpee.ShowSessionVerificationCode);

        SetPrivateField(runtime, "state", runtimeState);
        SetPrivateField(runtime, "transportState", transportState);
        SetPrivateField(runtime, "pendingApprovalRequest", null);
        SetPrivateField(
            runtime,
            "currentFlowSnapshot",
            runtime.FlowSnapshot with
            {
                Phase = phase,
                UiPhase = uiPhase,
                RuntimeState = runtimeState,
                TransportState = transportState,
                HasPendingApproval = false,
                ShowIncomingApproval = false,
                DisplayStatusText = "Connection ended",
                DisplayConnectionState = "Disconnected",
                VerificationCode = verificationCode,
            });

        InvokePrivateMethod(helpee, "SyncFromRuntime");

        Assert.True(helpee.HasSessionVerificationCode);
        Assert.False(helpee.ShowSessionVerificationCode);
    }

    private static void SetPendingApprovalState(
        SessionRuntime runtime,
        SessionId sessionId,
        PeerAddress helpeeAddress,
        PeerAddress helperAddress,
        SessionVerificationCode verificationCode)
    {
        var pendingApproval = new ApprovalRequest(helperAddress, CapabilityGrant.Chat, sessionId);
        var verifiedState = CreateVerifiedSecurityState(helpeeAddress, helperAddress, sessionId)
            .WithVerificationCode(verificationCode);

        SetPrivateField(runtime, "role", SessionRuntimeRole.Helpee);
        SetPrivateField(runtime, "state", SessionRuntimeState.IncomingJoinRequest);
        SetPrivateField(runtime, "transportState", TransportState.Handshake);
        SetPrivateField(runtime, "pendingApprovalRequest", pendingApproval);
        SetPrivateField(runtime, "sessionSecurityState", verifiedState);
        SetPrivateField(
            runtime,
            "currentFlowSnapshot",
            runtime.FlowSnapshot with
            {
                Phase = SessionFlowPhase.PendingApproval,
                UiPhase = SessionUiPhase.Waiting,
                Role = SessionRuntimeRole.Helpee,
                RuntimeState = SessionRuntimeState.IncomingJoinRequest,
                TransportState = TransportState.Handshake,
                HasPendingApproval = true,
                StatusText = "Waiting for your approval…",
                DisplayStatusText = "Waiting for your approval…",
                DisplayConnectionState = "IncomingRequest",
                ShowIncomingApproval = true,
                SessionId = sessionId.Value,
                HelperIdentity = helperAddress.Value,
                VerificationCode = verificationCode,
            });
    }

    private static SessionVerificationCode CreateTestSessionVerificationCode(
        string emojiSequence = "sun moon star cloud leaf fire key",
        string fallbackCode = "FACE-B00C-1234")
    {
        return new SessionVerificationCode(
            emojiSequence,
            fallbackCode,
            SessionVerificationCodeDerivation.SourceHandshakeTranscriptV1);
    }

    private sealed class BootstrapRetryHelpeeHostTransport :
        ISignalingTransport,
        IAddressHostSignalingTransport,
        IHostReadySignalingTransport,
        IBridgeBootstrapReadinessSignalingTransport,
        ILocalPeerAddressSignalingTransport,
        ISessionSecuritySignalingTransport
    {
        private readonly TaskCompletionSource<bool> hostReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool failBeforeReady;
        private readonly Action onHostCall;
        private readonly bool failAsHostReadyTimeout;
        private readonly bool failAsTransportUnavailable;
        private int bootstrapFailed;
        private SessionSecurityState currentSessionSecurityState = SessionSecurityState.Empty;

        public BootstrapRetryHelpeeHostTransport(
            string localPeerAddress,
            bool failBeforeReady,
            Action onHostCall,
            bool failAsHostReadyTimeout = false,
            bool failAsTransportUnavailable = false)
        {
            LocalPeerAddress = localPeerAddress;
            this.failBeforeReady = failBeforeReady;
            this.onHostCall = onHostCall;
            this.failAsHostReadyTimeout = failAsHostReadyTimeout;
            this.failAsTransportUnavailable = failAsTransportUnavailable;
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

        public Task HostByAddressAsync(CancellationToken ct)
        {
            onHostCall();
            if (failBeforeReady)
            {
                Volatile.Write(ref bootstrapFailed, 1);
                if (failAsTransportUnavailable)
                {
                    NknRuntimeDiagnostics.SetLastError("transport_unavailable");
                    throw new InvalidOperationException("transport_unavailable");
                }

                if (failAsHostReadyTimeout)
                {
                    NknRuntimeDiagnostics.SetLastError("ConnectToNodeTimeoutError: host_ready_connect_failed");
                    throw new TimeoutException("ConnectToNodeTimeoutError: host_ready_connect_failed");
                }

                NknRuntimeDiagnostics.SetLastError(
                    "nkn_bridge_bootstrap_not_ready: role=helpee; reason=bridge_transport_connect_failed_before_ready");
                throw new InvalidOperationException(
                    "nkn_bridge_bootstrap_not_ready: role=helpee; reason=bridge_transport_connect_failed_before_ready");
            }

            currentSessionSecurityState = SessionSecurityState.CreateHelpeeWaiting(new PeerAddress(LocalPeerAddress));
            SessionSecurityStateChanged?.Invoke(this, new TransportSessionSecurityStateChangedEventArgs(currentSessionSecurityState));
            hostReadyTcs.TrySetResult(true);
            return Task.Delay(Timeout.Infinite, ct);
        }

        public Task WaitUntilHostReadyAsync(CancellationToken ct) => hostReadyTcs.Task.WaitAsync(ct);

        public BridgeBootstrapReadinessSnapshot GetBridgeBootstrapReadinessSnapshot()
            => Volatile.Read(ref bootstrapFailed) == 0
                ? new(
                    Role: "helpee_test_host",
                    Generation: 1,
                    RunId: LocalPeerAddress,
                    ReadyEmitted: !failBeforeReady,
                    ControlReady: !failBeforeReady,
                    MediaReady: !failBeforeReady,
                    BulkReady: !failBeforeReady,
                    LastReadyUtcMs: failBeforeReady ? 0 : 1,
                    DeadlineUtcMs: 0,
                    TerminalFailure: false,
                    FailureReason: "(none)",
                    LastError: "(none)")
                : new(
                    Role: "helpee_test_host",
                    Generation: 1,
                    RunId: LocalPeerAddress,
                    ReadyEmitted: false,
                    ControlReady: false,
                    MediaReady: false,
                    BulkReady: false,
                    LastReadyUtcMs: 0,
                    DeadlineUtcMs: 0,
                    TerminalFailure: true,
                    FailureReason: failAsTransportUnavailable ? "transport_unavailable" : failAsHostReadyTimeout ? "host_ready_connect_failed" : "bridge_transport_connect_failed_before_ready",
                    LastError: failAsTransportUnavailable ? "(none)" : failAsHostReadyTimeout ? "ConnectToNodeTimeoutError" : "connect EACCES");

        public Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private sealed class NeverReadyHelpeeHostTransport :
        ISignalingTransport,
        IAddressHostSignalingTransport,
        IHostReadySignalingTransport,
        IBridgeBootstrapReadinessSignalingTransport,
        ILocalPeerAddressSignalingTransport
    {
        public NeverReadyHelpeeHostTransport(string localPeerAddress)
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

        public Task HostByAddressAsync(CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);

        public Task WaitUntilHostReadyAsync(CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);

        public BridgeBootstrapReadinessSnapshot GetBridgeBootstrapReadinessSnapshot()
            => new(
                Role: "helpee_test_host",
                Generation: 1,
                RunId: "helpee-never-ready-test",
                ReadyEmitted: false,
                ControlReady: false,
                MediaReady: false,
                BulkReady: false,
                LastReadyUtcMs: 0,
                DeadlineUtcMs: 0,
                TerminalFailure: true,
                FailureReason: "bridge_never_ready",
                LastError: "(none)");

        public Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => Task.CompletedTask;

        public void Dispose()
        {
        }
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
