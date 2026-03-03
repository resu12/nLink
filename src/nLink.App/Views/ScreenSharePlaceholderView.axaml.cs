using Avalonia.Controls;

namespace NLink.App.Views;

public partial class ScreenSharePlaceholderView : UserControl
{
    public ScreenSharePlaceholderView()
    {
        InitializeComponent();
#if !DEBUG
        if (PlaceholderText is not null)
        {
            PlaceholderText.IsVisible = false;
        }
#endif
    }
}
