using System.Threading;

namespace NLink.Core.Chat;

public static class ChatRuntimeCounters
{
    private static long chatSent;
    private static long chatReceived;
    private static long chatDecryptFailed;

    public static void IncrementSent() => Interlocked.Increment(ref chatSent);

    public static void IncrementReceived() => Interlocked.Increment(ref chatReceived);

    public static void IncrementDecryptFailed() => Interlocked.Increment(ref chatDecryptFailed);

    public static ChatRuntimeCountersSnapshot Snapshot()
    {
        return new ChatRuntimeCountersSnapshot(
            ChatSent: Interlocked.Read(ref chatSent),
            ChatReceived: Interlocked.Read(ref chatReceived),
            ChatDecryptFailed: Interlocked.Read(ref chatDecryptFailed));
    }

    public static void ResetForTests()
    {
        Interlocked.Exchange(ref chatSent, 0);
        Interlocked.Exchange(ref chatReceived, 0);
        Interlocked.Exchange(ref chatDecryptFailed, 0);
    }
}

public readonly record struct ChatRuntimeCountersSnapshot(long ChatSent, long ChatReceived, long ChatDecryptFailed);
