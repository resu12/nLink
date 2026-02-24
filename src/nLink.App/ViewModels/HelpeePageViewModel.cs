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

public sealed class HelpeePageViewModel : ViewModelBase, IDisposable
{
    private readonly Action cancelAction;
    private readonly TransportRuntimeConfig transportConfig;
    private readonly Func<ISignalingTransport> signalingTransportFactory;
    private readonly SessionChatService chatService = new();
    private readonly IClipboardService? clipboardService;
    private readonly ShareMessageConfig shareMessageConfig;

    private SessionCode sessionCode = SessionCode.CreateRandom();
    private bool hasIncomingRequest;
    private bool isRequestAllowed;
    private bool showTroubleshooting;
    private bool showChatNotice;
    private bool showShareCopyBanner;
    private string codeCopyStatusText = "Click the code to copy it.";
    private string shareCopyBannerText = string.Empty;
    private string connectionStatus = "Waiting for your helper to connect.";
    private string connectionState = "Waiting";
    private string chatDraft = string.Empty;
    private CancellationTokenSource? hostCts;
    private ISignalingTransport? hostTransport;
    private IncomingJoinRequestEventArgs? pendingJoinRequest;
    private SessionReliabilityAttempt? reliabilityAttempt;
    private bool disposed;

    public HelpeePageViewModel(
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

        RegenerateCodeCommand = new RelayCommand(RegenerateCode);
        ShareInviteCommand = new AsyncRelayCommand(ShareInviteAsync);
        SimulateIncomingRequestCommand = new RelayCommand(SimulateIncomingRequest);
        ToggleTroubleshootingCommand = new RelayCommand(ToggleTroubleshooting);
        AllowCommand = new RelayCommand(AllowIncomingRequest, CanAllowIncomingRequest);
        SendChatCommand = new AsyncRelayCommand(SendChatAsync, CanSendChat);
        CancelCommand = new RelayCommand(CancelAndGoBack);

        StartHosting();
    }

    public string PageTitle => "I need help";

    public string PageSubtitle => "Share the code below with the person helping you.";

    public string ShareCode => sessionCode.Digits;

    public string IncomingHelperName => "Helper on this PC";

    public string CodeCopyStatusText
    {
        get => codeCopyStatusText;
        private set => SetProperty(ref codeCopyStatusText, value);
    }

    public bool ShowShareCopyBanner
    {
        get => showShareCopyBanner;
        private set => SetProperty(ref showShareCopyBanner, value);
    }

    public string ShareCopyBannerText
    {
        get => shareCopyBannerText;
        private set => SetProperty(ref shareCopyBannerText, value);
    }

