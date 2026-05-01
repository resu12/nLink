using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Media.Imaging;
using NLink.App;
using NLink.App.Configuration;
using NLink.App.Services;
using NLink.App.Services.RemoteControl;
using NLink.App.Services.ScreenCapture;
using NLink.App.ViewModels;
using NLink.App.Views;
using NLink.Core;
using NLink.Core.Chat;
using NLink.Core.Diagnostics;
using NLink.Core.FileTransfer;
using NLink.Core.Metrics;
using NLink.Core.RemoteControl;
using NLink.Core.Resources;
using NLink.Core.Retry;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Core.Logging;
using NLink.Infra.DevLocal;
using NLink.Infra.Nkn;
using NLink.SmokeTests.Fakes;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Gui")]
public sealed class HelpeeSessionUiTests : SessionHeaderAndBannerTestBase
{
    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelpeePageViewModel_StatusBanner_ReactsToFailedReconnectingConnected()
    {
        var source = new FakeStatusPresenterSource();
        using var presenter = new StatusPresenter(source);
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport(), SessionRuntimeWatchdogOptions.Default with { Enabled = false });
        var transportConfig = CreateDevLocalTestConfig();
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, runtime, statusPresenter: presenter);
        source.SetFailure(TransportFailure.Create(TransportFailureCategory.BridgeStartFailure, "Bridge unavailable", exceptionType: nameof(InvalidOperationException), rawError: "bridge_start_failed", isTransient: false, correlationId: "corr2"));
        source.SetSessionUiState(SessionRuntimeState.Failed);
        source.SetTransportState(TransportState.Failed);
        source.RaiseStateChanged();
        Assert.Equal(UserStatusKind.Failed, helpee.BannerStatus.Kind);
        Assert.True(helpee.ShowStatusBanner);
        source.SetAttempt(3);
        source.SetTransportState(TransportState.Reconnecting);
        source.RaiseTransient(isVisible: true, text: "Reconnecting… (attempt 3, next retry in 2s)", canCancel: true);
        Assert.Equal(UserStatusKind.Reconnecting, helpee.BannerStatus.Kind);
        Assert.True(helpee.ShowStatusBanner);
        source.SetSessionUiState(SessionRuntimeState.Connected);
        source.SetTransportState(TransportState.Connected);
        source.RaiseTransient(isVisible: false, text: string.Empty, canCancel: false);
        source.RaiseStateChanged();
        Assert.Equal(UserStatusKind.Connected, helpee.BannerStatus.Kind);
        Assert.False(helpee.ShowStatusBanner);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeeViewModel_ProtectedSeedReadFailure_ShowsStableError_WithoutAutoRestartLoop()
    {
        PersistenceDiagnostics.ClearForTests();
        try
        {
            PersistenceDiagnostics.Record(domain: "nkn_secret_store", operation: "load_seed", severity: PersistenceDiagnosticSeverity.Error, outcome: PersistenceDiagnosticOutcome.FailedClosed, reason: "CryptographicException", userWarning: "Protected seed storage could not be read.");
            var createCount = 0;
            using var runtime = new SessionRuntime(() =>
            {
                Interlocked.Increment(ref createCount);
                throw new InvalidOperationException("Protected NKN seed storage is unavailable for 'identity.json'.");
            });
            using var helpee = new HelpeePageViewModel(cancelAction: static () =>
            {
            }, CreateNknTestConfig(), runtime);
            await WaitUntilAsync(() => string.Equals(helpee.ConnectionState, "Failed", StringComparison.Ordinal) && string.Equals(helpee.ConnectionStatus, "Protected seed storage could not be read.", StringComparison.Ordinal), TimeSpan.FromSeconds(2));
            var settledCreateCount = Volatile.Read(ref createCount);
            await Task.Delay(250);
            Assert.Equal(settledCreateCount, Volatile.Read(ref createCount));
            Assert.Equal("Protected seed storage could not be read.", helpee.ShareInviteStatusText);
            Assert.Equal("Failed", helpee.ConnectionState);
            Assert.True(helpee.ShowRetryAction);
        }
        finally
        {
            PersistenceDiagnostics.ClearForTests();
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelpeeViewModel_RecoveredLocalIdentity_KeepsRecoveryNoticeVisible_AfterInviteReady()
    {
        PersistenceDiagnostics.ClearForTests();
        try
        {
            PersistenceDiagnostics.Record(domain: "nkn_identity_store", operation: "automatic_identity_recovery", severity: PersistenceDiagnosticSeverity.Warning, outcome: PersistenceDiagnosticOutcome.Partial, reason: "default_identity_recreated", userWarning: "Local protected identity storage was unreadable. nLink created a new local identity. Previous helper address and invites are no longer valid.");
            using var runtime = new SessionRuntime(() => new DevLocalTransport());
            using var helpee = new HelpeePageViewModel(cancelAction: static () =>
            {
            }, CreateNknTestConfig(), runtime);
            SetPrivateField(helpee, "shareInviteText", "invite-token");
            InvokePrivateMethod(helpee, "UpdateShareInviteStatusText", "Invite ready");
            Assert.True(helpee.ShowShareInviteStatus);
            Assert.Contains("created a new local identity", helpee.ShareInviteStatusText, StringComparison.Ordinal);
            Assert.Contains("Invite ready", helpee.ShareInviteStatusText, StringComparison.Ordinal);
        }
        finally
        {
            PersistenceDiagnostics.ClearForTests();
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelpeeViewModel_RecoveredLocalIdentity_KeepsRecoveryNoticeVisible_AfterLaterPersistenceWarning()
    {
        PersistenceDiagnostics.ClearForTests();
        try
        {
            PersistenceDiagnostics.Record(domain: "nkn_identity_store", operation: "automatic_identity_recovery", severity: PersistenceDiagnosticSeverity.Warning, outcome: PersistenceDiagnosticOutcome.Partial, reason: "default_identity_recreated", userWarning: "Local protected identity storage was unreadable. nLink created a new local identity. Previous helper address and invites are no longer valid.");
            using var runtime = new SessionRuntime(() => new DevLocalTransport());
            using var helpee = new HelpeePageViewModel(cancelAction: static () =>
            {
            }, CreateNknTestConfig(), runtime);
            PersistenceDiagnostics.Record(domain: "nkn_secret_store", operation: "read_seed", severity: PersistenceDiagnosticSeverity.Warning, outcome: PersistenceDiagnosticOutcome.Fallback, reason: "CryptographicException", userWarning: "Protected seed storage could not be read.");
            SetPrivateField(helpee, "shareInviteText", "invite-token");
            InvokePrivateMethod(helpee, "UpdateShareInviteStatusText", "Invite ready");
            Assert.True(helpee.ShowShareInviteStatus);
            Assert.Contains("created a new local identity", helpee.ShareInviteStatusText, StringComparison.Ordinal);
            Assert.Contains("Invite ready", helpee.ShareInviteStatusText, StringComparison.Ordinal);
        }
        finally
        {
            PersistenceDiagnostics.ClearForTests();
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeeViewModel_RecoveredLocalIdentity_RefreshesNotice_AfterStartupRecovery()
    {
        PersistenceDiagnostics.ClearForTests();
        try
        {
            using var runtime = new SessionRuntime(() => new FakeSignalingTransport());
            using var helpee = new HelpeePageViewModel(cancelAction: static () =>
            {
            }, CreateNknTestConfig(), runtime);
            PersistenceDiagnostics.Record(domain: "nkn_identity_store", operation: "automatic_identity_recovery", severity: PersistenceDiagnosticSeverity.Warning, outcome: PersistenceDiagnosticOutcome.Partial, reason: "default_identity_recreated", userWarning: "Local protected identity storage was unreadable. nLink created a new local identity. Previous helper address and invites are no longer valid.");
            var startTask = Assert.IsAssignableFrom<Task>(InvokePrivateMethod(helpee, "StartHostingAsync"));
            await startTask;
            Assert.Contains("created a new local identity", helpee.ShareInviteStatusText, StringComparison.Ordinal);
        }
        finally
        {
            PersistenceDiagnostics.ClearForTests();
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeeViewModel_RequestHelpTimeout_DoesNotCrashAndRestoresWaitingState()
    {
        using var unboundInviteOptIn = new EnvironmentOverride(NLink.App.Configuration.AppFeatureFlags.AllowInsecureUnboundPublicInvitesEnvVar, null);
        var scriptedTransport = new ScriptedSignalingTransport(onHostByAddressAsync: _ => Task.CompletedTask, localPeerAddress: "helpee.request.timeout", onSendHelpRequestAsync: static (_, _) => throw new TimeoutException("Ack was not received."));
        using var runtime = new SessionRuntime(() => scriptedTransport);
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, CreateNknTestConfig(), runtime);
        helpee.InviteHelperIdentityInput = "helper.request.target";
        await WaitUntilAsync(() => helpee.RequestHelpCommand.CanExecute(null), TimeSpan.FromSeconds(2));
        var requestTask = Assert.IsAssignableFrom<Task>(InvokePrivateMethod(helpee, "RequestHelpAsync"));
        await requestTask;
        Assert.Equal(SessionRuntimeState.Waiting, runtime.State);
        Assert.Equal("Waiting for helper…", helpee.ConnectionStatus);
        Assert.Contains("Couldn't reach the helper", helpee.ShareInviteStatusText, StringComparison.Ordinal);
        Assert.Null(runtime.PendingOutboundHelpRequestDecision);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeeViewModel_RequestHelp_CompactBootstrap_RoutesToHelperAddress_AndBindsInviteToHelperId()
    {
        using var unboundInviteOptIn = new EnvironmentOverride(NLink.App.Configuration.AppFeatureFlags.AllowInsecureUnboundPublicInvitesEnvVar, null);
        HelpRequestMessage? sentRequest = null;
        var scriptedTransport = new ScriptedSignalingTransport(onHostByAddressAsync: _ => Task.CompletedTask, localPeerAddress: "helpee.compact.bootstrap", onSendHelpRequestAsync: (request, _) =>
        {
            sentRequest = request;
            return Task.CompletedTask;
        });
        using var runtime = new SessionRuntime(() => scriptedTransport);
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, CreateNknTestConfig(), runtime);
        var helperAddress = new PeerAddress("nlink-helper.request.target.compact.1234567890");
        var helperIdentity = new PeerAddress("nlink-helper.identity.compact.0987654321");
        helpee.InviteHelperIdentityInput = HelperBootstrapQrPayload.Format(HelperBootstrapPayload.Create(helperAddress, helperId: HelperIdentityTokenCodec.Encode(helperIdentity), fingerprintHint: "ignored"));
        await WaitUntilAsync(() => helpee.RequestHelpCommand.CanExecute(null), TimeSpan.FromSeconds(2));
        var requestTask = Assert.IsAssignableFrom<Task>(InvokePrivateMethod(helpee, "RequestHelpAsync"));
        await requestTask;
        Assert.NotNull(sentRequest);
        Assert.Equal(helperAddress, sentRequest!.HelperAddress);
        var validator = InviteTokenServiceFactory.CreateInviteTokenValidator();
        var validation = validator.Validate(sentRequest.InviteToken, DateTimeOffset.UtcNow);
        Assert.True(validation.IsSuccess, validation.Message);
        Assert.NotNull(validation.Invite);
        Assert.Equal(helperIdentity, validation.Invite!.BoundHelperAddress);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeeViewModel_HelperRejectsHelpRequest_ShowsRejectedStatus()
    {
        using var unboundInviteOptIn = new EnvironmentOverride(NLink.App.Configuration.AppFeatureFlags.AllowInsecureUnboundPublicInvitesEnvVar, null);
        using var scriptedTransport = new ScriptedSignalingTransport(onHostByAddressAsync: _ => Task.CompletedTask, localPeerAddress: "helpee.reject.status");
        using var helpeeRuntime = new SessionRuntime(() => scriptedTransport);
        var transportConfig = CreateNknTestConfig();
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helpeeRuntime);
        var helperIdentity = new PeerAddress("nlink-helper.reject.status");
        var helperTarget = new PeerAddress("nlink-helper.reject.target");
        var helperBootstrap = HelperBootstrapQrPayload.Format(HelperBootstrapPayload.Create(helperTarget, helperId: HelperIdentityTokenCodec.Encode(helperIdentity)));
        helpee.SetVerifiedInviteHelperIdentity(helperIdentity, helperTargetAddress: helperTarget, refreshInvite: true, normalizedInputOverride: helperBootstrap);
        await WaitUntilAsync(() => helpee.RequestHelpCommand.CanExecute(null), TimeSpan.FromSeconds(2));
        SetPrivateField(helpeeRuntime, "<PendingOutboundHelpRequestDecision>k__BackingField", new HelpRequestDecisionMessage("hr_reject", new PeerAddress("helpee.reject.status"), helperTarget, Accepted: false, Reason: "request_rejected"));
        SetPrivateField(helpeeRuntime, "state", SessionRuntimeState.Rejected);
        SetPrivateField(helpeeRuntime, "statusText", "Request was rejected.");
        SetPrivateField(helpeeRuntime, "currentFlowSnapshot", helpeeRuntime.FlowSnapshot with { Phase = SessionFlowPhase.Failed, UiPhase = SessionUiPhase.Failed, Role = SessionRuntimeRole.Helpee, RuntimeState = SessionRuntimeState.Rejected, LastEndOrigin = SessionFlowEndOrigin.Rejected, TerminalKind = SessionTerminalKind.Rejected, TerminalStatusText = "Request was rejected.", FailureTitle = "Request rejected", FailureMessage = "The helper declined the request.", FailureActionText = "Retry", ShouldClearConversationUi = true, ShouldSuppressConnectedControls = true, DisplayStatusText = "Request was rejected.", DisplayConnectionState = "Failed", ShowRetryAction = true, ShowDiagnosticsAction = true, PostTerminalAction = SessionFlowPostTerminalAction.ReturnToWaitingPreserveBootstrap, });
        InvokePrivateMethod(helpee, "OnHelpRequestDecisionAvailable", helpeeRuntime, EventArgs.Empty);
        await WaitUntilAsync(() => string.Equals(helpee.ShareInviteStatusText, "The helper declined the request.", StringComparison.Ordinal) && !helpee.HasIncomingRequest && helpee.HasShareInvite && helpee.RequestHelpCommand.CanExecute(null), TimeSpan.FromSeconds(5));
        Assert.False(helpee.HasIncomingRequest);
        Assert.Equal("The helper declined the request.", helpee.ShareInviteStatusText);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeeViewModel_HelperClosesHelpRequest_ShowsUnavailableStatus()
    {
        using var unboundInviteOptIn = new EnvironmentOverride(NLink.App.Configuration.AppFeatureFlags.AllowInsecureUnboundPublicInvitesEnvVar, null);
        using var scriptedTransport = new ScriptedSignalingTransport(onHostByAddressAsync: _ => Task.CompletedTask, localPeerAddress: "helpee.helper.closed.status");
        using var helpeeRuntime = new SessionRuntime(() => scriptedTransport);
        var transportConfig = CreateNknTestConfig();
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helpeeRuntime);
        var helperIdentity = new PeerAddress("nlink-helper.closed.status");
        var helperTarget = new PeerAddress("nlink-helper.closed.target");
        var helperBootstrap = HelperBootstrapQrPayload.Format(HelperBootstrapPayload.Create(helperTarget, helperId: HelperIdentityTokenCodec.Encode(helperIdentity)));
        helpee.SetVerifiedInviteHelperIdentity(helperIdentity, helperTargetAddress: helperTarget, refreshInvite: true, normalizedInputOverride: helperBootstrap);
        await WaitUntilAsync(() => helpee.RequestHelpCommand.CanExecute(null), TimeSpan.FromSeconds(2));
        SetPrivateField(helpeeRuntime, "<PendingOutboundHelpRequestDecision>k__BackingField", new HelpRequestDecisionMessage("hr_closed", new PeerAddress("helpee.helper.closed.status"), helperTarget, Accepted: false, Reason: "helper_closed"));
        SetPrivateField(helpeeRuntime, "state", SessionRuntimeState.Rejected);
        SetPrivateField(helpeeRuntime, "statusText", "The helper is no longer available.");
        SetPrivateField(helpeeRuntime, "currentFlowSnapshot", helpeeRuntime.FlowSnapshot with { Phase = SessionFlowPhase.Failed, UiPhase = SessionUiPhase.Failed, Role = SessionRuntimeRole.Helpee, RuntimeState = SessionRuntimeState.Rejected, LastEndOrigin = SessionFlowEndOrigin.Rejected, TerminalKind = SessionTerminalKind.Rejected, TerminalStatusText = "The helper is no longer available.", FailureTitle = "Request rejected", FailureMessage = "The helper is no longer available.", FailureActionText = "Retry", ShouldClearConversationUi = true, ShouldSuppressConnectedControls = true, DisplayStatusText = "The helper is no longer available.", DisplayConnectionState = "Failed", ShowRetryAction = true, ShowDiagnosticsAction = true, PostTerminalAction = SessionFlowPostTerminalAction.ReturnToWaitingPreserveBootstrap, });
        InvokePrivateMethod(helpee, "OnHelpRequestDecisionAvailable", helpeeRuntime, EventArgs.Empty);
        await WaitUntilAsync(
            () => string.Equals(helpee.ShareInviteStatusText, "The helper is no longer available.", StringComparison.Ordinal) &&
                  !helpee.HasIncomingRequest &&
                  helpee.HasShareInvite &&
                  string.IsNullOrWhiteSpace(helpee.InviteHelperIdentityInput) &&
                  !helpee.HasVerifiedInviteHelperIdentity &&
                  !helpee.RequestHelpCommand.CanExecute(null),
            TimeSpan.FromSeconds(5));
        Assert.False(helpee.HasIncomingRequest);
        Assert.Equal("The helper is no longer available.", helpee.ShareInviteStatusText);
        Assert.Equal(string.Empty, helpee.InviteHelperIdentityInput);
        Assert.False(helpee.HasVerifiedInviteHelperIdentity);
        Assert.False(helpee.CanRequestHelpAction);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeeViewModel_HelperDisconnectsAfterAccept_ReturnsToStartingSetupState()
    {
        using var unboundInviteOptIn = new EnvironmentOverride(NLink.App.Configuration.AppFeatureFlags.AllowInsecureUnboundPublicInvitesEnvVar, null);
        using var scriptedTransport = new ScriptedSignalingTransport(onHostByAddressAsync: _ => Task.CompletedTask, localPeerAddress: "helpee.helper.accepted.disconnect");
        using var helpeeRuntime = new SessionRuntime(() => scriptedTransport);
        var transportConfig = CreateNknTestConfig();
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helpeeRuntime);
        var helperIdentity = new PeerAddress("nlink-helper.accepted.disconnect.identity");
        var helperTarget = new PeerAddress("nlink-helper.accepted.disconnect.target");
        var helperBootstrap = HelperBootstrapQrPayload.Format(HelperBootstrapPayload.Create(helperTarget, helperId: HelperIdentityTokenCodec.Encode(helperIdentity)));
        helpee.SetVerifiedInviteHelperIdentity(helperIdentity, helperTargetAddress: helperTarget, refreshInvite: true, normalizedInputOverride: helperBootstrap);
        await WaitUntilAsync(() => helpee.RequestHelpCommand.CanExecute(null), TimeSpan.FromSeconds(2));

        SetPrivateField(helpeeRuntime, "<PendingOutboundHelpRequestDecision>k__BackingField", new HelpRequestDecisionMessage("hr_accepted_disconnect", new PeerAddress("helpee.helper.accepted.disconnect"), helperTarget, Accepted: true, Reason: null));
        SetPrivateField(helpeeRuntime, "state", SessionRuntimeState.Failed);
        SetPrivateField(helpeeRuntime, "transportState", TransportState.Failed);
        SetPrivateField(helpeeRuntime, "statusText", "Connection lost.");
        SetPrivateField(helpeeRuntime, "currentFlowSnapshot", helpeeRuntime.FlowSnapshot with
        {
            Phase = SessionFlowPhase.HelpeeWaiting,
            UiPhase = SessionUiPhase.Waiting,
            Role = SessionRuntimeRole.Helpee,
            RuntimeState = SessionRuntimeState.Failed,
            TransportState = TransportState.Failed,
            LastEndOrigin = SessionFlowEndOrigin.Remote,
            TerminalKind = SessionTerminalKind.None,
            TerminalStatusText = string.Empty,
            FailureTitle = string.Empty,
            FailureMessage = string.Empty,
            FailureActionText = string.Empty,
            ShouldClearConversationUi = true,
            ShouldSuppressConnectedControls = true,
            DisplayStatusText = "Connection lost.",
            DisplayConnectionState = "Waiting",
            ShowRetryAction = false,
            ShowDiagnosticsAction = true,
            PostTerminalAction = SessionFlowPostTerminalAction.None,
            FailureReason = "transport_disconnected",
        });

        InvokePrivateMethod(helpee, "SyncFromRuntime");

        await WaitUntilAsync(
            () => string.IsNullOrWhiteSpace(helpee.InviteHelperIdentityInput) &&
                  !helpee.HasVerifiedInviteHelperIdentity &&
                  !helpee.RequestHelpCommand.CanExecute(null) &&
                  string.Equals(helpee.ConnectionState, "Waiting", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));

        Assert.Equal(string.Empty, helpee.InviteHelperIdentityInput);
        Assert.False(helpee.HasVerifiedInviteHelperIdentity);
        Assert.False(helpee.CanRequestHelpAction);
        Assert.False(helpee.RequestHelpCommand.CanExecute(null));
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeeViewModel_HelperDisconnectsDuringPendingHelpRequest_ReturnsToStartingSetupState()
    {
        using var unboundInviteOptIn = new EnvironmentOverride(NLink.App.Configuration.AppFeatureFlags.AllowInsecureUnboundPublicInvitesEnvVar, null);
        using var scriptedTransport = new ScriptedSignalingTransport(
            onHostByAddressAsync: _ => Task.CompletedTask,
            localPeerAddress: "helpee.helper.pending.disconnect",
            onSendHelpRequestAsync: static (_, _) => Task.CompletedTask);
        using var helpeeRuntime = new SessionRuntime(() => scriptedTransport);
        var transportConfig = CreateNknTestConfig();
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helpeeRuntime);
        var helperIdentity = new PeerAddress("nlink-helper.pending.disconnect.identity");
        var helperTarget = new PeerAddress("nlink-helper.pending.disconnect.target");
        var helperBootstrap = HelperBootstrapQrPayload.Format(HelperBootstrapPayload.Create(helperTarget, helperId: HelperIdentityTokenCodec.Encode(helperIdentity)));
        helpee.SetVerifiedInviteHelperIdentity(helperIdentity, helperTargetAddress: helperTarget, refreshInvite: true, normalizedInputOverride: helperBootstrap);
        await WaitUntilAsync(() => helpee.RequestHelpCommand.CanExecute(null), TimeSpan.FromSeconds(2));

        var requestTask = Assert.IsAssignableFrom<Task>(InvokePrivateMethod(helpee, "RequestHelpAsync"));
        await WaitUntilAsync(() => helpeeRuntime.HasPendingOutboundHelpRequest, TimeSpan.FromSeconds(2));

        scriptedTransport.RaiseDisconnected();
        await requestTask.WaitAsync(TimeSpan.FromSeconds(2));

        await WaitUntilAsync(
            () => string.Equals(helpee.ShareInviteStatusText, "The helper is no longer available.", StringComparison.Ordinal) &&
                  string.IsNullOrWhiteSpace(helpee.InviteHelperIdentityInput) &&
                  !helpee.HasVerifiedInviteHelperIdentity &&
                  !helpee.RequestHelpCommand.CanExecute(null) &&
                  string.Equals(helpee.ConnectionState, "Waiting", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));

        Assert.Equal(string.Empty, helpee.InviteHelperIdentityInput);
        Assert.False(helpee.HasVerifiedInviteHelperIdentity);
        Assert.False(helpee.CanRequestHelpAction);
        Assert.False(helpee.RequestHelpCommand.CanExecute(null));
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeeViewModel_PendingHelpRequestTimeout_ReturnsToWaiting()
    {
        using var unboundInviteOptIn = new EnvironmentOverride(NLink.App.Configuration.AppFeatureFlags.AllowInsecureUnboundPublicInvitesEnvVar, null);
        var scriptedTransport = new ScriptedSignalingTransport(
            onHostByAddressAsync: _ => Task.CompletedTask,
            localPeerAddress: "helpee.pending.timeout",
            onSendHelpRequestAsync: static (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));
        using var helpeeRuntime = new SessionRuntime(
            () => scriptedTransport,
            SessionRuntimeWatchdogOptions.Default,
            outboundHelpRequestDecisionTimeout: TimeSpan.FromMilliseconds(80));
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, CreateNknTestConfig(), helpeeRuntime);
        var helperIdentity = new PeerAddress("nlink-helper.pending.timeout.identity");
        var helperTarget = new PeerAddress("nlink-helper.pending.timeout.target");
        var helperBootstrap = HelperBootstrapQrPayload.Format(HelperBootstrapPayload.Create(helperTarget, helperId: HelperIdentityTokenCodec.Encode(helperIdentity)));
        helpee.SetVerifiedInviteHelperIdentity(helperIdentity, helperTargetAddress: helperTarget, refreshInvite: true, normalizedInputOverride: helperBootstrap);
        await WaitUntilAsync(() => helpee.RequestHelpCommand.CanExecute(null), TimeSpan.FromSeconds(2));

        var requestTask = Assert.IsAssignableFrom<Task>(InvokePrivateMethod(helpee, "RequestHelpAsync"));
        await WaitUntilAsync(() => helpeeRuntime.HasPendingOutboundHelpRequest, TimeSpan.FromSeconds(2));

        await WaitUntilAsync(
            () => !helpeeRuntime.HasPendingOutboundHelpRequest &&
                  string.Equals(helpee.ShareInviteStatusText, "The help request expired.", StringComparison.Ordinal) &&
                  string.IsNullOrWhiteSpace(helpee.InviteHelperIdentityInput) &&
                  !helpee.HasVerifiedInviteHelperIdentity &&
                  !helpee.RequestHelpCommand.CanExecute(null),
            TimeSpan.FromSeconds(3));
        await requestTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(helpeeRuntime.PendingOutboundHelpRequestDecision);
        Assert.False(helpeeRuntime.PendingOutboundHelpRequestDecision!.Accepted);
        Assert.Equal("request_timeout", helpeeRuntime.PendingOutboundHelpRequestDecision.Reason);
        Assert.Equal(string.Empty, helpee.InviteHelperIdentityInput);
        Assert.False(helpee.HasVerifiedInviteHelperIdentity);
        Assert.False(helpee.CanRequestHelpAction);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeePageViewModel_DisablingScreenShare_ClearsAndDisablesRemoteControlApproval()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var hostAddress = CreateTestPeerAddress();
        var helperAddress = $"devlocal.helper.{Guid.NewGuid():N}";
        using var helpeeRuntime = new SessionRuntime(() => new DevLocalTransport(hostAddress));
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport(helperAddress));
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helpeeRuntime);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await helpeeRuntime.StartHelpeeAsync(cts.Token);
        var invite = CreateValidatedInviteForTarget(new PeerAddress(hostAddress), out var rawToken, InviteCapabilities.Chat | InviteCapabilities.ScreenShare | InviteCapabilities.RemoteControl, boundHelperAddress: new PeerAddress(helperAddress));
        await helperRuntime.StartHelperAsync(rawToken, invite, cts.Token);
        await WaitUntilAsync(() => helpee.HasIncomingRequest, TimeSpan.FromSeconds(5));
        Assert.True(helpee.AllowIncomingScreenShareCapability);
        Assert.True(helpee.AllowIncomingRemoteControlCapability);
        Assert.True(helpee.CanAllowIncomingRemoteControlCapability);
        helpee.AllowIncomingScreenShareCapability = false;
        Assert.False(helpee.AllowIncomingRemoteControlCapability);
        Assert.False(helpee.CanAllowIncomingRemoteControlCapability);
        helpee.AllowIncomingRemoteControlCapability = true;
        Assert.False(helpee.AllowIncomingRemoteControlCapability);
        helpee.AllowIncomingScreenShareCapability = true;
        Assert.True(helpee.CanAllowIncomingRemoteControlCapability);
        helpee.AllowIncomingRemoteControlCapability = true;
        Assert.True(helpee.AllowIncomingRemoteControlCapability);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeePageViewModel_NoApprovedCapabilities_DisablesAllowAction()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var hostAddress = CreateTestPeerAddress();
        var helperAddress = $"devlocal.helper.{Guid.NewGuid():N}";
        using var helpeeRuntime = new SessionRuntime(() => new DevLocalTransport(hostAddress));
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport(helperAddress));
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helpeeRuntime);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await helpeeRuntime.StartHelpeeAsync(cts.Token);
        var invite = CreateValidatedInviteForTarget(new PeerAddress(hostAddress), out var rawToken, InviteCapabilities.Chat | InviteCapabilities.ScreenShare | InviteCapabilities.RemoteControl, boundHelperAddress: new PeerAddress(helperAddress));
        await helperRuntime.StartHelperAsync(rawToken, invite, cts.Token);
        await WaitUntilAsync(() => helpee.HasIncomingRequest, TimeSpan.FromSeconds(2));
        Assert.True(helpee.CanAllowIncomingRequestAction);
        Assert.True(helpee.AllowCommand.CanExecute(null));
        helpee.AllowIncomingChatCapability = false;
        helpee.AllowIncomingScreenShareCapability = false;
        helpee.AllowIncomingRemoteControlCapability = false;
        Assert.False(helpee.CanAllowIncomingRequestAction);
        Assert.False(helpee.AllowCommand.CanExecute(null));
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelpeePageViewModel_HeaderStatusText_UsesConnectionStatusOrReady_AndIsNeverEmpty()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helpeeRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helpeeRuntime);
        SetPrivateField(helpee, "connectionState", "Waiting");
        SetPrivateField(helpee, "connectionStatus", "Waiting for helper…");
        Assert.Equal("Waiting for helper…", helpee.HeaderStatusText);
        Assert.False(string.IsNullOrWhiteSpace(helpee.HeaderStatusText));
        SetPrivateField(helpee, "connectionStatus", string.Empty);
        Assert.Equal("Ready", helpee.HeaderStatusText);
        Assert.False(string.IsNullOrWhiteSpace(helpee.HeaderStatusText));
        SetPrivateProperty(helpee, "ConnectionState", "Connected");
        SetPrivateField(helpee, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helpee, "isScreenSharingPreviewActive", false);
        Assert.Equal("Connected", helpee.HeaderStatusText);
        SetPrivateField(helpeeRuntime, "hasPendingRemoteControlConsentPrompt", true);
        SetPrivateField(helpee, "isScreenSharingPreviewActive", true);
        Assert.Equal("Waiting for your approval… • Screen sharing", helpee.HeaderStatusText);
        SetPrivateField(helpeeRuntime, "hasPendingRemoteControlConsentPrompt", false);
        SetPrivateField(helpee, "isScreenSharingPreviewActive", false);
        SetPrivateField(helpeeRuntime, "remoteControlSessionState", RemoteControlSessionState.Default with { ControlState = ControlState.Active });
        Assert.Equal("Waiting for fresh mapping", helpee.HeaderStatusText);
        SetPrivateField(helpeeRuntime, "remoteControlSessionState", RemoteControlSessionState.Default);
        SetPrivateField(helpeeRuntime, "remoteControlStatusHintText", "Authorization expired or revoked");
        Assert.Equal("Authorization expired or revoked", helpee.HeaderStatusText);
        SetPrivateField(helpeeRuntime, "remoteControlStatusHintText", string.Empty);
        SetPrivateField(helpee, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helpee, "isScreenSharingPreviewActive", true);
        Assert.Equal("Connected • Screen sharing", helpee.HeaderStatusText);
        SetPrivateField(helpee, "effectivePhase", SessionUiPhase.Connecting);
        Assert.Equal("Connecting… • Screen sharing", helpee.HeaderStatusText);
        SetPrivateField(helpee, "effectivePhase", SessionUiPhase.Failed);
        SetPrivateField(helpee, "failureTitle", "Connection failed");
        Assert.Equal("Connection failed", helpee.HeaderStatusText);
        SetPrivateField(helpee, "failureTitle", string.Empty);
        SetPrivateField(helpee, "connectionStatus", "The helper ended the session.");
        SetPrivateField(helpee, "effectivePhase", SessionUiPhase.Ended);
        Assert.Equal("The helper ended the session.", helpee.HeaderStatusText);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelpeePageViewModel_RemoteControlAffordances_ClearImmediately_WhenBackendLeavesConnected()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helpeeRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helpeeRuntime);
        SetPrivateProperty(helpee, "ConnectionState", "Connected");
        SetPrivateField(helpee, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helpeeRuntime, "state", SessionRuntimeState.Connected);
        SetPrivateField(helpeeRuntime, "currentFlowSnapshot", BuildHelpeeConnectedFlow(helpeeRuntime));
        SetPrivateField(helpeeRuntime, "hasPendingRemoteControlConsentPrompt", true);
        SetPrivateField(helpee, "isScreenSharingPreviewActive", true);
        SetPrivateProperty(helpee, "ScreenSharePreviewFrame", CreateTestBitmap(1, 1));
        Assert.True(helpee.ShowRemoteControlConsentDialog);
        SetPrivateField(helpeeRuntime, "hasPendingRemoteControlConsentPrompt", false);
        SetPrivateField(helpeeRuntime, "remoteControlSessionState", RemoteControlSessionState.Default with { ControlState = ControlState.Active, SupportsRemoteControl = true, PeerSupportsRemoteControl = true, });
        SetPrivateProperty(helpee, "ScreenSharePreviewFrame", CreateTestBitmap(1, 1));
        Assert.True(helpee.ShowStopControlAction);
        Assert.True(helpee.ShowRemoteControlActiveStatus);
        Assert.True(helpee.ShowRemoteControlPreviewActiveCue);
        SetPrivateField(helpee, "effectivePhase", SessionUiPhase.Failed);
        SetPrivateField(helpeeRuntime, "state", SessionRuntimeState.Failed);
        SetPrivateField(helpeeRuntime, "currentFlowSnapshot", helpeeRuntime.FlowSnapshot with { Phase = SessionFlowPhase.Failed, UiPhase = SessionUiPhase.Failed, Role = SessionRuntimeRole.Helpee, RuntimeState = SessionRuntimeState.Failed, DisplayStatusText = "Connection lost.", DisplayConnectionState = "Failed", TerminalKind = SessionTerminalKind.Failed, TerminalStatusText = "Connection lost.", });
        SetPrivateField(helpeeRuntime, "hasPendingRemoteControlConsentPrompt", true);
        Assert.False(helpee.ShowRemoteControlConsentDialog);
        Assert.False(helpee.ShowStopControlAction);
        Assert.False(helpee.CanStopControl);
        Assert.False(helpee.ShowRemoteControlActiveStatus);
        Assert.False(helpee.ShowRemoteControlPreviewActiveCue);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelpeePageViewModel_ToggleScreenSharePreviewCommand_CanExecute_FollowsPreviewState()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var transport = new DevLocalTransport();
        using var runtime = new SessionRuntime(() => transport);
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, runtime, screenCaptureSourceFactory: new FixedCaptureSourceFactory(new FakeScreenCaptureSource()));
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "transportState", TransportState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "ApplyTransportSecurityState", CreateApprovedSecurityState(new PeerAddress(transport.LocalPeerAddress), new PeerAddress("helpee.screenshare.helper"), CapabilityGrant.ScreenShare));
        Assert.True(helpee.ToggleScreenSharePreviewCommand.CanExecute(null));
        SetPrivateProperty(helpee, "ScreenSharePreviewStatus", new ScreenShareStatus(ScreenShareState.Starting, null, DateTimeOffset.UtcNow));
        Assert.False(helpee.ToggleScreenSharePreviewCommand.CanExecute(null));
        SetPrivateProperty(helpee, "ScreenSharePreviewStatus", new ScreenShareStatus(ScreenShareState.Active, null, DateTimeOffset.UtcNow));
        SetPrivateProperty(helpee, "IsScreenSharingPreviewActive", true);
        Assert.True(helpee.ToggleScreenSharePreviewCommand.CanExecute(null));
        SetPrivateProperty(helpee, "IsScreenSharingPreviewActive", false);
        SetPrivateProperty(helpee, "ScreenSharePreviewStatus", new ScreenShareStatus(ScreenShareState.Off, null, DateTimeOffset.UtcNow));
        Assert.True(helpee.ToggleScreenSharePreviewCommand.CanExecute(null));
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelpeePageViewModel_PersistedUnsupportedCaptureTarget_LoadsPrimaryDisplaySelection()
    {
        using var captureTargetOverride = new EnvironmentOverride("NLINK_FEATURE_SCREENCAP_TARGET", "{\"mode\":\"Window\",\"windowId\":\"ABC123\"}");
        var transportConfig = CreateDevLocalTestConfig();
        using var runtime = new SessionRuntime(() => new DevLocalTransport());
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, runtime, screenCaptureSourceFactory: new FixedCaptureSourceFactory(new FakeScreenCaptureSource()));
        Assert.NotEmpty(helpee.AvailableCaptureDisplays);
        Assert.NotNull(helpee.SelectedCaptureDisplay);
        Assert.True(helpee.SelectedCaptureDisplay!.IsPrimaryDisplay);
        Assert.Equal("Primary display", helpee.SelectedCaptureDisplay.Label);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeePageViewModel_ShareStart_PersistsSelectedDisplayTarget()
    {
        using var captureTargetOverride = new EnvironmentOverride("NLINK_FEATURE_SCREENCAP_TARGET", string.Empty);
        var transportConfig = CreateDevLocalTestConfig();
        using var transport = new DevLocalTransport();
        var captureSource = new FakeScreenCaptureSource();
        using var runtime = new SessionRuntime(() => transport);
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, runtime, screenCaptureSourceFactory: new FixedCaptureSourceFactory(captureSource));
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "transportState", TransportState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "ApplyTransportSecurityState", CreateApprovedSecurityState(new PeerAddress(transport.LocalPeerAddress), new PeerAddress("helpee.screenshare.helper"), CapabilityGrant.ScreenShare));
        SetPrivateProperty(helpee, "ConnectionState", "Connected");
        SetPrivateField(helpee, "effectivePhase", SessionUiPhase.Connected);
        var specificDisplay = helpee.AvailableCaptureDisplays.FirstOrDefault(option => !option.IsPrimaryDisplay);
        Assert.NotNull(specificDisplay);
        helpee.SelectedCaptureDisplay = specificDisplay;
        Assert.Null(Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_TARGET"));
        helpee.ToggleScreenSharePreviewCommand.Execute(null);
        await WaitUntilAsync(() => captureSource.IsStarted, TimeSpan.FromSeconds(2));
        var persisted = Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_TARGET");
        Assert.NotNull(persisted);
        using var document = JsonDocument.Parse(persisted!);
        Assert.Equal("Display", document.RootElement.GetProperty("mode").GetString());
        Assert.Equal(specificDisplay!.DisplayId, document.RootElement.GetProperty("displayId").GetString());
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeePageViewModel_StopScreenShareWhileConsentPending_ClearsApprovalUiImmediately()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var transport = new DevLocalTransport();
        using var runtime = new SessionRuntime(() => transport);
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, runtime, screenCaptureSourceFactory: new FixedCaptureSourceFactory(new FakeScreenCaptureSource()));
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "transportState", TransportState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        SetPrivateField(runtime, "currentFlowSnapshot", runtime.FlowSnapshot with { Phase = SessionFlowPhase.ActiveSession, UiPhase = SessionUiPhase.Connected, Role = SessionRuntimeRole.Helpee, RuntimeState = SessionRuntimeState.Connected, DisplayStatusText = "Connected", DisplayConnectionState = "Connected", });
        SetPrivateField(runtime, "remoteControlSessionState", new RemoteControlSessionState(ControlState.Requesting, ControllerPeerId: "helper-peer", CurrentControlRequestId: "req-preview-stop", ConsentToken: null, SupportsRemoteControl: true, PeerSupportsRemoteControl: true));
        SetPrivateField(runtime, "hasPendingRemoteControlConsentPrompt", true);
        SetPrivateProperty(helpee, "ConnectionState", "Connected");
        SetPrivateField(helpee, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateProperty(helpee, "IsScreenSharingPreviewActive", true);
        Assert.True(helpee.ShowRemoteControlConsentDialog);
        Assert.Equal("Waiting for your approval… • Screen sharing", helpee.HeaderStatusText);
        SetPrivateProperty(helpee, "IsScreenSharingPreviewActive", false);
        Assert.False(helpee.ShowRemoteControlConsentDialog);
        Assert.Equal("Connected", helpee.HeaderStatusText);
        await WaitUntilAsync(() => !helpee.ShowRemoteControlConsentDialog && helpee.HeaderStatusText == "Connected", TimeSpan.FromSeconds(1));
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeePageViewModel_AllowControlConsentCommand_SendsAllowResponse()
    {
        var sentResponses = new List<ControlResponseMessageV1>();
        var transportConfig = CreateDevLocalTestConfig();
        const string controllerPeerId = "helper-peer";
        using var scripted = new ScriptedSignalingTransport(onSendControlResponseAsync: (message, _) =>
        {
            sentResponses.Add(message);
            return Task.CompletedTask;
        });
        using var runtime = new SessionRuntime(() => scripted);
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, runtime, screenCaptureSourceFactory: new FixedCaptureSourceFactory(new FakeScreenCaptureSource()));
        runtime.SetRoleForTests(SessionRuntimeRole.Helpee);
        SetPrivateField(runtime, "transport", scripted);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "transportState", TransportState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", scripted);
        InvokePrivateMethod(runtime, "ApplyTransportSecurityState", CreateApprovedSecurityState(new PeerAddress(scripted.LocalPeerAddress), new PeerAddress(controllerPeerId)));
        InvokePrivateMethod(runtime, "RefreshRemoteControlCapabilitiesFromTransport");
        SetPrivateField(runtime, "currentFlowSnapshot", runtime.FlowSnapshot with { Phase = SessionFlowPhase.ActiveSession, UiPhase = SessionUiPhase.Connected, Role = SessionRuntimeRole.Helpee, RuntimeState = SessionRuntimeState.Connected, TransportState = TransportState.Connected, ApprovalActive = true, ApprovedCapabilities = CapabilityGrant.Chat | CapabilityGrant.ScreenShare | CapabilityGrant.RemoteControl, DisplayStatusText = "Connected", DisplayConnectionState = "Connected", });
        SetPrivateProperty(helpee, "ConnectionState", "Connected");
        SetPrivateField(helpee, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helpee, "isScreenSharingPreviewActive", true);
        scripted.InjectIncomingControlRequest(new ControlRequestMessageV1 { RequestId = "req-allow-consent", Caps = new[] { "mouse", "keyboard" }, }, controllerPeerId);
        await WaitUntilAsync(() => runtime.ControlState == ControlState.Requesting && runtime.HasPendingRemoteControlConsentPrompt && helpee.ShowRemoteControlConsentDialog, TimeSpan.FromSeconds(1));
        var pendingControlState = Assert.IsType<RemoteControlSessionState>(GetPrivateField(runtime, "remoteControlSessionState"));
        if (string.IsNullOrWhiteSpace(pendingControlState.ControllerPeerId))
        {
            SetPrivateField(runtime, "remoteControlSessionState", pendingControlState with { ControllerPeerId = controllerPeerId, });
        }

        Assert.Equal(controllerPeerId, Assert.IsType<RemoteControlSessionState>(GetPrivateField(runtime, "remoteControlSessionState")).ControllerPeerId);
        Assert.True(helpee.ShowRemoteControlConsentDialog);
        Assert.True(helpee.AllowControlConsentCommand.CanExecute(null));
        helpee.AllowIncomingScreenShareCapability = true;
        helpee.AllowIncomingRemoteControlCapability = true;
        await helpee.AllowControlConsentCommand.ExecuteAsync(null);
        var allowResponse = Assert.Single(sentResponses.Where(message => string.Equals(message.RequestId, "req-allow-consent", StringComparison.Ordinal) && string.Equals(message.Decision, "allow", StringComparison.Ordinal)));
        Assert.False(string.IsNullOrWhiteSpace(allowResponse.ConsentToken));
        Assert.False(helpee.ShowRemoteControlConsentFeedback);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeePageViewModel_DenyControlConsentCommand_SendsDenyResponse()
    {
        ControlResponseMessageV1? sentResponse = null;
        var transportConfig = CreateDevLocalTestConfig();
        using var scripted = new ScriptedSignalingTransport(onSendControlResponseAsync: (message, _) =>
        {
            sentResponse = message;
            return Task.CompletedTask;
        });
        using var runtime = new SessionRuntime(() => scripted);
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, runtime, screenCaptureSourceFactory: new FixedCaptureSourceFactory(new FakeScreenCaptureSource()));
        runtime.SetRoleForTests(SessionRuntimeRole.Helpee);
        SetPrivateField(runtime, "transport", scripted);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "transportState", TransportState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", scripted);
        scripted.SetSessionSecurityStateForTests(CreateApprovedSecurityState(new PeerAddress(scripted.LocalPeerAddress), new PeerAddress("helper-peer")));
        InvokePrivateMethod(runtime, "RefreshRemoteControlCapabilitiesFromTransport");
        SetPrivateField(runtime, "currentFlowSnapshot", runtime.FlowSnapshot with { Phase = SessionFlowPhase.ActiveSession, UiPhase = SessionUiPhase.Connected, Role = SessionRuntimeRole.Helpee, RuntimeState = SessionRuntimeState.Connected, TransportState = TransportState.Connected, ApprovalActive = true, ApprovedCapabilities = CapabilityGrant.Chat | CapabilityGrant.ScreenShare | CapabilityGrant.RemoteControl, DisplayStatusText = "Connected", DisplayConnectionState = "Connected", });
        SetPrivateProperty(helpee, "ConnectionState", "Connected");
        SetPrivateField(helpee, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helpee, "isScreenSharingPreviewActive", true);
        scripted.InjectIncomingControlRequest(new ControlRequestMessageV1 { RequestId = "req-deny-consent", Caps = new[] { "mouse", "keyboard" }, }, "helper-peer");
        await WaitUntilAsync(() => runtime.ControlState == ControlState.Requesting && runtime.HasPendingRemoteControlConsentPrompt && helpee.ShowRemoteControlConsentDialog, TimeSpan.FromSeconds(1));
        Assert.True(helpee.ShowRemoteControlConsentDialog);
        Assert.True(helpee.DenyControlConsentCommand.CanExecute(null));
        await helpee.DenyControlConsentCommand.ExecuteAsync(null);
        Assert.NotNull(sentResponse);
        Assert.Equal("req-deny-consent", sentResponse!.RequestId);
        Assert.Equal("deny", sentResponse.Decision);
        Assert.Equal("helpee_denied", sentResponse.Reason);
        Assert.False(helpee.ShowRemoteControlConsentFeedback);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeePageViewModel_StopScreenShareWhileControlActive_ClearsActiveUiImmediately()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var transport = new DevLocalTransport();
        using var runtime = new SessionRuntime(() => transport);
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, runtime, screenCaptureSourceFactory: new FixedCaptureSourceFactory(new FakeScreenCaptureSource()));
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "transportState", TransportState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        SetPrivateField(runtime, "currentFlowSnapshot", runtime.FlowSnapshot with { Phase = SessionFlowPhase.ActiveSession, UiPhase = SessionUiPhase.Connected, Role = SessionRuntimeRole.Helpee, RuntimeState = SessionRuntimeState.Connected, DisplayStatusText = "Connected", DisplayConnectionState = "Connected", });
        SetPrivateField(runtime, "remoteControlSessionState", new RemoteControlSessionState(ControlState.Active, ControllerPeerId: "helper-peer", CurrentControlRequestId: "req-preview-stop-active", ConsentToken: null, SupportsRemoteControl: true, PeerSupportsRemoteControl: true));
        SetPrivateProperty(helpee, "ConnectionState", "Connected");
        SetPrivateField(helpee, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateProperty(helpee, "ScreenSharePreviewFrame", CreateTestBitmap(1, 1));
        SetPrivateProperty(helpee, "IsScreenSharingPreviewActive", true);
        Assert.True(helpee.ShowRemoteControlActiveStatus);
        Assert.True(helpee.ShowStopControlAction);
        Assert.Contains("Screen sharing", helpee.HeaderStatusText, StringComparison.Ordinal);
        SetPrivateProperty(helpee, "IsScreenSharingPreviewActive", false);
        Assert.DoesNotContain("Screen sharing", helpee.HeaderStatusText, StringComparison.Ordinal);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeePageViewModel_ScreenShareStopped_ClearsLocalPreview()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var transport = new DevLocalTransport();
        using var runtime = new SessionRuntime(() => transport);
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, runtime, screenCaptureSourceFactory: new FixedCaptureSourceFactory(new FakeScreenCaptureSource()));
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "transportState", TransportState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        SetPrivateProperty(helpee, "ScreenSharePreviewStatus", new ScreenShareStatus(ScreenShareState.Active, null, DateTimeOffset.UtcNow));
        SetPrivateProperty(helpee, "ScreenSharePreviewFrame", CreateTestBitmap(2, 1));
        SetPrivateProperty(helpee, "IsScreenSharingPreviewActive", true);
        Assert.True(helpee.ShowScreenSharePreviewFrame);
        InvokePrivateMethod(helpee, "OnScreenShareStopped", runtime, EventArgs.Empty);
        await WaitUntilAsync(() => !helpee.ShowScreenSharePreviewFrame && helpee.ScreenSharePreviewFrame is null && !helpee.IsScreenSharingPreviewActive, TimeSpan.FromSeconds(1));
        Assert.Equal(ScreenShareState.Off, helpee.ScreenSharePreviewStatus.State);
        Assert.DoesNotContain("Screen sharing", helpee.HeaderStatusText, StringComparison.Ordinal);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeePageViewModel_ScreenShareStartFailure_ShowsHeaderStatus_AndRemainsInactive()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var transport = new DevLocalTransport();
        using var runtime = new SessionRuntime(() => transport);
        var failingSource = new FakeScreenCaptureSource
        {
            StartException = new InvalidOperationException("capture init failed"),
        };
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, runtime, screenCaptureSourceFactory: new FixedCaptureSourceFactory(failingSource));
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "transportState", TransportState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "ApplyTransportSecurityState", CreateApprovedSecurityState(new PeerAddress(transport.LocalPeerAddress), new PeerAddress("helpee.screenshare.helper"), CapabilityGrant.ScreenShare));
        SetPrivateField(helpee, "connectionState", "Connected");
        SetPrivateField(helpee, "effectivePhase", SessionUiPhase.Connected);
        helpee.ToggleScreenSharePreviewCommand.Execute(null);
        await WaitUntilAsync(() => helpee.ScreenSharePreviewStatus.State == ScreenShareState.Failed, TimeSpan.FromSeconds(2));
        Assert.False(helpee.IsScreenSharingPreviewActive);
        Assert.False(helpee.ShowScreenSharePreviewFrame);
        Assert.True(helpee.ShowScreenShareViewerError);
        Assert.Equal("Screen sharing failed to start", helpee.ScreenShareViewerMessage);
        Assert.Equal("Connected • Screen sharing failed to start", helpee.HeaderStatusText);
        Assert.True(helpee.ToggleScreenSharePreviewCommand.CanExecute(null));
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelpeePageViewModel_TransientStatusPanel_HidesWhenItDuplicatesHeader()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helpeeRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helpeeRuntime);
        SetPrivateField(helpee, "connectionStatus", "Waiting for helper…");
        SetPrivateField(helpee, "showTransientBanner", true);
        SetPrivateField(helpee, "transientBannerText", "Waiting for helper…");
        Assert.False(helpee.ShowTransientStatusPanel);
        SetPrivateField(helpee, "transientBannerText", "Recovering session");
        Assert.True(helpee.ShowTransientStatusPanel);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelpeePageViewModel_PreviewFrames_DoNotReRaiseShellVisibility_WhenVisibilityStaysTrue()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helpeeRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helpeeRuntime);
        var visibilityChangedCount = 0;
        helpee.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HelpeePageViewModel.ShowScreenSharePreviewFrame))
            {
                visibilityChangedCount++;
            }
        };
        SetPrivateField(helpee, "screenSharePreviewStatus", new ScreenShareStatus(ScreenShareState.Active, null, DateTimeOffset.UtcNow));
        SetPrivateProperty(helpee, "ScreenSharePreviewFrame", CreateTestBitmap(1, 1));
        SetPrivateProperty(helpee, "ScreenSharePreviewFrame", CreateTestBitmap(2, 1));
        Assert.True(helpee.ShowScreenSharePreviewFrame);
        Assert.Equal(1, visibilityChangedCount);
        SetPrivateProperty(helpee, "ScreenSharePreviewFrame", null);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelpeePageViewModel_CanEndSession_IsTrueOnlyForConnectedConnectingOrRecoveringPhases()
    {
        Assert.True(InvokeCanEndForPhase(typeof(HelpeePageViewModel), SessionUiPhase.Connected));
        Assert.False(InvokeCanEndForPhase(typeof(HelpeePageViewModel), SessionUiPhase.Failed));
        Assert.False(InvokeCanEndForPhase(typeof(HelpeePageViewModel), SessionUiPhase.Ended));
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeePageViewModel_EndSession_StopsPreviewCapture_AndClearsPreviewState()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var runtime = new SessionRuntime(() => new DevLocalTransport());
        var fakeSource = new FakeScreenCaptureSource();
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, runtime, screenCaptureSourceFactory: new FixedCaptureSourceFactory(fakeSource));
        using var previewCts = new CancellationTokenSource();
        var coordinatorField = typeof(HelpeePageViewModel).GetField("screenShareCoordinator", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(coordinatorField);
        var coordinator = coordinatorField!.GetValue(helpee);
        Assert.NotNull(coordinator);
        SetPrivateField(helpee, "connectionState", "Connected");
        SetPrivateField(helpee, "wasConnected", true);
        SetPrivateField(helpee, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helpee, "isScreenSharingPreviewActive", true);
        SetPrivateField(helpee, "screenSharePreviewStatus", new ScreenShareStatus(ScreenShareState.Active, null, DateTimeOffset.UtcNow));
        SetPrivateFieldDynamic(coordinator!, "screenSharePreviewCaptureSource", fakeSource);
        SetPrivateFieldDynamic(coordinator!, "screenSharePreviewCts", previewCts);
        Assert.Equal("Connected • Screen sharing", helpee.HeaderStatusText);
        helpee.EndSessionCommand.Execute(null);
        await WaitUntilAsync(() => !helpee.IsScreenSharingPreviewActive && helpee.ScreenSharePreviewStatus.State == ScreenShareState.Off, TimeSpan.FromSeconds(2));
        Assert.True(fakeSource.StopCallCount >= 1);
        Assert.True(fakeSource.DisposeCallCount >= 1);
        Assert.False(fakeSource.IsStarted);
        Assert.False(helpee.CanEndSession);
        Assert.DoesNotContain("Screen sharing", helpee.HeaderStatusText, StringComparison.Ordinal);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelpeePageViewModel_EndSession_DoesNotInvokeCancelAction()
    {
        var cancelInvoked = false;
        using var helpeeRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helpee = new HelpeePageViewModel(cancelAction: () => cancelInvoked = true, CreateDevLocalTestConfig(), helpeeRuntime);
        SetPrivateField(helpee, "connectionState", "Connected");
        SetPrivateField(helpee, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helpee, "wasConnected", true);
        SetPrivateField(helpee, "canEndSession", true);
        helpee.EndSessionCommand.Execute(null);
        Assert.False(cancelInvoked);
        Assert.True(helpee.ShowWaitingPanel);
        Assert.False(helpee.ShowConnectedPanel);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeePageViewModel_EndSession_DoesNotBlockUi_WhenPreviewStopIsSlow()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var runtime = new SessionRuntime(() => new DevLocalTransport());
        var fakeSource = new FakeScreenCaptureSource
        {
            StopBlocker = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, runtime, screenCaptureSourceFactory: new FixedCaptureSourceFactory(fakeSource));
        using var previewCts = new CancellationTokenSource();
        var coordinatorField = typeof(HelpeePageViewModel).GetField("screenShareCoordinator", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(coordinatorField);
        var coordinator = coordinatorField!.GetValue(helpee);
        Assert.NotNull(coordinator);
        SetPrivateField(helpee, "connectionState", "Connected");
        SetPrivateField(helpee, "wasConnected", true);
        SetPrivateField(helpee, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helpee, "isScreenSharingPreviewActive", true);
        SetPrivateField(helpee, "screenSharePreviewStatus", new ScreenShareStatus(ScreenShareState.Active, null, DateTimeOffset.UtcNow));
        SetPrivateFieldDynamic(coordinator!, "screenSharePreviewCaptureSource", fakeSource);
        SetPrivateFieldDynamic(coordinator!, "screenSharePreviewCts", previewCts);
        Assert.Equal("Connected • Screen sharing", helpee.HeaderStatusText);
        var stopwatch = Stopwatch.StartNew();
        helpee.EndSessionCommand.Execute(null);
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"EndSession command blocked for {stopwatch.Elapsed}.");
        fakeSource.StopBlocker.TrySetResult(true);
        await WaitUntilAsync(() => !helpee.IsScreenSharingPreviewActive && helpee.ScreenSharePreviewStatus.State == ScreenShareState.Off, TimeSpan.FromSeconds(2));
        Assert.DoesNotContain("Screen sharing", helpee.HeaderStatusText, StringComparison.Ordinal);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelpeePageViewModel_Dispose_StopsPreviewCapture_BeforeReturning()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var runtime = new SessionRuntime(() => new DevLocalTransport());
        var fakeSource = new FakeScreenCaptureSource();
        using var previewCts = new CancellationTokenSource();
        var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, runtime, screenCaptureSourceFactory: new FixedCaptureSourceFactory(fakeSource));
        var coordinatorField = typeof(HelpeePageViewModel).GetField("screenShareCoordinator", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(coordinatorField);
        var coordinator = coordinatorField!.GetValue(helpee);
        Assert.NotNull(coordinator);
        var disposeCountBeforeDispose = fakeSource.DisposeCallCount;
        var stopCountBeforeDispose = fakeSource.StopCallCount;
        SetPrivateField(helpee, "isScreenSharingPreviewActive", true);
        SetPrivateField(helpee, "screenSharePreviewStatus", new ScreenShareStatus(ScreenShareState.Active, null, DateTimeOffset.UtcNow));
        SetPrivateFieldDynamic(coordinator!, "screenSharePreviewCaptureSource", fakeSource);
        SetPrivateFieldDynamic(coordinator!, "screenSharePreviewCts", previewCts);
        helpee.Dispose();
        Assert.True(previewCts.IsCancellationRequested);
        Assert.Equal(stopCountBeforeDispose + 1, fakeSource.StopCallCount);
        Assert.Equal(disposeCountBeforeDispose + 1, fakeSource.DisposeCallCount);
        Assert.False(fakeSource.IsStarted);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeePageViewModel_SendChatFailure_RestoresDraft_AndKeepsSessionConnected()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var scripted = new ScriptedSignalingTransport(onSendChatAsync: static (_, _) => throw new TimeoutException("Ack was not received."));
        using var runtime = new SessionRuntime(() => scripted);
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, runtime);
        SetPrivateField(runtime, "transport", scripted);
        SetPrivateField(runtime, "role", SessionRuntimeRole.Helpee);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        var chatServiceField = typeof(SessionRuntime).GetField("chatService", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(chatServiceField);
        var chatService = chatServiceField!.GetValue(runtime);
        Assert.NotNull(chatService);
        SetPrivateFieldDynamic(chatService!, "transport", scripted);
        SetPrivateFieldDynamic(chatService!, "sessionKey", Enumerable.Repeat((byte)7, 32).ToArray());
        SetPrivateFieldDynamic(chatService!, "isApproved", true);
        SetPrivateField(helpee, "isChatInputEnabled", true);
        SetPrivateField(helpee, "connectionState", "Connected");
        SetPrivateField(helpee, "connectionStatus", "Connected");
        helpee.ChatDraft = "hello during screenshare";
        var sendTask = Assert.IsAssignableFrom<Task>(InvokePrivateMethod(helpee, "SendChatAsync"));
        await sendTask;
        Assert.Equal("hello during screenshare", helpee.ChatDraft);
        Assert.Empty(helpee.ChatMessages);
        Assert.Equal(SessionRuntimeState.Connected, runtime.State);
        Assert.Equal("Connected", runtime.StatusText);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeePageViewModel_EndedPhase_DisablesEndSession()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helpeeRuntime = new SessionRuntime(() => new DevLocalTransport());
        var uiStateStore = new SessionUiStateStore();
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helpeeRuntime, uiStateStore: uiStateStore);
        uiStateStore.SetPhase(SessionUiPhase.Ended, "test");
        await WaitUntilAsync(() => !helpee.CanEndSession, TimeSpan.FromSeconds(1));
        Assert.False(helpee.CanEndSession);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeeViewModel_PrepareForWindowClose_ConnectedNknSession_NotifiesHelperRemoteEnd()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            var options = NknTransportOptions.Load();
            using var helpeeTransport = new NknSignalingTransport(new FakeNknClient("helpee.windowclose.addr." + Guid.NewGuid().ToString("N")), options, new NknIdentity("helpee-windowclose-test", "helpee.windowclose.test.fake"));
            using var helperTransport = new NknSignalingTransport(new FakeNknClient("helper.windowclose.addr." + Guid.NewGuid().ToString("N")), options, new NknIdentity("helper-windowclose-test", "helper.windowclose.test.fake"));
            using var helpeeRuntime = new SessionRuntime(() => helpeeTransport);
            using var helperRuntime = new SessionRuntime(() => helperTransport);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var helpee = new HelpeePageViewModel(cancelAction: static () =>
            {
            }, CreateNknTestConfig(), helpeeRuntime);
            _ = await WaitForShareInviteAsync(helpee);
            await WaitUntilAsync(() => helpeeRuntime.CurrentLocalPeerAddress is not null, TimeSpan.FromSeconds(3));
            var invite = CreateValidatedInviteForTarget(GetHostedAddressOrThrow(helpeeRuntime), out var rawToken);
            var connectTask = helperRuntime.StartHelperAsync(rawToken, invite, cts.Token);
            await WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.IncomingJoinRequest, TimeSpan.FromSeconds(3));
            using var helper = new HelperPageViewModel(cancelAction: static () =>
            {
            }, CreateNknTestConfig(), helperRuntime);
            helpee.AllowCommand.Execute(null);
            await connectTask;
            await WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.Connected && helperRuntime.State == SessionRuntimeState.Connected && helper.EffectivePhase == SessionUiPhase.Connected, TimeSpan.FromSeconds(3));
            await helpee.PrepareForWindowCloseAsync();
            await WaitUntilAsync(() => helperRuntime.LastDisconnectWasRemoteEnd && string.Equals(helper.HeaderStatusText, "The other person ended the session.", StringComparison.Ordinal) && !helper.IsChatInputEnabled, TimeSpan.FromSeconds(5));
            Assert.Equal("The other person ended the session.", helper.HeaderStatusText);
            Assert.False(helper.IsChatInputEnabled);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeePageViewModel_EndSession_WithPreviewAndRemoteControlActive_NotifiesHelperRemoteEnd()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            var options = NknTransportOptions.Load();
            using var helpeeTransport = new NknSignalingTransport(new FakeNknClient("helpee.endsession.control.addr." + Guid.NewGuid().ToString("N")), options, new NknIdentity("helpee-endsession-control-test", "helpee.endsession.control.test.fake"));
            using var helperTransport = new NknSignalingTransport(new FakeNknClient("helper.endsession.control.addr." + Guid.NewGuid().ToString("N")), options, new NknIdentity("helper-endsession-control-test", "helper.endsession.control.test.fake"));
            using var helpeeRuntime = new SessionRuntime(() => helpeeTransport);
            using var helperRuntime = new SessionRuntime(() => helperTransport);
            var fakeSource = new FakeScreenCaptureSource();
            Task? helpeeDisconnectTask = null;
            using var helpee = new HelpeePageViewModel(cancelAction: () => helpeeDisconnectTask = helpeeRuntime.DisconnectAsync(), CreateNknTestConfig(), helpeeRuntime, screenCaptureSourceFactory: new FixedCaptureSourceFactory(fakeSource));
            using var previewCts = new CancellationTokenSource();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => helpeeRuntime.CurrentLocalPeerAddress is not null, TimeSpan.FromSeconds(3));
            var invite = CreateValidatedInviteForTarget(GetHostedAddressOrThrow(helpeeRuntime), out var rawToken);
            var connectTask = helperRuntime.StartHelperAsync(rawToken, invite, cts.Token);
            await WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.IncomingJoinRequest, TimeSpan.FromSeconds(3));
            using var helper = new HelperPageViewModel(cancelAction: static () =>
            {
            }, CreateNknTestConfig(), helperRuntime);
            helpee.AllowCommand.Execute(null);
            await connectTask;
            await WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.Connected && helperRuntime.State == SessionRuntimeState.Connected && helpee.EffectivePhase == SessionUiPhase.Connected && helper.EffectivePhase == SessionUiPhase.Connected, TimeSpan.FromSeconds(3));
            var coordinatorField = typeof(HelpeePageViewModel).GetField("screenShareCoordinator", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(coordinatorField);
            var coordinator = coordinatorField!.GetValue(helpee);
            Assert.NotNull(coordinator);
            SetPrivateField(helpee, "isScreenSharingPreviewActive", true);
            SetPrivateField(helpee, "screenSharePreviewStatus", new ScreenShareStatus(ScreenShareState.Active, null, DateTimeOffset.UtcNow));
            SetPrivateFieldDynamic(coordinator!, "screenSharePreviewCaptureSource", fakeSource);
            SetPrivateFieldDynamic(coordinator!, "screenSharePreviewCts", previewCts);
            SetPrivateField(helper.ScreenShareViewer, "isActive", true);
            SetPrivateField(helper.ScreenShareViewer, "currentFrame", CreateTestBitmap(1, 1));
            InvokePrivateMethod(helper, "OnScreenShareViewerPropertyChanged", helper.ScreenShareViewer, new PropertyChangedEventArgs(nameof(ScreenShareViewerViewModel.CurrentFrame)));
            var requested = await helperRuntime.RequestRemoteControlAsync(cts.Token);
            Assert.True(requested);
            await WaitUntilAsync(() => helpeeRuntime.HasPendingRemoteControlConsentPrompt, TimeSpan.FromSeconds(3));
            Assert.True(await helpeeRuntime.RespondToRemoteControlRequestAsync(allow: true, cts.Token));
            await WaitUntilAsync(() => helpeeRuntime.ControlState == ControlState.Active && helperRuntime.ControlState == ControlState.Active, TimeSpan.FromSeconds(3));
            Assert.True(helper.ShowRemoteScreenShareFrame);
            Assert.True(helper.ShowStopControlAction);
            Assert.True(helper.ShowRemoteControlActiveStatus);
            helpee.EndSessionCommand.Execute(null);
            if (helpeeDisconnectTask is not null)
            {
                await helpeeDisconnectTask;
            }

            await WaitUntilAsync(() => !helpee.IsScreenSharingPreviewActive && helpee.ScreenSharePreviewStatus.State == ScreenShareState.Off, TimeSpan.FromSeconds(2));
            SetPrivateField(helperRuntime, "state", SessionRuntimeState.Disconnected);
            SetPrivateField(helperRuntime, "currentFlowSnapshot", BuildHelperPeerEndedFlow(helperRuntime, "The other person ended the session."));
            InvokePrivateMethod(helper, "OnDisconnected", helperRuntime, EventArgs.Empty);
            await WaitUntilAsync(() => !helper.ShowRemoteScreenShareFrame && !helper.ShowStopControlAction && !helper.ShowRemoteControlActiveStatus && !helper.IsChatInputEnabled && string.Equals(helper.TransientBannerText, "The other person ended the session.", StringComparison.Ordinal), TimeSpan.FromSeconds(2));
            Assert.True(fakeSource.StopCallCount >= 1);
            Assert.True(fakeSource.DisposeCallCount >= 1);
            Assert.False(helper.IsChatInputEnabled);
            Assert.Equal("The other person ended the session.", helper.HeaderStatusText);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelpeeViewModel_RemoteEndAfterConnectedSession_ClearsHelperIdentityInput()
    {
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport());
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, CreateNknTestConfig(), runtime);
        var helperIdentity = new PeerAddress("nlink-helper.connected.identity.clear");
        var helperTarget = new PeerAddress("nlink-helper.connected.target.clear");
        helpee.SetVerifiedInviteHelperIdentity(helperIdentity, helperTargetAddress: helperTarget, refreshInvite: false, normalizedInputOverride: HelperBootstrapQrPayload.Format(HelperBootstrapPayload.Create(helperTarget, helperId: HelperIdentityTokenCodec.Encode(helperIdentity))));
        SetPrivateField(runtime, "state", SessionRuntimeState.Waiting);
        SetPrivateField(runtime, "currentFlowSnapshot", BuildHelpeeWaitingFlow(runtime));
        Assert.False(string.IsNullOrWhiteSpace(helpee.InviteHelperIdentityInput));
        Assert.True(helpee.HasVerifiedInviteHelperIdentity);
        InvokePrivateMethod(helpee, "RestartWaitingSession", false, false, null);
        Assert.Equal(string.Empty, helpee.InviteHelperIdentityInput);
        Assert.False(helpee.HasVerifiedInviteHelperIdentity);
        Assert.Equal("Waiting for helper…", helpee.ConnectionStatus);
        Assert.Equal("Waiting", helpee.ConnectionState);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeeViewModel_DisconnectAfterConnected_AutoRegeneratesCode()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-auto-rehost-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-auto-rehost-" + Guid.NewGuid().ToString("N")));
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helpeeRuntime);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var initialInvite = await WaitForShareInviteAsync(helpee);
        await helperRuntime.StartHelperAsync(GetHostedAddressOrThrow(helpeeRuntime), cts.Token);
        await WaitUntilAsync(() => helpee.IsIncomingRequestView, TimeSpan.FromSeconds(2));
        helpee.AllowCommand.Execute(null);
        await WaitUntilAsync(() => helpee.IsConnectedView && helpeeRuntime.State == SessionRuntimeState.Connected && helperRuntime.State == SessionRuntimeState.Connected, TimeSpan.FromSeconds(2));
        await helperRuntime.DisconnectAsync();
        string latestInvite = initialInvite;
        await WaitUntilAsync(() =>
        {
            latestInvite = helpee.ShareInvite;
            return helpee.ShowWaitingPanel && !helpee.IsConnectedView && !string.IsNullOrWhiteSpace(latestInvite) && !string.Equals(latestInvite, initialInvite, StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(8));
        Assert.NotEqual(initialInvite, latestInvite);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeeViewModel_DisconnectAfterConnected_AutoRegeneratesCode_OnceAndStaysStable()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-auto-rehost-stable-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-auto-rehost-stable-" + Guid.NewGuid().ToString("N")));
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helpeeRuntime);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var initialInvite = await WaitForShareInviteAsync(helpee);
        await helperRuntime.StartHelperAsync(GetHostedAddressOrThrow(helpeeRuntime), cts.Token);
        await WaitUntilAsync(() => helpee.IsIncomingRequestView, TimeSpan.FromSeconds(2));
        helpee.AllowCommand.Execute(null);
        await WaitUntilAsync(() => helpee.IsConnectedView && helpeeRuntime.State == SessionRuntimeState.Connected && helperRuntime.State == SessionRuntimeState.Connected, TimeSpan.FromSeconds(2));
        await helperRuntime.DisconnectAsync();
        string rotatedInvite = initialInvite;
        await WaitUntilAsync(() =>
        {
            rotatedInvite = helpee.ShareInvite;
            return helpee.ShowWaitingPanel && !helpee.IsConnectedView && !string.IsNullOrWhiteSpace(rotatedInvite) && !string.Equals(rotatedInvite, initialInvite, StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(8));
        string? lastInvite = null;
        var stableSampleCount = 0;
        await WaitUntilAsync(() =>
        {
            var currentInvite = helpee.ShareInvite;
            if (string.IsNullOrWhiteSpace(currentInvite) || string.Equals(currentInvite, initialInvite, StringComparison.Ordinal))
            {
                lastInvite = null;
                stableSampleCount = 0;
                return false;
            }

            if (string.Equals(lastInvite, currentInvite, StringComparison.Ordinal))
            {
                stableSampleCount++;
            }
            else
            {
                lastInvite = currentInvite;
                stableSampleCount = 0;
            }

            return stableSampleCount >= 3;
        }, TimeSpan.FromSeconds(4));
        Assert.Equal(lastInvite, helpee.ShareInvite);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeeViewModel_UserEndsConnectedSession_AutoRegeneratesCode()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-user-end-rehost-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-user-end-rehost-" + Guid.NewGuid().ToString("N")));
        using var helpee = new HelpeePageViewModel(cancelAction: () => _ = helpeeRuntime.DisconnectAsync(), transportConfig, helpeeRuntime);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var initialInvite = await WaitForShareInviteAsync(helpee);
        await helperRuntime.StartHelperAsync(GetHostedAddressOrThrow(helpeeRuntime), cts.Token);
        await WaitUntilAsync(() => helpee.IsIncomingRequestView, TimeSpan.FromSeconds(2));
        helpee.AllowCommand.Execute(null);
        await WaitUntilAsync(() => helpee.IsConnectedView && helpeeRuntime.State == SessionRuntimeState.Connected && helperRuntime.State == SessionRuntimeState.Connected, TimeSpan.FromSeconds(2));
        helpee.EndSessionCommand.Execute(null);
        string latestInvite = initialInvite;
        await WaitUntilAsync(() =>
        {
            latestInvite = helpee.ShareInvite;
            return helpee.ShowWaitingPanel && !helpee.IsConnectedView && !string.IsNullOrWhiteSpace(latestInvite) && !string.Equals(latestInvite, initialInvite, StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(8));
        Assert.NotEqual(initialInvite, latestInvite);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeeViewModel_DeclineIncomingRequest_AutoRegeneratesCode()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-decline-rehost-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-decline-rehost-" + Guid.NewGuid().ToString("N")));
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helpeeRuntime);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var initialInvite = await WaitForShareInviteAsync(helpee);
        await helperRuntime.StartHelperAsync(GetHostedAddressOrThrow(helpeeRuntime), cts.Token);
        await WaitUntilAsync(() => helpee.IsIncomingRequestView, TimeSpan.FromSeconds(2));
        await helpee.DeclineCommand.ExecuteAsync(null);
        string latestInvite = initialInvite;
        await WaitUntilAsync(() =>
        {
            latestInvite = helpee.ShareInvite;
            return helpee.ShowWaitingPanel && !helpee.IsIncomingRequestView && !string.IsNullOrWhiteSpace(latestInvite) && !string.Equals(latestInvite, initialInvite, StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(8));
        Assert.NotEqual(initialInvite, latestInvite);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeeViewModel_IncomingRequest_ShowsApprovalCountdown()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-timeout-countdown-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-timeout-countdown-" + Guid.NewGuid().ToString("N")));
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helpeeRuntime, incomingRequestTimeout: TimeSpan.FromSeconds(5));
        _ = await WaitForShareInviteAsync(helpee);
        await helperRuntime.StartHelperAsync(GetHostedAddressOrThrow(helpeeRuntime), CancellationToken.None);
        await WaitUntilAsync(() => helpee.IsIncomingRequestView && helpee.ShowIncomingRequestTimeout && !string.IsNullOrWhiteSpace(helpee.IncomingRequestTimeoutText), TimeSpan.FromSeconds(2));
        Assert.StartsWith("Request expires in 00:0", helpee.IncomingRequestTimeoutText);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeeViewModel_IncomingRequestTimeout_ReturnsToWaitingWithUsableCode()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-timeout-rehost-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-timeout-rehost-" + Guid.NewGuid().ToString("N")));
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helpeeRuntime, incomingRequestTimeout: TimeSpan.FromMilliseconds(250));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var usableInvite = string.Empty;
        _ = await WaitForShareInviteAsync(helpee);
        await helperRuntime.StartHelperAsync(GetHostedAddressOrThrow(helpeeRuntime), cts.Token);
        await WaitUntilAsync(() => helpee.IsIncomingRequestView, TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() =>
        {
            usableInvite = helpee.ShareInvite;
            return helpee.ShowWaitingPanel && !helpee.IsIncomingRequestView && string.Equals(helpee.ConnectionState, "Waiting", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(usableInvite);
        }, TimeSpan.FromSeconds(8));
        Assert.False(string.IsNullOrWhiteSpace(usableInvite));
        Assert.Equal("Waiting", helpee.ConnectionState);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeeViewModel_IncomingRequestTimeout_ShowsHelperTimeoutPresentation()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-timeout-helper-copy-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-timeout-helper-copy-" + Guid.NewGuid().ToString("N")));
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helpeeRuntime, incomingRequestTimeout: TimeSpan.FromMilliseconds(250));
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime, approvalTimeout: TimeSpan.FromSeconds(2), connectFailureCooldown: TimeSpan.Zero);
        helper.CodeInput = await WaitForShareInviteAsync(helpee);
        await helper.ConnectCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => (string.Equals(helper.StatusText, "No response yet.", StringComparison.Ordinal) || string.Equals(helper.TransientBannerText, "No response yet.", StringComparison.Ordinal)) && (helper.ConnectCommand.CanExecute(null) || helper.RetryCommand.CanExecute(null)), TimeSpan.FromSeconds(3));
        Assert.Equal(TransportFailureCategory.HandshakeTimeout, helperRuntime.GetLastFailureCategoryForTests());
        Assert.True(string.Equals(helper.StatusText, "No response yet.", StringComparison.Ordinal) || string.Equals(helper.TransientBannerText, "No response yet.", StringComparison.Ordinal));
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeeViewModel_HelperDisconnectsDuringIncomingRequest_ClearsAllowPanel_AndRotatesCode()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-incoming-cancel-rehost-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-incoming-cancel-rehost-" + Guid.NewGuid().ToString("N")));
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helpeeRuntime);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var initialInvite = await WaitForShareInviteAsync(helpee);
        await helperRuntime.StartHelperAsync(GetHostedAddressOrThrow(helpeeRuntime), cts.Token);
        await WaitUntilAsync(() => helpee.IsIncomingRequestView && helpee.ShowIncomingRequestPanel, TimeSpan.FromSeconds(2));
        Assert.False(helpee.ShowTransientBanner);
        await helperRuntime.DisconnectAsync();
        await WaitUntilAsync(() => !helpee.IsIncomingRequestView && helpee.ShowWaitingPanel && !helpee.HasIncomingRequest && !string.IsNullOrWhiteSpace(helpee.ShareInvite), TimeSpan.FromSeconds(8));
        Assert.False(helpee.ShowTransientBanner);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeeViewModel_IncomingRequest_DoesNotExposeTransientCancel()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-incoming-no-cancel-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-incoming-no-cancel-" + Guid.NewGuid().ToString("N")));
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helpeeRuntime);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        _ = await WaitForShareInviteAsync(helpee);
        await helperRuntime.StartHelperAsync(GetHostedAddressOrThrow(helpeeRuntime), cts.Token);
        await WaitUntilAsync(() => helpee.IsIncomingRequestView && helpee.ShowIncomingRequestPanel, TimeSpan.FromSeconds(2));
        Assert.False(helpee.CanCancelTransient);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeeViewModel_IncomingJoinRequest_SwitchesToApprovalPanel()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-ui-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-ui-" + Guid.NewGuid().ToString("N")));
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helpeeRuntime);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.False(helpee.IsIncomingRequestView);
        Assert.True(helpee.ShowWaitingPanel);
        _ = await WaitForShareInviteAsync(helpee);
        await helperRuntime.StartHelperAsync(GetHostedAddressOrThrow(helpeeRuntime), cts.Token);
        await WaitUntilAsync(() => helpee.IsIncomingRequestView && helpee.ShowIncomingRequestPanel && !helpee.ShowWaitingPanel, TimeSpan.FromSeconds(2));
    }

}
