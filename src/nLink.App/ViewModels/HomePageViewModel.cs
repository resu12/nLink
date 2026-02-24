using System;
using CommunityToolkit.Mvvm.Input;

namespace NLink.App.ViewModels;

public sealed class HomePageViewModel : ViewModelBase
{
    public HomePageViewModel(Action showHelpeePage, Action showHelperPage, Action showDiagnosticsPage)
    {
        NeedHelpCommand = new RelayCommand(showHelpeePage);
        WantToHelpCommand = new RelayCommand(showHelperPage);
        DiagnosticsCommand = new RelayCommand(showDiagnosticsPage);
    }

    public string AppTitle => "nLink";

    public string Subtitle => "Simple help for family and friends.";

    public string NeedHelpLabel => "I need help";

    public string NeedHelpDescription => "Get help from someone you trust on this computer.";

    public string WantToHelpLabel => "I want to help someone";

    public string WantToHelpDescription => "Guide a family member or friend with simple steps.";

    public IRelayCommand NeedHelpCommand { get; }

    public IRelayCommand WantToHelpCommand { get; }

    public IRelayCommand DiagnosticsCommand { get; }
}

