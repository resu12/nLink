using System.Text;
using System.Text.Json;

namespace NLink.Infra.Nkn;

public static class NknBridgePayloadAccounting
{
    public static int MeasureSendCommandJsonlBytes(string destination, ReadOnlySpan<byte> payload, string commandId = "1")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        var command = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = string.IsNullOrWhiteSpace(commandId) ? "1" : commandId.Trim(),
            ["cmd"] = "send",
            ["destination"] = destination,
            ["payloadBase64"] = Convert.ToBase64String(payload),
        };

        return MeasureSerializedJsonlBytes(JsonSerializer.Serialize(command));
    }

    internal static int MeasureSerializedJsonlBytes(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Encoding.UTF8.GetByteCount(json) + 1;
    }
}
