using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
namespace NLink.App.Views;

public partial class SessionHeaderView : UserControl
{
    private bool hasRoleText;

    public static readonly StyledProperty<string?> StatusTextProperty =
        AvaloniaProperty.Register<SessionHeaderView, string?>(nameof(StatusText));

    public static readonly StyledProperty<string?> VerificationCodeTextProperty =
        AvaloniaProperty.Register<SessionHeaderView, string?>(nameof(VerificationCodeText));

    public static readonly StyledProperty<bool> ShowVerificationCodeProperty =
        AvaloniaProperty.Register<SessionHeaderView, bool>(nameof(ShowVerificationCode), false);

    public static readonly StyledProperty<ICommand?> EndSessionCommandProperty =
        AvaloniaProperty.Register<SessionHeaderView, ICommand?>(nameof(EndSessionCommand));

    public static readonly StyledProperty<bool> CanEndSessionProperty =
        AvaloniaProperty.Register<SessionHeaderView, bool>(nameof(CanEndSession), false);

    public static readonly StyledProperty<bool> ShowEndSessionProperty =
        AvaloniaProperty.Register<SessionHeaderView, bool>(nameof(ShowEndSession), true);

    public static readonly StyledProperty<string?> RoleTextProperty =
        AvaloniaProperty.Register<SessionHeaderView, string?>(nameof(RoleText));

    public static readonly StyledProperty<bool> ShowScreenShareProperty =
        AvaloniaProperty.Register<SessionHeaderView, bool>(nameof(ShowScreenShare), false);

    public static readonly StyledProperty<ICommand?> ScreenShareCommandProperty =
        AvaloniaProperty.Register<SessionHeaderView, ICommand?>(nameof(ScreenShareCommand));

    public static readonly StyledProperty<bool> CanScreenShareProperty =
        AvaloniaProperty.Register<SessionHeaderView, bool>(nameof(CanScreenShare), false);

    public static readonly StyledProperty<string> ScreenShareButtonTextProperty =
        AvaloniaProperty.Register<SessionHeaderView, string>(nameof(ScreenShareButtonText), "Share screen");

    public static readonly StyledProperty<bool> ShowRequestControlProperty =
        AvaloniaProperty.Register<SessionHeaderView, bool>(nameof(ShowRequestControl), false);

    public static readonly StyledProperty<ICommand?> RequestControlCommandProperty =
        AvaloniaProperty.Register<SessionHeaderView, ICommand?>(nameof(RequestControlCommand));

    public static readonly StyledProperty<bool> CanRequestControlProperty =
        AvaloniaProperty.Register<SessionHeaderView, bool>(nameof(CanRequestControl), false);

    public static readonly StyledProperty<bool> ShowStopControlProperty =
        AvaloniaProperty.Register<SessionHeaderView, bool>(nameof(ShowStopControl), false);

    public static readonly StyledProperty<ICommand?> StopControlCommandProperty =
        AvaloniaProperty.Register<SessionHeaderView, ICommand?>(nameof(StopControlCommand));

    public static readonly StyledProperty<string> StopControlButtonTextProperty =
        AvaloniaProperty.Register<SessionHeaderView, string>(nameof(StopControlButtonText), "Stop control");

    public static readonly StyledProperty<bool> CanStopControlProperty =
        AvaloniaProperty.Register<SessionHeaderView, bool>(nameof(CanStopControl), false);

    public static readonly StyledProperty<bool> ShowRemoteControlActiveStatusProperty =
        AvaloniaProperty.Register<SessionHeaderView, bool>(nameof(ShowRemoteControlActiveStatus), false);

    public static readonly StyledProperty<bool> ShowControlModeToggleProperty =
        AvaloniaProperty.Register<SessionHeaderView, bool>(nameof(ShowControlModeToggle), false);

    public static readonly StyledProperty<ICommand?> ControlModeToggleCommandProperty =
        AvaloniaProperty.Register<SessionHeaderView, ICommand?>(nameof(ControlModeToggleCommand));

