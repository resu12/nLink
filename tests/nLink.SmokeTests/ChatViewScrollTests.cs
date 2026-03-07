using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using NLink.App.ViewModels;
using NLink.App.Views;

namespace NLink.SmokeTests;

[Collection(AvaloniaHeadlessUiCollection.Name)]
public sealed class ChatViewScrollTests : IClassFixture<ChatViewScrollFixture>
{
    private readonly ChatViewScrollFixture fixture;

    public ChatViewScrollTests(ChatViewScrollFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task ChatView_ScrolledUp_DoesNotAutoScroll_WhenNewMessageArrives()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var host = await CreateHostWithMessagesAsync();
            try
            {
                var bindings = host.Bindings;
                var scrollViewer = host.ScrollViewer;
                await ScrollToBottomAsync(scrollViewer);
                var bottomOffset = scrollViewer.Offset.Y;
                scrollViewer.Offset = new Vector(0, Math.Max(0, bottomOffset - 160));
                await FlushUiAsync();

                var offsetBefore = scrollViewer.Offset.Y;
                bindings.ChatMessages.Add(CreateMessage(bindings.ChatMessages.Count));
                await FlushUiAsync();

                Assert.InRange(scrollViewer.Offset.Y, offsetBefore - 1, offsetBefore + 1);
                Assert.True(GetRemainingDistance(scrollViewer) > 32d);
            }
            finally
            {
                host.Window.Close();
            }
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ChatView_AtBottom_AutoScrolls_WhenNewMessageArrives()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var host = await CreateHostWithMessagesAsync();
            try
            {
                var bindings = host.Bindings;
                var scrollViewer = host.ScrollViewer;
                await ScrollToBottomAsync(scrollViewer);
                Assert.True(GetRemainingDistance(scrollViewer) <= 1d);

                bindings.ChatMessages.Add(CreateMessage(bindings.ChatMessages.Count));
                await FlushUiAsync();

                Assert.True(GetRemainingDistance(scrollViewer) <= 1d);
            }
            finally
            {
                host.Window.Close();
            }
            return true;
        }, CancellationToken.None);
    }

    private static async Task<ChatViewHost> CreateHostWithMessagesAsync()
    {
        var bindings = new TestChatPanelBindings();
        for (var i = 0; i < 80; i++)
        {
            bindings.ChatMessages.Add(CreateMessage(i));
        }

        var chatView = new ChatView
        {
            DataContext = bindings,
        };

        var window = new Window
        {
            Width = 520,
            Height = 620,
            Content = chatView,
        };

        window.Show();
        await FlushUiAsync();

        var scrollViewer = chatView.FindControl<ScrollViewer>("MessagesScrollViewer")
            ?? throw new InvalidOperationException("ChatView MessagesScrollViewer not found.");

        return new ChatViewHost(window, bindings, scrollViewer);
    }

    private static ChatLineViewModel CreateMessage(int index)
        => new()
        {
            IsLocal = index % 2 == 0,
            Text = $"Message {index}: " + new string('x', 160),
        };

    private static async Task ScrollToBottomAsync(ScrollViewer scrollViewer)
    {
        scrollViewer.ScrollToEnd();
        await FlushUiAsync();
    }

    private static async Task FlushUiAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static double GetRemainingDistance(ScrollViewer scrollViewer)
    {
        var remaining = scrollViewer.Extent.Height - scrollViewer.Viewport.Height - scrollViewer.Offset.Y;
        return Math.Max(0d, remaining);
    }

    private sealed record ChatViewHost(
        Window Window,
        TestChatPanelBindings Bindings,
        ScrollViewer ScrollViewer);

    private sealed class TestChatPanelBindings : IChatPanelBindings
    {
        public string ChatPanelTitle => "Message";

        public bool ShowChatTopBar => false;

        public bool ShowChatNotice => false;

        public string ChatNoticeText => string.Empty;

        public ObservableCollection<ChatLineViewModel> ChatMessages { get; } = [];

        public bool HasChatMessages => ChatMessages.Count > 0;

        public bool ShowNoMessagesPlaceholder => !HasChatMessages;

        public string ChatDraft { get; set; } = string.Empty;

        public bool IsChatInputEnabled => true;

        public bool CanEndSession => true;

        public IAsyncRelayCommand SendChatCommand { get; } = new AsyncRelayCommand(() => Task.CompletedTask);

        public IRelayCommand EndSessionCommand { get; } = new RelayCommand(() => { });
    }
}

public sealed class ChatViewScrollFixture : IDisposable
{
    public ChatViewScrollFixture()
    {
        Session = HeadlessUnitTestSession.StartNew(typeof(AvaloniaHeadlessUiAppBootstrap));
    }

    public HeadlessUnitTestSession Session { get; }

    public void Dispose()
    {
        Session.Dispose();
    }
}
