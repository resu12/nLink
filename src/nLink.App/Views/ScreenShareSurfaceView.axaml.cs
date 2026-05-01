using System;
using System.Reflection;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NLink.App.Services.RemoteControl;
using NLink.Core.RemoteControl;
using NLink.Core.Logging;

namespace NLink.App.Views;

public partial class ScreenShareSurfaceView : UserControl
{
    private const int DefaultMouseMoveRateHz = 90;
    private const int MinMouseMoveRateHz = 60;
    private const int MaxMouseMoveRateHz = 120;
    internal const double CursorOverlayPointerWidthDip = 10d;
    internal const double CursorOverlayPointerHeightDip = 14d;
    internal const double CursorOverlayPointerStrokeThicknessDip = 0.8d;
    private static readonly PropertyInfo? PhysicalKeyProperty = typeof(KeyEventArgs).GetProperty("PhysicalKey");
    private static readonly PropertyInfo? IsRepeatProperty = typeof(KeyEventArgs).GetProperty("IsRepeat");

    public static readonly StyledProperty<Bitmap?> FrameProperty =
        AvaloniaProperty.Register<ScreenShareSurfaceView, Bitmap?>(nameof(Frame));

    public static readonly StyledProperty<bool> CaptureEnabledProperty =
        AvaloniaProperty.Register<ScreenShareSurfaceView, bool>(nameof(CaptureEnabled), false);

    public static readonly StyledProperty<bool> KeyboardCaptureEnabledProperty =
        AvaloniaProperty.Register<ScreenShareSurfaceView, bool>(nameof(KeyboardCaptureEnabled), false);

    public static readonly StyledProperty<int> MouseMoveRateHzProperty =
        AvaloniaProperty.Register<ScreenShareSurfaceView, int>(nameof(MouseMoveRateHz), DefaultMouseMoveRateHz);

    public static readonly StyledProperty<string> SurfaceRoleProperty =
        AvaloniaProperty.Register<ScreenShareSurfaceView, string>(nameof(SurfaceRole), "unknown");

    public static readonly StyledProperty<bool> CursorOverlayVisibleProperty =
        AvaloniaProperty.Register<ScreenShareSurfaceView, bool>(nameof(CursorOverlayVisible), false);

    public static readonly StyledProperty<double> CursorOverlayNxProperty =
        AvaloniaProperty.Register<ScreenShareSurfaceView, double>(nameof(CursorOverlayNx), 0d);

    public static readonly StyledProperty<double> CursorOverlayNyProperty =
        AvaloniaProperty.Register<ScreenShareSurfaceView, double>(nameof(CursorOverlayNy), 0d);

    private readonly DispatcherTimer mouseMoveThrottleTimer;
    private readonly RemoteControlHeldState heldState = new();
    private readonly Image frameImage;
    private readonly Canvas cursorOverlayLayer;
    private readonly Path cursorOverlayPointer;
    private bool hasPendingMouseMove;
    private double pendingMouseMoveNx;
    private double pendingMouseMoveNy;
    private BitmapInterpolationMode? currentInterpolationMode;
    private BitmapInterpolationMode? lastLoggedInterpolationMode;
    private int lastLoggedFrameWidth = -1;
    private int lastLoggedFrameHeight = -1;
    private int lastLoggedViewportWidth = -1;
    private int lastLoggedViewportHeight = -1;
    private double lastLoggedRenderScaling = double.NaN;
    private double lastKnownRenderScaling = 1d;
    private long lastInterpolationLogTick;
    private bool? lastLoggedCaptureEnabled;
    private bool? lastLoggedKeyboardCaptureEnabled;
    private bool? lastLoggedHitTestVisible;
    private bool? lastLoggedFocusable;
    private string? lastLoggedSurfaceRole;
    private static readonly TimeSpan InterpolationLogInterval = TimeSpan.FromSeconds(2);
#if DEBUG
    private int debugMouseMoveSentPerSecond;
    private int debugMouseMoveSentInWindow;
    private long debugMouseMoveWindowStartTickMs;
#endif

