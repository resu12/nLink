using System;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using NLink.App.Configuration;

namespace NLink.App.Views;

public partial class SessionShellView : UserControl
{
    public static readonly StyledProperty<Control?> MainContentProperty =
        AvaloniaProperty.Register<SessionShellView, Control?>(nameof(MainContent));

    public static readonly StyledProperty<Control?> ChatContentProperty =
        AvaloniaProperty.Register<SessionShellView, Control?>(nameof(ChatContent));

    public static readonly StyledProperty<string?> RoleTextProperty =
        AvaloniaProperty.Register<SessionShellView, string?>(nameof(RoleText));

    public static readonly DirectProperty<SessionShellView, bool> IsScreenSharePaneActiveProperty =
        AvaloniaProperty.RegisterDirect<SessionShellView, bool>(
            nameof(IsScreenSharePaneActive),
            o => o.IsScreenSharePaneActive);

    public static readonly DirectProperty<SessionShellView, bool> ShowScreenSharePaneProperty =
        AvaloniaProperty.RegisterDirect<SessionShellView, bool>(
            nameof(ShowScreenSharePane),
            o => o.ShowScreenSharePane);

    public static readonly StyledProperty<bool> ShowScreenShareActionProperty =
        AvaloniaProperty.Register<SessionShellView, bool>(nameof(ShowScreenShareAction), false);

    public static readonly StyledProperty<ICommand?> ScreenShareCommandProperty =
        AvaloniaProperty.Register<SessionShellView, ICommand?>(nameof(ScreenShareCommand));

    public static readonly StyledProperty<bool> ShowChatPaneRequestedProperty =
        AvaloniaProperty.Register<SessionShellView, bool>(nameof(ShowChatPaneRequested), false);

    public static readonly DirectProperty<SessionShellView, bool> EffectiveShowScreenShareActionProperty =
        AvaloniaProperty.RegisterDirect<SessionShellView, bool>(
            nameof(EffectiveShowScreenShareAction),
            o => o.EffectiveShowScreenShareAction);

    public static readonly DirectProperty<SessionShellView, bool> ShowChatPaneProperty =
        AvaloniaProperty.RegisterDirect<SessionShellView, bool>(
            nameof(ShowChatPane),
            o => o.ShowChatPane);

    public static readonly StyledProperty<bool> IsNarrowProperty =
        AvaloniaProperty.Register<SessionShellView, bool>(nameof(IsNarrow));

    public static readonly DirectProperty<SessionShellView, bool> ShowResponsiveWideLayoutProperty =
        AvaloniaProperty.RegisterDirect<SessionShellView, bool>(
            nameof(ShowResponsiveWideLayout),
            o => o.ShowResponsiveWideLayout);

    public static readonly DirectProperty<SessionShellView, bool> ShowResponsiveNarrowLayoutProperty =
        AvaloniaProperty.RegisterDirect<SessionShellView, bool>(
            nameof(ShowResponsiveNarrowLayout),
            o => o.ShowResponsiveNarrowLayout);

    public static readonly DirectProperty<SessionShellView, bool> ShowFixedShellLayoutProperty =
        AvaloniaProperty.RegisterDirect<SessionShellView, bool>(
            nameof(ShowFixedShellLayout),
            o => o.ShowFixedShellLayout);

    public static readonly DirectProperty<SessionShellView, bool> ShowFixedChatOnlyLayoutProperty =
        AvaloniaProperty.RegisterDirect<SessionShellView, bool>(
            nameof(ShowFixedChatOnlyLayout),
            o => o.ShowFixedChatOnlyLayout);

    public static readonly DirectProperty<SessionShellView, bool> ShowResponsiveWideChatOnlyLayoutProperty =
        AvaloniaProperty.RegisterDirect<SessionShellView, bool>(
            nameof(ShowResponsiveWideChatOnlyLayout),
            o => o.ShowResponsiveWideChatOnlyLayout);

