using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
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

    private async void PasteHelperId_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not HelpeePageViewModel vm)
        {
            return;
        }

        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard is null)
            {
                return;
            }

            var text = await topLevel.Clipboard.TryGetTextAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                vm.ApplyHelperBootstrapInput(text, "clipboard");
            }
        }
        catch
        {
        }
    }

    private async void ImportHelperQr_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        CloseImportQrContextMenu();

        if (DataContext is not HelpeePageViewModel vm ||
            TopLevel.GetTopLevel(this) is not TopLevel topLevel ||
            topLevel.StorageProvider is null ||
            TryGetQrCodeService() is not IQrCodeService qrCodeService)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select helper QR image",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("Image files")
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp" },
                },
            },
        });

        if (files.Count == 0)
        {
            return;
        }

        await using var stream = await files[0].OpenReadAsync();
        if (qrCodeService.TryDecode(stream, out var decoded, out _) && !string.IsNullOrWhiteSpace(decoded))
        {
            vm.ApplyHelperBootstrapInput(decoded, "qr");
        }
    }

    private async void ScanHelperQr_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        CloseImportQrContextMenu();

        if (DataContext is not HelpeePageViewModel vm ||
            TryGetQrCodeService() is not IQrCodeService qrCodeService ||
            TryGetCameraCaptureService() is not ICameraQrCaptureService cameraCaptureService ||
            !cameraCaptureService.IsSupported)
        {
            return;
        }

        var captured = await cameraCaptureService.CapturePhotoAsync(default);
        if (!captured.IsSuccess || string.IsNullOrWhiteSpace(captured.FilePath) || !File.Exists(captured.FilePath))
        {
            return;
        }

        await using var stream = File.OpenRead(captured.FilePath);
        if (qrCodeService.TryDecode(stream, out var decoded, out _) && !string.IsNullOrWhiteSpace(decoded))
        {
            vm.ApplyHelperBootstrapInput(decoded, "qr");
        }
    }

    private void OpenImportQrMenu_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Control control)
        {
            return;
        }

        if (ScanHelperQrMenuItem is not null)
        {
            ScanHelperQrMenuItem.IsEnabled = TryGetCameraCaptureService()?.IsSupported == true;
        }

        if (ImportQrContextMenu is null)
        {
            return;
        }

        ImportQrContextMenu.Close();
        ImportQrContextMenu.Open(control);
        e.Handled = true;
    }

    private void CloseImportQrContextMenu()
    {
        ImportQrContextMenu?.Close();
    }

    private IQrCodeService? TryGetQrCodeService()
    {
        if (Avalonia.Application.Current is not App app)
        {
            return null;
        }

        return app.Services.TryGet<IQrCodeService>(out var service) ? service : null;
    }

    private ICameraQrCaptureService? TryGetCameraCaptureService()
    {
        if (Avalonia.Application.Current is not App app)
        {
            return null;
        }

        return app.Services.TryGet<ICameraQrCaptureService>(out var service) ? service : null;
    }
}
