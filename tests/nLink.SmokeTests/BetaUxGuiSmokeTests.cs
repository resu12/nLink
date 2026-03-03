using Xunit.Abstractions;

namespace NLink.SmokeTests;

[Collection(GuiSmokeCollection.Name)]
public sealed class BetaUxGuiSmokeTests
{
    private readonly ITestOutputHelper output;

    public BetaUxGuiSmokeTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [GuiSmokeFact]
    [Trait("Category", "GuiSmoke")]
    public Task Windows_GuiSmoke_BetaUx_NavigationLoop_HasNoDeadEnds()
        => GuiSmokeHarness.RunScenariosAsync(output, "G");

    [GuiSmokeFact]
    [Trait("Category", "GuiSmoke")]
    public Task Windows_GuiSmoke_BetaUx_DiagnosticsFromHome_BackReturnsHome()
        => GuiSmokeHarness.RunScenariosAsync(output, "I");

    [GuiSmokeFact]
    [Trait("Category", "GuiSmoke")]
    public Task Windows_GuiSmoke_BetaUx_HelperCancelDuringConnecting_LeavesConnectUsable()
        => GuiSmokeHarness.RunScenariosAsync(output, "C");

    [GuiSmokeFact]
    [Trait("Category", "GuiSmoke")]
    public Task Windows_GuiSmoke_BetaUx_HelpeeNewCode_DoesNotShowReconnectNoise()
        => GuiSmokeHarness.RunScenariosAsync(output, "J");

    [GuiSmokeFact]
    [Trait("Category", "GuiSmoke")]
    public Task Windows_GuiSmoke_BetaUx_DeclinePath_RecoversWithoutDeadEnd()
        => GuiSmokeHarness.RunScenariosAsync(output, "F");

    [GuiSmokeFact]
    [Trait("Category", "GuiSmoke")]
    public Task Windows_GuiSmoke_BetaUx_HeaderChatCoherence()
        => GuiSmokeHarness.RunScenariosAsync(output, "header_chat_coherence");

    [GuiSmokeFact]
    [Trait("Category", "GuiSmoke")]
    public Task Windows_GuiSmoke_BetaUx_EndSessionDisablesChat()
        => GuiSmokeHarness.RunScenariosAsync(output, "end_session_disables_chat");
}
