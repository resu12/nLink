using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using NLink.App.Services;
using NLink.App.ViewModels;
using NLink.App.Views;
using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.Metrics;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.SmokeTests;

[Collection(AvaloniaHeadlessUiCollection.Name)]
[Trait("Area", "Gui")]
public sealed class Beta3SessionShellLayoutSmokeTests : Beta3DefaultUiSmokeTestBase
{
    public Beta3SessionShellLayoutSmokeTests(Beta3DefaultUiFixture fixture) : base(fixture) { }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelperPage_DefaultShell_ContainsChatControls_AndHidesInlineDisconnect()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            using var context = await CreateConnectedSessionContextAsync();
            var view = new HelperPageView
            {
                DataContext = context.Helper
            };
            var window = new Window
            {
                Width = 1080,
                Height = 760,
                Content = view
            };
            window.Show();
            try
            {
                await FlushUiAsync();
                Assert.True(NLink.App.Configuration.FeatureFlags.EnableSessionHeader);
                Assert.NotNull(FindFirstControlByAutomationId(window, "Chat.Messages"));
                Assert.NotNull(FindFirstControlByAutomationId(window, "Chat.Input"));
                Assert.NotNull(FindFirstControlByAutomationId(window, "Chat.Send"));
                var disconnect = FindFirstControlByAutomationId(window, "Chat.Disconnect");
                Assert.NotNull(disconnect);
                Assert.False(disconnect!.IsVisible);
                Assert.NotNull(FindFirstDescendant<SessionHeaderView>(window));
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeePage_DefaultShell_ContainsChatControls_AndHidesInlineDisconnect()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            using var context = await CreateConnectedSessionContextAsync();
            var view = new HelpeePageView
            {
                DataContext = context.Helpee
            };
            var window = new Window
            {
                Width = 1080,
                Height = 760,
                Content = view
            };
            window.Show();
            try
            {
                await FlushUiAsync();
                Assert.True(NLink.App.Configuration.FeatureFlags.EnableSessionHeader);
                Assert.NotNull(FindFirstControlByAutomationId(window, "Chat.Messages"));
                Assert.NotNull(FindFirstControlByAutomationId(window, "Chat.Input"));
                Assert.NotNull(FindFirstControlByAutomationId(window, "Chat.Send"));
                var disconnect = FindFirstControlByAutomationId(window, "Chat.Disconnect");
                Assert.NotNull(disconnect);
                Assert.False(disconnect!.IsVisible);
                Assert.NotNull(FindFirstDescendant<SessionHeaderView>(window));
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SessionShell_ResponsiveResize_WideNarrowWide_KeepsControlsAvailable()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            var shell = new SessionShellView
            {
                MainContent = new Border
                {
                    [AutomationProperties.AutomationIdProperty] = "Responsive.Main",
                    MinWidth = 620,
                },
                ChatContent = new Border
                {
                    [AutomationProperties.AutomationIdProperty] = "Responsive.Chat",
                },
                DataContext = new ConnectedChatShellContext(),
            };
            var window = new Window
            {
                Width = 1400,
                Height = 760,
                Content = shell
            };
            window.Show();
            try
            {
                await FlushUiAsync();
                shell.ShowScreenShareAction = true;
                shell.ScreenShareCommand?.Execute(null);
                await FlushUiAsync();
                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Responsive.Main"));
                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Responsive.Chat"));
                window.Width = 760;
                await FlushUiAsync();
                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Responsive.Main"));
                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Responsive.Chat"));
                window.Width = 1400;
                await FlushUiAsync();
                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Responsive.Main"));
                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Responsive.Chat"));
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelperPage_DefaultShell_EndSessionButton_StartsDisabled()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            var transportConfig = NLink.App.Configuration.TransportRuntimeConfig.Select();
            using var runtime = new SessionRuntime(() => new NLink.Infra.DevLocal.DevLocalTransport());
            using var helper = new HelperPageViewModel(cancelAction: static () =>
            {
            }, transportConfig, runtime, openDiagnosticsAction: static () =>
            {
            }, clipboardService: new TestClipboardService(), shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null));
            var view = new HelperPageView
            {
                DataContext = helper
            };
            var window = new Window
            {
                Width = 1080,
                Height = 760,
                Content = view
            };
            try
            {
                window.Show();
                await FlushUiAsync();
                var header = FindFirstDescendant<SessionHeaderView>(window);
                Assert.NotNull(header);
                var endButton = header.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => string.Equals(b.Content?.ToString(), "End session", StringComparison.Ordinal));
                Assert.NotNull(endButton);
                Assert.False(helper.CanEndSession);
                Assert.False(endButton!.IsEnabled);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SessionHeader_TunaNegotiationPulsesWithExactTooltip_AndWaitingApprovalIsSteady()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            var header = new SessionHeaderView
            {
                TunaActive = false,
                TunaStatusReason = "post_tuna_fallback_route_active",
            };

            SetPrivateField(header, "tunaUnlockToggleOn", true);
            SetPrivateField(header, "tunaRuntimeStatus", "listener_starting");
            InvokePrivate(header, "UpdateTunaVisualState");

            Assert.Equal(
                "Starting Tuna listener. Regular NKN stays connected until ready. This computer is paying as the Tuna listener.",
                header.TunaPictogramTip);
            Assert.True(header.TunaGlowOpacity > 0d);
            SetPrivateField(header, "tunaPulseStartedUtc", DateTime.UtcNow.AddMilliseconds(-350));
            InvokePrivate(header, "OnTunaPulseTimerTick", null, EventArgs.Empty);
            Assert.NotEqual(1d, header.TunaPulseScale);

            SetPrivateField(header, "tunaRuntimeStatus", "waiting_for_approved_session");
            InvokePrivate(header, "UpdateTunaVisualState");

            Assert.Equal("Tuna is unavailable. Regular NKN is being used.", header.TunaPictogramTip);
            Assert.Equal(0d, header.TunaGlowOpacity);
            Assert.Equal(1d, header.TunaPulseScale);
            InvokePrivate(header, "StopTunaPulse");
            await FlushUiAsync();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SessionHeader_TunaEstablishmentRefreshesBothTipsAndPulse_WithoutRuntimeRefresh()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            var header = new SessionHeaderView { RoleText = "Helpee" };
            var window = new Window { Content = header };
            window.Show();
            try
            {
                await FlushUiAsync();
                SetPrivateField(header, "tunaUnlockToggleOn", true);
                SetPrivateField(header, "tunaRuntimeStatus", "listener_paths_starting");
                var timer = Assert.IsType<DispatcherTimer>(typeof(SessionHeaderView)
                    .GetField("tunaPulseTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(header));

                header.TunaStatusReason = "preflight_listener_unavailable";
                Assert.StartsWith("Starting Tuna relay paths.", header.TunaPictogramTip);
                Assert.StartsWith("Starting Tuna relay paths.", header.TunaUnlockTip);
                Assert.True(timer.IsEnabled);
                SetPrivateField(header, "tunaPulseStartedUtc", DateTime.UtcNow.AddMilliseconds(-350));
                InvokePrivate(header, "OnTunaPulseTimerTick", null, EventArgs.Empty);
                Assert.NotEqual(1d, header.TunaPulseScale);

                header.TunaStatusReason = "waiting_for_answer";
                Assert.StartsWith("Negotiating Tuna acceleration.", header.TunaPictogramTip);
                Assert.Equal("Negotiating Tuna acceleration.", header.TunaUnlockTip);
                Assert.True(timer.IsEnabled);

                InvokePrivate(header, "SetTunaUnlockMessage", "Temporary user feedback");
                header.TunaStatusReason = "runtime_unlock_pending_route_commit";
                Assert.Equal("Temporary user feedback", header.TunaUnlockTip);
                SetPrivateField(header, "tunaUnlockMessageUntilUtc", DateTimeOffset.UtcNow.AddSeconds(-1));

                header.TunaActive = true;
                Assert.Contains("ready", header.TunaUnlockTip);
                Assert.False(timer.IsEnabled);
                Assert.Equal(1d, header.TunaPulseScale);
                header.TunaActive = false;
                Assert.True(timer.IsEnabled);

                foreach (var reason in new[] { "header_switch_off", "remote_header_switch_off", "provider_paths_wait_timeout", "handoff_rejected" })
                {
                    header.TunaStatusReason = reason;
                    Assert.False(timer.IsEnabled);
                    Assert.Equal(1d, header.TunaPulseScale);
                    Assert.Equal(header.TunaPictogramTip, header.TunaUnlockTip);
                }

                SetPrivateField(header, "tunaUnlockToggleOn", false);
                SetPrivateField(header, "tunaRuntimeStatus", "off");
                header.TunaStatusReason = "preflight_listener_unavailable";
                Assert.False(timer.IsEnabled);
                Assert.Contains("off", header.TunaUnlockTip);

                SetPrivateField(header, "tunaUnlockToggleOn", true);
                SetPrivateField(header, "tunaRuntimeStatus", "listener_ready");
                InvokePrivate(header, "UpdateTunaVisualState");
                Assert.True(timer.IsEnabled);
                window.Content = null;
                Assert.False(timer.IsEnabled);
                Assert.Equal(1d, header.TunaPulseScale);
            }
            finally
            {
                InvokePrivate(header, "StopTunaPulse");
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData("Helper")]
    [InlineData("Helpee")]
    [Trait("Category", "Smoke")]
    public async Task SessionHeader_TunaListenerProgressRefreshesTipsAndPulse_OverPreSessionIneligibility(string role)
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            var header = new SessionHeaderView
            {
                RoleText = role,
                TunaStatusReason = "session_security_state_not_eligible",
            };
            var window = new Window { Content = header };
            window.Show();
            try
            {
                await FlushUiAsync();
                var timer = Assert.IsType<DispatcherTimer>(typeof(SessionHeaderView)
                    .GetField("tunaPulseTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(header));
                var fish = Assert.Single(header.GetVisualDescendants().OfType<Viewbox>());

                InvokePrivate(header, "ApplyTunaUnlockToggleState", true, true, true, "", "waiting_for_approved_session");
                Assert.Equal("Tuna is unavailable. Regular NKN is being used.", header.TunaPictogramTip);
                Assert.False(timer.IsEnabled);

                foreach (var status in new[] { "listener_starting", "listener_paths_starting", "provider_paths_ready", "waiting_for_peer_dial" })
                {
                    InvokePrivate(header, "ApplyTunaUnlockToggleState", true, true, true, "", status);
                    var expected = TunaStatusPresentationMapper.FromState(false, null, status, true).Text;
                    Assert.Equal(expected + " This computer is paying as the Tuna listener.", header.TunaPictogramTip);
                    Assert.Equal(expected, header.TunaUnlockTip);
                    Assert.Equal(header.TunaPictogramTip, ToolTip.GetTip(fish));
                    Assert.True(timer.IsEnabled);
                    Assert.True(header.TunaGlowOpacity > 0d);
                    SetPrivateField(header, "tunaPulseStartedUtc", DateTime.UtcNow.AddMilliseconds(-350));
                    InvokePrivate(header, "OnTunaPulseTimerTick", null, EventArgs.Empty);
                    Assert.NotEqual(1d, header.TunaPulseScale);
                    Assert.False(header.TunaActive);
                    Assert.Equal("session_security_state_not_eligible", header.TunaStatusReason);
                }

                header.TunaActive = true;
                Assert.Contains("ready", header.TunaPictogramTip);
                Assert.False(timer.IsEnabled);
                Assert.Equal(1d, header.TunaPulseScale);
            }
            finally
            {
                InvokePrivate(header, "StopTunaPulse");
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData("listener_ready", true, true)]
    [InlineData("dialer_ready", true, false)]
    [InlineData("inactive", false, false)]
    [Trait("Category", "Smoke")]
    public async Task SessionHeader_TunaFinishedFileStopsPendingPulse_AndFreshNegotiationCanPulse(
        string runtimeStatus, bool unlockOn, bool localPayer)
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            var header = new SessionHeaderView { TunaStatusReason = "runtime_unlock_pending_route_commit" };
            SetPrivateField(header, "tunaUnlockToggleOn", unlockOn);
            SetPrivateField(header, "tunaRuntimeStatus", runtimeStatus);
            var timer = Assert.IsType<DispatcherTimer>(typeof(SessionHeaderView)
                .GetField("tunaPulseTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(header));
            try
            {
                InvokePrivate(header, "UpdateTunaVisualState");
                Assert.Equal(unlockOn, timer.IsEnabled);
                header.TunaActive = true;
                Assert.False(timer.IsEnabled);
                var activeColor = ((ISolidColorBrush)header.TunaPictogramBrush).Color;
                Assert.Equal(localPayer ? Color.FromRgb(255, 211, 91) : Color.FromRgb(125, 220, 255), activeColor);
                // An independently active session/Screen route wins when the file ends.
                header.TunaStatusReason = "file_transfer_tuna_idle";
                Assert.True(header.TunaActive);
                Assert.Contains("ready", header.TunaPictogramTip);
                Assert.Equal(activeColor, ((ISolidColorBrush)header.TunaPictogramBrush).Color);
                Assert.True(header.TunaGlowOpacity > 0d);
                Assert.False(timer.IsEnabled);
                header.TunaActive = false;
                InvokePrivate(header, "UpdateTunaVisualState");
                Assert.False(timer.IsEnabled);
                Assert.Equal(1d, header.TunaPulseScale);
                Assert.Equal(0d, header.TunaGlowOpacity);
                Assert.Equal(activeColor, ((ISolidColorBrush)header.TunaPictogramBrush).Color);
                Assert.Equal(1d, header.TunaPictogramOpacity);
                Assert.False(header.TunaActive);
                Assert.Equal("Tuna is idle for file transfer.", header.TunaPictogramTip);
                Assert.Equal(header.TunaPictogramTip, header.TunaUnlockTip);
                InvokePrivate(header, "OnTunaPulseTimerTick", null, EventArgs.Empty);
                Assert.False(timer.IsEnabled);
                Assert.Equal(activeColor, ((ISolidColorBrush)header.TunaPictogramBrush).Color);

                header.TunaStatusReason = "waiting_for_answer";
                Assert.Equal(unlockOn, timer.IsEnabled);
                Assert.StartsWith("Negotiating Tuna acceleration.", header.TunaPictogramTip);
                header.TunaActive = true;
                Assert.False(timer.IsEnabled);
                Assert.Equal(activeColor, ((ISolidColorBrush)header.TunaPictogramBrush).Color);

                header.TunaActive = false;
                foreach (var reason in new[] { "header_switch_off", "remote_header_switch_off", "handoff_rejected" })
                {
                    header.TunaStatusReason = reason;
                    Assert.False(timer.IsEnabled);
                    Assert.Equal(Color.FromRgb(111, 122, 136), ((ISolidColorBrush)header.TunaPictogramBrush).Color);
                    Assert.Equal(0d, header.TunaGlowOpacity);
                }

                SetPrivateField(header, "tunaUnlockToggleOn", false);
                SetPrivateField(header, "tunaRuntimeStatus", "locked");
                header.TunaStatusReason = "file_transfer_tuna_idle";
                Assert.False(timer.IsEnabled);
                Assert.Equal(Color.FromRgb(111, 122, 136), ((ISolidColorBrush)header.TunaPictogramBrush).Color);
                Assert.StartsWith("Tuna wallet is locked.", header.TunaPictogramTip);
            }
            finally
            {
                InvokePrivate(header, "StopTunaPulse");
            }
            await FlushUiAsync();
            return true;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData("Helper", false, "locked")]
    [InlineData("Helpee", true, "listener_ready")]
    [Trait("Category", "Smoke")]
    public async Task SessionHeader_DetachedShowsNoConnectionOnBothTips(string role, bool unlockOn, string runtimeStatus)
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            var header = new SessionHeaderView { RoleText = role };
            var window = new Window { Content = header };
            window.Show();
            try
            {
                await FlushUiAsync();
                InvokePrivate(header, "ApplyTunaUnlockToggleState", true, true, unlockOn, "", runtimeStatus);
                var fish = Assert.Single(header.GetVisualDescendants().OfType<Viewbox>());
                var toggle = Assert.IsType<Button>(FindFirstVisibleControlByAutomationId(window, "SessionHeader.TunaUnlockToggle"));
                var detached = TunaHeaderStateProjector.FromSnapshot(new(null, null), "(none)", false, "transport_unwired");
                header.TunaActive = detached.IsActive;
                header.TunaStatusReason = detached.StatusReason;
                header.FileUsesRegularNkn = detached.FileUsesRegularNkn;
                Assert.Equal("No active connection.", ToolTip.GetTip(fish));
                Assert.Equal("No active connection.", ToolTip.GetTip(toggle));
                Assert.Equal(0d, header.TunaGlowOpacity);
            }
            finally
            {
                window.Close();
            }
            return true;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(true, FileTransferDirection.Outbound, "regular_nkn_v4_fast", 4)]
    [InlineData(true, FileTransferDirection.Inbound, "regular_nkn_v4_fast", 4)]
    [InlineData(false, FileTransferDirection.Outbound, "regular_nkn_v4_fast", 4)]
    [InlineData(false, FileTransferDirection.Inbound, "regular_nkn_v4_fast", 4)]
    [InlineData(true, FileTransferDirection.Outbound, "post_tuna_fallback_v6", 6)]
    [InlineData(true, FileTransferDirection.Inbound, "post_tuna_fallback_v6", 6)]
    [InlineData(false, FileTransferDirection.Outbound, "post_tuna_fallback_v6", 6)]
    [InlineData(false, FileTransferDirection.Inbound, "post_tuna_fallback_v6", 6)]
    [InlineData(true, FileTransferDirection.Outbound, "post_tuna_fallback_v6", 6, false)]
    [InlineData(false, FileTransferDirection.Inbound, "post_tuna_fallback_v6", 6, false)]
    [Trait("Category", "Smoke")]
    public async Task SessionHeader_TunaSessionColorAndTooltipSurviveFilePauseAndCancel(
        bool localPayer, FileTransferDirection direction, string route, int protocol, bool sessionReady = true)
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            var header = new SessionHeaderView();
            SetPrivateField(header, "tunaUnlockToggleOn", localPayer);
            SetPrivateField(header, "tunaRuntimeStatus", localPayer ? "listener_ready" : "dialer_ready");
            var timer = Assert.IsType<DispatcherTimer>(typeof(SessionHeaderView)
                .GetField("tunaPulseTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(header));
            var reason = sessionReady ? (localPayer ? "paid_listener_active" : "free_dialer_active") : "post_tuna_fallback_route_active";
            var payerTip = localPayer ? "This computer is paying as the Tuna listener." : "The other computer is paying.";
            var negotiatingTip = localPayer ? "Waiting for the other side to connect to Tuna." : "Negotiating Tuna acceleration.";
            var transfer = new FileTransferTransferSnapshot(
                "session", "transfer", direction,
                direction == FileTransferDirection.Outbound ? FileTransferTransferState.Sending : FileTransferTransferState.Receiving,
                "file.bin", 1024, null, 0, 0, 1, 1024, null, null,
                RouteToken: route, ProtocolVersion: protocol);
            try
            {
                foreach (var (item, usesRegularFile) in new (FileTransferTransferSnapshot?, bool)[]
                {
                    (transfer with { State = direction == FileTransferDirection.Outbound
                        ? FileTransferTransferState.AwaitingAcceptance : FileTransferTransferState.PendingDecision }, false),
                    (transfer with { State = FileTransferTransferState.AwaitingStart }, false),
                    (transfer, true), (transfer with { IsPaused = true }, true), (transfer with { IsPeerPaused = true }, true),
                    (transfer with { State = FileTransferTransferState.Canceled }, false), (null, false),
                })
                {
                    var snapshot = direction == FileTransferDirection.Outbound
                        ? new SessionFileTransferSnapshot(item, null) : new SessionFileTransferSnapshot(null, item);
                    var projection = TunaHeaderStateProjector.FromSnapshot(snapshot, "NKN", sessionReady, reason);
                    header.TunaActive = projection.IsActive;
                    header.TunaStatusReason = projection.StatusReason;
                    header.FileUsesRegularNkn = projection.FileUsesRegularNkn;
                    Assert.Equal(sessionReady, header.TunaActive);
                    var fileTip = usesRegularFile ? " This file uses regular NKN." : "";
                    var expectedTip = sessionReady ? $"Tuna is ready.{fileTip} {payerTip}" : negotiatingTip + fileTip;
                    Assert.Equal(!sessionReady && localPayer ? expectedTip + " " + payerTip : expectedTip, header.TunaPictogramTip);
                    Assert.Equal(expectedTip, header.TunaUnlockTip);
                    Assert.Equal(localPayer ? Color.FromRgb(255, 211, 91) : sessionReady ? Color.FromRgb(125, 220, 255) : Color.FromRgb(111, 122, 136),
                        ((ISolidColorBrush)header.TunaPictogramBrush).Color);
                    Assert.Equal(sessionReady || localPayer ? 1d : 0.58d, header.TunaPictogramOpacity);
                    Assert.Equal(sessionReady || localPayer, header.TunaGlowOpacity > 0d);
                    Assert.Equal(!sessionReady && localPayer, timer.IsEnabled);
                    Assert.Equal(1d, header.TunaPulseScale);
                    Assert.Equal(route, transfer.RouteToken);
                    Assert.Equal(protocol, transfer.ProtocolVersion);
                }
            }
            finally
            {
                InvokePrivate(header, "StopTunaPulse");
            }
            await FlushUiAsync();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeePage_WhenConnected_ShowsVisibleChatImmediately()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            using var context = await CreateConnectedSessionContextAsync();
            var view = new HelpeePageView
            {
                DataContext = context.Helpee
            };
            var window = new Window
            {
                Width = 1080,
                Height = 760,
                Content = view
            };
            window.Show();
            try
            {
                await FlushUiAsync();
                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Chat.Messages"));
                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Chat.Input"));
                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Chat.Send"));
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SessionShell_WhenConnectedArrivesBeforeConnectedPanel_ShowsChatImmediately()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            var context = new MutableConnectedChatShellContext();
            var shell = new SessionShellView
            {
                ChatContent = new Border
                {
                    [AutomationProperties.AutomationIdProperty] = "Shell.Chat",
                },
                DataContext = context,
            };
            var window = new Window
            {
                Width = 1080,
                Height = 760,
                Content = shell
            };
            window.Show();
            try
            {
                await FlushUiAsync();
                context.HeaderStatusText = "Connected";
                await FlushUiAsync();
                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Shell.Chat"));
                context.ShowConnectedPanel = true;
                await FlushUiAsync();
                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Shell.Chat"));
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SessionShell_WhenConnectedInResponsiveWideChatOnly_KeepsStableWideWidth()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var shell = new SessionShellView
            {
                Width = 1080,
                ChatContent = new Border
                {
                    [AutomationProperties.AutomationIdProperty] = "Shell.Chat",
                },
                DataContext = new ConnectedChatShellContext(),
            };
            var window = new Window
            {
                Width = 1080,
                Height = 760,
                Content = shell
            };
            window.Show();
            try
            {
                await FlushUiAsync();
                Assert.True(shell.ShowResponsiveWideChatOnlyLayout);
                var chatOnlyLayout = shell.FindControl<Border>("ResponsiveWideChatOnlyLayout");
                Assert.NotNull(chatOnlyLayout);
                Assert.Equal(420d, chatOnlyLayout!.Width);
                Assert.Equal(420d, chatOnlyLayout.Bounds.Width, precision: 1);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SessionShell_WhenConnectedInResponsiveNarrowChatOnly_KeepsStableWideWidth()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var shell = new SessionShellView
            {
                Width = 760,
                ChatContent = new Border
                {
                    [AutomationProperties.AutomationIdProperty] = "Shell.Chat",
                },
                DataContext = new ConnectedChatShellContext(),
            };
            var window = new Window
            {
                Width = 760,
                Height = 760,
                Content = shell
            };
            window.Show();
            try
            {
                await FlushUiAsync();
                Assert.True(shell.ShowResponsiveNarrowChatOnlyLayout);
                var chatOnlyLayout = shell.FindControl<Border>("ResponsiveNarrowChatOnlyLayout");
                Assert.NotNull(chatOnlyLayout);
                Assert.Equal(420d, chatOnlyLayout!.Width);
                Assert.Equal(420d, chatOnlyLayout.Bounds.Width, precision: 1);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SessionShell_WhenNotConnected_HidesChatPane_EvenIfDataContextPanelFlagIsStale()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var shell = new SessionShellView
            {
                Width = 760,
                MainContent = new Border
                {
                    [AutomationProperties.AutomationIdProperty] = "Shell.Main",
                },
                ChatContent = new Border
                {
                    [AutomationProperties.AutomationIdProperty] = "Shell.Chat",
                },
                DataContext = new StaleDisconnectedShellContext(),
            };
            var window = new Window
            {
                Width = 760,
                Height = 760,
                Content = shell
            };
            window.Show();
            try
            {
                await FlushUiAsync();
                Assert.False(shell.ShowChatPane);
                Assert.NotNull(FindFirstControlByAutomationId(window, "Shell.Main"));
                Assert.Null(FindFirstVisibleControlByAutomationId(window, "Shell.Chat"));
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

}
