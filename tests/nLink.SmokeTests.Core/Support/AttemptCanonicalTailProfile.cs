using NLink.Core.Configuration;

namespace NLink.Tests;

// Test-only, fixed canonical suffix. Arming is evidence about an actual read,
// not proof that cached/batched sends have the required eight-frame geometry.
internal sealed class AttemptCanonicalTailProfile : IDisposable
{
    internal const string Name = "canonical-tail-late-publication";
    internal const long FileBytes = 268435456;
    internal const int ChunkBytes = 21504, ChunkCount = 12484, FirstChunk = ChunkCount - 8;
    internal const int PrearmChunk = FirstChunk - 3;
    internal const long PrearmOffset = (long)PrearmChunk * ChunkBytes;
    internal const long PrefixBytes = (long)FirstChunk * ChunkBytes;
    internal const long CommittedAfterWrite = (long)(FirstChunk + 4) * ChunkBytes;
    private static readonly object gate = new();
    private static AttemptCanonicalTailProfile? owner;
    private readonly string? previous;
    private bool armed, disposed;
    private string? failure;
    private Task? prefix;
    private Action? checkPrefix;
    private readonly CancellationTokenSource lifetime = new();
    private bool tailEntered, prefixConsumed;

    internal AttemptCanonicalTailProfile(long fileBytes, int chunkBytes, int chunkCount)
    {
        RequireSupported();
        RequireGeometry(fileBytes, chunkBytes, chunkCount);
        lock (gate)
        {
            if (owner is not null) throw new IOException("canonical_tail_profile_already_owned");
            previous = Environment.GetEnvironmentVariable(AttemptSingleSegmentProfile.Variable);
            if (previous is not null and not "3") throw new IOException("canonical_tail_requires_default_batching");
            owner = this;
        }
    }

    internal static void RequireSupported()
    {
#if !DEBUG
        throw new InvalidOperationException("canonical_tail_requires_debug");
#else
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("canonical_tail_requires_windows");
        if (!ReleaseOverridePolicy.UnsafeOverridesAllowed) throw new InvalidOperationException("canonical_tail_override_unavailable");
#endif
    }

    internal static void RequireGeometry(long fileBytes, int chunkBytes, int chunkCount)
    {
        if (fileBytes != FileBytes || chunkBytes != ChunkBytes || chunkCount != ChunkCount)
            throw new IOException("canonical_tail_geometry_mismatch");
    }

    // Called only by the fourth retained runtime reader, immediately before
    // its real read. Seeking and cache hits alone must never call this hook.
    internal void BeforeRead(long offset, CancellationToken ct)
    {
        lock (gate)
        {
            ct.ThrowIfCancellationRequested();
            Check();
            if (offset < 0 || offset > FileBytes) throw new IOException("canonical_tail_read_offset");
            if (!armed && offset >= PrearmOffset && offset < FileBytes)
            {
                Environment.SetEnvironmentVariable(AttemptSingleSegmentProfile.Variable, "1");
                armed = true;
                Console.WriteLine("carrier-canonical-tail=armed; read_offset=" + offset + "; first_chunk=" + FirstChunk);
            }
        }
    }

    internal void Check()
    {
        lock (gate)
        {
            if (disposed || !ReferenceEquals(owner, this)) throw new ObjectDisposedException(nameof(AttemptCanonicalTailProfile));
            if (Environment.GetEnvironmentVariable(AttemptSingleSegmentProfile.Variable) != (armed ? "1" : previous))
                failure ??= "canonical_tail_batch_setting_changed";
            if (failure is not null) throw new IOException(failure);
        }
    }

    internal void RequireArmed()
    {
        lock (gate)
        {
            Check();
            if (!armed) throw new IOException("canonical_tail_read_not_armed");
        }
    }

    internal void BindPrefix(Task handoff, Action check)
    {
        lock (gate)
        {
            Check();
            if (prefix is not null || tailEntered) throw new IOException("canonical_prefix_already_bound");
            prefix = handoff;
            checkPrefix = check;
        }
    }

    internal async ValueTask BeforeTailReadAsync(long offset, CancellationToken ct, Action checkSource)
    {
        if (offset < PrefixBytes || offset == FileBytes) return;
        Task wait;
        Action check;
        lock (gate)
        {
            Check();
            if (prefixConsumed) return;
            if (tailEntered || offset != PrefixBytes || prefix is null)
                throw new IOException(failure ??= "canonical_prefix_read_entry_invalid");
            tailEntered = true;
            wait = prefix;
            check = checkPrefix!;
        }
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        try
        {
            await wait.WaitAsync(pending.Token);
            pending.Token.ThrowIfCancellationRequested();
            check(); // No profile/service/scheduler locks are held across the wait.
            checkSource();
            lock (gate)
            {
                pending.Token.ThrowIfCancellationRequested();
                Check();
                prefixConsumed = true;
            }
            Console.WriteLine("carrier-canonical-prefix=consumed; next=" + FirstChunk + "; bytes=" + PrefixBytes);
        }
        catch
        {
            lock (gate) failure ??= "canonical_prefix_wait_failed";
            throw;
        }
    }

    internal void RequirePrefixConsumed()
    {
        lock (gate)
        {
            RequireArmed();
            if (!tailEntered || !prefixConsumed) throw new IOException("canonical_prefix_tail_bypass");
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (!ReferenceEquals(owner, this)) return;
            Environment.SetEnvironmentVariable(AttemptSingleSegmentProfile.Variable, previous);
            owner = null;
        }
        lifetime.Cancel();
        lifetime.Dispose();
    }
}
