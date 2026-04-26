namespace NLink.Core.ScreenShare;

public sealed class ScreenShareSendSupersededException : Exception
{
    public ScreenShareSendSupersededException(string message)
        : base(message)
    {
    }
}
