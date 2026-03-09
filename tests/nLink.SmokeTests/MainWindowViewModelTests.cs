using NLink.App.ViewModels;

namespace NLink.SmokeTests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task PreparePageForWindowCloseAsync_CompletesWhenPreparationHangs()
    {
        var page = new StubWindowCloseAware(() => Task.Delay(Timeout.InfiniteTimeSpan));
        var start = DateTimeOffset.UtcNow;

        await MainWindowViewModel.PreparePageForWindowCloseAsync(page);

        var elapsed = DateTimeOffset.UtcNow - start;
        Assert.True(page.CallCount == 1, "Expected close preparation to be attempted once.");
        Assert.InRange(elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(3));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task PreparePageForWindowCloseAsync_IgnoresPreparationFailures()
    {
        var page = new StubWindowCloseAware(() => Task.FromException(new InvalidOperationException("boom")));

        await MainWindowViewModel.PreparePageForWindowCloseAsync(page);

        Assert.Equal(1, page.CallCount);
    }

    private sealed class StubWindowCloseAware : IWindowCloseAware
    {
        private readonly Func<Task> prepareAsync;

        public StubWindowCloseAware(Func<Task> prepareAsync)
        {
            this.prepareAsync = prepareAsync;
        }

        public int CallCount { get; private set; }

        public Task PrepareForWindowCloseAsync()
        {
            CallCount++;
            return prepareAsync();
        }
    }
}
