using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using NLink.App.Services;

namespace NLink.App.Views;

public partial class SessionHeaderView : UserControl
{
    private static readonly IBrush TunaActiveBrush = new SolidColorBrush(Color.FromRgb(125, 220, 255));
    private static readonly IBrush TunaPayerBrush = new SolidColorBrush(Color.FromRgb(255, 211, 91));
    private static readonly IBrush TunaInactiveBrush = new SolidColorBrush(Color.FromRgb(111, 122, 136));
    private static readonly IBrush TunaActiveGlowBrush = new SolidColorBrush(Color.FromRgb(94, 209, 255));
    private static readonly IBrush TunaPayerGlowBrush = new SolidColorBrush(Color.FromRgb(255, 194, 55));
    private static readonly IBrush TunaInactiveGlowBrush = new SolidColorBrush(Colors.Transparent);
    private static readonly IBrush TunaUnlockTrackOnBrush = new SolidColorBrush(Color.FromRgb(20, 142, 210));
    private static readonly IBrush TunaUnlockTrackOffBrush = new SolidColorBrush(Color.FromRgb(54, 64, 78));
    private static readonly IBrush TunaUnlockTrackDisabledBrush = new SolidColorBrush(Color.FromRgb(38, 45, 56));
    private static readonly IBrush TunaUnlockThumbOnBrush = new SolidColorBrush(Color.FromRgb(226, 249, 255));
    private static readonly IBrush TunaUnlockThumbOffBrush = new SolidColorBrush(Color.FromRgb(137, 148, 162));
    private static readonly IBrush TunaUnlockThumbDisabledBrush = new SolidColorBrush(Color.FromRgb(87, 98, 112));
    private bool hasRoleText;
    private bool hasScreenShareAccessory;
    private IBrush tunaPictogramBrush = TunaInactiveBrush;
    private IBrush tunaGlowBrush = TunaInactiveGlowBrush;
    private double tunaPictogramOpacity = 0.58d;
    private double tunaGlowOpacity;
    private double tunaInnerGlowOpacity;
    private double tunaGillOpacity = 0.55d;
    private double tunaPulseScale = 1d;
    private string tunaPictogramTip = "Tuna acceleration inactive";
    private bool showTunaUnlockToggle;
    private bool canTunaUnlockToggle;
    private bool tunaUnlockToggleOn;
    private IBrush tunaUnlockTrackBrush = TunaUnlockTrackOffBrush;
    private IBrush tunaUnlockThumbBrush = TunaUnlockThumbOffBrush;
    private Thickness tunaUnlockThumbMargin = new(2, 0, 0, 0);
    private string tunaUnlockTip = "Unlock Tuna wallet";
    private bool tunaUnlockBusy;
    private int tunaUnlockRefreshVersion;
    private string tunaRuntimeStatus = "unknown";
    private string? tunaUnlockMessage;
    private DateTimeOffset? tunaUnlockMessageUntilUtc;
    private ITunaRuntimePilotService? subscribedTunaRuntime;
    private readonly DispatcherTimer tunaPulseTimer;
    private DateTime tunaPulseStartedUtc;

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

    public static readonly StyledProperty<bool> TunaActiveProperty =
        AvaloniaProperty.Register<SessionHeaderView, bool>(nameof(TunaActive), false);

    public static readonly StyledProperty<string?> TunaStatusReasonProperty =
        AvaloniaProperty.Register<SessionHeaderView, string?>(nameof(TunaStatusReason), "inactive");

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

    public static readonly StyledProperty<Control?> ScreenShareAccessoryProperty =
        AvaloniaProperty.Register<SessionHeaderView, Control?>(nameof(ScreenShareAccessory));

    public static readonly DirectProperty<SessionHeaderView, bool> HasRoleTextProperty =
        AvaloniaProperty.RegisterDirect<SessionHeaderView, bool>(
            nameof(HasRoleText),
            view => view.HasRoleText);

