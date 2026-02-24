using System;
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
            subscribedViewModel = null;
        }

        if (DataContext is not DiagnosticsPageViewModel vm)
        {
            return;
        }

        subscribedViewModel = vm;
        subscribedViewModel.CopyReliabilityLogRequested += OnCopyReliabilityLogRequested;
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