    static ScreenShareSurfaceView()
    {
        FrameProperty.Changed.AddClassHandler<ScreenShareSurfaceView>(
            static (view, _) => view.OnFrameChanged());
        CaptureEnabledProperty.Changed.AddClassHandler<ScreenShareSurfaceView>(
            static (view, _) => view.OnCaptureEnabledChanged());
        KeyboardCaptureEnabledProperty.Changed.AddClassHandler<ScreenShareSurfaceView>(
            static (view, _) => view.OnCaptureEnabledChanged());
        MouseMoveRateHzProperty.Changed.AddClassHandler<ScreenShareSurfaceView>(
            static (view, _) => view.OnMouseMoveRateHzChanged());
        SurfaceRoleProperty.Changed.AddClassHandler<ScreenShareSurfaceView>(
            static (view, _) =>
            {
                view.UpdateFrameInterpolationMode();
                view.LogCaptureStateIfChanged();
            });
        CursorOverlayVisibleProperty.Changed.AddClassHandler<ScreenShareSurfaceView>(
            static (view, _) => view.UpdateCursorOverlayPosition());
        CursorOverlayNxProperty.Changed.AddClassHandler<ScreenShareSurfaceView>(
            static (view, _) => view.UpdateCursorOverlayPosition());
        CursorOverlayNyProperty.Changed.AddClassHandler<ScreenShareSurfaceView>(
            static (view, _) => view.UpdateCursorOverlayPosition());
        BoundsProperty.Changed.AddClassHandler<ScreenShareSurfaceView>(
            static (view, _) =>
            {
                view.UpdateFrameInterpolationMode();
                view.UpdateCursorOverlayPosition();
            });
    }

