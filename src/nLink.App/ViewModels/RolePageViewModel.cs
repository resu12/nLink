using System;
using CommunityToolkit.Mvvm.Input;

namespace NLink.App.ViewModels;

public sealed class RolePageViewModel : ViewModelBase
{
    public RolePageViewModel(string title, string message, Action backAction)
    {
        Title = title;
        Message = message;
        BackCommand = new RelayCommand(backAction);
    }

    public string Title { get; }

    public string Message { get; }

    public IRelayCommand BackCommand { get; }
}

