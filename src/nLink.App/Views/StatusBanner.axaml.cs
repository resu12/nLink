using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using NLink.App.Services;

namespace NLink.App.Views;

public partial class StatusBanner : UserControl, INotifyPropertyChanged
{
    public static readonly StyledProperty<UserFacingStatus?> StatusProperty =
        AvaloniaProperty.Register<StatusBanner, UserFacingStatus?>(nameof(Status));

    public static readonly StyledProperty<ICommand?> CopyDiagnosticsCommandProperty =
        AvaloniaProperty.Register<StatusBanner, ICommand?>(nameof(CopyDiagnosticsCommand));

    public static readonly StyledProperty<ICommand?> CancelCommandProperty =
        AvaloniaProperty.Register<StatusBanner, ICommand?>(nameof(CancelCommand));

    public static readonly StyledProperty<string?> DetailsTextProperty =
        AvaloniaProperty.Register<StatusBanner, string?>(nameof(DetailsText));
    public static readonly StyledProperty<string?> FailureCategoryProperty =
        AvaloniaProperty.Register<StatusBanner, string?>(nameof(FailureCategory));
    public static readonly StyledProperty<string?> SessionCorrelationIdProperty =
        AvaloniaProperty.Register<StatusBanner, string?>(nameof(SessionCorrelationId));
    public static readonly StyledProperty<string?> LastConnectDurationProperty =
        AvaloniaProperty.Register<StatusBanner, string?>(nameof(LastConnectDuration));
    public static readonly StyledProperty<string?> LastHandshakeDurationProperty =
        AvaloniaProperty.Register<StatusBanner, string?>(nameof(LastHandshakeDuration));
    public static readonly StyledProperty<string?> BridgeStateProperty =
        AvaloniaProperty.Register<StatusBanner, string?>(nameof(BridgeState));

    public static readonly StyledProperty<bool> ForceVisibleProperty =
        AvaloniaProperty.Register<StatusBanner, bool>(nameof(ForceVisible), true);

    public StatusBanner()
    {
        InitializeComponent();
        DataContext = this;
    }

    public new event PropertyChangedEventHandler? PropertyChanged;

    public UserFacingStatus? Status
    {
        get => GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public ICommand? CopyDiagnosticsCommand
    {
        get => GetValue(CopyDiagnosticsCommandProperty);
        set => SetValue(CopyDiagnosticsCommandProperty, value);
    }

    public ICommand? CancelCommand
    {
        get => GetValue(CancelCommandProperty);
        set => SetValue(CancelCommandProperty, value);
    }

    public string? DetailsText
    {
        get => GetValue(DetailsTextProperty);
        set => SetValue(DetailsTextProperty, value);
    }

    public string? FailureCategory
    {
        get => GetValue(FailureCategoryProperty);
        set => SetValue(FailureCategoryProperty, value);
    }

    public string? SessionCorrelationId
    {
        get => GetValue(SessionCorrelationIdProperty);
        set => SetValue(SessionCorrelationIdProperty, value);
    }

    public string? LastConnectDuration
    {
        get => GetValue(LastConnectDurationProperty);
        set => SetValue(LastConnectDurationProperty, value);
    }

    public string? LastHandshakeDuration
    {
        get => GetValue(LastHandshakeDurationProperty);
        set => SetValue(LastHandshakeDurationProperty, value);
    }

    public string? BridgeState
    {
        get => GetValue(BridgeStateProperty);
        set => SetValue(BridgeStateProperty, value);
    }

    public bool ForceVisible
    {
        get => GetValue(ForceVisibleProperty);
        set => SetValue(ForceVisibleProperty, value);
    }

    public bool IsBannerVisible => ForceVisible && Current is { Kind: not UserStatusKind.Idle };

    public string StatusTitle => string.IsNullOrWhiteSpace(Current.Title) ? DefaultTitleFor(Current.Kind) : Current.Title;

    public string StatusMessage => Current.Message ?? string.Empty;

    public bool ShowRetryCountdown => Current.NextRetryInSeconds.HasValue;

    public string RetryCountdownText
    {
        get
        {
            if (!ShowRetryCountdown)
            {
                return string.Empty;
            }

            return $"Next retry in {Current.NextRetryInSeconds!.Value}s";
        }
    }

    public bool ShowCopyDiagnosticsButton => IsBannerVisible && Current.CanCopyDiagnostics;

    public bool ShowCancelButton => IsBannerVisible && Current.CanCancel;

    public bool ShowDetailsExpander =>
        !string.IsNullOrWhiteSpace(EffectiveDetailsText) ||
        !string.IsNullOrWhiteSpace(NormalizeDetail(FailureCategory)) ||
        !string.IsNullOrWhiteSpace(NormalizeDetail(SessionCorrelationId)) ||
        !string.IsNullOrWhiteSpace(NormalizeDetail(LastConnectDuration)) ||
        !string.IsNullOrWhiteSpace(NormalizeDetail(LastHandshakeDuration)) ||
        !string.IsNullOrWhiteSpace(NormalizeDetail(BridgeState));

    public string EffectiveDetailsText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(DetailsText))
            {
                return DetailsText!;
            }

