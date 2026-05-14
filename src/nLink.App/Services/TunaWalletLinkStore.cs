using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NLink.Core.Configuration;

namespace NLink.App.Services;

internal interface ITunaWalletLinkStore
{
    Task<TunaWalletLinkState> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(TunaWalletLinkState state, CancellationToken ct = default);

    Task ClearAsync(CancellationToken ct = default);
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

    public async Task<TunaWalletLinkState> LoadAsync(CancellationToken ct = default)
    {
        var path = pathProvider();
        try
        {
            if (!File.Exists(path))
            {
                return TunaWalletLinkState.Unlinked;
            }

            await using var stream = File.OpenRead(path);
            var state = await JsonSerializer.DeserializeAsync<TunaWalletLinkState>(stream, JsonOptions, ct).ConfigureAwait(false);
            return state is null || !state.IsLinked ? TunaWalletLinkState.Unlinked : state;
        }
        catch
        {
            return TunaWalletLinkState.Unlinked;
        }
    }

    public async Task SaveAsync(TunaWalletLinkState state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        var path = pathProvider();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, state, JsonOptions, ct).ConfigureAwait(false);
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        var path = pathProvider();
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort user-local diagnostics state only.
        }

        return Task.CompletedTask;
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