    public static readonly DirectProperty<SessionHeaderView, bool> HasScreenShareAccessoryProperty =
        AvaloniaProperty.RegisterDirect<SessionHeaderView, bool>(
            nameof(HasScreenShareAccessory),
            view => view.HasScreenShareAccessory);

    public static readonly DirectProperty<SessionHeaderView, IBrush> TunaPictogramBrushProperty =
        AvaloniaProperty.RegisterDirect<SessionHeaderView, IBrush>(
            nameof(TunaPictogramBrush),
            view => view.TunaPictogramBrush);

    public static readonly DirectProperty<SessionHeaderView, IBrush> TunaGlowBrushProperty =
        AvaloniaProperty.RegisterDirect<SessionHeaderView, IBrush>(
            nameof(TunaGlowBrush),
            view => view.TunaGlowBrush);

    public static readonly DirectProperty<SessionHeaderView, double> TunaPictogramOpacityProperty =
        AvaloniaProperty.RegisterDirect<SessionHeaderView, double>(
            nameof(TunaPictogramOpacity),
            view => view.TunaPictogramOpacity);

    public static readonly DirectProperty<SessionHeaderView, double> TunaGlowOpacityProperty =
        AvaloniaProperty.RegisterDirect<SessionHeaderView, double>(
            nameof(TunaGlowOpacity),
            view => view.TunaGlowOpacity);

    public static readonly DirectProperty<SessionHeaderView, double> TunaInnerGlowOpacityProperty =
        AvaloniaProperty.RegisterDirect<SessionHeaderView, double>(
            nameof(TunaInnerGlowOpacity),
            view => view.TunaInnerGlowOpacity);

    public static readonly DirectProperty<SessionHeaderView, double> TunaGillOpacityProperty =
        AvaloniaProperty.RegisterDirect<SessionHeaderView, double>(
            nameof(TunaGillOpacity),
            view => view.TunaGillOpacity);

    public static readonly DirectProperty<SessionHeaderView, double> TunaPulseScaleProperty =
        AvaloniaProperty.RegisterDirect<SessionHeaderView, double>(
            nameof(TunaPulseScale),
            view => view.TunaPulseScale);

    public static readonly DirectProperty<SessionHeaderView, string> TunaPictogramTipProperty =
        AvaloniaProperty.RegisterDirect<SessionHeaderView, string>(
            nameof(TunaPictogramTip),
            view => view.TunaPictogramTip);

    public static readonly DirectProperty<SessionHeaderView, bool> ShowTunaUnlockToggleProperty =
        AvaloniaProperty.RegisterDirect<SessionHeaderView, bool>(
            nameof(ShowTunaUnlockToggle),
            view => view.ShowTunaUnlockToggle);

    public static readonly DirectProperty<SessionHeaderView, bool> CanTunaUnlockToggleProperty =
        AvaloniaProperty.RegisterDirect<SessionHeaderView, bool>(
            nameof(CanTunaUnlockToggle),
            view => view.CanTunaUnlockToggle);

    public static readonly DirectProperty<SessionHeaderView, bool> TunaUnlockToggleOnProperty =
        AvaloniaProperty.RegisterDirect<SessionHeaderView, bool>(
            nameof(TunaUnlockToggleOn),
            view => view.TunaUnlockToggleOn);

    public static readonly DirectProperty<SessionHeaderView, IBrush> TunaUnlockTrackBrushProperty =
        AvaloniaProperty.RegisterDirect<SessionHeaderView, IBrush>(
            nameof(TunaUnlockTrackBrush),
            view => view.TunaUnlockTrackBrush);

    public static readonly DirectProperty<SessionHeaderView, IBrush> TunaUnlockThumbBrushProperty =
        AvaloniaProperty.RegisterDirect<SessionHeaderView, IBrush>(
            nameof(TunaUnlockThumbBrush),
            view => view.TunaUnlockThumbBrush);

