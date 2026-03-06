using Avalonia.Controls;
using Avalonia.Input;
using NLink.App.Services;
using NLink.App.ViewModels;

namespace NLink.App.Views;

public partial class HelpeePageView : UserControl
{
    public HelpeePageView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => BindClipboardTopLevel();
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
