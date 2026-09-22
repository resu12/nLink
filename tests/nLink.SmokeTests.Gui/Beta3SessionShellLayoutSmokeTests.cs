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
                Assert.Contains("active", header.TunaUnlockTip);
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
