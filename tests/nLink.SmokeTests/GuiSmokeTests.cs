using Xunit.Abstractions;

namespace NLink.SmokeTests;

public sealed class GuiSmokeTests
{
    private readonly ITestOutputHelper output;

    public GuiSmokeTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [GuiSmokeFact]
    [Trait("Category", "GuiSmoke")]
    public async Task Windows_GuiSmoke_Scenarios_Pass()
    {
        await GuiSmokeHarness.RunDefaultScenariosAsync(output);
    }
}
