using System;
using System.Threading.Tasks;
using NLink.App.Configuration;
using NLink.App.Services;
using NLink.Core;
using NLink.Core.Metrics;
using NLink.Infra.Nkn;

namespace NLink.App.ViewModels;

public class MainWindowViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan WindowClosePreparationTimeout = TimeSpan.FromSeconds(3);
    private readonly AppServiceRegistry services;
    private readonly TransportRuntimeConfig transportConfig;
    private readonly ShareMessageConfig shareMessageConfig;
    private readonly IClipboardService clipboardService;
    private readonly IInviteShareService inviteShareService;
    private readonly IQrCodeService qrCodeService;
    private readonly SessionRuntime sessionRuntime;
    private readonly StatusPresenter statusPresenter;
    private readonly SessionUiStateStore sessionUiStateStore;
    private readonly MetricsRegistry metricsRegistry;
    private readonly DebugMetricsPanelViewModel debugPanel;
    private readonly ResourceRuntimeTracker resourceRuntimeTracker;
    private readonly ITunaWalletLinkStore? tunaWalletLinkStore;
    private readonly ITunaWalletVerifier? tunaWalletVerifier;
    private readonly ITunaRuntimePilotService? tunaRuntimePilotService;
    private readonly HangReportService hangReportService;
    private readonly UiFreezeWatchdog uiFreezeWatchdog;
    private readonly INetworkEventSource networkEventSource;
    private readonly NetworkResilienceCoordinator networkResilienceCoordinator;
    private readonly HomePageViewModel homePage;
    private readonly object endSessionGate = new();
    private Task? endSessionTask;
    private ViewModelBase? lastNonDiagnosticsPage;
    private ViewModelBase currentPage;
    private bool disposed;

    public MainWindowViewModel(AppServiceRegistry services)
    {
        this.services = services ?? throw new ArgumentNullException(nameof(services));
        this.services.TryGet<ITunaRuntimePilotService>(out tunaRuntimePilotService);
        Func<ISignalingTransport>? nknTransportFactory = tunaRuntimePilotService is null
            ? null
            : tunaRuntimePilotService.CreateNknTransport;
        transportConfig = TransportRuntimeConfig.Select(nknTransportFactory);
        shareMessageConfig = this.services.GetRequired<ShareMessageConfig>();
        clipboardService = this.services.GetRequired<IClipboardService>();
        inviteShareService = this.services.GetRequired<IInviteShareService>();
        qrCodeService = this.services.GetRequired<IQrCodeService>();
        metricsRegistry = this.services.GetRequired<MetricsRegistry>();
        resourceRuntimeTracker = this.services.GetRequired<ResourceRuntimeTracker>();
        this.services.TryGet<ITunaWalletLinkStore>(out tunaWalletLinkStore);
        this.services.TryGet<ITunaWalletVerifier>(out tunaWalletVerifier);
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
            bootstrapHelperIdentityResolver: string.Equals(transportConfig.Key, "NKN", StringComparison.OrdinalIgnoreCase)
                ? NknLocalPeerAddressResolver.ResolvePersistedIdentityAsync
                : null,
            inviteShareService: inviteShareService));
    }

    private void EndSessionOnly()
    {
        _ = GetOrStartEndSessionTask();
    }

    private Task GetOrStartEndSessionTask()
    {
        lock (endSessionGate)
        {
            if (endSessionTask is { IsCompleted: false })
            {
                return endSessionTask;
            }

            endSessionTask = sessionRuntime.DisconnectAsync();
            return endSessionTask;
        }
    }

    private void ShowDiagnosticsPage()
    {
        NavigateTo(new DiagnosticsPageViewModel(
            ScreenShareEvidenceLocator.CreateDefault(),
            () => NavigateTo(lastNonDiagnosticsPage ?? homePage),
            transportConfig,
            shareMessageConfig,
            sessionRuntime,
            metricsRegistry,
            resourceRuntimeTracker,
            hangReportService,
            tunaWalletLinkStore: tunaWalletLinkStore,
            tunaWalletVerifier: tunaWalletVerifier,
            tunaRuntimePilotService: tunaRuntimePilotService));
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

    public async Task PrepareForWindowCloseAsync()
    {
        var closeAwarePage = CurrentPage as IWindowCloseAware;
        if (closeAwarePage is null && CurrentPage is DiagnosticsPageViewModel)
        {
            closeAwarePage = lastNonDiagnosticsPage as IWindowCloseAware;
        }

        await PrepareWindowCloseAsync(closeAwarePage, GetOrStartEndSessionTask, WindowClosePreparationTimeout)
            .ConfigureAwait(false);
    }

    internal static async Task PrepareWindowCloseAsync(
        IWindowCloseAware? closeAwarePage,
        Func<Task> getOrStartEndSessionTask,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(getOrStartEndSessionTask);

        var endSession = getOrStartEndSessionTask();
        var preparePage = PreparePageForWindowCloseAsync(closeAwarePage);
        try
        {
            await Task.WhenAll(endSession, preparePage)
                .WaitAsync(timeout)
                .ConfigureAwait(false);
        }
        catch
        {
            // Best-effort close path. App shutdown still proceeds.
        }
    }

    internal static async Task PreparePageForWindowCloseAsync(IWindowCloseAware? closeAwarePage)
    {
        if (closeAwarePage is null)
        {
            return;
        }

        try
        {
            await closeAwarePage.PrepareForWindowCloseAsync()
                .WaitAsync(WindowClosePreparationTimeout)
                .ConfigureAwait(false);
        }
        catch
        {
            // Best-effort close path. App shutdown still proceeds.
        }
    }
}
