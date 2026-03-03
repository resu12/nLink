using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace NLink.App.Views;

public partial class ScreenShareSurfaceView : UserControl
{
    public static readonly StyledProperty<Bitmap?> FrameProperty =
        AvaloniaProperty.Register<ScreenShareSurfaceView, Bitmap?>(nameof(Frame));

    public ScreenShareSurfaceView()
    {
        InitializeComponent();
    }

    public Bitmap? Frame
    {
        get => GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }
}
