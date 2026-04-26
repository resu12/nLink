using System.Runtime.InteropServices;

namespace NLink.SmokeTests;

internal sealed class MfDiagnosticFactAttribute : FactAttribute
{
    public MfDiagnosticFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "Media Foundation diagnostics run only on Windows.";
            return;
        }

        var value = Environment.GetEnvironmentVariable("NLINK_RUN_MF_DIAGNOSTIC");
        var enabled = string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

        if (!enabled)
        {
            Skip = "Set NLINK_RUN_MF_DIAGNOSTIC=1 to run Media Foundation diagnostic tests.";
        }
    }
}
