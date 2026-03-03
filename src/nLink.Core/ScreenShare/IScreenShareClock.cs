namespace NLink.Core.ScreenShare;

public interface IScreenShareClock
{
    DateTimeOffset UtcNow { get; }
}
