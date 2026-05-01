using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace NLink.SmokeTests;

public static class GuiTestAssertions
{
    public static T FindVisibleByAutomationId<T>(Control root, string automationId)
        where T : Control
    {
        var control = root.GetVisualDescendants()
            .OfType<T>()
            .FirstOrDefault(candidate =>
                candidate.IsVisible &&
                string.Equals(AutomationProperties.GetAutomationId(candidate), automationId, StringComparison.Ordinal));

        Assert.NotNull(control);
        return control!;
    }

    public static Button FindVisibleEnabledButton(Control root, string automationId)
    {
        var button = FindVisibleByAutomationId<Button>(root, automationId);
        Assert.True(button.IsEnabled, $"Expected button '{automationId}' to be enabled.");
        return button;
    }

    public static Button FindVisibleDisabledButton(Control root, string automationId)
    {
        var button = FindVisibleByAutomationId<Button>(root, automationId);
        Assert.False(button.IsEnabled, $"Expected button '{automationId}' to be disabled.");
        return button;
    }

    public static async Task FlushUiAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
    }
}
