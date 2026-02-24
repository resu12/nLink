namespace NLink.App.ViewModels;

public sealed class ChatLineViewModel
{
    public required string Text { get; init; }

    public required bool IsLocal { get; init; }

    public string DisplayText => (IsLocal ? "You: " : "Them: ") + Text;
}
