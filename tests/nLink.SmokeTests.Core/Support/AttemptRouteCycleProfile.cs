using NLink.Core.Configuration;
using NLink.Core.FileTransfer;

namespace NLink.Tests;

// A fixed fixture schedule, not route authority. Each read still goes through
// the original source/runtime/publication checks before and after waiting.
internal static class AttemptRouteCycleProfile
{
    internal const string Variable = "NLINK_TEST_FRONTIER_ROUTE_CYCLE";
    internal const string Name = "canonical-cycle-v1";
    internal const string Ineligible = "late-ineligible-v1";
    internal const string Scenario = "writer-source-read-cross-process-carrier-full-canonical-tail-late-publication";
    private static readonly Task Never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;

    internal static bool Enabled
    {
        get
        {
            if (Environment.GetEnvironmentVariable(Variable) != Name) return false;
#if !DEBUG
            throw new InvalidOperationException("route_cycle_requires_debug");
#else
            if (!OperatingSystem.IsWindows() || !ReleaseOverridePolicy.UnsafeOverridesAllowed)
                throw new InvalidOperationException("route_cycle_profile_unavailable");
            return true;
#endif
        }
    }

    internal static FileTransferRoute ExpectedRoute(int opened, bool cycle)
    {
        if (opened < 0 || opened >= (cycle ? 6 : 4)) throw new IOException("route_cycle_reader_count");
        return opened < 2 ? FileTransferRoute.RegularNknV4Fast :
            opened % 2 == 0 ? FileTransferRoute.FileTunaV4 : FileTransferRoute.PostTunaFallbackV6;
    }

    internal static long ReadLimit(int open) => open switch
    {
        2 => AttemptCanonicalTailProfile.FileBytes,
        3 => 3L * AttemptCanonicalTailProfile.ChunkBytes,
        4 => 4L * AttemptCanonicalTailProfile.ChunkBytes,
        5 => 6L * AttemptCanonicalTailProfile.ChunkBytes,
        _ => throw new IOException("route_cycle_read_stage")
    };

    internal static async ValueTask BeforeReadAsync(int open, long offset, int count, CancellationToken ct)
    {
        var limit = ReadLimit(open);
        if (offset < 0 || offset > limit || count < 0 || offset < limit && count > limit - offset)
            throw new IOException("route_cycle_read_crossing");
        // First fallback may replay the delivered prefix and finish chunk 3's repair.
        // The unchanged upper bound allows only one new chunk, not file completion.
        if (count > 0 && (open == 2 || offset == limit)) await Never.WaitAsync(ct);
    }
}
