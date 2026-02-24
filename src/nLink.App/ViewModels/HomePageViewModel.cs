using System;
using CommunityToolkit.Mvvm.Input;

namespace NLink.App.ViewModels;

using NLink.App.Configuration;

public sealed class HomePageViewModel : ViewModelBase
{
    public HomePageViewModel(
        Action showHelpeePage,
        Action showHelperPage,
        Action showDiagnosticsPage,
        TransportRuntimeConfig transportConfig)
    {
        if (transportConfig is null)
        {
            throw new ArgumentNullException(nameof(transportConfig));
        }
        NeedHelpCommand = new RelayCommand(showHelpeePage);
        WantToHelpCommand = new RelayCommand(showHelperPage);
        DiagnosticsCommand = new RelayCommand(showDiagnosticsPage);
        StartupWarningText = transportConfig.HasConfigurationError
            ? transportConfig.ConfigurationErrorText
            : transportConfig.StartupWarningText;
    }

    public string AppTitle => "nLink";

    public string Subtitle => "Simple help for family and friends.";

    public string NeedHelpLabel => "I need help";

    public string NeedHelpDescription => "Get help from someone you trust on this computer.";

    public string WantToHelpLabel => "I want to help someone";

    public string WantToHelpDescription => "Guide a family member or friend with simple steps.";

    public string StartupWarningText { get; }

    public bool ShowStartupWarning => !string.IsNullOrWhiteSpace(StartupWarningText);

    public IRelayCommand NeedHelpCommand { get; }

    public IRelayCommand WantToHelpCommand { get; }

    public IRelayCommand DiagnosticsCommand { get; }
}

