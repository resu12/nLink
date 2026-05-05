using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using NLink.App.Services;
using NLink.App.ViewModels;

namespace NLink.App.Views;

public partial class DiagnosticsPageView : UserControl
{
    private static readonly Uri NknWebsiteUri = new("https://nkn.org/");
    private DiagnosticsPageViewModel? subscribedViewModel;

    public DiagnosticsPageView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += (_, _) => BindClipboardTopLevel();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (subscribedViewModel is not null)
        {
            subscribedViewModel.CopyReliabilityLogRequested -= OnCopyReliabilityLogRequested;
            subscribedViewModel.OpenLogsFolderRequested -= OnOpenLogsFolderRequested;
            subscribedViewModel.OpenBugReportRequested -= OnOpenBugReportRequested;
            subscribedViewModel.OpenMetricsExportFolderRequested -= OnOpenMetricsExportFolderRequested;
            subscribedViewModel.OpenHangReportFolderRequested -= OnOpenHangReportFolderRequested;
            subscribedViewModel.LinkTunaWalletRequested -= OnLinkTunaWalletRequested;
            subscribedViewModel.ValidateTunaWalletPasswordRequested -= OnValidateTunaWalletPasswordRequested;
            subscribedViewModel.UnlockTunaRuntimePasswordRequested -= OnUnlockTunaRuntimePasswordRequested;
            subscribedViewModel.CopyTunaWalletAddressRequested -= OnCopyTunaWalletAddressRequested;
            subscribedViewModel = null;
        }

        if (DataContext is not DiagnosticsPageViewModel vm)
        {
            return;
        }

