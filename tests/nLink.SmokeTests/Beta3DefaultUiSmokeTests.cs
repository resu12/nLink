using System.Diagnostics;
using System.ComponentModel;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NLink.App.Services;
using NLink.App.ViewModels;
using NLink.App.Views;
using NLink.Core.Metrics;

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
    public async Task HelpeePage_DefaultShell_RendersWaitingContent_InVisibleBranch()
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

                Assert.NotNull(FindFirstControlByAutomationId(window, "Helpee.CopyCode"));
                Assert.NotNull(FindFirstControlByAutomationId(window, "Helpee.NewCode"));
                Assert.NotNull(FindFirstControlByAutomationId(window, "Helpee.Code"));
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

                Assert.NotNull(FindFirstControlByAutomationId(window, "Helpee.CopyCode"));
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

    private static void InvokePrivate(object target, string methodName)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(target, null);
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

        helper.CodeInput = helpee.ShareCode;
        var connectTask = helper.ConnectCommand.ExecuteAsync(null);

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
        private readonly Dictionary<string, FakeSessionTransport> hostsByCode = new(StringComparer.Ordinal);

        public FakeSessionTransport CreateTransport(string address)
        {
            return new FakeSessionTransport(this, address);
        }

        public void RegisterHost(string code, FakeSessionTransport host)
        {
            lock (gate)
            {
                hostsByCode[code] = host;
            }
        }

        public void UnregisterHost(FakeSessionTransport transport)
        {
            lock (gate)
            {
                foreach (var pair in hostsByCode.ToArray())
                {
                    if (ReferenceEquals(pair.Value, transport))
                    {
                        hostsByCode.Remove(pair.Key);
                    }
                }
            }
        }

        public FakeSessionTransport? TryFindHost(string code)
        {
            lock (gate)
            {
                return hostsByCode.TryGetValue(code, out var host) ? host : null;
            }
        }
    }

    private sealed class FakeSessionTransport : NLink.Core.ISignalingTransport
    {
        private readonly FakeSessionTransportNetwork network;
        private readonly byte[] sharedKey = SHA256LikeDeterministicBytes("beta3-ui-smoke-key", 32);
        private FakeSessionTransport? peer;
        private bool disposed;

        public FakeSessionTransport(FakeSessionTransportNetwork network, string address)
        {
            this.network = network;
            Address = address;
        }

        public string Address { get; }

        public event EventHandler<NLink.Core.IncomingJoinRequestEventArgs>? IncomingJoinRequest;
        public event EventHandler<NLink.Core.TransportSessionKeyReadyEventArgs>? SessionKeyReady;
        public event EventHandler<NLink.Core.TransportChatMessageEventArgs>? ChatMessageReceived;
        public event EventHandler? Approved;
        public event EventHandler? Rejected;
        public event EventHandler? Disconnected;

        public Task HostAsync(NLink.Core.SessionCode code, CancellationToken ct)
        {
            ThrowIfDisposed();
            network.RegisterHost(code.Digits, this);
            return Task.Delay(Timeout.Infinite, ct);
        }

        public Task JoinAsync(NLink.Core.SessionCode code, CancellationToken ct)
        {
            ThrowIfDisposed();
            var host = network.TryFindHost(code.Digits) ?? throw new TimeoutException("Host not found.");
            peer = host;
            host.peer = this;

            var joinRequest = new NLink.Core.IncomingJoinRequestEventArgs(
                approveAsync: _ =>
                {
                    host.SessionKeyReady?.Invoke(host, new NLink.Core.TransportSessionKeyReadyEventArgs(host.sharedKey));
                    SessionKeyReady?.Invoke(this, new NLink.Core.TransportSessionKeyReadyEventArgs(sharedKey));
                    host.Approved?.Invoke(host, EventArgs.Empty);
                    Approved?.Invoke(this, EventArgs.Empty);
                    return Task.CompletedTask;
                },
                rejectAsync: _ =>
                {
                    Rejected?.Invoke(this, EventArgs.Empty);
                    return Task.CompletedTask;
                });

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
