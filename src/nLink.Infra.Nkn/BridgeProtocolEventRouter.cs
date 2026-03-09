using System.Text.Json;

namespace NLink.Infra.Nkn;

internal sealed class BridgeProtocolEventRouter
{
    private readonly string identityAddress;
    private readonly ConnectAttemptCoordinator connectAttempts;
    private readonly Func<int?> getCurrentPid;
    private readonly Action<string> setConnectedAddress;
    private readonly Action<string> log;

    public BridgeProtocolEventRouter(
        string identityAddress,
        ConnectAttemptCoordinator connectAttempts,
        Func<int?> getCurrentPid,
        Action<string> setConnectedAddress,
        Action<string> log)
    {
        this.identityAddress = identityAddress;
        this.connectAttempts = connectAttempts;
        this.getCurrentPid = getCurrentPid;
        this.setConnectedAddress = setConnectedAddress;
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
        var readyAddress = TryGetString(root, "address", out var a) ? a : string.Empty;
        var hasConnectId = TryGetString(root, "connectId", out var readyConnectId) && !string.IsNullOrWhiteSpace(readyConnectId);

        var resolvedAddress = string.IsNullOrWhiteSpace(readyAddress) ? identityAddress : readyAddress;
        var accept = connectAttempts.AcceptReady(resolvedAddress, hasConnectId, readyConnectId);
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

        setConnectedAddress(resolvedAddress);
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
}