    public static readonly DirectProperty<SessionHeaderView, Thickness> TunaUnlockThumbMarginProperty =
        AvaloniaProperty.RegisterDirect<SessionHeaderView, Thickness>(
            nameof(TunaUnlockThumbMargin),
            view => view.TunaUnlockThumbMargin);

    public static readonly DirectProperty<SessionHeaderView, string> TunaUnlockTipProperty =
        AvaloniaProperty.RegisterDirect<SessionHeaderView, string>(
            nameof(TunaUnlockTip),
            view => view.TunaUnlockTip);

    static SessionHeaderView()
    {
        RoleTextProperty.Changed.AddClassHandler<SessionHeaderView>((view, _) =>
            view.UpdateHasRoleText());
        ScreenShareAccessoryProperty.Changed.AddClassHandler<SessionHeaderView>((view, _) =>
            view.UpdateHasScreenShareAccessory());
        TunaActiveProperty.Changed.AddClassHandler<SessionHeaderView>((view, _) =>
            view.UpdateTunaVisualState());
        TunaStatusReasonProperty.Changed.AddClassHandler<SessionHeaderView>((view, _) =>
            view.UpdateTunaVisualState());
    }

    public SessionHeaderView()
    {
        tunaPulseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(60),
        };
        tunaPulseTimer.Tick += OnTunaPulseTimerTick;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;

        InitializeComponent();
        UpdateHasRoleText();
        UpdateHasScreenShareAccessory();
        UpdateTunaVisualState();
        _ = RefreshTunaUnlockToggleAsync();
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

    public bool TunaActive
    {
        get => GetValue(TunaActiveProperty);
        set => SetValue(TunaActiveProperty, value);
    }

    public string? TunaStatusReason
    {
        get => GetValue(TunaStatusReasonProperty);
        set => SetValue(TunaStatusReasonProperty, value);
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

    public Control? ScreenShareAccessory
    {
        get => GetValue(ScreenShareAccessoryProperty);
        set => SetValue(ScreenShareAccessoryProperty, value);
    }

    public bool HasRoleText => hasRoleText;

    public bool HasScreenShareAccessory => hasScreenShareAccessory;

    public IBrush TunaPictogramBrush => tunaPictogramBrush;

    public IBrush TunaGlowBrush => tunaGlowBrush;

    public double TunaPictogramOpacity => tunaPictogramOpacity;

    public double TunaGlowOpacity => tunaGlowOpacity;

    public double TunaInnerGlowOpacity => tunaInnerGlowOpacity;

    public double TunaGillOpacity => tunaGillOpacity;

    public double TunaPulseScale => tunaPulseScale;

    public string TunaPictogramTip => tunaPictogramTip;

    public bool ShowTunaUnlockToggle => showTunaUnlockToggle;

    public bool CanTunaUnlockToggle => canTunaUnlockToggle;

    public bool TunaUnlockToggleOn => tunaUnlockToggleOn;

    public IBrush TunaUnlockTrackBrush => tunaUnlockTrackBrush;

    public IBrush TunaUnlockThumbBrush => tunaUnlockThumbBrush;

    public Thickness TunaUnlockThumbMargin => tunaUnlockThumbMargin;

    public string TunaUnlockTip => tunaUnlockTip;

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (tunaUnlockToggleOn &&
            TunaStatusPresentationMapper.FromState(
                transportActive: TunaActive,
                transportReason: TunaStatusReason,
                runtimeStatus: tunaRuntimeStatus,
                sessionUnlockOn: true).IsConnecting)
        {
            StartTunaPulse();
        }

        SubscribeTunaRuntimeStateChanged();
        _ = RefreshTunaUnlockToggleAsync();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        StopTunaPulse();
        UnsubscribeTunaRuntimeStateChanged();
    }

