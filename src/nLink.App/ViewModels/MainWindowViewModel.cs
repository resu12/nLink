using System;
using NLink.App.Configuration;
using NLink.App.Services;
using NLink.Core;

namespace NLink.App.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private readonly AppServiceRegistry services;
    private readonly TransportRuntimeConfig transportConfig;
    private readonly ShareMessageConfig shareMessageConfig;
    private readonly IClipboardService clipboardService;
    private readonly HomePageViewModel homePage;
    private ViewModelBase currentPage;

    public MainWindowViewModel(AppServiceRegistry services)
    {
        this.services = services ?? throw new ArgumentNullException(nameof(services));
        transportConfig = TransportRuntimeConfig.Select();
        shareMessageConfig = this.services.GetRequired<ShareMessageConfig>();
        clipboardService = this.services.GetRequired<IClipboardService>();
        homePage = new HomePageViewModel(ShowHelpeePage, ShowHelperPage, ShowDiagnosticsPage);
        currentPage = homePage;
    }

    public ViewModelBase CurrentPage
    {
        get => currentPage;
        private set => SetProperty(ref currentPage, value);
    }

    private void ShowHelpeePage()
    {
        NavigateTo(new HelpeePageViewModel(ShowHomePage, transportConfig, clipboardService, shareMessageConfig));
    }

    private void ShowHelperPage()
    {
        NavigateTo(new HelperPageViewModel(ShowHomePage, transportConfig, clipboardService, shareMessageConfig));
    }

    private void ShowDiagnosticsPage()
    {
        NavigateTo(new DiagnosticsPageViewModel(ShowHomePage, transportConfig));
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

        if (CurrentPage is IDisposable disposablePage)
        {
            disposablePage.Dispose();
        }

        CurrentPage = nextPage;
    }
}
