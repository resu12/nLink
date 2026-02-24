using System;
using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using NLink.App.ViewModels;

namespace NLink.App.Views;

public partial class ChatView : UserControl
{
    private INotifyCollectionChanged? observedCollection;

    public ChatView()
    {
        InitializeComponent();
        PropertyChanged += OnViewPropertyChanged;
        AttachedToVisualTree += (_, _) => HookMessagesCollection();
        DetachedFromVisualTree += (_, _) => UnhookMessagesCollection();
    }

    private void OnViewPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == DataContextProperty)
        {
            HookMessagesCollection();
        }
    }

    private void HookMessagesCollection()
    {
        UnhookMessagesCollection();

        var collection = TryGetChatMessagesCollection();
        if (collection is null)
        {
            return;
        }

        observedCollection = collection;
        observedCollection.CollectionChanged += OnChatMessagesCollectionChanged;
        ScrollToBottomBestEffort();
    }

    private void UnhookMessagesCollection()
    {
        if (observedCollection is null)
        {
            return;
        }

        observedCollection.CollectionChanged -= OnChatMessagesCollectionChanged;
        observedCollection = null;
    }

    private void OnChatMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ScrollToBottomBestEffort();
    }

    private void ScrollToBottomBestEffort()
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                MessagesScrollViewer?.ScrollToEnd();
            }
            catch
            {
                // Best-effort only.
            }
        }, DispatcherPriority.Background);
    }

    private void ChatDraftTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        var command = (DataContext as IChatPanelBindings)?.SendChatCommand;
        if (command is null || !command.CanExecute(null))
        {
            return;
        }

        command.Execute(null);
        e.Handled = true;
    }

    private INotifyCollectionChanged? TryGetChatMessagesCollection()
    {
        if (DataContext is null)
        {
            return null;
        }

        if (DataContext is not IChatPanelBindings bindings)
        {
            return null;
        }

        return bindings.ChatMessages as INotifyCollectionChanged;
    }
}
