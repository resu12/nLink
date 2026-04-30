using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;

namespace NLink.App.ViewModels;

public interface IChatPanelBindings
{
    string ChatPanelTitle { get; }

    bool ShowChatTopBar { get; }

    bool ShowChatNotice { get; }

    string ChatNoticeText { get; }

    ObservableCollection<ChatLineViewModel> ChatMessages { get; }

    bool HasChatMessages { get; }

    bool ShowNoMessagesPlaceholder { get; }

    string ChatDraft { get; set; }

    bool IsChatInputEnabled { get; }

    bool ShowSendFileAction { get; }

    bool CanSendFileAction { get; }

    FileTransferPanelItemViewModel? InboundFileTransfer { get; }

    FileTransferPanelItemViewModel? OutboundFileTransfer { get; }

    bool CanEndSession { get; }

    IRelayCommand SendFileCommand { get; }

    IAsyncRelayCommand SendChatCommand { get; }

    IAsyncRelayCommand<string?> AcceptIncomingFileCommand { get; }

    IAsyncRelayCommand<string?> DeclineIncomingFileCommand { get; }

    IAsyncRelayCommand<string?> CancelFileTransferCommand { get; }

    IAsyncRelayCommand<string?> PauseFileTransferCommand { get; }

    IAsyncRelayCommand<string?> ResumeFileTransferCommand { get; }

    IRelayCommand EndSessionCommand { get; }
}
