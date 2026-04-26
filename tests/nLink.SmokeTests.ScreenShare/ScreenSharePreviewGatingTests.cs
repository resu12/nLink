using NLink.App.ViewModels;

namespace NLink.SmokeTests;

[Trait("Area", "ScreenShare")]
[Collection(AvaloniaHeadlessUiCollection.Name)]
public sealed class ScreenSharePreviewGatingTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenSharePreviewGating_FlagsOff_ReturnsFalse()
    {
        var canShow = HelpeePageViewModel.ComputeCanShowScreenShareAction(
            enableScreenShareScaffold: false,
            enableScreenShareCapture: false,
            enableScreenSharePreview: false,
            isCaptureSupported: true);

        Assert.False(canShow);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenSharePreviewGating_UnsupportedCapture_ReturnsFalse()
    {
        var canShow = HelpeePageViewModel.ComputeCanShowScreenShareAction(
            enableScreenShareScaffold: true,
            enableScreenShareCapture: true,
            enableScreenSharePreview: true,
            isCaptureSupported: false);

        Assert.False(canShow);
    }
}
