using NLink.App.ViewModels;

namespace NLink.SmokeTests;

[Trait("Area", "Gui")]
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
        Assert.InRange(elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(4));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task PreparePageForWindowCloseAsync_IgnoresPreparationFailures()
    {
        var page = new StubWindowCloseAware(() => Task.FromException(new InvalidOperationException("boom")));

        await MainWindowViewModel.PreparePageForWindowCloseAsync(page);

        Assert.Equal(1, page.CallCount);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task PrepareWindowCloseAsync_StartsEndSessionAndPagePreparation()
    {
        var endSessionStarted = 0;
        var page = new StubWindowCloseAware(() => Task.CompletedTask);

        await MainWindowViewModel.PrepareWindowCloseAsync(
            page,
            () =>
            {
                endSessionStarted++;
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(1));

        Assert.Equal(1, endSessionStarted);
        Assert.Equal(1, page.CallCount);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task PrepareWindowCloseAsync_CompletesWhenEndSessionHangs()
    {
        var page = new StubWindowCloseAware(() => Task.CompletedTask);
        var start = DateTimeOffset.UtcNow;

        await MainWindowViewModel.PrepareWindowCloseAsync(
            page,
            () => Task.Delay(Timeout.InfiniteTimeSpan),
            TimeSpan.FromMilliseconds(100));

        var elapsed = DateTimeOffset.UtcNow - start;
        Assert.Equal(1, page.CallCount);
        Assert.InRange(elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(1));
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
