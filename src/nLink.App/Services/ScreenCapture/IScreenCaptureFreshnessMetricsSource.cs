namespace NLink.App.Services.ScreenCapture;

internal interface IScreenCaptureFreshnessMetricsSource
{
    ScreenCaptureFreshnessMetrics GetFreshnessMetricsSnapshot();

    int PurgePendingRawFrames();
}
