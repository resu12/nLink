using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace NLink.App.Views;

internal static class WalletPasswordDialog
{
    public static Task<char[]?> ShowAsync(Window owner, string title, string acceptText)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var result = new TaskCompletionSource<char[]?>();
        var passwordBox = new TextBox
        {
            Width = 320,
            PasswordChar = '*',
        };
        var okButton = new Button
        {
            Classes = { "appButton", "primaryButton", "compactButton" },
            Content = acceptText,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var cancelButton = new Button
        {
            Classes = { "appButton", "secondaryButton", "compactButton" },
            Content = "Cancel",
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var window = new Window
        {
            Title = "Wallet password",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        Classes = { "appSectionTitle" },
                    },
                    passwordBox,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancelButton, okButton },
                    },
                },
            },
        };

        void Complete(char[]? value)
        {
            if (passwordBox.Text is not null)
            {
                passwordBox.Text = string.Empty;
            }

            if (!result.Task.IsCompleted)
            {
                result.SetResult(value);
            }

            window.Close();
        }

        okButton.Click += (_, _) =>
        {
            var text = passwordBox.Text ?? string.Empty;
            Complete(text.Length == 0 ? Array.Empty<char>() : text.ToCharArray());
        };
        cancelButton.Click += (_, _) => Complete(null);
        window.Closed += (_, _) =>
        {
            if (!result.Task.IsCompleted)
            {
                result.SetResult(null);
            }
        };

        _ = window.ShowDialog(owner);
        passwordBox.Focus();
        return result.Task;
    }
}
