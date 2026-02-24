using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using NLink.App.Configuration;
using NLink.App.Services;
using NLink.App.Threading;
using NLink.Core;
using NLink.Core.Chat;
using NLink.Infra.Nkn;

namespace NLink.App.ViewModels;

public sealed class HelperPageViewModel : ViewModelBase, IDisposable
{
    private readonly Action cancelAction;
    private readonly TransportRuntimeConfig transportConfig;
    private readonly Func<ISignalingTransport> signalingTransportFactory;
    private readonly SessionChatService chatService = new();
    private readonly IClipboardService? clipboardService;
    private readonly ShareMessageConfig shareMessageConfig;

    private string codeInput = string.Empty;
    private string statusText = string.Empty;
    private string connectionState = "Idle";
    private string chatDraft = string.Empty;
    private bool isConnecting;
    private bool showChatNotice;
    private CancellationTokenSource? connectCts;
    private ISignalingTransport? joinTransport;
    private SessionReliabilityAttempt? reliabilityAttempt;
    private bool disposed;

    public HelperPageViewModel(
        Action cancelAction,
        TransportRuntimeConfig transportConfig,
        IClipboardService? clipboardService = null,
        ShareMessageConfig? shareMessageConfig = null)
    {
        this.cancelAction = cancelAction;
        this.transportConfig = transportConfig;
        this.clipboardService = clipboardService;
        this.shareMessageConfig = shareMessageConfig ?? new ShareMessageConfig(null);
        signalingTransportFactory = transportConfig.CreateTransport;

        ChatMessages = new ObservableCollection<ChatLineViewModel>();

        chatService.MessageReceived += OnChatMessageReceived;
        chatService.MessageReceivedBeforeApproved += OnChatMessageReceivedBeforeApproved;
        chatService.StateChanged += OnChatStateChanged;

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, CanConnect);
        ShowShareOptionsCommand = new RelayCommand(RequestShareOptionsDialog);
        CopyInstallMessageCommand = new AsyncRelayCommand(CopyInstallMessageAsync);
        SendFileCommand = new RelayCommand(RequestSendFileWindow);
        SendChatCommand = new AsyncRelayCommand(SendChatAsync, CanSendChat);
        CancelCommand = new RelayCommand(CancelAndGoBack);
    }

    public string PageTitle => "I want to help someone";

    public string CodeInput
    {
        get => codeInput;
        set
        {
            var formatted = SessionCode.FormatPartial(value);
            if (SetProperty(ref codeInput, formatted))
            {
                ConnectCommand.NotifyCanExecuteChanged();
                SendChatCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(ShowChatPanel));
                OnPropertyChanged(nameof(ShowChatConnectionHint));
            }
        }
    }

    public string StatusText
    {
        get => statusText;
        private set
        {
            if (SetProperty(ref statusText, value))
            {
                OnPropertyChanged(nameof(ShowStatusText));
            }
        }
    }

    public string ConnectionState
    {
        get => connectionState;
        private set => SetProperty(ref connectionState, value);
    }

    public bool IsConnecting
    {
        get => isConnecting;
        private set
        {
            if (SetProperty(ref isConnecting, value))
            {
                ConnectCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string HelperPageHelpText => "Ask them for the 6-digit code on their screen.";

    public string ConnectionMethodHint => transportConfig.HelperHintText;

    public ObservableCollection<ChatLineViewModel> ChatMessages { get; }

    public bool ShowChatPanel => SessionCode.TryParse(CodeInput, out _) || ChatMessages.Count > 0;

    public bool ShowStatusText => !string.IsNullOrWhiteSpace(StatusText);

    public string ChatPanelTitle => "Message";

    public bool ShowChatConnectionHint => ShowChatPanel && ConnectionState != "Connected";

    public string ChatConnectionHintText => "Waiting for connection...";

    public string ChatDraft
    {
        get => chatDraft;
        set
        {
            if (SetProperty(ref chatDraft, value))
            {
                SendChatCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool ShowChatNotice
    {
        get => showChatNotice;
        private set => SetProperty(ref showChatNotice, value);
    }

    public string ChatNoticeText => "You received a message";

    public bool IsChatReady => chatService.CanSend;

    public IAsyncRelayCommand ConnectCommand { get; }

    public IRelayCommand ShowShareOptionsCommand { get; }

    public IAsyncRelayCommand CopyInstallMessageCommand { get; }

    public IRelayCommand SendFileCommand { get; }

    public IAsyncRelayCommand SendChatCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public event EventHandler? SendFileRequested;

    public event EventHandler? ShareOptionsRequested;

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        chatService.MessageReceived -= OnChatMessageReceived;
        chatService.MessageReceivedBeforeApproved -= OnChatMessageReceivedBeforeApproved;
        chatService.StateChanged -= OnChatStateChanged;
        chatService.SetReliabilityAttempt(null);
        chatService.Dispose();

        CleanupJoinTransport();

        connectCts?.Cancel();
        connectCts?.Dispose();
        connectCts = null;
    }

    private bool CanConnect()
    {
        return !IsConnecting && SessionCode.TryParse(CodeInput, out _);
    }

    private bool CanSendChat()
    {
        return !string.IsNullOrWhiteSpace(ChatDraft) && chatService.CanSend;
    }

    private async Task ConnectAsync()
    {
        if (!SessionCode.TryParse(CodeInput, out var code))
        {
            StatusText = "Enter a valid 6-digit code.";
            ConnectionState = "InvalidCode";
            OnPropertyChanged(nameof(ShowChatConnectionHint));
            return;
        }

        connectCts?.Cancel();
        connectCts?.Dispose();
        connectCts = new CancellationTokenSource();
        reliabilityAttempt = SessionReliabilityLog.StartAttempt("Helper", transportConfig.Key);
        chatService.SetReliabilityAttempt(reliabilityAttempt);
        LogReliability(SessionReliabilityStage.DiscoveryStarted);

        CleanupJoinTransport();
        joinTransport = signalingTransportFactory();
        chatService.AttachTransport(joinTransport);
        joinTransport.Approved += OnApproved;
        joinTransport.Rejected += OnRejected;
        joinTransport.Disconnected += OnDisconnected;

        AppLog.Info($"Helper join requested using {transportConfig.Key} with code {code.Digits}");

        IsConnecting = true;
        StatusText = "Waiting for permission...";
        ConnectionState = "Connecting";
        OnPropertyChanged(nameof(ShowChatConnectionHint));

        try
        {
            await joinTransport.JoinAsync(code, connectCts.Token);
            // NKN transport logs these stages after JoinRequest Ack to avoid optimistic duplicates.
            if (!string.Equals(transportConfig.Key, "NKN", StringComparison.OrdinalIgnoreCase))
            {
                LogReliability(SessionReliabilityStage.DiscoveryFoundHost);
                LogReliability(SessionReliabilityStage.JoinRequestSent);
            }
        }
        catch (OperationCanceledException)
        {
            // User navigated away or a new connect attempt replaced this one.
        }
        catch (TimeoutException)
        {
            LogReliability(SessionReliabilityStage.DiscoveryTimeout, "timeout", "Could not find that code. Ask them to try a new code.");
            StatusText = "Could not find that code. Ask them to try a new code.";
            if (ConnectionState != "Connected")
            {
                ConnectionState = "Disconnected";
            }
            OnPropertyChanged(nameof(ShowChatConnectionHint));
        }
        catch (Exception)
        {
            var (errorCode, errorHint) = GetReliabilityError();
            LogReliability(SessionReliabilityStage.Disconnected, errorCode, errorHint);
            StatusText = "Could not connect. Please try again.";
            if (ConnectionState != "Connected")
            {
                ConnectionState = "Disconnected";
            }
            OnPropertyChanged(nameof(ShowChatConnectionHint));
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private async Task SendChatAsync()
    {
        if (disposed)
        {
            return;
        }

        if (!chatService.CanSend && !IsConnecting && SessionCode.TryParse(CodeInput, out _))
        {
            await ConnectAsync();
        }

        var message = await chatService.TrySendTextAsync(ChatDraft, CancellationToken.None);
        if (message is null)
        {
            return;
        }

        ChatDraft = string.Empty;
        ShowChatNotice = false;
        AddChatLine(message.Value.Text, isLocal: true);
    }

    private void CancelAndGoBack()
    {
        cancelAction();
    }

    private void RequestSendFileWindow()
    {
        SendFileRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RequestShareOptionsDialog()
    {
        ShareOptionsRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task CopyInstallMessageAsync()
    {
        if (clipboardService is null)
        {
            return;
        }

        var text = ShareMessageBuilder.BuildInstallMessage(code: null, shareMessageConfig.DownloadUrl);
        await clipboardService.SetTextAsync(text);
    }

    private void CleanupJoinTransport()
    {
        chatService.DetachTransport();

        if (joinTransport is null)
        {
            return;
        }

        joinTransport.Approved -= OnApproved;
        joinTransport.Rejected -= OnRejected;
        joinTransport.Disconnected -= OnDisconnected;
        DisposeTransportInBackground(joinTransport);
        joinTransport = null;
    }

    private static void DisposeTransportInBackground(ISignalingTransport transport)
    {
        _ = Task.Run(() =>
        {
            try
            {
                transport.Dispose();
            }
            catch
            {
                // Best-effort cleanup. UI should not block on transport shutdown.
            }
        });
    }

    private void OnApproved(object? sender, EventArgs e)
    {
        _ = UiThreadDispatch.RunAsync(() =>
        {
            StatusText = transportConfig.ApprovedStatusText;
            ConnectionState = "Connected";
            ShowChatNotice = false;
            LogReliability(SessionReliabilityStage.Approved);
            LogReliability(SessionReliabilityStage.Completed);
            OnPropertyChanged(nameof(ShowChatConnectionHint));
        });
    }

    private void OnRejected(object? sender, EventArgs e)
    {
        _ = UiThreadDispatch.RunAsync(() =>
        {
            StatusText = "Permission was declined.";
            ConnectionState = "Rejected";
            LogReliability(SessionReliabilityStage.Rejected, "rejected", "They did not allow the connection.");
            OnPropertyChanged(nameof(ShowChatConnectionHint));
        });
    }

    private void OnDisconnected(object? sender, EventArgs e)
    {
        if (disposed || connectCts?.IsCancellationRequested == true)
        {
            return;
        }

        _ = UiThreadDispatch.RunAsync(() =>
        {
            StatusText = transportConfig.HelperDisconnectedText;
            var (errorCode, errorHint) = GetReliabilityError();
            LogReliability(SessionReliabilityStage.Disconnected, errorCode, errorHint);
            if (ConnectionState != "Connected")
            {
                ConnectionState = "Disconnected";
            }
            OnPropertyChanged(nameof(ShowChatConnectionHint));
        });
    }

    private void OnChatMessageReceived(object? sender, ChatMessageEventArgs e)
    {
        _ = UiThreadDispatch.RunAsync(() =>
        {
            AddChatLine(e.Message.Text, isLocal: false);
        });
    }

    private void OnChatMessageReceivedBeforeApproved(object? sender, EventArgs e)
    {
        _ = UiThreadDispatch.RunAsync(() =>
        {
            if (ConnectionState != "Connected")
            {
                ShowChatNotice = true;
            }
        });
    }

    private void OnChatStateChanged(object? sender, EventArgs e)
    {
        _ = UiThreadDispatch.RunAsync(() =>
        {
            OnPropertyChanged(nameof(IsChatReady));
            SendChatCommand.NotifyCanExecuteChanged();
        });
    }

    private void AddChatLine(string text, bool isLocal)
    {
        ChatMessages.Add(new ChatLineViewModel
        {
            Text = text,
            IsLocal = isLocal,
        });

        while (ChatMessages.Count > 100)
        {
            ChatMessages.RemoveAt(0);
        }

        OnPropertyChanged(nameof(ShowChatPanel));
        OnPropertyChanged(nameof(ShowChatConnectionHint));
    }

    private void LogReliability(SessionReliabilityStage stage, string? errorCode = null, string? errorHint = null)
    {
        if (reliabilityAttempt is null)
        {
            return;
        }

        SessionReliabilityLog.RecordStage(reliabilityAttempt, stage, errorCode, errorHint);
    }

    private (string? Code, string? Hint) GetReliabilityError()
    {
        if (!string.Equals(transportConfig.Key, "NKN", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        var lastError = NknRuntimeDiagnostics.Snapshot().LastError;
        if (string.IsNullOrWhiteSpace(lastError) || string.Equals(lastError, "(none)", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        return (lastError, "Connection did not complete. Try again or use a new code.");
    }
}
