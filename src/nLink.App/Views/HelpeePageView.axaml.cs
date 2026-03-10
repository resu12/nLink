using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using NLink.App.Services;
using NLink.App.ViewModels;

namespace NLink.App.Views;

public partial class HelpeePageView : UserControl
{
    private HelpeePageViewModel? currentViewModel;

    public HelpeePageView()
    {
        InitializeComponent();
        PropertyChanged += OnViewPropertyChanged;
        AttachedToVisualTree += (_, _) => BindClipboardTopLevel();
        SyncViewModelSubscription();
    }

    private void OnViewPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == DataContextProperty)
        {
            SyncViewModelSubscription();
        }
    }

    private void SyncViewModelSubscription()
    {
        if (currentViewModel is not null)
        {
            currentViewModel.SendFileRequested -= OnSendFileRequested;
        }

        currentViewModel = DataContext as HelpeePageViewModel;

        if (currentViewModel is not null)
        {
            currentViewModel.SendFileRequested += OnSendFileRequested;
        }
    }

    private async void OnSendFileRequested(object? sender, EventArgs e)
    {
        if (currentViewModel is not HelpeePageViewModel vm)
        {
            return;
        }

        try
        {
            var selection = await NativeFileTransferPicker.PickSingleFileAsync(this);
            if (selection is null)
            {
                return;
            }

            await vm.StartSendFileAsync(selection.Descriptor, selection.OpenReadStreamAsync);
        }
        catch
        {
            vm.NotifySendFileError("Couldn't open the selected file.");
        }
    }

    private void ChatDraftTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if (DataContext is not HelpeePageViewModel vm)
        {
            return;
        }

        if (!vm.SendChatCommand.CanExecute(null))
        {
            return;
        }

        vm.SendChatCommand.Execute(null);
        e.Handled = true;
    }

    private void InviteHelperIdentityInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if (DataContext is not HelpeePageViewModel vm)
        {
            return;
        }

        if (!vm.ApplyInviteHelperIdentityCommand.CanExecute(null))
        {
            return;
        }

        vm.ApplyInviteHelperIdentityCommand.Execute(null);
        e.Handled = true;
    }

    private void BindClipboardTopLevel()
    {
        if (TryGetClipboardService() is not AvaloniaClipboardService service)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this) is TopLevel topLevel)
        {
            service.SetTopLevel(topLevel);
        }
    }

    private IClipboardService? TryGetClipboardService()
    {
        BindClipboardTopLevelForCurrentTopLevel();
        if (Avalonia.Application.Current is not App app)
        {
            return null;
        }

        return app.Services.TryGet<IClipboardService>(out var service) ? service : null;
    }

    private void BindClipboardTopLevelForCurrentTopLevel()
    {
        if (Avalonia.Application.Current is not App app)
        {
            return;
        }

        if (!app.Services.TryGet<AvaloniaClipboardService>(out var service) || service is null)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this) is TopLevel topLevel)
        {
            service.SetTopLevel(topLevel);
        }
    }
}
