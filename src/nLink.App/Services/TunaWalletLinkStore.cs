using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NLink.Core.Configuration;

namespace NLink.App.Services;

internal interface ITunaWalletLinkStore
{
    Task<TunaWalletLinkState> LoadAsync(CancellationToken ct = default);

    Task<bool> SaveAsync(TunaWalletLinkState state, CancellationToken ct = default, TunaWalletLinkState? expectedValidation = null,
        string? expectedFundedPayer = null);

    Task<bool> TryClearAsync(TunaWalletLinkState expected, CancellationToken ct = default);
}

internal sealed class JsonTunaWalletLinkStore : ITunaWalletLinkStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly Func<string> pathProvider;

    public JsonTunaWalletLinkStore(Func<string>? pathProvider = null)
    {
        this.pathProvider = pathProvider ?? DefaultPathProvider;
    }

    public Task<TunaWalletLinkState> LoadAsync(CancellationToken ct = default)
        => ReadAsync(pathProvider(), ct);

    internal static bool IsStorageError(Exception ex)
        => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException;

    private static async Task<TunaWalletLinkState> ReadAsync(string path, CancellationToken ct)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var state = await JsonSerializer.DeserializeAsync<TunaWalletLinkState>(stream, JsonOptions, ct).ConfigureAwait(false);
            return state is null || !state.IsLinked ? TunaWalletLinkState.Unlinked : state;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return TunaWalletLinkState.Unlinked;
        }
    }

    public async Task<bool> SaveAsync(TunaWalletLinkState state, CancellationToken ct = default, TunaWalletLinkState? expectedValidation = null,
        string? expectedFundedPayer = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var path = Path.GetFullPath(pathProvider());
        using var writeLock = await AcquireWriteLockAsync(path, ct).ConfigureAwait(false);
        if (expectedValidation is not null || expectedFundedPayer is not null)
        {
            var current = await ReadAsync(path, ct).ConfigureAwait(false);
            if (!state.IsSameLinkAs(current) || expectedValidation?.IsSameValidationAs(current) == false || (expectedFundedPayer is not null &&
                (current.Status != TunaWalletLinkStatus.VerifiedFunded ||
                 !string.Equals(expectedFundedPayer, current.WalletAddress, StringComparison.Ordinal))))
                return false;
        }

        // Readers see the old document or the new one, never a truncated validation or relink.
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Open(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (File.Exists(path))
                File.Replace(temp, path, destinationBackupFileName: null); // Keep the existing file's Windows ACL.
            else
                File.Move(temp, path);
            return true;
        }
        finally { File.Delete(temp); }
    }

    public async Task<bool> TryClearAsync(TunaWalletLinkState expected, CancellationToken ct = default)
    {
        var path = Path.GetFullPath(pathProvider());
        using var writeLock = await AcquireWriteLockAsync(path, ct).ConfigureAwait(false);
        if (!expected.IsSamePayerLinkAs(await ReadAsync(path, ct).ConfigureAwait(false)))
            return false;
        ct.ThrowIfCancellationRequested();
        File.Delete(path);
        return true;
    }

    private static async Task<FileStream> AcquireWriteLockAsync(string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Keep this sibling in place across replacements and unlinks. The OS releases the handle on process exit.
        var started = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return File.Open(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (started.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(25, ct).ConfigureAwait(false);
            }
        }
    }

    internal static string DefaultPathProvider()
    {
        var root = TunaRuntimeStateRoot.Resolve();
        return Path.Combine(root, "tuna-wallet-link.json");
    }
}

internal static class TunaRuntimeStateRoot
{
    private const string StateRootEnvVar = "NLINK_TUNA_STATE_ROOT";

    public static string Resolve()
    {
        var overrideRoot = ReleaseOverridePolicy.ReadUnsafeEnvironmentVariable(
            StateRootEnvVar,
            category: "nkn_tuna_test_state");
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            try
            {
                return Path.GetFullPath(overrideRoot.Trim());
            }
            catch
            {
                // Fall through to the normal user-local state path.
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData)
            ? AppContext.BaseDirectory
            : Path.Combine(localAppData, "nLink");
    }
}
