using System.Threading;
using NLink.Core.Logging;

namespace NLink.Core.Configuration;

public static class ReleaseOverridePolicy
{
    public const string UnsafeDeveloperModeEnvVar = "NLINK_UNSAFE_DEVELOPER_MODE";

    private static readonly object Gate = new();
    private static readonly HashSet<string> SuppressedOverrideKeys = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> SuppressedOverrideSummaries = new();
    private static readonly AsyncLocal<bool?> UnsafeDeveloperModeOverrideForTests = new();

    public static bool UnsafeDeveloperModeEnabled
    {
        get
        {
            var testOverride = UnsafeDeveloperModeOverrideForTests.Value;
            if (testOverride.HasValue)
            {
                return testOverride.Value;
            }

            return IsEnabled(Environment.GetEnvironmentVariable(UnsafeDeveloperModeEnvVar));
        }
    }

    public static bool UnsafeOverridesAllowed
    {
        get
        {
#if DEBUG
            return true;
#else
            return UnsafeDeveloperModeEnabled;
#endif
        }
    }

    public static string? ReadUnsafeEnvironmentVariable(string variableName, string category)
        => ApplyUnsafeOverride(
            variableName,
            Environment.GetEnvironmentVariable(variableName),
            source: "env",
            category);

    public static string? ApplyUnsafeAppSetting(string variableName, string? value, string category)
        => ApplyUnsafeOverride(variableName, value, source: "appsettings", category);

    public static string? ApplyUnsafeOverride(string variableName, string? value, string source, string category)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (UnsafeOverridesAllowed)
        {
            return value;
        }

        RecordSuppressed(variableName, source, category);
        return null;
    }

    public static bool AllowUnsafeOverride(string variableName, string source, string category)
    {
        if (UnsafeOverridesAllowed)
        {
            return true;
        }

        RecordSuppressed(variableName, source, category);
        return false;
    }

    public static IReadOnlyList<string> GetSuppressedOverrideSummaries()
    {
        lock (Gate)
        {
            return SuppressedOverrideSummaries.ToArray();
        }
    }

    internal static IDisposable OverrideUnsafeDeveloperModeForTests(bool? enabled)
    {
        var previous = UnsafeDeveloperModeOverrideForTests.Value;
        UnsafeDeveloperModeOverrideForTests.Value = enabled;
        return new RestoreAction(() => UnsafeDeveloperModeOverrideForTests.Value = previous);
    }

    internal static void ResetSuppressedOverridesForTests()
    {
        lock (Gate)
        {
            SuppressedOverrideKeys.Clear();
            SuppressedOverrideSummaries.Clear();
        }
    }

    private static void RecordSuppressed(string variableName, string source, string category)
    {
        var normalizedVariableName = string.IsNullOrWhiteSpace(variableName) ? "(unknown)" : variableName.Trim();
        var normalizedSource = string.IsNullOrWhiteSpace(source) ? "(unknown)" : source.Trim();
        var normalizedCategory = string.IsNullOrWhiteSpace(category) ? "unsafe_override" : category.Trim();
        var key = $"{normalizedVariableName}|{normalizedSource}|{normalizedCategory}";

        lock (Gate)
        {
            if (!SuppressedOverrideKeys.Add(key))
            {
                return;
            }

            SuppressedOverrideSummaries.Add($"{normalizedVariableName}:{normalizedSource}:{normalizedCategory}=suppressed");
        }

        LocalOperationalLog.Warn(
            "Security",
            $"event=release_override_suppressed; variable={normalizedVariableName}; source={normalizedSource}; category={normalizedCategory}; required={UnsafeDeveloperModeEnvVar}");
    }

    private static bool IsEnabled(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return raw.Trim() switch
        {
            "1" => true,
            "true" => true,
            "TRUE" => true,
            "yes" => true,
            "YES" => true,
            "on" => true,
            "ON" => true,
            _ => false,
        };
    }

    private sealed class RestoreAction : IDisposable
    {
        private readonly Action restore;
        private int disposed;

        public RestoreAction(Action restore)
        {
            this.restore = restore;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                restore();
            }
        }
    }
}