    private void UpdateHasRoleText()
    {
        var next = !string.IsNullOrWhiteSpace(RoleText);
        SetAndRaise(HasRoleTextProperty, ref hasRoleText, next);
    }

    private void UpdateHasScreenShareAccessory()
    {
        var next = ScreenShareAccessory is not null;
        SetAndRaise(HasScreenShareAccessoryProperty, ref hasScreenShareAccessory, next);
    }

    private void UpdateTunaVisualState()
    {
        var active = TunaActive;
        var reason = string.IsNullOrWhiteSpace(TunaStatusReason) ? "inactive" : TunaStatusReason.Trim();
        var presentation = TunaStatusPresentationMapper.FromState(
            active,
            reason,
            tunaRuntimeStatus,
            tunaUnlockToggleOn);
        var pulsing = !active && tunaUnlockToggleOn && presentation.IsConnecting;
        var highlighted = active || pulsing;
        var paying = highlighted && presentation.IsLocalPayer;
        var nextPictogramBrush = highlighted ? (paying ? TunaPayerBrush : TunaActiveBrush) : TunaInactiveBrush;
        var nextGlowBrush = highlighted ? (paying ? TunaPayerGlowBrush : TunaActiveGlowBrush) : TunaInactiveGlowBrush;
        var nextPictogramOpacity = highlighted ? 1d : 0.58d;
        var nextGlowOpacity = active ? 0.38d : pulsing ? 0.55d : 0d;
        var nextInnerGlowOpacity = active ? 0.2d : pulsing ? 0.28d : 0d;
        var nextGillOpacity = highlighted ? 0.92d : 0.55d;
        var nextTip = paying
            ? $"{presentation.Text} This computer is the Tuna listener and pays for Tuna traffic while active."
            : presentation.Text;

        SetAndRaise(TunaPictogramBrushProperty, ref tunaPictogramBrush, nextPictogramBrush);
        SetAndRaise(TunaGlowBrushProperty, ref tunaGlowBrush, nextGlowBrush);
        SetAndRaise(TunaPictogramOpacityProperty, ref tunaPictogramOpacity, nextPictogramOpacity);
        SetAndRaise(TunaGlowOpacityProperty, ref tunaGlowOpacity, nextGlowOpacity);
        SetAndRaise(TunaInnerGlowOpacityProperty, ref tunaInnerGlowOpacity, nextInnerGlowOpacity);
        SetAndRaise(TunaGillOpacityProperty, ref tunaGillOpacity, nextGillOpacity);
        SetAndRaise(TunaPulseScaleProperty, ref tunaPulseScale, 1d);
        SetAndRaise(TunaPictogramTipProperty, ref tunaPictogramTip, nextTip);

        if (pulsing)
        {
            StartTunaPulse();
        }
        else
        {
            StopTunaPulse();
        }
    }

    private void StartTunaPulse()
    {
        if (tunaPulseTimer.IsEnabled)
        {
            return;
        }

        tunaPulseStartedUtc = DateTime.UtcNow;
        tunaPulseTimer.Start();
    }

    private void StopTunaPulse()
    {
        tunaPulseTimer.Stop();
        SetAndRaise(TunaPulseScaleProperty, ref tunaPulseScale, 1d);
    }

    private void OnTunaPulseTimerTick(object? sender, EventArgs e)
    {
        if (TunaActive ||
            !tunaUnlockToggleOn ||
            !TunaStatusPresentationMapper.FromState(
                transportActive: false,
                transportReason: TunaStatusReason,
                runtimeStatus: tunaRuntimeStatus,
                sessionUnlockOn: true).IsConnecting)
        {
            StopTunaPulse();
            UpdateTunaVisualState();
            return;
        }

        var elapsed = DateTime.UtcNow - tunaPulseStartedUtc;
        var cycle = elapsed.TotalMilliseconds % 1400d / 1400d;
        var wave = (Math.Sin(cycle * Math.PI * 2d) + 1d) / 2d;
        var nextGlowOpacity = 0.24d + (0.58d * wave);
        var nextInnerGlowOpacity = 0.18d + (0.22d * wave);
        var nextScale = 1d + (0.11d * wave);

        SetAndRaise(TunaGlowOpacityProperty, ref tunaGlowOpacity, nextGlowOpacity);
        SetAndRaise(TunaInnerGlowOpacityProperty, ref tunaInnerGlowOpacity, nextInnerGlowOpacity);
        SetAndRaise(TunaPulseScaleProperty, ref tunaPulseScale, nextScale);
    }

