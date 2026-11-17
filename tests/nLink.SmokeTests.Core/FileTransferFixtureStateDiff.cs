using System.Text.Json;

namespace NLink.SmokeTests;

internal static class FileTransferFixtureStateDiff
{
    internal static string Describe(JsonElement before, JsonElement after, string path = "$")
    {
        if (before.ValueKind == JsonValueKind.Object && after.ValueKind == JsonValueKind.Object)
        {
            return string.Join(Environment.NewLine, before.EnumerateObject().Select(item => item.Name)
                .Union(after.EnumerateObject().Select(item => item.Name)).Order(StringComparer.Ordinal)
                .Select(name => Describe(before.TryGetProperty(name, out var oldValue) ? oldValue : default,
                    after.TryGetProperty(name, out var newValue) ? newValue : default, path + "." + name))
                .Where(change => change.Length != 0));
        }
        if (before.ValueKind == JsonValueKind.Array && after.ValueKind == JsonValueKind.Array)
        {
            return string.Join(Environment.NewLine, Enumerable.Range(0, Math.Max(before.GetArrayLength(), after.GetArrayLength()))
                .Select(index => Describe(index < before.GetArrayLength() ? before[index] : default,
                    index < after.GetArrayLength() ? after[index] : default, $"{path}[{index}]"))
                .Where(change => change.Length != 0));
        }
        var oldText = before.ValueKind == JsonValueKind.Undefined ? "<missing>" : before.GetRawText();
        var newText = after.ValueKind == JsonValueKind.Undefined ? "<missing>" : after.GetRawText();
        return oldText == newText ? string.Empty : $"{path}: {oldText} -> {newText}";
    }
}
