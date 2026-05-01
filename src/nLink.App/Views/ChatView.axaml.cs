using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NLink.App.Configuration;
using NLink.App.ViewModels;
using NLink.Core.Logging;

namespace NLink.App.Views;

public partial class ChatView : UserControl
{
    private const double StickyBottomThreshold = 32d;
    private const string SendFileAutomationId = "Chat.SendFile";
    private const string AcceptFileTransferAutomationId = "Chat.FileTransfer.Accept";
    private const string DeclineFileTransferAutomationId = "Chat.FileTransfer.Decline";
    private const string CancelFileTransferAutomationId = "Chat.FileTransfer.Cancel";
    private const string PauseFileTransferAutomationId = "Chat.FileTransfer.Pause";
    private const string ResumeFileTransferAutomationId = "Chat.FileTransfer.Resume";

    private INotifyCollectionChanged? observedCollection;
    private INotifyPropertyChanged? observedBindings;
    private CommunityToolkit.Mvvm.Input.IAsyncRelayCommand? observedSendChatCommand;
    private bool isNearBottom = true;
    private bool scrollToEndQueued;
    private bool forceScrollToEndQueued;

    internal static Func<string, bool>? OpenDirectoryOverrideForTests { get; set; }

    public ChatView()
    {
        InitializeComponent();
        if (ChatInputTextBox is not null)
        {
            // Handle Enter before TextBox AcceptsReturn consumes it, while still letting
            // Shift+Enter pass through for newline insertion.
            ChatInputTextBox.TextChanged += ChatDraftTextBox_TextChanged;
            ChatInputTextBox.AddHandler(
                InputElement.KeyDownEvent,
                ChatDraftTextBox_KeyDown,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
        }
        AddHandler(
            InputElement.PointerPressedEvent,
            ChatActionPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        PropertyChanged += OnViewPropertyChanged;
        AttachedToVisualTree += (_, _) =>
        {
            HookScrollViewer();
            HookMessagesCollection();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            UnhookBindings();
            UnhookMessagesCollection();
            UnhookScrollViewer();
        };
        HookBindings();
        UpdateSendChatButtonState();
    }

    public bool ShowInlineEndSession => !FeatureFlags.EnableSessionHeader;

    private void OnViewPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == DataContextProperty)
        {
            HookBindings();
            HookMessagesCollection();
            UpdateSendChatButtonState();
        }
    }

    private void HookBindings()
    {
        UnhookBindings();

        observedBindings = DataContext as INotifyPropertyChanged;
        if (observedBindings is not null)
        {
            observedBindings.PropertyChanged += OnObservedBindingsPropertyChanged;
        }

        observedSendChatCommand = (DataContext as IChatPanelBindings)?.SendChatCommand;
        if (observedSendChatCommand is not null)
        {
            observedSendChatCommand.CanExecuteChanged += OnObservedSendChatCommandCanExecuteChanged;
        }
    }

    private void UnhookBindings()
    {
        if (observedBindings is not null)
        {
            observedBindings.PropertyChanged -= OnObservedBindingsPropertyChanged;
            observedBindings = null;
        }

        if (observedSendChatCommand is not null)
        {
            observedSendChatCommand.CanExecuteChanged -= OnObservedSendChatCommandCanExecuteChanged;
            observedSendChatCommand = null;
        }
    }

