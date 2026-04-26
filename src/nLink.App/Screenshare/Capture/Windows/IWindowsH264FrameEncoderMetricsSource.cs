namespace NLink.App.Services.ScreenCapture;

internal interface IWindowsH264FrameEncoderMetricsSource
{
    WindowsH264FrameEncoderRuntimeMetrics GetRuntimeMetricsSnapshot();
}
