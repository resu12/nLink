using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using NLink.App.Configuration;
using NLink.App.Services;
using NLink.App.ViewModels;
using NLink.App.Views;

namespace NLink.SmokeTests;

[Collection(AvaloniaHeadlessUiCollection.Name)]
[Trait("Area", "Gui")]
public sealed class DiagnosticsWalletBalanceButtonTests : IClassFixture<Beta3DefaultUiFixture>
{
    private readonly Beta3DefaultUiFixture fixture;

    public DiagnosticsWalletBalanceButtonTests(Beta3DefaultUiFixture fixture)
    {
        this.fixture = fixture;
    }

    [Theory]
    [InlineData("Wallet verified", 640)]
    [InlineData("Wallet validation failed. Please check your password.", 900)]
    public async Task WalletFeedback_RemainsVisibleWithoutScrolling_AndDoesNotDuplicateHeader(string message, int height)
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var vm = new DiagnosticsPageViewModel(
                ScreenShareEvidenceLocator.CreateDefault(), static () => { }, TransportRuntimeConfig.Select());
            var view = new DiagnosticsPageView { DataContext = vm };
            var window = new Window { Width = 1080, Height = height, Content = view };
            try
            {
                window.Show();
                await GuiTestAssertions.FlushUiAsync();
                var tabs = view.GetVisualDescendants().OfType<TabControl>().Single();
                tabs.SelectedIndex = 1;
                await GuiTestAssertions.FlushUiAsync();
                var scroll = view.FindControl<ScrollViewer>("OptionsScrollViewer")!;
                var feedback = view.FindControl<TextBlock>("WalletFeedback")!;
                var headerFeedback = view.FindControl<TextBlock>("PageFeedback")!;
                var maxOffset = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
                Assert.True(maxOffset > 0, "The wallet controls should require scrolling at this window height.");
                scroll.Offset = new Vector(0, maxOffset);
                await GuiTestAssertions.FlushUiAsync();
                var originalOffset = scroll.Offset.Y;

                vm.CopyFeedback.Show(message, TimeSpan.FromMinutes(1));
                await GuiTestAssertions.FlushUiAsync();
                Assert.Equal(message, feedback.Text);
                Assert.True(feedback.IsEffectivelyVisible);
                Assert.False(headerFeedback.IsEffectivelyVisible);
                Assert.DoesNotContain(scroll, feedback.GetVisualAncestors());
                Assert.InRange(scroll.Offset.Y, originalOffset - 1, originalOffset + 1);
                var position = feedback.TranslatePoint(default, view)!.Value;
                Assert.InRange(position.Y, 0, view.Bounds.Height - feedback.Bounds.Height);

                scroll.Offset = default;
                await GuiTestAssertions.FlushUiAsync();
                Assert.Equal(position, feedback.TranslatePoint(default, view)!.Value);

                foreach (var tabIndex in new[] { 0, 2 })
                {
                    tabs.SelectedIndex = tabIndex;
                    await GuiTestAssertions.FlushUiAsync();
                    Assert.False(feedback.IsEffectivelyVisible);
                    Assert.True(headerFeedback.IsEffectivelyVisible);
                    Assert.Equal(message, headerFeedback.Text);
                }

                tabs.SelectedIndex = 1;
                vm.CopyFeedback.Hide();
                await GuiTestAssertions.FlushUiAsync();
                Assert.False(feedback.IsEffectivelyVisible);
                Assert.False(headerFeedback.IsEffectivelyVisible);
            }
            finally
            {
                view.DataContext = null;
                window.Close();
            }
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ValidateBalance_UsesYellowOnlyForNewlyLinkedWallet()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var vm = new DiagnosticsPageViewModel(
                ScreenShareEvidenceLocator.CreateDefault(), static () => { }, TransportRuntimeConfig.Select());
            var view = new DiagnosticsPageView { DataContext = vm };
            var window = new Window { Width = 1080, Height = 900, Content = view };
            try
            {
                window.Show();
                await GuiTestAssertions.FlushUiAsync();
                view.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 1;
                await GuiTestAssertions.FlushUiAsync();
                var button = view.GetVisualDescendants().OfType<Button>()
                    .Single(candidate => Equals(candidate.Content, "Validate balance"));
                Assert.Contains("secondaryButton", button.Classes);
                Assert.DoesNotContain("positiveButton", button.Classes);

                await vm.LinkTunaWalletAsync(Path.Combine(Path.GetTempPath(), "wallet-button-test.json"));
                await GuiTestAssertions.FlushUiAsync();
                Assert.Contains("positiveButton", button.Classes);
                Assert.DoesNotContain("secondaryButton", button.Classes);
                Assert.Equal(Color.Parse("#F2C94C"), Assert.IsAssignableFrom<ISolidColorBrush>(button.Background).Color);
                Assert.Equal(Color.Parse("#0B0F16"), Assert.IsAssignableFrom<ISolidColorBrush>(button.Foreground).Color);

                // No verifier is installed: even a failed check consumes the prompt.
                await vm.ValidateTunaWalletAsync("test-password".ToCharArray());
                await GuiTestAssertions.FlushUiAsync();
                Assert.Contains("secondaryButton", button.Classes);
                Assert.DoesNotContain("positiveButton", button.Classes);
                Assert.Equal(Color.Parse("#1F2937"), Assert.IsAssignableFrom<ISolidColorBrush>(button.Background).Color);
            }
            finally
            {
                view.DataContext = null;
                window.Close();
            }
            return true;
        }, CancellationToken.None);
    }
}