    public bool HasIncomingRequest
    {
        get => hasIncomingRequest;
        private set
        {
            if (SetProperty(ref hasIncomingRequest, value))
            {
                OnPropertyChanged(nameof(ShowPreConnectActions));
                AllowCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsRequestAllowed
    {
        get => isRequestAllowed;
        private set
        {
            if (SetProperty(ref isRequestAllowed, value))
            {
                AllowCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool ShowTroubleshooting
    {
        get => showTroubleshooting;
        private set => SetProperty(ref showTroubleshooting, value);
    }

    public bool ShowDevTroubleshooting => transportConfig.IsDevLocal;

    public bool ShowPreConnectActions => !HasIncomingRequest;

    public string ConnectionStatus
    {
        get => connectionStatus;
        private set => SetProperty(ref connectionStatus, value);
    }

    public string ConnectionState
    {
        get => connectionState;
        private set => SetProperty(ref connectionState, value);
    }

    public ObservableCollection<ChatLineViewModel> ChatMessages { get; }

    public string ChatPanelTitle => "Message";

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

    public bool IsChatReady => chatService.CanSend;

    public bool ShowChatNotice
    {
        get => showChatNotice;
        private set => SetProperty(ref showChatNotice, value);
    }

    public string ChatNoticeText => "You received a message";

    public IRelayCommand RegenerateCodeCommand { get; }

    public IAsyncRelayCommand ShareInviteCommand { get; }

    public IRelayCommand SimulateIncomingRequestCommand { get; }

    public IRelayCommand ToggleTroubleshootingCommand { get; }

    public RelayCommand AllowCommand { get; }

    public IAsyncRelayCommand SendChatCommand { get; }

    public IRelayCommand CancelCommand { get; }

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

        if (hostTransport is not null)
        {
            hostTransport.IncomingJoinRequest -= OnIncomingJoinRequest;
            hostTransport.Disconnected -= OnTransportDisconnected;
            DisposeTransportInBackground(hostTransport);
            hostTransport = null;
        }

        hostCts?.Cancel();
        hostCts?.Dispose();
        hostCts = null;
    }

    private void RegenerateCode()
    {
        sessionCode = SessionCode.CreateRandom();
        OnPropertyChanged(nameof(ShareCode));

        pendingJoinRequest = null;
        HasIncomingRequest = false;
        IsRequestAllowed = false;
        ShowChatNotice = false;
        ChatDraft = string.Empty;
        ChatMessages.Clear();
        CodeCopyStatusText = "Click the code to copy it.";
        ShowShareCopyBanner = false;
        ShareCopyBannerText = string.Empty;
        ConnectionStatus = "Waiting for your helper to connect.";
        ConnectionState = "Waiting";

        StartHosting();
    }

    private void SimulateIncomingRequest()
    {
        pendingJoinRequest = null;
        HasIncomingRequest = true;
        IsRequestAllowed = false;
        ConnectionStatus = "Helper on this PC wants to connect. Click Allow.";
        ConnectionState = "IncomingRequest";
    }

    private void ToggleTroubleshooting()
    {
        ShowTroubleshooting = !ShowTroubleshooting;
    }

    public void NotifyCodeCopied()
    {
        CodeCopyStatusText = $"Copied code: {ShareCode}";
        ShowShareCopyBanner = false;
    }

    public void NotifyCodeCopyFailed()
    {
        CodeCopyStatusText = "Could not copy the code. Please read it to your helper.";
        ShowShareCopyBanner = false;
    }

    private async Task ShareInviteAsync()
    {
        if (clipboardService is null)
        {
            ShareCopyBannerText = "Could not copy. Please read the code to your helper.";
            ShowShareCopyBanner = true;
            return;
        }

        try
        {
            var message = ShareMessageBuilder.BuildInstallMessage(ShareCode, shareMessageConfig.DownloadUrl);
            await clipboardService.SetTextAsync(message);
            ShareCopyBannerText = "Copied. Paste it into your chat.";
            ShowShareCopyBanner = true;
        }
        catch
        {
            ShareCopyBannerText = "Could not copy. Please try again.";
            ShowShareCopyBanner = true;
        }
    }

    private bool CanAllowIncomingRequest()
    {
        return HasIncomingRequest && !IsRequestAllowed;
    }

    private bool CanSendChat()
    {
        return !string.IsNullOrWhiteSpace(ChatDraft) && chatService.CanSend;
    }

    private void AllowIncomingRequest()
    {
        if (!CanAllowIncomingRequest())
        {
            return;
        }

        var request = pendingJoinRequest;
        pendingJoinRequest = null;

        HasIncomingRequest = false;
        IsRequestAllowed = true;
        ShowChatNotice = false;
        ConnectionStatus = transportConfig.AllowStatusText;
        ConnectionState = "Connected";
        LogReliability(SessionReliabilityStage.Approved);
        LogReliability(SessionReliabilityStage.Completed);

        if (request is not null)
        {
            _ = CompleteJoinRequestAsync(request);
        }
    }

    private async Task SendChatAsync()
    {
        var sent = await chatService.TrySendTextAsync(ChatDraft, CancellationToken.None);
        if (sent is null)
        {
            return;
        }

        ChatDraft = string.Empty;
        ShowChatNotice = false;
        AddChatLine(sent.Value.Text, isLocal: true);
    }

    private void CancelAndGoBack()
    {
        cancelAction();
    }

    private void StartHosting()
    {
        hostCts?.Cancel();
        hostCts?.Dispose();
        hostCts = new CancellationTokenSource();
        reliabilityAttempt = SessionReliabilityLog.StartAttempt("Helpee", transportConfig.Key);
        chatService.SetReliabilityAttempt(reliabilityAttempt);
        LogReliability(SessionReliabilityStage.CodeGenerated);
        LogReliability(SessionReliabilityStage.DiscoveryStarted);

        chatService.DetachTransport();

        if (hostTransport is not null)
        {
            hostTransport.IncomingJoinRequest -= OnIncomingJoinRequest;
            hostTransport.Disconnected -= OnTransportDisconnected;
            DisposeTransportInBackground(hostTransport);
        }

        hostTransport = signalingTransportFactory();
        chatService.AttachTransport(hostTransport);
        hostTransport.IncomingJoinRequest += OnIncomingJoinRequest;
        hostTransport.Disconnected += OnTransportDisconnected;

        AppLog.Info($"Helpee hosting using {transportConfig.Key} with code {sessionCode.Digits}");

        _ = RunHostAsync(hostTransport, sessionCode, hostCts.Token);
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

    private async Task RunHostAsync(ISignalingTransport transport, SessionCode code, CancellationToken ct)
    {
        try
        {
            await transport.HostAsync(code, ct);
        }
        catch (OperationCanceledException)
        {
            // Normal when generating a new code or leaving the page.
        }
        catch
        {
            if (disposed || ct.IsCancellationRequested || !ReferenceEquals(hostTransport, transport))
            {
                return;
            }

            await UiThreadDispatch.RunAsync(() =>
            {
                if (!HasIncomingRequest && !IsRequestAllowed)
                {
                    ConnectionStatus = "Could not start. Try a new code.";
                    ConnectionState = "Disconnected";
                }
            });
        }
    }

    private void OnIncomingJoinRequest(object? sender, IncomingJoinRequestEventArgs e)
    {
        if (disposed)
        {
            _ = e.RejectAsync();
            return;
        }

        if (HasIncomingRequest)
        {
            _ = e.RejectAsync();
            return;
        }

        pendingJoinRequest = e;
        LogReliability(SessionReliabilityStage.IncomingJoinRequest);

        _ = UiThreadDispatch.RunAsync(() =>
        {
            HasIncomingRequest = true;
            IsRequestAllowed = false;
            ConnectionStatus = "Helper on this PC wants to connect. Click Allow.";
            ConnectionState = "IncomingRequest";
        });
    }

    private void OnTransportDisconnected(object? sender, EventArgs e)
    {
        if (disposed || hostCts?.IsCancellationRequested == true)
        {
            return;
        }

        _ = UiThreadDispatch.RunAsync(() =>
        {
            if (!HasIncomingRequest && !IsRequestAllowed)
            {
                ConnectionStatus = transportConfig.HelpeeDisconnectedText;
                var (errorCode, errorHint) = GetReliabilityError();
                LogReliability(SessionReliabilityStage.Disconnected, errorCode, errorHint);
                if (ConnectionState != "Connected")
                {
                    ConnectionState = "Disconnected";
                }
            }
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
    }

    private static async Task CompleteJoinRequestAsync(IncomingJoinRequestEventArgs request)
    {
        try
        {
            await request.ApproveAsync(CancellationToken.None);
        }
        catch
        {
            // UI state already reflects approval.
        }
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

        return (lastError, "The connection stopped. Try a new code.");
    }
}