    public ScreenShareSurfaceView()
    {
        InitializeComponent();
        frameImage = this.FindControl<Image>("FrameImage")
            ?? throw new InvalidOperationException("FrameImage was not found.");
        cursorOverlayLayer = this.FindControl<Canvas>("CursorOverlayLayer")
            ?? throw new InvalidOperationException("CursorOverlayLayer was not found.");
        cursorOverlayPointer = this.FindControl<Path>("CursorOverlayPointer")
            ?? throw new InvalidOperationException("CursorOverlayPointer was not found.");
        cursorOverlayPointer.Width = CursorOverlayPointerWidthDip;
        cursorOverlayPointer.Height = CursorOverlayPointerHeightDip;
        cursorOverlayPointer.StrokeThickness = CursorOverlayPointerStrokeThicknessDip;
        LocalOperationalLog.Info(
            "ScreenShare",
            $"event=screenshare_cursor_overlay_visual_configured; width_dip={CursorOverlayPointerWidthDip}; height_dip={CursorOverlayPointerHeightDip}; stroke_thickness_dip={CursorOverlayPointerStrokeThicknessDip}; hot_spot=top_left");
        mouseMoveThrottleTimer = new DispatcherTimer
        {
            Interval = GetMouseMoveThrottleInterval(MouseMoveRateHz),
        };
        mouseMoveThrottleTimer.Tick += OnMouseMoveThrottleTick;
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Tunnel, handledEventsToo: true);
        PointerCaptureLost += OnPointerCaptureLost;
        LostFocus += OnLostFocus;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        OnCaptureEnabledChanged();
    }

    public Bitmap? Frame
    {
        get => GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    public bool CaptureEnabled
    {
        get => GetValue(CaptureEnabledProperty);
        set => SetValue(CaptureEnabledProperty, value);
    }

    public bool KeyboardCaptureEnabled
    {
        get => GetValue(KeyboardCaptureEnabledProperty);
        set => SetValue(KeyboardCaptureEnabledProperty, value);
    }

    public int MouseMoveRateHz
    {
        get => GetValue(MouseMoveRateHzProperty);
        set => SetValue(MouseMoveRateHzProperty, value);
    }

    public string SurfaceRole
    {
        get => GetValue(SurfaceRoleProperty);
        set => SetValue(SurfaceRoleProperty, value);
    }

    public bool CursorOverlayVisible
    {
        get => GetValue(CursorOverlayVisibleProperty);
        set => SetValue(CursorOverlayVisibleProperty, value);
    }

    public double CursorOverlayNx
    {
        get => GetValue(CursorOverlayNxProperty);
        set => SetValue(CursorOverlayNxProperty, value);
    }

    public double CursorOverlayNy
    {
        get => GetValue(CursorOverlayNyProperty);
        set => SetValue(CursorOverlayNyProperty, value);
    }

    public event EventHandler<RemoteControlInputProducedEventArgs>? RemoteControlInputProduced;
    public event EventHandler<RemoteControlHeldStateChangedEventArgs>? RemoteControlHeldStateChanged;
    public event EventHandler? ControlModeExitRequested;

    private void OnCaptureEnabledChanged()
    {
        IsHitTestVisible = CaptureEnabled;
        Focusable = KeyboardCaptureEnabled;
        RemoteControlDebugDiagnostics.SetHelperControlMode(CaptureEnabled && KeyboardCaptureEnabled);
        LogCaptureStateIfChanged();
        if (KeyboardCaptureEnabled)
        {
            Dispatcher.UIThread.Post(
                () => TryFocusForKeyboardCapture("keyboard_capture_enabled"),
                DispatcherPriority.Input);
        }

        if (CaptureEnabled)
        {
            return;
        }

        ClearHeldStateAndRequestReleaseAll();
        ResetMouseMovePumpState();
    }

    private void TryFocusForKeyboardCapture(string reason)
    {
        if (!KeyboardCaptureEnabled || !Focusable || IsFocused)
        {
            return;
        }

        var focused = Focus(NavigationMethod.Pointer);
        LocalOperationalLog.Info(
            "ScreenShareUi",
            $"event=remote_control_surface_focus_requested; role={SanitizeRole(SurfaceRole)}; reason={SanitizeRole(reason)}; focused={(focused ? 1 : 0)}; keyboard_capture_enabled={(KeyboardCaptureEnabled ? 1 : 0)}; focusable={(Focusable ? 1 : 0)}");
    }

    private void LogCaptureStateIfChanged()
    {
        if (lastLoggedCaptureEnabled == CaptureEnabled &&
            lastLoggedKeyboardCaptureEnabled == KeyboardCaptureEnabled &&
            lastLoggedHitTestVisible == IsHitTestVisible &&
            lastLoggedFocusable == Focusable &&
            string.Equals(lastLoggedSurfaceRole, SurfaceRole, StringComparison.Ordinal))
        {
            return;
        }

        lastLoggedCaptureEnabled = CaptureEnabled;
        lastLoggedKeyboardCaptureEnabled = KeyboardCaptureEnabled;
        lastLoggedHitTestVisible = IsHitTestVisible;
        lastLoggedFocusable = Focusable;
        lastLoggedSurfaceRole = SurfaceRole;
        LocalOperationalLog.Info(
            "ScreenShareUi",
            $"event=remote_control_surface_capture_state; role={SanitizeRole(SurfaceRole)}; capture_enabled={(CaptureEnabled ? 1 : 0)}; keyboard_capture_enabled={(KeyboardCaptureEnabled ? 1 : 0)}; hit_test_visible={(IsHitTestVisible ? 1 : 0)}; focusable={(Focusable ? 1 : 0)}");
    }

    private void OnFrameChanged()
    {
        UpdateFrameInterpolationMode();

        if (Frame is null)
        {
            RemoteControlDebugDiagnostics.SetHelperFrameSize(null);
            UpdateCursorOverlayPosition();
            return;
        }

        var frameWidth = Frame.PixelSize.Width;
        var frameHeight = Frame.PixelSize.Height;
        if (frameWidth <= 0 || frameHeight <= 0)
        {
            RemoteControlDebugDiagnostics.SetHelperFrameSize(null);
            UpdateCursorOverlayPosition();
            return;
        }

        RemoteControlDebugDiagnostics.SetHelperFrameSize(new RemoteControlSizePx(frameWidth, frameHeight));
        UpdateCursorOverlayPosition();
    }

    internal static bool TryMapCursorOverlayToSurface(
        double nx,
        double ny,
        int frameWidth,
        int frameHeight,
        double viewportWidth,
        double viewportHeight,
        out Point point)
    {
        point = default;
        if (frameWidth <= 0 ||
            frameHeight <= 0 ||
            viewportWidth <= 0 ||
            viewportHeight <= 0 ||
            double.IsNaN(nx) ||
            double.IsNaN(ny) ||
            double.IsInfinity(nx) ||
            double.IsInfinity(ny))
        {
            return false;
        }

        var clampedNx = Math.Clamp(nx, 0d, 1d);
        var clampedNy = Math.Clamp(ny, 0d, 1d);
        var scale = Math.Min(viewportWidth / frameWidth, viewportHeight / frameHeight);
        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale))
        {
            return false;
        }

        var displayedWidth = frameWidth * scale;
        var displayedHeight = frameHeight * scale;
        var offsetX = Math.Max(0d, (viewportWidth - displayedWidth) / 2d);
        var offsetY = Math.Max(0d, (viewportHeight - displayedHeight) / 2d);
        point = new Point(
            offsetX + clampedNx * displayedWidth,
            offsetY + clampedNy * displayedHeight);
        return true;
    }

    private void UpdateCursorOverlayPosition()
    {
        if (cursorOverlayLayer is null || cursorOverlayPointer is null)
        {
            return;
        }

        if (!CursorOverlayVisible ||
            Frame is null ||
            !TryMapCursorOverlayToSurface(
                CursorOverlayNx,
                CursorOverlayNy,
                Frame.PixelSize.Width,
                Frame.PixelSize.Height,
                Bounds.Width,
                Bounds.Height,
                out var point))
        {
            cursorOverlayLayer.IsVisible = false;
            return;
        }

        cursorOverlayLayer.IsVisible = true;
        Canvas.SetLeft(cursorOverlayPointer, point.X);
        Canvas.SetTop(cursorOverlayPointer, point.Y);
    }

    internal static BitmapInterpolationMode ResolveInterpolationModeForPresentation(
        int frameWidth,
        int frameHeight,
        double viewportWidth,
        double viewportHeight,
        double renderScaling)
    {
        if (frameWidth <= 0 || frameHeight <= 0 || viewportWidth <= 0 || viewportHeight <= 0)
        {
            return BitmapInterpolationMode.HighQuality;
        }

        var effectiveRenderScaling = renderScaling > 0 ? renderScaling : 1d;
        var displayedWidthPx = viewportWidth * effectiveRenderScaling;
        var displayedHeightPx = viewportHeight * effectiveRenderScaling;
        var scaleRatio = Math.Min(displayedWidthPx / frameWidth, displayedHeightPx / frameHeight);
        return scaleRatio < 0.95d || scaleRatio > 1.05d
            ? BitmapInterpolationMode.HighQuality
            : BitmapInterpolationMode.None;
    }

    private void OnMouseMoveRateHzChanged()
    {
        var clampedHz = ClampMouseMoveRate(MouseMoveRateHz);
        if (clampedHz != MouseMoveRateHz)
        {
            SetCurrentValue(MouseMoveRateHzProperty, clampedHz);
            return;
        }

        mouseMoveThrottleTimer.Interval = GetMouseMoveThrottleInterval(clampedHz);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        RemoteControlDebugDiagnostics.SetHelperControlMode(controlMode: false);
        ClearHeldStateAndRequestReleaseAll();
        ResetMouseMovePumpState();
        base.OnDetachedFromVisualTree(e);
    }

    private void UpdateFrameInterpolationMode()
    {
        var frameWidth = Frame?.PixelSize.Width ?? 0;
        var frameHeight = Frame?.PixelSize.Height ?? 0;
        var viewportWidth = (int)Math.Round(Bounds.Width);
        var viewportHeight = (int)Math.Round(Bounds.Height);
        var renderScaling = ResolveEffectiveRenderScaling();
        var nextMode = ResolveInterpolationModeForPresentation(
            frameWidth,
            frameHeight,
            Bounds.Width,
            Bounds.Height,
            renderScaling);
        var modeChanged = currentInterpolationMode != nextMode;
        if (!modeChanged && Frame is not null && HasLoggedInterpolationSnapshot(nextMode, frameWidth, frameHeight, viewportWidth, viewportHeight, renderScaling))
        {
            return;
        }

        currentInterpolationMode = nextMode;
        RenderOptions.SetBitmapInterpolationMode(frameImage, nextMode);

        if (Frame is null)
        {
            return;
        }

        if (!ShouldLogInterpolationSnapshot(nextMode, frameWidth, frameHeight, viewportWidth, viewportHeight, renderScaling))
        {
            return;
        }

        lastLoggedInterpolationMode = nextMode;
        lastLoggedFrameWidth = frameWidth;
        lastLoggedFrameHeight = frameHeight;
        lastLoggedViewportWidth = viewportWidth;
        lastLoggedViewportHeight = viewportHeight;
        lastLoggedRenderScaling = renderScaling;
        lastInterpolationLogTick = Stopwatch.GetTimestamp();

        LocalOperationalLog.Info(
            "ScreenShareUi",
            $"event=screenshare_surface_interpolation_changed; role={SanitizeRole(SurfaceRole)}; viewer_interpolation_mode={FormatInterpolationMode(nextMode)}; frame_width={frameWidth}; frame_height={frameHeight}; viewport_width={viewportWidth}; viewport_height={viewportHeight}; render_scaling={renderScaling:0.##}");
    }

    private bool ShouldLogInterpolationSnapshot(
        BitmapInterpolationMode interpolationMode,
        int frameWidth,
        int frameHeight,
        int viewportWidth,
        int viewportHeight,
        double renderScaling)
    {
        if (!HasLoggedInterpolationSnapshot(interpolationMode, frameWidth, frameHeight, viewportWidth, viewportHeight, renderScaling))
        {
            return true;
        }

        if (lastInterpolationLogTick <= 0)
        {
            return true;
        }

        return Stopwatch.GetElapsedTime(lastInterpolationLogTick) >= InterpolationLogInterval;
    }

    private bool HasLoggedInterpolationSnapshot(
        BitmapInterpolationMode interpolationMode,
        int frameWidth,
        int frameHeight,
        int viewportWidth,
        int viewportHeight,
        double renderScaling)
    {
        return lastLoggedInterpolationMode == interpolationMode &&
               lastLoggedFrameWidth == frameWidth &&
               lastLoggedFrameHeight == frameHeight &&
               lastLoggedViewportWidth == viewportWidth &&
               lastLoggedViewportHeight == viewportHeight &&
               Math.Abs(lastLoggedRenderScaling - renderScaling) < 0.01d;
    }

    private double ResolveEffectiveRenderScaling()
    {
        var topLevelScaling = (VisualRoot as TopLevel)?.RenderScaling ?? 0d;
        if (topLevelScaling > 0d)
        {
            lastKnownRenderScaling = topLevelScaling;
        }

        return lastKnownRenderScaling > 0d ? lastKnownRenderScaling : 1d;
    }

    private static string FormatInterpolationMode(BitmapInterpolationMode interpolationMode)
    {
        return interpolationMode switch
        {
            BitmapInterpolationMode.None => "none",
            BitmapInterpolationMode.HighQuality => "high_quality",
            BitmapInterpolationMode.MediumQuality => "medium_quality",
            BitmapInterpolationMode.LowQuality => "low_quality",
            _ => "unknown",
        };
    }

    private static string SanitizeRole(string? role)
    {
        return string.IsNullOrWhiteSpace(role)
            ? "unknown"
            : role.Trim().ToLowerInvariant();
    }

    private void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (!CaptureEnabled && !KeyboardCaptureEnabled)
        {
            return;
        }

        ClearHeldStateAndRequestReleaseAll();
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!CaptureEnabled)
        {
            return;
        }

        ClearHeldStateAndRequestReleaseAll();
    }

    private void ResetMouseMovePumpState()
    {
        hasPendingMouseMove = false;
        pendingMouseMoveNx = 0d;
        pendingMouseMoveNy = 0d;
        mouseMoveThrottleTimer.Stop();
#if DEBUG
        debugMouseMoveSentPerSecond = 0;
        debugMouseMoveSentInWindow = 0;
        debugMouseMoveWindowStartTickMs = 0;
#endif
        RemoteControlDebugDiagnostics.SetHelperMouseMoveSentPerSec(0);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!KeyboardCaptureEnabled || !IsFocused || e.Key == Key.None)
        {
            return;
        }

        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            ControlModeExitRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        heldState.UpdateModifiers(ToRemoteControlModifiersMask(e.KeyModifiers));
        PublishHeldState();

        EmitInput(
            new ControlInputMessageV1
            {
                Kind = "key",
                Action = "down",
                Key = e.Key.ToString(),
                PhysicalKey = TryGetPhysicalKey(e),
                Shift = (e.KeyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift,
                Ctrl = (e.KeyModifiers & KeyModifiers.Control) == KeyModifiers.Control,
                Alt = (e.KeyModifiers & KeyModifiers.Alt) == KeyModifiers.Alt,
                Meta = (e.KeyModifiers & KeyModifiers.Meta) == KeyModifiers.Meta,
                Repeat = TryGetIsRepeat(e),
            });
        e.Handled = true;
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (!KeyboardCaptureEnabled || !IsFocused || e.Key == Key.None)
        {
            return;
        }

        heldState.UpdateModifiers(ToRemoteControlModifiersMask(e.KeyModifiers));
        PublishHeldState();

        EmitInput(
            new ControlInputMessageV1
            {
                Kind = "key",
                Action = "up",
                Key = e.Key.ToString(),
                PhysicalKey = TryGetPhysicalKey(e),
                Shift = (e.KeyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift,
                Ctrl = (e.KeyModifiers & KeyModifiers.Control) == KeyModifiers.Control,
                Alt = (e.KeyModifiers & KeyModifiers.Alt) == KeyModifiers.Alt,
                Meta = (e.KeyModifiers & KeyModifiers.Meta) == KeyModifiers.Meta,
                Repeat = TryGetIsRepeat(e),
            });
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!CaptureEnabled || !TryMapPointerToNormalized(e, out var nx, out var ny))
        {
            return;
        }

        if (hasPendingMouseMove)
        {
            RemoteControlDebugDiagnostics.IncrementHelperMouseMoveDropped();
        }

        pendingMouseMoveNx = nx;
        pendingMouseMoveNy = ny;
        hasPendingMouseMove = true;
        if (!mouseMoveThrottleTimer.IsEnabled)
        {
            mouseMoveThrottleTimer.Start();
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        TryFocusForKeyboardCapture("pointer_pressed");

        if (!CaptureEnabled ||
            !TryMapPointerToNormalized(e, out var nx, out var ny) ||
            !TryMapButtonUpdate(e.GetCurrentPoint(this).Properties.PointerUpdateKind, out var action, out var button))
        {
            return;
        }

        heldState.UpdateModifiers(ToRemoteControlModifiersMask(e.KeyModifiers));
        heldState.ApplyMouseButton(
            ToRemoteControlMouseButtonsMask(button),
            string.Equals(action, "down", StringComparison.Ordinal));
        PublishHeldState();

        EmitInput(
            new ControlInputMessageV1
            {
                Kind = "mouse_button",
                Action = action,
                Button = button,
                Nx = nx,
                Ny = ny,
                Shift = (e.KeyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift,
                Ctrl = (e.KeyModifiers & KeyModifiers.Control) == KeyModifiers.Control,
                Alt = (e.KeyModifiers & KeyModifiers.Alt) == KeyModifiers.Alt,
                Meta = (e.KeyModifiers & KeyModifiers.Meta) == KeyModifiers.Meta,
            });
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!CaptureEnabled ||
            !TryMapPointerToNormalized(e, out var nx, out var ny) ||
            !TryMapButtonUpdate(e.GetCurrentPoint(this).Properties.PointerUpdateKind, out var action, out var button))
        {
            return;
        }

        heldState.UpdateModifiers(ToRemoteControlModifiersMask(e.KeyModifiers));
        heldState.ApplyMouseButton(
            ToRemoteControlMouseButtonsMask(button),
            string.Equals(action, "down", StringComparison.Ordinal));
        PublishHeldState();

        EmitInput(
            new ControlInputMessageV1
            {
                Kind = "mouse_button",
                Action = action,
                Button = button,
                Nx = nx,
                Ny = ny,
                Shift = (e.KeyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift,
                Ctrl = (e.KeyModifiers & KeyModifiers.Control) == KeyModifiers.Control,
                Alt = (e.KeyModifiers & KeyModifiers.Alt) == KeyModifiers.Alt,
                Meta = (e.KeyModifiers & KeyModifiers.Meta) == KeyModifiers.Meta,
            });
        e.Handled = true;
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!CaptureEnabled || !TryMapPointerToNormalized(e, out var nx, out var ny))
        {
            return;
        }

        heldState.UpdateModifiers(ToRemoteControlModifiersMask(e.KeyModifiers));
        PublishHeldState();

        EmitInput(
            new ControlInputMessageV1
            {
                Kind = "mouse_wheel",
                Nx = nx,
                Ny = ny,
                DeltaX = e.Delta.X,
                DeltaY = e.Delta.Y,
                Shift = (e.KeyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift,
                Ctrl = (e.KeyModifiers & KeyModifiers.Control) == KeyModifiers.Control,
                Alt = (e.KeyModifiers & KeyModifiers.Alt) == KeyModifiers.Alt,
                Meta = (e.KeyModifiers & KeyModifiers.Meta) == KeyModifiers.Meta,
            });
        e.Handled = true;
    }

    private void OnMouseMoveThrottleTick(object? sender, EventArgs e)
    {
        if (!CaptureEnabled || !hasPendingMouseMove)
        {
            mouseMoveThrottleTimer.Stop();
            return;
        }

        hasPendingMouseMove = false;
#if DEBUG
        RecordMouseMoveSentForDebug();
#endif
        EmitInput(
            new ControlInputMessageV1
            {
                Kind = "mouse_move",
                Nx = pendingMouseMoveNx,
                Ny = pendingMouseMoveNy,
            });
    }

    private void EmitInput(ControlInputMessageV1 message)
    {
        RemoteControlInputProduced?.Invoke(this, new RemoteControlInputProducedEventArgs(message));
    }

    private void ClearHeldStateAndRequestReleaseAll()
    {
        heldState.Clear();
        PublishHeldState(immediateReleaseAll: true);
    }

    private void PublishHeldState(bool immediateReleaseAll = false)
    {
        RemoteControlHeldStateChanged?.Invoke(
            this,
            new RemoteControlHeldStateChangedEventArgs(
                heldState.Modifiers,
                heldState.Buttons,
                immediateReleaseAll));
    }

    private bool TryMapPointerToNormalized(PointerEventArgs e, out double nx, out double ny)
    {
        nx = 0d;
        ny = 0d;
        if (Frame is null)
        {
            return false;
        }

        var frameWidth = Frame.PixelSize.Width;
        var frameHeight = Frame.PixelSize.Height;
        if (frameWidth <= 0 || frameHeight <= 0)
        {
            return false;
        }

        var viewerWidth = Bounds.Width;
        var viewerHeight = Bounds.Height;
        var position = e.GetPosition(this);
        if (!TryMapPointerToNormalizedUniform(
                position.X,
                position.Y,
                viewerWidth,
                viewerHeight,
                frameWidth,
                frameHeight,
                out nx,
                out ny,
                out var wasOutOfRange))
        {
            return false;
        }

        if (wasOutOfRange)
        {
            RemoteControlDebugDiagnostics.IncrementHelperOutOfRangeClamp();
        }

        return true;
    }

    private static bool TryMapButtonUpdate(PointerUpdateKind updateKind, out string action, out string button)
    {
        action = string.Empty;
        button = string.Empty;
        switch (updateKind)
        {
            case PointerUpdateKind.LeftButtonPressed:
                action = "down";
                button = "left";
                return true;
            case PointerUpdateKind.LeftButtonReleased:
                action = "up";
                button = "left";
                return true;
            case PointerUpdateKind.RightButtonPressed:
                action = "down";
                button = "right";
                return true;
            case PointerUpdateKind.RightButtonReleased:
                action = "up";
                button = "right";
                return true;
            case PointerUpdateKind.MiddleButtonPressed:
                action = "down";
                button = "middle";
                return true;
            case PointerUpdateKind.MiddleButtonReleased:
                action = "up";
                button = "middle";
                return true;
            case PointerUpdateKind.XButton1Pressed:
                action = "down";
                button = "x1";
                return true;
            case PointerUpdateKind.XButton1Released:
                action = "up";
                button = "x1";
                return true;
            case PointerUpdateKind.XButton2Pressed:
                action = "down";
                button = "x2";
                return true;
            case PointerUpdateKind.XButton2Released:
                action = "up";
                button = "x2";
                return true;
            default:
                return false;
        }
    }

    private static RemoteControlMouseButtonsMask ToRemoteControlMouseButtonsMask(string button)
    {
        return button switch
        {
            "left" => RemoteControlMouseButtonsMask.Left,
            "right" => RemoteControlMouseButtonsMask.Right,
            "middle" => RemoteControlMouseButtonsMask.Middle,
            "x1" => RemoteControlMouseButtonsMask.X1,
            "x2" => RemoteControlMouseButtonsMask.X2,
            _ => RemoteControlMouseButtonsMask.None,
        };
    }

    private static RemoteControlModifiersMask ToRemoteControlModifiersMask(KeyModifiers keyModifiers)
    {
        var mask = RemoteControlModifiersMask.None;
        if ((keyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift)
        {
            mask |= RemoteControlModifiersMask.Shift;
        }

        if ((keyModifiers & KeyModifiers.Control) == KeyModifiers.Control)
        {
            mask |= RemoteControlModifiersMask.Ctrl;
        }

        if ((keyModifiers & KeyModifiers.Alt) == KeyModifiers.Alt)
        {
            mask |= RemoteControlModifiersMask.Alt;
        }

        if ((keyModifiers & KeyModifiers.Meta) == KeyModifiers.Meta)
        {
            mask |= RemoteControlModifiersMask.Meta;
            mask |= RemoteControlModifiersMask.Win;
        }

        return mask;
    }

    private static string? TryGetPhysicalKey(KeyEventArgs e)
    {
        var value = PhysicalKeyProperty?.GetValue(e)?.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool? TryGetIsRepeat(KeyEventArgs e)
    {
        return IsRepeatProperty?.GetValue(e) is bool repeat ? repeat : null;
    }

    private static int ClampMouseMoveRate(int hz)
    {
        return Math.Clamp(hz, MinMouseMoveRateHz, MaxMouseMoveRateHz);
    }

    private static TimeSpan GetMouseMoveThrottleInterval(int hz)
    {
        var clampedHz = ClampMouseMoveRate(hz);
        return TimeSpan.FromSeconds(1d / clampedHz);
    }

    private static bool TryMapPointerToNormalizedUniform(
        double pointerX,
        double pointerY,
        double viewerWidth,
        double viewerHeight,
        double frameWidth,
        double frameHeight,
        out double nx,
        out double ny,
        out bool wasOutOfRange)
    {
        nx = 0d;
        ny = 0d;
        wasOutOfRange = false;
        if (!IsFinitePositive(viewerWidth) ||
            !IsFinitePositive(viewerHeight) ||
            !IsFinitePositive(frameWidth) ||
            !IsFinitePositive(frameHeight))
        {
            return false;
        }

        var scale = Math.Min(viewerWidth / frameWidth, viewerHeight / frameHeight);
        if (!IsFinitePositive(scale))
        {
            return false;
        }

        var contentWidth = frameWidth * scale;
        var contentHeight = frameHeight * scale;
        if (!IsFinitePositive(contentWidth) || !IsFinitePositive(contentHeight))
        {
            return false;
        }

        var offsetX = (viewerWidth - contentWidth) / 2d;
        var offsetY = (viewerHeight - contentHeight) / 2d;

        var rawNx = (pointerX - offsetX) / contentWidth;
        var rawNy = (pointerY - offsetY) / contentHeight;
        if (double.IsNaN(rawNx) || double.IsInfinity(rawNx) ||
            double.IsNaN(rawNy) || double.IsInfinity(rawNy))
        {
            return false;
        }

        wasOutOfRange = rawNx < 0d || rawNx > 1d || rawNy < 0d || rawNy > 1d;
        nx = Math.Clamp(rawNx, 0d, 1d);
        ny = Math.Clamp(rawNy, 0d, 1d);
        return true;
    }

    private static bool IsFinitePositive(double value)
    {
        return value > 0d && !double.IsNaN(value) && !double.IsInfinity(value);
    }

#if DEBUG
    private void RecordMouseMoveSentForDebug()
    {
        var nowMs = Environment.TickCount64;
        if (debugMouseMoveWindowStartTickMs == 0)
        {
            debugMouseMoveWindowStartTickMs = nowMs;
        }

        if (nowMs - debugMouseMoveWindowStartTickMs >= 1000)
        {
            debugMouseMoveSentPerSecond = debugMouseMoveSentInWindow;
            debugMouseMoveSentInWindow = 0;
            debugMouseMoveWindowStartTickMs = nowMs;
            RemoteControlDebugDiagnostics.SetHelperMouseMoveSentPerSec(debugMouseMoveSentPerSecond);
        }

        debugMouseMoveSentInWindow++;
    }
#endif
}