    private async void TunaUnlockToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (tunaUnlockBusy)
        {
            return;
        }

        if (!TryGetAppService<ITunaRuntimePilotService>(out var runtime) || runtime is null)
        {
            SetTunaUnlockMessage("Tuna runtime unavailable.");
            await RefreshTunaUnlockToggleAsync().ConfigureAwait(true);
            return;
        }

        var currentState = await runtime.GetUnlockStateAsync(CancellationToken.None).ConfigureAwait(true);
        if (currentState.IsOn)
        {
            try
            {
                tunaUnlockBusy = true;
                var result = await runtime
                    .LockOrStopForSessionAsync("header_switch_off", TunaRuntimeUnlockSource.Header, CancellationToken.None)
                    .ConfigureAwait(true);
                SetTunaUnlockMessage(result.Message);
            }
            catch
            {
                SetTunaUnlockMessage("Tuna could not be stopped. Current NKN remains available.");
            }
            finally
            {
                tunaUnlockBusy = false;
                await RefreshTunaUnlockToggleAsync().ConfigureAwait(true);
            }

            return;
        }

        if (!currentState.CanToggle)
        {
            SetTunaUnlockMessage(currentState.UserMessage);
            await RefreshTunaUnlockToggleAsync().ConfigureAwait(true);
            return;
        }

        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            SetTunaUnlockMessage("Wallet password prompt is unavailable.");
            await RefreshTunaUnlockToggleAsync().ConfigureAwait(true);
            return;
        }

        char[]? password = null;
        try
        {
            password = await WalletPasswordDialog.ShowAsync(owner, "Unlock Tuna for this session", "Unlock").ConfigureAwait(true);
            if (password is null || password.Length == 0)
            {
                await RefreshTunaUnlockToggleAsync().ConfigureAwait(true);
                return;
            }

            tunaUnlockBusy = true;
            SetTunaUnlockMessage("Unlocking Tuna wallet...");
            await RefreshTunaUnlockToggleAsync().ConfigureAwait(true);

            var result = await runtime
                .UnlockForSessionAsync(password, TunaRuntimeUnlockSource.Header, CancellationToken.None)
                .ConfigureAwait(true);
            SetTunaUnlockMessage(result.Message);
        }
        finally
        {
            tunaUnlockBusy = false;
            await RefreshTunaUnlockToggleAsync().ConfigureAwait(true);
        }
    }

    private void SubscribeTunaRuntimeStateChanged()
    {
        if (subscribedTunaRuntime is not null)
        {
            return;
        }

        if (TryGetAppService<ITunaRuntimePilotService>(out var runtime) && runtime is not null)
        {
            subscribedTunaRuntime = runtime;
            runtime.StateChanged += OnTunaRuntimeStateChanged;
        }
    }

    private void UnsubscribeTunaRuntimeStateChanged()
    {
        if (subscribedTunaRuntime is null)
        {
            return;
        }

        subscribedTunaRuntime.StateChanged -= OnTunaRuntimeStateChanged;
        subscribedTunaRuntime = null;
    }

    private void OnTunaRuntimeStateChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() => _ = RefreshTunaUnlockToggleAsync());

    private async Task RefreshTunaUnlockToggleAsync()
    {
        var version = Interlocked.Increment(ref tunaUnlockRefreshVersion);
        if (!TryGetAppService<ITunaRuntimePilotService>(out var runtime) || runtime is null)
        {
            await Dispatcher.UIThread.InvokeAsync(() => ApplyTunaUnlockToggleState(
                show: false,
                canToggle: false,
                unlocked: false,
                tip: "Tuna wallet unavailable",
                runtimeStatus: "service_unavailable"));
            return;
        }

        var state = await runtime.GetUnlockStateAsync(CancellationToken.None).ConfigureAwait(false);
        if (version != Volatile.Read(ref tunaUnlockRefreshVersion))
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var presentation = TunaStatusPresentationMapper.FromState(
                TunaActive,
                TunaStatusReason,
                state.RuntimeStatus,
                state.IsOn);
            var tip = GetPendingTunaUnlockMessage() ?? presentation.Text;
            ApplyTunaUnlockToggleState(
                state.IsVisible,
                state.CanToggle && !tunaUnlockBusy,
                state.IsOn,
                tip,
                state.RuntimeStatus);
        });
    }

    private void ApplyTunaUnlockToggleState(bool show, bool canToggle, bool unlocked, string tip, string runtimeStatus)
    {
        SetAndRaise(ShowTunaUnlockToggleProperty, ref showTunaUnlockToggle, show);
        SetAndRaise(CanTunaUnlockToggleProperty, ref canTunaUnlockToggle, canToggle);
        SetAndRaise(TunaUnlockToggleOnProperty, ref tunaUnlockToggleOn, unlocked);
        SetAndRaise(TunaUnlockTipProperty, ref tunaUnlockTip, string.IsNullOrWhiteSpace(tip) ? "Unlock Tuna wallet" : tip);
        tunaRuntimeStatus = string.IsNullOrWhiteSpace(runtimeStatus) ? "unknown" : runtimeStatus.Trim();

        var track = unlocked
            ? TunaUnlockTrackOnBrush
            : canToggle ? TunaUnlockTrackOffBrush : TunaUnlockTrackDisabledBrush;
        var thumb = unlocked
            ? TunaUnlockThumbOnBrush
            : canToggle ? TunaUnlockThumbOffBrush : TunaUnlockThumbDisabledBrush;
        var margin = unlocked ? new Thickness(16, 0, 0, 0) : new Thickness(2, 0, 0, 0);

        SetAndRaise(TunaUnlockTrackBrushProperty, ref tunaUnlockTrackBrush, track);
        SetAndRaise(TunaUnlockThumbBrushProperty, ref tunaUnlockThumbBrush, thumb);
        SetAndRaise(TunaUnlockThumbMarginProperty, ref tunaUnlockThumbMargin, margin);
        UpdateTunaVisualState();
    }

    private static async Task<TunaWalletLinkState> LoadWalletStateAsync(ITunaWalletLinkStore store)
    {
        try
        {
            return await store.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            return TunaWalletLinkState.Unlinked;
        }
    }

    private static bool TryGetAppService<T>(out T? service)
        where T : class
    {
        if (Application.Current is App app &&
            app.Services.TryGet<T>(out var resolved) &&
            resolved is not null)
        {
            service = resolved;
            return true;
        }

        service = null;
        return false;
    }

    private void SetTunaUnlockMessage(string message)
    {
        tunaUnlockMessage = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        tunaUnlockMessageUntilUtc = tunaUnlockMessage is null ? null : DateTimeOffset.UtcNow.AddSeconds(8);
    }

    private string? GetPendingTunaUnlockMessage()
    {
        if (string.IsNullOrWhiteSpace(tunaUnlockMessage) ||
            tunaUnlockMessageUntilUtc is not { } until ||
            DateTimeOffset.UtcNow > until)
        {
            tunaUnlockMessage = null;
            tunaUnlockMessageUntilUtc = null;
            return null;
        }

        return tunaUnlockMessage;
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
