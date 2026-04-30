using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using NLink.App.ViewModels;
using NLink.Core.FileTransfer;
using NLink.App.Views;

namespace NLink.SmokeTests;

[Collection(AvaloniaHeadlessUiCollection.Name)]
[Trait("Area", "Gui")]
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

    [Fact]
    public async Task ChatView_FileTransferAccept_ExecutesItemCommandWithTransferId()
    {
        await fixture.Session.Dispatch(async () =>
        {
            const string transferId = "transfer-pending";
            var accepted = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var acceptCommand = new AsyncRelayCommand<string?>(id =>
            {
                accepted.TrySetResult(id);
                return Task.CompletedTask;
            });

            var bindings = new TestChatPanelBindings
            {
                InboundFileTransfer = FileTransferPanelItemViewModel.FromSnapshot(
                    new FileTransferTransferSnapshot(
                        SessionId: "session-a",
                        TransferId: transferId,
                        Direction: FileTransferDirection.Inbound,
                        State: FileTransferTransferState.PendingDecision,
                        FileName: "nLink-Setup.exe",
                        FileSizeBytes: 57_400_000,
                        Sha256Base64: null,
                        BytesTransferred: 0,
                        ChunksTransferred: 0,
                        ChunkCount: 0,
                        ChunkSizeBytes: 0,
                        ErrorCode: null,
                        StatusMessage: null),
                    acceptCommand,
                    new AsyncRelayCommand<string?>(_ => Task.CompletedTask),
                    null),
            };

            var host = await CreateHostAsync(bindings);
            try
            {
                var acceptButton = host.Window.GetVisualDescendants()
                    .OfType<Button>()
                    .FirstOrDefault(button => string.Equals(
                        AutomationProperties.GetAutomationId(button),
                        "Chat.FileTransfer.Accept",
                        StringComparison.Ordinal));

                Assert.NotNull(acceptButton);
                Assert.True(acceptButton!.IsVisible);
                Assert.Equal(transferId, acceptButton.CommandParameter);

                acceptButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent, acceptButton));

                var acceptedTransferId = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(transferId, acceptedTransferId);
            }
            finally
            {
                host.Window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ChatView_FileTransferAccept_RemainsEnabledWithParentCanExecute()
    {
        await fixture.Session.Dispatch(async () =>
        {
            const string transferId = "transfer-pending";
            var accepted = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var bindings = new TestChatPanelBindings();
            var acceptCommand = new AsyncRelayCommand<string?>(
                id =>
                {
                    accepted.TrySetResult(id);
                    return Task.CompletedTask;
                },
                id => bindings.InboundFileTransfer is { ShowAccept: true } inbound &&
                      string.Equals(inbound.TransferId, id, StringComparison.Ordinal));
            bindings.InboundFileTransfer = FileTransferPanelItemViewModel.FromSnapshot(
                new FileTransferTransferSnapshot(
                    SessionId: "session-a",
                    TransferId: transferId,
                    Direction: FileTransferDirection.Inbound,
                    State: FileTransferTransferState.PendingDecision,
                    FileName: "nLink-Setup.exe",
                    FileSizeBytes: 57_400_000,
                    Sha256Base64: null,
                    BytesTransferred: 0,
                    ChunksTransferred: 0,
                    ChunkCount: 0,
                    ChunkSizeBytes: 0,
                    ErrorCode: null,
                    StatusMessage: null),
                acceptCommand,
                new AsyncRelayCommand<string?>(_ => Task.CompletedTask),
                null);

            acceptCommand.NotifyCanExecuteChanged();

            var host = await CreateHostAsync(bindings);
            try
            {
                var acceptButton = host.Window.GetVisualDescendants()
                    .OfType<Button>()
                    .FirstOrDefault(button => string.Equals(
                        AutomationProperties.GetAutomationId(button),
                        "Chat.FileTransfer.Accept",
                        StringComparison.Ordinal));

                Assert.NotNull(acceptButton);
                Assert.True(acceptButton!.IsVisible);
                Assert.True(acceptButton.IsEnabled);

                acceptButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent, acceptButton));

                var acceptedTransferId = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(transferId, acceptedTransferId);
            }
            finally
            {
                host.Window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ChatView_ChatNotice_WrapsLongMessage()
    {
        await fixture.Session.Dispatch(async () =>
        {
            const string notice = "File-transfer target already exists and overwrite is disabled.";
            var bindings = new TestChatPanelBindings
            {
                ShowChatNotice = true,
                ChatNoticeText = notice,
            };

            var host = await CreateHostAsync(bindings);
            try
            {
                var noticeTextBlock = host.Window.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .FirstOrDefault(textBlock => string.Equals(textBlock.Text, notice, StringComparison.Ordinal));

                Assert.NotNull(noticeTextBlock);
                Assert.Equal(TextWrapping.Wrap, noticeTextBlock!.TextWrapping);
            }
            finally
            {
                host.Window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ChatView_SendFile_ExecutesBoundCommand()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var bindings = new TestChatPanelBindings
            {
                ShowSendFileAction = true,
                CanSendFileAction = true,
                SendFileCommand = new RelayCommand(() => requested.TrySetResult()),
            };

            var host = await CreateHostAsync(bindings);
            try
            {
                var sendFileButton = host.Window.GetVisualDescendants()
                    .OfType<Button>()
                    .FirstOrDefault(button => string.Equals(
                        AutomationProperties.GetAutomationId(button),
                        "Chat.SendFile",
                        StringComparison.Ordinal));

                Assert.NotNull(sendFileButton);
                Assert.True(sendFileButton!.IsVisible);
                Assert.True(sendFileButton.IsEnabled);

                sendFileButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent, sendFileButton));

                await requested.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
            finally
            {
                host.Window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ChatView_FileTransferCancel_ExecutesItemCommandWithTransferId()
    {
        await fixture.Session.Dispatch(async () =>
        {
            const string transferId = "transfer-sending";
            var canceled = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelCommand = new AsyncRelayCommand<string?>(id =>
            {
                canceled.TrySetResult(id);
                return Task.CompletedTask;
            });

            var bindings = new TestChatPanelBindings
            {
                OutboundFileTransfer = FileTransferPanelItemViewModel.FromSnapshot(
                    new FileTransferTransferSnapshot(
                        SessionId: "session-a",
                        TransferId: transferId,
                        Direction: FileTransferDirection.Outbound,
                        State: FileTransferTransferState.Sending,
                        FileName: "nLink-Setup.exe",
                        FileSizeBytes: 57_400_000,
                        Sha256Base64: null,
                        BytesTransferred: 10_000,
                        ChunksTransferred: 1,
                        ChunkCount: 8,
                        ChunkSizeBytes: 21_504,
                        ErrorCode: null,
                        StatusMessage: null),
                    null,
                    null,
                    cancelCommand),
            };

            var host = await CreateHostAsync(bindings);
            try
            {
                var cancelButton = host.Window.GetVisualDescendants()
                    .OfType<Button>()
                    .FirstOrDefault(button => string.Equals(
                        AutomationProperties.GetAutomationId(button),
                        "Chat.FileTransfer.Cancel",
                        StringComparison.Ordinal));

                Assert.NotNull(cancelButton);
                Assert.True(cancelButton!.IsVisible);
                Assert.Equal(transferId, cancelButton.CommandParameter);

                cancelButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent, cancelButton));

                var canceledTransferId = await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(transferId, canceledTransferId);
            }
            finally
            {
                host.Window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ChatView_FileTransferPause_ExecutesItemCommandWithTransferId()
    {
        await fixture.Session.Dispatch(async () =>
        {
            const string transferId = "transfer-pause";
            var paused = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pauseCommand = new AsyncRelayCommand<string?>(id =>
            {
                paused.TrySetResult(id);
                return Task.CompletedTask;
            });

            var bindings = new TestChatPanelBindings
            {
                OutboundFileTransfer = FileTransferPanelItemViewModel.FromSnapshot(
                    new FileTransferTransferSnapshot(
                        SessionId: "session-a",
                        TransferId: transferId,
                        Direction: FileTransferDirection.Outbound,
                        State: FileTransferTransferState.Sending,
                        FileName: "nLink-Setup.exe",
                        FileSizeBytes: 57_400_000,
                        Sha256Base64: null,
                        BytesTransferred: 10_000,
                        ChunksTransferred: 1,
                        ChunkCount: 8,
                        ChunkSizeBytes: 21_504,
                        ErrorCode: null,
                        StatusMessage: null),
                    pauseCommand: pauseCommand),
            };

            var host = await CreateHostAsync(bindings);
            try
            {
                var pauseButton = host.Window.GetVisualDescendants()
                    .OfType<Button>()
                    .FirstOrDefault(button => string.Equals(
                        AutomationProperties.GetAutomationId(button),
                        "Chat.FileTransfer.Pause",
                        StringComparison.Ordinal));

                Assert.NotNull(pauseButton);
                Assert.True(pauseButton!.IsVisible);
                Assert.Equal(transferId, pauseButton.CommandParameter);

                pauseButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent, pauseButton));

                var pausedTransferId = await paused.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(transferId, pausedTransferId);
            }
            finally
            {
                host.Window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ChatView_FileTransferResume_ExecutesItemCommandWithTransferId()
    {
        await fixture.Session.Dispatch(async () =>
        {
            const string transferId = "transfer-resume";
            var resumed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var resumeCommand = new AsyncRelayCommand<string?>(id =>
            {
                resumed.TrySetResult(id);
                return Task.CompletedTask;
            });

            var bindings = new TestChatPanelBindings
            {
                InboundFileTransfer = FileTransferPanelItemViewModel.FromSnapshot(
                    new FileTransferTransferSnapshot(
                        SessionId: "session-a",
                        TransferId: transferId,
                        Direction: FileTransferDirection.Inbound,
                        State: FileTransferTransferState.Receiving,
                        FileName: "payload.bin",
                        FileSizeBytes: 57_400_000,
                        Sha256Base64: null,
                        BytesTransferred: 10_000,
                        ChunksTransferred: 1,
                        ChunkCount: 8,
                        ChunkSizeBytes: 21_504,
                        ErrorCode: null,
                        StatusMessage: "Transfer paused.",
                        IsPaused: true,
                        PauseReason: "ui_pause"),
                    resumeCommand: resumeCommand),
            };

            var host = await CreateHostAsync(bindings);
            try
            {
                var resumeButton = host.Window.GetVisualDescendants()
                    .OfType<Button>()
                    .FirstOrDefault(button => string.Equals(
                        AutomationProperties.GetAutomationId(button),
                        "Chat.FileTransfer.Resume",
                        StringComparison.Ordinal));

                Assert.NotNull(resumeButton);
                Assert.True(resumeButton!.IsVisible);
                Assert.Equal(transferId, resumeButton.CommandParameter);

                resumeButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent, resumeButton));

                var resumedTransferId = await resumed.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(transferId, resumedTransferId);
            }
            finally
            {
                host.Window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ChatView_CompletedInboundFileTransferCard_OpensSavedDirectory()
    {
        await fixture.Session.Dispatch(async () =>
        {
            const string transferId = "transfer-completed";
            var savedDirectoryPath = AppContext.BaseDirectory;
            var opened = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            ChatView.OpenDirectoryOverrideForTests = path =>
            {
                opened.TrySetResult(path);
                return true;
            };

            var bindings = new TestChatPanelBindings
            {
                InboundFileTransfer = FileTransferPanelItemViewModel.FromSnapshot(
                    new FileTransferTransferSnapshot(
                        SessionId: "session-a",
                        TransferId: transferId,
                        Direction: FileTransferDirection.Inbound,
                        State: FileTransferTransferState.Completed,
                        FileName: "payload.bin",
                        FileSizeBytes: 57_400_000,
                        Sha256Base64: null,
                        BytesTransferred: 57_400_000,
                        ChunksTransferred: 8,
                        ChunkCount: 8,
                        ChunkSizeBytes: 21_504,
                        ErrorCode: null,
                        StatusMessage: "Transfer complete.",
                        SavedFilePath: Path.Combine(savedDirectoryPath, "payload.bin"),
                        SavedDirectoryPath: savedDirectoryPath,
                        SavedFileName: "payload.bin")),
            };

            var host = await CreateHostAsync(bindings);
            try
            {
                var card = host.Window.GetVisualDescendants()
                    .OfType<Border>()
                    .FirstOrDefault(border => string.Equals(
                        AutomationProperties.GetAutomationId(border),
                        "Chat.FileTransfer.Card",
                        StringComparison.Ordinal));

                Assert.NotNull(card);

                card!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(InputElement.PointerReleasedEvent, card));

                var openedPath = await opened.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(Path.GetFullPath(savedDirectoryPath), openedPath);
            }
            finally
            {
                ChatView.OpenDirectoryOverrideForTests = null;
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

        return await CreateHostAsync(bindings);
    }

    private static async Task<ChatViewHost> CreateHostAsync(TestChatPanelBindings bindings)
    {
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

        public bool ShowChatNotice { get; init; }

        public string ChatNoticeText { get; init; } = string.Empty;

        public ObservableCollection<ChatLineViewModel> ChatMessages { get; } = [];

        public bool HasChatMessages => ChatMessages.Count > 0;

        public bool ShowNoMessagesPlaceholder => !HasChatMessages;

        public string ChatDraft { get; set; } = string.Empty;

        public bool IsChatInputEnabled => true;

        public bool ShowSendFileAction { get; init; }

        public bool CanSendFileAction { get; init; }

        public FileTransferPanelItemViewModel? InboundFileTransfer { get; set; }

        public FileTransferPanelItemViewModel? OutboundFileTransfer { get; set; }

        public bool CanEndSession => true;

        public IRelayCommand SendFileCommand { get; init; } = new RelayCommand(() => { });

        public IAsyncRelayCommand SendChatCommand { get; } = new AsyncRelayCommand(() => Task.CompletedTask);

        public IAsyncRelayCommand<string?> AcceptIncomingFileCommand { get; } =
            new AsyncRelayCommand<string?>(_ => Task.CompletedTask, _ => false);

        public IAsyncRelayCommand<string?> DeclineIncomingFileCommand { get; } =
            new AsyncRelayCommand<string?>(_ => Task.CompletedTask, _ => false);

        public IAsyncRelayCommand<string?> CancelFileTransferCommand { get; } =
            new AsyncRelayCommand<string?>(_ => Task.CompletedTask, _ => false);

        public IAsyncRelayCommand<string?> PauseFileTransferCommand { get; } =
            new AsyncRelayCommand<string?>(_ => Task.CompletedTask, _ => false);

        public IAsyncRelayCommand<string?> ResumeFileTransferCommand { get; } =
            new AsyncRelayCommand<string?>(_ => Task.CompletedTask, _ => false);

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
