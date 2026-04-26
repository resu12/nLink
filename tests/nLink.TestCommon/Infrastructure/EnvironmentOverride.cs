namespace NLink.SmokeTests;

internal sealed class EnvironmentOverride : IDisposable
{
    private readonly string key;
    private readonly string? previousValue;

    public EnvironmentOverride(string key, string? value)
    {
        this.key = key;
        previousValue = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(key, previousValue);
    }
}
