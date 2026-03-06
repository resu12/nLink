using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;

namespace NLink.App.ViewModels;

public interface IChatPanelBindings
{
    string ChatPanelTitle { get; }

    string ChatConnectionPillText { get; }

    bool ShowChatConnectionPill { get; }

    bool ShowChatTopBar { get; }

    bool ShowChatNotice { get; }

    string ChatNoticeText { get; }

    ObservableCollection<ChatLineViewModel> ChatMessages { get; }

    bool HasChatMessages { get; }

    bool ShowNoMessagesPlaceholder { get; }

    string ChatDraft { get; set; }

    bool IsChatInputEnabled { get; }

    bool CanEndSession { get; }

    IAsyncRelayCommand SendChatCommand { get; }

    IRelayCommand EndSessionCommand { get; }
}
