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

    [GuiSmokeFact]
    [Trait("Category", "GuiSmoke")]
    public Task Windows_GuiSmoke_BetaUx_ScreenShareButtonVisibility_WhenScaffoldEnabled()
        => GuiSmokeHarness.RunScenariosAsync(output, "screenshare_button_visibility");

    [GuiSmokeFact]
    [Trait("Category", "GuiSmoke")]
    public Task Windows_GuiSmoke_BetaUx_ScreenShareViewer_TogglesVisibility_WhenScaffoldEnabled()
        => GuiSmokeHarness.RunScenariosAsync(output, "screenshare_viewer_toggle");

    [GuiSmokeFact]
    [Trait("Category", "GuiSmoke")]
    public Task Windows_GuiSmoke_BetaUx_ScreenShare_AndChat_Coexist_WhenScaffoldEnabled()
        => GuiSmokeHarness.RunScenariosAsync(output, "screenshare_chat_coexistence");

    [GuiSmokeFact]
    [Trait("Category", "GuiSmoke")]
    public Task Windows_GuiSmoke_BetaUx_ScreenShareStopWhileControlApprovalPending_ClearsViewer()
        => GuiSmokeHarness.RunScenariosAsync(output, "screenshare_stop_pending_approval");

    [GuiSmokeFact]
    [Trait("Category", "GuiSmoke")]
    public Task Windows_GuiSmoke_BetaUx_StatusTextGuardrails()
        => GuiSmokeHarness.RunScenariosAsync(output, "status_text_guardrails");

    [GuiSmokeFact]
    [Trait("Category", "GuiSmoke")]
    [Trait("Transport", "NKN")]
    public Task Windows_GuiSmoke_BetaUx_DirectHelpRequest_ConnectsViaNkn()
        => GuiSmokeHarness.RunScenariosWithTransportAsync(output, "NKN", "nkn_direct_connect");
}