    private void OnObservedBindingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.PropertyName) ||
            string.Equals(e.PropertyName, nameof(IChatPanelBindings.ChatDraft), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(IChatPanelBindings.IsChatInputEnabled), StringComparison.Ordinal))
        {
            UpdateSendChatButtonState();
        }
    }

    private void OnObservedSendChatCommandCanExecuteChanged(object? sender, EventArgs e)
        => UpdateSendChatButtonState();

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

        SyncChatDraftFromTextBox();
        var command = (DataContext as IChatPanelBindings)?.SendChatCommand;
        if (command is null || !command.CanExecute(null))
        {
            return;
        }

        command.Execute(null);
        UpdateSendChatButtonState();
    }

    private void ChatDraftTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        SyncChatDraftFromTextBox();
        UpdateSendChatButtonState();
    }

    private void SendChatButton_Click(object? sender, RoutedEventArgs e)
    {
        SyncChatDraftFromTextBox();
        var command = (DataContext as IChatPanelBindings)?.SendChatCommand;
        if (command is null || !command.CanExecute(null))
        {
            UpdateSendChatButtonState();
            return;
        }

        command.Execute(null);
        UpdateSendChatButtonState();
    }

    private void SyncChatDraftFromTextBox()
    {
        if (DataContext is not IChatPanelBindings bindings ||
            ChatInputTextBox is null)
        {
            return;
        }

        var visibleText = ChatInputTextBox.Text ?? string.Empty;
        if (!string.Equals(bindings.ChatDraft, visibleText, StringComparison.Ordinal))
        {
            bindings.ChatDraft = visibleText;
        }
    }

    private void UpdateSendChatButtonState()
    {
        if (SendChatButton is null)
        {
            return;
        }

        if (DataContext is not IChatPanelBindings bindings)
        {
            SendChatButton.IsEnabled = false;
            return;
        }

        var visibleDraft = ChatInputTextBox?.Text ?? bindings.ChatDraft;
        SendChatButton.IsEnabled =
            bindings.IsChatInputEnabled &&
            !string.IsNullOrWhiteSpace(visibleDraft) &&
            bindings.SendChatCommand.CanExecute(null);
    }

    private void SendFileButton_Click(object? sender, RoutedEventArgs e)
        => ExecuteSendFileAction(e);

    private void ExecuteSendFileAction(RoutedEventArgs e)
    {
        var command = (DataContext as IChatPanelBindings)?.SendFileCommand;
        if (command is null)
        {
            LogSendFileClickIgnored("command_missing");
            return;
        }

        if (!command.CanExecute(null))
        {
            LogSendFileClickIgnored("can_execute_false");
            return;
        }

        e.Handled = true;
        command.Execute(null);
    }

    private void ChatActionPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var button = TryResolveActionButton(e.Source);
        if (button is null || !button.IsVisible || !button.IsEnabled)
        {
            return;
        }

        switch (AutomationProperties.GetAutomationId(button))
        {
            case SendFileAutomationId:
                ExecuteSendFileAction(e);
                break;
            case AcceptFileTransferAutomationId:
                ExecuteFileTransferAction(
                    button,
                    e,
                    "accept",
                    item => item.AcceptCommand,
                    bindings => bindings.AcceptIncomingFileCommand);
                break;
            case DeclineFileTransferAutomationId:
                ExecuteFileTransferAction(
                    button,
                    e,
                    "decline",
                    item => item.DeclineCommand,
                    bindings => bindings.DeclineIncomingFileCommand);
                break;
            case CancelFileTransferAutomationId:
                ExecuteFileTransferAction(
                    button,
                    e,
                    "cancel",
                    item => item.CancelCommand,
                    bindings => bindings.CancelFileTransferCommand);
                break;
            case PauseFileTransferAutomationId:
                ExecuteFileTransferAction(
                    button,
                    e,
                    "pause",
                    item => item.PauseCommand,
                    bindings => bindings.PauseFileTransferCommand);
                break;
            case ResumeFileTransferAutomationId:
                ExecuteFileTransferAction(
                    button,
                    e,
                    "resume",
                    item => item.ResumeCommand,
                    bindings => bindings.ResumeFileTransferCommand);
                break;
        }
    }

    private void AcceptFileTransferButton_Click(object? sender, RoutedEventArgs e)
    {
        ExecuteFileTransferAction(
            sender,
            e,
            "accept",
            item => item.AcceptCommand,
            bindings => bindings.AcceptIncomingFileCommand);
    }

    private void DeclineFileTransferButton_Click(object? sender, RoutedEventArgs e)
    {
        ExecuteFileTransferAction(
            sender,
            e,
            "decline",
            item => item.DeclineCommand,
            bindings => bindings.DeclineIncomingFileCommand);
    }

    private void CancelFileTransferButton_Click(object? sender, RoutedEventArgs e)
    {
        ExecuteFileTransferAction(
            sender,
            e,
            "cancel",
            item => item.CancelCommand,
            bindings => bindings.CancelFileTransferCommand);
    }

    private void PauseFileTransferButton_Click(object? sender, RoutedEventArgs e)
    {
        ExecuteFileTransferAction(
            sender,
            e,
            "pause",
            item => item.PauseCommand,
            bindings => bindings.PauseFileTransferCommand);
    }

    private void ResumeFileTransferButton_Click(object? sender, RoutedEventArgs e)
    {
        ExecuteFileTransferAction(
            sender,
            e,
            "resume",
            item => item.ResumeCommand,
            bindings => bindings.ResumeFileTransferCommand);
    }

    private void FileTransferCard_PointerReleased(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not FileTransferPanelItemViewModel item ||
            !item.ShowSavedLocation ||
            string.IsNullOrWhiteSpace(item.SavedDirectoryPath))
        {
            return;
        }

        if (TryResolveActionButton(e.Source) is not null)
        {
            return;
        }

        e.Handled = true;
        OpenSavedFileTransferDirectory(item);
    }

    private void ExecuteFileTransferAction(
        object? sender,
        RoutedEventArgs e,
        string actionName,
        Func<FileTransferPanelItemViewModel, CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<string?>?> itemCommandSelector,
        Func<IChatPanelBindings, CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<string?>> fallbackCommandSelector)
    {
        var item = (sender as Control)?.DataContext as FileTransferPanelItemViewModel;
        var transferId = item?.TransferId ?? (sender as Button)?.CommandParameter as string;
        var command = item is null ? null : itemCommandSelector(item);
        command ??= DataContext is IChatPanelBindings bindings
            ? fallbackCommandSelector(bindings)
            : null;

        if (command is null)
        {
            LogFileTransferClickIgnored(actionName, "command_missing", item, transferId);
            return;
        }

        if (!command.CanExecute(transferId))
        {
            LogFileTransferClickIgnored(actionName, "can_execute_false", item, transferId);
            return;
        }

        e.Handled = true;
        command.Execute(transferId);
    }

    private static Button? TryResolveActionButton(object? eventSource)
    {
        if (eventSource is not Visual visual)
        {
            return null;
        }

        var button = visual.FindAncestorOfType<Button>(includeSelf: true);
        if (button is null)
        {
            return null;
        }

        return AutomationProperties.GetAutomationId(button) is
            SendFileAutomationId or
            AcceptFileTransferAutomationId or
            DeclineFileTransferAutomationId or
            CancelFileTransferAutomationId or
            PauseFileTransferAutomationId or
            ResumeFileTransferAutomationId
            ? button
            : null;
    }

    private static void OpenSavedFileTransferDirectory(FileTransferPanelItemViewModel item)
    {
        var directoryPath = item.SavedDirectoryPath;
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            LogSavedLocationOpenIgnored(item, "directory_missing");
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(directoryPath);
            if (!Directory.Exists(fullPath))
            {
                LogSavedLocationOpenIgnored(item, "directory_not_found");
                return;
            }

            if (OpenDirectoryOverrideForTests?.Invoke(fullPath) == true)
            {
                LogSavedLocationOpened(item, "test_override");
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = fullPath,
                UseShellExecute = true,
            });
            LogSavedLocationOpened(item, "shell_execute");
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn(
                "ChatView",
                $"event=file_transfer_saved_location_open_failed; transfer_id_present={(string.IsNullOrWhiteSpace(item.TransferId) ? 0 : 1)}; reason={ex.GetType().Name}");
        }
    }

    private static void LogSavedLocationOpened(FileTransferPanelItemViewModel item, string reason)
    {
        LocalOperationalLog.Info(
            "ChatView",
            $"event=file_transfer_saved_location_opened; transfer_id_present={(string.IsNullOrWhiteSpace(item.TransferId) ? 0 : 1)}; reason={reason}");
    }

    private static void LogSavedLocationOpenIgnored(FileTransferPanelItemViewModel item, string reason)
    {
        LocalOperationalLog.Info(
            "ChatView",
            $"event=file_transfer_saved_location_open_ignored; transfer_id_present={(string.IsNullOrWhiteSpace(item.TransferId) ? 0 : 1)}; reason={reason}; item_state={item.State}");
    }

    private void LogFileTransferClickIgnored(
        string actionName,
        string reason,
        FileTransferPanelItemViewModel? item,
        string? transferId)
    {
        LocalOperationalLog.Info(
            "ChatView",
            $"event=file_transfer_{actionName}_ui_click_ignored; reason={reason}; " +
            $"transfer_id_present={(string.IsNullOrWhiteSpace(transferId) ? 0 : 1)}; " +
            $"item_state={item?.State.ToString() ?? "(none)"}; " +
            $"item_show_accept={(item?.ShowAccept == true ? 1 : 0)}; " +
            $"item_show_decline={(item?.ShowDecline == true ? 1 : 0)}; " +
            $"item_show_cancel={(item?.ShowCancel == true ? 1 : 0)}; " +
            $"item_show_pause={(item?.ShowPause == true ? 1 : 0)}; " +
            $"item_show_resume={(item?.ShowResume == true ? 1 : 0)}; " +
            $"root_context={DataContext?.GetType().Name ?? "(none)"}");
    }

    private void LogSendFileClickIgnored(string reason)
    {
        var bindings = DataContext as IChatPanelBindings;
        LocalOperationalLog.Info(
            "ChatView",
            $"event=file_transfer_send_ui_click_ignored; reason={reason}; " +
            $"show_send_file_action={(bindings?.ShowSendFileAction == true ? 1 : 0)}; " +
            $"can_send_file_action={(bindings?.CanSendFileAction == true ? 1 : 0)}; " +
            $"root_context={DataContext?.GetType().Name ?? "(none)"}");
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
