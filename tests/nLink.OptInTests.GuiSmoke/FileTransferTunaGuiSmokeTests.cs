using Xunit.Abstractions;

namespace NLink.SmokeTests;

[Collection(GuiSmokeCollection.Name)]
[Trait("Area", "Gui")]
public sealed class FileTransferTunaGuiSmokeTests
{
    private readonly ITestOutputHelper output;

    public FileTransferTunaGuiSmokeTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [GuiSmokeFact]
    [Trait("Category", "GuiSmoke")]
    [Trait("Transport", "NKN")]
    [Trait("Feature", "Tuna")]
    public Task Windows_GuiSmoke_FileTransfer_TunaHandoffFallback()
    {
        if (!IsEnabled("NLINK_RUN_TUNA_GUI_FILETRANSFER"))
        {
            output.WriteLine("Set NLINK_RUN_TUNA_GUI_FILETRANSFER=1 to run the paid Tuna GUI file-transfer handoff/fallback smoke.");
            return Task.CompletedTask;
        }

        return GuiSmokeHarness.RunScenariosWithTransportAsync(output, "NKN", "filetransfer_tuna_handoff_fallback");
    }

    private static bool IsEnabled(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrWhiteSpace(value) &&
               (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }
}
