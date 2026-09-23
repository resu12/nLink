using System;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using NLink.App.ViewModels;

namespace NLink.App.Views;

public sealed class FileTransferProgressView : StackPanel
{
    private readonly ProgressBar bar = new() { Minimum = 0d, Maximum = 1d };
    private readonly TextBlock text = new();
    private readonly FileTransferProgressPresentation presentation = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private bool attached;

    public FileTransferProgressView()
    {
        Spacing = 6;
        bar.Classes.Add("transferProgress");
        text.Classes.Add("appMuted");
        AutomationProperties.SetAutomationId(bar, "Chat.FileTransfer.ProgressBar");
        AutomationProperties.SetAutomationId(text, "Chat.FileTransfer.ProgressText");
        Children.Add(bar);
        Children.Add(text);
        DataContextChanged += (_, _) => RefreshTarget();
        AttachedToVisualTree += (_, _) =>
        {
            attached = true;
            RefreshTarget();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            attached = false;
            RefreshTarget();
        };
        timer.Tick += (_, _) =>
        {
            presentation.Advance(Environment.TickCount64);
            RenderProgress();
        };
    }

    private void RefreshTarget()
    {
        presentation.SetTarget(DataContext as FileTransferPanelItemViewModel, Environment.TickCount64, attached);
        RenderProgress();
    }

    private void RenderProgress()
    {
        bar.Value = presentation.Fraction;
        text.Text = presentation.Text;
        if (attached && presentation.IsAnimating)
        {
            timer.Start();
        }
        else
        {
            timer.Stop();
        }
    }
}
