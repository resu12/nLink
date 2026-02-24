using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;

namespace NLink.App.ViewModels;

public interface IChatPanelBindings
{
    string ChatPanelTitle { get; }

    bool ShowChatNotice { get; }

    string ChatNoticeText { get; }

    ObservableCollection<ChatLineViewModel> ChatMessages { get; }

    string ChatDraft { get; set; }

    IAsyncRelayCommand SendChatCommand { get; }
}