    public static readonly StyledProperty<string> ControlModeButtonTextProperty =
        AvaloniaProperty.Register<SessionHeaderView, string>(nameof(ControlModeButtonText), "Control mode: Off");

    public static readonly StyledProperty<bool> CanControlModeToggleProperty =
        AvaloniaProperty.Register<SessionHeaderView, bool>(nameof(CanControlModeToggle), false);

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

    public string? VerificationCodeText
    {
        get => GetValue(VerificationCodeTextProperty);
        set => SetValue(VerificationCodeTextProperty, value);
    }

    public bool ShowVerificationCode
    {
        get => GetValue(ShowVerificationCodeProperty);
        set => SetValue(ShowVerificationCodeProperty, value);
    }

    public ICommand? EndSessionCommand
    {
        get => GetValue(EndSessionCommandProperty);
        set => SetValue(EndSessionCommandProperty, value);
    }

    public bool CanEndSession
    {
        get => GetValue(CanEndSessionProperty);
        set => SetValue(CanEndSessionProperty, value);
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

    public string ScreenShareButtonText
    {
        get => GetValue(ScreenShareButtonTextProperty);
        set => SetValue(ScreenShareButtonTextProperty, value);
    }

    public bool CanScreenShare
    {
        get => GetValue(CanScreenShareProperty);
        set => SetValue(CanScreenShareProperty, value);
    }

    public bool ShowRequestControl
    {
        get => GetValue(ShowRequestControlProperty);
        set => SetValue(ShowRequestControlProperty, value);
    }

    public ICommand? RequestControlCommand
    {
        get => GetValue(RequestControlCommandProperty);
        set => SetValue(RequestControlCommandProperty, value);
    }

    public bool CanRequestControl
    {
        get => GetValue(CanRequestControlProperty);
        set => SetValue(CanRequestControlProperty, value);
    }

    public bool ShowStopControl
    {
        get => GetValue(ShowStopControlProperty);
        set => SetValue(ShowStopControlProperty, value);
    }

    public ICommand? StopControlCommand
    {
        get => GetValue(StopControlCommandProperty);
        set => SetValue(StopControlCommandProperty, value);
    }

    public string StopControlButtonText
    {
        get => GetValue(StopControlButtonTextProperty);
        set => SetValue(StopControlButtonTextProperty, value);
    }

    public bool CanStopControl
    {
        get => GetValue(CanStopControlProperty);
        set => SetValue(CanStopControlProperty, value);
    }

    public bool ShowRemoteControlActiveStatus
    {
        get => GetValue(ShowRemoteControlActiveStatusProperty);
        set => SetValue(ShowRemoteControlActiveStatusProperty, value);
    }

    public bool ShowControlModeToggle
    {
        get => GetValue(ShowControlModeToggleProperty);
        set => SetValue(ShowControlModeToggleProperty, value);
    }

    public ICommand? ControlModeToggleCommand
    {
        get => GetValue(ControlModeToggleCommandProperty);
        set => SetValue(ControlModeToggleCommandProperty, value);
    }

    public string ControlModeButtonText
    {
        get => GetValue(ControlModeButtonTextProperty);
        set => SetValue(ControlModeButtonTextProperty, value);
    }

    public bool CanControlModeToggle
    {
        get => GetValue(CanControlModeToggleProperty);
        set => SetValue(CanControlModeToggleProperty, value);
    }

    public bool HasRoleText => hasRoleText;

    private void UpdateHasRoleText()
    {
        var next = !string.IsNullOrWhiteSpace(RoleText);
        SetAndRaise(HasRoleTextProperty, ref hasRoleText, next);
    }

    private void ShareScreenButton_Click(object? sender, RoutedEventArgs e)
    {
        var command = ScreenShareCommand;
        if (command is null || !command.CanExecute(null))
        {
            return;
        }

        command.Execute(null);
    }
}
