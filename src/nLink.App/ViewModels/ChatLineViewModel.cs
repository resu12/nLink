using NLink.App.Configuration;

namespace NLink.App.ViewModels;

public sealed class ChatLineViewModel
{
    private const int MaxDisplayCharacters = 4000;

    public required string Text { get; init; }

    public required bool IsLocal { get; init; }

    public string SenderLabel => IsLocal ? "You" : "Them";

    public bool IsRemote => !IsLocal;

    public string DisplayText
    {
        get
        {
            if (!FeatureFlags.EnableChatHardening || Text.Length <= MaxDisplayCharacters)
            {
                return Text;
            }

            return Text[..MaxDisplayCharacters];
        }
    }
}
