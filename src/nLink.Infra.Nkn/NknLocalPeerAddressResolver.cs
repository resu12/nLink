using NLink.Core.SessionConnect;

namespace NLink.Infra.Nkn;

public static class NknLocalPeerAddressResolver
{
    public static Task<PeerAddress?> ResolvePersistedIdentityAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var options = NknTransportOptions.Load();
        var identity = NknIdentityStore.LoadOrCreate(options);
        NknRuntimeDiagnostics.SetIdentity(
            address: identity.Address,
            identifier: identity.Identifier,
            keyPath: options.KeyPath,
            seedRpc: options.SeedRpc);

        return Task.FromResult(PeerAddress.TryParse(identity.Address, out var parsed)
            ? parsed
            : (PeerAddress?)null);
    }

    public static Task<PeerAddress?> RegeneratePersistedIdentityAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var options = NknTransportOptions.Load();
        var identity = NknIdentityStore.Regenerate(options);
        NknRuntimeDiagnostics.SetIdentity(
            address: identity.Address,
            identifier: identity.Identifier,
            keyPath: options.KeyPath,
            seedRpc: options.SeedRpc);
        NknRuntimeDiagnostics.RecordIdentityRegenerated(DateTimeOffset.UtcNow);

        return Task.FromResult(PeerAddress.TryParse(identity.Address, out var parsed)
            ? parsed
            : (PeerAddress?)null);
    }

    public static async Task<PeerAddress?> ResolveAsync(CancellationToken ct)
    {
        var options = NknTransportOptions.Load();
        var identity = NknIdentityStore.LoadOrCreate(options);
        using var client = new RealNknClientAdapter(identity, options);

        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
            var resolvedAddress = string.IsNullOrWhiteSpace(client.Address)
                ? identity.Address
                : client.Address;

            NknRuntimeDiagnostics.SetIdentity(
                address: resolvedAddress,
                identifier: identity.Identifier,
                keyPath: options.KeyPath,
                seedRpc: options.SeedRpc);

            return PeerAddress.TryParse(resolvedAddress, out var parsed)
                ? parsed
                : null;
        }
        finally
        {
            try
            {
                await client.DisconnectAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }
}
