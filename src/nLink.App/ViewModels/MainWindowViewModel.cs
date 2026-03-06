using System;
using NLink.App.Configuration;
using NLink.App.Services;
using NLink.Core.Metrics;

namespace NLink.App.ViewModels;

public class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly AppServiceRegistry services;
    private readonly TransportRuntimeConfig transportConfig;
    private readonly ShareMessageConfig shareMessageConfig;
    private readonly IClipboardService clipboardService;
    private readonly IInviteShareService inviteShareService;
    private readonly IRecentConnectTargetsStore recentConnectTargetsStore;
    private readonly IQrCodeService qrCodeService;
    private readonly SessionRuntime sessionRuntime;
    private readonly StatusPresenter statusPresenter;
    private readonly SessionUiStateStore sessionUiStateStore;
    private readonly MetricsRegistry metricsRegistry;
    private readonly DebugMetricsPanelViewModel debugPanel;
    private readonly ResourceRuntimeTracker resourceRuntimeTracker;
    private readonly HangReportService hangReportService;
    private readonly UiFreezeWatchdog uiFreezeWatchdog;
    private readonly INetworkEventSource networkEventSource;
    private readonly NetworkResilienceCoordinator networkResilienceCoordinator;
    private readonly HomePageViewModel homePage;
    private ViewModelBase? lastNonDiagnosticsPage;
    private ViewModelBase currentPage;
    private bool disposed;

    public MainWindowViewModel(AppServiceRegistry services)
    {
        this.services = services ?? throw new ArgumentNullException(nameof(services));
        transportConfig = TransportRuntimeConfig.Select();
        shareMessageConfig = this.services.GetRequired<ShareMessageConfig>();
        clipboardService = this.services.GetRequired<IClipboardService>();
        inviteShareService = this.services.GetRequired<IInviteShareService>();
        recentConnectTargetsStore = this.services.GetRequired<IRecentConnectTargetsStore>();
        qrCodeService = this.services.GetRequired<IQrCodeService>();
        metricsRegistry = this.services.GetRequired<MetricsRegistry>();
        resourceRuntimeTracker = this.services.GetRequired<ResourceRuntimeTracker>();
        sessionRuntime = new SessionRuntime(
            transportConfig.CreateTransport,
            watchdogOptions: null,
            watchdogDelayAsync: null,
            telemetrySink: new MetricsTelemetrySink(metricsRegistry),
            bridgeReusePolicy: transportConfig.BridgeReusePolicy);
        statusPresenter = new StatusPresenter(sessionRuntime);
        sessionUiStateStore = new SessionUiStateStore();
        debugPanel = new DebugMetricsPanelViewModel(sessionRuntime, metricsRegistry);
        hangReportService = new HangReportService(sessionRuntime, resourceRuntimeTracker);
        uiFreezeWatchdog = new UiFreezeWatchdog(hangReportService);
        networkEventSource = new SystemNetworkEventSource();
        networkResilienceCoordinator = new NetworkResilienceCoordinator(
            networkEventSource,
            sessionRuntime.HandleExternalRecoveryAsync);
        homePage = new HomePageViewModel(ShowHelpeePage, ShowHelperPage, ShowDiagnosticsPage, transportConfig);
        lastNonDiagnosticsPage = homePage;
        currentPage = homePage;
    }

    public ViewModelBase CurrentPage
    {
        get => currentPage;
        private set => SetProperty(ref currentPage, value);
    }

    public DebugMetricsPanelViewModel DebugPanel => debugPanel;

    public void ToggleDebugPanel()
    {
        debugPanel.ToggleVisible();
    }

    private void ShowHelpeePage()
    {
        NavigateTo(new HelpeePageViewModel(
            EndSessionOnly,
            transportConfig,
            sessionRuntime,
            ShowDiagnosticsPage,
            clipboardService,
            shareMessageConfig,
            statusPresenter,
            incomingRequestTimeout: null,
            uiStateStore: sessionUiStateStore,
            backAction: ShowHomePage,
            inviteShareService: inviteShareService,
            qrCodeService: qrCodeService));
    }

    private void ShowHelperPage()
    {
        sessionUiStateStore.SetPhase(SessionUiPhase.Waiting, "Navigation:ShowHelperPage");
        NavigateTo(new HelperPageViewModel(
            EndSessionOnly,
            transportConfig,
            sessionRuntime,
            ShowDiagnosticsPage,
            clipboardService,
            shareMessageConfig,
            statusPresenter,
            approvalTimeout: null,
            connectFailureCooldown: null,
            nowProvider: null,
            uiStateStore: sessionUiStateStore,
            backAction: ShowHomePage,
            recentConnectTargetsStore: recentConnectTargetsStore));
    }

    private void EndSessionOnly()
    {
        _ = sessionRuntime.DisconnectAsync();
    }

    private void ShowDiagnosticsPage()
    {
        NavigateTo(new DiagnosticsPageViewModel(
            () => NavigateTo(lastNonDiagnosticsPage ?? homePage),
            transportConfig,
            shareMessageConfig,
            sessionRuntime,
            metricsRegistry,
            resourceRuntimeTracker,
            hangReportService));
    }

    private void ShowHomePage()
    {
        NavigateTo(homePage);
    }

    private void NavigateTo(ViewModelBase nextPage)
    {
        if (ReferenceEquals(CurrentPage, nextPage))
        {
            return;
        }

        var navigatingToDiagnostics = nextPage is DiagnosticsPageViewModel;
        if (!navigatingToDiagnostics)
        {
            lastNonDiagnosticsPage = nextPage;
        }

        var preserveCurrentForDiagnosticsBack = navigatingToDiagnostics &&
            CurrentPage is not DiagnosticsPageViewModel;

        if (!ReferenceEquals(CurrentPage, homePage) &&
            !preserveCurrentForDiagnosticsBack &&
            CurrentPage is IDisposable disposablePage)
        {
            disposablePage.Dispose();
        }

        CurrentPage = nextPage;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        if (CurrentPage is IDisposable disposablePage)
        {
            disposablePage.Dispose();
        }

        networkResilienceCoordinator.Dispose();
        networkEventSource.Dispose();
        uiFreezeWatchdog.Dispose();
        debugPanel.Dispose();
        statusPresenter.Dispose();
        sessionRuntime.Dispose();
        resourceRuntimeTracker.Dispose();
    }
}
