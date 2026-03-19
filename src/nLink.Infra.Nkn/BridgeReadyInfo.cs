namespace NLink.Infra.Nkn;

internal sealed record BridgeReadyInfo(
    string ControlAddress,
    string MediaAddress,
    string BulkAddress,
    int? Protocol,
    string[] SupportedChannels,
    string? BridgeAppVersion)
{
    public bool SupportsChannel(string channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        return SupportedChannels.Any(value => string.Equals(value, channel, StringComparison.OrdinalIgnoreCase));
    }

    public string ChannelsSummary =>
        SupportedChannels.Length == 0
            ? "(none)"
            : string.Join(",", SupportedChannels.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase));
}