    public static readonly DirectProperty<SessionShellView, bool> ShowResponsiveNarrowChatOnlyLayoutProperty =
        AvaloniaProperty.RegisterDirect<SessionShellView, bool>(
            nameof(ShowResponsiveNarrowChatOnlyLayout),
            o => o.ShowResponsiveNarrowChatOnlyLayout);

    private bool showFixedShellLayout;
    private bool showFixedChatOnlyLayout;
    private bool showResponsiveWideChatOnlyLayout;
    private bool showResponsiveNarrowChatOnlyLayout;
    private bool showResponsiveWideLayout;
    private bool showResponsiveNarrowLayout;
    private bool isScreenSharePaneActive;
    private bool showScreenSharePane;
    private bool effectiveShowScreenShareAction;
    private bool showChatPane;
    private INotifyPropertyChanged? observedDataContext;
    private bool showMainPane = true;

    public SessionShellView()
    {
        InitializeComponent();
        ToggleScreenSharePaneCommand = new RelayCommand(ToggleScreenSharePane);
        DataContextChanged += OnDataContextChanged;
        PropertyChanged += OnSessionShellViewPropertyChanged;
        OnDataContextChanged(this, EventArgs.Empty);
        UpdateContentPresenters();
        UpdatePlaceholderVisibility();
        UpdateResponsiveLayoutVisibility();
        UpdateIsNarrow();
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

    public Control? MainContent
    {
        get => GetValue(MainContentProperty);
        set => SetValue(MainContentProperty, value);
    }

    public Control? ChatContent
    {
        get => GetValue(ChatContentProperty);
        set => SetValue(ChatContentProperty, value);
    }

    public string? RoleText
    {
        get => GetValue(RoleTextProperty);
        set => SetValue(RoleTextProperty, value);
    }

    public bool IsScreenSharePaneActive
    {
        get => isScreenSharePaneActive;
        private set => SetAndRaise(IsScreenSharePaneActiveProperty, ref isScreenSharePaneActive, value);
    }

    public bool ShowScreenSharePane
    {
        get => showScreenSharePane;
        private set => SetAndRaise(ShowScreenSharePaneProperty, ref showScreenSharePane, value);
    }

    public bool ShowScreenShareAction
    {
        get => GetValue(ShowScreenShareActionProperty);
        set => SetValue(ShowScreenShareActionProperty, value);
    }

    public ICommand? ScreenShareCommand
    {
        get => GetValue(ScreenShareCommandProperty);
        private set => SetValue(ScreenShareCommandProperty, value);
    }

    public bool ShowChatPaneRequested
    {
        get => GetValue(ShowChatPaneRequestedProperty);
        set => SetValue(ShowChatPaneRequestedProperty, value);
    }

    public bool EffectiveShowScreenShareAction
    {
        get => effectiveShowScreenShareAction;
        private set => SetAndRaise(EffectiveShowScreenShareActionProperty, ref effectiveShowScreenShareAction, value);
    }

    public bool ShowChatPane
    {
        get => showChatPane;
        private set => SetAndRaise(ShowChatPaneProperty, ref showChatPane, value);
    }

    public bool IsNarrow
    {
        get => GetValue(IsNarrowProperty);
        set => SetValue(IsNarrowProperty, value);
    }

    public bool UseSessionHeader => FeatureFlags.EnableSessionHeader;

    public bool UseScreenShareScaffold => IsScreenShareScaffoldEnabled();

    public bool UseResponsiveLayout => FeatureFlags.EnableResponsiveLayout;

    public bool UseFixedChatWidth => !FeatureFlags.EnableResponsiveLayout;

    public ICommand ToggleScreenSharePaneCommand { get; }

    public bool ShowResponsiveWideLayout
    {
        get => showResponsiveWideLayout;
        private set => SetAndRaise(ShowResponsiveWideLayoutProperty, ref showResponsiveWideLayout, value);
    }

    public bool ShowResponsiveNarrowLayout
    {
        get => showResponsiveNarrowLayout;
        private set => SetAndRaise(ShowResponsiveNarrowLayoutProperty, ref showResponsiveNarrowLayout, value);
    }

    public bool ShowFixedShellLayout
    {
        get => showFixedShellLayout;
        private set => SetAndRaise(ShowFixedShellLayoutProperty, ref showFixedShellLayout, value);
    }

    public bool ShowFixedChatOnlyLayout
    {
        get => showFixedChatOnlyLayout;
        private set => SetAndRaise(ShowFixedChatOnlyLayoutProperty, ref showFixedChatOnlyLayout, value);
    }

    public bool ShowResponsiveWideChatOnlyLayout
    {
        get => showResponsiveWideChatOnlyLayout;
        private set => SetAndRaise(ShowResponsiveWideChatOnlyLayoutProperty, ref showResponsiveWideChatOnlyLayout, value);
    }

    public bool ShowResponsiveNarrowChatOnlyLayout
    {
        get => showResponsiveNarrowChatOnlyLayout;
        private set => SetAndRaise(ShowResponsiveNarrowChatOnlyLayoutProperty, ref showResponsiveNarrowChatOnlyLayout, value);
    }

    private bool ShowMainPane
    {
        get => showMainPane;
        set
        {
            if (showMainPane == value)
            {
                return;
            }

            showMainPane = value;
            UpdateLayoutVisibility();
            UpdateContentPresenters();
        }
    }

    private void OnSessionShellViewPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == MainContentProperty || e.Property == ChatContentProperty)
        {
            UpdateContentPresenters();
            UpdatePlaceholderVisibility();
            return;
        }

