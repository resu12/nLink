using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
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
using NLink.Core.Metrics;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.SmokeTests;

[Collection(AvaloniaHeadlessUiCollection.Name)]
public sealed class Beta3DefaultUiSmokeTests : IClassFixture<Beta3DefaultUiFixture>
{
    private readonly Beta3DefaultUiFixture fixture;

    public Beta3DefaultUiSmokeTests(Beta3DefaultUiFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task MainWindow_HelperDiagnosticsBack_ReturnsToHelper()
    {
        var services = CreateServicesForMainWindow();
        using var vm = new MainWindowViewModel(services);

        Assert.IsType<HomePageViewModel>(vm.CurrentPage);
        InvokePrivate(vm, "ShowHelperPage");
        Assert.IsType<HelperPageViewModel>(vm.CurrentPage);

        InvokePrivate(vm, "ShowDiagnosticsPage");
        var diagnostics = Assert.IsType<DiagnosticsPageViewModel>(vm.CurrentPage);

        diagnostics.BackCommand.Execute(null);
        Assert.IsType<HelperPageViewModel>(vm.CurrentPage);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task MainWindow_HelpeeDiagnosticsBack_ReturnsToHelpee()
    {
        var services = CreateServicesForMainWindow();
        using var vm = new MainWindowViewModel(services);

        Assert.IsType<HomePageViewModel>(vm.CurrentPage);
        InvokePrivate(vm, "ShowHelpeePage");
        Assert.IsType<HelpeePageViewModel>(vm.CurrentPage);

        InvokePrivate(vm, "ShowDiagnosticsPage");
        var diagnostics = Assert.IsType<DiagnosticsPageViewModel>(vm.CurrentPage);

        diagnostics.BackCommand.Execute(null);
        Assert.IsType<HelpeePageViewModel>(vm.CurrentPage);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelperPage_DefaultShell_ContainsChatControls_AndHidesInlineDisconnect()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            using var context = await CreateConnectedSessionContextAsync();
            var view = new HelperPageView { DataContext = context.Helper };
            var window = new Window { Width = 1080, Height = 760, Content = view };
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
            var view = new HelpeePageView { DataContext = context.Helpee };
            var window = new Window { Width = 1080, Height = 760, Content = view };
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
    public async Task HelpeePage_DefaultShell_PrioritizesQr_AndOmitsRawInviteFallback()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            var transportConfig = NLink.App.Configuration.TransportRuntimeConfig.Select();
            using var runtime = new SessionRuntime(() => new NLink.Infra.DevLocal.DevLocalTransport());
            using var helpee = new HelpeePageViewModel(
                cancelAction: static () => { },
                transportConfig,
                runtime,
                openDiagnosticsAction: static () => { },
                clipboardService: new TestClipboardService(),
                shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null));

            var view = new HelpeePageView { DataContext = helpee };
            var window = new Window { Width = 1080, Height = 760, Content = view };
            try
            {
                window.Show();
                await FlushUiAsync();

                var inviteQr = await WaitForLayoutConditionAsync(
                    window,
                    () => FindFirstControlByAutomationId(window, "Helpee.InviteQr") as Image is { IsVisible: true } control &&
                          control.Bounds.Width > 0 &&
                          control.Bounds.Height > 0
                        ? control
                        : null,
                    TimeSpan.FromSeconds(2),
                    "helpee invite qr");

                Assert.NotNull(inviteQr);
                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Helpee.ShareInvite"));
                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Helpee.CopyInvite"));
                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Helpee.RefreshInvite"));
                Assert.Null(FindFirstControlByAutomationId(window, "Helpee.InviteDetails"));
                Assert.Null(FindFirstControlByAutomationId(window, "Helpee.InviteText"));
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
    public async Task HelpeePage_WaitingView_MakesQrProminent_AndKeepsRefreshSecondary()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            var transportConfig = NLink.App.Configuration.TransportRuntimeConfig.Select();
            using var runtime = new SessionRuntime(() => new NLink.Infra.DevLocal.DevLocalTransport());
            using var helpee = new HelpeePageViewModel(
                cancelAction: static () => { },
                transportConfig,
                runtime,
                openDiagnosticsAction: static () => { },
                clipboardService: new TestClipboardService(),
                shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null));

            var view = new HelpeePageView { DataContext = helpee };
            var window = new Window { Width = 860, Height = 980, Content = view };
            try
            {
                window.Show();
                await FlushUiAsync();

                var inviteQr = await WaitForLayoutConditionAsync(
                    window,
                    () => FindFirstControlByAutomationId(window, "Helpee.InviteQr") as Image is { IsVisible: true } control &&
                          control.Bounds.Width > 0 &&
                          control.Bounds.Height > 0
                        ? control
                        : null,
                    TimeSpan.FromSeconds(2),
                    "helpee invite qr");

                var copyInvite = await WaitForLayoutConditionAsync(
                    window,
                    () => FindFirstControlByAutomationId(window, "Helpee.CopyInvite") as Button is { IsVisible: true } control &&
                          control.Bounds.Width > 0 &&
                          control.Bounds.Height > 0
                        ? control
                        : null,
                    TimeSpan.FromSeconds(2),
                    "helpee copy invite button");

                var shareInvite = await WaitForLayoutConditionAsync(
                    window,
                    () => FindFirstControlByAutomationId(window, "Helpee.ShareInvite") as Button is { IsVisible: true } control &&
                          control.Bounds.Width > 0 &&
                          control.Bounds.Height > 0
                        ? control
                        : null,
                    TimeSpan.FromSeconds(2),
                    "helpee share invite button");

                var refreshInvite = await WaitForLayoutConditionAsync(
                    window,
                    () => FindFirstControlByAutomationId(window, "Helpee.RefreshInvite") as Button is { IsVisible: true } control &&
                          control.Bounds.Width > 0 &&
                          control.Bounds.Height > 0
                        ? control
                        : null,
                    TimeSpan.FromSeconds(2),
                    "helpee refresh invite button");

                Assert.True(inviteQr.Bounds.Width >= 200, $"Expected invite QR width >= 200, got {inviteQr.Bounds.Width:N1}.");
                Assert.True(inviteQr.Bounds.Height >= 200, $"Expected invite QR height >= 200, got {inviteQr.Bounds.Height:N1}.");
                Assert.True(copyInvite.Bounds.Width >= 160, $"Expected copy invite button width >= 160, got {copyInvite.Bounds.Width:N1}.");
                Assert.True(shareInvite.Bounds.Width >= 160, $"Expected share invite button width >= 160, got {shareInvite.Bounds.Width:N1}.");
                Assert.True(refreshInvite.Bounds.Height < shareInvite.Bounds.Height, $"Expected refresh invite button height < share invite height, got {refreshInvite.Bounds.Height:N1} vs {shareInvite.Bounds.Height:N1}.");

                Assert.True(
                    Math.Abs(copyInvite.Bounds.Y - shareInvite.Bounds.Y) < 2,
                    $"Expected copy/share invite buttons on the same row, got Y={copyInvite.Bounds.Y:N1} and {shareInvite.Bounds.Y:N1}.");
                Assert.True(
                    refreshInvite.Bounds.Y - copyInvite.Bounds.Y > 20,
                    $"Expected invite actions to wrap into a second row, got Y={copyInvite.Bounds.Y:N1} and {refreshInvite.Bounds.Y:N1}.");
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
            var window = new Window { Width = 1400, Height = 760, Content = shell };
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
            using var helper = new HelperPageViewModel(
                cancelAction: static () => { },
                transportConfig,
                runtime,
                openDiagnosticsAction: static () => { },
                clipboardService: new TestClipboardService(),
                shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null));

            var view = new HelperPageView { DataContext = helper };
            var window = new Window { Width = 1080, Height = 760, Content = view };
            try
            {
                window.Show();
                await FlushUiAsync();

                var header = FindFirstDescendant<SessionHeaderView>(window);
                Assert.NotNull(header);
                var endButton = header.GetVisualDescendants().OfType<Button>().FirstOrDefault(b =>
                    string.Equals(b.Content?.ToString(), "End session", StringComparison.Ordinal));

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
    public async Task HelperPage_WaitingView_ShowsVisibleFallbackLinks_AndQuietRecentTargets()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            const string recentTarget = "nlink-0dfa1ae.f2e91df1fb70f4897f7390c176055ff17bb26d99e0ee66194";
            var transportConfig = NLink.App.Configuration.TransportRuntimeConfig.Select();
            using var runtime = new SessionRuntime(() => new NLink.Infra.DevLocal.DevLocalTransport());
            using var helper = new HelperPageViewModel(
                cancelAction: static () => { },
                transportConfig,
                runtime,
                openDiagnosticsAction: static () => { },
                clipboardService: new TestClipboardService(),
                shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null),
                recentConnectTargetsStore: new FixedRecentConnectTargetsStore(recentTarget));

            var view = new HelperPageView { DataContext = helper };
            var window = new Window { Width = 820, Height = 900, Content = view };
            try
            {
                window.Show();
                await FlushUiAsync();

                var scanQrButton = await WaitForLayoutConditionAsync(
                    window,
                    () => FindFirstControlByAutomationId(window, "Helper.ScanQr") as Button is { IsVisible: true } control &&
                          control.Bounds.Width > 0 &&
                          control.Bounds.Height > 0
                        ? control
                        : null,
                    TimeSpan.FromSeconds(2),
                    "helper scan qr action");

                var recentButton = await WaitForLayoutConditionAsync(
                    window,
                    () => FindFirstControlByAutomationId(window, "Helper.RecentTarget") as Button is { IsVisible: true } control &&
                          control.Bounds.Width > 0 &&
                          control.Bounds.Height > 0
                        ? control
                        : null,
                    TimeSpan.FromSeconds(2),
                    "helper recent target");

                var installLink = await WaitForLayoutConditionAsync(
                    window,
                    () => FindFirstControlByAutomationId(window, "Helper.CopyInstallLink") as Button is { IsVisible: true } control &&
                          control.Bounds.Width > 0 &&
                          control.Bounds.Height > 0
                        ? control
                        : null,
                    TimeSpan.FromSeconds(2),
                    "helper install link");

                var nftpLink = await WaitForLayoutConditionAsync(
                    window,
                    () => FindFirstControlByAutomationId(window, "Helper.UseNftp") as Button is { IsVisible: true } control &&
                          control.Bounds.Width > 0 &&
                          control.Bounds.Height > 0
                        ? control
                        : null,
                    TimeSpan.FromSeconds(2),
                    "helper nftp link");

                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Helper.CodeInput"));
                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Helper.Connect"));
                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Helper.PasteFromClipboard"));
                Assert.Null(FindFirstControlByAutomationId(window, "Helper.ConnectHint"));
                Assert.Equal("Scan QR", scanQrButton.Content?.ToString());
                Assert.Null(FindFirstControlByAutomationId(window, "Helper.SecondaryOptions"));

                var recentText = recentButton.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .FirstOrDefault(text =>
                        string.Equals(text.Text, recentTarget, StringComparison.Ordinal));

                Assert.Equal("Copy install link", installLink.Content?.ToString());
                Assert.Equal("Use file transfer instead", nftpLink.Content?.ToString());
                Assert.True(
                    recentButton.Bounds.Width >= 300,
                    $"Expected recent target width >= 300, got {recentButton.Bounds.Width:N1}.");
                Assert.True(
                    recentButton.Bounds.Width <= 580,
                    $"Expected recent target width <= 580, got {recentButton.Bounds.Width:N1}.");
                Assert.NotNull(recentText);
                Assert.Equal(TextWrapping.NoWrap, recentText!.TextWrapping);
                Assert.Equal(TextTrimming.CharacterEllipsis, recentText.TextTrimming);
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
    public async Task HelpeePage_WhenConnected_ShowsVisibleChatImmediately()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            using var context = await CreateConnectedSessionContextAsync();
            var view = new HelpeePageView { DataContext = context.Helpee };
            var window = new Window { Width = 1080, Height = 760, Content = view };
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

            var window = new Window { Width = 1080, Height = 760, Content = shell };
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
    public async Task HelpeePage_WaitingView_DoesNotRenderEmptyChatPane()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            var transportConfig = NLink.App.Configuration.TransportRuntimeConfig.Select();
            using var runtime = new SessionRuntime(() => new NLink.Infra.DevLocal.DevLocalTransport());
            using var helpee = new HelpeePageViewModel(
                cancelAction: static () => { },
                transportConfig,
                runtime,
                openDiagnosticsAction: static () => { },
                clipboardService: new TestClipboardService(),
                shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null));

            var view = new HelpeePageView { DataContext = helpee };
            var window = new Window { Width = 1080, Height = 760, Content = view };
            try
            {
                window.Show();
                await FlushUiAsync();

                Assert.NotNull(FindFirstControlByAutomationId(window, "Helpee.CopyInvite"));
                Assert.Null(FindFirstVisibleControlByAutomationId(window, "Chat.Messages"));
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
    public async Task SessionShell_ScreenShareScaffold_ToggleControlsPlaceholderVisibility()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var previous = Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD");
            try
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", "1");
                var shell = new SessionShellView
                {
                    ShowScreenShareAction = true,
                    DataContext = new ConnectedShellContext(),
                };

                Assert.True(shell.EffectiveShowScreenShareAction);
                Assert.False(shell.ShowScreenSharePane);

                shell.ScreenShareCommand?.Execute(null);
                await FlushUiAsync();
                Assert.True(shell.ShowScreenSharePane);

                shell.ScreenShareCommand?.Execute(null);
                await FlushUiAsync();
                Assert.False(shell.ShowScreenSharePane);
            }
            finally
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", previous);
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SessionShell_WhenScreenSharePaneVisible_HidesContentPlaceholder()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var previous = Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD");
            try
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", "1");
                var shell = new SessionShellView
                {
                    ShowScreenShareAction = true,
                    DataContext = new ConnectedShellContext(),
                };

                var window = new Window { Width = 1080, Height = 760, Content = shell };
                window.Show();

                try
                {
                    await FlushUiAsync();

                    var placeholder = window.GetVisualDescendants()
                        .OfType<TextBlock>()
                        .FirstOrDefault(x => string.Equals(x.Text, "Content", StringComparison.Ordinal));
                    Assert.NotNull(placeholder);

                    shell.ScreenShareCommand?.Execute(null);
                    await FlushUiAsync();

                    Assert.True(shell.ShowScreenSharePane);
                    Assert.False(placeholder!.IsVisible);
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", previous);
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SessionShell_WhenMainContentExists_PlaceholderStaysHidden_BeforeAndAfterScreenShareStops()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var previous = Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD");
            try
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", "1");
                var context = new MutableConnectedScreenShareShellContext
                {
                    HeaderStatusText = "Connected",
                    ShowConnectedPanel = true,
                    CanShowScreenShareAction = true,
                };

                var shell = new SessionShellView
                {
                    Width = 1080,
                    ShowScreenShareAction = true,
                    MainContent = new Border
                    {
                        [AutomationProperties.AutomationIdProperty] = "Shell.Main",
                    },
                    ChatContent = new Border
                    {
                        [AutomationProperties.AutomationIdProperty] = "Shell.Chat",
                    },
                    DataContext = context,
                };

                var window = new Window { Width = 1080, Height = 760, Content = shell };
                window.Show();

                try
                {
                    await FlushUiAsync();

                    var placeholder = window.GetVisualDescendants()
                        .OfType<TextBlock>()
                        .FirstOrDefault(x => string.Equals(x.Text, "Content", StringComparison.Ordinal));
                    Assert.NotNull(placeholder);
                    Assert.False(placeholder!.IsVisible);

                    shell.ScreenShareCommand?.Execute(null);
                    await WaitUntilAsync(() => shell.ShowScreenSharePane, TimeSpan.FromSeconds(2));
                    await FlushUiAsync();
                    Assert.False(placeholder.IsVisible);

                    shell.ScreenShareCommand?.Execute(null);
                    await WaitUntilAsync(() => !shell.ShowScreenSharePane, TimeSpan.FromSeconds(2));
                    await FlushUiAsync();
                    Assert.False(placeholder.IsVisible);
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", previous);
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SessionShell_WhenConnected_HidesMainPaneUntilScreenShareIsToggled()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var previous = Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD");
            try
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", "1");
                var shell = new SessionShellView
                {
                    Width = 760,
                    ShowScreenShareAction = true,
                    MainContent = new Border
                    {
                        [AutomationProperties.AutomationIdProperty] = "Shell.Main",
                    },
                    ChatContent = new Border
                    {
                        [AutomationProperties.AutomationIdProperty] = "Shell.Chat",
                    },
                    DataContext = new ConnectedChatShellContext(),
                };

                var window = new Window { Width = 760, Height = 760, Content = shell };
                window.Show();

                try
                {
                    await FlushUiAsync();

                    Assert.True(shell.EffectiveShowScreenShareAction);
                    Assert.False(shell.ShowScreenSharePane);
                    Assert.False(shell.ShowResponsiveNarrowLayout);
                    Assert.True(shell.ShowResponsiveNarrowChatOnlyLayout);
                    Assert.Null(FindFirstControlByAutomationId(window, "Shell.Main"));
                    Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Shell.Chat"));

                    shell.ScreenShareCommand?.Execute(null);
                    await FlushUiAsync();

                    Assert.True(shell.ShowScreenSharePane);
                    Assert.True(shell.ShowResponsiveNarrowLayout);
                    Assert.False(shell.ShowResponsiveNarrowChatOnlyLayout);
                    Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Shell.Main"));
                    Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Shell.Chat"));
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", previous);
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SessionShell_WhenHelperReceivesFrame_AutoShowsViewer_AndClearsOnStop()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var previous = Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD");
            try
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", "1");
                EnsureAppServices();
                var transportConfig = NLink.App.Configuration.TransportRuntimeConfig.Select();
                using var runtime = new SessionRuntime(() => new NLink.Infra.DevLocal.DevLocalTransport());
                using var helper = new HelperPageViewModel(
                    cancelAction: static () => { },
                    transportConfig,
                    runtime,
                    openDiagnosticsAction: static () => { },
                    clipboardService: new TestClipboardService(),
                    shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null));

                var shell = new SessionShellView
                {
                    Width = 1080,
                    ShowScreenShareAction = true,
                    MainContent = new Border
                    {
                        [AutomationProperties.AutomationIdProperty] = "Shell.Main",
                    },
                    ChatContent = new Border
                    {
                        [AutomationProperties.AutomationIdProperty] = "Shell.Chat",
                    },
                    DataContext = helper,
                };

                var window = new Window { Width = 1080, Height = 760, Content = shell };
                window.Show();

                try
                {
                    await FlushUiAsync();
                    Assert.False(shell.ShowScreenSharePane);

                    InvokePrivate(
                        helper,
                        "OnScreenShareFrameCompleted",
                        null,
                        new NLink.Infra.Nkn.ScreenShareFrameCompletedEventArgs(1, 1, 1, "jpeg", CreateTinyImageBytes()));

                    await WaitUntilAsync(() => helper.ScreenShareViewer.CurrentFrame is not null, TimeSpan.FromSeconds(2));
                    await WaitUntilAsync(() => helper.ShowRemoteScreenShareFrame, TimeSpan.FromSeconds(2));
                    await WaitUntilAsync(() => shell.ShowScreenSharePane, TimeSpan.FromSeconds(2));

                    InvokePrivate(helper, "ClearRemoteScreenShareFrame");

                    await WaitUntilAsync(() => helper.ScreenShareViewer.CurrentFrame is null, TimeSpan.FromSeconds(2));
                    await WaitUntilAsync(() => !helper.ShowRemoteScreenShareFrame, TimeSpan.FromSeconds(2));
                    await WaitUntilAsync(() => !shell.ShowScreenSharePane, TimeSpan.FromSeconds(2));
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", previous);
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SessionShell_WhenHelpeePreviewStarts_AutoShowsViewer_AndLeavesChatOnlyLayout()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var previousScaffold = Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD");
            try
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", "1");
                var context = new MutableConnectedScreenShareShellContext
                {
                    HeaderStatusText = "Connected",
                    ShowConnectedPanel = true,
                };

                var shell = new SessionShellView
                {
                    Width = 1080,
                    ShowScreenShareAction = true,
                    MainContent = new Border
                    {
                        [AutomationProperties.AutomationIdProperty] = "Shell.Main",
                    },
                    ChatContent = new Border
                    {
                        [AutomationProperties.AutomationIdProperty] = "Shell.Chat",
                    },
                    DataContext = context,
                };

                var window = new Window { Width = 1080, Height = 760, Content = shell };
                window.Show();

                try
                {
                    await FlushUiAsync();
                    Assert.False(shell.ShowScreenSharePane);

                    context.ShowScreenSharePreviewFrame = true;

                    await WaitUntilAsync(() => context.ShowScreenSharePreviewFrame, TimeSpan.FromSeconds(2));
                    await WaitUntilAsync(() => shell.ShowScreenSharePane, TimeSpan.FromSeconds(2));

                    Assert.True(shell.ShowFixedShellLayout || shell.ShowResponsiveWideLayout || shell.ShowResponsiveNarrowLayout);
                    Assert.False(shell.ShowFixedChatOnlyLayout || shell.ShowResponsiveWideChatOnlyLayout || shell.ShowResponsiveNarrowChatOnlyLayout);
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", previousScaffold);
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SessionShell_WhenWindowIsLarge_ScreenShareViewerExceedsExpectedBounds()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var previousScaffold = Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD");
            try
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", "1");
                using var previewFrame = CreateBitmap(1280, 720);
                var context = new MutableConnectedScreenShareShellContext
                {
                    HeaderStatusText = "Connected",
                    ShowConnectedPanel = true,
                    ShowScreenSharePreviewFrame = true,
                    ScreenSharePreviewFrame = previewFrame,
                };

                var shell = new SessionShellView
                {
                    Width = 1400,
                    ShowScreenShareAction = true,
                    MainContent = new Border
                    {
                        [AutomationProperties.AutomationIdProperty] = "Shell.Main",
                    },
                    ChatContent = new Border
                    {
                        [AutomationProperties.AutomationIdProperty] = "Shell.Chat",
                    },
                    DataContext = context,
                };

                var window = new Window { Width = 1400, Height = 900, Content = shell };
                window.Show();

                try
                {
                    await WaitUntilAsync(() => shell.ShowScreenSharePane, TimeSpan.FromSeconds(2));

                    var viewer = await WaitForLayoutConditionAsync(
                        window,
                        () => FindVisibleScreenShareViewer(window),
                        TimeSpan.FromSeconds(2),
                        "screen share viewer layout");

                    Assert.True(
                        viewer.Bounds.Width > 800,
                        $"Expected large-window screenshare viewer width > 800, got {viewer.Bounds.Width:N1}.");
                    Assert.True(
                        viewer.Bounds.Height > 500,
                        $"Expected large-window screenshare viewer height > 500, got {viewer.Bounds.Height:N1}.");
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", previousScaffold);
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SessionShell_MainPaneWidthPolicy_OnlyStretchesWhenScreenShareIsVisible()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var previousScaffold = Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD");
            try
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", "1");
                using var previewFrame = CreateBitmap(1280, 720);
                var context = new MutableConnectedScreenShareShellContext
                {
                    HeaderStatusText = "Connected",
                    ShowConnectedPanel = true,
                };

                var shell = new SessionShellView
                {
                    Width = 1600,
                    Height = 900,
                    ShowScreenShareAction = true,
                    MainContent = new Border
                    {
                        Width = 900,
                        Height = 400,
                    },
                    DataContext = context,
                };

                var window = new Window { Width = 1600, Height = 900, Content = shell };
                window.Show();

                try
                {
                    await WaitUntilAsync(
                        () => shell.MainPaneHorizontalAlignment == HorizontalAlignment.Center,
                        TimeSpan.FromSeconds(2));

                    Assert.Equal(HorizontalAlignment.Center, shell.MainPaneHorizontalAlignment);
                    Assert.Equal(1120d, shell.MainPaneMaxWidth);

                    context.ScreenSharePreviewFrame = previewFrame;
                    context.ShowScreenSharePreviewFrame = true;

                    await WaitUntilAsync(
                        () => shell.MainPaneHorizontalAlignment == HorizontalAlignment.Stretch,
                        TimeSpan.FromSeconds(2));

                    Assert.Equal(HorizontalAlignment.Stretch, shell.MainPaneHorizontalAlignment);
                    Assert.True(double.IsPositiveInfinity(shell.MainPaneMaxWidth));
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", previousScaffold);
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SessionShell_WhenDisconnected_AndLaterApproved_KeepsScreenShareInactive_UntilNewFrameArrives()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var previous = Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD");
            try
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", "1");
                EnsureAppServices();
                var transportConfig = NLink.App.Configuration.TransportRuntimeConfig.Select();
                using var runtime = new SessionRuntime(() => new NLink.Infra.DevLocal.DevLocalTransport());
                using var helper = new HelperPageViewModel(
                    cancelAction: static () => { },
                    transportConfig,
                    runtime,
                    openDiagnosticsAction: static () => { },
                    clipboardService: new TestClipboardService(),
                    shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null));

                var shell = new SessionShellView
                {
                    Width = 1080,
                    ShowScreenShareAction = true,
                    MainContent = new Border
                    {
                        [AutomationProperties.AutomationIdProperty] = "Shell.Main",
                    },
                    ChatContent = new Border
                    {
                        [AutomationProperties.AutomationIdProperty] = "Shell.Chat",
                    },
                    DataContext = helper,
                };

                var window = new Window { Width = 1080, Height = 760, Content = shell };
                window.Show();

                try
                {
                    await FlushUiAsync();

                    InvokePrivate(
                        helper,
                        "OnScreenShareFrameCompleted",
                        null,
                        new NLink.Infra.Nkn.ScreenShareFrameCompletedEventArgs(1, 1, 1, "jpeg", CreateTinyImageBytes()));

                    await WaitUntilAsync(() => helper.ScreenShareViewer.CurrentFrame is not null, TimeSpan.FromSeconds(2));
                    await WaitUntilAsync(() => helper.ShowRemoteScreenShareFrame, TimeSpan.FromSeconds(2));
                    await WaitUntilAsync(() => shell.ShowScreenSharePane, TimeSpan.FromSeconds(2));

                    InvokePrivate(helper, "OnDisconnected", null, EventArgs.Empty);

                    await WaitUntilAsync(() => helper.ScreenShareViewer.CurrentFrame is null, TimeSpan.FromSeconds(2));
                    await WaitUntilAsync(() => !helper.ShowRemoteScreenShareFrame, TimeSpan.FromSeconds(2));
                    await WaitUntilAsync(() => !shell.ShowScreenSharePane, TimeSpan.FromSeconds(2));

                    InvokePrivate(helper, "OnApproved", null, EventArgs.Empty);
                    await FlushUiAsync();

                    Assert.Null(helper.ScreenShareViewer.CurrentFrame);
                    Assert.False(helper.ShowRemoteScreenShareFrame);
                    Assert.False(shell.ShowScreenSharePane);
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", previous);
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SessionShell_WhenNotConnected_ScreenShareCommand_NoOps()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var previous = Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD");
            try
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", "1");
                var shell = new SessionShellView
                {
                    ShowScreenShareAction = true,
                    DataContext = new StaleDisconnectedShellContext(),
                };

                await FlushUiAsync();

                shell.ScreenShareCommand?.Execute(null);
                await FlushUiAsync();

                Assert.False(shell.EffectiveShowScreenShareAction);
                Assert.False(shell.ShowScreenSharePane);
            }
            finally
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", previous);
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SessionShell_ScreenShareHeaderButton_TogglesBetweenShareAndStop()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var previous = Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD");
            try
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", "1");
                var context = new MutableConnectedScreenShareShellContext
                {
                    HeaderStatusText = "Connected",
                    ShowConnectedPanel = true,
                    CanShowScreenShareAction = true,
                };
                var shell = new SessionShellView
                {
                    Width = 760,
                    ShowScreenShareAction = true,
                    DataContext = context,
                };

                var window = new Window { Width = 760, Height = 240, Content = shell };
                window.Show();

                try
                {
                    await FlushUiAsync();

                    var shareButton = Assert.IsType<Button>(FindFirstControlByAutomationId(window, "SessionHeader.ShareScreen"));
                    Assert.Equal("Share screen", Assert.IsType<string>(shareButton.Content));
                    Assert.False(shell.ShowScreenSharePane);

                    shell.ScreenShareCommand?.Execute(null);
                    await WaitUntilAsync(() => context.IsScreenSharingPreviewActive, TimeSpan.FromSeconds(2));
                    await WaitUntilAsync(() => shell.ShowScreenSharePane, TimeSpan.FromSeconds(2));
                    await FlushUiAsync();
                    Assert.Equal("Stop sharing", Assert.IsType<string>(shareButton.Content));

                    shell.ScreenShareCommand?.Execute(null);
                    await WaitUntilAsync(() => !context.IsScreenSharingPreviewActive, TimeSpan.FromSeconds(2));
                    await WaitUntilAsync(() => !shell.ShowScreenSharePane, TimeSpan.FromSeconds(2));
                    await FlushUiAsync();
                    Assert.Equal("Share screen", Assert.IsType<string>(shareButton.Content));
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", previous);
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SessionShell_ScreenShareHeaderButton_DisablesWhenToggleCommandCannotExecute()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var previous = Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD");
            try
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", "1");
                var context = new MutableConnectedScreenShareShellContext
                {
                    HeaderStatusText = "Connected",
                    ShowConnectedPanel = true,
                    CanShowScreenShareAction = true,
                    CanToggleScreenSharePreview = true,
                };
                var shell = new SessionShellView
                {
                    Width = 760,
                    ShowScreenShareAction = true,
                    DataContext = context,
                };

                var window = new Window { Width = 760, Height = 240, Content = shell };
                window.Show();

                try
                {
                    await FlushUiAsync();

                    var shareButton = Assert.IsType<Button>(FindFirstControlByAutomationId(window, "SessionHeader.ShareScreen"));
                    Assert.True(shareButton.IsEnabled);

                    context.CanToggleScreenSharePreview = false;
                    await WaitUntilAsync(() => !shareButton.IsEnabled, TimeSpan.FromSeconds(2));

                    context.CanToggleScreenSharePreview = true;
                    await WaitUntilAsync(() => shareButton.IsEnabled, TimeSpan.FromSeconds(2));
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", previous);
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

            var window = new Window { Width = 760, Height = 760, Content = shell };
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

    private static AppServiceRegistry EnsureAppServices()
    {
        var app = Assert.IsType<NLink.App.App>(Application.Current);
        var services = app.Services;

        if (!services.TryGet<IClipboardService>(out _))
        {
            var clipboard = new TestClipboardService();
            services.AddSingleton<IClipboardService>(clipboard);
        }

        if (!services.TryGet<IInviteShareService>(out _))
        {
            services.AddSingleton<IInviteShareService>(new DefaultInviteShareService());
        }

        if (!services.TryGet<IQrCodeService>(out _))
        {
            services.AddSingleton<IQrCodeService>(new QrCodeService());
        }

        if (!services.TryGet<IRecentConnectTargetsStore>(out _))
        {
            services.AddSingleton<IRecentConnectTargetsStore>(new LocalRecentConnectTargetsStore());
        }

        if (!services.TryGet<NLink.App.Configuration.ShareMessageConfig>(out _))
        {
            services.AddSingleton(new NLink.App.Configuration.ShareMessageConfig(null));
        }

        if (!services.TryGet<MetricsRegistry>(out _))
        {
            services.AddSingleton(new MetricsRegistry());
        }

        if (!services.TryGet<ResourceRuntimeTracker>(out _))
        {
            services.AddSingleton(new ResourceRuntimeTracker());
        }

        return services;
    }

    private static AppServiceRegistry CreateServicesForMainWindow()
    {
        var services = new AppServiceRegistry();
        services.AddSingleton<IClipboardService>(new TestClipboardService());
        services.AddSingleton<IInviteShareService>(new DefaultInviteShareService());
        services.AddSingleton<IQrCodeService>(new QrCodeService());
        services.AddSingleton<IRecentConnectTargetsStore>(new LocalRecentConnectTargetsStore());
        services.AddSingleton(new NLink.App.Configuration.ShareMessageConfig(null));
        services.AddSingleton(new MetricsRegistry());
        services.AddSingleton(new ResourceRuntimeTracker());
        return services;
    }

    private static T? FindFirstDescendant<T>(Control root)
        where T : class
        => root.GetVisualDescendants().OfType<T>().FirstOrDefault();

    private static Control? FindFirstControlByAutomationId(Control root, string automationId)
        => root.GetVisualDescendants()
            .OfType<Control>()
            .Concat(root.GetLogicalDescendants().OfType<Control>())
            .FirstOrDefault(control =>
                string.Equals(
                    AutomationProperties.GetAutomationId(control),
                    automationId,
                    StringComparison.Ordinal));

    private static Control? FindFirstVisibleControlByAutomationId(Control root, string automationId)
        => root.GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(control =>
                control.IsVisible &&
                string.Equals(
                    AutomationProperties.GetAutomationId(control),
                    automationId,
                    StringComparison.Ordinal));

    private static async Task FlushUiAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (predicate())
            {
                return;
            }

            await FlushUiAsync();
        }

        throw new TimeoutException($"Condition not met within {timeout.TotalSeconds:N1}s.");
    }

    private static async Task<T> WaitForLayoutConditionAsync<T>(
        Control root,
        Func<T?> probe,
        TimeSpan timeout,
        string phase)
        where T : class
    {
        var current = probe();
        if (current is not null)
        {
            return current;
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        void TryComplete()
        {
            var value = probe();
            if (value is not null)
            {
                tcs.TrySetResult(value);
            }
        }

        EventHandler? handler = null;
        handler = (_, _) => TryComplete();
        root.LayoutUpdated += handler;

        try
        {
            await FlushUiAsync();
            TryComplete();

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var registration = timeoutCts.Token.Register(() =>
                tcs.TrySetException(new TimeoutException($"Timed out waiting for {phase} after {timeout.TotalSeconds:N1}s.")));
            return await tcs.Task;
        }
        finally
        {
            root.LayoutUpdated -= handler;
        }
    }

    private static Image? FindVisibleScreenShareViewer(Control root)
        => root.GetVisualDescendants()
            .OfType<Image>()
            .FirstOrDefault(control =>
                control.IsVisible &&
                string.Equals(
                    AutomationProperties.GetAutomationId(control),
                    "ScreenShare.Viewer",
                    StringComparison.Ordinal) &&
                control.Bounds.Width > 0 &&
                control.Bounds.Height > 0);

    private static void InvokePrivate(object target, string methodName)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(target, null);
    }

    private static void InvokePrivate(object target, string methodName, params object?[]? args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(target, args);
    }

    private static byte[] CreateTinyImageBytes()
    {
        return Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/a5kAAAAASUVORK5CYII=");
    }

    private static Bitmap CreateBitmap(int width, int height)
    {
        var writeable = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using (var locked = writeable.Lock())
        {
            var totalBytes = width * height * 4;
            var pixels = new byte[totalBytes];
            Marshal.Copy(pixels, 0, locked.Address, totalBytes);
        }

        return writeable;
    }

    private static Bitmap CreateTinyBitmap()
    {
        using var stream = new MemoryStream(CreateTinyImageBytes(), writable: false);
        return new Bitmap(stream);
    }

    private static void SetPrivateProperty(object target, string propertyName, object? value)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(property);
        property!.SetValue(target, value);
    }

    private static async Task<ConnectedSessionContext> CreateConnectedSessionContextAsync()
    {
        var transportConfig = NLink.App.Configuration.TransportRuntimeConfig.Select();
        var network = new FakeSessionTransportNetwork();
        var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-ui-smoke-" + Guid.NewGuid().ToString("N")));
        var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-ui-smoke-" + Guid.NewGuid().ToString("N")));
        var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            transportConfig,
            helperRuntime,
            openDiagnosticsAction: static () => { },
            clipboardService: new TestClipboardService(),
            shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null));
        var helpee = new HelpeePageViewModel(
            cancelAction: static () => { },
            transportConfig,
            helpeeRuntime,
            openDiagnosticsAction: static () => { },
            clipboardService: new TestClipboardService(),
            shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null));

        await WaitUntilAsync(() => !string.IsNullOrWhiteSpace(helpee.ShareInvite), TimeSpan.FromSeconds(3));
        var connectTask = helperRuntime.StartHelperAsync(new NLink.Core.SessionConnect.PeerAddress(helpeeRuntime.CurrentLocalPeerAddress!.Value.Value), CancellationToken.None);

        await WaitUntilAsync(
            () => helpee.HasIncomingRequest && helpee.ConnectionState == "IncomingRequest",
            TimeSpan.FromSeconds(5));

        helpee.AllowCommand.Execute(null);
        await connectTask;

        await WaitUntilAsync(
            () => helpee.ConnectionState == "Connected" && helper.ConnectionState == "Connected",
            TimeSpan.FromSeconds(5));

        return new ConnectedSessionContext(helper, helpee, helperRuntime, helpeeRuntime);
    }

    private sealed class TestClipboardService : IClipboardService
    {
        public Task SetTextAsync(string text) => Task.CompletedTask;
    }

    private sealed class FixedRecentConnectTargetsStore : IRecentConnectTargetsStore
    {
        private readonly IReadOnlyList<string> targets;

        public FixedRecentConnectTargetsStore(params string[] targets)
        {
            this.targets = targets;
        }

        public IReadOnlyList<string> LoadTargets() => targets;

        public void SaveTargets(IReadOnlyList<string> targets)
        {
        }
    }

    private sealed class ConnectedShellContext
    {
        public string HeaderStatusText => "Connected";
    }

    private sealed class ConnectedChatShellContext
    {
        public string HeaderStatusText => "Connected";

        public bool ShowConnectedPanel => true;
    }

    private sealed class MutableConnectedChatShellContext : INotifyPropertyChanged
    {
        private string headerStatusText = "Ready";
        private bool showConnectedPanel;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string HeaderStatusText
        {
            get => headerStatusText;
            set
            {
                if (string.Equals(headerStatusText, value, StringComparison.Ordinal))
                {
                    return;
                }

                headerStatusText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HeaderStatusText)));
            }
        }

        public bool ShowConnectedPanel
        {
            get => showConnectedPanel;
            set
            {
                if (showConnectedPanel == value)
                {
                    return;
                }

                showConnectedPanel = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowConnectedPanel)));
            }
        }
    }

    private sealed class MutableConnectedScreenShareShellContext : INotifyPropertyChanged
    {
        private string headerStatusText = "Ready";
        private bool showConnectedPanel;
        private bool showScreenSharePreviewFrame;
        private Bitmap? screenSharePreviewFrame;
        private bool isScreenSharingPreviewActive;
        private bool canShowScreenShareAction;
        private bool canToggleScreenSharePreview = true;
        private readonly RelayCommand toggleScreenSharePreviewCommand;

        public MutableConnectedScreenShareShellContext()
        {
            toggleScreenSharePreviewCommand = new RelayCommand(() =>
            {
                IsScreenSharingPreviewActive = !IsScreenSharingPreviewActive;
                ShowScreenSharePreviewFrame = IsScreenSharingPreviewActive;
            }, () => CanToggleScreenSharePreview);
            ToggleScreenSharePreviewCommand = toggleScreenSharePreviewCommand;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string HeaderStatusText
        {
            get => headerStatusText;
            set
            {
                if (string.Equals(headerStatusText, value, StringComparison.Ordinal))
                {
                    return;
                }

                headerStatusText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HeaderStatusText)));
            }
        }

        public bool ShowConnectedPanel
        {
            get => showConnectedPanel;
            set
            {
                if (showConnectedPanel == value)
                {
                    return;
                }

                showConnectedPanel = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowConnectedPanel)));
            }
        }

        public bool CanShowScreenShareAction
        {
            get => canShowScreenShareAction;
            set
            {
                if (canShowScreenShareAction == value)
                {
                    return;
                }

                canShowScreenShareAction = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanShowScreenShareAction)));
            }
        }

        public bool IsScreenSharingPreviewActive
        {
            get => isScreenSharingPreviewActive;
            set
            {
                if (isScreenSharingPreviewActive == value)
                {
                    return;
                }

                isScreenSharingPreviewActive = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsScreenSharingPreviewActive)));
            }
        }

        public bool ShowScreenSharePreviewFrame
        {
            get => showScreenSharePreviewFrame;
            set
            {
                if (showScreenSharePreviewFrame == value)
                {
                    return;
                }

                showScreenSharePreviewFrame = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowScreenSharePreviewFrame)));
            }
        }

        public Bitmap? ScreenSharePreviewFrame
        {
            get => screenSharePreviewFrame;
            set
            {
                if (ReferenceEquals(screenSharePreviewFrame, value))
                {
                    return;
                }

                screenSharePreviewFrame = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ScreenSharePreviewFrame)));
            }
        }

        public bool CanToggleScreenSharePreview
        {
            get => canToggleScreenSharePreview;
            set
            {
                if (canToggleScreenSharePreview == value)
                {
                    return;
                }

                canToggleScreenSharePreview = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanToggleScreenSharePreview)));
                toggleScreenSharePreviewCommand.NotifyCanExecuteChanged();
            }
        }

        public ICommand ToggleScreenSharePreviewCommand { get; }
    }

    private sealed class StaleDisconnectedShellContext
    {
        public string HeaderStatusText => "Request rejected";

        public bool ShowConnectedPanel => true;
    }

    private sealed class ConnectedSessionContext : IDisposable
    {
        public ConnectedSessionContext(
            HelperPageViewModel helper,
            HelpeePageViewModel helpee,
            SessionRuntime helperRuntime,
            SessionRuntime helpeeRuntime)
        {
            Helper = helper;
            Helpee = helpee;
            HelperRuntime = helperRuntime;
            HelpeeRuntime = helpeeRuntime;
        }

        public HelperPageViewModel Helper { get; }

        public HelpeePageViewModel Helpee { get; }

        public SessionRuntime HelperRuntime { get; }

        public SessionRuntime HelpeeRuntime { get; }

        public void Dispose()
        {
            Helper.Dispose();
            Helpee.Dispose();
            HelperRuntime.Dispose();
            HelpeeRuntime.Dispose();
        }
    }

    private sealed class FakeSessionTransportNetwork
    {
        private readonly object gate = new();
        private readonly Dictionary<string, FakeSessionTransport> hostsByAddress = new(StringComparer.Ordinal);

        public FakeSessionTransport CreateTransport(string address)
        {
            return new FakeSessionTransport(this, address);
        }

        public void RegisterHost(string address, FakeSessionTransport host)
        {
            lock (gate)
            {
                hostsByAddress[address] = host;
            }
        }

        public void UnregisterHost(FakeSessionTransport transport)
        {
            lock (gate)
            {
                foreach (var pair in hostsByAddress.ToArray())
                {
                    if (ReferenceEquals(pair.Value, transport))
                    {
                        hostsByAddress.Remove(pair.Key);
                    }
                }
            }
        }

        public FakeSessionTransport? TryFindHost(string address)
        {
            lock (gate)
            {
                return hostsByAddress.TryGetValue(address, out var host) ? host : null;
            }
        }
    }

    private sealed class FakeSessionTransport : NLink.Core.ISignalingTransport, NLink.Core.IAddressTargetSignalingTransport, NLink.Core.IInviteTargetSignalingTransport, NLink.Core.IAddressHostSignalingTransport, NLink.Core.IHostReadySignalingTransport, NLink.Core.ILocalPeerAddressSignalingTransport, NLink.Core.ISessionSecuritySignalingTransport
    {
        private readonly FakeSessionTransportNetwork network;
        private readonly byte[] sharedKey = SHA256LikeDeterministicBytes("beta3-ui-smoke-key", 32);
        private readonly TaskCompletionSource<bool> hostReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private SessionSecurityState currentSessionSecurityState = SessionSecurityState.Empty;
        private FakeSessionTransport? peer;
        private bool disposed;

        public FakeSessionTransport(FakeSessionTransportNetwork network, string address)
        {
            this.network = network;
            Address = address;
        }

        public string Address { get; }
        public string LocalPeerAddress => Address;

        public event EventHandler<NLink.Core.IncomingJoinRequestEventArgs>? IncomingJoinRequest;
        public event EventHandler<NLink.Core.TransportSessionKeyReadyEventArgs>? SessionKeyReady;
        public event EventHandler<NLink.Core.TransportChatMessageEventArgs>? ChatMessageReceived;
        public event EventHandler? Approved;
        public event EventHandler? Rejected;
        public event EventHandler? Disconnected;
        public event EventHandler<NLink.Core.TransportSessionSecurityStateChangedEventArgs>? SessionSecurityStateChanged;
        public SessionSecurityState CurrentSessionSecurityState => currentSessionSecurityState;

        public Task WaitUntilHostReadyAsync(CancellationToken ct) => hostReadyTcs.Task.WaitAsync(ct);

        public Task HostByAddressAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            network.RegisterHost(Address, this);
            UpdateSessionSecurityState(SessionSecurityState.CreateHelpeeWaiting(new PeerAddress(Address)));
            hostReadyTcs.TrySetResult(true);
            return Task.Delay(Timeout.Infinite, ct);
        }

        public Task JoinByAddressAsync(string peerAddress, CancellationToken ct)
        {
            ThrowIfDisposed();
            var host = network.TryFindHost(peerAddress) ?? throw new TimeoutException("Host not found.");
            return JoinCoreAsync(
                host,
                new SessionId($"fake_session_{Guid.NewGuid():N}"),
                SessionSecurityDefaults.AllCapabilityGrants,
                inviteValidated: true);
        }

        public Task JoinByInviteAsync(string inviteToken, ValidatedInviteV1 invite, CancellationToken ct)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(inviteToken))
            {
                throw new ArgumentException("Invite token is required.", nameof(inviteToken));
            }

            ArgumentNullException.ThrowIfNull(invite);
            var host = network.TryFindHost(invite.TargetAddress.Value) ?? throw new TimeoutException("Host not found.");
            var helperAddress = new PeerAddress(Address);
            if (invite.BoundHelperAddress is not null && invite.BoundHelperAddress != helperAddress)
            {
                throw new InvalidOperationException("Invite token is bound to a different helper identity.");
            }

            return JoinCoreAsync(
                host,
                invite.SessionId,
                invite.Payload.Capabilities.ToCapabilityGrant(),
                inviteValidated: true);
        }

        private Task JoinCoreAsync(
            FakeSessionTransport host,
            SessionId sessionId,
            CapabilityGrant requestedCapabilities,
            bool inviteValidated)
        {
            peer = host;
            host.peer = this;
            var helpeeAddress = new PeerAddress(host.Address);
            var helperAddress = new PeerAddress(Address);
            var approvalRequest = new ApprovalRequest(
                helperAddress,
                requestedCapabilities,
                sessionId);

            var verifiedState = CreateVerifiedSecurityState(sessionId, helpeeAddress, helperAddress, inviteValidated);
            UpdateSessionSecurityState(verifiedState);
            host.UpdateSessionSecurityState(verifiedState);

            var joinRequest = new NLink.Core.IncomingJoinRequestEventArgs(
                approveAsync: (decision, _) =>
                {
                    if (decision is null)
                    {
                        throw new InvalidOperationException("Explicit approval decision is required.");
                    }

                    ValidateApprovalDecision(approvalRequest, decision);
                    var grant = decision.ToGrant();
                    host.UpdateSessionSecurityState(host.CurrentSessionSecurityState.WithApproval(grant));
                    UpdateSessionSecurityState(CurrentSessionSecurityState.WithApproval(grant));
                    host.SessionKeyReady?.Invoke(host, new NLink.Core.TransportSessionKeyReadyEventArgs(host.sharedKey));
                    SessionKeyReady?.Invoke(this, new NLink.Core.TransportSessionKeyReadyEventArgs(sharedKey));
                    host.Approved?.Invoke(host, EventArgs.Empty);
                    Approved?.Invoke(this, EventArgs.Empty);
                    return Task.CompletedTask;
                },
                rejectAsync: _ =>
                {
                    host.UpdateSessionSecurityState(host.CurrentSessionSecurityState.Invalidate("local_reject"));
                    UpdateSessionSecurityState(CurrentSessionSecurityState.Invalidate("local_reject"));
                    Rejected?.Invoke(this, EventArgs.Empty);
                    return Task.CompletedTask;
                },
                approvalRequest: approvalRequest);

            host.IncomingJoinRequest?.Invoke(host, joinRequest);
            return Task.CompletedTask;
        }

        public Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            ThrowIfDisposed();
            var target = peer ?? throw new InvalidOperationException("No peer connected.");
            target.ChatMessageReceived?.Invoke(target, new NLink.Core.TransportChatMessageEventArgs(payload.ToArray()));
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            network.UnregisterHost(this);

            if (peer is { } target)
            {
                peer = null;
                target.peer = null;
                UpdateSessionSecurityState(CurrentSessionSecurityState.Invalidate("transport_disposed"));
                target.UpdateSessionSecurityState(target.CurrentSessionSecurityState.Invalidate("transport_disposed"));
                target.Disconnected?.Invoke(target, EventArgs.Empty);
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(FakeSessionTransport));
            }
        }

        private void UpdateSessionSecurityState(SessionSecurityState nextState)
        {
            if (Equals(currentSessionSecurityState, nextState))
            {
                return;
            }

            currentSessionSecurityState = nextState;
            SessionSecurityStateChanged?.Invoke(this, new NLink.Core.TransportSessionSecurityStateChangedEventArgs(nextState));
        }

        private static SessionSecurityState CreateVerifiedSecurityState(
            SessionId sessionId,
            PeerAddress helpeeAddress,
            PeerAddress helperAddress,
            bool inviteValidated)
        {
            return (SessionSecurityState.Empty with
            {
                SessionId = sessionId,
                HelpeeAddress = helpeeAddress,
                HelperAddress = helperAddress,
                InviteValidated = inviteValidated,
            }).WithHandshakeVerified(helperAddress);
        }

        private static void ValidateApprovalDecision(ApprovalRequest approvalRequest, ApprovalDecision decision)
        {
            if (decision.SessionId != approvalRequest.SessionId ||
                decision.HelperIdentity != approvalRequest.HelperIdentity ||
                decision.ExpiresAtUtc <= DateTimeOffset.UtcNow ||
                (decision.ApprovedCapabilities & ~approvalRequest.RequestedCapabilities) != 0)
            {
                throw new InvalidOperationException("Approval decision does not match the pending approval request.");
            }
        }
    }

    private static byte[] SHA256LikeDeterministicBytes(string text, int length)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text));
        if (hash.Length == length)
        {
            return hash;
        }

        return hash[..length];
    }
}

public sealed class Beta3DefaultUiFixture : IDisposable
{
    public Beta3DefaultUiFixture()
    {
        Session = HeadlessUnitTestSession.StartNew(typeof(AvaloniaHeadlessUiAppBootstrap));
    }

    public HeadlessUnitTestSession Session { get; }

    public void Dispose()
    {
        Session.Dispose();
    }
}