            if (!string.IsNullOrWhiteSpace(Current.CorrelationId))
            {
                return $"Correlation: {Current.CorrelationId}";
            }

            return string.Empty;
        }
    }

    public string FailureCategoryText => NormalizeDetail(FailureCategory);
    public string SessionCorrelationIdText => NormalizeDetail(SessionCorrelationId);
    public string LastConnectDurationText => NormalizeDetail(LastConnectDuration);
    public string LastHandshakeDurationText => NormalizeDetail(LastHandshakeDuration);
    public string BridgeStateText => NormalizeDetail(BridgeState);

    public bool ShowFailureCategoryDetail => !string.IsNullOrWhiteSpace(FailureCategoryText);
    public bool ShowSessionCorrelationIdDetail => !string.IsNullOrWhiteSpace(SessionCorrelationIdText);
    public bool ShowLastConnectDurationDetail => !string.IsNullOrWhiteSpace(LastConnectDurationText);
    public bool ShowLastHandshakeDurationDetail => !string.IsNullOrWhiteSpace(LastHandshakeDurationText);
    public bool ShowBridgeStateDetail => !string.IsNullOrWhiteSpace(BridgeStateText);
    public bool ShowRawDetailsText => !string.IsNullOrWhiteSpace(EffectiveDetailsText);

    private UserFacingStatus Current => Status ?? UserFacingStatus.IdleStatus;

    private static string DefaultTitleFor(UserStatusKind kind)
    {
        return kind switch
        {
            UserStatusKind.Connecting => "Connecting",
            UserStatusKind.Handshake => "Finalizing connection",
            UserStatusKind.Connected => "Connected",
            UserStatusKind.Reconnecting => "Reconnecting",
            UserStatusKind.Failed => "Connection problem",
            UserStatusKind.Degraded => "Status unavailable",
            _ => string.Empty
        };
    }

    private void RaiseComputedChanged()
    {
        OnPropertyChanged(nameof(IsBannerVisible));
        OnPropertyChanged(nameof(StatusTitle));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(ShowRetryCountdown));
        OnPropertyChanged(nameof(RetryCountdownText));
        OnPropertyChanged(nameof(ShowCopyDiagnosticsButton));
        OnPropertyChanged(nameof(ShowCancelButton));
        OnPropertyChanged(nameof(ShowDetailsExpander));
        OnPropertyChanged(nameof(EffectiveDetailsText));
        OnPropertyChanged(nameof(FailureCategoryText));
        OnPropertyChanged(nameof(SessionCorrelationIdText));
        OnPropertyChanged(nameof(LastConnectDurationText));
        OnPropertyChanged(nameof(LastHandshakeDurationText));
        OnPropertyChanged(nameof(BridgeStateText));
        OnPropertyChanged(nameof(ShowFailureCategoryDetail));
        OnPropertyChanged(nameof(ShowSessionCorrelationIdDetail));
        OnPropertyChanged(nameof(ShowLastConnectDurationDetail));
        OnPropertyChanged(nameof(ShowLastHandshakeDurationDetail));
        OnPropertyChanged(nameof(ShowBridgeStateDetail));
        OnPropertyChanged(nameof(ShowRawDetailsText));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == StatusProperty ||
            change.Property == DetailsTextProperty ||
            change.Property == ForceVisibleProperty ||
            change.Property == FailureCategoryProperty ||
            change.Property == SessionCorrelationIdProperty ||
            change.Property == LastConnectDurationProperty ||
            change.Property == LastHandshakeDurationProperty ||
            change.Property == BridgeStateProperty)
        {
            RaiseComputedChanged();
        }
    }

    private static string NormalizeDetail(string? value)
        => string.IsNullOrWhiteSpace(value) || string.Equals(value, "(none)", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : value!;
}