        if (e.Property == ShowScreenShareActionProperty || e.Property == ShowChatPaneRequestedProperty)
        {
            UpdateScreenShareComputedProperties();
            return;
        }

        if (e.Property == BoundsProperty)
        {
            UpdateIsNarrow();
            return;
        }

        if (e.Property == IsNarrowProperty)
        {
            UpdateResponsiveLayoutVisibility();
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (observedDataContext is not null)
        {
            observedDataContext.PropertyChanged -= OnObservedDataContextPropertyChanged;
        }

        observedDataContext = DataContext as INotifyPropertyChanged;
        if (observedDataContext is not null)
        {
            observedDataContext.PropertyChanged += OnObservedDataContextPropertyChanged;
        }

        UpdateScreenShareComputedProperties();
    }

    private void OnObservedDataContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) ||
            string.Equals(e.PropertyName, "HeaderStatusText", StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, "ConnectionState", StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, "ShowConnectedPanel", StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, "ShowScreenSharePreviewFrame", StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, "ShowRemoteScreenShareFrame", StringComparison.Ordinal))
        {
            UpdateScreenShareComputedProperties();
        }
    }

    private void UpdateIsNarrow()
    {
        var nextIsNarrow = Bounds.Width > 0 &&
                           Bounds.Width < LayoutConstants.NarrowThresholdWidth;
        if (nextIsNarrow == IsNarrow)
        {
            return;
        }

        IsNarrow = nextIsNarrow;
    }

    private void UpdateResponsiveLayoutVisibility()
    {
        ShowResponsiveWideLayout = FeatureFlags.EnableResponsiveLayout && !IsNarrow;
        ShowResponsiveNarrowLayout = FeatureFlags.EnableResponsiveLayout && IsNarrow;
        UpdateLayoutVisibility();
        UpdateContentPresenters();
    }

    private void UpdateContentPresenters()
    {
        var fixedLayout = !FeatureFlags.EnableResponsiveLayout;
        var responsiveWide = FeatureFlags.EnableResponsiveLayout && !IsNarrow;
        var responsiveNarrow = FeatureFlags.EnableResponsiveLayout && IsNarrow;
        var showMain = ShowMainPane;

        if (FixedMainContentPresenter is not null)
        {
            FixedMainContentPresenter.Content = null;
        }

        if (ResponsiveWideMainContentPresenter is not null)
        {
            ResponsiveWideMainContentPresenter.Content = null;
        }

        if (ResponsiveNarrowMainContentPresenter is not null)
        {
            ResponsiveNarrowMainContentPresenter.Content = null;
        }

        if (FixedChatContentPresenter is not null)
        {
            FixedChatContentPresenter.Content = null;
        }

        if (FixedChatOnlyContentPresenter is not null)
        {
            FixedChatOnlyContentPresenter.Content = null;
        }

        if (ResponsiveWideChatContentPresenter is not null)
        {
            ResponsiveWideChatContentPresenter.Content = null;
        }

        if (ResponsiveWideChatOnlyContentPresenter is not null)
        {
            ResponsiveWideChatOnlyContentPresenter.Content = null;
        }

        if (ResponsiveNarrowChatContentPresenter is not null)
        {
            ResponsiveNarrowChatContentPresenter.Content = null;
        }

        if (ResponsiveNarrowChatOnlyContentPresenter is not null)
        {
            ResponsiveNarrowChatOnlyContentPresenter.Content = null;
        }

        if (FixedMainContentPresenter is not null)
        {
            FixedMainContentPresenter.Content = fixedLayout && showMain ? MainContent : null;
        }

        if (ResponsiveWideMainContentPresenter is not null)
        {
            ResponsiveWideMainContentPresenter.Content = responsiveWide && showMain ? MainContent : null;
        }

        if (ResponsiveNarrowMainContentPresenter is not null)
        {
            ResponsiveNarrowMainContentPresenter.Content = responsiveNarrow && showMain ? MainContent : null;
        }

        if (FixedChatContentPresenter is not null)
        {
            FixedChatContentPresenter.Content = fixedLayout && showMain && ShowChatPane ? ChatContent : null;
        }

        if (FixedChatOnlyContentPresenter is not null)
        {
            FixedChatOnlyContentPresenter.Content = fixedLayout && !showMain && ShowChatPane ? ChatContent : null;
        }

        if (ResponsiveWideChatContentPresenter is not null)
        {
            ResponsiveWideChatContentPresenter.Content = responsiveWide && showMain && ShowChatPane ? ChatContent : null;
        }

        if (ResponsiveWideChatOnlyContentPresenter is not null)
        {
            ResponsiveWideChatOnlyContentPresenter.Content = responsiveWide && !showMain && ShowChatPane ? ChatContent : null;
        }

        if (ResponsiveNarrowChatContentPresenter is not null)
        {
            ResponsiveNarrowChatContentPresenter.Content = responsiveNarrow && showMain && ShowChatPane ? ChatContent : null;
        }

        if (ResponsiveNarrowChatOnlyContentPresenter is not null)
        {
            ResponsiveNarrowChatOnlyContentPresenter.Content = responsiveNarrow && !showMain && ShowChatPane ? ChatContent : null;
        }
    }

    private void ToggleScreenSharePane()
    {
        if (!UseScreenShareScaffold || !EffectiveShowScreenShareAction)
        {
            return;
        }

        IsScreenSharePaneActive = !IsScreenSharePaneActive;
        UpdateScreenShareComputedProperties();
    }

    private bool IsConnectedFromDataContext()
    {
        var dataContext = DataContext;
        if (dataContext is null)
        {
            return false;
        }

        var headerStatusText = TryGetStringPropertyValue(dataContext, "HeaderStatusText");
        if (string.Equals(headerStatusText, "Connected", StringComparison.Ordinal))
        {
            return true;
        }

        var connectionState = TryGetStringPropertyValue(dataContext, "ConnectionState");
        return string.Equals(connectionState, "Connected", StringComparison.Ordinal);
    }

    private void UpdateScreenShareComputedProperties()
    {
        var showAction = UseScreenShareScaffold && ShowScreenShareAction && IsConnectedFromDataContext();
        EffectiveShowScreenShareAction = showAction;
        ScreenShareCommand = ToggleScreenSharePaneCommand;

        if (!showAction && IsScreenSharePaneActive)
        {
            IsScreenSharePaneActive = false;
        }

        ShowChatPane = HasVisibleChatPane();
        ShowScreenSharePane = UseScreenShareScaffold && IsScreenSharePaneActive;
        ShowMainPane = !ShowChatPane || ShowScreenSharePane || HasVisibleScreenShareFrame();
        UpdateLayoutVisibility();
        // ShowConnectedPanel can arrive after the header flips to Connected. Refresh presenters
        // on every recompute so the chat pane is attached even when the main-pane mode is unchanged.
        UpdateContentPresenters();
        Dispatcher.UIThread.Post(() =>
        {
            UpdateLayoutVisibility();
            UpdateContentPresenters();
        }, DispatcherPriority.Loaded);
    }

    private void UpdateLayoutVisibility()
    {
        var fixedLayout = !FeatureFlags.EnableResponsiveLayout;
        var responsiveWide = FeatureFlags.EnableResponsiveLayout && !IsNarrow;
        var responsiveNarrow = FeatureFlags.EnableResponsiveLayout && IsNarrow;
        ShowFixedShellLayout = fixedLayout && ShowMainPane;
        ShowFixedChatOnlyLayout = fixedLayout && !ShowMainPane && ShowChatPane;
        ShowResponsiveWideLayout = responsiveWide && ShowMainPane;
        ShowResponsiveWideChatOnlyLayout = responsiveWide && !ShowMainPane && ShowChatPane;
        ShowResponsiveNarrowLayout = responsiveNarrow && ShowMainPane;
        ShowResponsiveNarrowChatOnlyLayout = responsiveNarrow && !ShowMainPane && ShowChatPane;
    }

    private bool HasVisibleScreenShareFrame()
    {
        var dataContext = DataContext;
        if (dataContext is null)
        {
            return false;
        }

        return TryGetBoolPropertyValue(dataContext, "ShowScreenSharePreviewFrame") == true ||
               TryGetBoolPropertyValue(dataContext, "ShowRemoteScreenShareFrame") == true;
    }

    private bool HasVisibleChatPane()
    {
        return ShowChatPaneRequested || IsConnectedFromDataContext();
    }

    private static string? TryGetStringPropertyValue(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName);
        if (property is null || property.PropertyType != typeof(string))
        {
            return null;
        }

        return property.GetValue(instance) as string;
    }

    private static bool? TryGetBoolPropertyValue(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName);
        if (property is null || property.PropertyType != typeof(bool))
        {
            return null;
        }

        return property.GetValue(instance) as bool?;
    }

    private static bool IsScreenShareScaffoldEnabled()
    {
        var raw = Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return FeatureFlags.EnableScreenShareScaffold;
        }

        return raw.Trim() switch
        {
            "1" => true,
            "true" => true,
            "TRUE" => true,
            "yes" => true,
            "YES" => true,
            "on" => true,
            "ON" => true,
            "0" => false,
            "false" => false,
            "FALSE" => false,
            "no" => false,
            "NO" => false,
            "off" => false,
            "OFF" => false,
            _ => FeatureFlags.EnableScreenShareScaffold,
        };
    }

    private void UpdatePlaceholderVisibility()
    {
        if (ContentPlaceholderText is null)
        {
            return;
        }

#if DEBUG
        ContentPlaceholderText.IsVisible = MainContent is null;
        if (ResponsiveContentPlaceholderText is not null)
        {
            ResponsiveContentPlaceholderText.IsVisible = MainContent is null;
        }
        if (NarrowContentPlaceholderText is not null)
        {
            NarrowContentPlaceholderText.IsVisible = MainContent is null;
        }
#else
        ContentPlaceholderText.IsVisible = false;
        if (ResponsiveContentPlaceholderText is not null)
        {
            ResponsiveContentPlaceholderText.IsVisible = false;
        }
        if (NarrowContentPlaceholderText is not null)
        {
            NarrowContentPlaceholderText.IsVisible = false;
        }
#endif
    }
}
