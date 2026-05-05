using System;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
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

    public static readonly StyledProperty<Control?> OverlayContentProperty =
        AvaloniaProperty.Register<SessionShellView, Control?>(nameof(OverlayContent));

    public static readonly StyledProperty<string?> RoleTextProperty =
        AvaloniaProperty.Register<SessionShellView, string?>(nameof(RoleText));

    public static readonly StyledProperty<bool> TunaActiveProperty =
        AvaloniaProperty.Register<SessionShellView, bool>(nameof(TunaActive), false);

    public static readonly StyledProperty<string?> TunaStatusReasonProperty =
        AvaloniaProperty.Register<SessionShellView, string?>(nameof(TunaStatusReason), "inactive");

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

    public static readonly StyledProperty<Control?> HeaderAccessoryProperty =
        AvaloniaProperty.Register<SessionShellView, Control?>(nameof(HeaderAccessory));

    public static readonly StyledProperty<bool> ShowChatPaneRequestedProperty =
        AvaloniaProperty.Register<SessionShellView, bool>(nameof(ShowChatPaneRequested), false);

    public static readonly DirectProperty<SessionShellView, bool> EffectiveShowScreenShareActionProperty =
        AvaloniaProperty.RegisterDirect<SessionShellView, bool>(
            nameof(EffectiveShowScreenShareAction),
            o => o.EffectiveShowScreenShareAction);

    public static readonly DirectProperty<SessionShellView, string> ScreenShareButtonTextProperty =
        AvaloniaProperty.RegisterDirect<SessionShellView, string>(
            nameof(ScreenShareButtonText),
            o => o.ScreenShareButtonText);

    public static readonly DirectProperty<SessionShellView, bool> CanScreenShareActionProperty =
        AvaloniaProperty.RegisterDirect<SessionShellView, bool>(
            nameof(CanScreenShareAction),
            o => o.CanScreenShareAction);

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

    public static readonly DirectProperty<SessionShellView, HorizontalAlignment> MainPaneHorizontalAlignmentProperty =
        AvaloniaProperty.RegisterDirect<SessionShellView, HorizontalAlignment>(
            nameof(MainPaneHorizontalAlignment),
            o => o.MainPaneHorizontalAlignment);

    public static readonly DirectProperty<SessionShellView, double> MainPaneMaxWidthProperty =
        AvaloniaProperty.RegisterDirect<SessionShellView, double>(
            nameof(MainPaneMaxWidth),
            o => o.MainPaneMaxWidth);

    public static readonly DirectProperty<SessionShellView, double> MainPaneMinHeightProperty =
        AvaloniaProperty.RegisterDirect<SessionShellView, double>(
            nameof(MainPaneMinHeight),
            o => o.MainPaneMinHeight);

    public static readonly DirectProperty<SessionShellView, double> SideBySideChatPaneWidthProperty =
        AvaloniaProperty.RegisterDirect<SessionShellView, double>(
            nameof(SideBySideChatPaneWidth),
            o => o.SideBySideChatPaneWidth);

    private const double MainPaneContentMaxWidth = 1120d;
    private const double MainPaneCompactMinHeight = 0d;
    private const double MainPaneExpandedMinHeight = 420d;
    private const double DefaultSideBySideChatPaneWidth = 420d;
    private const double ScreenShareSideBySideChatPaneWidth = 320d;

    private bool showFixedShellLayout;
    private bool showFixedChatOnlyLayout;
    private bool showResponsiveWideChatOnlyLayout;
    private bool showResponsiveNarrowChatOnlyLayout;
    private bool showResponsiveWideLayout;
    private bool showResponsiveNarrowLayout;
    private bool isScreenSharePaneActive;
    private bool showScreenSharePane;
    private bool effectiveShowScreenShareAction;
    private bool canScreenShareAction;
    private bool showChatPane;
    private string screenShareButtonText = "Share screen";
    private INotifyPropertyChanged? observedDataContext;
    private ICommand? observedScreenShareCommand;
    private bool showMainPane = true;
    private bool screenSharePaneAutoActivated;
    private HorizontalAlignment mainPaneHorizontalAlignment = HorizontalAlignment.Center;
    private double mainPaneMaxWidth = MainPaneContentMaxWidth;
    private double mainPaneMinHeight = MainPaneCompactMinHeight;
    private double sideBySideChatPaneWidth = DefaultSideBySideChatPaneWidth;
    private readonly RelayCommand toggleScreenSharePaneCommand;

    public SessionShellView()
    {
        InitializeComponent();
        toggleScreenSharePaneCommand = new RelayCommand(ToggleScreenSharePane, CanToggleScreenSharePane);
        ToggleScreenSharePaneCommand = toggleScreenSharePaneCommand;
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

    public Control? OverlayContent
    {
        get => GetValue(OverlayContentProperty);
        set => SetValue(OverlayContentProperty, value);
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

    public Control? HeaderAccessory
    {
        get => GetValue(HeaderAccessoryProperty);
        set => SetValue(HeaderAccessoryProperty, value);
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

    public string ScreenShareButtonText
    {
        get => screenShareButtonText;
        private set => SetAndRaise(ScreenShareButtonTextProperty, ref screenShareButtonText, value);
    }

    public bool CanScreenShareAction
    {
        get => canScreenShareAction;
        private set => SetAndRaise(CanScreenShareActionProperty, ref canScreenShareAction, value);
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

    public HorizontalAlignment MainPaneHorizontalAlignment
    {
        get => mainPaneHorizontalAlignment;
        private set => SetAndRaise(MainPaneHorizontalAlignmentProperty, ref mainPaneHorizontalAlignment, value);
    }

    public double MainPaneMaxWidth
    {
        get => mainPaneMaxWidth;
        private set => SetAndRaise(MainPaneMaxWidthProperty, ref mainPaneMaxWidth, value);
    }

    public double MainPaneMinHeight
    {
        get => mainPaneMinHeight;
        private set => SetAndRaise(MainPaneMinHeightProperty, ref mainPaneMinHeight, value);
    }

    public double SideBySideChatPaneWidth
    {
        get => sideBySideChatPaneWidth;
        private set => SetAndRaise(SideBySideChatPaneWidthProperty, ref sideBySideChatPaneWidth, value);
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

        if (observedScreenShareCommand is not null)
        {
            observedScreenShareCommand.CanExecuteChanged -= OnObservedScreenShareCommandCanExecuteChanged;
        }

        observedDataContext = DataContext as INotifyPropertyChanged;
        if (observedDataContext is not null)
        {
            observedDataContext.PropertyChanged += OnObservedDataContextPropertyChanged;
        }

        observedScreenShareCommand = DataContext is null
            ? null
            : TryGetCommandPropertyValue(DataContext, "ToggleScreenSharePreviewCommand");
        if (observedScreenShareCommand is not null)
        {
            observedScreenShareCommand.CanExecuteChanged += OnObservedScreenShareCommandCanExecuteChanged;
        }

        UpdateScreenShareComputedProperties();
    }

    private void OnObservedDataContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) ||
            string.Equals(e.PropertyName, "HeaderStatusText", StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, "ConnectionState", StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, "ShowConnectedPanel", StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, "CanShowScreenShareAction", StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, "ScreenSharePreviewStatus", StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, "IsScreenSharingPreviewActive", StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, "ShowScreenSharePreviewFrame", StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, "ShowRemoteScreenShareFrame", StringComparison.Ordinal))
        {
            UpdateScreenShareComputedProperties();
        }
    }

    private void OnObservedScreenShareCommandCanExecuteChanged(object? sender, EventArgs e)
    {
        UpdateScreenShareComputedProperties();
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

        var fixedMainContent = fixedLayout && showMain ? MainContent : null;
        var responsiveWideMainContent = responsiveWide && showMain ? MainContent : null;
        var responsiveNarrowMainContent = responsiveNarrow && showMain ? MainContent : null;
        var fixedChatContent = fixedLayout && showMain && ShowChatPane ? ChatContent : null;
        var fixedChatOnlyContent = fixedLayout && !showMain && ShowChatPane ? ChatContent : null;
        var responsiveWideChatContent = responsiveWide && showMain && ShowChatPane ? ChatContent : null;
        var responsiveWideChatOnlyContent = responsiveWide && !showMain && ShowChatPane ? ChatContent : null;
        var responsiveNarrowChatContent = responsiveNarrow && showMain && ShowChatPane ? ChatContent : null;
        var responsiveNarrowChatOnlyContent = responsiveNarrow && !showMain && ShowChatPane ? ChatContent : null;

        ClearPresenterIfNeeded(FixedMainContentPresenter, fixedMainContent);
        ClearPresenterIfNeeded(ResponsiveWideMainContentPresenter, responsiveWideMainContent);
        ClearPresenterIfNeeded(ResponsiveNarrowMainContentPresenter, responsiveNarrowMainContent);
        ClearPresenterIfNeeded(FixedChatContentPresenter, fixedChatContent);
        ClearPresenterIfNeeded(FixedChatOnlyContentPresenter, fixedChatOnlyContent);
        ClearPresenterIfNeeded(ResponsiveWideChatContentPresenter, responsiveWideChatContent);
        ClearPresenterIfNeeded(ResponsiveWideChatOnlyContentPresenter, responsiveWideChatOnlyContent);
        ClearPresenterIfNeeded(ResponsiveNarrowChatContentPresenter, responsiveNarrowChatContent);
        ClearPresenterIfNeeded(ResponsiveNarrowChatOnlyContentPresenter, responsiveNarrowChatOnlyContent);

        SetPresenterContent(FixedMainContentPresenter, fixedMainContent);
        SetPresenterContent(ResponsiveWideMainContentPresenter, responsiveWideMainContent);
        SetPresenterContent(ResponsiveNarrowMainContentPresenter, responsiveNarrowMainContent);
        SetPresenterContent(FixedChatContentPresenter, fixedChatContent);
        SetPresenterContent(FixedChatOnlyContentPresenter, fixedChatOnlyContent);
        SetPresenterContent(ResponsiveWideChatContentPresenter, responsiveWideChatContent);
        SetPresenterContent(ResponsiveWideChatOnlyContentPresenter, responsiveWideChatOnlyContent);
        SetPresenterContent(ResponsiveNarrowChatContentPresenter, responsiveNarrowChatContent);
        SetPresenterContent(ResponsiveNarrowChatOnlyContentPresenter, responsiveNarrowChatOnlyContent);
    }

    private static void ClearPresenterIfNeeded(ContentPresenter? presenter, object? desiredContent)
    {
        if (presenter is null ||
            desiredContent is not null ||
            presenter.Content is null)
        {
            return;
        }

        presenter.Content = null;
    }

    private static void SetPresenterContent(ContentPresenter? presenter, object? content)
    {
        if (presenter is null || ReferenceEquals(presenter.Content, content))
        {
            return;
        }

        presenter.Content = content;
    }

    private void ToggleScreenSharePane()
    {
        if (!UseScreenShareScaffold || !EffectiveShowScreenShareAction)
        {
            return;
        }

        var dataContext = DataContext;
        var dataContextScreenShareCommand = dataContext is null
            ? null
            : TryGetCommandPropertyValue(dataContext, "ToggleScreenSharePreviewCommand");
        if (dataContextScreenShareCommand is not null)
        {
            var isActive = HasLocalScreenSharePreviewActive();
            if (!dataContextScreenShareCommand.CanExecute(null))
            {
                return;
            }

            dataContextScreenShareCommand.Execute(null);
            screenSharePaneAutoActivated = false;
            IsScreenSharePaneActive = !isActive;
            UpdateScreenShareComputedProperties();
            return;
        }

        screenSharePaneAutoActivated = false;
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
        var dataContext = DataContext;
        var canShowDataContextScreenShareAction = dataContext is not null &&
                                                 TryGetBoolPropertyValue(dataContext, "CanShowScreenShareAction") == true;
        var dataContextScreenShareCommand = dataContext is null
            ? null
            : TryGetCommandPropertyValue(dataContext, "ToggleScreenSharePreviewCommand");
        var hasLocalScreenSharePreviewActive = UseScreenShareScaffold && HasLocalScreenSharePreviewActive();
        var hasVisibleScreenShareFrame = UseScreenShareScaffold && HasVisibleScreenShareFrame();
        var hasActiveScreenShare = dataContextScreenShareCommand is not null
            ? hasLocalScreenSharePreviewActive || hasVisibleScreenShareFrame
            : UseScreenShareScaffold && HasActiveScreenShare();
        var showAction = UseScreenShareScaffold &&
                         ShowScreenShareAction &&
                         IsConnectedFromDataContext() &&
                         (dataContextScreenShareCommand is null || canShowDataContextScreenShareAction);
        EffectiveShowScreenShareAction = showAction;
        toggleScreenSharePaneCommand.NotifyCanExecuteChanged();
        CanScreenShareAction = toggleScreenSharePaneCommand.CanExecute(null);
        ScreenShareCommand = ToggleScreenSharePaneCommand;
        ScreenShareButtonText = dataContextScreenShareCommand is not null
            ? (hasLocalScreenSharePreviewActive ? "Stop sharing" : "Share screen")
            : hasActiveScreenShare || ShowScreenSharePane || hasVisibleScreenShareFrame || IsScreenSharePaneActive
                ? "Stop sharing"
                : "Share screen";

        if (hasActiveScreenShare && !IsScreenSharePaneActive)
        {
            IsScreenSharePaneActive = true;
            screenSharePaneAutoActivated = true;
        }
        else if (!hasActiveScreenShare && screenSharePaneAutoActivated)
        {
            IsScreenSharePaneActive = false;
            screenSharePaneAutoActivated = false;
        }

        if (!showAction && !hasActiveScreenShare && IsScreenSharePaneActive)
        {
            IsScreenSharePaneActive = false;
            screenSharePaneAutoActivated = false;
        }

        ShowChatPane = HasVisibleChatPane();
        ShowScreenSharePane = UseScreenShareScaffold && IsScreenSharePaneActive;
        UpdateMainPaneSizing(ShowScreenSharePane);
        ScreenShareButtonText = dataContextScreenShareCommand is not null
            ? (hasLocalScreenSharePreviewActive ? "Stop sharing" : "Share screen")
            : hasActiveScreenShare || ShowScreenSharePane || hasVisibleScreenShareFrame || IsScreenSharePaneActive
                ? "Stop sharing"
                : "Share screen";
        ShowMainPane = !ShowChatPane || ShowScreenSharePane || hasVisibleScreenShareFrame;
        UpdateLayoutVisibility();
        // ShowConnectedPanel can arrive after the header flips to Connected. Refresh presenters
        // on every recompute so the chat pane is attached even when the main-pane mode is unchanged.
        UpdateContentPresenters();
        UpdatePlaceholderVisibility();
        Dispatcher.UIThread.Post(() =>
        {
            UpdateLayoutVisibility();
            UpdateContentPresenters();
            UpdatePlaceholderVisibility();
        }, DispatcherPriority.Loaded);
    }

    private bool CanToggleScreenSharePane()
    {
        if (!UseScreenShareScaffold || !EffectiveShowScreenShareAction)
        {
            return false;
        }

        var dataContext = DataContext;
        var dataContextScreenShareCommand = dataContext is null
            ? null
            : TryGetCommandPropertyValue(dataContext, "ToggleScreenSharePreviewCommand");

        return dataContextScreenShareCommand?.CanExecute(null) ?? true;
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

    private void UpdateMainPaneSizing(bool stretchForScreenShare)
    {
        MainPaneHorizontalAlignment = stretchForScreenShare
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Center;
        MainPaneMaxWidth = stretchForScreenShare
            ? double.PositiveInfinity
            : MainPaneContentMaxWidth;
        MainPaneMinHeight = stretchForScreenShare || ShowChatPane
            ? MainPaneExpandedMinHeight
            : MainPaneCompactMinHeight;
        SideBySideChatPaneWidth = stretchForScreenShare
            ? ScreenShareSideBySideChatPaneWidth
            : DefaultSideBySideChatPaneWidth;
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

    private bool HasActiveScreenShare()
    {
        var dataContext = DataContext;
        if (dataContext is null)
        {
            return false;
        }

        return TryGetBoolPropertyValue(dataContext, "IsScreenSharingPreviewActive") == true ||
               HasVisibleScreenShareFrame();
    }

    private bool HasLocalScreenSharePreviewActive()
    {
        var dataContext = DataContext;
        if (dataContext is null)
        {
            return false;
        }

        return TryGetBoolPropertyValue(dataContext, "IsScreenSharingPreviewActive") == true;
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

    private static ICommand? TryGetCommandPropertyValue(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName);
        if (property is null || !typeof(ICommand).IsAssignableFrom(property.PropertyType))
        {
            return null;
        }

        return property.GetValue(instance) as ICommand;
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
        var showPlaceholder = MainContent is null && !ShowScreenSharePane;
        ContentPlaceholderText.IsVisible = showPlaceholder;
        if (ResponsiveContentPlaceholderText is not null)
        {
            ResponsiveContentPlaceholderText.IsVisible = showPlaceholder;
        }
        if (NarrowContentPlaceholderText is not null)
        {
            NarrowContentPlaceholderText.IsVisible = showPlaceholder;
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
