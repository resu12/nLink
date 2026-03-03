using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
namespace NLink.App.Views;

public partial class SessionHeaderView : UserControl
{
    private bool hasRoleText;

    public static readonly StyledProperty<string?> StatusTextProperty =
        AvaloniaProperty.Register<SessionHeaderView, string?>(nameof(StatusText));

    public static readonly StyledProperty<ICommand?> EndSessionCommandProperty =
        AvaloniaProperty.Register<SessionHeaderView, ICommand?>(nameof(EndSessionCommand));

    public static readonly StyledProperty<bool> ShowEndSessionProperty =
        AvaloniaProperty.Register<SessionHeaderView, bool>(nameof(ShowEndSession), true);

    public static readonly StyledProperty<string?> RoleTextProperty =
        AvaloniaProperty.Register<SessionHeaderView, string?>(nameof(RoleText));

    public static readonly StyledProperty<bool> ShowScreenShareProperty =
        AvaloniaProperty.Register<SessionHeaderView, bool>(nameof(ShowScreenShare), false);

    public static readonly StyledProperty<ICommand?> ScreenShareCommandProperty =
        AvaloniaProperty.Register<SessionHeaderView, ICommand?>(nameof(ScreenShareCommand));

    public static readonly DirectProperty<SessionHeaderView, bool> HasRoleTextProperty =
        AvaloniaProperty.RegisterDirect<SessionHeaderView, bool>(
            nameof(HasRoleText),
            view => view.HasRoleText);

    static SessionHeaderView()
    {
        RoleTextProperty.Changed.AddClassHandler<SessionHeaderView>((view, _) =>
            view.UpdateHasRoleText());
    }

    public SessionHeaderView()
    {
        InitializeComponent();
        UpdateHasRoleText();
    }

    public string? StatusText
    {
        get => GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public ICommand? EndSessionCommand
    {
        get => GetValue(EndSessionCommandProperty);
        set => SetValue(EndSessionCommandProperty, value);
    }

    public bool ShowEndSession
    {
        get => GetValue(ShowEndSessionProperty);
        set => SetValue(ShowEndSessionProperty, value);
    }

    public string? RoleText
    {
        get => GetValue(RoleTextProperty);
        set => SetValue(RoleTextProperty, value);
    }

    public bool ShowScreenShare
    {
        get => GetValue(ShowScreenShareProperty);
        set => SetValue(ShowScreenShareProperty, value);
    }

    public ICommand? ScreenShareCommand
    {
        get => GetValue(ScreenShareCommandProperty);
        set => SetValue(ScreenShareCommandProperty, value);
    }

    public bool HasRoleText => hasRoleText;

    private void UpdateHasRoleText()
    {
        var next = !string.IsNullOrWhiteSpace(RoleText);
        SetAndRaise(HasRoleTextProperty, ref hasRoleText, next);
    }
}
