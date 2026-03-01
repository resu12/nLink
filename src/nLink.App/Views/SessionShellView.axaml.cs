using Avalonia;
using Avalonia.Controls;

namespace NLink.App.Views;

public partial class SessionShellView : UserControl
{
    public static readonly StyledProperty<object?> MainContentProperty =
        AvaloniaProperty.Register<SessionShellView, object?>(nameof(MainContent));

    public static readonly StyledProperty<object?> ChatContentProperty =
        AvaloniaProperty.Register<SessionShellView, object?>(nameof(ChatContent));

    public SessionShellView()
    {
        InitializeComponent();
        UpdatePlaceholderVisibility();
#if DEBUG
        PropertyChanged += (_, e) =>
        {
            if (e.Property == MainContentProperty)
            {
                UpdatePlaceholderVisibility();
            }
        };
#endif
#if !DEBUG
        if (ContentPlaceholderText is not null)
        {
            ContentPlaceholderText.IsVisible = false;
        }
#endif
    }

    public object? MainContent
    {
        get => GetValue(MainContentProperty);
        set => SetValue(MainContentProperty, value);
    }

    public object? ChatContent
    {
        get => GetValue(ChatContentProperty);
        set => SetValue(ChatContentProperty, value);
    }

    private void UpdatePlaceholderVisibility()
    {
        if (ContentPlaceholderText is null)
        {
            return;
        }

#if DEBUG
        ContentPlaceholderText.IsVisible = MainContent is null;
#else
        ContentPlaceholderText.IsVisible = false;
#endif
    }
}
