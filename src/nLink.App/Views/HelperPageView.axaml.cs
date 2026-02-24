using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NLink.App.Services;
using NLink.App.ViewModels;

namespace NLink.App.Views;

public partial class HelperPageView : UserControl
{
    private HelperPageViewModel? currentViewModel;

    public HelperPageView()
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

        currentViewModel = DataContext as HelperPageViewModel;

        if (currentViewModel is not null)
        {
            currentViewModel.SendFileRequested += OnSendFileRequested;
        }
    }

    private void OnSendFileRequested(object? sender, EventArgs e)
    {
        try
        {
            ShowSendFileWindow();
        }
        catch
        {
            var errorWindow = new Window
            {
                Title = "Send file",
                Width = 680,
                Height = 260,
                Background = Brushes.Black,
                Content = new TextBlock
                {
                    Text = "Could not open the send file screen." + Environment.NewLine +
                           "Please open https://nftp.nkn.org in your browser.",
                    Foreground = Brushes.White,
                    Margin = new Thickness(16),
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            if (TopLevel.GetTopLevel(this) is Window owner)
            {
                errorWindow.Show(owner);
                return;
            }

            errorWindow.Show();
        }
    }

    private void ChatDraftTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if (DataContext is not HelperPageViewModel vm)
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

    private void ShowSendFileWindow()
    {
        var window = new SendFileWindow();

        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            window.Show(owner);
            return;
        }

        window.Show();
    }

    private void BindClipboardTopLevel()
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

