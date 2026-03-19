using System.Text.Json;

namespace NLink.Infra.Nkn;

internal sealed class BridgeProtocolEventRouter
{
    private readonly string identityAddress;
    private readonly string identityMediaAddress;
    private readonly string identityBulkAddress;
    private readonly ConnectAttemptCoordinator connectAttempts;
    private readonly Func<int?> getCurrentPid;
    private readonly Action<string, string, string> setConnectedAddresses;
    private readonly Action<string> log;

    public BridgeProtocolEventRouter(
        string identityAddress,
        string identityMediaAddress,
        string identityBulkAddress,
        ConnectAttemptCoordinator connectAttempts,
        Func<int?> getCurrentPid,
        Action<string, string, string> setConnectedAddresses,
        Action<string> log)
    {
        this.identityAddress = identityAddress;
        this.identityMediaAddress = identityMediaAddress;
        this.identityBulkAddress = identityBulkAddress;
        this.connectAttempts = connectAttempts;
        this.getCurrentPid = getCurrentPid;
        this.setConnectedAddresses = setConnectedAddresses;
        this.log = log;
    }

    public void HandleHelloOk(JsonElement root)
    {
        string? sdk = null;
        if (TryGetString(root, "sdk", out var sdkValue) && !string.IsNullOrWhiteSpace(sdkValue))
        {
            sdk = sdkValue;
        }

        try
        {
            NknRuntimeDiagnostics.SetBridgeProcessInfo(getCurrentPid() ?? 0, sdk);
        }
        catch
        {
            NknRuntimeDiagnostics.SetBridgeProcessInfo(0, sdk);
        }
    }

    public void HandlePong(JsonElement root)
    {
        NknRuntimeDiagnostics.SetBridgeLastPongUtc(DateTimeOffset.UtcNow);
    }

    public void HandleReady(JsonElement root)
    {
        var readyAddress =
            TryGetString(root, "controlAddress", out var controlAddress) ? controlAddress :
            TryGetString(root, "address", out var legacyAddress) ? legacyAddress :
            string.Empty;
        var readyMediaAddress = TryGetString(root, "mediaAddress", out var mediaAddress) ? mediaAddress : string.Empty;
        var readyBulkAddress = TryGetString(root, "bulkAddress", out var bulkAddress) ? bulkAddress : string.Empty;
        int? protocol = TryGetInt32(root, "protocol", out var protocolValue) ? protocolValue : null;
        var supportedChannels = TryGetStringArray(root, "channels", out var channels)
            ? channels
            : [];
        var bridgeAppVersion = TryGetString(root, "bridgeAppVersion", out var bridgeVersion) && !string.IsNullOrWhiteSpace(bridgeVersion)
            ? bridgeVersion
            : null;
        var hasConnectId = TryGetString(root, "connectId", out var readyConnectId) && !string.IsNullOrWhiteSpace(readyConnectId);

        var resolvedAddress = string.IsNullOrWhiteSpace(readyAddress) ? identityAddress : readyAddress;
        var resolvedMediaAddress = string.IsNullOrWhiteSpace(readyMediaAddress) ? identityMediaAddress : readyMediaAddress;
        var resolvedBulkAddress = string.IsNullOrWhiteSpace(readyBulkAddress) ? identityBulkAddress : readyBulkAddress;
        var readyInfo = new BridgeReadyInfo(
            resolvedAddress,
            resolvedMediaAddress,
            resolvedBulkAddress,
            protocol,
            supportedChannels,
            bridgeAppVersion);
        var accept = connectAttempts.AcceptReady(readyInfo, hasConnectId, readyConnectId);
        switch (accept.Kind)
        {
            case ConnectReadyAcceptKind.NoPending:
                log("Late ready ignored (no pending ready)");
                return;
            case ConnectReadyAcceptKind.StaleMismatch:
                log($"stale_ready_ignored (expected={accept.ExpectedConnectId}, actual={accept.ActualConnectId})");
                return;
            case ConnectReadyAcceptKind.AcceptedMissingConnectId:
                log($"ready_missing_connect_id_accepting (expected={accept.ExpectedConnectId})");
                break;
        }

        setConnectedAddresses(resolvedAddress, resolvedMediaAddress, resolvedBulkAddress);
        NknRuntimeDiagnostics.SetAuthoritativeConnectedAddressResolved(true);
    }

    public void HandleRpcProgress(string eventName, JsonElement root)
    {
        string? selectedRpc = null;
        if (TryGetString(root, "rpc", out var rpc) && !string.IsNullOrWhiteSpace(rpc))
        {
            selectedRpc = rpc;
        }
        else if (TryGetString(root, "selectedRpc", out var selected) && !string.IsNullOrWhiteSpace(selected))
        {
            selectedRpc = selected;
        }

        NknRuntimeDiagnostics.SetLastProgressEvent(eventName, DateTimeOffset.UtcNow, selectedRpc);
        log($"Bridge progress ({eventName}{(selectedRpc is null ? string.Empty : $", rpc={selectedRpc}")})");
    }

    private static bool TryGetString(JsonElement root, string propertyName, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(propertyName, out var prop))
        {
            return false;
        }

        if (prop.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = prop.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryGetInt32(JsonElement root, string propertyName, out int value)
    {
        value = default;
        if (!root.TryGetProperty(propertyName, out var prop))
        {
            return false;
        }

        return prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out value);
    }

    private static bool TryGetStringArray(JsonElement root, string propertyName, out string[] values)
    {
        values = [];
        if (!root.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var items = new List<string>();
        foreach (var item in prop.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = item.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                items.Add(value.Trim());
            }
        }

        values = items.ToArray();
        return true;
    }
}
