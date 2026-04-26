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
public sealed class HelperSessionUiTests : SessionHeaderAndBannerTestBase
{
    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelperPageViewModel_StatusBanner_ReactsToFailedReconnectingConnected()
    {
        var source = new FakeStatusPresenterSource();
        using var presenter = new StatusPresenter(source);
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport(), SessionRuntimeWatchdogOptions.Default with { Enabled = false });
        var transportConfig = CreateDevLocalTestConfig();
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, runtime, statusPresenter: presenter);
        source.SetFailure(TransportFailure.Create(TransportFailureCategory.HandshakeTimeout, "Timed out", exceptionType: nameof(TimeoutException), rawError: "handshake timeout", isTransient: true, correlationId: "corr1"));
        source.SetSessionUiState(SessionRuntimeState.Failed);
        source.SetTransportState(TransportState.Failed);
        source.RaiseStateChanged();
        Assert.Equal(UserStatusKind.Failed, helper.BannerStatus.Kind);
        Assert.True(helper.ShowStatusBanner);
        source.SetAttempt(2);
        source.SetTransportState(TransportState.Reconnecting);
        source.RaiseTransient(isVisible: true, text: "Reconnecting… (attempt 2, next retry in 1s)", canCancel: true);
        Assert.Equal(UserStatusKind.Reconnecting, helper.BannerStatus.Kind);
        Assert.True(helper.ShowStatusBanner);
        source.SetSessionUiState(SessionRuntimeState.Connected);
        source.SetTransportState(TransportState.Connected);
        source.RaiseTransient(isVisible: false, text: string.Empty, canCancel: false);
        source.RaiseStateChanged();
        Assert.Equal(UserStatusKind.Connected, helper.BannerStatus.Kind);
        Assert.False(helper.ShowStatusBanner);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelperPageViewModel_HeaderStatusText_UsesStatusTextOrReady_AndIsNeverEmpty()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime);
        SetPrivateField(helper, "connectionState", "Idle");
        SetPrivateField(helper, "statusText", "Waiting for code");
        Assert.Equal("Waiting for code", helper.HeaderStatusText);
        Assert.False(string.IsNullOrWhiteSpace(helper.HeaderStatusText));
        SetPrivateField(helper, "statusText", string.Empty);
        Assert.Equal("Ready", helper.HeaderStatusText);
        Assert.False(string.IsNullOrWhiteSpace(helper.HeaderStatusText));
        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helper.ScreenShareViewer, "isActive", true);
        SetPrivateField(helper.ScreenShareViewer, "currentFrame", CreateTestBitmap(1, 1));
        Assert.Equal("Connected • Viewing screen", helper.HeaderStatusText);
        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Recovering);
        Assert.Equal("Reconnecting… • Viewing screen", helper.HeaderStatusText);
        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Failed);
        SetPrivateField(helper, "failureTitle", "Connection failed");
        Assert.Equal("Connection failed", helper.HeaderStatusText);
        SetPrivateField(helper, "failureTitle", string.Empty);
        SetPrivateField(helper, "statusText", "The other person ended the session.");
        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Ended);
        Assert.Equal("The other person ended the session.", helper.HeaderStatusText);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelperPageViewModel_TransientStatusPanel_HidesWhenItDuplicatesHeader()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime);
        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Connecting);
        SetPrivateField(helper, "showTransientBanner", true);
        SetPrivateField(helper, "transientBannerText", "Connecting…");
        Assert.False(helper.ShowTransientStatusPanel);
        SetPrivateField(helper, "transientBannerText", "Trying bridge fallback");
        Assert.True(helper.ShowTransientStatusPanel);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelperPageViewModel_RemoteEnd_UsesSessionEndedCopy_NotConnectionFailed()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime);
        SetPrivateField(helperRuntime, "state", SessionRuntimeState.Failed);
        SetPrivateField(helperRuntime, "statusText", "The other person ended the session.");
        SetPrivateField(helperRuntime, "lastDisconnectWasRemoteEnd", true);
        SetPrivateField(helperRuntime, "currentFlowSnapshot", BuildHelperPeerEndedFlow(helperRuntime, "The other person ended the session."));
        InvokePrivateMethod(helper, "SyncFromRuntime");
        Assert.Equal("The other person ended the session.", helper.StatusText);
        Assert.Equal("Waiting", helper.ConnectionState);
        Assert.Equal("The other person ended the session.", helper.HeaderStatusText);
        Assert.False(helper.ShowFailurePanel);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelperPageViewModel_OnDisconnected_GenericDisconnect_DoesNotLeaveConnectedStateStuck()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime);
        SetPrivateField(helper, "connectionState", "Connected");
        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helperRuntime, "state", SessionRuntimeState.Failed);
        SetPrivateField(helperRuntime, "statusText", "Connection lost.");
        SetPrivateField(helper.ScreenShareViewer, "isActive", true);
        SetPrivateField(helper.ScreenShareViewer, "currentFrame", CreateTestBitmap(1, 1));
        InvokePrivateMethod(helper, "OnScreenShareViewerPropertyChanged", helper.ScreenShareViewer, new PropertyChangedEventArgs(nameof(ScreenShareViewerViewModel.CurrentFrame)));
        Assert.True(helper.ShowRemoteScreenShareFrame);
        Assert.Equal("Connected • Viewing screen", helper.HeaderStatusText);
        InvokePrivateMethod(helper, "OnDisconnected", helperRuntime, EventArgs.Empty);
        await WaitUntilAsync(() => !string.Equals(helper.ConnectionState, "Connected", StringComparison.Ordinal), TimeSpan.FromSeconds(3));
        Assert.NotEqual("Connected", helper.ConnectionState);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelperPageViewModel_OnDisconnected_RemoteEnd_ClearsActiveSessionAffordancesImmediately()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime);
        SetPrivateField(helper, "connectionState", "Connected");
        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helperRuntime, "state", SessionRuntimeState.Connected);
        SetPrivateField(helperRuntime, "lastDisconnectWasRemoteEnd", true);
        SetPrivateField(helperRuntime, "currentFlowSnapshot", BuildHelperConnectedFlow(helperRuntime));
        SetPrivateField(helperRuntime, "remoteControlSessionState", new RemoteControlSessionState(ControlState.Active, "peer", "req-1", null, SupportsRemoteControl: true, PeerSupportsRemoteControl: true));
        SetPrivateField(helper.ScreenShareViewer, "isActive", true);
        SetPrivateField(helper.ScreenShareViewer, "currentFrame", CreateTestBitmap(1, 1));
        InvokePrivateMethod(helper, "OnScreenShareViewerPropertyChanged", helper.ScreenShareViewer, new PropertyChangedEventArgs(nameof(ScreenShareViewerViewModel.CurrentFrame)));
        InvokePrivateMethod(helper, "OnRemoteControlStateChanged", helperRuntime, EventArgs.Empty);
        Assert.True(helper.ShowRemoteScreenShareFrame);
        Assert.True(helper.ShowStopControlAction);
        Assert.True(helper.ShowRemoteControlActiveStatus);
        SetPrivateField(helperRuntime, "state", SessionRuntimeState.Disconnected);
        SetPrivateField(helperRuntime, "currentFlowSnapshot", BuildHelperPeerEndedFlow(helperRuntime, "The other person ended the session."));
        InvokePrivateMethod(helper, "OnDisconnected", helperRuntime, EventArgs.Empty);
        await WaitUntilAsync(() => helper.EffectivePhase == SessionUiPhase.Waiting && string.Equals(helper.ConnectionState, "Waiting", StringComparison.Ordinal) && helper.ShowTransientBanner && string.Equals(helper.TransientBannerText, "The other person ended the session.", StringComparison.Ordinal) && !helper.ShowRemoteScreenShareFrame && !helper.ShowStopControlAction && !helper.ShowRemoteControlActiveStatus, TimeSpan.FromSeconds(5));
        Assert.Equal("The other person ended the session.", helper.HeaderStatusText);
        Assert.True(helper.ShowTransientBanner);
        Assert.Equal("The other person ended the session.", helper.TransientBannerText);
        Assert.False(helper.IsChatInputEnabled);
        Assert.False(helper.ShowFailurePanel);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelperPageViewModel_WaitingSession_ClearsStaleUserEndedMarker_FromPreviousSession()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime, qrCodeService: new NoOpQrCodeService());
        SetPrivateField(helper, "connectionState", "Connected");
        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helper, "endSessionRequested", true);
        SetPrivateField(helper, "endReason", SessionEndReason.UserEnded);
        SetPrivateField(helperRuntime, "state", SessionRuntimeState.Waiting);
        SetPrivateField(helperRuntime, "statusText", "Waiting for help requests…");
        SetPrivateField(helperRuntime, "currentFlowSnapshot", BuildHelperWaitingFlow(helperRuntime));
        InvokePrivateMethod(helper, "SyncFromRuntime");
        Assert.False(Assert.IsType<bool>(GetPrivateField(helper, "endSessionRequested")));
        Assert.Equal(SessionUiPhase.Waiting, helper.EffectivePhase);
        Assert.True(helper.ShowMainControls);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelperPageViewModel_WaitingRuntime_ClearsStaleUserEndedMarker_AfterLocalEnd()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime, qrCodeService: new NoOpQrCodeService());
        SetPrivateField(helper, "connectionState", "Idle");
        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Ended);
        SetPrivateField(helper, "endSessionRequested", true);
        SetPrivateField(helper, "endReason", SessionEndReason.UserEnded);
        SetPrivateField(helperRuntime, "state", SessionRuntimeState.Waiting);
        SetPrivateField(helperRuntime, "statusText", "Waiting for help requests…");
        SetPrivateField(helperRuntime, "currentFlowSnapshot", BuildHelperWaitingFlow(helperRuntime));
        InvokePrivateMethod(helper, "SyncFromRuntime");
        Assert.False(Assert.IsType<bool>(GetPrivateField(helper, "endSessionRequested")));
        Assert.Equal(SessionUiPhase.Waiting, helper.EffectivePhase);
        Assert.Equal("Waiting", helper.ConnectionState);
        Assert.True(helper.ShowMainControls);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelperPageViewModel_ConnectedRuntime_EnablesChatInput_WhenFallbackPhaseLags()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime, qrCodeService: new NoOpQrCodeService());
        SetPrivateField(helperRuntime, "state", SessionRuntimeState.Connected);
        SetPrivateField(helperRuntime, "statusText", "Connected");
        SetPrivateField(helperRuntime, "currentFlowSnapshot", BuildHelperConnectedFlow(helperRuntime));
        SetPrivateField(helper, "connectionState", "Connected");
        SetPrivateField(helper, "statusText", "Connected");
        SetPrivateField(helper, "fallbackUiPhase", SessionUiPhase.Connecting);
        InvokePrivateMethod(helper, "UpdateUiFromSnapshot");
        Assert.True(helper.IsChatInputEnabled);
        Assert.Equal("Connected", helper.ConnectionState);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelperPageViewModel_RemoteControlActive_DoesNotDisableEndSession()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime, qrCodeService: new NoOpQrCodeService());
        SetPrivateField(helperRuntime, "remoteControlSessionState", RemoteControlSessionState.Default with { ControlState = ControlState.Active });
        SetPrivateField(helper, "canEndSession", true);
        Assert.True(helper.CanEndSession);
        Assert.True(helper.EndSessionCommand.CanExecute(null));
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelperPageViewModel_OnDisconnected_ClearsChatHistory_WhenPeerEndsSession()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime);
        helper.ChatMessages.Add(new ChatLineViewModel { Text = "old", IsLocal = true });
        SetPrivateField(helper, "connectionState", "Connected");
        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helperRuntime, "state", SessionRuntimeState.Connected);
        SetPrivateField(helperRuntime, "statusText", "Connected");
        SetPrivateField(helperRuntime, "lastDisconnectWasRemoteEnd", true);
        SetPrivateField(helperRuntime, "currentFlowSnapshot", BuildHelperPeerEndedFlow(helperRuntime, "The other person ended the session."));
        InvokePrivateMethod(helper, "OnDisconnected", helperRuntime, EventArgs.Empty);
        await WaitUntilAsync(() => helper.EffectivePhase == SessionUiPhase.Waiting && string.Equals(helper.ConnectionState, "Waiting", StringComparison.Ordinal) && !helper.HasChatMessages, TimeSpan.FromSeconds(5));
        Assert.True(helper.ShowNoMessagesPlaceholder);
        Assert.Equal("The other person ended the session.", helper.TransientBannerText);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void SessionUxPhaseMapper_SessionEndedBannerStatus_MapsToEndedPhase()
    {
        var status = new UserFacingStatus(UserStatusKind.Failed, "Session ended", "The other person ended the session.", FailureSeverity.Error);
        Assert.Equal(SessionUiPhase.Ended, SessionUxPhaseMapper.FromBannerStatus(status));
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelperPageViewModel_RemoteControlAffordances_ClearImmediately_WhenBackendLeavesConnected()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime);
        var helperIdentity = new PeerAddress("helper-affordance-test");
        var sessionId = new SessionId("helper-affordance-session");
        var expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5);
        SetPrivateProperty(helper, "ConnectionState", "Connected");
        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helperRuntime, "role", SessionRuntimeRole.Helper);
        SetPrivateField(helperRuntime, "state", SessionRuntimeState.Connected);
        SetPrivateField(helperRuntime, "transportState", TransportState.Connected);
        SetPrivateField(helperRuntime, "currentFlowSnapshot", BuildHelperConnectedFlow(helperRuntime));
        SetPrivateField(helperRuntime, "transport", new DevLocalTransport());
        SetPrivateField(helperRuntime, "sessionSecurityState", SessionSecurityState.Empty with { SessionId = sessionId, HelperAddress = helperIdentity, ApprovalGranted = true, HandshakeCompleted = true, HandshakeState = SessionHandshakeState.Verified, InviteValidated = true, ApprovedCapabilities = CapabilityGrant.RemoteControl | CapabilityGrant.ScreenShare, ApprovalExpiresAt = expiresAtUtc, });
        SetPrivateField(helperRuntime, "currentSessionGrant", new SessionGrant(helperIdentity, CapabilityGrant.RemoteControl | CapabilityGrant.ScreenShare, sessionId, expiresAtUtc));
        SetPrivateField(helperRuntime, "remoteControlSessionState", RemoteControlSessionState.Default with { ControlState = ControlState.Off, SupportsRemoteControl = true, PeerSupportsRemoteControl = true, });
        SetPrivateField(helper.ScreenShareViewer, "isActive", true);
        SetPrivateField(helper.ScreenShareViewer, "currentFrame", CreateTestBitmap(1, 1));
        Assert.True(helper.ShowRequestControlAction);
        Assert.True(helper.CanRequestControl);
        SetPrivateField(helperRuntime, "remoteControlSessionState", RemoteControlSessionState.Default with { ControlState = ControlState.Active, SupportsRemoteControl = true, PeerSupportsRemoteControl = true, });
        Assert.True(helper.ShowStopControlAction);
        Assert.True(helper.ShowRemoteControlActiveStatus);
        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Failed);
        SetPrivateField(helperRuntime, "state", SessionRuntimeState.Failed);
        SetPrivateField(helperRuntime, "currentFlowSnapshot", helperRuntime.FlowSnapshot with { Phase = SessionFlowPhase.Failed, UiPhase = SessionUiPhase.Failed, Role = SessionRuntimeRole.Helper, RuntimeState = SessionRuntimeState.Failed, DisplayStatusText = "Connection lost.", DisplayConnectionState = "Failed", TerminalKind = SessionTerminalKind.Failed, TerminalStatusText = "Connection lost.", });
        Assert.False(helper.ShowRequestControlAction);
        Assert.False(helper.CanRequestControl);
        Assert.False(helper.ShowStopControlAction);
        Assert.False(helper.CanStopControl);
        Assert.False(helper.ShowRemoteControlActiveStatus);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelperPageViewModel_KeyboardToggle_RemainsAvailable_WhenMappingIsUnavailable()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var scripted = new ScriptedSignalingTransport();
        using var helperRuntime = new SessionRuntime(() => scripted);
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime);
        var focusRequested = false;
        helper.RemoteControlViewerFocusRequested += (_, _) => focusRequested = true;
        SetPrivateProperty(helper, "ConnectionState", "Connected");
        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Connected);
        helperRuntime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(helperRuntime, "transport", scripted);
        SetPrivateField(helperRuntime, "state", SessionRuntimeState.Connected);
        SetPrivateField(helperRuntime, "transportState", TransportState.Connected);
        SetPrivateField(helperRuntime, "statusText", "Connected");
        InvokePrivateMethod(helperRuntime, "WireTransport", scripted);
        InvokePrivateMethod(helperRuntime, "ApplyTransportSecurityState", CreateApprovedSecurityState(new PeerAddress("helper.keyboardtoggle.helpee"), new PeerAddress(scripted.LocalPeerAddress), CapabilityGrant.RemoteControl | CapabilityGrant.ScreenShare));
        SetPrivateField(helperRuntime, "remoteControlSessionState", new RemoteControlSessionState(ControlState.Active, "helper.keyboardtoggle.helpee", "req_keyboard_toggle", null, SupportsRemoteControl: true, PeerSupportsRemoteControl: true));
        SetPrivateField(helper.ScreenShareViewer, "isActive", true);
        SetPrivateField(helper.ScreenShareViewer, "currentFrame", CreateTestBitmap(1, 1));
        InvokePrivateMethod(helper, "OnScreenShareViewerPropertyChanged", helper.ScreenShareViewer, new PropertyChangedEventArgs(nameof(ScreenShareViewerViewModel.CurrentFrame)));
        Assert.False(helper.RemoteControlMappingAvailable);
        Assert.True(helper.ShowRemoteControlActiveStatus);
        Assert.True(helper.ShowControlModeToggle);
        Assert.True(helper.CanControlModeToggle);
        Assert.False(helper.IsRemoteControlInputCaptureEnabled);
        Assert.False(helper.IsRemoteControlKeyboardCaptureEnabled);
        helper.ToggleControlModeCommand.Execute(null);
        Assert.True(helper.IsRemoteControlKeyboardCaptureEnabled);
        Assert.Equal("Keyboard to remote: On", helper.ControlModeButtonText);
        Assert.True(focusRequested);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelperPageViewModel_RemoteFrames_DoNotReRaiseShellVisibility_WhenVisibilityStaysTrue()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime);
        var visibilityChangedCount = 0;
        helper.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HelperPageViewModel.ShowRemoteScreenShareFrame))
            {
                visibilityChangedCount++;
            }
        };
        SetPrivateField(helper.ScreenShareViewer, "isActive", true);
        SetPrivateField(helper.ScreenShareViewer, "currentFrame", CreateTestBitmap(1, 1));
        InvokePrivateMethod(helper, "OnScreenShareViewerPropertyChanged", helper.ScreenShareViewer, new PropertyChangedEventArgs(nameof(ScreenShareViewerViewModel.CurrentFrame)));
        SetPrivateField(helper.ScreenShareViewer, "currentFrame", CreateTestBitmap(2, 1));
        InvokePrivateMethod(helper, "OnScreenShareViewerPropertyChanged", helper.ScreenShareViewer, new PropertyChangedEventArgs(nameof(ScreenShareViewerViewModel.CurrentFrame)));
        Assert.True(helper.ShowRemoteScreenShareFrame);
        Assert.Equal(1, visibilityChangedCount);
        SetPrivateField(helper.ScreenShareViewer, "currentFrame", null);
        SetPrivateField(helper.ScreenShareViewer, "isActive", false);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelperPageViewModel_ScreenShareStopped_ClearsRemoteViewer()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime);
        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helper.ScreenShareViewer, "isActive", true);
        SetPrivateField(helper.ScreenShareViewer, "currentFrame", CreateTestBitmap(1, 1));
        InvokePrivateMethod(helper, "OnScreenShareViewerPropertyChanged", helper.ScreenShareViewer, new PropertyChangedEventArgs(nameof(ScreenShareViewerViewModel.CurrentFrame)));
        Assert.True(helper.ShowRemoteScreenShareFrame);
        Assert.Equal("Connected • Viewing screen", helper.HeaderStatusText);
        InvokePrivateMethod(helper, "OnScreenShareStopped", helperRuntime, EventArgs.Empty);
        Assert.False(helper.ShowRemoteScreenShareFrame);
        Assert.Null(helper.RemoteScreenShareFrame);
        Assert.Equal("Connected", helper.HeaderStatusText);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelperPageViewModel_RequestPendingWithoutVisibleScreen_DoesNotKeepWaitingApprovalHeader()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime);
        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helperRuntime, "remoteControlSessionState", new RemoteControlSessionState(ControlState.Requesting, ControllerPeerId: "helpee-peer", CurrentControlRequestId: "req-no-screen", ConsentToken: null, SupportsRemoteControl: true, PeerSupportsRemoteControl: true));
        Assert.Equal("Connected", helper.HeaderStatusText);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelperPageViewModel_ScreenShareDecodeError_ShowsClearViewerMessage_AndClearsOnStop()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime);
        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helper.ScreenShareViewer, "isActive", true);
        SetPrivateField(helper.ScreenShareViewer, "currentFrame", null);
        SetPrivateField(helper.ScreenShareViewer, "statusText", "Invalid frame received");
        InvokePrivateMethod(helper, "OnScreenShareViewerPropertyChanged", helper.ScreenShareViewer, new PropertyChangedEventArgs(nameof(ScreenShareViewerViewModel.StatusText)));
        Assert.False(helper.ShowRemoteScreenShareFrame);
        Assert.True(helper.ShowScreenShareViewerError);
        Assert.Equal("Screen sharing is active, but the latest frame could not be displayed.", helper.ScreenShareViewerMessage);
        Assert.Equal("Connected • Viewing screen", helper.HeaderStatusText);
        InvokePrivateMethod(helper, "OnScreenShareStopped", helperRuntime, EventArgs.Empty);
        Assert.False(helper.ShowScreenShareViewerError);
        Assert.Equal(string.Empty, helper.ScreenShareViewerMessage);
        Assert.Equal("Connected", helper.HeaderStatusText);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelperPageViewModel_BlankScreenSharePlaceholder_DoesNotAppendViewingSuffix()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime);
        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helper.ScreenShareViewer, "isActive", true);
        SetPrivateField(helper.ScreenShareViewer, "currentFrame", null);
        SetPrivateField(helper.ScreenShareViewer, "statusText", string.Empty);
        InvokePrivateMethod(helper, "OnScreenShareViewerPropertyChanged", helper.ScreenShareViewer, new PropertyChangedEventArgs(nameof(ScreenShareViewerViewModel.StatusText)));
        Assert.False(helper.ShowRemoteScreenShareFrame);
        Assert.False(helper.ShowScreenShareViewerError);
        Assert.Equal("Connected", helper.HeaderStatusText);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelperPageViewModel_CanEndSession_IsTrueOnlyForConnectedConnectingOrRecoveringPhases()
    {
        Assert.True(InvokeCanEndForPhase(typeof(HelperPageViewModel), SessionUiPhase.Connected));
        Assert.False(InvokeCanEndForPhase(typeof(HelperPageViewModel), SessionUiPhase.Failed));
        Assert.False(InvokeCanEndForPhase(typeof(HelperPageViewModel), SessionUiPhase.Ended));
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void ChatView_DoesNotShowTopBar_WhenSessionHeaderIsEnabled()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime);
        Assert.False(helper.ShowChatTopBar);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelperPageViewModel_EndSession_DoesNotInvokeCancelAction()
    {
        var cancelInvoked = false;
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: () => cancelInvoked = true, CreateDevLocalTestConfig(), helperRuntime);
        SetPrivateField(helper, "connectionState", "Connected");
        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Connected);
        SetPrivateField(helper, "wasConnected", true);
        SetPrivateField(helper, "canEndSession", true);
        helper.EndSessionCommand.Execute(null);
        Assert.False(cancelInvoked);
        Assert.True(helper.ShowMainControls);
        Assert.False(helper.ShowConnectedPanel);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelperPageViewModel_EndSession_DeactivatesViewer_AndResetsSession()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-end-session-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-end-session-" + Guid.NewGuid().ToString("N")));
        using var helpee = new HelpeePageViewModel(cancelAction: () => _ = helpeeRuntime.DisconnectAsync(), transportConfig, helpeeRuntime);
        using var helper = new HelperPageViewModel(cancelAction: () => _ = helperRuntime.DisconnectAsync(), transportConfig, helperRuntime);
        _ = await WaitForShareInviteAsync(helpee);
        var connectTask = helperRuntime.StartHelperAsync(GetHostedAddressOrThrow(helpeeRuntime), CancellationToken.None);
        await WaitUntilAsync(() => helpee.HasIncomingRequest && helpee.ConnectionState == "IncomingRequest", TimeSpan.FromSeconds(5));
        helpee.AllowCommand.Execute(null);
        await connectTask;
        await WaitUntilAsync(() => helper.EffectivePhase == SessionUiPhase.Connected && helper.IsChatInputEnabled && helper.CanEndSession, TimeSpan.FromSeconds(5));
        SetPrivateField(helper.ScreenShareViewer, "isActive", true);
        Assert.True(helper.ScreenShareViewer.IsActive);
        helper.EndSessionCommand.Execute(null);
        await WaitUntilAsync(() => !helper.ScreenShareViewer.IsActive && !helper.IsChatInputEnabled && helperRuntime.State == SessionRuntimeState.Waiting && string.Equals(helper.ConnectionState, "Waiting", StringComparison.Ordinal) && !helper.CanEndSession, TimeSpan.FromSeconds(5));
        Assert.False(helper.ShowRemoteScreenShareFrame);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelperPageViewModel_EndSession_WhileApprovalPending_AllowsReconnectWithFreshInvite()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-end-pending-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-end-pending-" + Guid.NewGuid().ToString("N")));
        using var helpee = new HelpeePageViewModel(cancelAction: () => _ = helpeeRuntime.DisconnectAsync(), transportConfig, helpeeRuntime);
        using var helper = new HelperPageViewModel(cancelAction: () => _ = helperRuntime.DisconnectAsync(), transportConfig, helperRuntime);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        helper.CodeInput = await WaitForShareInviteAsync(helpee);
        var connectTask = Assert.IsAssignableFrom<Task>(InvokePrivateMethod(helper, "ConnectAsync"));
        await WaitUntilAsync(() => helpee.HasIncomingRequest && helpee.ConnectionState == "IncomingRequest", TimeSpan.FromSeconds(5));
        helper.EndSessionCommand.Execute(null);
        await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(2), cts.Token));
        await WaitUntilAsync(() => !helper.IsConnecting && helperRuntime.State == SessionRuntimeState.Waiting && string.Equals(helper.ConnectionState, "Waiting", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        await helpeeRuntime.ResetAsync();
        await helpeeRuntime.StartHelpeeAsync(cts.Token);
        var freshInvite = await WaitForShareInviteAsync(helpee);
        helper.CodeInput = freshInvite;
        Assert.True(helper.ConnectCommand.CanExecute(null));
        var reconnectTask = Assert.IsAssignableFrom<Task>(InvokePrivateMethod(helper, "ConnectAsync"));
        await WaitUntilAsync(() => helpee.HasIncomingRequest && helpee.ConnectionState == "IncomingRequest", TimeSpan.FromSeconds(5));
        await Task.WhenAny(reconnectTask, Task.Delay(TimeSpan.FromSeconds(2), cts.Token));
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task UnsentChatDraft_DoesNotLeakIntoNextSession_AfterEndAndRestart()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helpeeRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helpeeRuntime);
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime);
        helper.ChatDraft = "unsent helper draft";
        helpee.ChatDraft = "unsent helpee draft";
        helper.ChatMessages.Add(new ChatLineViewModel { Text = "old helper message", IsLocal = true });
        helpee.ChatMessages.Add(new ChatLineViewModel { Text = "old helpee message", IsLocal = true });
        SetPrivateField(helper, "endSessionRequested", true);
        SetPrivateField(helper, "endReason", Enum.Parse(typeof(SessionEndReason), "UserEnded"));
        SetPrivateField(helpee, "endSessionRequested", true);
        SetPrivateField(helpee, "endReason", Enum.Parse(typeof(SessionEndReason), "UserEnded"));
        InvokePrivateMethod(helper, "PrepareForNewSession", true);
        InvokePrivateMethod(helpee, "PrepareForNewSession");
        Assert.Equal(string.Empty, helper.ChatDraft);
        Assert.Equal(string.Empty, helpee.ChatDraft);
        Assert.Empty(helper.ChatMessages);
        Assert.Empty(helpee.ChatMessages);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelperPageViewModel_FailedPhase_DisablesChatInput()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        var uiStateStore = new SessionUiStateStore();
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime, uiStateStore: uiStateStore);
        uiStateStore.SetPhase(SessionUiPhase.Failed, "test");
        await WaitUntilAsync(() => !helper.IsChatInputEnabled, TimeSpan.FromSeconds(1));
        Assert.False(helper.IsChatInputEnabled);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HeaderStatusText_IsNeverEmpty_InDefaultVmStates()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helpeeRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime);
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helpeeRuntime);
        Assert.False(string.IsNullOrWhiteSpace(helper.HeaderStatusText));
        Assert.False(string.IsNullOrWhiteSpace(helpee.HeaderStatusText));
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelperViewModel_Dispose_ConnectedNknSession_NotifiesHelpeeRemoteEnd()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            var options = NknTransportOptions.Load();
            using var helpeeTransport = new NknSignalingTransport(new FakeNknClient("helpee.dispose.addr." + Guid.NewGuid().ToString("N")), options, new NknIdentity("helpee-dispose-test", "helpee.dispose.test.fake"));
            using var helperTransport = new NknSignalingTransport(new FakeNknClient("helper.dispose.addr." + Guid.NewGuid().ToString("N")), options, new NknIdentity("helper-dispose-test", "helper.dispose.test.fake"));
            using var helpeeRuntime = new SessionRuntime(() => helpeeTransport);
            using var helperRuntime = new SessionRuntime(() => helperTransport);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await helpeeRuntime.StartHelpeeAsync(cts.Token);
            var invite = CreateValidatedInviteForTarget(GetHostedAddressOrThrow(helpeeRuntime), out var rawToken);
            await helperRuntime.StartHelperAsync(rawToken, invite, cts.Token);
            await WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.IncomingJoinRequest, TimeSpan.FromSeconds(2));
            await helpeeRuntime.ApproveAsync(cts.Token);
            await WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.Connected && helperRuntime.State == SessionRuntimeState.Connected, TimeSpan.FromSeconds(2));
            using var helper = new HelperPageViewModel(cancelAction: static () =>
            {
            }, CreateNknTestConfig(), helperRuntime);
            helper.Dispose();
            await WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.Failed && string.Equals(helpeeRuntime.StatusText, "The helper ended the session.", StringComparison.Ordinal), TimeSpan.FromSeconds(3));
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelperViewModel_RemoteEndAfterSuccessfulSession_RetainsHelperAddress_WhenBootstrapWasMissing()
    {
        var transportConfig = CreateNknTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime);
        var helpeeIdentity = new PeerAddress("nlink-helpee.retained.target");
        var helperIdentity = new PeerAddress("nlink-helper.retained.identity");
        var approvedState = CreateApprovedSecurityState(helpeeIdentity, helperIdentity);
        SetPrivateField(helperRuntime, "sessionSecurityState", approvedState);
        SetPrivateField(helperRuntime, "state", SessionRuntimeState.Connected);
        InvokePrivateMethod(helper, "SyncFromRuntime");
        Assert.StartsWith("nlinkh1.", helper.HelperIdentityBootstrapText, StringComparison.Ordinal);
        Assert.True(HelperBootstrapQrPayload.TryParse(helper.HelperIdentityBootstrapText, out var connectedBootstrap));
        Assert.NotNull(connectedBootstrap);
        var connectedBootstrapAddress = connectedBootstrap!.HelperAddress.Value;
        Assert.False(string.IsNullOrWhiteSpace(connectedBootstrapAddress));
        Assert.True(helper.HasHelperIdentityBootstrapVerificationCode);
        SetPrivateField(helperRuntime, "sessionSecurityState", SessionSecurityState.Empty);
        SetPrivateField(helperRuntime, "state", SessionRuntimeState.Failed);
        SetPrivateField(helperRuntime, "statusText", "The other person ended the session.");
        SetPrivateField(helperRuntime, "lastDisconnectWasRemoteEnd", true);
        InvokePrivateMethod(helper, "SyncFromRuntime");
        Assert.StartsWith("nlinkh1.", helper.HelperIdentityBootstrapText, StringComparison.Ordinal);
        Assert.True(HelperBootstrapQrPayload.TryParse(helper.HelperIdentityBootstrapText, out var retainedBootstrap));
        Assert.NotNull(retainedBootstrap);
        Assert.Equal(connectedBootstrapAddress, retainedBootstrap!.HelperAddress.Value);
        Assert.True(helper.HasHelperIdentityBootstrapVerificationCode);
        Assert.Equal("Waiting for help requests…", helper.StatusText);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelperViewModel_BootstrapIdentityFailure_KeepsPanelVisible_WithExplicitSeedStorageError()
    {
        var transportConfig = CreateNknTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime, bootstrapHelperIdentityResolver: _ => Task.FromException<PeerAddress?>(new CryptographicException("The data is invalid.")));
        var pending = Assert.IsAssignableFrom<Task>(InvokePrivateMethod(helper, "ResolveBootstrapHelperIdentityAsync", CancellationToken.None));
        await pending;
        Assert.StartsWith("nlinkh1.", helper.HelperIdentityBootstrapText, StringComparison.Ordinal);
        Assert.Equal("Protected seed storage could not be read.", helper.HelperIdentityBootstrapHintText);
        Assert.True(helper.HasHelperIdentityBootstrapVerificationCode);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelperViewModel_RemoteEndAfterSuccessfulSession_ReplacesBootstrapAddress_WithVerifiedHelperIdentity()
    {
        var transportConfig = CreateNknTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime);
        var bootstrapHelperIdentity = new PeerAddress("nlink-helper.bootstrap.placeholder");
        var helpeeIdentity = new PeerAddress("nlink-helpee.retained.target");
        var verifiedHelperIdentity = new PeerAddress("nlink-helper.connected.verified");
        var approvedState = CreateApprovedSecurityState(helpeeIdentity, verifiedHelperIdentity);
        SetPrivateField(helper, "bootstrapHelperIdentity", bootstrapHelperIdentity);
        SetPrivateField(helper, "bootstrapHelperIdentityIsAuthoritative", false);
        SetPrivateField(helperRuntime, "sessionSecurityState", approvedState);
        SetPrivateField(helperRuntime, "state", SessionRuntimeState.Connected);
        InvokePrivateMethod(helper, "SyncFromRuntime");
        Assert.StartsWith("nlinkh1.", helper.HelperIdentityBootstrapText, StringComparison.Ordinal);
        Assert.NotEqual(bootstrapHelperIdentity.Value, helper.HelperIdentityBootstrapText);
        Assert.True(HelperBootstrapQrPayload.TryParse(helper.HelperIdentityBootstrapText, out var connectedBootstrap));
        Assert.NotNull(connectedBootstrap);
        var connectedBootstrapAddress = connectedBootstrap!.HelperAddress.Value;
        Assert.False(string.IsNullOrWhiteSpace(connectedBootstrapAddress));
        Assert.NotEqual(bootstrapHelperIdentity.Value, connectedBootstrapAddress);
        Assert.True(helper.HasHelperIdentityBootstrapVerificationCode);
        SetPrivateField(helperRuntime, "sessionSecurityState", SessionSecurityState.Empty);
        SetPrivateField(helperRuntime, "state", SessionRuntimeState.Failed);
        SetPrivateField(helperRuntime, "statusText", "The other person ended the session.");
        SetPrivateField(helperRuntime, "lastDisconnectWasRemoteEnd", true);
        InvokePrivateMethod(helper, "SyncFromRuntime");
        Assert.StartsWith("nlinkh1.", helper.HelperIdentityBootstrapText, StringComparison.Ordinal);
        Assert.NotEqual(bootstrapHelperIdentity.Value, helper.HelperIdentityBootstrapText);
        Assert.True(HelperBootstrapQrPayload.TryParse(helper.HelperIdentityBootstrapText, out var retainedBootstrap));
        Assert.NotNull(retainedBootstrap);
        Assert.Equal(connectedBootstrapAddress, retainedBootstrap!.HelperAddress.Value);
        Assert.True(helper.HasHelperIdentityBootstrapVerificationCode);
        Assert.Equal("Waiting for help requests…", helper.StatusText);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelperViewModel_CopyInstallMessageCommand_UsesClipboardService()
    {
        var fakeClipboard = new FakeClipboardService();
        var transportConfig = CreateDevLocalTestConfig();
        var shareConfig = new ShareMessageConfig("https://example.com/nlink");
        using var runtime = new SessionRuntime(() => new FakeSignalingTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, runtime, clipboardService: fakeClipboard, shareMessageConfig: shareConfig);
        await helper.CopyInstallMessageCommand.ExecuteAsync(null);
        Assert.Equal("Install nLink and open it." + Environment.NewLine + "Download: https://example.com/nlink" + Environment.NewLine, fakeClipboard.LastText);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelperViewModel_WrongCode_TransitionsToFailed_WithMappedMessage_AndReconnectEnabled()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var scripted = new ScriptedSignalingTransport(onJoinByAddressAsync: static (_, __) => throw new TimeoutException("Could not find target session"));
        using var runtime = new SessionRuntime(() => scripted);
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, runtime, approvalTimeout: TimeSpan.FromMilliseconds(100), connectFailureCooldown: TimeSpan.Zero);
        CreateValidatedInviteForTarget(new PeerAddress("scripted.timeout.target"), out var inviteToken);
        helper.CodeInput = inviteToken;
        await helper.ConnectCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => (string.Equals(helper.StatusText, "No response from target address.", StringComparison.Ordinal) || string.Equals(helper.TransientBannerText, "No response from target address.", StringComparison.Ordinal)) && (helper.ConnectCommand.CanExecute(null) || helper.RetryCommand.CanExecute(null)), TimeSpan.FromSeconds(2));
        Assert.True(string.Equals(helper.ConnectionState, "Failed", StringComparison.Ordinal) || string.Equals(helper.ConnectionState, "Waiting", StringComparison.Ordinal));
        Assert.True(helper.ConnectCommand.CanExecute(null) || helper.RetryCommand.CanExecute(null));
        SetPrivateField(runtime, "transport", scripted);
        InvokePrivateMethod(runtime, "OnTransportDisconnected", scripted, EventArgs.Empty);
        await WaitUntilAsync(() => string.Equals(helper.TransientBannerText, "No response from target address.", StringComparison.Ordinal) || string.Equals(helper.StatusText, "No response from target address.", StringComparison.Ordinal), TimeSpan.FromSeconds(2));
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelperViewModel_ApprovalTimeout_TransitionsToFailed_WithMappedMessage()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var scripted = new ScriptedSignalingTransport(onJoinByAddressAsync: static (_, __) => Task.CompletedTask);
        using var runtime = new SessionRuntime(() => scripted);
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, runtime, approvalTimeout: TimeSpan.FromMilliseconds(100), connectFailureCooldown: TimeSpan.Zero);
        CreateValidatedInviteForTarget(new PeerAddress("scripted.approval.timeout"), out var inviteToken);
        helper.CodeInput = inviteToken;
        await helper.ConnectCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => (string.Equals(helper.StatusText, "No response yet.", StringComparison.Ordinal) || string.Equals(helper.TransientBannerText, "No response yet.", StringComparison.Ordinal)) && (helper.ConnectCommand.CanExecute(null) || helper.RetryCommand.CanExecute(null)), TimeSpan.FromSeconds(2));
        Assert.True(string.Equals(helper.ConnectionState, "Failed", StringComparison.Ordinal) || string.Equals(helper.ConnectionState, "Waiting", StringComparison.Ordinal));
        Assert.True(helper.ConnectCommand.CanExecute(null) || helper.RetryCommand.CanExecute(null));
        SetPrivateField(runtime, "transport", scripted);
        InvokePrivateMethod(runtime, "OnTransportDisconnected", scripted, EventArgs.Empty);
        await WaitUntilAsync(() => string.Equals(helper.TransientBannerText, "No response yet.", StringComparison.Ordinal) || string.Equals(helper.StatusText, "No response yet.", StringComparison.Ordinal), TimeSpan.FromSeconds(2));
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelperViewModel_Cooldown_PreventsRapidSecondConnectAttempt()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var factory = new CountingTransportFactory(() => new ScriptedSignalingTransport(onJoinByAddressAsync: static (_, __) => throw new TimeoutException("Could not find target session")));
        using var runtime = new SessionRuntime(factory.Create);
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, runtime, approvalTimeout: TimeSpan.FromMilliseconds(100), connectFailureCooldown: TimeSpan.FromSeconds(2));
        var initialCreateCount = factory.CreateCount;
        CreateValidatedInviteForTarget(new PeerAddress("scripted.cooldown.target"), out var inviteToken);
        helper.CodeInput = inviteToken;
        await helper.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(initialCreateCount + 1, factory.CreateCount);
        await helper.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(initialCreateCount + 1, factory.CreateCount);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelperViewModel_RawAddressInput_IsRejected_WithoutStartingRuntime()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var factory = new CountingTransportFactory(() => new ScriptedSignalingTransport());
        using var runtime = new SessionRuntime(factory.Create);
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, runtime, approvalTimeout: TimeSpan.FromMilliseconds(100), connectFailureCooldown: TimeSpan.Zero);
        var initialCreateCount = factory.CreateCount;
        helper.CodeInput = "nlink-helpee.raw-address";
        await helper.ConnectCommand.ExecuteAsync(null);
        Assert.Equal("InvalidInput", helper.ConnectionState);
        Assert.Equal("Use the helpee invite token.", helper.StatusText);
        Assert.Equal(SessionRuntimeState.Waiting, runtime.State);
        Assert.Equal(initialCreateCount, factory.CreateCount);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelperViewModel_FirstConnectAfterEndedSession_PreservesInviteAndStartsJoin()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var joinByInviteCalls = 0;
        string? observedInviteToken = null;
        using var scripted = new ScriptedSignalingTransport(onJoinByInviteAsync: (inviteToken, _, _) =>
        {
            observedInviteToken = inviteToken;
            Interlocked.Increment(ref joinByInviteCalls);
            return Task.CompletedTask;
        });
        using var runtime = new SessionRuntime(() => scripted);
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, runtime, approvalTimeout: TimeSpan.FromMilliseconds(100), connectFailureCooldown: TimeSpan.Zero);
        CreateValidatedInviteForTarget(new PeerAddress("scripted.connect.after-ended"), out var inviteToken);
        SetPrivateField(helper, "endReason", SessionEndReason.UserEnded);
        helper.CodeInput = inviteToken;
        await helper.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(inviteToken, observedInviteToken);
        Assert.Equal(1, Volatile.Read(ref joinByInviteCalls));
        Assert.Equal(inviteToken, helper.CodeInput);
        Assert.NotEqual("InvalidInput", helper.ConnectionState);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelperViewModel_NknMissing_ShowsFriendlyError_AndDiagnosticsLink()
    {
        var config = CreateStartupBlockedNknTestConfig();
        using var runtime = new SessionRuntime(() => new FakeSignalingTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, config, runtime, openDiagnosticsAction: static () =>
        {
        });
        Assert.True(helper.IsStartupBlocked);
        Assert.Equal("Please reinstall.", helper.StatusText);
        Assert.True(helper.ShowOpenDiagnosticsLink);
        Assert.False(helper.ShowConnectAction);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelperViewModel_NknReadyTimeoutDiagnostic_DoesNotShowReinstall()
    {
        var config = CreateNknTestConfig();
        NknRuntimeDiagnostics.SetLastError("NKN_START_FAILED: ready_timeout progress=rpc_selected");
        try
        {
            using var runtime = new SessionRuntime(() => new FakeSignalingTransport());
            using var helper = new HelperPageViewModel(cancelAction: static () =>
            {
            }, config, runtime);

            Assert.False(helper.IsStartupBlocked);
            Assert.NotEqual("Please reinstall.", helper.StatusText);
            Assert.True(helper.ShowConnectAction);
        }
        finally
        {
            NknRuntimeDiagnostics.SetLastError(string.Empty);
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelperViewModel_Disconnect_ShowsRetry_AndRetryReturnsToIdle()
    {
        var scripted = new ScriptedSignalingTransport(onJoinByAddressAsync: static (_, __) => Task.CompletedTask);
        var transportConfig = CreateDevLocalTestConfig();
        using var runtime = new SessionRuntime(() => scripted);
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, runtime, connectFailureCooldown: TimeSpan.Zero);
        CreateValidatedInviteForTarget(new PeerAddress("scripted.disconnect.retry"), out var inviteToken);
        helper.CodeInput = inviteToken;
        var connectTask = helper.ConnectCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => runtime.State == SessionRuntimeState.Connecting, TimeSpan.FromSeconds(1));
        scripted.RaiseDisconnected();
        await connectTask;
        await WaitUntilAsync(() => runtime.State == SessionRuntimeState.Failed && helper.ShowRetryAction && string.Equals(helper.StatusText, "Connection lost.", StringComparison.Ordinal), TimeSpan.FromSeconds(2));
        Assert.True(helper.RetryCommand.CanExecute(null));
        await helper.RetryCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => string.Equals(helper.ConnectionState, "Waiting", StringComparison.Ordinal) && helper.ConnectCommand.CanExecute(null) && !helper.ShowRetryAction, TimeSpan.FromSeconds(2));
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelperViewModel_ListenerDisconnect_DoesNotStayInFailedState()
    {
        var created = new List<ScriptedSignalingTransport>();
        var factory = new CountingTransportFactory(() =>
        {
            var transport = new ScriptedSignalingTransport();
            lock (created)
            {
                created.Add(transport);
            }

            return transport;
        });
        var transportConfig = CreateDevLocalTestConfig();
        using var runtime = new SessionRuntime(factory.Create);
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, runtime, connectFailureCooldown: TimeSpan.Zero);
        await WaitUntilAsync(() => runtime.State == SessionRuntimeState.Waiting && string.Equals(helper.ConnectionState, "Waiting", StringComparison.Ordinal), TimeSpan.FromSeconds(2));
        ScriptedSignalingTransport first;
        lock (created)
        {
            Assert.NotEmpty(created);
            first = Assert.IsType<ScriptedSignalingTransport>(created[0]);
        }

        first.RaiseDisconnected();
        await WaitUntilAsync(() => factory.CreateCount >= 2 && runtime.State == SessionRuntimeState.Waiting && string.Equals(helper.ConnectionState, "Waiting", StringComparison.Ordinal) && helper.EffectivePhase == SessionUiPhase.Waiting, TimeSpan.FromSeconds(3));
        Assert.False(string.Equals(helper.ConnectionState, "Failed", StringComparison.Ordinal));
        Assert.Equal("Waiting for help requests…", helper.StatusText);
        Assert.Equal(SessionUiPhase.Waiting, helper.EffectivePhase);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelperRuntime_RemoteSessionEnd_RestartsListenerQuietly()
    {
        var nextTransport = new ScriptedSignalingTransport();
        var factory = new CountingTransportFactory(() => nextTransport);
        var connectedTransport = new ScriptedSignalingTransport();
        using var runtime = new SessionRuntime(factory.Create);
        SetPrivateField(runtime, "transport", connectedTransport);
        SetPrivateField(runtime, "role", SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "transportState", TransportState.Connected);
        SetPrivateField(runtime, "sessionCts", new CancellationTokenSource());
        InvokePrivateMethod(runtime, "OnRemoteSessionEnded", connectedTransport, EventArgs.Empty);
        await WaitUntilAsync(() => factory.CreateCount >= 1 && runtime.State == SessionRuntimeState.Waiting && runtime.TransportLifecycleState is TransportState.Connecting or TransportState.BridgeStarting && string.Equals(runtime.StatusText, "Waiting for help requests…", StringComparison.Ordinal), TimeSpan.FromSeconds(3));
        Assert.Equal(SessionRuntimeState.Waiting, runtime.State);
        Assert.Equal("Waiting for help requests…", runtime.StatusText);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelperViewModel_StaleAutoListenCallback_DoesNotResetActiveHelperConnect()
    {
        var factory = new CountingTransportFactory(static () => new ScriptedSignalingTransport());
        var transportConfig = CreateDevLocalTestConfig();
        using var runtime = new SessionRuntime(factory.Create);
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, runtime, connectFailureCooldown: TimeSpan.Zero);
        var initialCreateCount = factory.CreateCount;
        SetPrivateField(runtime, "state", SessionRuntimeState.Idle);
        SetPrivateField(runtime, "transportState", TransportState.TransportInitializing);
        var staleAutoListenTask = Assert.IsAssignableFrom<Task>(InvokePrivateMethod(helper, "StartListeningAsync"));
        await staleAutoListenTask;
        Assert.Equal(initialCreateCount, factory.CreateCount);
        Assert.Equal(TransportState.TransportInitializing, runtime.TransportLifecycleState);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelperViewModel_PassiveWaiting_SuppressesConnectingTransientBanner()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, runtime, connectFailureCooldown: TimeSpan.Zero);
        SetPrivateField(runtime, "state", SessionRuntimeState.Waiting);
        SetPrivateField(runtime, "transientStatusVisible", true);
        SetPrivateField(runtime, "transientStatusText", "Connecting... (attempt 1)");
        SetPrivateField(runtime, "transientStatusCanCancel", true);
        SetPrivateField(helper, "connectionState", "Waiting");
        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Waiting);
        InvokePrivateMethod(helper, "SyncTransientStatusFromRuntime");
        Assert.False(helper.ShowTransientBanner);
        Assert.True(string.IsNullOrWhiteSpace(helper.TransientBannerText));
        Assert.False(helper.CanCancelTransient);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelperViewModel_CancelTransientWhileConnecting_ReturnsToIdle_AndCodeInputRemainsEditable()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, runtime, connectFailureCooldown: TimeSpan.Zero);
        CreateValidatedInviteForTarget(new PeerAddress("scripted.connect.cancel"), out var inviteToken);
        helper.CodeInput = inviteToken;
        SetPrivateField(runtime, "state", SessionRuntimeState.Connecting);
        SetPrivateField(helper, "isConnecting", true);
        SetPrivateField(helper, "connectionState", "Connecting");
        SetPrivateField(helper, "showTransientBanner", true);
        SetPrivateField(helper, "canCancelTransient", true);
        var cancelTask = Assert.IsAssignableFrom<Task>(InvokePrivateMethod(helper, "CancelTransientAsync"));
        await cancelTask;
        await WaitUntilAsync(() => runtime.State == SessionRuntimeState.Waiting && helper.ConnectionState == "Waiting" && !helper.IsConnecting && !helper.ShowTransientBanner && helper.ConnectCommand.CanExecute(null), TimeSpan.FromSeconds(3));
        helper.CodeInput = "nlink-invite.editable";
        Assert.Equal("nlink-invite.editable", helper.CodeInput);
        Assert.True(helper.ConnectCommand.CanExecute(null));
        Assert.False(helper.ShowTransientBanner);
        Assert.True(string.IsNullOrWhiteSpace(helper.TransientBannerText));
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelperCancelDuringConnecting_ClearsHelpeeAllowPanel_AndRotatesCode()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-helper-cancel-flow-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-helper-cancel-flow-" + Guid.NewGuid().ToString("N")));
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helpeeRuntime);
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime, connectFailureCooldown: TimeSpan.Zero);
        var initialInvite = await WaitForShareInviteAsync(helpee);
        helper.CodeInput = await WaitForShareInviteAsync(helpee);
        var connectTask = helper.ConnectCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => helpee.IsIncomingRequestView && helpee.ShowIncomingRequestPanel && helpee.HasIncomingRequest, TimeSpan.FromSeconds(3));
        await helper.CancelTransientCommand.ExecuteAsync(null);
        await connectTask.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => !helpee.IsIncomingRequestView && !helpee.ShowIncomingRequestPanel && !helpee.HasIncomingRequest && helpee.ShowWaitingPanel && !string.IsNullOrWhiteSpace(helpee.ShareInvite), TimeSpan.FromSeconds(8));
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelpeePreviewVerificationCode_IsShown_WhenBootstrapPayloadContainsHelperId()
    {
        using var unboundInviteOptIn = new EnvironmentOverride(NLink.App.Configuration.AppFeatureFlags.AllowInsecureUnboundPublicInvitesEnvVar, null);
        var transportConfig = CreateNknTestConfig();
        using var helpeeRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helpeeRuntime);
        var helperAddress = new PeerAddress("nlink-helper.connect.target");
        var helperVerificationIdentity = new PeerAddress("nlink-helper.verification.identity");
        var payload = HelperBootstrapQrPayload.Format(HelperBootstrapPayload.Create(helperAddress, helperId: HelperIdentityTokenCodec.Encode(helperVerificationIdentity)));
        helpee.InviteHelperIdentityInput = payload;
        Assert.True(helpee.HasVerifiedInviteHelperIdentity);
        Assert.True(helpee.HasVerifiedInviteHelperVerificationCode);
        Assert.Equal(HelperVerificationCodeFormatter.Format(helperVerificationIdentity), helpee.VerifiedInviteHelperVerificationCode);
        Assert.Equal(helperVerificationIdentity.Value, helpee.VerifiedInviteTechnicalHelperIdentityText);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelpeePreviewVerificationCode_IsHidden_WhenBootstrapPayloadHasNoHelperId()
    {
        using var unboundInviteOptIn = new EnvironmentOverride(NLink.App.Configuration.AppFeatureFlags.AllowInsecureUnboundPublicInvitesEnvVar, null);
        var transportConfig = CreateNknTestConfig();
        using var helpeeRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helpeeRuntime);
        var helperAddress = new PeerAddress("nlink-helper.connect.target.noid");
        var payload = HelperBootstrapQrPayload.Format(HelperBootstrapPayload.Create(helperAddress));
        helpee.InviteHelperIdentityInput = payload;
        Assert.True(helpee.HasVerifiedInviteHelperIdentity);
        Assert.False(helpee.HasVerifiedInviteHelperVerificationCode);
        Assert.Equal(string.Empty, helpee.VerifiedInviteHelperVerificationCode);
        Assert.Equal(helperAddress.Value, helpee.VerifiedInviteTechnicalHelperIdentityText);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelperVerificationCode_IsHiddenUntilStableSessionSecurityHelperIdentityExists()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime);
        var localTransport = new ScriptedSignalingTransport(localPeerAddress: "helper.local.identity");
        SetPrivateField(helperRuntime, "transport", localTransport);
        Assert.False(helper.HasHelperVerificationCode);
        Assert.False(helper.ShowHelperVerificationCode);
        SetPrivateField(helperRuntime, "sessionSecurityState", SessionSecurityState.CreateHelperPending(new SessionId("session-verify"), new PeerAddress("helpee.target.identity"), new PeerAddress("helper.stable.identity"), inviteValidated: true));
        var expected = HelperVerificationCodeFormatter.Format(new PeerAddress("helper.stable.identity"));
        Assert.Equal(expected, helper.HelperVerificationCode);
        Assert.Equal("helper.stable.identity", helper.HelperTechnicalIdentityText);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelperHeaderVerificationCode_IsHidden_WhileConnecting_WhenBootstrapIdentityIsNotAuthoritative()
    {
        using var unboundInviteOptIn = new EnvironmentOverride(NLink.App.Configuration.AppFeatureFlags.AllowInsecureUnboundPublicInvitesEnvVar, null);
        var transportConfig = CreateNknTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime);
        var bootstrapHelperIdentity = new PeerAddress("nlink-bootstrap.shared.identity");
        var boundHelperIdentity = new PeerAddress("nlink-bound.preview.identity");
        var stableSessionHelperIdentity = new PeerAddress("nlink-session.security.identity");
        SetPrivateField(helperRuntime, "transport", new ScriptedSignalingTransport(localPeerAddress: "nlink-runtime.local.identity"));
        SetPrivateField(helperRuntime, "sessionSecurityState", SessionSecurityState.CreateHelperPending(new SessionId("session-preview-pin"), new PeerAddress("helpee.preview.target"), stableSessionHelperIdentity, inviteValidated: true));
        SetPrivateField(helper, "bootstrapHelperIdentity", bootstrapHelperIdentity);
        SetPrivateField(helper, "bootstrapHelperIdentityIsAuthoritative", false);
        var invite = CreateValidatedInviteForTarget(new PeerAddress("helpee.preview.target"), out var rawToken, boundHelperAddress: boundHelperIdentity);
        helper.CodeInput = rawToken;
        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Connecting);
        Assert.Equal(boundHelperIdentity, invite.BoundHelperAddress);
        Assert.False(helper.ShowHeaderVerificationCode);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelperHeaderVerificationCode_IsHidden_WhenConnectionFails_WithoutAuthoritativeBootstrapIdentity()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime);
        var bootstrapHelperIdentity = new PeerAddress("nlink-bootstrap.shared.identity");
        var boundHelperIdentity = new PeerAddress("nlink-bound.preview.identity");
        var stableSessionHelperIdentity = new PeerAddress("nlink-session.security.identity");
        SetPrivateField(helperRuntime, "transport", new ScriptedSignalingTransport(localPeerAddress: "nlink-runtime.local.identity"));
        SetPrivateField(helperRuntime, "sessionSecurityState", SessionSecurityState.CreateHelperPending(new SessionId("session-preview-pin"), new PeerAddress("helpee.preview.target"), stableSessionHelperIdentity, inviteValidated: true));
        SetPrivateField(helper, "bootstrapHelperIdentity", bootstrapHelperIdentity);
        SetPrivateField(helper, "bootstrapHelperIdentityIsAuthoritative", false);
        _ = CreateValidatedInviteForTarget(new PeerAddress("helpee.preview.target"), out var rawToken, boundHelperAddress: boundHelperIdentity);
        helper.CodeInput = rawToken;
        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Failed);
        var expected = HelperVerificationCodeFormatter.Format(stableSessionHelperIdentity);
        Assert.True(helper.ShowHeaderVerificationCode);
        Assert.Equal(expected, helper.HeaderVerificationCodeText);
    }

}
