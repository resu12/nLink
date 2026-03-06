using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NLink.App.Services;
using NLink.App.ViewModels;

namespace NLink.App.Views;

public partial class HelperPageView : UserControl
{
    private const string HelperCodeInputElementName = "HelperCodeInputBox";
    private HelperPageViewModel? currentViewModel;

    public HelperPageView()
    {
        InitializeComponent();
        PropertyChanged += OnViewPropertyChanged;
        AttachedToVisualTree += (_, _) =>
        {
            BindClipboardTopLevel();
            ScheduleFocusHelperCodeInput();
        };
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
            currentViewModel.RemoteControlViewerFocusRequested -= OnRemoteControlViewerFocusRequested;
            currentViewModel.ScanQrFromFileRequested -= OnScanQrFromFileRequested;
            currentViewModel.ScanQrFromCameraRequested -= OnScanQrFromCameraRequested;
        }

        currentViewModel = DataContext as HelperPageViewModel;

        if (currentViewModel is not null)
        {
            currentViewModel.SendFileRequested += OnSendFileRequested;
            currentViewModel.RemoteControlViewerFocusRequested += OnRemoteControlViewerFocusRequested;
            currentViewModel.ScanQrFromFileRequested += OnScanQrFromFileRequested;
            currentViewModel.ScanQrFromCameraRequested += OnScanQrFromCameraRequested;
        }

        ScheduleFocusHelperCodeInput();
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

    private void HelperCodeInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if ((e.KeyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift)
        {
            return;
        }

        if (DataContext is not HelperPageViewModel vm)
        {
            return;
        }

        if (!vm.ConnectCommand.CanExecute(null))
        {
            return;
        }

        vm.ConnectCommand.Execute(null);
        e.Handled = true;
    }

    private void ScreenShareSurfaceView_RemoteControlInputProduced(object? sender, RemoteControlInputProducedEventArgs e)
    {
        if (DataContext is not HelperPageViewModel vm)
        {
            return;
        }

        vm.PostRemoteControlInput(e.Message);
    }

    private void ScreenShareSurfaceView_RemoteControlHeldStateChanged(object? sender, RemoteControlHeldStateChangedEventArgs e)
    {
        if (DataContext is not HelperPageViewModel vm)
        {
            return;
        }

        vm.UpdateRemoteControlHeldState(e.ModifiersMask, e.MouseButtonsMask, e.ImmediateReleaseAll);
    }

    private void ScreenShareSurfaceView_ControlModeExitRequested(object? sender, EventArgs e)
    {
        if (DataContext is not HelperPageViewModel vm)
        {
            return;
        }

        vm.ExitControlMode();
    }

    private void OnRemoteControlViewerFocusRequested(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(FocusRemoteControlViewer, DispatcherPriority.Input);
    }

    private async void OnScanQrFromFileRequested(object? sender, EventArgs e)
    {
        await ScanQrFromFileAsync();
    }

    private async void OnScanQrFromCameraRequested(object? sender, EventArgs e)
    {
        await ScanQrFromCameraAsync();
    }

    private void FocusRemoteControlViewer()
    {
        var surface = this.FindControl<ScreenShareSurfaceView>("RemoteScreenShareSurface");
        if (surface is null || !surface.IsVisible || !surface.IsEnabled)
        {
            return;
        }

        surface.Focus();
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

    private void ScheduleFocusHelperCodeInput()
    {
        Dispatcher.UIThread.Post(TryFocusHelperCodeInput, DispatcherPriority.Loaded);
    }

    private void TryFocusHelperCodeInput()
    {
        var codeInput = this.FindControl<TextBox>(HelperCodeInputElementName);
        if (codeInput is null || !codeInput.IsVisible || !codeInput.IsEnabled)
        {
            return;
        }

        codeInput.Focus();
        codeInput.CaretIndex = 0;
    }

    private async void PasteFromClipboard_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not HelperPageViewModel vm)
        {
            return;
        }

        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard is null)
            {
                vm.NotifyExternalInputError("Clipboard isn't available right now.");
                return;
            }

            var text = await topLevel.Clipboard.GetTextAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                vm.NotifyExternalInputError("There's no text in the clipboard.");
                return;
            }

            vm.ApplyExternalConnectInput(text, "clipboard");
        }
        catch
        {
            vm.NotifyExternalInputError("Couldn't paste from clipboard.");
        }
    }

    private async Task ScanQrFromFileAsync()
    {
        if (DataContext is not HelperPageViewModel vm)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            vm.NotifyExternalInputError("Can't open files right now.");
            return;
        }

        if (TryGetQrCodeService() is not IQrCodeService qrCodeService)
        {
            vm.NotifyExternalInputError("QR scanning isn't available right now.");
            return;
        }

        try
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select image with QR code",
                AllowMultiple = false,
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new("Image files")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp" },
                        MimeTypes = new[] { "image/png", "image/jpeg", "image/bmp", "image/webp" },
                    },
                },
            });

            if (files.Count == 0)
            {
                return;
            }

            await using var stream = await files[0].OpenReadAsync();
            if (!qrCodeService.TryDecode(stream, out var decoded, out var error) || string.IsNullOrWhiteSpace(decoded))
            {
                vm.NotifyExternalInputError(error ?? "No QR code found in the selected image.");
                return;
            }

            vm.ApplyExternalConnectInput(decoded, "qr");
        }
        catch
        {
            vm.NotifyExternalInputError("Couldn't scan that QR code.");
        }
    }

    private async Task ScanQrFromCameraAsync()
    {
        if (DataContext is not HelperPageViewModel vm)
        {
            return;
        }

        if (TryGetQrCodeService() is not IQrCodeService qrCodeService ||
            TryGetCameraCaptureService() is not ICameraQrCaptureService cameraCaptureService)
        {
            vm.NotifyExternalInputError("Camera scanning isn't available right now.");
            return;
        }

        if (!cameraCaptureService.IsSupported)
        {
            vm.NotifyExternalInputError("Camera scanning isn't available on this device.");
            return;
        }

        try
        {
            var captured = await cameraCaptureService.CapturePhotoAsync(default);
            if (!captured.IsSuccess)
            {
                if (!captured.IsCancelled)
                {
                    vm.NotifyExternalInputError(captured.Message ?? "Couldn't capture an image from the camera.");
                }

                return;
            }

            if (string.IsNullOrWhiteSpace(captured.FilePath) || !File.Exists(captured.FilePath))
            {
                vm.NotifyExternalInputError("Couldn't use the captured image.");
                return;
            }

            await using var stream = File.OpenRead(captured.FilePath);
            if (!qrCodeService.TryDecode(stream, out var decoded, out var error) || string.IsNullOrWhiteSpace(decoded))
            {
                vm.NotifyExternalInputError(error ?? "No QR code found in the captured image.");
                return;
            }

            vm.ApplyExternalConnectInput(decoded, "qr");
        }
        catch
        {
            vm.NotifyExternalInputError("Couldn't scan that QR code.");
        }
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
