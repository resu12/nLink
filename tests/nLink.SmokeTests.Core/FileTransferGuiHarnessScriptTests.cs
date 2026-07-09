using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NLink.App.Views;
using NLink.Core.Configuration;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "GuiHarness")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferGuiHarnessScriptTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    public void GuiSmoke_NknDirectConnectPreflightDoesNotDependOnSendButtonEnabledState()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1");
        var scriptText = File.ReadAllText(scriptPath);
        var functionStart = scriptText.IndexOf("function Run-ScenarioNknDirectConnect", StringComparison.Ordinal);
        Assert.True(functionStart >= 0, "Expected NKN direct-connect scenario to exist.");
        var functionEnd = scriptText.IndexOf("function Run-ScenarioHeaderChatCoherence", functionStart, StringComparison.Ordinal);
        Assert.True(functionEnd > functionStart, "Expected to locate the end of the NKN direct-connect scenario.");

        var functionBody = scriptText[functionStart..functionEnd];
        Assert.Contains("Send-ChatMessage -Window $Context.HelperWindow -Text $msg -UseEnter", functionBody, StringComparison.Ordinal);
    }

    [Fact]
    public void GuiSmoke_NknDirectConnectRequiresCurrentHelperBootstrapBeforeClipboardFallback()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1");
        var scriptText = File.ReadAllText(scriptPath);

        Assert.Contains("function Wait-HelperIdentityBootstrapReadyForClipboard", scriptText, StringComparison.Ordinal);
        Assert.Contains("function Get-NLinkMainWindowElementByProcessId", scriptText, StringComparison.Ordinal);
        Assert.Contains("$className, 'MainWindow'", scriptText, StringComparison.Ordinal);
        Assert.Contains("'ControlType.Window'", scriptText, StringComparison.Ordinal);
        Assert.Contains("Timed out waiting for current helper identity bootstrap UI before clipboard fallback.", scriptText, StringComparison.Ordinal);
        Assert.Contains("helper_identity_bootstrap_not_current_before_clipboard", scriptText, StringComparison.Ordinal);
        Assert.Contains("helper_identity_clipboard_copy_not_current", scriptText, StringComparison.Ordinal);
        Assert.Contains("helper_identity_clipboard_copy_stale", scriptText, StringComparison.Ordinal);
        Assert.Contains("helper_identity_clipboard_fallback_ready_pending_status", scriptText, StringComparison.Ordinal);
        Assert.Contains("helper_identity_clipboard_current_match", scriptText, StringComparison.Ordinal);
        Assert.Contains("helper_identity_window_ownership_mismatch", scriptText, StringComparison.Ordinal);
        Assert.Contains("Get-HelpeeHelperIdentityEntryDiagnostic", scriptText, StringComparison.Ordinal);
        Assert.Contains("helpee_request_flow_not_enabled_after_identity_paste", scriptText, StringComparison.Ordinal);
        Assert.Contains("helper_identity_request_flow_restart_after_helpee_request_not_enabled", scriptText, StringComparison.Ordinal);
        Assert.Contains("function Restart-HelperIdentityPairFlow", scriptText, StringComparison.Ordinal);
        Assert.Contains("helper_identity_pair_restart_after_helpee_invite_bootstrap_failure", scriptText, StringComparison.Ordinal);
        Assert.Contains("helper_identity_pair_retry_scheduled_after_helpee_invite_bootstrap_failure", scriptText, StringComparison.Ordinal);
        Assert.Contains("function Wait-NknBridgeBootstrapReadyForRole", scriptText, StringComparison.Ordinal);
        Assert.Contains("function Wait-NknBridgeBootstrapReadyForCurrentPair", scriptText, StringComparison.Ordinal);
        Assert.Contains("function Wait-NknBridgeBootstrapReadyForCurrentPairWithRecovery", scriptText, StringComparison.Ordinal);
        Assert.Contains("bridge-bootstrap-readiness-", scriptText, StringComparison.Ordinal);
        Assert.Contains("function Get-GuiSmokeDelimitedLogField", scriptText, StringComparison.Ordinal);
        Assert.Contains("FailureSubreason", scriptText, StringComparison.Ordinal);
        Assert.Contains("failure_subreason=", scriptText, StringComparison.Ordinal);
        Assert.Contains("helpee_invite_host_before_bridge_ready", scriptText, StringComparison.Ordinal);
        Assert.Contains("helpee_bridge_host_ready_connect_failed", scriptText, StringComparison.Ordinal);
        Assert.Contains("helper_bridge_host_ready_connect_failed", scriptText, StringComparison.Ordinal);
        Assert.Contains("helpee_invite_bootstrap_bridge_before_ready_retry", scriptText, StringComparison.Ordinal);
        Assert.Contains("nkn_bridge_bootstrap_network_unready", scriptText, StringComparison.Ordinal);
        Assert.Contains("nkn_bridge_bootstrap_pair_retry_scheduled", scriptText, StringComparison.Ordinal);
        Assert.Contains("Test-HelpeeInviteBootstrapBridgeRetryableError", scriptText, StringComparison.Ordinal);
        Assert.Contains("Test-NknBridgeBootstrapFailureRequiresIdentityRotation", scriptText, StringComparison.Ordinal);
        Assert.Contains("nkn_identity_rotation_skipped_for_bridge_bootstrap_retry", scriptText, StringComparison.Ordinal);
        Assert.Contains("Wait-NknBridgeBootstrapReadyForCurrentPairWithRecovery -Context $Context", scriptText, StringComparison.Ordinal);
        Assert.Contains("Wait-NknBridgeBootstrapReadyForCurrentPair -Context $Context", scriptText, StringComparison.Ordinal);
        Assert.Contains("before_request_help_click", scriptText, StringComparison.Ordinal);
        Assert.Contains("Enter-HelpeeHelperIdentityAndRequestHelp -Context $Context", scriptText, StringComparison.Ordinal);
        Assert.Contains("-RotateIdentity $nextPairRestartRotateIdentity", scriptText, StringComparison.Ordinal);
        Assert.Contains("function Rotate-GuiSmokeNknIdentityPairForBootstrapRetry", scriptText, StringComparison.Ordinal);
        Assert.Contains("function Rotate-GuiSmokeNknIdentityForRole", scriptText, StringComparison.Ordinal);
        Assert.Contains("nkn_identity_rotated_after_helpee_invite_bootstrap_failure", scriptText, StringComparison.Ordinal);
        Assert.Contains("nkn-identities-retired", scriptText, StringComparison.Ordinal);
        Assert.Contains("Test-HelpeeInviteBootstrapPairRetryableError", scriptText, StringComparison.Ordinal);
        Assert.Contains("Get-GuiSmokeStringHashPrefix", scriptText, StringComparison.Ordinal);
        Assert.DoesNotContain("if ($headerStatus.StartsWith('Connecting'", scriptText, StringComparison.Ordinal);
        Assert.DoesNotContain("-not $copy.Current.IsEnabled -or -not $regenerate.Current.IsEnabled", scriptText, StringComparison.Ordinal);

        var copyStart = scriptText.IndexOf("function Copy-HelperIdentityAndReadClipboard", StringComparison.Ordinal);
        Assert.True(copyStart >= 0, "Expected helper clipboard copy function to exist.");
        var copyEnd = scriptText.IndexOf("function Read-HelperIdentityFromAppLog", copyStart, StringComparison.Ordinal);
        Assert.True(copyEnd > copyStart, "Expected to locate the end of the helper clipboard copy function.");
        var copyBody = scriptText[copyStart..copyEnd];

        Assert.Contains("Wait-HelperIdentityBootstrapReadyForClipboard", copyBody, StringComparison.Ordinal);
        Assert.Contains("Test-HelperIdentityClipboardCandidateUsable -Value $text", copyBody, StringComparison.Ordinal);
        Assert.Contains("$expectedClipboardValue", copyBody, StringComparison.Ordinal);
        Assert.Contains("[System.StringComparison]::Ordinal", copyBody, StringComparison.Ordinal);

        var bridgeRetryStart = scriptText.IndexOf("function Test-HelpeeInviteBootstrapBridgeRetryableError", StringComparison.Ordinal);
        Assert.True(bridgeRetryStart >= 0, "Expected bridge bootstrap retry classifier to exist.");
        var bridgeRetryEnd = scriptText.IndexOf("function Test-HelpeeInviteBootstrapPairRetryableError", bridgeRetryStart, StringComparison.Ordinal);
        Assert.True(bridgeRetryEnd > bridgeRetryStart, "Expected to locate the end of the bridge bootstrap retry classifier.");
        var bridgeRetryBody = scriptText[bridgeRetryStart..bridgeRetryEnd];
        Assert.Contains("nkn_bridge_bootstrap_network_unready", bridgeRetryBody, StringComparison.Ordinal);
        Assert.Contains("helpee_invite_host_before_bridge_ready", bridgeRetryBody, StringComparison.Ordinal);
        Assert.Contains("helpee_invite_bootstrap_bridge_before_ready_retry", bridgeRetryBody, StringComparison.Ordinal);
        Assert.Contains("HostByAddressAsync failed", bridgeRetryBody, StringComparison.Ordinal);
        Assert.Contains("RPC call failed", bridgeRetryBody, StringComparison.Ordinal);
        Assert.Contains("Connect failed", bridgeRetryBody, StringComparison.Ordinal);
        Assert.Contains("connect EACCES", bridgeRetryBody, StringComparison.Ordinal);
        Assert.Contains("Could not prepare invite", bridgeRetryBody, StringComparison.Ordinal);

        var rotationPolicyStart = scriptText.IndexOf("function Test-NknBridgeBootstrapFailureRequiresIdentityRotation", StringComparison.Ordinal);
        Assert.True(rotationPolicyStart >= 0, "Expected bridge bootstrap identity rotation policy to exist.");
        var rotationPolicyEnd = scriptText.IndexOf("function Test-HelpeeInviteBootstrapPairRetryableError", rotationPolicyStart, StringComparison.Ordinal);
        Assert.True(rotationPolicyEnd > rotationPolicyStart, "Expected to locate the end of the identity rotation policy.");
        var rotationPolicyBody = scriptText[rotationPolicyStart..rotationPolicyEnd];
        Assert.Contains("helpee_bridge_host_ready_connect_failed", rotationPolicyBody, StringComparison.Ordinal);
        Assert.Contains("helper_bridge_host_ready_connect_failed", rotationPolicyBody, StringComparison.Ordinal);
        Assert.Contains("HostByAddressAsync failed", rotationPolicyBody, StringComparison.Ordinal);
        Assert.Contains("RPC call failed", rotationPolicyBody, StringComparison.Ordinal);
        Assert.Contains("Connect failed", rotationPolicyBody, StringComparison.Ordinal);
        Assert.Contains("connect EACCES", rotationPolicyBody, StringComparison.Ordinal);
        Assert.Contains("$rotateIdentity = Test-NknBridgeBootstrapFailureRequiresIdentityRotation -Message $message", scriptText, StringComparison.Ordinal);
        Assert.Contains("-RotateIdentity $rotateIdentity", scriptText, StringComparison.Ordinal);
        Assert.Contains("$nextPairRestartRotateIdentity = if ($bridgeRetry) { Test-NknBridgeBootstrapFailureRequiresIdentityRotation -Message $message } else { $true }", scriptText, StringComparison.Ordinal);

        var identityRetryStart = scriptText.IndexOf("function Test-HelpeeInviteBootstrapPairRetryableError", StringComparison.Ordinal);
        Assert.True(identityRetryStart >= 0, "Expected helper identity retry classifier to exist.");
        var identityRetryEnd = scriptText.IndexOf("function Wait-HelpeeConnectionEntryMode", identityRetryStart, StringComparison.Ordinal);
        Assert.True(identityRetryEnd > identityRetryStart, "Expected to locate the end of the helper identity retry classifier.");
        var identityRetryBody = scriptText[identityRetryStart..identityRetryEnd];
        Assert.Contains("Timed out waiting for helpee Allow approval UI", identityRetryBody, StringComparison.Ordinal);
        Assert.DoesNotContain("nkn_bridge_bootstrap_not_ready", identityRetryBody, StringComparison.Ordinal);
        Assert.DoesNotContain("HostByAddressAsync failed", identityRetryBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Could not prepare invite", identityRetryBody, StringComparison.Ordinal);

        var directConnectStart = scriptText.IndexOf("function Run-ScenarioNknDirectConnect", StringComparison.Ordinal);
        Assert.True(directConnectStart >= 0, "Expected NKN direct-connect scenario to exist.");
        var directConnectEnd = scriptText.IndexOf("function Run-ScenarioHeaderChatCoherence", directConnectStart, StringComparison.Ordinal);
        Assert.True(directConnectEnd > directConnectStart, "Expected to locate the end of the NKN direct-connect scenario.");
        var directConnectBody = scriptText[directConnectStart..directConnectEnd];

        Assert.Contains("Connect-HelperAndHelpee -Context $Context", directConnectBody, StringComparison.Ordinal);
        Assert.Contains("Helper identity connect flow returned empty text.", directConnectBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Enter-HelpeeHelperIdentityAndRequestHelp -HelpeeWindow $Context.HelpeeWindow -HelperIdentity $helperIdentity", directConnectBody, StringComparison.Ordinal);
    }

    [Fact]
    public void NknBridge_BootstrapRetriesPlainRpcBeforeTlsFallback()
    {
        var repoRoot = FindRepoRoot();
        var bridgePath = Path.Combine(repoRoot, "tools", "nkn-bridge", "index.js");
        var bridgeText = File.ReadAllText(bridgePath);

        Assert.Contains("function buildRpcBootstrapCandidates", bridgeText, StringComparison.Ordinal);
        Assert.Contains("bootstrapCandidates.push({ rpc, tls: false, stage: plainStage })", bridgeText, StringComparison.Ordinal);
        Assert.Contains("bootstrapCandidates.push({ rpc, tls: true, stage: 'tls_fallback' })", bridgeText, StringComparison.Ordinal);
        Assert.Contains("function normalizeRpcBootstrapCandidate", bridgeText, StringComparison.Ordinal);
        Assert.Contains("baseOptions.tls = initialCandidate.tls", bridgeText, StringComparison.Ordinal);
        Assert.Contains("stage: bootstrapCandidate.stage", bridgeText, StringComparison.Ordinal);
        Assert.Contains("tls: bootstrapCandidate.tls ? 1 : 0", bridgeText, StringComparison.Ordinal);
        Assert.Contains("trackSelectedRpc(rpc, bootstrapCandidate.stage)", bridgeText, StringComparison.Ordinal);
        Assert.Contains("state.selectedRpcStage = typeof stage === 'string' && stage.length > 0", bridgeText, StringComparison.Ordinal);
        Assert.Contains("Creating NKN clients (rpc=${baseOptions.rpcServerAddr || 'default'}, tls=${baseOptions.tls ? 1 : 0}", bridgeText, StringComparison.Ordinal);
        Assert.Contains("const CONNECT_FALLBACK_CLIENT_CLOSE_TIMEOUT_MS", bridgeText, StringComparison.Ordinal);
        Assert.Contains("await closeClient(CONNECT_FALLBACK_CLIENT_CLOSE_TIMEOUT_MS)", bridgeText, StringComparison.Ordinal);
        Assert.Contains("timed out during bootstrap fallback close", bridgeText, StringComparison.Ordinal);
    }

    [Fact]
    public void GuiSmoke_FileTransferNknScenariosRequireNknTransport()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1");
        var scriptText = File.ReadAllText(scriptPath);

        Assert.Contains("Assert-GuiSmokeNknTransport -ScenarioName 'FILETRANSFER_NKN_SOAK'", scriptText, StringComparison.Ordinal);
        Assert.Contains("Assert-GuiSmokeNknTransport -ScenarioName 'FILETRANSFER_NKN_MIXED_SOAK'", scriptText, StringComparison.Ordinal);
        Assert.Contains("requires NLINK_TRANSPORT=NKN", scriptText, StringComparison.Ordinal);
    }

    [Fact]
    public void RunFileTransferTunaGuiSmoke_NoFaultFallbackEvidenceIsFailure()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1");
        var scriptText = File.ReadAllText(scriptPath);

        Assert.Contains("Tuna GUI preactivated no-fault transfer unexpectedly entered fallback", scriptText, StringComparison.Ordinal);
        Assert.Contains("$FaultMode -eq 'none'", scriptText, StringComparison.Ordinal);
        Assert.Contains("$evidence.fallbackEpochStarted", scriptText, StringComparison.Ordinal);
    }

    [Fact]
    public void RunFileTransferTunaGuiSmoke_PreactivationAcceptFailuresAreClassified()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1");
        var scriptText = File.ReadAllText(scriptPath);
        var routeAcceptanceText = File.ReadAllText(Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"));

        Assert.Contains("Get-TunaGuiFileTransferSetupFailureClassification", scriptText, StringComparison.Ordinal);
        Assert.Contains("Wait-TunaGuiFileTransferAcceptOrThrow", scriptText, StringComparison.Ordinal);
        Assert.Contains("Wait-TunaGuiDiagnosticReadinessBeforeMeasuredTransfer", scriptText, StringComparison.Ordinal);
        Assert.Contains("diagnostic Tuna readiness before measured GUI file transfer", scriptText, StringComparison.Ordinal);
        Assert.Contains("pretransfer_tuna_readiness_missing", scriptText, StringComparison.Ordinal);
        Assert.Contains("Wait-TunaGuiFileTunaV4PublicationAfterBookmark", scriptText, StringComparison.Ordinal);
        Assert.Contains("file_tuna_v4_publication_missing_after_transfer", scriptText, StringComparison.Ordinal);
        Assert.Contains("initial_tuna_evidence_not_current", scriptText, StringComparison.Ordinal);
        Assert.Contains("readiness_failure=tuna_path_lease_unavailable_at_answer", scriptText, StringComparison.Ordinal);
        Assert.DoesNotContain("Tuna transport active before measured GUI file transfer", scriptText, StringComparison.Ordinal);
        Assert.Contains("nkn_bridge_receive_stall_recovery_unproven", scriptText, StringComparison.Ordinal);
        Assert.Contains("failurePhase", scriptText, StringComparison.Ordinal);
        Assert.Contains("failureReason", scriptText, StringComparison.Ordinal);
        Assert.Contains("offer_sent_accept_not_enabled", scriptText, StringComparison.Ordinal);
        Assert.Contains("preflight_listener_unavailable", scriptText, StringComparison.Ordinal);
        Assert.Contains("regular_v4_receive_recovery_unproven", scriptText, StringComparison.Ordinal);
        Assert.Contains("runtime_unlock_retry_regular_v4_recovery_settlement_missing", scriptText, StringComparison.Ordinal);
        Assert.Contains("event=runtime_unlock_retry_blocked_by_regular_v4_receive_recovery", scriptText, StringComparison.Ordinal);
        Assert.Contains("event=runtime_unlock_retry_rejected_after_regular_v4_receive_recovery", scriptText, StringComparison.Ordinal);
        Assert.Contains("activation_offer_not_observed", scriptText, StringComparison.Ordinal);
        Assert.Contains("activation_offer_sent_waiting_answer", scriptText, StringComparison.Ordinal);
        Assert.Contains("normal_to_tuna_live_epoch_missing", scriptText, StringComparison.Ordinal);
        Assert.Contains("normal_to_tuna_live_epoch_recovery_missing", scriptText, StringComparison.Ordinal);
        Assert.Contains("normal_to_tuna_route_selection_missing", scriptText, StringComparison.Ordinal);
        Assert.Contains("event=tuna_acceleration_activation_offer_not_observed", scriptText, StringComparison.Ordinal);
        Assert.Contains("event=tuna_acceleration_runtime_unlock_dispatch_deferred_for_regular_v4_receive_recovery", scriptText, StringComparison.Ordinal);
        Assert.Contains("event=tuna_acceleration_control_send_wait_timeout", scriptText, StringComparison.Ordinal);
        Assert.Contains("activationOfferReceived", scriptText, StringComparison.Ordinal);
        Assert.Contains("measuredOfferReceived", scriptText, StringComparison.Ordinal);
        Assert.Contains("offerReceived", scriptText, StringComparison.Ordinal);
        Assert.Contains("FallbackTailLocalYieldBlocked", scriptText, StringComparison.Ordinal);
        Assert.Contains("fallback_tail_local_yield_missing", scriptText, StringComparison.Ordinal);
        Assert.Contains("fallback_frontier_state_missing", scriptText, StringComparison.Ordinal);
        Assert.Contains("FallbackFrontierRepairStalled", scriptText, StringComparison.Ordinal);
        Assert.Contains("fallback_frontier_repair_stalled", scriptText, StringComparison.Ordinal);
        Assert.Contains("fallback_frontier_request_dispatch_missing", scriptText, StringComparison.Ordinal);
        Assert.Contains("Test-TunaGuiLatestFallbackFrontierRequestDispatchMissing", scriptText, StringComparison.Ordinal);
        Assert.Contains("Test-TunaGuiFallbackFrontierRequestDispatchTupleMatch", scriptText, StringComparison.Ordinal);
        Assert.Contains("'live_route_epoch'", scriptText, StringComparison.Ordinal);
        Assert.Contains("'transport_epoch'", scriptText, StringComparison.Ordinal);
        Assert.Contains("'repair_request_id'", scriptText, StringComparison.Ordinal);
        Assert.Contains("'request_correlation_id'", scriptText, StringComparison.Ordinal);
        Assert.Contains("'first_start_chunk_index'", scriptText, StringComparison.Ordinal);
        Assert.Contains("'first_chunk_count'", scriptText, StringComparison.Ordinal);
        Assert.Contains("'range_count'", scriptText, StringComparison.Ordinal);
        Assert.Contains("'checkpoint_request_id'", scriptText, StringComparison.Ordinal);
        Assert.Contains("filetransfer_v6_frontier_request_receive_dispatched", scriptText, StringComparison.Ordinal);
        Assert.Contains("filetransfer_v6_frontier_request_receive_dispatch_rejected", scriptText, StringComparison.Ordinal);
        Assert.Contains("filetransfer_v6_frontier_request_receive_dispatch_coalesced", scriptText, StringComparison.Ordinal);
        Assert.Contains("fallback_frontier_repair", scriptText, StringComparison.Ordinal);
        Assert.Contains("postTunaFallbackRouteEvidence", scriptText, StringComparison.Ordinal);
        Assert.Contains("postTunaFallbackFrontierRescueObserved", scriptText, StringComparison.Ordinal);
        Assert.Contains("normalToTunaEpochWaitAfterFallback", scriptText, StringComparison.Ordinal);
        Assert.Contains("filetransfer_v6_post_tuna_fallback_frontier_rescue_requested", scriptText, StringComparison.Ordinal);
        Assert.Contains("Test-Phase5ScenarioRuntimeRouteEvidence", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("Clear-Phase5StaleSetupFailureAfterRuntimeEvidence", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("Clear-Phase5StaleSetupFailureAfterRuntimeEvidence -Result $result", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("Tuna GUI runtime failure: phase={0}; reason={1}", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("fallback_frontier_request_dispatch_missing", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("fallback_frontier_repair_stalled", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("Tuna GUI setup failure:", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("regularV4FrontierRepairSettledStalledEvent", scriptText, StringComparison.Ordinal);
        Assert.Contains("regularV4FrontierRepairPrecontractSettlementConsumed", scriptText, StringComparison.Ordinal);
        Assert.Contains("regularV4ReceiveRecoveryClosedNoDeferral", scriptText, StringComparison.Ordinal);
        Assert.Contains("regularV4ReceiveRecoveryRejectedBeforeRuntimeUnlock", scriptText, StringComparison.Ordinal);
        Assert.Contains("regular_v4_receive_recovery_rejected_before_runtime_unlock", scriptText, StringComparison.Ordinal);
        Assert.Contains("regularV4FrontierRepairSettlementClosed", scriptText, StringComparison.Ordinal);
        Assert.Contains("regularV4FrontierRepairSettledStalledUnclosed", scriptText, StringComparison.Ordinal);
        Assert.Contains("runtimeUnlockRetryDispatchMissingAfterAuthorityGranted", scriptText, StringComparison.Ordinal);
        Assert.Contains("runtime_unlock_retry_dispatch_missing_after_authority_granted", scriptText, StringComparison.Ordinal);
        Assert.Contains("runtimeUnlockFileTransferOwnerMissingBeforePathLease", scriptText, StringComparison.Ordinal);
        Assert.Contains("runtime_unlock_filetransfer_owner_missing_before_path_lease", scriptText, StringComparison.Ordinal);
        Assert.Contains("runtimeUnlockFileTransferOwnerStaleBeforeOfferDispatch", scriptText, StringComparison.Ordinal);
        Assert.Contains("runtime_unlock_filetransfer_owner_stale_before_offer_dispatch", scriptText, StringComparison.Ordinal);
        Assert.Contains("Test-TunaGuiRuntimeUnlockFileTransferOwnerRejectionUnresolved", scriptText, StringComparison.Ordinal);
        Assert.Contains("Test-TunaGuiRegularV4FrontierRepairSettlementClosed", scriptText, StringComparison.Ordinal);
        Assert.Contains("Test-TunaGuiRegularV4FrontierRepairPrecontractClosureMatch", scriptText, StringComparison.Ordinal);
        Assert.Contains("Test-TunaGuiRegularV4FrontierRepairMatch", scriptText, StringComparison.Ordinal);
        Assert.Contains("filetransfer_regular_v4_frontier_repair_settlement_accepted", scriptText, StringComparison.Ordinal);
        Assert.Contains("regular_v4_frontier_repair_liveness_settlement_missing", scriptText, StringComparison.Ordinal);
        Assert.Contains("(-not $fallbackFrontierRepairStalled) -and ($regularV4FrontierRepairSettledStalledUnclosed", scriptText, StringComparison.Ordinal);
        Assert.Contains("post_fallback_runtime_unlock_resume", scriptText, StringComparison.Ordinal);
        Assert.Contains("post_fallback_runtime_unlock_resume_blocked", scriptText, StringComparison.Ordinal);
        Assert.Contains("fallback_proof_replay_pending_after_paired_yield", scriptText, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_runtime_unlock_precommit_probe_pending_held", scriptText, StringComparison.Ordinal);
        Assert.Contains("message_type=file_transfer_offer", scriptText, StringComparison.Ordinal);
        Assert.Contains("event=tuna_acceleration_offer_received_raw", scriptText, StringComparison.Ordinal);
        Assert.Contains("event=offer_received", scriptText, StringComparison.Ordinal);
        Assert.Contains("Get-TunaGuiReadinessStateAfterBookmark -Bookmark $Bookmark", scriptText, StringComparison.Ordinal);
        Assert.Contains("rawListenerUnavailable", scriptText, StringComparison.Ordinal);
        Assert.Contains("rawListenerReady", scriptText, StringComparison.Ordinal);
        Assert.Contains("$hasScenarioRuntimeEvidence", scriptText, StringComparison.Ordinal);
        Assert.Contains("$runtimeUnlockRetryRecoverySettlementMissingMarker -or", scriptText, StringComparison.Ordinal);
        Assert.Contains("$runtimeUnlockRetryRegularV4RecoverySettlementMissing -or", scriptText, StringComparison.Ordinal);
        Assert.Contains("(-not $hasScenarioRuntimeEvidence) -and", scriptText, StringComparison.Ordinal);
        Assert.True(
            scriptText.IndexOf("elseif ($fallbackFrontierRequestDispatchMissing)", StringComparison.Ordinal) <
            scriptText.IndexOf("elseif ($fallbackFrontierRepairStalled)", StringComparison.Ordinal),
            "Missing fallback frontier request dispatch must classify before generic fallback frontier repair stalls.");
        Assert.True(
            scriptText.IndexOf("elseif ($fallbackFrontierRepairStalled)", StringComparison.Ordinal) <
            scriptText.IndexOf("elseif ($regularV4FrontierRepairSettledStalledUnclosed)", StringComparison.Ordinal),
            "Fallback frontier repair classification must outrank settled regular V4 frontier labels after a fallback route is current.");
        Assert.True(
            scriptText.IndexOf("elseif ($runtimeUnlockFileTransferOwnerMissingBeforePathLease)", StringComparison.Ordinal) <
            scriptText.IndexOf("elseif ($runtimeUnlockRetryDispatchMissingAfterAuthorityGranted)", StringComparison.Ordinal),
            "A missing coordinator file-transfer owner must classify before downstream retry-dispatch failures.");
        Assert.True(
            scriptText.IndexOf("elseif ($runtimeUnlockRetryDispatchMissingAfterAuthorityGranted)", StringComparison.Ordinal) <
            scriptText.IndexOf("elseif ($regularV4FrontierRepairSettledStalledUnclosed)", StringComparison.Ordinal),
            "Runtime-unlock dispatch authority gaps must outrank broader regular V4 frontier settlement labels.");
        Assert.True(
            scriptText.IndexOf("elseif ($regularV4ReceiveRecoveryRejectedBeforeRuntimeUnlock)", StringComparison.Ordinal) <
            scriptText.IndexOf("elseif ($runtimeUnlockRetryRegularV4RecoverySettlementMissing)", StringComparison.Ordinal),
            "Explicit regular V4 receive-recovery rejection must classify before missing-settlement buckets.");
        Assert.True(
            scriptText.IndexOf("elseif ($regularV4ReceiveRecoveryRejectedBeforeRuntimeUnlock)", StringComparison.Ordinal) <
            scriptText.IndexOf("elseif ($regularV4FrontierRepairSettledStalledUnclosed)", StringComparison.Ordinal),
            "Explicit regular V4 receive-recovery rejection must classify before generic regular V4 liveness labels.");
        Assert.True(
            scriptText.IndexOf("elseif ($regularV4FrontierRepairSettledStalledUnclosed)", StringComparison.Ordinal) <
            scriptText.IndexOf("elseif ($regularV4FrontierRepairStalled)", StringComparison.Ordinal),
            "Settled regular V4 frontier repair labels must outrank generic regular V4 frontier stalls.");
    }

    [Fact]
    public void RunFileTransferTunaGuiSmoke_PreactivationRequiresPublicationAuthorityAfterMeasuredTransfer()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1");
        var scriptText = File.ReadAllText(scriptPath);
        var cycleBody = ExtractPowerShellFunctionBody(scriptText, "Invoke-FileTransferTunaHandoffFallbackCycle", "Wait-FileTransferTerminalOrProgressBeforeAction");

        Assert.Contains("diagnosticTunaReadinessObserved", cycleBody, StringComparison.Ordinal);
        Assert.Contains("fileTunaV4PublicationObserved", cycleBody, StringComparison.Ordinal);
        Assert.Contains("Test-GuiSmokeFieldEquals", scriptText, StringComparison.Ordinal);
        Assert.Contains("Name 'route' -Expected 'file_tuna_v4'", scriptText, StringComparison.Ordinal);
        Assert.Contains("Name 'target_route' -Expected 'file_tuna_v4'", scriptText, StringComparison.Ordinal);
        Assert.Contains("Name 'target_transport' -Expected 'tuna'", scriptText, StringComparison.Ordinal);
        Assert.Contains("Name 'coordinator_decision_reason' -Expected 'accepted'", scriptText, StringComparison.Ordinal);
        Assert.Contains("Name 'route_publication_action_kind' -Expected $ActionKind", scriptText, StringComparison.Ordinal);
        Assert.Contains("-ActionKind 'RoutePublication'", cycleBody, StringComparison.Ordinal);
        Assert.Contains("-ActionKind 'RouteCommitPublication'", cycleBody, StringComparison.Ordinal);
        Assert.Contains("'route_publication_generation'", scriptText, StringComparison.Ordinal);
        Assert.Contains("'runtime_generation'", scriptText, StringComparison.Ordinal);
        Assert.Contains("'terminal_generation'", scriptText, StringComparison.Ordinal);
        Assert.Contains("'leg_generation'", scriptText, StringComparison.Ordinal);
        Assert.Contains("RequireZeroRouteCommitGeneration $true", cycleBody, StringComparison.Ordinal);
        Assert.Contains("RequirePositiveRouteCommitGeneration $true", cycleBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-TunaGuiFileTunaV4PublicationNeedles", scriptText, StringComparison.Ordinal);
        Assert.DoesNotContain("Needle 'event=tuna_acceleration_negotiated' -TimeoutMs 150000 -Description 'Tuna negotiated before measured GUI file transfer'", cycleBody, StringComparison.Ordinal);
        Assert.DoesNotContain("active=1', 'paid_listener_active'", cycleBody, StringComparison.Ordinal);
        Assert.DoesNotContain("active=1', 'free_dialer_active'", cycleBody, StringComparison.Ordinal);
    }

    [Fact]
    public void RunFileTransferTunaGuiSmoke_PreactivationReadinessRequiresAnswerReceivedWithCurrentLease()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1");
        var scriptText = File.ReadAllText(scriptPath);
        var readinessBody = ExtractPowerShellFunctionBody(scriptText, "Wait-TunaGuiDiagnosticReadinessBeforeMeasuredTransfer", "Test-GuiSmokeFieldEquals");
        var summaryBody = ExtractPowerShellFunctionBody(scriptText, "Get-TunaGuiEvidenceSummary", "Invoke-FileTransferTunaPostFallbackPrecondition");
        var cycleBody = ExtractPowerShellFunctionBody(scriptText, "Invoke-FileTransferTunaHandoffFallbackCycle", "Wait-FileTransferTerminalOrProgressBeforeAction");

        Assert.Contains("'event=runtime_unlock_transaction_answer_received', 'peer_visible_proof=1', 'answer_received=1', 'tuna_path_lease_current=1', 'tuna_path_lease_state=listenerready'", readinessBody, StringComparison.Ordinal);
        Assert.DoesNotContain("'event=tuna_path_lease_listener_ready', 'current=1'", readinessBody, StringComparison.Ordinal);
        Assert.DoesNotContain("'event=runtime_unlock_transaction_listener_ready'", readinessBody, StringComparison.Ordinal);
        Assert.DoesNotContain("'event=runtime_unlock_transaction_offer_generation_created'", readinessBody, StringComparison.Ordinal);
        Assert.DoesNotContain("'event=tuna_acceleration_answer_ack_received'", readinessBody, StringComparison.Ordinal);
        Assert.DoesNotContain("'event=tuna_acceleration_negotiated'", readinessBody, StringComparison.Ordinal);

        Assert.Contains("event=runtime_unlock_transaction_answer_received", summaryBody, StringComparison.Ordinal);
        Assert.Contains("tuna_path_lease_current=1", summaryBody, StringComparison.Ordinal);
        Assert.Contains("tuna_path_lease_state=listenerready", summaryBody, StringComparison.Ordinal);
        Assert.DoesNotContain("event=tuna_path_lease_listener_ready", summaryBody, StringComparison.Ordinal);
        Assert.DoesNotContain("event=tuna_acceleration_answer_ack_received", summaryBody, StringComparison.Ordinal);
        Assert.DoesNotContain("-or $line.IndexOf('event=tuna_acceleration_negotiated'", summaryBody, StringComparison.Ordinal);

        Assert.Contains("$secondDiagnosticReadinessLine = [string](Wait-TunaGuiDiagnosticReadinessBeforeMeasuredTransfer -Bookmark $secondBookmark", cycleBody, StringComparison.Ordinal);
        Assert.Contains("$secondSendButton = Wait-TunaGuiSecondTransferReadinessOrThrow -Window $senderWindow -Bookmark $secondBookmark", cycleBody, StringComparison.Ordinal);
        Assert.Contains("secondTransferDiagnosticReadiness", cycleBody, StringComparison.Ordinal);
        Assert.Contains("second_transfer_file_tuna_v4_route_publication", cycleBody, StringComparison.Ordinal);
        Assert.Contains("continuing to require coordinator-authorized file_tuna_v4 publication", cycleBody, StringComparison.Ordinal);
        Assert.Contains("if ($secondTransferRequiresFreshDiagnosticReadiness) {", cycleBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Tuna GUI second-transfer preactivation readiness missing", cycleBody, StringComparison.Ordinal);
    }

    [Fact]
    public void RunFileTransferTunaGuiSmoke_UsesArtifactLocalNknIdentitiesAndEmptyLogClassification()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1");
        var scriptText = File.ReadAllText(scriptPath);

        Assert.Contains("Get-AppLaunchEnvironmentOverrides -RoleName $RoleName", scriptText, StringComparison.Ordinal);
        Assert.Contains("Get-GuiSmokeNknIdentityPathForRole", scriptText, StringComparison.Ordinal);
        Assert.Contains("NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR", scriptText, StringComparison.Ordinal);
        Assert.Contains("nkn-identities", scriptText, StringComparison.Ordinal);
        Assert.Contains("NLINK_NKN_KEY_PATH", scriptText, StringComparison.Ordinal);
        Assert.Contains("[AllowEmptyCollection()][object[]]$Lines", scriptText, StringComparison.Ordinal);
    }

    [Fact]
    public void GuiSmokeNknBootstrap_RetriesHelpeeEntryModeAfterTransientConnectionFailure()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1");
        var scriptText = File.ReadAllText(scriptPath);

        Assert.Contains("Wait-HelpeeConnectionEntryModeWithRecovery", scriptText, StringComparison.Ordinal);
        Assert.Contains("Recovering helpee connection entry-mode wait after transient failure", scriptText, StringComparison.Ordinal);
        Assert.Contains("Reenter-RoleFlowAfterConnectionFailure -Window $Context.HelpeeWindow -HomeButtonText 'I need help'", scriptText, StringComparison.Ordinal);
        Assert.Contains("Restart-HelpeeFlow -Context $Context", scriptText, StringComparison.Ordinal);
    }

    [Fact]
    public void RunFileTransferTunaGuiSmoke_LiveSwitchOffUsesSameTransferV6FallbackProof()
    {
        var repoRoot = FindRepoRoot();
        var wrapperPath = Path.Combine(repoRoot, "tools", "Run-FileTransferTunaGuiSmoke.ps1");
        var guiPath = Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1");
        var wrapperText = File.ReadAllText(wrapperPath);
        var guiText = File.ReadAllText(guiPath);

        Assert.Contains("\"live-v4-switch-off\"", wrapperText, StringComparison.Ordinal);
        Assert.Contains("'live-v4-switch-off'", guiText, StringComparison.Ordinal);
        Assert.Contains("measured_live_post_tuna_fallback_v6", guiText, StringComparison.Ordinal);
        Assert.Contains("fallbackModel = $fallbackModel", guiText, StringComparison.Ordinal);
        Assert.Contains("live_v6", guiText, StringComparison.Ordinal);
        Assert.Contains("singleTransferLiveFallback", guiText, StringComparison.Ordinal);
        Assert.Contains("route=post_tuna_fallback_v6", guiText, StringComparison.Ordinal);
        Assert.Contains("protocol_version=6", guiText, StringComparison.Ordinal);
        Assert.Contains("filetransfer_live_route_epoch_recovered", guiText, StringComparison.Ordinal);
        Assert.Contains("off_recovered", guiText, StringComparison.Ordinal);
        Assert.Contains("Get-TunaGuiLiveRouteEpochProof", guiText, StringComparison.Ordinal);
        Assert.Contains("strict live-route epoch sequence", guiText, StringComparison.Ordinal);
        Assert.Contains("missing_metadata", guiText, StringComparison.Ordinal);
        Assert.Contains("postTunaFallbackV6RouteObserved", guiText, StringComparison.Ordinal);
        Assert.Contains("same-transfer V6 post-Tuna fallback", guiText, StringComparison.Ordinal);
        Assert.Contains("Resolve-TunaGuiLiveSwitchOffMinimumPeerVisiblePayloadBytes", guiText, StringComparison.Ordinal);
        Assert.Contains("NLINK_TUNA_GUI_LIVE_SWITCH_OFF_MIN_PEER_VISIBLE_PAYLOAD_BYTES", guiText, StringComparison.Ordinal);
        Assert.Contains("filetransfer_tuna_gui_live_switch_off_progress_gate_satisfied", guiText, StringComparison.Ordinal);
        Assert.Contains("Test-TunaGuiPeerVisibleFileTunaV4DataPathLine", guiText, StringComparison.Ordinal);
        Assert.Contains("filetransfer_tuna_data_path_peer_visible", guiText, StringComparison.Ordinal);
        Assert.Contains("actual_transport", guiText, StringComparison.Ordinal);
        Assert.Contains("route_publication_generation", guiText, StringComparison.Ordinal);
        Assert.Contains("routePublicationObserved", guiText, StringComparison.Ordinal);
        Assert.Contains("receiverCommittedProgressObserved", guiText, StringComparison.Ordinal);
        Assert.Contains("peerVisibleTunaPayloadObserved", guiText, StringComparison.Ordinal);
        Assert.Contains("terminalCompletedBeforeFault", guiText, StringComparison.Ordinal);
        Assert.Contains("faultInjected", guiText, StringComparison.Ordinal);
        Assert.Contains("dataPathProofMissing", guiText, StringComparison.Ordinal);
        Assert.Contains("missingTunaDataPathProof", guiText, StringComparison.Ordinal);
        Assert.Contains("data_path_proof_missing=", guiText, StringComparison.Ordinal);
        Assert.Contains("DataPathProofMissing = (-not $peerVisibleObserved)", guiText, StringComparison.Ordinal);
        Assert.Contains("completed_before_fault_injection", guiText, StringComparison.Ordinal);
        Assert.Contains("function Add-TunaGuiCompletedTerminalToTracker", guiText, StringComparison.Ordinal);
        Assert.Contains("$InboundCompletedByTransfer.ContainsKey($transferId)", guiText, StringComparison.Ordinal);
        Assert.Contains("elseif ($liveSwitchOffTerminalCompletedBeforeFault -and", guiText, StringComparison.Ordinal);
        Assert.Contains("$RouteMode -eq 'live-multi-toggle'", guiText, StringComparison.Ordinal);
        Assert.Contains("$RouteMode -eq 'live-reactivation-second-transfer' -and -not $liveSwitchOffTerminalCompletedBeforeFault", guiText, StringComparison.Ordinal);
        Assert.Contains("mode=peer_visible_payload", guiText, StringComparison.Ordinal);
        Assert.Contains("mode=receiver_committed", guiText, StringComparison.Ordinal);
        Assert.Contains("-MinimumPeerVisiblePayloadBytes $liveSwitchOffMinimumPeerVisiblePayloadBytes", guiText, StringComparison.Ordinal);
        Assert.Contains("Wait-TunaGuiFileTunaV4PublicationAfterBookmark -Bookmark $bookmark", guiText, StringComparison.Ordinal);
        Assert.DoesNotContain("$acceleratedPayloadBytes -gt 0L -and", guiText, StringComparison.Ordinal);
        Assert.DoesNotContain("tuna_acceleration_negotiated', 'route=file_tuna_v4", guiText, StringComparison.Ordinal);
        Assert.DoesNotContain("@('event=filetransfer_route_selected', (\"route={0}\" -f $Route)", guiText, StringComparison.Ordinal);
    }

    [Fact]
    public void RunFileTransferTunaGuiSmoke_LiveMultiToggleUsesRouteEpochSequence()
    {
        var repoRoot = FindRepoRoot();
        var wrapperPath = Path.Combine(repoRoot, "tools", "Run-FileTransferTunaGuiSmoke.ps1");
        var guiPath = Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1");
        var wrapperText = File.ReadAllText(wrapperPath);
        var guiText = File.ReadAllText(guiPath);

        Assert.Contains("\"live-multi-toggle\"", wrapperText, StringComparison.Ordinal);
        Assert.Contains("'live-multi-toggle'", guiText, StringComparison.Ordinal);
        Assert.Contains("NLINK_TUNA_GUI_LIVE_MULTI_TOGGLE_SEQUENCE", wrapperText, StringComparison.Ordinal);
        Assert.Contains("Get-TunaGuiLiveMultiToggleSequence", guiText, StringComparison.Ordinal);
        Assert.Contains("filetransfer_tuna_gui_live_multi_toggle_step", guiText, StringComparison.Ordinal);
        Assert.Contains("filetransfer_live_route_epoch_started", guiText, StringComparison.Ordinal);
        Assert.Contains("normal_to_tuna_activation", guiText, StringComparison.Ordinal);
        Assert.Contains("tuna_to_normal_fallback", guiText, StringComparison.Ordinal);
        Assert.Contains("liveRouteEpochs", guiText, StringComparison.Ordinal);
        Assert.Contains("liveRouteEpochRouteChanges", guiText, StringComparison.Ordinal);
        Assert.Contains("post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", guiText, StringComparison.Ordinal);
        Assert.Contains("$fallbackRouteLine = [string](Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $stepBookmark -NeedleSets @(", guiText, StringComparison.Ordinal);
        Assert.Contains("@('event=filetransfer_route_selected', 'route=post_tuna_fallback_v6', 'protocol_version=6')", guiText, StringComparison.Ordinal);
        Assert.Contains("$activationRouteLine = [string](Wait-TunaGuiFileTunaV4PublicationAfterBookmark -Bookmark $stepBookmark", guiText, StringComparison.Ordinal);
        Assert.Contains("-ActionKind 'RouteCommitPublication'", guiText, StringComparison.Ordinal);
        Assert.Contains("-RequirePositiveRouteCommitGeneration $true", guiText, StringComparison.Ordinal);
        Assert.DoesNotContain("$fallbackRouteLine = if ($fallbackStartedLine.IndexOf('route=post_tuna_fallback_v6'", guiText, StringComparison.Ordinal);
        Assert.DoesNotContain("$activationRouteLine = if ($activationStartedLine.IndexOf('route=file_tuna_v4'", guiText, StringComparison.Ordinal);
        Assert.Contains("same-transfer strict live-route epoch cycling", guiText, StringComparison.Ordinal);
    }

    [Fact]
    public void RunFileTransferTunaGuiSmoke_LiveRegularActivationCycleStartsRegularThenCyclesRoutes()
    {
        var repoRoot = FindRepoRoot();
        var wrapperPath = Path.Combine(repoRoot, "tools", "Run-FileTransferTunaGuiSmoke.ps1");
        var routeAcceptancePath = Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1");
        var guiPath = Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1");
        var wrapperText = File.ReadAllText(wrapperPath);
        var routeAcceptanceText = File.ReadAllText(routeAcceptancePath);
        var guiText = File.ReadAllText(guiPath);

        Assert.Contains("\"live-regular-activation-cycle\"", wrapperText, StringComparison.Ordinal);
        Assert.Contains("'live-regular-activation-cycle'", guiText, StringComparison.Ordinal);
        Assert.Contains("regular_nkn_v4_fast", guiText, StringComparison.Ordinal);
        Assert.Contains("on,off,on,off", guiText, StringComparison.Ordinal);
        Assert.Contains("RegularActivationCycle", wrapperText, StringComparison.Ordinal);
        Assert.Contains("measured_live_regular_activation_cycle", guiText, StringComparison.Ordinal);
        Assert.Contains("$activationProofTimeoutMs = if ($RouteMode -eq 'live-regular-activation-cycle' -or $RouteMode -eq 'live-reactivation-second-transfer') { 240000 } else { 90000 }", guiText, StringComparison.Ordinal);
        Assert.Contains("$regularActivationCycleCapBytes = [Math]::Max(1L, [long]($PayloadSizeBytes / 4))", guiText, StringComparison.Ordinal);
        Assert.Contains("AfterLiveRouteEpoch", guiText, StringComparison.Ordinal);
        Assert.Contains("[string]$AfterLine = ''", guiText, StringComparison.Ordinal);
        Assert.Contains("-AfterLine $AfterLine", guiText, StringComparison.Ordinal);
        Assert.Contains("$lastObservedLiveRouteEpoch", guiText, StringComparison.Ordinal);
        Assert.Contains("FallbackBookmark", guiText, StringComparison.Ordinal);
        Assert.Contains("-FallbackBookmark $bookmark", guiText, StringComparison.Ordinal);
        Assert.Contains("-AfterLine $activationStartedLine", guiText, StringComparison.Ordinal);
        Assert.Contains("-AfterLine $fallbackStartedLine", guiText, StringComparison.Ordinal);
        Assert.Contains("final_off_after_reactivation", guiText, StringComparison.Ordinal);
        Assert.Contains("regular_activation_cycle_off_after_live_tuna_epoch", guiText, StringComparison.Ordinal);
        Assert.Contains("file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", guiText, StringComparison.Ordinal);
        Assert.Contains("$terminalBeforeFallbackAllowedSequences = @(", guiText, StringComparison.Ordinal);
        Assert.Contains("'file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4'", guiText, StringComparison.Ordinal);
        Assert.Contains("$terminalBeforeFallbackExpectedRoutes = @($joinedRouteSequence.Split(',', [System.StringSplitOptions]::RemoveEmptyEntries))", guiText, StringComparison.Ordinal);
        Assert.Contains("fallbackEvidenceObserved", guiText, StringComparison.Ordinal);
        Assert.Contains("failure_class=terminal_before_fallback_switch_off", guiText, StringComparison.Ordinal);
        Assert.Contains("regular-to-Tuna and fallback/reactivation strict live-route epoch cycling", guiText, StringComparison.Ordinal);
        Assert.Contains("immediate_after_fallback_recovered=1", guiText, StringComparison.Ordinal);
        Assert.Contains("prearmed_on_step_{0}_immediate_after_fallback_recovered", guiText, StringComparison.Ordinal);
        Assert.Contains("terminal-before-second-activation evidence", guiText, StringComparison.Ordinal);
        Assert.DoesNotContain("accepting terminal fallback outcome for regular activation cycle", guiText, StringComparison.Ordinal);
        Assert.DoesNotContain("accepting terminal fallback outcome from retained terminal log evidence", guiText, StringComparison.Ordinal);
        Assert.Contains("filetransfer_tuna_gui_post_transfer_connected_header_wait_skipped", guiText, StringComparison.Ordinal);
        Assert.Contains("$skipPostTransferConnectedHeaderWait", guiText, StringComparison.Ordinal);
        Assert.Contains("terminal_fallback_outcome", guiText, StringComparison.Ordinal);
        Assert.Contains("$hasConnectedHeaderCleanupFailure", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("post_transfer_connected_header", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("connected header", routeAcceptanceText, StringComparison.Ordinal);
    }

    [Fact]
    public void RunFileTransferTunaGuiSmoke_LiveReactivationSecondTransferUsesSeparateProof()
    {
        var repoRoot = FindRepoRoot();
        var wrapperPath = Path.Combine(repoRoot, "tools", "Run-FileTransferTunaGuiSmoke.ps1");
        var routeAcceptancePath = Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1");
        var guiPath = Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1");
        var wrapperText = File.ReadAllText(wrapperPath);
        var routeAcceptanceText = File.ReadAllText(routeAcceptancePath);
        var guiText = File.ReadAllText(guiPath);

        Assert.Contains("\"live-reactivation-second-transfer\"", wrapperText, StringComparison.Ordinal);
        Assert.Contains("'live-reactivation-second-transfer'", guiText, StringComparison.Ordinal);
        Assert.Contains("measured_live_reactivation_file_tuna_v4", guiText, StringComparison.Ordinal);
        Assert.Contains("live_reactivation_v4", guiText, StringComparison.Ordinal);
        Assert.Contains("secondTransfer", guiText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-second-transfer-retained-log-slice.log", guiText, StringComparison.Ordinal);
        Assert.Contains("Wait-TunaGuiSecondTransferReadinessOrThrow", guiText, StringComparison.Ordinal);
        Assert.Contains("second_transfer_preactivation_readiness_missing", guiText, StringComparison.Ordinal);
        Assert.Contains("Tuna GUI second-transfer readiness did not stabilize: phase=preactivation_readiness", guiText, StringComparison.Ordinal);
        Assert.Contains("$lastTunaInactiveIndex", guiText, StringComparison.Ordinal);
        Assert.Contains("$requiredStableReadyPolls = 4", guiText, StringComparison.Ordinal);
        Assert.Contains("$allowCoordinatorProofAfterSend", guiText, StringComparison.Ordinal);
        Assert.Contains("$coordinatorProofAfterSendReady = $allowCoordinatorProofAfterSend -and", guiText, StringComparison.Ordinal);
        Assert.Contains("$isReadyNow = $coordinatorProofAfterSendReady -or", guiText, StringComparison.Ordinal);
        Assert.Contains("$routeReadinessSatisfied = $allowCoordinatorProofAfterSend -or", guiText, StringComparison.Ordinal);
        Assert.Contains("$RouteMode -eq 'live-reactivation-second-transfer') -and", guiText, StringComparison.Ordinal);
        Assert.Contains("reason=prearm_after_fallback_epoch_started", guiText, StringComparison.Ordinal);
        Assert.Contains("firstTransferTerminalBeforeLiveReactivation", guiText, StringComparison.Ordinal);
        Assert.Contains("terminal_before_same_transfer_reactivation", guiText, StringComparison.Ordinal);
        Assert.Contains("secondTransferRequiresFreshDiagnosticReadiness", guiText, StringComparison.Ordinal);
        Assert.Contains("filetransfer_tuna_gui_second_transfer_fresh_readiness_required", guiText, StringComparison.Ordinal);
        Assert.Contains("filetransfer_tuna_gui_second_transfer_fresh_unlock_not_completed", guiText, StringComparison.Ordinal);
        Assert.Contains("unlockError = $secondFreshUnlockError", guiText, StringComparison.Ordinal);
        Assert.Contains("first_transfer_terminal_before_same_transfer_reactivation", guiText, StringComparison.Ordinal);
        Assert.DoesNotContain("Tuna GUI second-transfer preactivation readiness missing", guiText, StringComparison.Ordinal);
        Assert.Contains("Tuna GUI live reactivation second-transfer proof did not prove clean fallback terminal followed by second-transfer Tuna V4", guiText, StringComparison.Ordinal);
        Assert.Contains("last_tuna_inactive_index=", guiText, StringComparison.Ordinal);
        Assert.Contains("required_stable_ready_polls=", guiText, StringComparison.Ordinal);
        Assert.Contains("-FileName 'filetransfer-second-transfer-retained-log-slice.log'", guiText, StringComparison.Ordinal);
        Assert.Contains("setupFailurePhase", guiText, StringComparison.Ordinal);
        Assert.Contains("$secondSetupPhaseKnown", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("$secondSetupReasonKnown", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("[string]::Equals($secondSetupPhase, 'unknown'", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("$result.setupFailurePhase = $secondSetupPhase", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("$result.setupFailureReason = $secondSetupReason", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("$secondRetainedPath = Join-Path $result.artifactDir 'filetransfer-second-transfer-retained-log-slice.log'", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("$secondRouteAnalysisPath = Join-Path $result.artifactDir 'second-transfer-analysis\\filetransfer-route-consistency-summary.txt'", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("Add-RouteAcceptanceFailure -Result $result -Message 'second transfer evidence is missing'", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("$status.ContainsKey('second_transfer_started')", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("$statusStartedValue = Get-RouteAcceptanceReportValue -Report $status -Name 'second_transfer_started'", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("$hasSecondTransferStartedStatus -and -not $secondTransferStartedFromStatus", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("second transfer did not start because first transfer setup failed", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("second-transfer-analysis", wrapperText, StringComparison.Ordinal);
        Assert.Contains("Ensure-TunaGuiSecondTransferRetainedLogSlice", wrapperText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-second-transfer-retained-log-slice-source.txt", wrapperText, StringComparison.Ordinal);
        Assert.Contains("source=reconstructed_from_retained_log_slice", wrapperText, StringComparison.Ordinal);
        Assert.Contains("second_transfer_retained_slice_reconstructed", wrapperText, StringComparison.Ordinal);
        Assert.DoesNotContain("event=file_transfer_send_requested", wrapperText, StringComparison.Ordinal);
        Assert.Contains("source=missing", wrapperText, StringComparison.Ordinal);
        Assert.Contains("second_transfer_retained_slice_missing", wrapperText, StringComparison.Ordinal);
        Assert.Contains("refusing to analyze the full retained log as second-transfer proof", wrapperText, StringComparison.Ordinal);
        Assert.DoesNotContain("Second-transfer retained analysis skipped because the scenario-specific retained slice is missing", wrapperText, StringComparison.Ordinal);
        Assert.DoesNotContain("Copy-Item -LiteralPath $retainedPath -Destination $secondRetainedPath", wrapperText, StringComparison.Ordinal);
        Assert.Contains("Invoke-TunaGuiLiveRetainedAnalysisBestEffort", wrapperText, StringComparison.Ordinal);
        Assert.Contains("Write-TunaGuiFailureSummaryIfMissing", wrapperText, StringComparison.Ordinal);
        Assert.Contains("$errorSummary.failurePhase", wrapperText, StringComparison.Ordinal);
        Assert.Contains("$errorSummary.failureReason", wrapperText, StringComparison.Ordinal);
        Assert.Contains("setupFailurePhase = $SetupFailurePhase", wrapperText, StringComparison.Ordinal);
        Assert.Contains("setupFailureReason = $SetupFailureReason", wrapperText, StringComparison.Ordinal);
        Assert.Contains("completed = $false", wrapperText, StringComparison.Ordinal);
        Assert.Contains("integrityOk = $false", wrapperText, StringComparison.Ordinal);
        Assert.Contains("post_tuna_fallback_v6,file_tuna_v4", guiText, StringComparison.Ordinal);
        Assert.Contains("Second transfer after live reactivation failed route/integrity check", guiText, StringComparison.Ordinal);
    }

    [Fact]
    public void RunFileTransferTunaGuiSmoke_TerminalSuccessRetainedTimeoutIsWarning()
    {
        var repoRoot = FindRepoRoot();
        var wrapperText = File.ReadAllText(Path.Combine(repoRoot, "tools", "Run-FileTransferTunaGuiSmoke.ps1"));
        var routeAcceptanceText = File.ReadAllText(Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"));

        foreach (var scriptText in new[] { wrapperText, routeAcceptanceText })
        {
            Assert.Contains("retained_analysis_timeout_after_terminal_success", scriptText, StringComparison.Ordinal);
            Assert.Contains("$terminalSuccess", scriptText, StringComparison.Ordinal);
            Assert.Contains("$terminalSuccessWithProof", scriptText, StringComparison.Ordinal);
            Assert.Contains("$routeProofOk", scriptText, StringComparison.Ordinal);
            Assert.Contains("terminal_success_without_retained_route_proof", scriptText, StringComparison.Ordinal);
            Assert.Contains("route_consistency_verdict", scriptText, StringComparison.Ordinal);
            Assert.Contains("'verdict={0}' -f $operatorVerdict", scriptText, StringComparison.Ordinal);
            Assert.Contains("if ($terminalSuccessTimeout)", scriptText, StringComparison.Ordinal);
            Assert.Contains("return", scriptText, StringComparison.Ordinal);
        }

        Assert.Contains("$guiSummaryRaw", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("\"completed\"\\s*:\\s*(true|false)", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("\"integrityOk\"\\s*:\\s*(true|false)", routeAcceptanceText, StringComparison.Ordinal);
    }

    [Fact]
    public void RetainedAnalysisCompletedProofsReapLingeringAnalyzerProcess()
    {
        var repoRoot = FindRepoRoot();
        var wrapperText = File.ReadAllText(Path.Combine(repoRoot, "tools", "Run-FileTransferTunaGuiSmoke.ps1"));
        var routeAcceptanceText = File.ReadAllText(Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"));
        var analyzerText = File.ReadAllText(Path.Combine(repoRoot, "tools", "FileTransferOps", "AnalyzerOrchestration.ps1"));

        Assert.Contains("Test-TunaGuiRetainedAnalysisCompletedProofArtifacts", wrapperText, StringComparison.Ordinal);
        Assert.Contains("Stop-TunaGuiRetainedAnalysisProcess", wrapperText, StringComparison.Ordinal);
        Assert.Contains("Test-RouteAcceptanceRetainedAnalysisCompletedProofArtifacts", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("Stop-RouteAcceptanceRetainedAnalysisProcess", routeAcceptanceText, StringComparison.Ordinal);

        foreach (var scriptText in new[] { wrapperText, routeAcceptanceText })
        {
            Assert.Contains("filetransfer-retained-analysis-completed-process-reaped.json", scriptText, StringComparison.Ordinal);
            Assert.Contains("completed_artifacts_observed_while_process_alive", scriptText, StringComparison.Ordinal);
            Assert.Contains("stability-gates-summary.txt", scriptText, StringComparison.Ordinal);
            Assert.Contains("hard_failure_count", scriptText, StringComparison.Ordinal);
            Assert.Contains("gate_status", scriptText, StringComparison.Ordinal);
            Assert.Contains("StartsWith('FAIL'", scriptText, StringComparison.Ordinal);
            Assert.Contains("StartsWith('INVALID'", scriptText, StringComparison.Ordinal);
            Assert.Contains("StartsWith('INCONCLUSIVE'", scriptText, StringComparison.Ordinal);
            Assert.Contains("$Process.WaitForExit(5000)", scriptText, StringComparison.Ordinal);
        }

        Assert.Contains("'stability-gates-summary.txt'", analyzerText, StringComparison.Ordinal);
        Assert.Contains("\"hard_failure_count={0}\" -f $hardFailureCount", analyzerText, StringComparison.Ordinal);
    }

    [Fact]
    public void RunFileTransferTunaGuiSmoke_MergesMilestoneEvidenceChronologically()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "Run-FileTransferTunaGuiSmoke.ps1");
        var scriptText = File.ReadAllText(scriptPath);

        Assert.Contains("Sort-TunaGuiRetainedLogLinesChronologically", scriptText, StringComparison.Ordinal);
        Assert.Contains("Get-TunaGuiRetainedLogLineSortKey", scriptText, StringComparison.Ordinal);
        Assert.Contains("Sort-Object -Property SortTicks, Ordinal", scriptText, StringComparison.Ordinal);
        Assert.DoesNotContain("$combined = @($missingLines.ToArray())", scriptText, StringComparison.Ordinal);
        Assert.DoesNotContain("$combined += $existingText", scriptText, StringComparison.Ordinal);
    }

    [Fact]
    public void RunFileTransferTunaGuiSmoke_ControlledFallbackFailureStillWritesMeasuredAnalysis()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "Run-FileTransferTunaGuiSmoke.ps1");
        var scriptText = File.ReadAllText(scriptPath);

        Assert.Contains("Invoke-TunaGuiMeasuredFallbackRetainedAnalysisBestEffort", scriptText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-retained-log-slice-full.log", scriptText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-setup-retained-log-slice.log", scriptText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-measured-fallback-retained-log-slice.log", scriptText, StringComparison.Ordinal);
        Assert.Contains("measured-fallback-analysis", scriptText, StringComparison.Ordinal);
        Assert.Contains("setup-analysis", scriptText, StringComparison.Ordinal);
        Assert.Contains("controlledRestartAnalysis", scriptText, StringComparison.Ordinal);
        Assert.Contains("fallbackFailurePhase", scriptText, StringComparison.Ordinal);
        Assert.Contains("fallbackDiagnostics", scriptText, StringComparison.Ordinal);
        Assert.Contains("lastCommittedChunk", scriptText, StringComparison.Ordinal);
        Assert.Contains("v6ChunkSendTimeoutCount", scriptText, StringComparison.Ordinal);
        Assert.Contains("fallbackWarningKinds", scriptText, StringComparison.Ordinal);
        Assert.Contains("sendTimeoutsPerMiB", scriptText, StringComparison.Ordinal);
        Assert.Contains("frontierRequestsPerMiB", scriptText, StringComparison.Ordinal);
        Assert.Contains("fallbackRescueFreezeCount", scriptText, StringComparison.Ordinal);
        Assert.Contains("fallbackRescueWidenCount", scriptText, StringComparison.Ordinal);
        Assert.Contains("setupNormalizedVerdict", scriptText, StringComparison.Ordinal);
        Assert.Contains("expected_controlled_setup_cancel", scriptText, StringComparison.Ordinal);
        Assert.Contains("Write-TunaGuiControlledRestartFailureSummary", scriptText, StringComparison.Ordinal);
        Assert.Contains("Test-TunaGuiLateSetupCleanupLine", scriptText, StringComparison.Ordinal);
        Assert.Contains("Test-TunaGuiControlledSetupCancelAccepted", scriptText, StringComparison.Ordinal);
        Assert.Contains("frame_type=filetransfer.cancel.v4", scriptText, StringComparison.Ordinal);
        Assert.Contains("Invoke-TunaGuiRetainedAnalysis -RepoRoot $repoRoot -AnalysisDir $resolvedArtifactDir", scriptText, StringComparison.Ordinal);

        var failureAnalysisIndex = scriptText.IndexOf("Invoke-TunaGuiMeasuredFallbackRetainedAnalysisBestEffort", StringComparison.Ordinal);
        var guiFailureBranchIndex = scriptText.IndexOf("if ($guiSmokeExitCode -ne 0)", StringComparison.Ordinal);
        var guiFailureThrowIndex = scriptText.IndexOf("GUI smoke failed with exit code", StringComparison.Ordinal);
        var missingSummaryBranchIndex = scriptText.IndexOf("if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf))", StringComparison.Ordinal);
        var missingSummaryThrowIndex = scriptText.IndexOf("GUI smoke did not write file-transfer Tuna summary", StringComparison.Ordinal);
        Assert.True(failureAnalysisIndex >= 0, "Expected best-effort measured fallback analysis helper.");
        Assert.True(guiFailureBranchIndex >= 0, "Expected GUI failure branch.");
        Assert.True(guiFailureThrowIndex > guiFailureBranchIndex, "Expected GUI failure throw.");
        Assert.Contains(
            "Invoke-TunaGuiMeasuredFallbackRetainedAnalysisBestEffort",
            scriptText[guiFailureBranchIndex..guiFailureThrowIndex],
            StringComparison.Ordinal);
        Assert.True(missingSummaryBranchIndex >= 0, "Expected missing-summary branch.");
        Assert.True(missingSummaryThrowIndex > missingSummaryBranchIndex, "Expected missing-summary throw.");
        Assert.Contains(
            "Invoke-TunaGuiMeasuredFallbackRetainedAnalysisBestEffort",
            scriptText[missingSummaryBranchIndex..missingSummaryThrowIndex],
            StringComparison.Ordinal);
    }

    [Fact]
    public void GuiSmokeWindows_ControlledFallbackWritesStablePhaseMarkersAndWaitsForSetupCleanup()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1");
        var scriptText = File.ReadAllText(scriptPath);

        Assert.Contains("phase=setup_file_tuna_v4_started", scriptText, StringComparison.Ordinal);
        Assert.Contains("phase=setup_file_tuna_v4_terminal", scriptText, StringComparison.Ordinal);
        Assert.Contains("phase=setup_file_tuna_v4_cleanup_closed", scriptText, StringComparison.Ordinal);
        Assert.Contains("phase=measured_post_tuna_fallback_v6_started", scriptText, StringComparison.Ordinal);
        Assert.Contains("phase=measured_post_tuna_fallback_v6_terminal", scriptText, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_data_session_removed", scriptText, StringComparison.Ordinal);
        Assert.Contains("V4 restart precondition setup cleanup closed", scriptText, StringComparison.Ordinal);
    }

    [Fact]
    public void GuiSmokeWindows_FileTransferNknScenariosAreRegistered()
    {
        var repoRoot = FindRepoRoot();
        var scriptText = File.ReadAllText(Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1"));

        Assert.Contains("FILETRANSFER_NKN_SOAK", scriptText, StringComparison.Ordinal);
        Assert.Contains("FILETRANSFER_NKN_MIXED_SOAK", scriptText, StringComparison.Ordinal);
        Assert.Contains("Run-ScenarioFileTransferNknSoak", scriptText, StringComparison.Ordinal);
        Assert.Contains("Run-ScenarioFileTransferNknMixedSoak", scriptText, StringComparison.Ordinal);
        Assert.Contains("NLINK_UNSAFE_DEVELOPER_MODE", scriptText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-live-nkn-cycles.jsonl", scriptText, StringComparison.Ordinal);
        Assert.Contains("NLINK_FILETRANSFER_SOAK_STARTUP_TIMEOUT_SECONDS", scriptText, StringComparison.Ordinal);
        Assert.Contains("NLINK_FILETRANSFER_SOAK_PROGRESS_TIMEOUT_SECONDS", scriptText, StringComparison.Ordinal);
        Assert.Contains("NLINK_FILETRANSFER_MIXED_SCREENSHARE_WARMUP_TIMEOUT_SECONDS", scriptText, StringComparison.Ordinal);
        Assert.Contains("Get-FileTransferSoakStartupTimeoutMs", scriptText, StringComparison.Ordinal);
        Assert.Contains("Get-FileTransferSoakProgressTimeoutMs", scriptText, StringComparison.Ordinal);
        Assert.Contains("Get-FileTransferMixedScreenShareWarmupTimeoutMs", scriptText, StringComparison.Ordinal);
    }

    [Fact]
    public void GuiSmokeWindows_FileTransferIntegrityResolverSearchesArtifactReceivedDirectory()
    {
        var repoRoot = FindRepoRoot();
        var scriptText = File.ReadAllText(Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1"));
        var resolveBody = ExtractPowerShellFunctionBody(scriptText, "Resolve-FileTransferLiveReceivedFilePath", "Find-FileTransferLiveReceivedFileByHash");
        var findBody = ExtractPowerShellFunctionBody(scriptText, "Find-FileTransferLiveReceivedFileByHash", "Append-FileTransferLiveHarnessDiagnostic");

        Assert.Contains("[string]$ArtifactDir", resolveBody, StringComparison.Ordinal);
        Assert.Contains("Join-Path $ArtifactDir 'received'", resolveBody, StringComparison.Ordinal);
        Assert.Contains("[string]$ArtifactDir", findBody, StringComparison.Ordinal);
        Assert.Contains("Join-Path $ArtifactDir 'received'", findBody, StringComparison.Ordinal);
        Assert.Contains("-ArtifactDir $ArtifactDir", scriptText, StringComparison.Ordinal);
    }

    [Fact]
    public void GuiSmokeWindows_FileTransferTerminalWaitRequiresCurrentCycleResolvedPath()
    {
        var repoRoot = FindRepoRoot();
        var scriptText = File.ReadAllText(Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1"));
        var functionBody = ExtractPowerShellFunctionBody(scriptText, "Wait-FileTransferTerminalPairAfterBookmark", "Append-FileTransferLiveCycleArtifact");

        Assert.Contains("$requiresResolvedInboundPath", functionBody, StringComparison.Ordinal);
        Assert.Contains("[string]::IsNullOrWhiteSpace($resolvedCandidatePath)", functionBody, StringComparison.Ordinal);
        Assert.Contains("filetransfer_live_terminal_ignored_unresolved_saved_path", functionBody, StringComparison.Ordinal);
        Assert.Contains("current_cycle_saved_path_unresolved", functionBody, StringComparison.Ordinal);
        Assert.Contains("continue", functionBody, StringComparison.Ordinal);
    }

    [Fact]
    public void GuiSmokeWindows_FileTransferProgressWatchdogCountsOnlyDataProgress()
    {
        var repoRoot = FindRepoRoot();
        var scriptText = File.ReadAllText(Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1"));
        var functionBody = ExtractPowerShellFunctionBody(scriptText, "Get-FileTransferLiveProgressScore", "Get-GuiSmokeInt64FieldValue");

        Assert.Contains("filetransfer_chunk_batch_sent_as_batch", functionBody, StringComparison.Ordinal);
        Assert.Contains("filetransfer_binary_frame_sent", functionBody, StringComparison.Ordinal);
        Assert.Contains("filetransfer_binary_frame_received", functionBody, StringComparison.Ordinal);
        Assert.Contains("raw_bytes_sent", functionBody, StringComparison.Ordinal);
        Assert.Contains("raw_bytes_received", functionBody, StringComparison.Ordinal);
        Assert.Contains("written_chunk_count", functionBody, StringComparison.Ordinal);
        Assert.Contains("written_bytes", functionBody, StringComparison.Ordinal);

        Assert.DoesNotContain("nkn_bridge_bulk_send_summary", functionBody, StringComparison.Ordinal);
        Assert.DoesNotContain("chunks_accepted_for_transport", functionBody, StringComparison.Ordinal);
        Assert.DoesNotContain("chunks_written", functionBody, StringComparison.Ordinal);
        Assert.DoesNotContain("bytes_written", functionBody, StringComparison.Ordinal);
    }

    [Fact]
    public void GuiSmokeWindows_AnyAllLogWaitNormalizesSingleNeedleSet()
    {
        var repoRoot = FindRepoRoot();
        var scriptText = File.ReadAllText(Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1"));
        var functionBody = ExtractPowerShellFunctionBody(scriptText, "Wait-AppLogContainsAnyAllAfterBookmark", "Unlock-TunaFromSessionHeader");

        Assert.Contains("$normalizedNeedleSets = @($NeedleSets)", functionBody, StringComparison.Ordinal);
        Assert.Contains("$flatStringNeedleSet = $true", functionBody, StringComparison.Ordinal);
        Assert.Contains("$normalizedNeedleSets = @(, $normalizedNeedleSets)", functionBody, StringComparison.Ordinal);
        Assert.Contains("foreach ($needleSet in $normalizedNeedleSets)", functionBody, StringComparison.Ordinal);
    }

    [Fact]
    public void GuiSmokeWindows_LiveTunaReactivationServicesLateWalletPrompt()
    {
        var repoRoot = FindRepoRoot();
        var scriptText = File.ReadAllText(Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1"));
        var unlockFunctionBody = ExtractPowerShellFunctionBody(scriptText, "Unlock-TunaFromSessionHeader", "Get-TunaGuiPayerRole");
        var liveCycleFunctionBody = ExtractPowerShellFunctionBody(scriptText, "Invoke-FileTransferTunaHandoffFallbackCycle", "Wait-FileTransferTerminalOrProgressBeforeAction");

        Assert.Contains("unlock_entry_existing_prompt", unlockFunctionBody, StringComparison.Ordinal);
        Assert.Contains("runtime_evidence_late_wallet_prompt", unlockFunctionBody, StringComparison.Ordinal);
        Assert.Contains("Submit-TunaWalletPasswordDialogsForPayersIfPresent", scriptText, StringComparison.Ordinal);
        Assert.Contains("prearmed_on_step_", liveCycleFunctionBody, StringComparison.Ordinal);
        Assert.Contains("activation_route_wait_step_", liveCycleFunctionBody, StringComparison.Ordinal);
        Assert.Contains("-OnPoll $serviceWalletPrompts", liveCycleFunctionBody, StringComparison.Ordinal);
    }

    [Fact]
    public void GuiSmokeWindows_ClickElementUsesAutomationFallbacksBeforeBoundsClick()
    {
        var repoRoot = FindRepoRoot();
        var scriptText = File.ReadAllText(Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1"));
        var functionBody = ExtractPowerShellFunctionBody(scriptText, "Click-Element", "Test-ElementValueMatchesText");

        Assert.Contains("InvokePattern", functionBody, StringComparison.Ordinal);
        Assert.Contains("TryGetClickablePoint", functionBody, StringComparison.Ordinal);
        Assert.Contains("LegacyIAccessiblePattern", functionBody, StringComparison.Ordinal);
        Assert.Contains("Cannot click element without bounds (Id=", functionBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeFileTransferPicker_AutopickFileUsesExistingEnvPathOnly()
    {
        var previous = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_AUTOPICK_FILE");
        using var unsafeDeveloperMode = ReleaseOverridePolicy.OverrideUnsafeDeveloperModeForTests(true);
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-autopick", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var payloadPath = Path.Combine(tempRoot, "payload.bin");
        await File.WriteAllBytesAsync(payloadPath, [1, 2, 3, 4, 5]);

        try
        {
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_AUTOPICK_FILE", payloadPath);
            var selection = await NativeFileTransferPicker.TryCreateAutomationSelectionForTestsAsync();

            Assert.NotNull(selection);
            Assert.Equal("payload.bin", selection.Descriptor.FileName);
            Assert.Equal(5, selection.Descriptor.FileSizeBytes);

            await using var stream = await selection.OpenReadStreamAsync(CancellationToken.None);
            Assert.Equal(5, stream.Length);

            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_AUTOPICK_FILE", Path.Combine(tempRoot, "missing.bin"));
            Assert.Null(await NativeFileTransferPicker.TryCreateAutomationSelectionForTestsAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_AUTOPICK_FILE", previous);
            TryDeleteDirectory(tempRoot);
        }
    }
}
