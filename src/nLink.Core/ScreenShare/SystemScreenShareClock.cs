namespace NLink.Core.ScreenShare;

public sealed class SystemScreenShareClock : IScreenShareClock
{
    public static SystemScreenShareClock Instance { get; } = new();

    private SystemScreenShareClock()
    {
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