        subscribedViewModel = vm;
        subscribedViewModel.CopyReliabilityLogRequested += OnCopyReliabilityLogRequested;
        subscribedViewModel.OpenLogsFolderRequested += OnOpenLogsFolderRequested;
        subscribedViewModel.OpenBugReportRequested += OnOpenBugReportRequested;
        subscribedViewModel.OpenMetricsExportFolderRequested += OnOpenMetricsExportFolderRequested;
        subscribedViewModel.OpenHangReportFolderRequested += OnOpenHangReportFolderRequested;
        subscribedViewModel.LinkTunaWalletRequested += OnLinkTunaWalletRequested;
        subscribedViewModel.ValidateTunaWalletPasswordRequested += OnValidateTunaWalletPasswordRequested;
        subscribedViewModel.UnlockTunaRuntimePasswordRequested += OnUnlockTunaRuntimePasswordRequested;
        subscribedViewModel.CopyTunaWalletAddressRequested += OnCopyTunaWalletAddressRequested;
    }

    private async void OnCopyReliabilityLogRequested(object? sender, string text)
    {
        try
        {
            if (Avalonia.Application.Current is not App app ||
                !app.Services.TryGet<IClipboardService>(out var clipboardService) ||
                clipboardService is null)
            {
                return;
            }

            BindClipboardTopLevel();
            await clipboardService.SetTextAsync(text);
            if (subscribedViewModel is not null)
            {
                subscribedViewModel.NotifyCopySucceeded();
            }
        }
        catch
        {
            subscribedViewModel?.NotifyCopyFailed();
        }
    }

    private void OnOpenLogsFolderRequested(object? sender, string path)
    {
        try
        {
            OpenFolder(path);
        }
        catch
        {
            // Best-effort helper action only.
        }
    }

    private void OnOpenBugReportRequested(object? sender, string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Best-effort helper action only.
        }
    }

    private void OnOpenMetricsExportFolderRequested(object? sender, string path)
    {
        try
        {
            OpenFolder(path);
        }
        catch
        {
            // Best-effort helper action only.
        }
    }

    private void OnOpenHangReportFolderRequested(object? sender, string path)
    {
        try
        {
            OpenFolder(path);
        }
        catch
        {
            // Best-effort helper action only.
        }
    }

    private static void OpenFolder(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        System.IO.Directory.CreateDirectory(fullPath);
        Process.Start(new ProcessStartInfo
        {
            FileName = fullPath,
            UseShellExecute = true,
        });
    }

    private void PoweredByNknLink_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = NknWebsiteUri.ToString(),
                UseShellExecute = true,
            });
        }
        catch
        {
            // Best-effort helper action only.
        }
    }

    private async void OnLinkTunaWalletRequested(object? sender, EventArgs e)
    {
        try
        {
            if (subscribedViewModel is null ||
                TopLevel.GetTopLevel(this) is not TopLevel topLevel)
            {
                return;
            }

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Link NKN wallet.json",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("NKN wallet JSON")
                    {
                        Patterns = ["*.json"],
                        MimeTypes = ["application/json"],
                    },
                    FilePickerFileTypes.All,
                ],
            });
            var selected = files.Count > 0 ? files[0] : null;
            if (selected?.Path.IsFile != true)
            {
                return;
            }

            await subscribedViewModel.LinkTunaWalletAsync(selected.Path.LocalPath);
        }
        catch
        {
            // Best-effort developer diagnostics action only.
        }
    }

    private async void OnValidateTunaWalletPasswordRequested(object? sender, EventArgs e)
    {
        char[]? password = null;
        try
        {
            if (subscribedViewModel is null ||
                TopLevel.GetTopLevel(this) is not Window owner)
            {
                return;
            }

            password = await ShowWalletPasswordDialogAsync(owner, "Enter wallet password", "Validate");
            if (password is null || password.Length == 0)
            {
                return;
            }

            await subscribedViewModel.ValidateTunaWalletAsync(password);
        }
        finally
        {
            if (password is not null)
            {
                Array.Clear(password);
            }
        }
    }

    private async void OnUnlockTunaRuntimePasswordRequested(object? sender, EventArgs e)
    {
        char[]? password = null;
        try
        {
            if (subscribedViewModel is null ||
                TopLevel.GetTopLevel(this) is not Window owner)
            {
                return;
            }

            password = await ShowWalletPasswordDialogAsync(owner, "Unlock Tuna for this session", "Unlock");
            if (password is null || password.Length == 0)
            {
                return;
            }

            await subscribedViewModel.UnlockTunaRuntimeAsync(password);
        }
        finally
        {
            if (password is not null)
            {
                Array.Clear(password);
            }
        }
    }

    private async void OnCopyTunaWalletAddressRequested(object? sender, string address)
    {
        try
        {
            if (Avalonia.Application.Current is not App app ||
                !app.Services.TryGet<IClipboardService>(out var clipboardService) ||
                clipboardService is null)
            {
                return;
            }

            BindClipboardTopLevel();
            await clipboardService.SetTextAsync(address);
            subscribedViewModel?.NotifyTunaWalletAddressCopied();
        }
        catch
        {
            subscribedViewModel?.NotifyTunaWalletAddressCopyFailed();
        }
    }

    private static Task<char[]?> ShowWalletPasswordDialogAsync(Window owner, string title, string acceptText)
    {
        var result = new TaskCompletionSource<char[]?>();
        var passwordBox = new TextBox
        {
            Width = 320,
            PasswordChar = '*',
        };
        var okButton = new Button
        {
            Classes = { "appButton", "primaryButton", "compactButton" },
            Content = acceptText,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var cancelButton = new Button
        {
            Classes = { "appButton", "secondaryButton", "compactButton" },
            Content = "Cancel",
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var window = new Window
        {
            Title = "Wallet password",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        Classes = { "appSectionTitle" },
                    },
                    passwordBox,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancelButton, okButton },
                    },
                },
            },
        };

        void Complete(char[]? value)
        {
            if (passwordBox.Text is not null)
            {
                passwordBox.Text = string.Empty;
            }

            if (!result.Task.IsCompleted)
            {
                result.SetResult(value);
            }

            window.Close();
        }

        okButton.Click += (_, _) =>
        {
            var text = passwordBox.Text ?? string.Empty;
            Complete(text.Length == 0 ? Array.Empty<char>() : text.ToCharArray());
        };
        cancelButton.Click += (_, _) => Complete(null);
        window.Closed += (_, _) =>
        {
            if (!result.Task.IsCompleted)
            {
                result.SetResult(null);
            }
        };

        _ = window.ShowDialog(owner);
        passwordBox.Focus();
        return result.Task;
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
