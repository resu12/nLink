using System;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NLink.App.Configuration;
using NLink.App.ViewModels;

namespace NLink.App.Views;

public partial class ChatView : UserControl
{
    private const double StickyBottomThreshold = 32d;

    private INotifyCollectionChanged? observedCollection;
    private bool isNearBottom = true;
    private bool scrollToEndQueued;
    private bool forceScrollToEndQueued;

    public ChatView()
    {
        InitializeComponent();
        if (ChatInputTextBox is not null)
        {
            // Handle Enter before TextBox AcceptsReturn consumes it, while still letting
            // Shift+Enter pass through for newline insertion.
            ChatInputTextBox.AddHandler(
                InputElement.KeyDownEvent,
                ChatDraftTextBox_KeyDown,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
        }
        PropertyChanged += OnViewPropertyChanged;
        AttachedToVisualTree += (_, _) =>
        {
            HookScrollViewer();
            HookMessagesCollection();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            UnhookMessagesCollection();
            UnhookScrollViewer();
        };
    }

    public bool ShowInlineEndSession => !FeatureFlags.EnableSessionHeader;

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
        QueueScrollToEnd(force: true);
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
        QueueScrollToEnd(force: false);
    }

    private void HookScrollViewer()
    {
        if (MessagesScrollViewer is null)
        {
            return;
        }

        MessagesScrollViewer.PropertyChanged -= OnMessagesScrollViewerPropertyChanged;
        MessagesScrollViewer.PropertyChanged += OnMessagesScrollViewerPropertyChanged;
        UpdateIsNearBottom();
    }

    private void UnhookScrollViewer()
    {
        if (MessagesScrollViewer is null)
        {
            return;
        }

        MessagesScrollViewer.PropertyChanged -= OnMessagesScrollViewerPropertyChanged;
        scrollToEndQueued = false;
        forceScrollToEndQueued = false;
    }

    private void OnMessagesScrollViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ScrollViewer.OffsetProperty ||
            e.Property == ScrollViewer.ExtentProperty ||
            e.Property == ScrollViewer.ViewportProperty)
        {
            UpdateIsNearBottom();
        }
    }

    private void UpdateIsNearBottom()
    {
        if (MessagesScrollViewer is null)
        {
            isNearBottom = true;
            return;
        }

        var extentHeight = MessagesScrollViewer.Extent.Height;
        var viewportHeight = MessagesScrollViewer.Viewport.Height;
        var offsetY = MessagesScrollViewer.Offset.Y;
        if (extentHeight <= 0 || viewportHeight <= 0)
        {
            isNearBottom = true;
            return;
        }

        var remaining = Math.Max(0d, extentHeight - viewportHeight - offsetY);
        isNearBottom = remaining <= StickyBottomThreshold;
    }

    private void QueueScrollToEnd(bool force)
    {
        forceScrollToEndQueued |= force;
        if (scrollToEndQueued)
        {
            return;
        }

        scrollToEndQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            var forceNow = forceScrollToEndQueued;
            scrollToEndQueued = false;
            forceScrollToEndQueued = false;
            if (!forceNow && !isNearBottom)
            {
                return;
            }

            try
            {
                MessagesScrollViewer?.ScrollToEnd();
                UpdateIsNearBottom();
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

        if ((e.KeyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift)
        {
            return;
        }

        // Plain Enter is always reserved for sending. If send is currently unavailable,
        // suppress newline insertion rather than treating Enter like Shift+Enter.
        e.Handled = true;

        var command = (DataContext as IChatPanelBindings)?.SendChatCommand;
        if (command is null || !command.CanExecute(null))
        {
            return;
        }

        command.Execute(null);
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
