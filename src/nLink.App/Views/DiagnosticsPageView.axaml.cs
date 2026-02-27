using System;
using System.Diagnostics;
using Avalonia.Controls;
using NLink.App.Services;
using NLink.App.ViewModels;

namespace NLink.App.Views;

public partial class DiagnosticsPageView : UserControl
{
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
