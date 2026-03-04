using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NLink.App.Services;
using NLink.App.ViewModels;
using NLink.Core;

namespace NLink.App.Views;

public partial class HelperPageView : UserControl
{
    private const string HelperCodeInputElementName = "HelperCodeInputBox";
    private HelperPageViewModel? currentViewModel;
    private bool normalizingHelperCodeInput;

    public HelperPageView()
    {
        InitializeComponent();
        PropertyChanged += OnViewPropertyChanged;
        AttachedToVisualTree += (_, _) =>
        {
            BindClipboardTopLevel();
            ScheduleFocusHelperCodeInput();
        };
        SyncViewModelSubscription();
    }

    private void OnViewPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == DataContextProperty)
        {
            SyncViewModelSubscription();
        }
    }

    private void SyncViewModelSubscription()
    {
        if (currentViewModel is not null)
        {
            currentViewModel.SendFileRequested -= OnSendFileRequested;
        }

        currentViewModel = DataContext as HelperPageViewModel;

        if (currentViewModel is not null)
        {
            currentViewModel.SendFileRequested += OnSendFileRequested;
        }

        ScheduleFocusHelperCodeInput();
    }

    private void OnSendFileRequested(object? sender, EventArgs e)
    {
        try
        {
            ShowSendFileWindow();
        }
        catch
        {
            var errorWindow = new Window
            {
                Title = "Send file",
                Width = 680,
                Height = 260,
                Background = Brushes.Black,
                Content = new TextBlock
                {
                    Text = "Could not open the send file screen." + Environment.NewLine +
                           "Please open https://nftp.nkn.org in your browser.",
                    Foreground = Brushes.White,
                    Margin = new Thickness(16),
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            if (TopLevel.GetTopLevel(this) is Window owner)
            {
                errorWindow.Show(owner);
                return;
            }

            errorWindow.Show();
        }
    }

    private void ChatDraftTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if (DataContext is not HelperPageViewModel vm)
        {
            return;
        }

        if (!vm.SendChatCommand.CanExecute(null))
        {
            return;
        }

        vm.SendChatCommand.Execute(null);
        e.Handled = true;
    }

    private void HelperCodeInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if ((e.KeyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift)
        {
            return;
        }

        if (DataContext is not HelperPageViewModel vm)
        {
            return;
        }

        if (!vm.ConnectCommand.CanExecute(null))
        {
            return;
        }

        vm.ConnectCommand.Execute(null);
        e.Handled = true;
    }

    private void HelperCodeInput_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (normalizingHelperCodeInput || sender is not TextBox textBox)
        {
            return;
        }

        var incoming = textBox.Text ?? string.Empty;
        var caret = textBox.CaretIndex;
        var digitsBeforeCaret = CountDigitsBeforeIndex(incoming, caret);

        var digits = SessionCode.NormalizeDigits(incoming);
        if (digits.Length > 6)
        {
            digits = digits[..6];
        }

        var formatted = SessionCode.FormatPartial(digits);
        if (string.Equals(incoming, formatted, StringComparison.Ordinal))
        {
            return;
        }

        normalizingHelperCodeInput = true;
        try
        {
            textBox.Text = formatted;
            textBox.CaretIndex = MapCaretIndexFromDigitCount(formatted, digitsBeforeCaret);
        }
        finally
        {
            normalizingHelperCodeInput = false;
        }
    }

    private void HelperCodeInput_TextInput(object? sender, TextInputEventArgs e)
    {
        if (sender is not TextBox textBox || string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        foreach (var ch in e.Text)
        {
            if (!char.IsDigit(ch))
            {
                e.Handled = true;
                return;
            }
        }

        if (!WouldExceedDigitLimit(textBox, e.Text))
        {
            return;
        }

        e.Handled = true;
    }

    private void ShowSendFileWindow()
    {
        var window = new SendFileWindow();

        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            window.Show(owner);
            return;
        }

        window.Show();
    }

    private void BindClipboardTopLevel()
    {
        if (Avalonia.Application.Current is not App app)
        {
            return;
        }

        if (!app.Services.TryGet<AvaloniaClipboardService>(out var service) || service is null)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this) is TopLevel topLevel)
        {
            service.SetTopLevel(topLevel);
        }
    }

    private void ScheduleFocusHelperCodeInput()
    {
        Dispatcher.UIThread.Post(TryFocusHelperCodeInput, DispatcherPriority.Loaded);
    }

    private void TryFocusHelperCodeInput()
    {
        var codeInput = this.FindControl<TextBox>(HelperCodeInputElementName);
        if (codeInput is null || !codeInput.IsVisible || !codeInput.IsEnabled)
        {
            return;
        }

        codeInput.Focus();
        codeInput.CaretIndex = 0;
    }

    private static int CountDigitsBeforeIndex(string text, int caretIndex)
    {
        var limit = Math.Clamp(caretIndex, 0, text.Length);
        var count = 0;
        for (var i = 0; i < limit; i++)
        {
            if (char.IsDigit(text[i]))
            {
                count++;
            }
        }

        return count;
    }

    private static int MapCaretIndexFromDigitCount(string formattedText, int digitCount)
    {
        if (digitCount <= 0)
        {
            return 0;
        }

        var seenDigits = 0;
        for (var i = 0; i < formattedText.Length; i++)
        {
            if (!char.IsDigit(formattedText[i]))
            {
                continue;
            }

            seenDigits++;
            if (seenDigits >= digitCount)
            {
                return i + 1;
            }
        }

        return formattedText.Length;
    }

    private static bool WouldExceedDigitLimit(TextBox textBox, string newText)
    {
        var currentText = textBox.Text ?? string.Empty;
        var selectionStart = Math.Clamp(textBox.SelectionStart, 0, currentText.Length);
        var selectionEnd = Math.Clamp(textBox.SelectionEnd, 0, currentText.Length);
        if (selectionEnd < selectionStart)
        {
            (selectionStart, selectionEnd) = (selectionEnd, selectionStart);
        }

        var selectedDigitCount = 0;
        for (var i = selectionStart; i < selectionEnd; i++)
        {
            if (char.IsDigit(currentText[i]))
            {
                selectedDigitCount++;
            }
        }

        var currentDigitCount = SessionCode.NormalizeDigits(currentText).Length;
        var incomingDigitCount = 0;
        foreach (var ch in newText)
        {
            if (char.IsDigit(ch))
            {
                incomingDigitCount++;
            }
        }

        var resultingDigitCount = currentDigitCount - selectedDigitCount + incomingDigitCount;
        return resultingDigitCount > 6;
    }
}

