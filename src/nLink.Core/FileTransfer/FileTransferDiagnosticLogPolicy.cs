namespace NLink.Core.FileTransfer;

public static class FileTransferDiagnosticLogPolicy
{
    public const string TraceEnvironmentVariableName = "NLINK_FILETRANSFER_TRACE";

    private static readonly AsyncLocal<bool?> TraceLoggingOverrideForTests = new();
    private static readonly Lazy<bool> RunningUnderTests = new(DetectRunningUnderTests);

    public static bool TraceEnabled
    {
        get
        {
            var overrideValue = TraceLoggingOverrideForTests.Value;
            if (overrideValue.HasValue)
            {
                return overrideValue.Value;
            }

            if (RunningUnderTests.Value)
            {
                return true;
            }

            return IsEnabled(Environment.GetEnvironmentVariable(TraceEnvironmentVariableName));
        }
    }

    internal static IDisposable OverrideTraceLoggingForTests(bool? enabled)
    {
        var previous = TraceLoggingOverrideForTests.Value;
        TraceLoggingOverrideForTests.Value = enabled;
        return new RestoreAction(() => TraceLoggingOverrideForTests.Value = previous);
    }

    private static bool DetectRunningUnderTests()
    {
        try
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = assembly.GetName().Name;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (name.StartsWith("NLink.SmokeTests", StringComparison.Ordinal) ||
                    name.StartsWith("NLink.TestCommon", StringComparison.Ordinal) ||
                    name.StartsWith("xunit", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
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
