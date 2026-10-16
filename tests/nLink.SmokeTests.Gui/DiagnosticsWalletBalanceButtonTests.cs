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
