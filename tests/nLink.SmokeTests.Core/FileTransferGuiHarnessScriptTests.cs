using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NLink.App.Views;
using NLink.Core.Configuration;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "GuiHarness")]
[Trait("FileTransferEvidenceSuite", "OperationalLogIntegrity")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferGuiHarnessScriptTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    public async Task GuiSmokeSemanticCatalog_IncludesTerminalFrontierClosureEvents()
    {
        var helperPath = Path.Combine(FindRepoRoot(), "tools", "GuiSmokeLogLedger.ps1");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            . '{{helperPath.Replace("'", "''", StringComparison.Ordinal)}}'
            $names = @(Get-GuiSmokeSemanticEvidenceEventNames)
            foreach ($required in @('filetransfer_fallback_tail_terminal_proof_consumed', 'integrity_verify_passed', 'temp_finalize_succeeded')) {
                if ($required -notin $names) { throw "semantic event missing: $required" }
            }
            Write-Output 'terminal-frontier-semantic-catalog-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(
            result.ExitCode == 0,
            $"Terminal-frontier semantic catalog failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("terminal-frontier-semantic-catalog-ok", result.Stdout, StringComparison.Ordinal);
    }

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
    public void GuiSmoke_EnterSendFocusesExactChatInputBeforeSendingKey()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1");
        var scriptText = File.ReadAllText(scriptPath);
        var functionStart = scriptText.IndexOf("function Send-ChatMessage", StringComparison.Ordinal);
        Assert.True(functionStart >= 0, "Expected Send-ChatMessage to exist.");
        var functionEnd = scriptText.IndexOf("function Wait-MessageVisible", functionStart, StringComparison.Ordinal);
        Assert.True(functionEnd > functionStart, "Expected to locate the end of Send-ChatMessage.");

        var functionBody = scriptText[functionStart..functionEnd];
        var setFocusIndex = functionBody.IndexOf("$chatInput.SetFocus()", StringComparison.Ordinal);
        var exactFocusIndex = functionBody.IndexOf("[System.Windows.Automation.Automation]::Compare($focused, $chatInput)", StringComparison.Ordinal);
        var enterIndex = functionBody.IndexOf("[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')", StringComparison.Ordinal);

        Assert.True(setFocusIndex >= 0, "Expected Enter send to focus Chat.Input explicitly.");
        Assert.True(exactFocusIndex > setFocusIndex, "Expected exact Chat.Input focus validation after focus acquisition.");
        Assert.True(enterIndex > exactFocusIndex, "Expected Enter only after exact Chat.Input focus validation.");
        Assert.DoesNotContain("$focused.Current.ProcessId -eq $Window.Current.ProcessId", functionBody, StringComparison.Ordinal);
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
    public async Task GuiSmokeLogLedger_PreservesAppendAndRotationOrderWithoutReplay()
    {
        var repoRoot = FindRepoRoot();
        var helperPath = Path.Combine(repoRoot, "tools", "GuiSmokeLogLedger.ps1")
            .Replace("'", "''", StringComparison.Ordinal);
        var guiText = File.ReadAllText(Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1"));
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            . '{{helperPath}}'

            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-ledger-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $utf8 = [Text.UTF8Encoding]::new($false)
                $nl = [Environment]::NewLine
                $active = Join-Path $tempRoot 'nlink.log'
                $rotated1 = Join-Path $tempRoot 'nlink.1.log'
                $rotated2 = Join-Path $tempRoot 'nlink.2.log'
                $rotated3 = Join-Path $tempRoot 'nlink.3.log'
                $rotated4 = Join-Path $tempRoot 'nlink.4.log'
                $state = New-GuiSmokeLogLedgerState -MaxLines 100 -MaxGenerations 20

                [IO.File]::WriteAllText($active, "event=a${nl}event=b${nl}", $utf8)
                Sync-GuiSmokeLogLedger -State $state -Paths @($active)
                $bookmark = Get-GuiSmokeLogLedgerBookmark -State $state
                [IO.File]::AppendAllText($active, "event=c${nl}", $utf8)
                Sync-GuiSmokeLogLedger -State $state -Paths @($active)
                $afterAppend = @(Get-GuiSmokeLogLedgerLinesAfterBookmark -State $state -Bookmark $bookmark)
                if ($afterAppend.Count -ne 1 -or $afterAppend[0] -ne 'event=c') { throw "append mismatch: $($afterAppend -join '|')" }

                $partialBookmark = Get-GuiSmokeLogLedgerBookmark -State $state
                [IO.File]::AppendAllText($active, 'event=partial', $utf8)
                Sync-GuiSmokeLogLedger -State $state -Paths @($active)
                if (@(Get-GuiSmokeLogLedgerLinesAfterBookmark -State $state -Bookmark $partialBookmark).Count -ne 0) { throw 'partial trailing line was consumed' }
                [IO.File]::AppendAllText($active, $nl, $utf8)
                Sync-GuiSmokeLogLedger -State $state -Paths @($active)
                if ((@(Get-GuiSmokeLogLedgerLinesAfterBookmark -State $state -Bookmark $partialBookmark) -join '|') -ne 'event=partial') { throw 'completed trailing line was not consumed once' }

                Move-Item -LiteralPath $active -Destination $rotated1
                [IO.File]::WriteAllText($active, "event=d${nl}", $utf8)
                Sync-GuiSmokeLogLedger -State $state -Paths @($rotated1, $active)

                Move-Item -LiteralPath $rotated1 -Destination $rotated3
                Move-Item -LiteralPath $active -Destination $rotated2
                [IO.File]::WriteAllText($rotated1, "event=e${nl}", $utf8)
                [IO.File]::WriteAllText($active, "event=f${nl}", $utf8)
                Sync-GuiSmokeLogLedger -State $state -Paths @($rotated3, $rotated2, $rotated1, $active)

                Move-Item -LiteralPath $rotated3 -Destination $rotated4
                Sync-GuiSmokeLogLedger -State $state -Paths @($rotated4, $rotated2, $rotated1, $active)

                $duplicateBookmark = Get-GuiSmokeLogLedgerBookmark -State $state
                [IO.File]::AppendAllText($active, "event=duplicate${nl}event=duplicate${nl}", $utf8)
                Sync-GuiSmokeLogLedger -State $state -Paths @($rotated4, $rotated2, $rotated1, $active)
                $duplicates = @(Get-GuiSmokeLogLedgerLinesAfterBookmark -State $state -Bookmark $duplicateBookmark)
                if ($duplicates.Count -ne 2 -or $duplicates[0] -ne 'event=duplicate' -or $duplicates[1] -ne 'event=duplicate') {
                    throw "duplicate mismatch: $($duplicates -join '|')"
                }

                $expected = @('event=a', 'event=b', 'event=c', 'event=partial', 'event=d', 'event=e', 'event=f', 'event=duplicate', 'event=duplicate')
                $actual = @(Get-GuiSmokeLogLedgerLines -State $state)
                if (($actual -join '|') -ne ($expected -join '|')) { throw "ledger mismatch: $($actual -join '|')" }

                $settledBookmark = Get-GuiSmokeLogLedgerBookmark -State $state
                Sync-GuiSmokeLogLedger -State $state -Paths @($rotated4, $rotated2, $rotated1, $active)
                if (@(Get-GuiSmokeLogLedgerLinesAfterBookmark -State $state -Bookmark $settledBookmark).Count -ne 0) { throw 'consumed lines replayed' }
                if ((Get-GuiSmokeLogLedgerDiagnostics -State $state).GapCount -ne 0) { throw 'unexpected cursor gap' }
                Write-Output 'gui-log-ledger-rotation-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(result.ExitCode == 0, $"Ledger rotation test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-log-ledger-rotation-ok", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("GuiSmokeLogLedger.ps1", guiText, StringComparison.Ordinal);
        Assert.Contains("Reset-GuiSmokeLogLedgerState -State $script:GuiSmokeAppLogLedger", guiText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeWindows_LiveRouteWaitScansLedgerRangesIncrementally()
    {
        var repoRoot = FindRepoRoot();
        var helperPath = Path.Combine(repoRoot, "tools", "GuiSmokeLogLedger.ps1")
            .Replace("'", "''", StringComparison.Ordinal);
        var guiText = File.ReadAllText(Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1"));
        var waitBody = ExtractPowerShellFunctionBody(
            guiText,
            "Wait-TunaGuiLiveRouteEpochEvidence",
            "Wait-TunaGuiActiveBridgeQuietWindow");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            . '{{helperPath}}'
            function Wait-TunaGuiLiveRouteEpochEvidence { {{waitBody}} }
            function Assert-GuiSmokeScenarioDeadline {}
            function ConvertFrom-GuiSmokeSemicolonFields {
                param([string]$Message)
                $fields = @{}
                foreach ($part in @($Message -split ';')) {
                    $pair = $part.Trim() -split '=', 2
                    if ($pair.Count -eq 2) { $fields[$pair[0].Trim()] = $pair[1].Trim() }
                }
                return $fields
            }
            function Get-GuiSmokeFieldValue {
                param($Fields, [string]$Name, [string]$Default)
                if ($Fields.ContainsKey($Name)) { return [string]$Fields[$Name] }
                return $Default
            }
            function ConvertTo-GuiSmokeInt {
                param($Value, [int]$Default)
                $parsed = 0
                if ([int]::TryParse([string]$Value, [ref]$parsed)) { return $parsed }
                return $Default
            }

            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-route-wait-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $utf8 = [Text.UTF8Encoding]::new($false)
                $nl = [Environment]::NewLine
                $script:testLogPath = Join-Path $tempRoot 'nlink.log'
                [IO.File]::WriteAllText($script:testLogPath, "event=old${nl}event=after-marker${nl}", $utf8)
                $script:GuiSmokeAppLogLedger = New-GuiSmokeLogLedgerState
                Sync-GuiSmokeLogLedger -State $script:GuiSmokeAppLogLedger -Paths @($script:testLogPath)

                $script:syncCount = 0
                function Sync-GuiSmokeAppLogLedger {
                    $script:syncCount++
                    Sync-GuiSmokeLogLedger -State $script:GuiSmokeAppLogLedger -Paths @($script:testLogPath)
                }

                $script:rangeCalls = [Collections.Generic.List[string]]::new()
                $originalRangeVisitor = (Get-Command Invoke-GuiSmokeLogLedgerRangeVisitor).ScriptBlock
                function Invoke-GuiSmokeLogLedgerRangeVisitor {
                    param($State, [int]$StartIndex, [int]$EndIndex, [scriptblock]$Visitor)
                    $script:rangeCalls.Add(("$StartIndex`:$EndIndex"))
                    return & $originalRangeVisitor -State $State -StartIndex $StartIndex -EndIndex $EndIndex -Visitor $Visitor
                }

                $script:pollCount = 0
                $onPoll = {
                    $script:pollCount++
                    if ($script:pollCount -eq 1) {
                        [IO.File]::AppendAllText($script:testLogPath, "event=noise${nl}", $utf8)
                    }
                    elseif ($script:pollCount -eq 2) {
                        [IO.File]::AppendAllText(
                            $script:testLogPath,
                            "event=filetransfer_live_route_epoch_started; route=file_tuna_v4; protocol_version=4; handoff_kind=NormalToTunaActivation; target_transport=Tuna; live_route_epoch=2${nl}",
                            $utf8)
                    }
                }

                $matched = Wait-TunaGuiLiveRouteEpochEvidence `
                    -Bookmark 2 `
                    -FallbackBookmark 0 `
                    -Route file_tuna_v4 `
                    -ProtocolVersion 4 `
                    -HandoffKind NormalToTunaActivation `
                    -TargetTransport Tuna `
                    -EventName filetransfer_live_route_epoch_started `
                    -Description incremental-route-wait `
                    -AfterLine event=after-marker `
                    -TimeoutMs 5000 `
                    -OnPoll $onPoll

                if ($matched.IndexOf('live_route_epoch=2', [StringComparison]::Ordinal) -lt 0) { throw "unexpected match: $matched" }
                if ($script:syncCount -ne 2) { throw "expected one sync per poll, got $($script:syncCount)" }
                $expectedCalls = @('2:3', '0:3', '3:4', '3:4')
                if (($script:rangeCalls -join '|') -ne ($expectedCalls -join '|')) {
                    throw "range calls were not incremental: $($script:rangeCalls -join '|')"
                }

                $visited = [Collections.Generic.List[string]]::new()
                $visitResult = & $originalRangeVisitor `
                    -State $script:GuiSmokeAppLogLedger `
                    -StartIndex 1 `
                    -EndIndex 4 `
                    -Visitor {
                        param([string]$line, [int]$lineIndex)
                        $visited.Add(("$lineIndex=$line"))
                        return $null
                    }
                if (($visited -join '|') -ne '1=event=after-marker|2=event=noise|3=event=filetransfer_live_route_epoch_started; route=file_tuna_v4; protocol_version=4; handoff_kind=NormalToTunaActivation; target_transport=Tuna; live_route_epoch=2') {
                    throw "indexed range mismatch: $($visited -join '|')"
                }

                Write-Output 'gui-live-route-incremental-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(result.ExitCode == 0, $"Incremental live-route wait test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-live-route-incremental-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeWindows_LiveRouteWaitAcceptsExactCoordinatorTransitionWithoutLegacyProjection()
    {
        var repoRoot = FindRepoRoot();
        var helperPath = Path.Combine(repoRoot, "tools", "GuiSmokeLogLedger.ps1")
            .Replace("'", "''", StringComparison.Ordinal);
        var guiText = File.ReadAllText(Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1"));
        var waitBody = ExtractPowerShellFunctionBody(
            guiText,
            "Wait-TunaGuiLiveRouteEpochEvidence",
            "Wait-TunaGuiActiveBridgeQuietWindow");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            . '{{helperPath}}'
            function Wait-TunaGuiLiveRouteEpochEvidence { {{waitBody}} }
            function Assert-GuiSmokeScenarioDeadline {}
            function ConvertFrom-GuiSmokeSemicolonFields {
                param([string]$Message)
                $fields = @{}
                foreach ($part in @($Message -split ';')) {
                    $pair = $part.Trim() -split '=', 2
                    if ($pair.Count -eq 2) { $fields[$pair[0].Trim()] = $pair[1].Trim() }
                }
                return $fields
            }
            function Get-GuiSmokeFieldValue {
                param($Fields, [string]$Name, [string]$Default)
                if ($Fields.ContainsKey($Name)) { return [string]$Fields[$Name] }
                return $Default
            }
            function ConvertTo-GuiSmokeInt {
                param($Value, [int]$Default)
                $parsed = 0
                if ([int]::TryParse([string]$Value, [ref]$parsed)) { return $parsed }
                return $Default
            }
            function Test-GuiSmokeFieldEquals {
                param($Fields, [string]$Name, [string]$Expected)
                return [string]::Equals(
                    (Get-GuiSmokeFieldValue -Fields $Fields -Name $Name -Default ''),
                    $Expected,
                    [StringComparison]::OrdinalIgnoreCase)
            }

            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-route-authority-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $script:testLogPath = Join-Path $tempRoot 'nlink.log'
                $lines = @(
                    'event=filetransfer_route_transition_committed; session_id=s1; transfer_id=t1; direction=outbound; authority_id=a1; transition_generation=1; decision_status=Committed; projected_target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; handoff_kind=NormalToTunaActivation; target_runtime_generation=2; target_terminal_generation=0; target_leg_generation=2; target_route_publication_generation=2; target_route_commit_generation=1',
                    'event=filetransfer_route_hint_authorized_publication_accepted; session_id=s1; transfer_id=t1; direction=outbound; route=file_tuna_v4; protocol_version=4; live_route_epoch=1; runtime_generation=2; terminal_generation=0; leg_generation=2; route_publication_generation=2; route_commit_generation=1',
                    'event=filetransfer_route_transition_disposition_applied; session_id=s1; transfer_id=t1; direction=outbound; authority_id=a1; transition_generation=1; disposition=committed; resulting_route_publication_generation=2; resulting_route_commit_generation=1',
                    'event=filetransfer_data_path_send_result; session_id=s1; transfer_id=t1; direction=outbound; route=file_tuna_v4; protocol_version=4; runtime_generation=2; terminal_generation=0; leg_generation=2; route_publication_generation=2; route_commit_generation=1; delivery_result=sent'
                )
                [IO.File]::WriteAllLines($script:testLogPath, $lines, [Text.UTF8Encoding]::new($false))
                $script:GuiSmokeAppLogLedger = New-GuiSmokeLogLedgerState
                function Sync-GuiSmokeAppLogLedger {
                    Sync-GuiSmokeLogLedger -State $script:GuiSmokeAppLogLedger -Paths @($script:testLogPath)
                }

                $matched = Wait-TunaGuiLiveRouteEpochEvidence `
                    -Bookmark 0 `
                    -Route file_tuna_v4 `
                    -ProtocolVersion 4 `
                    -HandoffKind NormalToTunaActivation `
                    -TargetTransport Tuna `
                    -EventName filetransfer_live_route_epoch_started `
                    -Description authoritative-route-transition `
                    -TimeoutMs 2000 `
                    -RequireAuthoritativeProjection

                if ($matched.IndexOf('gui_authoritative_projection=1', [StringComparison]::Ordinal) -lt 0) {
                    throw "generic coordinator transition was not normalized: $matched"
                }
                Write-Output 'gui-live-route-authority-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(result.ExitCode == 0, $"Authoritative live-route wait test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-live-route-authority-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeLogLedger_CancellationBeforeCommitLeavesCursorReusable()
    {
        var helperPath = Path.Combine(FindRepoRoot(), "tools", "GuiSmokeLogLedger.ps1")
            .Replace("'", "''", StringComparison.Ordinal);
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            . '{{helperPath}}'

            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-ledger-cancel-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $logPath = Join-Path $tempRoot 'nlink.log'
                [IO.File]::WriteAllText(
                    $logPath,
                    ("event=one{0}event=two{0}" -f [Environment]::NewLine),
                    [Text.UTF8Encoding]::new($false))
                $state = New-GuiSmokeLogLedgerState
                $cancelled = $false
                try {
                    Sync-GuiSmokeLogLedger `
                        -State $state `
                        -Paths @($logPath) `
                        -CancellationProbe {
                            throw [OperationCanceledException]::new('test-deadline')
                        }
                }
                catch [OperationCanceledException] {
                    $cancelled = $_.Exception.Message -eq 'test-deadline'
                }
                if (-not $cancelled) { throw 'ledger synchronization did not propagate cancellation' }
                if ($state.Lines.Count -ne 0 -or $state.Generations.Count -ne 0) {
                    throw 'cancellation mutated the ledger before its atomic commit'
                }

                Sync-GuiSmokeLogLedger -State $state -Paths @($logPath)
                $lines = @(Get-GuiSmokeLogLedgerLines -State $state)
                if (($lines -join '|') -ne 'event=one|event=two') {
                    throw "ledger was not reusable after cancellation: $($lines -join '|')"
                }
                Write-Output 'gui-ledger-cancellation-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(result.ExitCode == 0, $"Ledger cancellation test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-ledger-cancellation-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeLogLedger_AtomicRangeCaptureAndCopyPreserveExactEvidence()
    {
        var helperPath = Path.Combine(FindRepoRoot(), "tools", "GuiSmokeLogLedger.ps1")
            .Replace("'", "''", StringComparison.Ordinal);
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            . '{{helperPath}}'

            function Get-TestSha256 {
                param([byte[]]$Bytes)
                $sha = [Security.Cryptography.SHA256]::Create()
                try {
                    return (([BitConverter]::ToString($sha.ComputeHash($Bytes))) -replace '-', '').ToLowerInvariant()
                }
                finally {
                    $sha.Dispose()
                }
            }

            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-atomic-capture-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $utf8 = [Text.UTF8Encoding]::new($false)
                $nl = [Environment]::NewLine
                $logPath = Join-Path $tempRoot 'nlink.log'
                [IO.File]::WriteAllText($logPath, "ignored${nl}alpha${nl}βeta${nl}last${nl}", $utf8)
                $state = New-GuiSmokeLogLedgerState
                Sync-GuiSmokeLogLedger -State $state -Paths @($logPath)

                $capturePath = Join-Path $tempRoot 'capture.log'
                $capture = Write-GuiSmokeLogLedgerRangeCapture `
                    -State $state `
                    -StartIndex 1 `
                    -EndIndex 4 `
                    -DestinationPath $capturePath
                $expectedBytes = $utf8.GetBytes("alpha${nl}βeta${nl}last")
                $capturedBytes = [IO.File]::ReadAllBytes($capturePath)
                if ([Convert]::ToBase64String($capturedBytes) -ne [Convert]::ToBase64String($expectedBytes)) {
                    throw 'range capture bytes did not match the exact UTF-8 payload'
                }
                if ($capturedBytes.Length -ge 3 -and
                    $capturedBytes[0] -eq 0xEF -and
                    $capturedBytes[1] -eq 0xBB -and
                    $capturedBytes[2] -eq 0xBF) {
                    throw 'range capture unexpectedly contains a UTF-8 BOM'
                }
                if ($capturedBytes.Length -gt 0 -and
                    ($capturedBytes[$capturedBytes.Length - 1] -eq 10 -or
                     $capturedBytes[$capturedBytes.Length - 1] -eq 13)) {
                    throw 'range capture unexpectedly has a trailing newline'
                }

                $expectedSha256 = Get-TestSha256 -Bytes $expectedBytes
                if ([string]$capture.Path -ne [IO.Path]::GetFullPath($capturePath) -or
                    [string]$capture.Sha256 -ne $expectedSha256 -or
                    [long]$capture.LineCount -ne 3 -or
                    [long]$capture.ByteCount -ne $expectedBytes.Length) {
                    throw "range capture evidence mismatch: $($capture | ConvertTo-Json -Compress)"
                }

                $copyPath = Join-Path $tempRoot 'copy.log'
                $copy = Copy-GuiSmokeFileAtomicCreateNew -SourcePath $capturePath -DestinationPath $copyPath
                $copiedBytes = [IO.File]::ReadAllBytes($copyPath)
                if ([Convert]::ToBase64String($copiedBytes) -ne [Convert]::ToBase64String($capturedBytes)) {
                    throw 'atomic copy bytes differ from the captured source'
                }
                if ([string]$copy.Path -ne [IO.Path]::GetFullPath($copyPath) -or
                    [string]$copy.Sha256 -ne $expectedSha256 -or
                    [long]$copy.LineCount -ne 3 -or
                    [long]$copy.ByteCount -ne $expectedBytes.Length) {
                    throw "atomic copy evidence mismatch: $($copy | ConvertTo-Json -Compress)"
                }

                $terminatedSourcePath = Join-Path $tempRoot 'terminated-source.log'
                $terminatedCopyPath = Join-Path $tempRoot 'terminated-copy.log'
                [IO.File]::WriteAllText($terminatedSourcePath, "first${nl}second${nl}", $utf8)
                $terminatedCopy = Copy-GuiSmokeFileAtomicCreateNew `
                    -SourcePath $terminatedSourcePath `
                    -DestinationPath $terminatedCopyPath
                if ([long]$terminatedCopy.LineCount -ne 2) {
                    throw "newline-terminated source line count was wrong: $($terminatedCopy.LineCount)"
                }

                $sentinelBytes = $utf8.GetBytes('do-not-overwrite')
                $existingCapturePath = Join-Path $tempRoot 'existing-capture.log'
                $existingCopyPath = Join-Path $tempRoot 'existing-copy.log'
                [IO.File]::WriteAllBytes($existingCapturePath, $sentinelBytes)
                [IO.File]::WriteAllBytes($existingCopyPath, $sentinelBytes)

                $captureRejected = $false
                try {
                    [void](Write-GuiSmokeLogLedgerRangeCapture `
                        -State $state `
                        -StartIndex 1 `
                        -EndIndex 4 `
                        -DestinationPath $existingCapturePath)
                }
                catch {
                    $captureRejected = $_.Exception.Message.IndexOf('already exists', [StringComparison]::OrdinalIgnoreCase) -ge 0
                }
                if (-not $captureRejected) { throw 'existing range-capture destination was not rejected' }

                $copyRejected = $false
                try {
                    [void](Copy-GuiSmokeFileAtomicCreateNew `
                        -SourcePath $capturePath `
                        -DestinationPath $existingCopyPath)
                }
                catch {
                    $copyRejected = $_.Exception.Message.IndexOf('already exists', [StringComparison]::OrdinalIgnoreCase) -ge 0
                }
                if (-not $copyRejected) { throw 'existing atomic-copy destination was not rejected' }

                if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($existingCapturePath)) -ne [Convert]::ToBase64String($sentinelBytes) -or
                    [Convert]::ToBase64String([IO.File]::ReadAllBytes($existingCopyPath)) -ne [Convert]::ToBase64String($sentinelBytes)) {
                    throw 'an existing destination was overwritten'
                }
                if (@(Get-ChildItem -LiteralPath $tempRoot -File | Where-Object Name -Like '.*.tmp').Count -ne 0) {
                    throw 'atomic helper left temporary files behind'
                }

                Write-Output 'gui-log-atomic-capture-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(result.ExitCode == 0, $"Atomic ledger capture test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-log-atomic-capture-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void GuiSmokeLogLedger_CanonicalRetainedSliceIsOwnedByFinalizer()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var finalizerBody = ExtractPowerShellFunctionBody(
            guiText,
            "Write-FileTransferGuiLogLedgerEvidence",
            "Write-TunaGuiMilestoneEvidence");
        var tunaCycleBody = ExtractPowerShellFunctionBody(
            guiText,
            "Invoke-FileTransferTunaHandoffFallbackCycle",
            "Wait-FileTransferTerminalOrProgressBeforeAction");

        Assert.Single(
            Regex.Matches(
                finalizerBody,
                @"-DestinationPath\s+\(Join-Path\s+\$ArtifactDir\s+'filetransfer-retained-log-slice\.log'\)"));

        var milestoneCopies = Regex.Matches(
            tunaCycleBody,
            @"(?m)^[ \t]*Copy-FileTransferLiveLogSlice[^\r\n]*-Bookmark[ \t]+\$bookmark[^\r\n]*");
        Assert.Empty(milestoneCopies);
        Assert.DoesNotContain(
            "'filetransfer-retained-log-slice.log'",
            tunaCycleBody,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GuiSmokeLogLedger_FinalizationStageJournalAdvancesMonotonically()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var stageBody = ExtractPowerShellFunctionBody(
            guiText,
            "Update-FileTransferGuiEvidenceFinalizationStage",
            "Copy-FileTransferLiveLogSlice");
        var writerBody = ExtractPowerShellFunctionBody(
            guiText,
            "Write-FileTransferGuiLogLedgerEvidence",
            "Write-TunaGuiMilestoneEvidence");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Update-FileTransferGuiEvidenceFinalizationStage { {{stageBody}} }

            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-finalization-stage-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $statePath = Join-Path $tempRoot 'filetransfer-gui-evidence-finalization-state.json'
                $env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH = $statePath
                [ordered]@{
                    schemaVersion = 1
                    acceptanceRunId = 'acceptance-run'
                    scenarioRunId = 'scenario-run'
                    lastCompletedStage = 'gui_child_started'
                    timedOut = $false
                    capturePath = ''
                    captureSha256 = ''
                    captureLineCount = 0
                } | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8

                Update-FileTransferGuiEvidenceFinalizationStage -Stage 'scenario_summary_persisted'
                Update-FileTransferGuiEvidenceFinalizationStage -Stage 'failure_record_persisted'
                Update-FileTransferGuiEvidenceFinalizationStage -Stage 'process_cleanup_started'
                Update-FileTransferGuiEvidenceFinalizationStage -Stage 'process_output_drained'
                Update-FileTransferGuiEvidenceFinalizationStage `
                    -Stage 'capture_persisted' `
                    -CapturePath 'frozen-capture.log' `
                    -CaptureSha256 'frozen-sha256' `
                    -CaptureLineCount 190000
                Update-FileTransferGuiEvidenceFinalizationStage `
                    -Stage 'capture_persisted' `
                    -CapturePath 'frozen-capture.log' `
                    -CaptureSha256 'frozen-sha256' `
                    -CaptureLineCount 190000
                $sameStageMutationRejected = $false
                try {
                    Update-FileTransferGuiEvidenceFinalizationStage `
                        -Stage 'capture_persisted' `
                        -CapturePath 'mutated-capture.log' `
                        -CaptureSha256 'mutated-sha256' `
                        -CaptureLineCount 1
                }
                catch {
                    $sameStageMutationRejected =
                        $_.Exception.Message -eq 'filetransfer_gui_evidence_finalization_stage_conflict:capture_persisted'
                }
                if (-not $sameStageMutationRejected) {
                    throw 'same-stage capture mutation was not rejected'
                }
                Update-FileTransferGuiEvidenceFinalizationStage `
                    -Stage 'writer_validation_completed' `
                    -CapturePath 'frozen-capture.log' `
                    -CaptureSha256 'frozen-sha256' `
                    -CaptureLineCount 190000
                Update-FileTransferGuiEvidenceFinalizationStage `
                    -Stage 'capture_persisted' `
                    -CapturePath 'mutated-capture.log' `
                    -CaptureSha256 'mutated-sha256' `
                    -CaptureLineCount 1
                Update-FileTransferGuiEvidenceFinalizationStage -Stage 'failure_classification_started'
                Update-FileTransferGuiEvidenceFinalizationStage -Stage 'failure_classification_completed'

                $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
                if ($state.lastCompletedStage -ne 'failure_classification_completed' -or
                    $state.capturePath -ne 'frozen-capture.log' -or
                    $state.captureSha256 -ne 'frozen-sha256' -or
                    [long]$state.captureLineCount -ne 190000 -or
                    [int]$state.guiChildProcessId -ne $PID) {
                    throw "finalization stage journal was not monotonic: $($state | ConvertTo-Json -Compress)"
                }

                $state.timedOut = $true
                $state | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8
                Update-FileTransferGuiEvidenceFinalizationStage `
                    -Stage 'gui_child_exited' `
                    -CapturePath 'late-capture.log' `
                    -CaptureSha256 'late-sha256' `
                    -CaptureLineCount 2
                $timedOutState = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
                if ($timedOutState.lastCompletedStage -ne 'failure_classification_completed' -or
                    $timedOutState.capturePath -ne 'frozen-capture.log' -or
                    $timedOutState.captureSha256 -ne 'frozen-sha256' -or
                    [long]$timedOutState.captureLineCount -ne 190000) {
                    throw "timed-out stage journal accepted a late update: $($timedOutState | ConvertTo-Json -Compress)"
                }
                if (@(Get-ChildItem -LiteralPath $tempRoot -File |
                        Where-Object { $_.Name -like '*.tmp' -or $_.Name -like '*.bak' }).Count -ne 0) {
                    throw 'finalization stage journal left temporary files behind'
                }

                Write-Output 'gui-finalization-stage-ok'
            }
            finally {
                Remove-Item Env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH -ErrorAction SilentlyContinue
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        Assert.DoesNotContain("Write-GuiSmokeLogLedgerRangeCapture", writerBody, StringComparison.Ordinal);
        Assert.Contains("$writerLedgerVisitCount++", writerBody, StringComparison.Ordinal);
        Assert.Contains("while ($ledgerIndex -lt $frozenWriterEvidenceEnd)", writerBody, StringComparison.Ordinal);
        Assert.Contains("$retainedSliceStream.Write", writerBody, StringComparison.Ordinal);
        Assert.Contains("Copy-GuiSmokeFileAtomicCreateNew", writerBody, StringComparison.Ordinal);
        Assert.Contains("-Stage 'process_cleanup_started'", writerBody, StringComparison.Ordinal);
        Assert.Contains("-Stage 'process_output_drained'", writerBody, StringComparison.Ordinal);
        Assert.Contains("-Stage 'capture_persisted'", writerBody, StringComparison.Ordinal);
        Assert.Contains("-Stage 'writer_validation_completed'", writerBody, StringComparison.Ordinal);
        Assert.DoesNotContain("$capturedLines", writerBody, StringComparison.Ordinal);
        Assert.DoesNotContain("GetRange(", writerBody, StringComparison.Ordinal);

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(result.ExitCode == 0, $"Finalization stage journal test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-finalization-stage-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TunaGuiWrapper_HardDeadlinePersistsFailClosedEvidenceAndCleansChild()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var wrapperText = File.ReadAllText(Path.Combine(repoRoot, "tools", "Run-FileTransferTunaGuiSmoke.ps1"));
        var guiSmokeText = File.ReadAllText(Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1"));
        var scenarioDeadlineBody = ExtractPowerShellFunctionBody(
            guiSmokeText,
            "Assert-GuiSmokeScenarioDeadline",
            "Wait-Until");
        var convertArgumentBody = ExtractPowerShellFunctionBody(
            wrapperText,
            "ConvertTo-TunaGuiProcessArgument",
            "Write-TunaGuiAtomicJson");
        var atomicJsonBody = ExtractPowerShellFunctionBody(
            wrapperText,
            "Write-TunaGuiAtomicJson",
            "Get-TunaGuiFinalizationGraceSeconds");
        var graceBody = ExtractPowerShellFunctionBody(
            wrapperText,
            "Get-TunaGuiFinalizationGraceSeconds",
            "Complete-TunaGuiChildOutputCapture");
        var childOutputBody = ExtractPowerShellFunctionBody(
            wrapperText,
            "Complete-TunaGuiChildOutputCapture",
            "Stop-TunaGuiSmokeProcessTree");
        var stopTreeBody = ExtractPowerShellFunctionBody(
            wrapperText,
            "Stop-TunaGuiSmokeProcessTree",
            "Stop-TunaGuiSmokeApplicationDescendants");
        var stopApplicationsBody = ExtractPowerShellFunctionBody(
            wrapperText,
            "Stop-TunaGuiSmokeApplicationDescendants",
            "Request-TunaGuiScenarioDeadline");
        var requestDeadlineBody = ExtractPowerShellFunctionBody(
            wrapperText,
            "Request-TunaGuiScenarioDeadline",
            "Get-TunaGuiTerminalPhaseDeadlineRequest");
        var terminalPhaseDeadlineBody = ExtractPowerShellFunctionBody(
            wrapperText,
            "Get-TunaGuiTerminalPhaseDeadlineRequest",
            "Invoke-TunaGuiSmokeWithHardDeadline");
        var watchdogBody = ExtractPowerShellFunctionBody(
            wrapperText,
            "Invoke-TunaGuiSmokeWithHardDeadline",
            "Test-TunaGuiLateSetupCleanupLine");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function ConvertTo-TunaGuiProcessArgument { {{convertArgumentBody}} }
            function Write-TunaGuiAtomicJson { {{atomicJsonBody}} }
            function Get-TunaGuiFinalizationGraceSeconds { {{graceBody}} }
            function Complete-TunaGuiChildOutputCapture { {{childOutputBody}} }
            function Stop-TunaGuiSmokeProcessTree { {{stopTreeBody}} }
            function Stop-TunaGuiSmokeApplicationDescendants { {{stopApplicationsBody}} }
            function Request-TunaGuiScenarioDeadline { {{requestDeadlineBody}} }
            function Get-TunaGuiTerminalPhaseDeadlineRequest { {{terminalPhaseDeadlineBody}} }
            function Invoke-TunaGuiSmokeWithHardDeadline { {{watchdogBody}} }
            function Assert-GuiSmokeScenarioDeadline { {{scenarioDeadlineBody}} }

            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-tuna-gui-watchdog-' + [guid]::NewGuid().ToString('N'))
            $toolsDir = Join-Path $tempRoot 'tools'
            $cooperativeArtifact = Join-Path $tempRoot 'cooperative-artifact'
            $routeWaitArtifact = Join-Path $tempRoot 'route-wait-artifact'
            $terminalPhaseArtifact = Join-Path $tempRoot 'terminal-phase-artifact'
            $timeoutArtifact = Join-Path $tempRoot 'timeout-artifact'
            $successArtifact = Join-Path $tempRoot 'success-artifact'
            $nonzeroArtifact = Join-Path $tempRoot 'nonzero-artifact'
            [void][IO.Directory]::CreateDirectory($toolsDir)
            [void][IO.Directory]::CreateDirectory($cooperativeArtifact)
            [void][IO.Directory]::CreateDirectory($routeWaitArtifact)
            [void][IO.Directory]::CreateDirectory($terminalPhaseArtifact)
            [void][IO.Directory]::CreateDirectory($timeoutArtifact)
            [void][IO.Directory]::CreateDirectory($successArtifact)
            [void][IO.Directory]::CreateDirectory($nonzeroArtifact)
            try {
                $deadlineProbePath = Join-Path $tempRoot 'scenario-deadline-request.json'
                $script:GuiSmokeScenarioDeadlineUtc = [DateTimeOffset]::MaxValue
                $script:GuiSmokeScenarioDeadlineName = 'deadline-probe'
                $env:NLINK_FILETRANSFER_GUI_SCENARIO_DEADLINE_REQUEST_PATH = $deadlineProbePath
                Assert-GuiSmokeScenarioDeadline
                '{}' | Set-Content -LiteralPath $deadlineProbePath -Encoding UTF8
                $deadlineCancelled = $false
                try {
                    Assert-GuiSmokeScenarioDeadline
                }
                catch [OperationCanceledException] {
                    $deadlineCancelled = $_.Exception.Message -eq 'gui_smoke_scenario_deadline_exceeded: scenario=deadline-probe'
                }
                if (-not $deadlineCancelled) {
                    throw 'GUI child did not consume the wrapper scenario-deadline request'
                }
                Remove-Item -LiteralPath $deadlineProbePath -Force

                $invalidPhasePath = Join-Path $tempRoot 'invalid-terminal-phase.json'
                $invalidStartedUtc = [DateTimeOffset]::UtcNow
                [ordered]@{
                    event = 'filetransfer_gui_terminal_phase_deadline_requested'
                    acceptanceRunId = 'acceptance-run'
                    scenarioRunId = 'scenario-run'
                    routeMode = 'live-regular-activation-cycle'
                    phase = 'terminal_integrity'
                    phaseGeneration = 1
                    actionBookmark = 10
                    proofEndBookmark = 20
                    liveRouteEpoch = 4
                    timeoutMilliseconds = 1000
                    requestedUtc = $invalidStartedUtc.ToString('O')
                    deadlineUtc = $invalidStartedUtc.AddSeconds(3).ToString('O')
                } | ConvertTo-Json | Set-Content -LiteralPath $invalidPhasePath -Encoding UTF8
                $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID = 'acceptance-run'
                $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID = 'scenario-run'
                $invalidPhase = Get-TunaGuiTerminalPhaseDeadlineRequest `
                    -Path $invalidPhasePath `
                    -StartedUtc $invalidStartedUtc `
                    -OriginalScenarioDeadlineUtc ($invalidStartedUtc.AddSeconds(1)) `
                    -TimeoutSeconds 1
                if ($null -ne $invalidPhase) {
                    throw 'out-of-bound terminal phase request was accepted'
                }

                $script:stoppedApplicationPids = [System.Collections.Generic.List[int]]::new()
                function Get-CimInstance {
                    [CmdletBinding()]
                    param([Parameter(Position = 0)]$ClassName, [string]$Filter)
                    if (-not [string]::IsNullOrWhiteSpace($Filter)) {
                        return [pscustomobject]@{
                            ProcessId = 101
                            ParentProcessId = 100
                            ExecutablePath = 'C:\target.exe'
                            CreationDate = 'target-created'
                        }
                    }
                    return @(
                        [pscustomobject]@{ ProcessId = 100; ParentProcessId = 1; ExecutablePath = 'powershell.exe'; CreationDate = 'child-created' },
                        [pscustomobject]@{ ProcessId = 101; ParentProcessId = 100; ExecutablePath = 'C:\target.exe'; CreationDate = 'target-created' },
                        [pscustomobject]@{ ProcessId = 102; ParentProcessId = 100; ExecutablePath = 'C:\other.exe'; CreationDate = 'other-created' })
                }
                function Get-Process {
                    [CmdletBinding()]
                    param([int]$Id)
                    return [pscustomobject]@{ Id = $Id }
                }
                function Stop-Process {
                    [CmdletBinding()]
                    param([int]$Id, [switch]$Force)
                    $script:stoppedApplicationPids.Add($Id) | Out-Null
                }
                Stop-TunaGuiSmokeApplicationDescendants `
                    -GuiChildProcess ([pscustomobject]@{ Id = 100 }) `
                    -ExePath 'C:\target.exe' `
                    -GraceMilliseconds 1
                if (($script:stoppedApplicationPids -join ',') -ne '101') {
                    throw "application descendant cleanup was not exact: $($script:stoppedApplicationPids -join ',')"
                }
                Remove-Item function:Get-CimInstance
                Remove-Item function:Get-Process
                Remove-Item function:Stop-Process

                $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID = 'acceptance-run'
                $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID = 'scenario-run'
                $env:NLINK_FILETRANSFER_GUI_FINALIZATION_GRACE_SECONDS_TEST_ONLY = '1'
                $env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH = 'sentinel-state-path'
                $env:NLINK_FILETRANSFER_GUI_SCENARIO_DEADLINE_REQUEST_PATH = 'sentinel-request-path'
                $env:NLINK_FILETRANSFER_GUI_TERMINAL_PHASE_DEADLINE_PATH = 'sentinel-terminal-phase-path'
                $stubPath = Join-Path $toolsDir 'GuiSmoke-Windows.ps1'
                @'
            param([string]$ExePath, [int]$TimeoutSeconds)
            while (-not (Test-Path -LiteralPath $env:NLINK_FILETRANSFER_GUI_SCENARIO_DEADLINE_REQUEST_PATH -PathType Leaf)) {
                Start-Sleep -Milliseconds 25
            }
            $state = Get-Content -LiteralPath $env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH -Raw | ConvertFrom-Json
            $state.lastCompletedStage = 'capture_persisted'
            $state | Add-Member -NotePropertyName capturePath -NotePropertyValue 'cooperative-capture.log'
            $state | Add-Member -NotePropertyName captureSha256 -NotePropertyValue 'cooperative-sha256'
            $state | Add-Member -NotePropertyName captureLineCount -NotePropertyValue 7
            $state | ConvertTo-Json | Set-Content -LiteralPath $env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH -Encoding UTF8
            exit 1
            '@ | Set-Content -LiteralPath $stubPath -Encoding UTF8
                $cooperative = Invoke-TunaGuiSmokeWithHardDeadline `
                    -RepoRoot $tempRoot `
                    -ArtifactDir $cooperativeArtifact `
                    -ExePath 'unused.exe' `
                    -TimeoutSeconds 1
                if ($cooperative.TimedOut) {
                    throw "cooperative scenario deadline did not preserve child finalization: $($cooperative | ConvertTo-Json -Compress)"
                }
                if (Test-Path -LiteralPath (Join-Path $cooperativeArtifact 'filetransfer-gui-evidence-finalization-timeout.json')) {
                    throw 'cooperative scenario deadline wrote a hard-timeout marker'
                }
                $cooperativeState = Get-Content -LiteralPath (Join-Path $cooperativeArtifact 'filetransfer-gui-evidence-finalization-state.json') -Raw | ConvertFrom-Json
                if (-not $cooperativeState.scenarioDeadlineRequested -or
                    $cooperativeState.childLastCompletedStage -ne 'capture_persisted' -or
                    $cooperativeState.capturePath -ne 'cooperative-capture.log') {
                    throw "cooperative finalization state was incomplete: $($cooperativeState | ConvertTo-Json -Compress)"
                }

                @'
            param([string]$ExePath, [int]$TimeoutSeconds)
            Start-Sleep -Milliseconds 600
            $requestedUtc = [DateTimeOffset]::UtcNow
            [ordered]@{
                schemaVersion = 1
                event = 'filetransfer_gui_terminal_phase_deadline_requested'
                acceptanceRunId = [string]$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID
                scenarioRunId = [string]$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID
                routeMode = 'live-regular-activation-cycle'
                phase = 'terminal_integrity'
                phaseGeneration = 1
                actionBookmark = 10
                proofEndBookmark = 20
                liveRouteEpoch = 4
                timeoutMilliseconds = 1000
                requestedUtc = $requestedUtc.ToString('O')
                deadlineUtc = $requestedUtc.AddSeconds(1).ToString('O')
                authorityEffect = 'none'
            } | ConvertTo-Json | Set-Content -LiteralPath $env:NLINK_FILETRANSFER_GUI_TERMINAL_PHASE_DEADLINE_PATH -Encoding UTF8
            Start-Sleep -Milliseconds 800
            exit 0
            '@ | Set-Content -LiteralPath $stubPath -Encoding UTF8
                $terminalPhase = Invoke-TunaGuiSmokeWithHardDeadline `
                    -RepoRoot $tempRoot `
                    -ArtifactDir $terminalPhaseArtifact `
                    -ExePath 'unused.exe' `
                    -TimeoutSeconds 1
                $terminalPhaseState = Get-Content -LiteralPath (Join-Path $terminalPhaseArtifact 'filetransfer-gui-evidence-finalization-state.json') -Raw | ConvertFrom-Json
                if ($terminalPhase.TimedOut -or
                    -not $terminalPhaseState.terminalPhaseDeadlineAdopted -or
                    [string]::IsNullOrWhiteSpace([string]$terminalPhaseState.terminalPhaseDeadlineUtc) -or
                    $terminalPhaseState.scenarioDeadlineRequested -or
                    (Test-Path -LiteralPath (Join-Path $terminalPhaseArtifact 'filetransfer-gui-scenario-deadline-request.json'))) {
                    throw "terminal phase deadline was not adopted exactly once: result=$($terminalPhase | ConvertTo-Json -Compress); state=$($terminalPhaseState | ConvertTo-Json -Compress)"
                }

                @'
            param([string]$ExePath, [int]$TimeoutSeconds)
            Start-Sleep -Seconds 10
            '@ | Set-Content -LiteralPath $stubPath -Encoding UTF8
                $routeWaitTimedOut = Invoke-TunaGuiSmokeWithHardDeadline `
                    -RepoRoot $tempRoot `
                    -ArtifactDir $routeWaitArtifact `
                    -ExePath 'unused.exe' `
                    -TimeoutSeconds 1
                $routeWaitTimeout = Get-Content `
                    -LiteralPath (Join-Path $routeWaitArtifact 'filetransfer-gui-evidence-finalization-timeout.json') `
                    -Raw |
                    ConvertFrom-Json
                if (-not $routeWaitTimedOut.TimedOut -or
                    [int]$routeWaitTimedOut.ExitCode -ne 124 -or
                    $routeWaitTimeout.lastCompletedStage -ne 'gui_child_started' -or
                    -not $routeWaitTimeout.permanentlyNonAuthoritative) {
                    throw "route-wait hang was not bounded fail-closed: result=$($routeWaitTimedOut | ConvertTo-Json -Compress); timeout=$($routeWaitTimeout | ConvertTo-Json -Compress)"
                }

                @'
            param([string]$ExePath, [int]$TimeoutSeconds)
            Start-Sleep -Milliseconds 500
            $state = [ordered]@{
                schemaVersion = 1
                lastCompletedStage = 'capture_persisted'
                capturePath = 'capture.log'
                captureSha256 = 'abc123'
                captureLineCount = 42
            }
            $state | ConvertTo-Json | Set-Content -LiteralPath $env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH -Encoding UTF8
            Start-Sleep -Seconds 10
            '@ | Set-Content -LiteralPath $stubPath -Encoding UTF8

                $timedOut = Invoke-TunaGuiSmokeWithHardDeadline `
                    -RepoRoot $tempRoot `
                    -ArtifactDir $timeoutArtifact `
                    -ExePath 'unused.exe' `
                    -TimeoutSeconds 1
                if (-not $timedOut.TimedOut -or [int]$timedOut.ExitCode -ne 124) {
                    throw "watchdog did not return timeout: $($timedOut | ConvertTo-Json -Compress)"
                }
                if ($env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH -ne 'sentinel-state-path') {
                    throw 'watchdog did not restore the parent state-path environment value'
                }
                if ($env:NLINK_FILETRANSFER_GUI_SCENARIO_DEADLINE_REQUEST_PATH -ne 'sentinel-request-path') {
                    throw 'watchdog did not restore the parent deadline-request environment value'
                }
                if ($env:NLINK_FILETRANSFER_GUI_TERMINAL_PHASE_DEADLINE_PATH -ne 'sentinel-terminal-phase-path') {
                    throw 'watchdog did not restore the parent terminal-phase environment value'
                }
                $timeoutPath = Join-Path $timeoutArtifact 'filetransfer-gui-evidence-finalization-timeout.json'
                $statePath = Join-Path $timeoutArtifact 'filetransfer-gui-evidence-finalization-state.json'
                $timeout = Get-Content -LiteralPath $timeoutPath -Raw | ConvertFrom-Json
                $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
                if (-not $timeout.permanentlyNonAuthoritative -or
                    $timeout.lastCompletedStage -ne 'capture_persisted' -or
                    $timeout.capturePath -ne 'capture.log' -or
                    $timeout.captureSha256 -ne 'abc123' -or
                    [long]$timeout.captureLineCount -ne 42 -or
                    -not $state.timedOut -or
                    [int]$state.exitCode -ne 124) {
                    throw "timeout evidence was incomplete: timeout=$($timeout | ConvertTo-Json -Compress); state=$($state | ConvertTo-Json -Compress)"
                }
                if ($null -ne (Get-Process -Id ([int]$timeout.guiChildProcessId) -ErrorAction SilentlyContinue)) {
                    throw 'timed-out GUI child remained alive'
                }

                @'
            param([string]$ExePath, [int]$TimeoutSeconds)
            exit 0
            '@ | Set-Content -LiteralPath $stubPath -Encoding UTF8
                $completed = Invoke-TunaGuiSmokeWithHardDeadline `
                    -RepoRoot $tempRoot `
                    -ArtifactDir $successArtifact `
                    -ExePath 'unused.exe' `
                    -TimeoutSeconds 1
                if ($completed.TimedOut -or
                    -not $completed.ExitCodeAvailable -or
                    [int]$completed.ExitCode -ne 0) {
                    throw "natural child exit lost the timeout race: $($completed | ConvertTo-Json -Compress)"
                }
                if (Test-Path -LiteralPath (Join-Path $successArtifact 'filetransfer-gui-evidence-finalization-timeout.json')) {
                    throw 'natural child exit wrote a timeout marker'
                }
                $successState = Get-Content -LiteralPath (Join-Path $successArtifact 'filetransfer-gui-evidence-finalization-state.json') -Raw | ConvertFrom-Json
                if ($successState.lastCompletedStage -ne 'gui_child_exited' -or
                    $successState.timedOut -or
                    -not [bool]$successState.exitCodeAvailable -or
                    [int]$successState.exitCode -ne 0 -or
                    $successState.exitCodeCaptureBranch -ne 'owned_process_exit_code' -or
                    -not [bool]$successState.processHasExited -or
                    -not [bool]$successState.outputCaptureCompleted) {
                    throw "natural child state was invalid: $($successState | ConvertTo-Json -Compress)"
                }

                @'
            param([string]$ExePath, [int]$TimeoutSeconds)
            Write-Output 'child-stdout-marker'
            [Console]::Error.WriteLine('child-stderr-marker')
            exit 7
            '@ | Set-Content -LiteralPath $stubPath -Encoding UTF8
                $nonzero = Invoke-TunaGuiSmokeWithHardDeadline `
                    -RepoRoot $tempRoot `
                    -ArtifactDir $nonzeroArtifact `
                    -ExePath 'unused.exe' `
                    -TimeoutSeconds 1
                $nonzeroState = Get-Content -LiteralPath (Join-Path $nonzeroArtifact 'filetransfer-gui-evidence-finalization-state.json') -Raw | ConvertFrom-Json
                $nonzeroStdout = Get-Content -LiteralPath (Join-Path $nonzeroArtifact 'gui-smoke-stdout.log') -Raw
                $nonzeroStderr = Get-Content -LiteralPath (Join-Path $nonzeroArtifact 'gui-smoke-stderr.log') -Raw
                if ($nonzero.TimedOut -or
                    -not $nonzero.ExitCodeAvailable -or
                    [int]$nonzero.ExitCode -ne 7 -or
                    -not [bool]$nonzeroState.exitCodeAvailable -or
                    [int]$nonzeroState.exitCode -ne 7 -or
                    $nonzeroState.exitCodeCaptureBranch -ne 'owned_process_exit_code' -or
                    $nonzeroStdout -notmatch 'child-stdout-marker' -or
                    $nonzeroStderr -notmatch 'child-stderr-marker') {
                    throw "nonzero child exit/output was not preserved: result=$($nonzero | ConvertTo-Json -Compress); state=$($nonzeroState | ConvertTo-Json -Compress)"
                }

                Write-Output 'tuna-gui-watchdog-ok'
            }
            finally {
                Remove-Item Env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID -ErrorAction SilentlyContinue
                Remove-Item Env:NLINK_FILETRANSFER_SCENARIO_RUN_ID -ErrorAction SilentlyContinue
                Remove-Item Env:NLINK_FILETRANSFER_GUI_FINALIZATION_GRACE_SECONDS_TEST_ONLY -ErrorAction SilentlyContinue
                Remove-Item Env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH -ErrorAction SilentlyContinue
                Remove-Item Env:NLINK_FILETRANSFER_GUI_SCENARIO_DEADLINE_REQUEST_PATH -ErrorAction SilentlyContinue
                Remove-Item Env:NLINK_FILETRANSFER_GUI_TERMINAL_PHASE_DEADLINE_PATH -ErrorAction SilentlyContinue
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(result.ExitCode == 0, $"Tuna GUI watchdog test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("tuna-gui-watchdog-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TunaGuiWrapper_ValidatesCurrentFallbackSummaryContract()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var wrapperText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "Run-FileTransferTunaGuiSmoke.ps1"));
        var validatorBody = ExtractPowerShellFunctionBody(
            wrapperText,
            "Test-TunaGuiCurrentSummary",
            "Update-TunaGuiControlledRestartSummary");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Test-TunaGuiCurrentSummary { {{validatorBody}} }

            $valid = [pscustomobject]@{
                event = 'filetransfer_tuna_gui_handoff_fallback_summary'
                routeMode = 'live-regular-activation-cycle'
                completed = $true
                integrityOk = $true
            }
            $legacy = [pscustomobject]@{
                event = 'filetransfer_tuna_gui_handoff_summary'
                routeMode = 'live-regular-activation-cycle'
                completed = $true
                integrityOk = $true
            }
            $wrongRoute = [pscustomobject]@{
                event = 'filetransfer_tuna_gui_handoff_fallback_summary'
                routeMode = 'preactivated'
                completed = $true
                integrityOk = $true
            }
            $missingIntegrity = [pscustomobject]@{
                event = 'filetransfer_tuna_gui_handoff_fallback_summary'
                routeMode = 'live-regular-activation-cycle'
                completed = $true
            }
            $incomplete = [pscustomobject]@{
                event = 'filetransfer_tuna_gui_handoff_fallback_summary'
                routeMode = 'live-regular-activation-cycle'
                completed = $false
                integrityOk = $true
            }
            $badIntegrity = [pscustomobject]@{
                event = 'filetransfer_tuna_gui_handoff_fallback_summary'
                routeMode = 'live-regular-activation-cycle'
                completed = $true
                integrityOk = $false
            }

            if (-not (Test-TunaGuiCurrentSummary -Summary $valid -RouteMode 'live-regular-activation-cycle') -or
                (Test-TunaGuiCurrentSummary -Summary $legacy -RouteMode 'live-regular-activation-cycle') -or
                (Test-TunaGuiCurrentSummary -Summary $wrongRoute -RouteMode 'live-regular-activation-cycle') -or
                (Test-TunaGuiCurrentSummary -Summary $missingIntegrity -RouteMode 'live-regular-activation-cycle') -or
                (Test-TunaGuiCurrentSummary -Summary $incomplete -RouteMode 'live-regular-activation-cycle') -or
                (Test-TunaGuiCurrentSummary -Summary $badIntegrity -RouteMode 'live-regular-activation-cycle')) {
                throw 'Tuna GUI summary validation did not enforce the current producer contract'
            }

            Write-Output 'tuna-gui-summary-validator-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(result.ExitCode == 0, $"Tuna GUI summary validator test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("tuna-gui-summary-validator-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TunaGuiTerminalPhaseDeadline_RebasesOnceAfterExactFinalFallbackProjection()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var atomicJsonBody = ExtractPowerShellFunctionBody(
            guiText,
            "Write-GuiSmokeAtomicJson",
            "New-TunaGuiFileTransferFailureClassificationResult");
        var terminalPhaseBody = ExtractPowerShellFunctionBody(
            guiText,
            "Start-GuiSmokeTerminalPhaseDeadline",
            "Wait-Until");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Write-GuiSmokeAtomicJson { {{atomicJsonBody}} }
            function Start-GuiSmokeTerminalPhaseDeadline { {{terminalPhaseBody}} }

            $requestPath = Join-Path ([IO.Path]::GetTempPath()) ('nlink-terminal-phase-' + [guid]::NewGuid().ToString('N') + '.json')
            try {
                $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID = 'acceptance-a'
                $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID = 'scenario-a'
                $env:NLINK_FILETRANSFER_GUI_TERMINAL_PHASE_DEADLINE_PATH = $requestPath
                $script:GuiSmokeTerminalPhaseGeneration = 0L
                $script:GuiSmokeScenarioDeadlineUtc = [DateTimeOffset]::UtcNow.AddMilliseconds(100)
                $projection = [pscustomobject]@{ LiveRouteEpoch = 4; EndBookmark = 40 }

                $started = Start-GuiSmokeTerminalPhaseDeadline `
                    -RouteMode 'live-regular-activation-cycle' `
                    -TimeoutMs 1000 `
                    -Projection $projection `
                    -ActionBookmark 20
                $duplicate = Start-GuiSmokeTerminalPhaseDeadline `
                    -RouteMode 'live-regular-activation-cycle' `
                    -TimeoutMs 1000 `
                    -Projection $projection `
                    -ActionBookmark 20
                $request = Get-Content -LiteralPath $requestPath -Raw | ConvertFrom-Json
                if ($null -eq $started -or $null -ne $duplicate -or
                    [long]$script:GuiSmokeTerminalPhaseGeneration -ne 1L -or
                    $request.event -ne 'filetransfer_gui_terminal_phase_deadline_requested' -or
                    $request.phase -ne 'terminal_integrity' -or
                    [long]$request.phaseGeneration -ne 1L -or
                    [long]$request.liveRouteEpoch -ne 4L -or
                    [long]$request.actionBookmark -ne 20L -or
                    [long]$request.proofEndBookmark -ne 40L -or
                    [long]$request.timeoutMilliseconds -ne 1000L -or
                    $request.authorityEffect -ne 'none') {
                    throw "terminal phase request was invalid: started=$($started | ConvertTo-Json -Compress); request=$($request | ConvertTo-Json -Compress)"
                }

                $script:GuiSmokeTerminalPhaseGeneration = 0L
                $invalid = Start-GuiSmokeTerminalPhaseDeadline `
                    -RouteMode 'live-regular-activation-cycle' `
                    -TimeoutMs 1000 `
                    -Projection ([pscustomobject]@{ LiveRouteEpoch = 3; EndBookmark = 40 }) `
                    -ActionBookmark 20
                if ($null -ne $invalid -or [long]$script:GuiSmokeTerminalPhaseGeneration -ne 0L) {
                    throw 'pre-final fallback projection rebased the deadline'
                }
                Write-Output 'terminal-phase-deadline-ok'
            }
            finally {
                Remove-Item Env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID -ErrorAction SilentlyContinue
                Remove-Item Env:NLINK_FILETRANSFER_SCENARIO_RUN_ID -ErrorAction SilentlyContinue
                Remove-Item Env:NLINK_FILETRANSFER_GUI_TERMINAL_PHASE_DEADLINE_PATH -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $requestPath -Force -ErrorAction SilentlyContinue
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(result.ExitCode == 0, $"Terminal phase deadline test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("terminal-phase-deadline-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeLogLedger_BindsReadsToHandlesAndReconcilesIntraSyncRotations()
    {
        var helperPath = Path.Combine(FindRepoRoot(), "tools", "GuiSmokeLogLedger.ps1")
            .Replace("'", "''", StringComparison.Ordinal);
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            . '{{helperPath}}'

            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-ledger-atomic-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $utf8 = [Text.UTF8Encoding]::new($false)
                $nl = [Environment]::NewLine

                $active = Join-Path $tempRoot 'nlink.log'
                $rotated1 = Join-Path $tempRoot 'nlink.1.log'
                [IO.File]::WriteAllText($active, "event=old-a${nl}event=old-b${nl}", $utf8)
                $script:GuiSmokeLogLedgerAfterSnapshotOpenedForTests = {
                    param($snapshot)
                    if ([IO.Path]::GetFileName([string]$snapshot.Path) -ieq 'nlink.log') {
                        $script:GuiSmokeLogLedgerAfterSnapshotOpenedForTests = $null
                        Move-Item -LiteralPath $active -Destination $rotated1
                        [IO.File]::WriteAllText($active, "event=new-active${nl}", $utf8)
                    }
                }

                $opened = Open-GuiSmokeLogLedgerFileSnapshot -Path $active
                if ($opened.Status -ne 'Opened') { throw "atomic snapshot was not opened: $($opened.Status)" }
                $delta = Read-GuiSmokeLogLedgerFileDelta -Snapshot $opened.Snapshot -ByteOffset 0
                $atomicLines = @($delta.Lines)
                if (($atomicLines -join '|') -ne 'event=old-a|event=old-b') { throw "renamed handle read mismatch: $($atomicLines -join '|')" }
                $opened.Snapshot.Stream.Dispose()
                Move-Item -LiteralPath $rotated1 -Destination (Join-Path $tempRoot 'disposed-handle.log')

                Remove-Item -LiteralPath $active -Force
                $appendPath = Join-Path $tempRoot 'append.log'
                [IO.File]::WriteAllText($appendPath, "event=before-capture${nl}", $utf8)
                $script:GuiSmokeLogLedgerAfterSnapshotOpenedForTests = {
                    param($snapshot)
                    $script:GuiSmokeLogLedgerAfterSnapshotOpenedForTests = $null
                    [IO.File]::AppendAllText($appendPath, "event=after-capture${nl}", $utf8)
                }
                $appendOpened = Open-GuiSmokeLogLedgerFileSnapshot -Path $appendPath
                $appendDelta = Read-GuiSmokeLogLedgerFileDelta -Snapshot $appendOpened.Snapshot -ByteOffset 0
                if ((@($appendDelta.Lines) -join '|') -ne 'event=before-capture') { throw 'captured read included a concurrent append' }
                $appendOpened.Snapshot.Stream.Dispose()
                $appendState = New-GuiSmokeLogLedgerState -MaxLines 20 -MaxGenerations 10
                Sync-GuiSmokeLogLedger -State $appendState -Paths @($appendPath)
                if ((@(Get-GuiSmokeLogLedgerLines -State $appendState) -join '|') -ne 'event=before-capture|event=after-capture') { throw 'next sync missed concurrent append' }

                $active = Join-Path $tempRoot 'nlink.log'
                $rotated1 = Join-Path $tempRoot 'nlink.1.log'
                $rotated2 = Join-Path $tempRoot 'nlink.2.log'
                $state = New-GuiSmokeLogLedgerState -MaxLines 50 -MaxGenerations 20
                [IO.File]::WriteAllText($active, "event=g1${nl}", $utf8)
                Sync-GuiSmokeLogLedger -State $state -Paths @($active)
                Move-Item -LiteralPath $active -Destination $rotated1
                [IO.File]::WriteAllText($active, "event=g2${nl}", $utf8)
                $script:GuiSmokeLogLedgerAfterSnapshotOpenedForTests = {
                    param($snapshot)
                    if ([IO.Path]::GetFileName([string]$snapshot.Path) -ieq 'nlink.1.log') {
                        $script:GuiSmokeLogLedgerAfterSnapshotOpenedForTests = $null
                        Move-Item -LiteralPath $rotated1 -Destination $rotated2
                        Move-Item -LiteralPath $active -Destination $rotated1
                        [IO.File]::WriteAllText($active, "event=g3${nl}", $utf8)
                    }
                }
                Sync-GuiSmokeLogLedger -State $state -Paths @($rotated1, $active)
                $lines = @(Get-GuiSmokeLogLedgerLines -State $state)
                if (($lines -join '|') -ne 'event=g1|event=g2|event=g3') { throw "reconciled ledger mismatch: $($lines -join '|')" }
                $diagnostics = Get-GuiSmokeLogLedgerDiagnostics -State $state
                if ($diagnostics.TransientRescanCount -lt 1 -or $diagnostics.GapCount -ne 0) { throw 'rotation reconciliation diagnostics mismatch' }

                Move-Item -LiteralPath $active -Destination (Join-Path $tempRoot 'post-sync-handle-disposed.log')
                Write-Output 'gui-log-ledger-atomic-ok'
            }
            finally {
                $script:GuiSmokeLogLedgerAfterSnapshotOpenedForTests = $null
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(result.ExitCode == 0, $"Atomic ledger test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-log-ledger-atomic-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeLogLedger_ReportsTruncationOverflowAndGenerationGap()
    {
        var helperPath = Path.Combine(FindRepoRoot(), "tools", "GuiSmokeLogLedger.ps1")
            .Replace("'", "''", StringComparison.Ordinal);
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            . '{{helperPath}}'

            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-ledger-failures-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $utf8 = [Text.UTF8Encoding]::new($false)
                $nl = [Environment]::NewLine

                $truncatePath = Join-Path $tempRoot 'truncate.log'
                $truncateState = New-GuiSmokeLogLedgerState -MaxLines 20 -MaxGenerations 10
                [IO.File]::WriteAllText($truncatePath, "same-first${nl}second${nl}third${nl}", $utf8)
                Sync-GuiSmokeLogLedger -State $truncateState -Paths @($truncatePath)
                [IO.File]::WriteAllText($truncatePath, "same-first${nl}", $utf8)
                $truncateError = try { Sync-GuiSmokeLogLedger -State $truncateState -Paths @($truncatePath); '' } catch { $_.Exception.Message }
                if ($truncateError -notlike 'gui_smoke_log_cursor_truncated*') { throw "missing truncation failure: $truncateError" }

                $overflowPath = Join-Path $tempRoot 'overflow.log'
                $overflowState = New-GuiSmokeLogLedgerState -MaxLines 2 -MaxGenerations 10
                [IO.File]::WriteAllText($overflowPath, "one${nl}two${nl}three${nl}", $utf8)
                $overflowError = try { Sync-GuiSmokeLogLedger -State $overflowState -Paths @($overflowPath); '' } catch { $_.Exception.Message }
                if ($overflowError -notlike 'gui_smoke_log_cursor_overflow*') { throw "missing overflow failure: $overflowError" }

                $gapPath = Join-Path $tempRoot 'nlink.log'
                $discardedPath = Join-Path $tempRoot 'discarded.log'
                $gapState = New-GuiSmokeLogLedgerState -MaxLines 20 -MaxGenerations 10
                [IO.File]::WriteAllText($gapPath, "old-generation${nl}", $utf8)
                Sync-GuiSmokeLogLedger -State $gapState -Paths @($gapPath)
                Move-Item -LiteralPath $gapPath -Destination $discardedPath
                [IO.File]::WriteAllText($gapPath, "new-generation${nl}", $utf8)
                $gapError = try { Sync-GuiSmokeLogLedger -State $gapState -Paths @($gapPath); '' } catch { $_.Exception.Message }
                if ($gapError -notlike 'gui_smoke_log_cursor_gap*') { throw "missing gap failure: $gapError" }
                Move-Item -LiteralPath $gapPath -Destination (Join-Path $tempRoot 'gap-handle-disposed.log')

                $generationState = New-GuiSmokeLogLedgerState -MaxLines 20 -MaxGenerations 1
                $generationOne = Join-Path $tempRoot 'generation-one.log'
                $generationTwo = Join-Path $tempRoot 'generation-two.log'
                [IO.File]::WriteAllText($generationOne, "generation-one${nl}", $utf8)
                [IO.File]::WriteAllText($generationTwo, "generation-two${nl}", $utf8)
                Sync-GuiSmokeLogLedger -State $generationState -Paths @($generationOne)
                $generationError = try { Sync-GuiSmokeLogLedger -State $generationState -Paths @($generationOne, $generationTwo); '' } catch { $_.Exception.Message }
                if ($generationError -notlike 'gui_smoke_log_cursor_generation_limit*') { throw "missing generation limit failure: $generationError" }

                $bookmarkState = New-GuiSmokeLogLedgerState -MaxLines 20 -MaxGenerations 10
                Sync-GuiSmokeLogLedger -State $bookmarkState -Paths @($generationOne)
                $bookmarkError = try { Get-GuiSmokeLogLedgerLinesAfterBookmark -State $bookmarkState -Bookmark 99; '' } catch { $_.Exception.Message }
                if ($bookmarkError -notlike 'gui_smoke_log_cursor_invalid_bookmark*') { throw "missing invalid bookmark failure: $bookmarkError" }

                $aliasState = New-GuiSmokeLogLedgerState -MaxLines 20 -MaxGenerations 10
                $aliasOne = Join-Path $tempRoot 'alias-one.log'
                $aliasTwo = Join-Path $tempRoot 'alias-two.log'
                [IO.File]::WriteAllText($aliasOne, "alias-line${nl}", $utf8)
                [IO.File]::Copy($aliasOne, $aliasTwo)
                [IO.File]::SetCreationTimeUtc($aliasTwo, [IO.File]::GetCreationTimeUtc($aliasOne))
                Sync-GuiSmokeLogLedger -State $aliasState -Paths @($aliasOne, $aliasTwo)
                if ((@(Get-GuiSmokeLogLedgerLines -State $aliasState) -join '|') -ne 'alias-line') { throw 'generation aliases replayed the same line' }

                Write-Output 'gui-log-ledger-failures-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(result.ExitCode == 0, $"Ledger failure test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-log-ledger-failures-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeLogLedger_AppendOnlyAcceptanceSourceStreamsAndFailsClosed()
    {
        var helperPath = Path.Combine(FindRepoRoot(), "tools", "GuiSmokeLogLedger.ps1")
            .Replace("'", "''", StringComparison.Ordinal);
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            . '{{helperPath}}'

            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-ledger-append-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $path = Join-Path $tempRoot 'nlink.log'
                $state = New-GuiSmokeLogLedgerState
                Enable-GuiSmokeAppendOnlyLogLedgerState -State $state -Path $path -MaximumBytes 4096
                $utf8 = [Text.UTF8Encoding]::new($false)
                [IO.File]::AppendAllText($path, "one`r`ntwo`r`npartial", $utf8)
                Sync-GuiSmokeLogLedger -State $state -Paths @($path)
                if ((Get-GuiSmokeLogLedgerBookmark -State $state) -ne 2) { throw 'partial line was committed' }
                if ($state.Lines.Count -ne 0) { throw 'append-only source retained raw lines in memory' }
                if ((@(Get-GuiSmokeLogLedgerLines -State $state) -join '|') -ne 'one|two') { throw 'streamed lines mismatch' }

                [IO.File]::AppendAllText($path, "-done`r`nthree`r`n", $utf8)
                Sync-GuiSmokeLogLedger -State $state -Paths @($path)
                $visited = New-Object System.Collections.Generic.List[string]
                [void](Invoke-GuiSmokeLogLedgerRangeVisitor -State $state -StartIndex 1 -EndIndex 4 -Visitor {
                    param([string]$line, [int]$index)
                    $visited.Add("$index=$line") | Out-Null
                    return $null
                })
                if (($visited -join '|') -ne '1=two|2=partial-done|3=three') { throw "range visit mismatch: $($visited -join '|')" }

                $capture = Write-GuiSmokeLogLedgerRangeCapture -State $state -StartIndex 1 -EndIndex 4 -DestinationPath (Join-Path $tempRoot 'capture.log')
                if ($capture.LineCount -ne 3 -or (Get-Content -LiteralPath $capture.Path -Raw) -ne "two`r`npartial-done`r`nthree`r`n") { throw 'byte-bound capture mismatch' }
                $diagnostics = Get-GuiSmokeLogLedgerDiagnostics -State $state
                if ($diagnostics.StorageMode -ne 'AppendOnlyFile' -or $diagnostics.ConsumedIndexCount -ne 4) { throw 'append-only diagnostics mismatch' }
                if ($diagnostics.AppendOnlyBufferReadCount -le 0 -or
                    $diagnostics.AppendOnlyNewlineSearchCount -le 0 -or
                    $diagnostics.AppendOnlyIndexedByteCount -le $diagnostics.AppendOnlyNewlineSearchCount) {
                    throw 'append-only block-search diagnostics mismatch'
                }
                $indexedBeforeSettledSync = [long]$diagnostics.AppendOnlyIndexedByteCount
                $readsBeforeSettledSync = [long]$diagnostics.AppendOnlyBufferReadCount
                Sync-GuiSmokeLogLedger -State $state -Paths @($path)
                $settledDiagnostics = Get-GuiSmokeLogLedgerDiagnostics -State $state
                if ([long]$settledDiagnostics.AppendOnlyIndexedByteCount -ne $indexedBeforeSettledSync -or
                    [long]$settledDiagnostics.AppendOnlyBufferReadCount -ne $readsBeforeSettledSync) {
                    throw 'settled append-only sync rescanned source bytes'
                }

                [IO.File]::WriteAllText($path, "replacement`r`n", $utf8)
                $failure = try { Sync-GuiSmokeLogLedger -State $state -Paths @($path); '' } catch { $_.Exception.Message }
                if ($failure -notlike 'gui_smoke_log_cursor_truncated*' -and $failure -notlike 'gui_smoke_log_cursor_gap*') { throw "replacement was not rejected: $failure" }
                Write-Output 'gui-log-ledger-append-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(result.ExitCode == 0, $"Append-only ledger test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-log-ledger-append-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeLogLedger_ExportsStableIdentityAndDetectsGenerationOrdinalReplay()
    {
        var helperPath = Path.Combine(FindRepoRoot(), "tools", "GuiSmokeLogLedger.ps1")
            .Replace("'", "''", StringComparison.Ordinal);
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            . '{{helperPath}}'

            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-ledger-identity-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $utf8 = [Text.UTF8Encoding]::new($false)
                $nl = [Environment]::NewLine
                $active = Join-Path $tempRoot 'nlink.log'
                $state = New-GuiSmokeLogLedgerState -MaxLines 20 -MaxGenerations 10
                $firstLedgerId = [string]$state.LedgerId
                if ([string]::IsNullOrWhiteSpace($firstLedgerId)) { throw 'ledger id missing' }
                Reset-GuiSmokeLogLedgerState -State $state
                if ([string]::IsNullOrWhiteSpace([string]$state.LedgerId) -or $state.LedgerId -eq $firstLedgerId) { throw 'ledger id was not regenerated on reset' }

                [IO.File]::WriteAllText($active, "event=identical${nl}event=identical${nl}", $utf8)
                Sync-GuiSmokeLogLedger -State $state -Paths @($active)
                $snapshot = Get-GuiSmokeLogLedgerSnapshot -State $state -Bookmark 0
                if ($snapshot.ledgerId -ne $state.LedgerId) { throw 'snapshot ledger id mismatch' }
                if ([string]::IsNullOrWhiteSpace([string]$snapshot.initialGenerationKey)) { throw 'initial generation missing' }
                if ($snapshot.initialGenerationKey -ne $snapshot.activeGenerationKey) { throw 'initial/current generation mismatch' }
                if (@($snapshot.generations).Count -ne 1 -or @($snapshot.initialGenerations).Count -ne 1) { throw 'generation snapshots missing' }
                if (-not (Test-GuiSmokeLogLedgerSnapshotMonotonicSubset -InitialGenerations $snapshot.initialGenerations -FinalGenerations $snapshot.generations)) { throw 'valid generation snapshot was not monotonic' }
                $lines = @(Get-GuiSmokeLogLedgerLines -State $state)
                if ($lines.Count -ne 2 -or $lines[0] -ne 'event=identical' -or $lines[1] -ne 'event=identical') { throw 'legitimate identical-content lines were collapsed' }
                $beforeReplayDiagnostics = Get-GuiSmokeLogLedgerDiagnostics -State $state
                if ($beforeReplayDiagnostics.DuplicateIndexCount -ne 0) { throw 'identical content was treated as duplicate index' }
                if ($beforeReplayDiagnostics.ConsumedIndexCount -ne 2) { throw 'generation/ordinal index count mismatch' }

                $generation = @($state.Generations.Values)[0]
                $generation.ConsumedByteOffset = 0L
                $generation.ConsumedLineCount = 0L
                $replayError = try { Sync-GuiSmokeLogLedger -State $state -Paths @($active); '' } catch { $_.Exception.Message }
                if ($replayError -notlike 'gui_smoke_log_cursor_duplicate_index*') { throw "generation/ordinal replay was not rejected: $replayError" }
                $diagnostics = Get-GuiSmokeLogLedgerDiagnostics -State $state
                if ($diagnostics.DuplicateIndexCount -ne 1) { throw "duplicate index count mismatch: $($diagnostics.DuplicateIndexCount)" }

                Write-Output 'gui-log-ledger-identity-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(result.ExitCode == 0, $"Ledger identity test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-log-ledger-identity-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeLogLedger_TracksOnlyCurrentRunWritersAcrossInterleavingAndDetectsWriterCorruption()
    {
        var helperPath = Path.Combine(FindRepoRoot(), "tools", "GuiSmokeLogLedger.ps1")
            .Replace("'", "''", StringComparison.Ordinal);
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            . '{{helperPath}}'

            $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID = 'acceptance-current'
            $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID = 'acceptance-current:scenario:1'
            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-ledger-writers-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $utf8 = [Text.UTF8Encoding]::new($false)
                $nl = [Environment]::NewLine
                $active = Join-Path $tempRoot 'nlink.log'
                $historical = '[old] event=operational_log_writer_started; acceptance_run_id=acceptance-old; scenario_run_id=acceptance-old:scenario:1; log_writer_id=old; log_writer_sequence=7'
                $a1 = '[a] event=operational_log_writer_started; acceptance_run_id=acceptance-current; scenario_run_id=acceptance-current:scenario:1; log_writer_id=writer-a; log_writer_sequence=1'
                $a2 = '[a] event=a2; log_writer_id=writer-a; log_writer_sequence=2'
                [IO.File]::WriteAllText($active, (@($historical, $a1, $a2) -join $nl) + $nl, $utf8)

                $state = New-GuiSmokeLogLedgerState -MaxLines 30 -MaxGenerations 5
                Sync-GuiSmokeLogLedger -State $state -Paths @($active)
                $initial = Get-GuiSmokeLogLedgerSnapshot -State $state
                Set-GuiSmokeLogLedgerWriterEvidenceBookmark -State $state -Bookmark $initial.lineCount
                if (@($initial.writers).Count -ne 1 -or $initial.writers[0].writerId -ne 'writer-a' -or $initial.writers[0].lastSequence -ne 2) {
                    throw 'current-run initial writer snapshot mismatch'
                }

                $b1 = '[b] event=operational_log_writer_started; acceptance_run_id=acceptance-current; scenario_run_id=acceptance-current:scenario:1; log_writer_id=writer-b; log_writer_sequence=1'
                $a3 = '[a] event=a3; log_writer_id=writer-a; log_writer_sequence=3'
                $b2 = '[b] event=b2; log_writer_id=writer-b; log_writer_sequence=2'
                [IO.File]::AppendAllText($active, (@($b1, $a3, $b2) -join $nl) + $nl, $utf8)
                Sync-GuiSmokeLogLedger -State $state -Paths @($active)
                $final = Get-GuiSmokeLogLedgerSnapshot -State $state
                $diagnostics = Get-GuiSmokeLogLedgerDiagnostics -State $state
                if (@($final.writers).Count -ne 2 -or
                    $diagnostics.WriterGapCount -ne 0 -or
                    $diagnostics.WriterDuplicateCount -ne 0 -or
                    $diagnostics.WriterIdentityMismatchCount -ne 0) {
                    throw 'valid interleaved writers failed integrity'
                }
                if (-not (Test-GuiSmokeLogLedgerWriterSnapshotMonotonicSubset -InitialWriters $initial.writers -FinalWriters $final.writers)) {
                    throw 'writer snapshots were not monotonic'
                }

                $gapPath = Join-Path $tempRoot 'gap.log'
                [IO.File]::WriteAllText($gapPath, (@($a1, '[a] event=a3; log_writer_id=writer-a; log_writer_sequence=3') -join $nl) + $nl, $utf8)
                $gapState = New-GuiSmokeLogLedgerState
                Sync-GuiSmokeLogLedger -State $gapState -Paths @($gapPath)
                if ((Get-GuiSmokeLogLedgerDiagnostics -State $gapState).WriterGapCount -ne 1) { throw 'writer gap was not detected' }

                $duplicatePath = Join-Path $tempRoot 'duplicate.log'
                [IO.File]::WriteAllText($duplicatePath, (@($a1, $a2, $a2) -join $nl) + $nl, $utf8)
                $duplicateState = New-GuiSmokeLogLedgerState
                Sync-GuiSmokeLogLedger -State $duplicateState -Paths @($duplicatePath)
                if ((Get-GuiSmokeLogLedgerDiagnostics -State $duplicateState).WriterDuplicateCount -ne 1) { throw 'writer duplicate was not detected' }

                $invalidStartPath = Join-Path $tempRoot 'invalid-start.log'
                $invalidStart = '[a] event=operational_log_writer_started; acceptance_run_id=acceptance-current; scenario_run_id=acceptance-current:scenario:1; log_writer_id=writer-a; log_writer_sequence=2'
                [IO.File]::WriteAllText($invalidStartPath, $invalidStart + $nl, $utf8)
                $invalidStartState = New-GuiSmokeLogLedgerState
                Sync-GuiSmokeLogLedger -State $invalidStartState -Paths @($invalidStartPath)
                $invalidDiagnostics = Get-GuiSmokeLogLedgerDiagnostics -State $invalidStartState
                if ($invalidDiagnostics.WriterGapCount -ne 1 -or $invalidDiagnostics.WriterIdentityMismatchCount -ne 1) { throw 'invalid writer start was not rejected' }

                $orphanPath = Join-Path $tempRoot 'orphan.log'
                [IO.File]::WriteAllText($orphanPath, $historical + $nl, $utf8)
                $orphanState = New-GuiSmokeLogLedgerState
                Sync-GuiSmokeLogLedger -State $orphanState -Paths @($orphanPath)
                if ((Get-GuiSmokeLogLedgerDiagnostics -State $orphanState).WriterIdentityMismatchCount -ne 0) { throw 'historical pre-bookmark orphan was rejected' }
                Set-GuiSmokeLogLedgerWriterEvidenceBookmark -State $orphanState -Bookmark (Get-GuiSmokeLogLedgerBookmark -State $orphanState)
                [IO.File]::AppendAllText($orphanPath, '[lost-start] event=after; log_writer_id=writer-lost; log_writer_sequence=2' + $nl, $utf8)
                Sync-GuiSmokeLogLedger -State $orphanState -Paths @($orphanPath)
                if ((Get-GuiSmokeLogLedgerDiagnostics -State $orphanState).WriterIdentityMismatchCount -ne 1) { throw 'post-bookmark writer without a start event was accepted' }

                $wrongIdentityPath = Join-Path $tempRoot 'wrong-identity.log'
                [IO.File]::WriteAllText($wrongIdentityPath, '', $utf8)
                $wrongIdentityState = New-GuiSmokeLogLedgerState
                Sync-GuiSmokeLogLedger -State $wrongIdentityState -Paths @($wrongIdentityPath)
                Set-GuiSmokeLogLedgerWriterEvidenceBookmark -State $wrongIdentityState -Bookmark (Get-GuiSmokeLogLedgerBookmark -State $wrongIdentityState)
                [IO.File]::AppendAllText($wrongIdentityPath, '[wrong] event=operational_log_writer_started; acceptance_run_id=acceptance-other; scenario_run_id=acceptance-other:scenario:1; log_writer_id=writer-wrong; log_writer_sequence=1' + $nl, $utf8)
                Sync-GuiSmokeLogLedger -State $wrongIdentityState -Paths @($wrongIdentityPath)
                if ((Get-GuiSmokeLogLedgerDiagnostics -State $wrongIdentityState).WriterIdentityMismatchCount -ne 1) { throw 'post-bookmark writer with the wrong run identity was accepted' }

                $untaggedPath = Join-Path $tempRoot 'mixed-untagged.log'
                [IO.File]::WriteAllText($untaggedPath, $a1 + $nl, $utf8)
                $untaggedState = New-GuiSmokeLogLedgerState
                Sync-GuiSmokeLogLedger -State $untaggedState -Paths @($untaggedPath)
                Set-GuiSmokeLogLedgerWriterEvidenceBookmark -State $untaggedState -Bookmark (Get-GuiSmokeLogLedgerBookmark -State $untaggedState)
                [IO.File]::AppendAllText($untaggedPath, '[legacy] event=untagged_after_bookmark' + $nl, $utf8)
                Sync-GuiSmokeLogLedger -State $untaggedState -Paths @($untaggedPath)
                if ((Get-GuiSmokeLogLedgerDiagnostics -State $untaggedState).WriterIdentityMismatchCount -ne 1) { throw 'post-bookmark untagged line was accepted alongside a valid writer' }

                Write-Output 'gui-log-ledger-writers-ok'
            }
            finally {
                Remove-Item Env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID -ErrorAction SilentlyContinue
                Remove-Item Env:NLINK_FILETRANSFER_SCENARIO_RUN_ID -ErrorAction SilentlyContinue
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(result.ExitCode == 0, $"Writer ledger test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-log-ledger-writers-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void GuiSmokeTunaScenario_WritesDurableLogLedgerEvidenceOnce()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var wrapperText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "Run-FileTransferTunaGuiSmoke.ps1"));
        var routeAcceptanceText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "Run-FileTransferRouteAcceptance.ps1"));
        var scenarioBody = ExtractPowerShellFunctionBody(
            guiText,
            "Run-ScenarioFileTransferTunaHandoffFallback",
            "Suspend-ProcessForRecoveryWindow");

        Assert.Contains("Get-GuiSmokeLogLedgerSnapshot", guiText, StringComparison.Ordinal);
        Assert.Contains("Write-FileTransferGuiLogLedgerEvidence", guiText, StringComparison.Ordinal);
        Assert.Contains("Write-GuiSmokeLogLedgerRangeCapture", guiText, StringComparison.Ordinal);
        Assert.Contains("Copy-GuiSmokeFileAtomicCreateNew", guiText, StringComparison.Ordinal);
        Assert.Contains("capture_persisted", guiText, StringComparison.Ordinal);
        Assert.Contains("writer_validation_completed", guiText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-gui-log-ledger.json", guiText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-gui-log-ledger-slice.log", guiText, StringComparison.Ordinal);
        Assert.Contains("analyzerInput = $analyzerInputEvidence", guiText, StringComparison.Ordinal);
        Assert.Contains("[System.IO.FileMode]::CreateNew", guiText, StringComparison.Ordinal);
        Assert.Contains("NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID", guiText, StringComparison.Ordinal);
        Assert.Contains("NLINK_FILETRANSFER_SCENARIO_RUN_ID", guiText, StringComparison.Ordinal);
        Assert.Contains("duplicateIndexCount", guiText, StringComparison.Ordinal);
        Assert.Contains("writerEvidenceVersion", guiText, StringComparison.Ordinal);
        Assert.Contains("writerGapCount", guiText, StringComparison.Ordinal);
        Assert.Contains("writerFailureMarkerFiles", guiText, StringComparison.Ordinal);
        Assert.Contains("operational_log_write_failed", guiText, StringComparison.Ordinal);
        Assert.Contains("Wait-GuiSmokeProcessOutputCaptureDrain", guiText, StringComparison.Ordinal);
        Assert.Contains("-ProcessOutputDrainAction", scenarioBody, StringComparison.Ordinal);
        var captureIndex = scenarioBody.IndexOf(
            "Write-FileTransferGuiLogLedgerEvidence",
            StringComparison.Ordinal);
        var enrichmentIndex = scenarioBody.IndexOf(
            "Invoke-TunaGuiFailureClassificationEnrichment",
            StringComparison.Ordinal);
        Assert.True(
            captureIndex >= 0 && enrichmentIndex > captureIndex,
            "Failure enrichment must run only after immutable capture and writer validation.");
        Assert.Contains("classificationEnrichmentStatus", scenarioBody, StringComparison.Ordinal);
        Assert.Contains("classificationEnrichmentReason", scenarioBody, StringComparison.Ordinal);
        Assert.Contains("classificationEnrichmentElapsedMilliseconds", scenarioBody, StringComparison.Ordinal);
        Assert.Contains("-BaseClassification $failureClassification", scenarioBody, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Get-TunaGuiFileTransferSetupFailureClassification",
            scenarioBody,
            StringComparison.Ordinal);
        Assert.Contains("consumedIndexSha256", guiText, StringComparison.Ordinal);
        Assert.Contains("cursorIntegrityVerdict", guiText, StringComparison.Ordinal);
        Assert.Contains("[string]$FileName = 'filetransfer-retained-log-slice.log'", wrapperText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-gui-log-ledger-slice.log", wrapperText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-tuna-gui-evidence-manifest.json", wrapperText, StringComparison.Ordinal);
        Assert.Contains("NLINK_FILETRANSFER_ACCEPTANCE_LOG_PATH", guiText, StringComparison.Ordinal);
        Assert.Contains("Invoke-TunaGuiSmokeWithHardDeadline", wrapperText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-gui-evidence-finalization-timeout.json", wrapperText, StringComparison.Ordinal);
        Assert.Contains("RouteAcceptanceEvidenceCompletionAttempted", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("Test-RouteAcceptanceEvidenceFinalizationTimedOut", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("-not $evidenceFinalizationTimedOut", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("fail_closed_manifest_write_failed", routeAcceptanceText, StringComparison.Ordinal);

        var helpeeStartIndex = scenarioBody.IndexOf("Start-HelpeeFlow -Context $Context", StringComparison.Ordinal);
        var bookmarkIndex = scenarioBody.IndexOf("$runBookmark = Get-AppLogBookmark", StringComparison.Ordinal);
        var initialSnapshotIndex = scenarioBody.IndexOf("$initialLogLedgerSnapshot = Get-GuiSmokeLogLedgerSnapshot", StringComparison.Ordinal);
        var writerBookmarkIndex = scenarioBody.IndexOf("Set-GuiSmokeLogLedgerWriterEvidenceBookmark", StringComparison.Ordinal);
        var helperStartIndex = scenarioBody.IndexOf("Start-HelperFlow -Context $Context", StringComparison.Ordinal);
        Assert.True(
            helpeeStartIndex >= 0 &&
            bookmarkIndex > helpeeStartIndex &&
            initialSnapshotIndex > bookmarkIndex &&
            writerBookmarkIndex > initialSnapshotIndex &&
            helperStartIndex > writerBookmarkIndex,
            "The Tuna ledger bookmark and initial generation snapshot must be captured after the first app establishes nlink.log and before the helper contributes scenario events.");
        Assert.Contains("gui_smoke_log_cursor_initial_generation_missing", scenarioBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeLogLedger_ValidatesWriterChronologyWithinEachProcess()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var chronologyBody = ExtractPowerShellFunctionBody(
            guiText,
            "Get-GuiSmokeLogWriterChronology",
            "Write-FileTransferGuiLogLedgerEvidence");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Get-GuiSmokeLogWriterChronology { {{chronologyBody}} }
            function New-WriterLineRecord {
                param(
                    [string]$WriterId,
                    [long]$Sequence,
                    [long]$BatchGeneration,
                    [long]$BatchFirstSequence,
                    [long]$BatchLastSequence,
                    [long]$AttemptUtcTicks)
                return [pscustomobject]@{
                    writerId = $WriterId
                    sequence = $Sequence
                    batchGeneration = $BatchGeneration
                    batchFirstSequence = $BatchFirstSequence
                    batchLastSequence = $BatchLastSequence
                    attemptUtcTicks = $AttemptUtcTicks
                }
            }
            function Assert-Chronology {
                param(
                    [object[]]$Records,
                    [long]$ExpectedInversions,
                    [long]$ExpectedCollisions,
                    [string]$Name)
                $result = Get-GuiSmokeLogWriterChronology -WriterLineRecords $Records
                if ([long]$result.inversionCount -ne $ExpectedInversions -or
                    [long]$result.collisionCount -ne $ExpectedCollisions -or
                    @($result.reasons).Count -ne 0) {
                    throw "$Name failed: inversions=$($result.inversionCount); collisions=$($result.collisionCount); reasons=$(@($result.reasons) -join ',')"
                }
            }
            function Assert-InvalidChronology {
                param([object[]]$Records, [string]$Name)
                $result = Get-GuiSmokeLogWriterChronology -WriterLineRecords $Records
                if (@($result.reasons) -notcontains 'writer_recovery_chronology_batch_invalid') {
                    throw "$Name did not reject malformed batch evidence"
                }
            }

            # Writer B may persist a newer batch before writer A recovers its
            # older batch. Physical cross-writer ordering is not chronology.
            Assert-Chronology -Name 'cross-writer-later-tick' -ExpectedInversions 0 -ExpectedCollisions 0 -Records @(
                (New-WriterLineRecord writer-a 1 1 1 1 100),
                (New-WriterLineRecord writer-b 1 1 1 1 300),
                (New-WriterLineRecord writer-a 2 2 2 2 200))
            Assert-Chronology -Name 'cross-writer-equal-tick' -ExpectedInversions 0 -ExpectedCollisions 0 -Records @(
                (New-WriterLineRecord writer-a 1 1 1 1 100),
                (New-WriterLineRecord writer-b 1 1 1 1 100))

            $artifactLikeInterleaving = [Collections.Generic.List[object]]::new()
            for ($batch = 1; $batch -le 24; $batch++) {
                # Writer B's newer attempt is physically before writer A's
                # recovered older attempt for every batch.
                $artifactLikeInterleaving.Add((New-WriterLineRecord writer-b $batch $batch $batch $batch (1000 + (2 * $batch))))
                $artifactLikeInterleaving.Add((New-WriterLineRecord writer-a $batch $batch $batch $batch (999 + (2 * $batch))))
            }
            Assert-Chronology -Name 'artifact-like-24-batch-interleaving' -ExpectedInversions 0 -ExpectedCollisions 0 -Records @($artifactLikeInterleaving.ToArray())

            Assert-Chronology -Name 'same-writer-lower-tick' -ExpectedInversions 1 -ExpectedCollisions 0 -Records @(
                (New-WriterLineRecord writer-a 1 1 1 1 200),
                (New-WriterLineRecord writer-a 2 2 2 2 100))
            Assert-Chronology -Name 'same-writer-equal-distinct-batch' -ExpectedInversions 0 -ExpectedCollisions 1 -Records @(
                (New-WriterLineRecord writer-a 1 1 1 1 100),
                (New-WriterLineRecord writer-a 2 2 2 2 100))

            # Lines in one batch intentionally share one attempt timestamp.
            Assert-Chronology -Name 'multiline-same-batch-equal-tick' -ExpectedInversions 0 -ExpectedCollisions 0 -Records @(
                (New-WriterLineRecord writer-a 1 1 1 2 100),
                (New-WriterLineRecord writer-a 2 1 1 2 100),
                (New-WriterLineRecord writer-a 3 2 3 3 200))
            Assert-InvalidChronology -Name 'noncontiguous-batch-range' -Records @(
                (New-WriterLineRecord writer-a 1 1 1 3 100),
                (New-WriterLineRecord writer-a 3 1 1 3 100))
            Assert-InvalidChronology -Name 'nonpositive-batch-timestamp' -Records @(
                (New-WriterLineRecord writer-a 1 1 1 1 0))

            Write-Output 'gui-log-writer-chronology-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(result.ExitCode == 0, $"Writer chronology test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-log-writer-chronology-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeLogLedger_ProducerAndRevalidatorShareVersion3ChronologyRules()
    {
        var repoRoot = FindRepoRoot();
        var helperPath = Path.Combine(repoRoot, "tools", "GuiSmokeLogLedger.ps1")
            .Replace("'", "''", StringComparison.Ordinal);
        var evidencePath = Path.Combine(repoRoot, "tools", "FileTransferRouteAcceptanceEvidence.ps1")
            .Replace("'", "''", StringComparison.Ordinal);
        var guiText = File.ReadAllText(Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1"));
        var writerBody = ExtractPowerShellFunctionBody(
            guiText,
            "Write-FileTransferGuiLogLedgerEvidence",
            "Write-TunaGuiMilestoneEvidence");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            . '{{helperPath}}'
            . '{{evidencePath}}'
            function Write-FileTransferGuiLogLedgerEvidence { {{writerBody}} }
            function Sync-GuiSmokeAppLogLedger {}
            function Wait-GuiSmokeProcessOutputCaptureDrain { param([object[]]$Processes) }
            function Get-GuiSmokeLogLedgerSnapshotIntegrityReasons { return @() }
            function Get-GuiSmokeLogLedgerSnapshot { return $script:finalSnapshot }
            function Get-GuiSmokeLogLedgerDiagnostics {
                return [pscustomobject]@{
                    RotationCount = 0
                    TransientRescanCount = 0
                    GapCount = 0
                    DuplicateIndexCount = 0
                    WriterGapCount = 0L
                    WriterDuplicateCount = [long]$script:writerDuplicateCount
                    WriterIdentityMismatchCount = 0L
                    ConsumedIndexCount = [int]$script:finalSnapshot.lineCount
                    ConsumedIndexSha256 = 'synthetic-consumed-index'
                    ConsumedIndexRanges = @('synthetic')
                    SyncCount = 1
                    FailureCode = '(none)'
                    FailureDetail = '(none)'
                }
            }
            function New-WriterLine {
                param(
                    [string]$Event,
                    [long]$Sequence,
                    [long]$Batch,
                    [long]$First,
                    [long]$Last,
                    [long]$Ticks,
                    [string]$Suffix = '')
                return "event=$Event; acceptance_run_id=run-v3; scenario_run_id=run-v3:scenario:1; log_writer_id=writer-v3; log_writer_sequence=$Sequence; log_writer_batch_generation=$Batch; log_writer_batch_first_sequence=$First; log_writer_batch_last_sequence=$Last; log_writer_attempt_utc_ticks=$Ticks$Suffix"
            }
            function Invoke-ChronologyCase {
                param(
                    [string]$Name,
                    [string[]]$Lines,
                    [long]$FinalSequence,
                    [long]$ExpectedInversions,
                    [long]$ExpectedCollisions,
                    [bool]$ExpectedBatchValid,
                    [long]$WriterDuplicateCount)

                $artifactDir = Join-Path $tempRoot $Name
                $outputRoot = Join-Path $artifactDir 'process-output'
                [void][IO.Directory]::CreateDirectory($outputRoot)
                $markerPath = Join-Path $outputRoot 'writer.stderr.log'
                [IO.File]::WriteAllText($markerPath, '', [Text.UTF8Encoding]::new($false))

                $script:GuiSmokeAppLogLedger = New-GuiSmokeLogLedgerState
                $script:GuiSmokeAppLogLedger.Lines = [Collections.Generic.List[string]]::new()
                foreach ($line in $Lines) {
                    $script:GuiSmokeAppLogLedger.Lines.Add($line)
                }
                $script:finalSnapshot = [pscustomobject]@{
                    ledgerId = "ledger-$Name"
                    bookmark = 0
                    lineCount = $Lines.Count
                    activeGenerationKey = 'generation-v3'
                    generations = @()
                    writers = @([pscustomobject]@{
                        writerId = 'writer-v3'
                        acceptanceRunId = 'run-v3'
                        scenarioRunId = 'run-v3:scenario:1'
                        lastSequence = $FinalSequence
                    })
                }
                $initialSnapshot = [pscustomobject]@{
                    bookmark = 0
                    lineCount = 0
                    activeGenerationKey = 'generation-v3'
                    generations = @()
                    writers = @()
                }
                $script:writerDuplicateCount = $WriterDuplicateCount
                $script:GuiSmokeProcessOutputFiles = [Collections.Generic.List[string]]::new()
                $script:GuiSmokeProcessOutputFiles.Add($markerPath)

                Write-FileTransferGuiLogLedgerEvidence `
                    -ArtifactDir $artifactDir `
                    -Bookmark 0 `
                    -InitialSnapshot $initialSnapshot

                $ledger = Get-Content -LiteralPath (Join-Path $artifactDir 'filetransfer-gui-log-ledger.json') -Raw | ConvertFrom-Json
                $writerEvidencePath = [string]$ledger.writerEvidenceSlice.path
                if ([string]::IsNullOrWhiteSpace($writerEvidencePath)) {
                    throw "$Name writer evidence missing: $($ledger | ConvertTo-Json -Compress -Depth 4)"
                }
                $revalidated = Get-RouteAcceptanceEvidenceWriterChronology -Path $writerEvidencePath
                $producerBatchValid =
                    @($ledger.writerChronologyReasons) -notcontains 'writer_recovery_chronology_batch_invalid'
                $expectedVerdict = if ($ExpectedBatchValid -and
                    $ExpectedInversions -eq 0 -and
                    $ExpectedCollisions -eq 0) { 'pass' } else { 'fail' }

                if ([long]$ledger.writerChronologyInversionCount -ne $ExpectedInversions -or
                    [long]$ledger.writerChronologyCollisionCount -ne $ExpectedCollisions -or
                    [bool]$producerBatchValid -ne $ExpectedBatchValid -or
                    [string]$ledger.writerChronologyVerdict -ne $expectedVerdict) {
                    throw "$Name producer mismatch: $($ledger | ConvertTo-Json -Compress -Depth 4)"
                }
                if ([long]$revalidated.inversionCount -ne $ExpectedInversions -or
                    [long]$revalidated.collisionCount -ne $ExpectedCollisions -or
                    [bool]$revalidated.batchEvidenceValid -ne $ExpectedBatchValid) {
                    throw "$Name revalidator mismatch: $($revalidated | ConvertTo-Json -Compress)"
                }
                if ([long]$ledger.writerDuplicateCount -ne $WriterDuplicateCount) {
                    throw "$Name writer duplicate detector mismatch: $($ledger.writerDuplicateCount)"
                }
            }

            $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID = 'run-v3'
            $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID = 'run-v3:scenario:1'
            Remove-Item Env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH -ErrorAction SilentlyContinue
            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-ledger-v3-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                Invoke-ChronologyCase -Name 'multiline-equal-ticks' -FinalSequence 3 `
                    -ExpectedInversions 0 -ExpectedCollisions 0 -ExpectedBatchValid $true `
                    -WriterDuplicateCount 0 -Lines @(
                        (New-WriterLine operational_log_writer_started 1 1 1 2 100),
                        (New-WriterLine payload 2 1 1 2 100),
                        (New-WriterLine operational_log_writer_drained 3 2 3 3 200))
                Invoke-ChronologyCase -Name 'decreasing-batches' -FinalSequence 3 `
                    -ExpectedInversions 1 -ExpectedCollisions 0 -ExpectedBatchValid $true `
                    -WriterDuplicateCount 0 -Lines @(
                        (New-WriterLine operational_log_writer_started 1 1 1 1 200),
                        (New-WriterLine payload 2 2 2 2 100),
                        (New-WriterLine operational_log_writer_drained 3 3 3 3 300))
                Invoke-ChronologyCase -Name 'equal-distinct-batches' -FinalSequence 3 `
                    -ExpectedInversions 0 -ExpectedCollisions 1 -ExpectedBatchValid $true `
                    -WriterDuplicateCount 0 -Lines @(
                        (New-WriterLine operational_log_writer_started 1 1 1 1 100),
                        (New-WriterLine payload 2 2 2 2 100),
                        (New-WriterLine operational_log_writer_drained 3 3 3 3 300))
                Invoke-ChronologyCase -Name 'duplicate-within-batch' -FinalSequence 2 `
                    -ExpectedInversions 0 -ExpectedCollisions 0 -ExpectedBatchValid $false `
                    -WriterDuplicateCount 1 -Lines @(
                        (New-WriterLine operational_log_writer_started 1 1 1 2 100),
                        (New-WriterLine payload 1 1 1 2 100),
                        (New-WriterLine operational_log_writer_drained 2 2 2 2 200))
                Invoke-ChronologyCase -Name 'sequence-reused-across-batches' -FinalSequence 2 `
                    -ExpectedInversions 0 -ExpectedCollisions 0 -ExpectedBatchValid $true `
                    -WriterDuplicateCount 1 -Lines @(
                        (New-WriterLine operational_log_writer_started 1 1 1 1 100),
                        (New-WriterLine payload 1 2 1 1 200),
                        (New-WriterLine operational_log_writer_drained 2 3 2 2 300))
                Invoke-ChronologyCase -Name 'duplicate-non-writer-structured-field' -FinalSequence 2 `
                    -ExpectedInversions 0 -ExpectedCollisions 0 -ExpectedBatchValid $true `
                    -WriterDuplicateCount 0 -Lines @(
                        (New-WriterLine operational_log_writer_started 1 1 1 1 100 '; route=regular_nkn_v4_fast; route=file_tuna_v4'),
                        (New-WriterLine operational_log_writer_drained 2 2 2 2 200))
                Invoke-ChronologyCase -Name 'duplicate-writer-evidence-field' -FinalSequence 2 `
                    -ExpectedInversions 0 -ExpectedCollisions 0 -ExpectedBatchValid $false `
                    -WriterDuplicateCount 0 -Lines @(
                        (New-WriterLine operational_log_writer_started 1 1 1 1 100 '; log_writer_attempt_utc_ticks=999'),
                        (New-WriterLine operational_log_writer_drained 2 2 2 2 200))
                Invoke-ChronologyCase -Name 'duplicate-acceptance-run-id' -FinalSequence 2 `
                    -ExpectedInversions 0 -ExpectedCollisions 0 -ExpectedBatchValid $false `
                    -WriterDuplicateCount 0 -Lines @(
                        (New-WriterLine operational_log_writer_started 1 1 1 1 100 '; acceptance_run_id=other-run'),
                        (New-WriterLine operational_log_writer_drained 2 2 2 2 200))
                Invoke-ChronologyCase -Name 'duplicate-scenario-run-id' -FinalSequence 2 `
                    -ExpectedInversions 0 -ExpectedCollisions 0 -ExpectedBatchValid $false `
                    -WriterDuplicateCount 0 -Lines @(
                        (New-WriterLine operational_log_writer_started 1 1 1 1 100 '; scenario_run_id=other-run:scenario:1'),
                        (New-WriterLine operational_log_writer_drained 2 2 2 2 200))
                Invoke-ChronologyCase -Name 'duplicate-event' -FinalSequence 2 `
                    -ExpectedInversions 0 -ExpectedCollisions 0 -ExpectedBatchValid $false `
                    -WriterDuplicateCount 0 -Lines @(
                        (New-WriterLine operational_log_writer_started 1 1 1 1 100),
                        (New-WriterLine operational_log_writer_drained 2 2 2 2 200 '; event=payload'))
                Invoke-ChronologyCase -Name 'nonpositive-sequence' -FinalSequence 1 `
                    -ExpectedInversions 0 -ExpectedCollisions 0 -ExpectedBatchValid $false `
                    -WriterDuplicateCount 0 -Lines @(
                        (New-WriterLine operational_log_writer_started 0 1 0 0 100),
                        (New-WriterLine operational_log_writer_drained 1 2 1 1 200))

                Write-Output 'gui-log-ledger-v3-producer-revalidator-ok'
            }
            finally {
                Remove-Item Env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID -ErrorAction SilentlyContinue
                Remove-Item Env:NLINK_FILETRANSFER_SCENARIO_RUN_ID -ErrorAction SilentlyContinue
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(result.ExitCode == 0, $"Version-3 producer/revalidator parity failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-log-ledger-v3-producer-revalidator-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeLogLedger_WriterFailureMarkersAreBoundAndScopedToCurrentArtifact()
    {
        var repoRoot = FindRepoRoot();
        var helperPath = Path.Combine(repoRoot, "tools", "GuiSmokeLogLedger.ps1")
            .Replace("'", "''", StringComparison.Ordinal);
        var guiText = File.ReadAllText(Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1"));
        var chronologyBody = ExtractPowerShellFunctionBody(
            guiText,
            "Get-GuiSmokeLogWriterChronology",
            "Write-FileTransferGuiLogLedgerEvidence");
        var writerBody = ExtractPowerShellFunctionBody(
            guiText,
            "Write-FileTransferGuiLogLedgerEvidence",
            "Write-TunaGuiMilestoneEvidence");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            . '{{helperPath}}'
            function Get-GuiSmokeLogWriterChronology { {{chronologyBody}} }
            function Write-FileTransferGuiLogLedgerEvidence { {{writerBody}} }
            function Update-FileTransferGuiEvidenceFinalizationStage { param([string]$Stage) }
            function Get-FileSha256Hex {
                param([string]$Path)
                $sha = [Security.Cryptography.SHA256]::Create()
                try { return ([BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($Path))) -replace '-', '').ToLowerInvariant() }
                finally { $sha.Dispose() }
            }
            function Read-AppLogLinesSafe { param([string]$Path) return @(Get-Content -LiteralPath $Path -ErrorAction Stop) }
            function Sync-GuiSmokeAppLogLedger { Sync-GuiSmokeLogLedger -State $script:GuiSmokeAppLogLedger -Paths @($script:testLogPath) }
            function Wait-GuiSmokeProcessOutputCaptureDrain { param([object[]]$Processes) Start-Sleep -Milliseconds 50 }
            function Copy-FileTransferLiveLogSlice {
                param(
                    [string]$ArtifactDir,
                    [int]$Bookmark,
                    [string]$FileName = 'filetransfer-retained-log-slice.log',
                    $CapturedLines,
                    [switch]$WriteOnce,
                    [switch]$PassThru)
                $path = Join-Path $ArtifactDir $FileName
                $lines = @($CapturedLines)
                $text = if ($lines.Count -gt 0) { ($lines -join [Environment]::NewLine) + [Environment]::NewLine } else { '' }
                [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
                if ($PassThru) {
                    return [pscustomobject]@{ Path = [IO.Path]::GetFullPath($path); Sha256 = Get-FileSha256Hex -Path $path; LineCount = $lines.Count }
                }
            }

            $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID = 'run-current'
            $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID = 'run-current:scenario:1'
            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-ledger-marker-' + [guid]::NewGuid().ToString('N'))
            $artifactDir = Join-Path $tempRoot 'current'
            $currentOutput = Join-Path $artifactDir 'process-output'
            $oldOutput = Join-Path $tempRoot 'old\process-output'
            [void][IO.Directory]::CreateDirectory($currentOutput)
            [void][IO.Directory]::CreateDirectory($oldOutput)
            try {
                $nl = [Environment]::NewLine
                $utf8 = [Text.UTF8Encoding]::new($false)
                $script:testLogPath = Join-Path $tempRoot 'nlink.log'
                $start = '[app] event=operational_log_writer_started; acceptance_run_id=run-current; scenario_run_id=run-current:scenario:1; log_writer_id=writer-current; log_writer_sequence=1'
                [IO.File]::WriteAllText($script:testLogPath, $start + $nl, $utf8)
                $script:GuiSmokeAppLogLedger = New-GuiSmokeLogLedgerState
                Sync-GuiSmokeAppLogLedger
                $bookmark = Get-GuiSmokeLogLedgerBookmark -State $script:GuiSmokeAppLogLedger
                $initial = Get-GuiSmokeLogLedgerSnapshot -State $script:GuiSmokeAppLogLedger -Bookmark $bookmark
                Set-GuiSmokeLogLedgerWriterEvidenceBookmark -State $script:GuiSmokeAppLogLedger -Bookmark $bookmark
                [IO.File]::AppendAllText($script:testLogPath, '[app] event=after; log_writer_id=writer-current; log_writer_sequence=2' + $nl, $utf8)

                $currentError = Join-Path $currentOutput 'helper.stderr.log'
                $oldError = Join-Path $oldOutput 'helper.stderr.log'
                [IO.File]::WriteAllText($currentError, '', $utf8)
                [IO.File]::WriteAllText($oldError, 'event=operational_log_write_failed; log_writer_id=writer-old; log_writer_sequence=9; failure_code=io_error' + $nl, $utf8)
                $script:GuiSmokeProcessOutputFiles = [Collections.Generic.List[string]]::new()
                $script:GuiSmokeProcessOutputFiles.Add($oldError)
                $script:GuiSmokeProcessOutputFiles.Add($currentError)

                Write-FileTransferGuiLogLedgerEvidence `
                    -ArtifactDir $artifactDir `
                    -Bookmark $bookmark `
                    -InitialSnapshot $initial `
                    -ProcessOutputDrainAction {
                        Start-Sleep -Milliseconds 100
                        [IO.File]::AppendAllText($script:testLogPath, '[app] event=after_drain_boundary; log_writer_id=writer-current; log_writer_sequence=3' + $nl, $utf8)
                        [IO.File]::AppendAllText($currentError, 'event=operational_log_write_failed; log_writer_id=writer-current; log_writer_sequence=4; failure_code=mutex_timeout' + $nl, $utf8)
                    }
                $ledger = Get-Content -LiteralPath (Join-Path $artifactDir 'filetransfer-gui-log-ledger.json') -Raw | ConvertFrom-Json
                if ($ledger.writerEvidenceVersion -ne 3 -or
                    $ledger.writerWriteFailureCount -ne 1 -or
                    $ledger.writerIntegrityVerdict -ne 'fail' -or
                    $ledger.cursorIntegrityVerdict -ne 'fail') {
                    throw 'writer failure did not fail cursor evidence'
                }
                if ($ledger.final.writers[0].lastSequence -ne 3 -or
                    (Get-Content -LiteralPath $ledger.retainedSlice.path -Raw) -notlike '*event=after_drain_boundary*') {
                    throw 'authoritative final sync did not include a write produced during process drain'
                }
                $markers = @(Get-Content -LiteralPath $ledger.writerFailureMarkerFiles[0].path)
                if ($markers.Count -ne 1 -or $markers[0] -notlike '*writer-current*' -or $markers[0] -like '*writer-old*') {
                    throw 'writer failure markers were not scoped to the current artifact'
                }

                $missingArtifactDir = Join-Path $tempRoot 'current-missing-output'
                $missingOutputRoot = Join-Path $missingArtifactDir 'process-output'
                [void][IO.Directory]::CreateDirectory($missingOutputRoot)
                $script:testLogPath = Join-Path $tempRoot 'missing-output-nlink.log'
                [IO.File]::WriteAllText($script:testLogPath, $start + $nl, $utf8)
                $script:GuiSmokeAppLogLedger = New-GuiSmokeLogLedgerState
                Sync-GuiSmokeAppLogLedger
                $missingBookmark = Get-GuiSmokeLogLedgerBookmark -State $script:GuiSmokeAppLogLedger
                $missingInitial = Get-GuiSmokeLogLedgerSnapshot -State $script:GuiSmokeAppLogLedger -Bookmark $missingBookmark
                Set-GuiSmokeLogLedgerWriterEvidenceBookmark -State $script:GuiSmokeAppLogLedger -Bookmark $missingBookmark
                [IO.File]::AppendAllText($script:testLogPath, '[app] event=after; log_writer_id=writer-current; log_writer_sequence=2' + $nl, $utf8)
                $presentOutput = Join-Path $missingOutputRoot 'helper.stdout.log'
                $missingOutput = Join-Path $missingOutputRoot 'helper.stderr.log'
                [IO.File]::WriteAllText($presentOutput, '', $utf8)
                $script:GuiSmokeProcessOutputFiles = [Collections.Generic.List[string]]::new()
                $script:GuiSmokeProcessOutputFiles.Add($presentOutput)
                $script:GuiSmokeProcessOutputFiles.Add($missingOutput)
                Write-FileTransferGuiLogLedgerEvidence `
                    -ArtifactDir $missingArtifactDir `
                    -Bookmark $missingBookmark `
                    -InitialSnapshot $missingInitial
                $missingLedger = Get-Content -LiteralPath (Join-Path $missingArtifactDir 'filetransfer-gui-log-ledger.json') -Raw | ConvertFrom-Json
                if ($missingLedger.writerFailureMarkerFiles.Count -ne 1 -or
                    $missingLedger.writerWriteFailureCount -ne 0 -or
                    $missingLedger.writerIntegrityVerdict -ne 'fail' -or
                    $missingLedger.cursorIntegrityVerdict -ne 'fail' -or
                    @($missingLedger.writerIntegrityReasons | Where-Object { $_ -like 'writer_output_registered_file_missing:*' }).Count -ne 1) {
                    throw 'one missing registered output file did not fail otherwise-valid writer evidence'
                }

                $recoveredArtifactDir = Join-Path $tempRoot 'current-recovered'
                $recoveredOutputRoot = Join-Path $recoveredArtifactDir 'process-output'
                [void][IO.Directory]::CreateDirectory($recoveredOutputRoot)
                $script:testLogPath = Join-Path $tempRoot 'recovered-nlink.log'
                $recoveredStart = '[app] event=operational_log_writer_started; acceptance_run_id=run-current; scenario_run_id=run-current:scenario:1; log_writer_id=writer-current; log_writer_sequence=1; log_writer_batch_generation=1; log_writer_batch_first_sequence=1; log_writer_batch_last_sequence=1; log_writer_attempt_utc_ticks=100'
                [IO.File]::WriteAllText($script:testLogPath, $recoveredStart + $nl, $utf8)
                $script:GuiSmokeAppLogLedger = New-GuiSmokeLogLedgerState
                Sync-GuiSmokeAppLogLedger
                $recoveredBookmark = Get-GuiSmokeLogLedgerBookmark -State $script:GuiSmokeAppLogLedger
                $recoveredInitial = Get-GuiSmokeLogLedgerSnapshot -State $script:GuiSmokeAppLogLedger -Bookmark $recoveredBookmark
                Set-GuiSmokeLogLedgerWriterEvidenceBookmark -State $script:GuiSmokeAppLogLedger -Bookmark $recoveredBookmark
                $recoveredPayload = '[app] event=after; log_writer_id=writer-current; log_writer_sequence=2; log_writer_batch_generation=2; log_writer_batch_first_sequence=2; log_writer_batch_last_sequence=2; log_writer_attempt_utc_ticks=200'
                $recoveredDrain = '[app] event=operational_log_writer_drained; acceptance_run_id=run-current; scenario_run_id=run-current:scenario:1; last_payload_sequence=2; log_writer_id=writer-current; log_writer_sequence=3; log_writer_batch_generation=3; log_writer_batch_first_sequence=3; log_writer_batch_last_sequence=3; log_writer_attempt_utc_ticks=300'
                [IO.File]::AppendAllText($script:testLogPath, $recoveredPayload + $nl + $recoveredDrain + $nl, $utf8)
                $payloadHash = Get-GuiSmokeLogLedgerHash -Value $recoveredPayload
                $recoveredError = Join-Path $recoveredOutputRoot 'helper.stderr.log'
                $deferred = 'event=operational_log_write_deferred; acceptance_run_id=run-current; scenario_run_id=run-current:scenario:1; log_writer_id=writer-current; batch_generation=2; first_sequence=2; last_sequence=2; line_count=1; payload_sha256=' + $payloadHash + '; attempt_utc_ticks=200; attempt_count=1; failure_code=mutex_timeout'
                $recovered = 'event=operational_log_write_recovered; acceptance_run_id=run-current; scenario_run_id=run-current:scenario:1; log_writer_id=writer-current; batch_generation=2; first_sequence=2; last_sequence=2; line_count=1; payload_sha256=' + $payloadHash + '; attempt_utc_ticks=200; attempt_count=2; failure_code=none'
                [IO.File]::WriteAllText($recoveredError, $deferred + $nl + $recovered + $nl, $utf8)
                $script:GuiSmokeProcessOutputFiles = [Collections.Generic.List[string]]::new()
                $script:GuiSmokeProcessOutputFiles.Add($recoveredError)
                Write-FileTransferGuiLogLedgerEvidence `
                    -ArtifactDir $recoveredArtifactDir `
                    -Bookmark $recoveredBookmark `
                    -InitialSnapshot $recoveredInitial
                $recoveredLedger = Get-Content -LiteralPath (Join-Path $recoveredArtifactDir 'filetransfer-gui-log-ledger.json') -Raw | ConvertFrom-Json
                if ($recoveredLedger.writerDeferredBatchCount -ne 1 -or
                    $recoveredLedger.writerRecoveredBatchCount -ne 1 -or
                    $recoveredLedger.writerUnresolvedDeferredBatchCount -ne 0 -or
                    $recoveredLedger.writerDrainMissingCount -ne 0 -or
                    $recoveredLedger.writerRecoveryVerdict -ne 'pass' -or
                    $recoveredLedger.writerIntegrityVerdict -ne 'pass') {
                    throw 'exact recovered writer batch and final drain did not pass v3 writer evidence'
                }

                $persistedTimestampArtifactDir = Join-Path $tempRoot 'persisted-timestamp-mismatch'
                $persistedTimestampOutputRoot = Join-Path $persistedTimestampArtifactDir 'process-output'
                [void][IO.Directory]::CreateDirectory($persistedTimestampOutputRoot)
                $script:testLogPath = Join-Path $tempRoot 'persisted-timestamp-mismatch.log'
                [IO.File]::WriteAllText($script:testLogPath, $recoveredStart + $nl, $utf8)
                $script:GuiSmokeAppLogLedger = New-GuiSmokeLogLedgerState
                Sync-GuiSmokeAppLogLedger
                $persistedTimestampBookmark = Get-GuiSmokeLogLedgerBookmark -State $script:GuiSmokeAppLogLedger
                $persistedTimestampInitial = Get-GuiSmokeLogLedgerSnapshot -State $script:GuiSmokeAppLogLedger -Bookmark $persistedTimestampBookmark
                Set-GuiSmokeLogLedgerWriterEvidenceBookmark -State $script:GuiSmokeAppLogLedger -Bookmark $persistedTimestampBookmark
                $wrongPersistedTimestampPayload = $recoveredPayload -replace 'log_writer_attempt_utc_ticks=200', 'log_writer_attempt_utc_ticks=201'
                [IO.File]::AppendAllText(
                    $script:testLogPath,
                    $wrongPersistedTimestampPayload + $nl + $recoveredDrain + $nl,
                    $utf8)
                $wrongPersistedTimestampHash = Get-GuiSmokeLogLedgerHash -Value $wrongPersistedTimestampPayload
                $persistedTimestampDeferred = $deferred -replace [regex]::Escape($payloadHash), $wrongPersistedTimestampHash
                $persistedTimestampRecovered = $recovered -replace [regex]::Escape($payloadHash), $wrongPersistedTimestampHash
                $persistedTimestampError = Join-Path $persistedTimestampOutputRoot 'helper.stderr.log'
                [IO.File]::WriteAllText(
                    $persistedTimestampError,
                    $persistedTimestampDeferred + $nl + $persistedTimestampRecovered + $nl,
                    $utf8)
                $script:GuiSmokeProcessOutputFiles = [Collections.Generic.List[string]]::new()
                $script:GuiSmokeProcessOutputFiles.Add($persistedTimestampError)
                Write-FileTransferGuiLogLedgerEvidence `
                    -ArtifactDir $persistedTimestampArtifactDir `
                    -Bookmark $persistedTimestampBookmark `
                    -InitialSnapshot $persistedTimestampInitial
                $persistedTimestampLedger = Get-Content `
                    -LiteralPath (Join-Path $persistedTimestampArtifactDir 'filetransfer-gui-log-ledger.json') `
                    -Raw |
                    ConvertFrom-Json
                if ($persistedTimestampLedger.writerUnresolvedDeferredBatchCount -ne 1 -or
                    $persistedTimestampLedger.writerRecoveryMismatchCount -le 0 -or
                    $persistedTimestampLedger.writerRecoveryVerdict -ne 'fail' -or
                    [bool]$persistedTimestampLedger.writerDeferredBatches[0].recovered) {
                    throw 'persisted batch timestamp mismatch was accepted as recovered'
                }

                $failureCaseIndex = 0
                function Assert-RecoveryMarkerFailure {
                    param([string]$Name, [string[]]$MarkerLines)
                    $script:failureCaseIndex++
                    $caseArtifactDir = Join-Path $tempRoot ("recovery-failure-$($script:failureCaseIndex)-$Name")
                    $caseOutputRoot = Join-Path $caseArtifactDir 'process-output'
                    [void][IO.Directory]::CreateDirectory($caseOutputRoot)
                    $script:testLogPath = Join-Path $tempRoot 'recovered-nlink.log'
                    $script:GuiSmokeAppLogLedger = New-GuiSmokeLogLedgerState
                    Sync-GuiSmokeAppLogLedger
                    $caseBookmark = Get-GuiSmokeLogLedgerBookmark -State $script:GuiSmokeAppLogLedger
                    $caseInitial = Get-GuiSmokeLogLedgerSnapshot -State $script:GuiSmokeAppLogLedger -Bookmark $caseBookmark
                    Set-GuiSmokeLogLedgerWriterEvidenceBookmark -State $script:GuiSmokeAppLogLedger -Bookmark $caseBookmark
                    $caseError = Join-Path $caseOutputRoot 'helper.stderr.log'
                    [IO.File]::WriteAllText($caseError, (($MarkerLines -join $nl) + $nl), $utf8)
                    $script:GuiSmokeProcessOutputFiles = [Collections.Generic.List[string]]::new()
                    $script:GuiSmokeProcessOutputFiles.Add($caseError)
                    Write-FileTransferGuiLogLedgerEvidence `
                        -ArtifactDir $caseArtifactDir `
                        -Bookmark $caseBookmark `
                        -InitialSnapshot $caseInitial
                    $caseLedger = Get-Content -LiteralPath (Join-Path $caseArtifactDir 'filetransfer-gui-log-ledger.json') -Raw | ConvertFrom-Json
                    if ($caseLedger.writerRecoveryVerdict -ne 'fail' -or
                        $caseLedger.writerRecoveryMismatchCount -le 0 -or
                        $caseLedger.writerIntegrityVerdict -ne 'fail') {
                        throw "$Name marker evidence unexpectedly passed"
                    }
                }

                Assert-RecoveryMarkerFailure -Name 'missing-recovery' -MarkerLines @($deferred)
                Assert-RecoveryMarkerFailure -Name 'duplicate-recovery' -MarkerLines @($deferred, $recovered, $recovered)
                $orphanRecovery = $recovered `
                    -replace 'batch_generation=2', 'batch_generation=99' `
                    -replace 'first_sequence=2', 'first_sequence=99' `
                    -replace 'last_sequence=2', 'last_sequence=99'
                Assert-RecoveryMarkerFailure -Name 'orphan-recovery' -MarkerLines @($deferred, $recovered, $orphanRecovery)
                $wrongRunDeferred = $deferred -replace 'acceptance_run_id=run-current', 'acceptance_run_id=run-other'
                $wrongRunRecovered = $recovered -replace 'acceptance_run_id=run-current', 'acceptance_run_id=run-other'
                Assert-RecoveryMarkerFailure -Name 'run-mismatch' -MarkerLines @($wrongRunDeferred, $wrongRunRecovered)
                $wrongTimestampRecovery = $recovered -replace 'attempt_utc_ticks=200', 'attempt_utc_ticks=201'
                Assert-RecoveryMarkerFailure -Name 'timestamp-mismatch' -MarkerLines @($deferred, $wrongTimestampRecovery)

                $crossArtifactDir = Join-Path $tempRoot 'cross-writer-recovery'
                $crossOutputRoot = Join-Path $crossArtifactDir 'process-output'
                $crossLogRoot = Join-Path $tempRoot 'cross-writer-log'
                [void][IO.Directory]::CreateDirectory($crossOutputRoot)
                [void][IO.Directory]::CreateDirectory($crossLogRoot)
                $script:testLogPath = Join-Path $crossLogRoot 'nlink.log'
                $writerAStart = '[a] event=operational_log_writer_started; acceptance_run_id=run-current; scenario_run_id=run-current:scenario:1; log_writer_id=writer-a; log_writer_sequence=1; log_writer_batch_generation=1; log_writer_batch_first_sequence=1; log_writer_batch_last_sequence=1; log_writer_attempt_utc_ticks=100'
                $writerBStart = '[b] event=operational_log_writer_started; acceptance_run_id=run-current; scenario_run_id=run-current:scenario:1; log_writer_id=writer-b; log_writer_sequence=1; log_writer_batch_generation=1; log_writer_batch_first_sequence=1; log_writer_batch_last_sequence=1; log_writer_attempt_utc_ticks=100'
                [IO.File]::WriteAllText($script:testLogPath, $writerAStart + $nl + $writerBStart + $nl, $utf8)
                $script:GuiSmokeAppLogLedger = New-GuiSmokeLogLedgerState
                Sync-GuiSmokeAppLogLedger
                $crossBookmark = Get-GuiSmokeLogLedgerBookmark -State $script:GuiSmokeAppLogLedger
                $crossInitial = Get-GuiSmokeLogLedgerSnapshot -State $script:GuiSmokeAppLogLedger -Bookmark $crossBookmark
                Set-GuiSmokeLogLedgerWriterEvidenceBookmark -State $script:GuiSmokeAppLogLedger -Bookmark $crossBookmark
                $writerBPayload = '[b] event=after; log_writer_id=writer-b; log_writer_sequence=2; log_writer_batch_generation=2; log_writer_batch_first_sequence=2; log_writer_batch_last_sequence=2; log_writer_attempt_utc_ticks=300'
                $writerAPayload = '[a] event=after; log_writer_id=writer-a; log_writer_sequence=2; log_writer_batch_generation=2; log_writer_batch_first_sequence=2; log_writer_batch_last_sequence=2; log_writer_attempt_utc_ticks=200'
                $writerADrain = '[a] event=operational_log_writer_drained; acceptance_run_id=run-current; scenario_run_id=run-current:scenario:1; last_payload_sequence=2; log_writer_id=writer-a; log_writer_sequence=3; log_writer_batch_generation=3; log_writer_batch_first_sequence=3; log_writer_batch_last_sequence=3; log_writer_attempt_utc_ticks=400'
                $writerBDrain = '[b] event=operational_log_writer_drained; acceptance_run_id=run-current; scenario_run_id=run-current:scenario:1; last_payload_sequence=2; log_writer_id=writer-b; log_writer_sequence=3; log_writer_batch_generation=3; log_writer_batch_first_sequence=3; log_writer_batch_last_sequence=3; log_writer_attempt_utc_ticks=400'
                [IO.File]::AppendAllText($script:testLogPath, $writerBPayload + $nl + $writerAPayload + $nl + $writerADrain + $nl + $writerBDrain + $nl, $utf8)
                $writerAHash = Get-GuiSmokeLogLedgerHash -Value $writerAPayload
                $writerADeferred = 'event=operational_log_write_deferred; acceptance_run_id=run-current; scenario_run_id=run-current:scenario:1; log_writer_id=writer-a; batch_generation=2; first_sequence=2; last_sequence=2; line_count=1; payload_sha256=' + $writerAHash + '; attempt_utc_ticks=200; attempt_count=1; failure_code=mutex_timeout'
                $writerARecovered = 'event=operational_log_write_recovered; acceptance_run_id=run-current; scenario_run_id=run-current:scenario:1; log_writer_id=writer-a; batch_generation=2; first_sequence=2; last_sequence=2; line_count=1; payload_sha256=' + $writerAHash + '; attempt_utc_ticks=200; attempt_count=2; failure_code=none'
                $crossError = Join-Path $crossOutputRoot 'helper.stderr.log'
                [IO.File]::WriteAllText($crossError, $writerADeferred + $nl + $writerARecovered + $nl, $utf8)
                $script:GuiSmokeProcessOutputFiles = [Collections.Generic.List[string]]::new()
                $script:GuiSmokeProcessOutputFiles.Add($crossError)
                Write-FileTransferGuiLogLedgerEvidence `
                    -ArtifactDir $crossArtifactDir `
                    -Bookmark $crossBookmark `
                    -InitialSnapshot $crossInitial
                $crossLedger = Get-Content -LiteralPath (Join-Path $crossArtifactDir 'filetransfer-gui-log-ledger.json') -Raw | ConvertFrom-Json
                if ($crossLedger.writerChronologyInversionCount -ne 0 -or
                    $crossLedger.writerChronologyCollisionCount -ne 0 -or
                    $crossLedger.writerChronologyVerdict -ne 'pass' -or
                    $crossLedger.writerIntegrityVerdict -ne 'pass' -or
                    $crossLedger.cursorIntegrityVerdict -ne 'pass') {
                    throw "cross-writer deferred/recovered ordering did not pass complete ledger validation: chronology=$($crossLedger.writerChronologyInversionCount)/$($crossLedger.writerChronologyCollisionCount)/$($crossLedger.writerChronologyVerdict); writer=$($crossLedger.writerIntegrityVerdict):$(@($crossLedger.writerIntegrityReasons) -join ','); cursor=$($crossLedger.cursorIntegrityVerdict):$(@($crossLedger.cursorIntegrityReasons) -join ',')"
                }

                $missingDrainArtifactDir = Join-Path $tempRoot 'missing-drain'
                $missingDrainOutputRoot = Join-Path $missingDrainArtifactDir 'process-output'
                [void][IO.Directory]::CreateDirectory($missingDrainOutputRoot)
                $script:testLogPath = Join-Path $tempRoot 'missing-drain-nlink.log'
                [IO.File]::WriteAllText($script:testLogPath, $recoveredStart + $nl + $recoveredPayload + $nl, $utf8)
                $script:GuiSmokeAppLogLedger = New-GuiSmokeLogLedgerState
                Sync-GuiSmokeAppLogLedger
                $missingDrainBookmark = Get-GuiSmokeLogLedgerBookmark -State $script:GuiSmokeAppLogLedger
                $missingDrainInitial = Get-GuiSmokeLogLedgerSnapshot -State $script:GuiSmokeAppLogLedger -Bookmark $missingDrainBookmark
                Set-GuiSmokeLogLedgerWriterEvidenceBookmark -State $script:GuiSmokeAppLogLedger -Bookmark $missingDrainBookmark
                $missingDrainError = Join-Path $missingDrainOutputRoot 'helper.stderr.log'
                [IO.File]::WriteAllText($missingDrainError, '', $utf8)
                $script:GuiSmokeProcessOutputFiles = [Collections.Generic.List[string]]::new()
                $script:GuiSmokeProcessOutputFiles.Add($missingDrainError)
                Write-FileTransferGuiLogLedgerEvidence `
                    -ArtifactDir $missingDrainArtifactDir `
                    -Bookmark $missingDrainBookmark `
                    -InitialSnapshot $missingDrainInitial
                $missingDrainLedger = Get-Content -LiteralPath (Join-Path $missingDrainArtifactDir 'filetransfer-gui-log-ledger.json') -Raw | ConvertFrom-Json
                if ($missingDrainLedger.writerDrainMissingCount -ne 1 -or
                    $missingDrainLedger.writerRecoveryVerdict -ne 'fail' -or
                    $missingDrainLedger.writerIntegrityVerdict -ne 'fail') {
                    throw 'missing writer drain did not fail producer evidence'
                }
                Write-Output 'gui-log-ledger-marker-ok'
            }
            finally {
                Remove-Item Env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID -ErrorAction SilentlyContinue
                Remove-Item Env:NLINK_FILETRANSFER_SCENARIO_RUN_ID -ErrorAction SilentlyContinue
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(result.ExitCode == 0, $"Writer marker test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-log-ledger-marker-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeLogLedger_ArtifactScaleWriterEvidenceUsesLinearVisitsAndLookups()
    {
        var repoRoot = FindRepoRoot();
        var helperPath = Path.Combine(repoRoot, "tools", "GuiSmokeLogLedger.ps1")
            .Replace("'", "''", StringComparison.Ordinal);
        var guiText = File.ReadAllText(Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1"));
        var chronologyBody = ExtractPowerShellFunctionBody(
            guiText,
            "Get-GuiSmokeLogWriterChronology",
            "Write-FileTransferGuiLogLedgerEvidence");
        var writerBody = ExtractPowerShellFunctionBody(
            guiText,
            "Write-FileTransferGuiLogLedgerEvidence",
            "Write-TunaGuiMilestoneEvidence");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            . '{{helperPath}}'
            function Get-GuiSmokeLogWriterChronology { {{chronologyBody}} }
            function Write-FileTransferGuiLogLedgerEvidence { {{writerBody}} }
            function Sync-GuiSmokeAppLogLedger {}
            function Wait-GuiSmokeProcessOutputCaptureDrain { param([object[]]$Processes) }
            function Get-GuiSmokeLogLedgerSnapshotIntegrityReasons { return @() }
            function Get-GuiSmokeLogLedgerSnapshot { return $script:finalSnapshot }
            function Get-GuiSmokeLogLedgerDiagnostics {
                return [pscustomobject]@{
                    RotationCount = 0
                    TransientRescanCount = 0
                    GapCount = 0
                    DuplicateIndexCount = 0
                    WriterGapCount = 0L
                    WriterDuplicateCount = 0L
                    WriterIdentityMismatchCount = 0L
                    ConsumedIndexCount = 200000
                    ConsumedIndexSha256 = 'synthetic-consumed-index'
                    ConsumedIndexRanges = @('0-199999')
                    SyncCount = 1
                    FailureCode = '(none)'
                    FailureDetail = '(none)'
                }
            }

            $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID = 'run-scale'
            $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID = 'run-scale:scenario:1'
            Remove-Item Env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH -ErrorAction SilentlyContinue
            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-ledger-scale-' + [guid]::NewGuid().ToString('N'))
            $artifactDir = Join-Path $tempRoot 'artifact'
            $outputRoot = Join-Path $artifactDir 'process-output'
            [void][IO.Directory]::CreateDirectory($outputRoot)
            $markerWriter = $null
            $ledgerWriter = $null
            $payloadHasher = $null
            try {
                $script:GuiSmokeAppLogLedger = New-GuiSmokeLogLedgerState
                $ledgerPath = Join-Path $tempRoot 'nlink.log'
                Enable-GuiSmokeAppendOnlyLogLedgerState `
                    -State $script:GuiSmokeAppLogLedger `
                    -Path $ledgerPath `
                    -MaximumBytes (512L * 1024L * 1024L)
                $ledgerWriter = [IO.StreamWriter]::new(
                    $ledgerPath,
                    $true,
                    [Text.UTF8Encoding]::new($false),
                    65536)
                $markerPath = Join-Path $outputRoot 'scale.stderr.log'
                $markerWriter = [IO.StreamWriter]::new(
                    $markerPath,
                    $false,
                    [Text.UTF8Encoding]::new($false),
                    65536)
                $payloadHasher = [Security.Cryptography.SHA256]::Create()
                $utf8 = [Text.UTF8Encoding]::new($false)
                for ($sequence = 1; $sequence -le 100000; $sequence++) {
                    $event = if ($sequence -eq 1) {
                        'operational_log_writer_started'
                    }
                    elseif ($sequence -eq 100000) {
                        'operational_log_writer_drained'
                    }
                    else {
                        'payload'
                    }
                    $lineA = '[a] event=' + $event +
                        '; acceptance_run_id=run-scale; scenario_run_id=run-scale:scenario:1' +
                        '; log_writer_id=writer-a; log_writer_sequence=' + $sequence +
                        '; log_writer_batch_generation=' + $sequence +
                        '; log_writer_batch_first_sequence=' + $sequence +
                        '; log_writer_batch_last_sequence=' + $sequence +
                        '; log_writer_attempt_utc_ticks=' + (1000000 + $sequence) +
                        '; payload_padding=' + ('a' * 650)
                    $lineB = '[b] event=' + $event +
                        '; acceptance_run_id=run-scale; scenario_run_id=run-scale:scenario:1' +
                        '; log_writer_id=writer-b; log_writer_sequence=' + $sequence +
                        '; log_writer_batch_generation=' + $sequence +
                        '; log_writer_batch_first_sequence=' + $sequence +
                        '; log_writer_batch_last_sequence=' + $sequence +
                        '; log_writer_attempt_utc_ticks=' + (2000000 + $sequence) +
                        '; payload_padding=' + ('b' * 650)
                    $ledgerWriter.WriteLine($lineA)
                    $ledgerWriter.WriteLine($lineB)
                    if ($sequence -le 100) {
                        $payloadHash = (([BitConverter]::ToString($payloadHasher.ComputeHash($utf8.GetBytes($lineA)))) -replace '-', '').ToLowerInvariant()
                        $common = 'acceptance_run_id=run-scale; scenario_run_id=run-scale:scenario:1' +
                            '; log_writer_id=writer-a; batch_generation=' + $sequence +
                            '; first_sequence=' + $sequence + '; last_sequence=' + $sequence +
                            '; line_count=1; payload_sha256=' + $payloadHash +
                            '; attempt_utc_ticks=' + (1000000 + $sequence)
                        $markerWriter.WriteLine('event=operational_log_write_deferred; ' + $common + '; attempt_count=1; failure_code=mutex_timeout')
                        $markerWriter.WriteLine('event=operational_log_write_recovered; ' + $common + '; attempt_count=2; failure_code=none')
                    }
                }
                $markerWriter.Flush()
                $markerWriter.Dispose()
                $markerWriter = $null
                $ledgerWriter.Flush()
                $ledgerWriter.Dispose()
                $ledgerWriter = $null
                Sync-GuiSmokeLogLedger -State $script:GuiSmokeAppLogLedger -Paths @($ledgerPath)
                if ($script:GuiSmokeAppLogLedger.Lines.Count -ne 0 -or
                    (Get-GuiSmokeLogLedgerBookmark -State $script:GuiSmokeAppLogLedger) -ne 200000) {
                    throw 'artifact-scale source was not retained as one disk-backed append-only ledger'
                }
                $payloadHasher.Dispose()
                $payloadHasher = $null

                $script:finalSnapshot = [pscustomobject]@{
                    ledgerId = 'scale-ledger'
                    bookmark = 0
                    lineCount = 200000
                    activeGenerationKey = 'scale-generation'
                    generations = @()
                    writers = @(
                        [pscustomobject]@{ writerId = 'writer-a'; acceptanceRunId = 'run-scale'; scenarioRunId = 'run-scale:scenario:1'; lastSequence = 100000 },
                        [pscustomobject]@{ writerId = 'writer-b'; acceptanceRunId = 'run-scale'; scenarioRunId = 'run-scale:scenario:1'; lastSequence = 100000 })
                }
                $initialSnapshot = [pscustomobject]@{
                    bookmark = 0
                    lineCount = 0
                    activeGenerationKey = 'scale-generation'
                    generations = @()
                    writers = @()
                }
                $script:GuiSmokeProcessOutputFiles = [Collections.Generic.List[string]]::new()
                $script:GuiSmokeProcessOutputFiles.Add($markerPath)
                $metrics = [pscustomobject]@{
                    ledgerLineVisitCount = 0L
                    markerLineVisitCount = 0L
                    deferredLookupCount = 0L
                    recoveredLookupCount = 0L
                }

                $finalizationStopwatch = [Diagnostics.Stopwatch]::StartNew()
                try {
                    Write-FileTransferGuiLogLedgerEvidence `
                        -ArtifactDir $artifactDir `
                        -Bookmark 0 `
                        -InitialSnapshot $initialSnapshot `
                        -TestMetrics $metrics
                }
                finally {
                    $finalizationStopwatch.Stop()
                }
                $ledger = Get-Content -LiteralPath (Join-Path $artifactDir 'filetransfer-gui-log-ledger.json') -Raw | ConvertFrom-Json
                if ([long]$metrics.ledgerLineVisitCount -ne 200000 -or
                    [long]$metrics.markerLineVisitCount -ne 200 -or
                    [long]$metrics.deferredLookupCount -ne 100 -or
                    [long]$metrics.recoveredLookupCount -ne 100) {
                    throw "nonlinear visit metrics: $($metrics | ConvertTo-Json -Compress)"
                }
                if ($ledger.writerDeferredBatchCount -ne 100 -or
                    $ledger.writerRecoveredBatchCount -ne 100 -or
                    $ledger.writerUnresolvedDeferredBatchCount -ne 0 -or
                    $ledger.writerRecoveryMismatchCount -ne 0 -or
                    $ledger.writerChronologyInversionCount -ne 0 -or
                    $ledger.writerChronologyCollisionCount -ne 0 -or
                    $ledger.writerDrainMissingCount -ne 0 -or
                    $ledger.writerIntegrityVerdict -ne 'pass' -or
                    $ledger.cursorIntegrityVerdict -ne 'pass') {
                    throw "artifact-scale writer evidence failed: $($ledger | ConvertTo-Json -Compress -Depth 4)"
                }
                if ($ledger.retainedSlice.sha256 -ne $ledger.analyzerInput.captureSha256 -or
                    $ledger.retainedSlice.lineCount -ne 200000 -or
                    $ledger.analyzerInput.captureLineCount -ne 200000) {
                    throw 'artifact-scale immutable capture evidence diverged'
                }
                if ($ledger.writerEvidenceSlice.sha256 -ne $ledger.retainedSlice.sha256 -or
                    $ledger.writerEvidenceSlice.lineCount -ne 200000) {
                    throw 'artifact-scale writer evidence diverged from the frozen ledger'
                }
                if ($finalizationStopwatch.ElapsedMilliseconds -ge 30000) {
                    throw "compiled writer validation exceeded 30-second scale gate: $($finalizationStopwatch.ElapsedMilliseconds)ms"
                }
                Write-Output "gui-log-ledger-scale-linear-ok elapsed_ms=$($finalizationStopwatch.ElapsedMilliseconds)"
            }
            finally {
                if ($markerWriter) { $markerWriter.Dispose() }
                if ($ledgerWriter) { $ledgerWriter.Dispose() }
                if ($payloadHasher) { $payloadHasher.Dispose() }
                Remove-Item Env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID -ErrorAction SilentlyContinue
                Remove-Item Env:NLINK_FILETRANSFER_SCENARIO_RUN_ID -ErrorAction SilentlyContinue
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(result.ExitCode == 0, $"Artifact-scale writer validation failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-log-ledger-scale-linear-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeLogLedger_FrozenArtifactWriterValidationBenchmarkCompletesInsideGrace()
    {
        var sourcePath = Environment.GetEnvironmentVariable("NLINK_GUI_LEDGER_BENCHMARK_SOURCE");
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var helperPath = Path.Combine(repoRoot, "tools", "GuiSmokeLogLedger.ps1")
            .Replace("'", "''", StringComparison.Ordinal);
        var escapedSourcePath = Path.GetFullPath(sourcePath)
            .Replace("'", "''", StringComparison.Ordinal);
        var guiText = File.ReadAllText(Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1"));
        var chronologyBody = ExtractPowerShellFunctionBody(
            guiText,
            "Get-GuiSmokeLogWriterChronology",
            "Write-FileTransferGuiLogLedgerEvidence");
        var writerBody = ExtractPowerShellFunctionBody(
            guiText,
            "Write-FileTransferGuiLogLedgerEvidence",
            "Write-TunaGuiMilestoneEvidence");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            . '{{helperPath}}'
            function Get-GuiSmokeLogWriterChronology { {{chronologyBody}} }
            function Write-FileTransferGuiLogLedgerEvidence { {{writerBody}} }
            function Sync-GuiSmokeAppLogLedger {}
            function Wait-GuiSmokeProcessOutputCaptureDrain { param([object[]]$Processes) }

            $sourcePath = '{{escapedSourcePath}}'
            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-ledger-benchmark-' + [guid]::NewGuid().ToString('N'))
            $artifactDir = Join-Path $tempRoot 'artifact'
            $outputRoot = Join-Path $artifactDir 'process-output'
            [void][IO.Directory]::CreateDirectory($outputRoot)
            $source = $null
            $destination = $null
            try {
                $script:GuiSmokeAppLogLedger = New-GuiSmokeLogLedgerState
                $ledgerPath = Join-Path $tempRoot 'nlink.log'
                Enable-GuiSmokeAppendOnlyLogLedgerState `
                    -State $script:GuiSmokeAppLogLedger `
                    -Path $ledgerPath `
                    -MaximumBytes (4L * 1024L * 1024L * 1024L)
                $source = [IO.FileStream]::new($sourcePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
                $destination = [IO.FileStream]::new($ledgerPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::Read)
                $source.CopyTo($destination, 1048576)
                if ($destination.Position -gt 0) {
                    $destination.Position--
                    $lastByte = $destination.ReadByte()
                    $destination.Position = $destination.Length
                    if ($lastByte -ne 10) {
                        $destination.WriteByte(10)
                    }
                }
                $destination.Flush($true)
                $source.Dispose()
                $source = $null
                $destination.Dispose()
                $destination = $null

                Sync-GuiSmokeLogLedger -State $script:GuiSmokeAppLogLedger -Paths @($ledgerPath)
                Set-GuiSmokeLogLedgerWriterEvidenceBookmark -State $script:GuiSmokeAppLogLedger -Bookmark 0
                $reader = [IO.StreamReader]::new($ledgerPath, [Text.UTF8Encoding]::new($false, $true))
                try {
                    $firstFields = ConvertFrom-GuiSmokeLogLedgerStructuredFields -Line ([string]$reader.ReadLine())
                }
                finally {
                    $reader.Dispose()
                }
                $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID = [string]$firstFields['acceptance_run_id']
                $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID = [string]$firstFields['scenario_run_id']
                Remove-Item Env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH -ErrorAction SilentlyContinue

                $markerPath = Join-Path $outputRoot 'benchmark.stdout.log'
                [IO.File]::WriteAllText($markerPath, '', [Text.UTF8Encoding]::new($false))
                $script:GuiSmokeProcessOutputFiles = [Collections.Generic.List[string]]::new()
                $script:GuiSmokeProcessOutputFiles.Add($markerPath)
                $baselineSnapshot = Get-GuiSmokeLogLedgerSnapshot `
                    -State $script:GuiSmokeAppLogLedger `
                    -Bookmark 0
                $initialSnapshot = [pscustomobject]@{
                    ledgerId = [string]$baselineSnapshot.ledgerId
                    bookmark = 0
                    lineCount = 0
                    activeGenerationKey = [string]$baselineSnapshot.activeGenerationKey
                    generations = @($baselineSnapshot.generations)
                    writers = @()
                }
                $metrics = [pscustomobject]@{
                    ledgerLineVisitCount = 0L
                    markerLineVisitCount = 0L
                    deferredLookupCount = 0L
                    recoveredLookupCount = 0L
                }

                $stopwatch = [Diagnostics.Stopwatch]::StartNew()
                try {
                    Write-FileTransferGuiLogLedgerEvidence `
                        -ArtifactDir $artifactDir `
                        -Bookmark 0 `
                        -InitialSnapshot $initialSnapshot `
                        -TestMetrics $metrics
                }
                finally {
                    $stopwatch.Stop()
                }
                $ledger = Get-Content -LiteralPath (Join-Path $artifactDir 'filetransfer-gui-log-ledger.json') -Raw | ConvertFrom-Json
                $expectedLines = Get-GuiSmokeLogLedgerBookmark -State $script:GuiSmokeAppLogLedger
                $remainingTemps = @(Get-ChildItem -LiteralPath $artifactDir -File -Filter '*.tmp' -ErrorAction SilentlyContinue)
                if ([long]$expectedLines -lt 1700000 -or
                    $stopwatch.ElapsedMilliseconds -ge 90000 -or
                    [long]$metrics.ledgerLineVisitCount -ne [long]$expectedLines -or
                    [long]$ledger.retainedSlice.lineCount -ne [long]$expectedLines -or
                    [long]$ledger.writerEvidenceSlice.lineCount -le 0 -or
                    [long]$ledger.writerEvidenceSlice.lineCount -ge [long]$expectedLines -or
                    [long]$ledger.semanticEvidenceSlice.lineCount -le 0 -or
                    [long]$ledger.semanticEvidenceSlice.lineCount -ge [long]$expectedLines -or
                    [string]::IsNullOrWhiteSpace([string]$ledger.semanticEvidenceSlice.sha256) -or
                    $remainingTemps.Count -ne 0) {
                    throw "frozen artifact writer benchmark failed: elapsed_ms=$($stopwatch.ElapsedMilliseconds); expected_lines=$expectedLines; metrics=$($metrics | ConvertTo-Json -Compress); ledger=$($ledger | ConvertTo-Json -Compress -Depth 3)"
                }
                Write-Output "gui-log-ledger-frozen-benchmark-ok elapsed_ms=$($stopwatch.ElapsedMilliseconds) lines=$expectedLines"
            }
            finally {
                if ($source) { $source.Dispose() }
                if ($destination) { $destination.Dispose() }
                Remove-Item Env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID -ErrorAction SilentlyContinue
                Remove-Item Env:NLINK_FILETRANSFER_SCENARIO_RUN_ID -ErrorAction SilentlyContinue
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(result.ExitCode == 0, $"Frozen artifact writer benchmark failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-log-ledger-frozen-benchmark-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeLogLedger_ProcessOutputDrainWaitsForDelayedStderrCallback()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var drainBody = ExtractPowerShellFunctionBody(
            guiText,
            "Wait-GuiSmokeProcessOutputCaptureDrain",
            "Copy-AppLogsIfPresent");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Wait-GuiSmokeProcessOutputCaptureDrain { {{drainBody}} }
            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-ledger-drain-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $stderrPath = Join-Path $tempRoot 'delayed.stderr.log'
                $childScriptPath = Join-Path $tempRoot 'emit-delayed-stderr.ps1'
                [IO.File]::WriteAllText($stderrPath, '', [Text.UTF8Encoding]::new($false))
                [IO.File]::WriteAllText($childScriptPath, "Start-Sleep -Milliseconds 150; [Console]::Error.WriteLine('event=operational_log_write_failed; log_writer_id=writer-delayed; log_writer_sequence=9; failure_code=io_error')", [Text.UTF8Encoding]::new($false))
                $script:GuiSmokeProcessOutputFiles = [Collections.Generic.List[string]]::new()
                $script:GuiSmokeProcessOutputFiles.Add($stderrPath)

                $startInfo = [Diagnostics.ProcessStartInfo]::new()
                $startInfo.FileName = (Get-Process -Id $PID).Path
                $startInfo.UseShellExecute = $false
                $startInfo.RedirectStandardError = $true
                $startInfo.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $childScriptPath + '"'
                $process = [Diagnostics.Process]::Start($startInfo)
                Register-ObjectEvent -InputObject $process -EventName ErrorDataReceived -MessageData $stderrPath -Action {
                    if ($null -ne $EventArgs.Data) {
                        [IO.File]::AppendAllText([string]$Event.MessageData, $EventArgs.Data + [Environment]::NewLine, [Text.Encoding]::UTF8)
                    }
                } | Out-Null
                $process.BeginErrorReadLine()

                Wait-GuiSmokeProcessOutputCaptureDrain -Processes @($process) -TimeoutMs 5000 -QuietPeriodMs 250
                if ([IO.File]::ReadAllText($stderrPath) -notlike '*writer-delayed*') {
                    throw 'delayed stderr callback was not drained before snapshot boundary'
                }
                Write-Output 'gui-log-ledger-drain-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(result.ExitCode == 0, $"Writer drain test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-log-ledger-drain-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeWindows_LogicalOperatorsAreNotParsedAsCommandParameters()
    {
        var guiPath = Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1")
            .Replace("'", "''", StringComparison.Ordinal);
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile('{{guiPath}}', [ref]$tokens, [ref]$errors)
            if (@($errors).Count -gt 0) {
                throw "Gui smoke parse errors: $(@($errors | ForEach-Object { $_.Message }) -join '; ')"
            }

            $invalidLogicalParameters = @($ast.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.CommandParameterAst] -and
                    $node.ParameterName -in @('and', 'or', 'xor')
            }, $true))
            if ($invalidLogicalParameters.Count -gt 0) {
                $locations = @($invalidLogicalParameters | ForEach-Object { "line=$($_.Extent.StartLineNumber); parameter=$($_.ParameterName); text=$($_.Parent.Extent.Text)" })
                throw "PowerShell logical operator parsed as a command parameter: $($locations -join ' | ')"
            }

            Write-Output 'gui-smoke-logical-operator-ast-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(result.ExitCode == 0, $"Gui smoke AST validation failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-smoke-logical-operator-ast-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeLogLedger_MissingInitialFailsAndAnalyzerMergeCannotMutateLedgerSlice()
    {
        var helperPath = Path.Combine(FindRepoRoot(), "tools", "GuiSmokeLogLedger.ps1")
            .Replace("'", "''", StringComparison.Ordinal);
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            . '{{helperPath}}'

            $generation = [pscustomobject]@{
                generationKey = 'generation-1'
                creationTimeUtcTicks = 1L
                firstLineHash = 'hash'
                lastPath = 'nlink.log'
                isActivePath = $true
                consumedByteOffset = 8L
                consumedLineCount = 1L
            }
            $final = [pscustomobject]@{
                ledgerId = 'ledger-1'
                activeGenerationKey = 'generation-1'
                generations = @($generation)
            }
            $missingInitialReasons = @(Get-GuiSmokeLogLedgerSnapshotIntegrityReasons -InitialSnapshot $null -FinalSnapshot $final)
            if ($missingInitialReasons -notcontains 'initial_snapshot_missing') {
                throw "missing initial snapshot was accepted: $($missingInitialReasons -join ',')"
            }
            $emptyInitial = [pscustomobject]@{
                ledgerId = 'ledger-1'
                activeGenerationKey = ''
                generations = @()
            }
            $emptyInitialReasons = @(Get-GuiSmokeLogLedgerSnapshotIntegrityReasons -InitialSnapshot $emptyInitial -FinalSnapshot $final)
            if ($emptyInitialReasons -notcontains 'initial_generation_missing' -or
                $emptyInitialReasons -notcontains 'initial_generation_snapshot_missing') {
                throw "missing initial generation was accepted: $($emptyInitialReasons -join ',')"
            }

            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-ledger-slice-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $utf8 = [Text.UTF8Encoding]::new($false)
                $ledgerSlice = Join-Path $tempRoot 'filetransfer-gui-log-ledger-slice.log'
                $analyzerInput = Join-Path $tempRoot 'filetransfer-retained-log-slice.log'
                [IO.File]::WriteAllText($ledgerSlice, 'event=runtime' + [Environment]::NewLine, $utf8)
                [IO.File]::WriteAllText($analyzerInput, 'event=runtime' + [Environment]::NewLine, $utf8)
                $sha = [Security.Cryptography.SHA256]::Create()
                try {
                    $beforeHash = [BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($ledgerSlice)))
                }
                finally {
                    $sha.Dispose()
                }
                $beforeCount = @(Get-Content -LiteralPath $ledgerSlice).Count

                [IO.File]::AppendAllText($analyzerInput, 'event=analyzer_only_milestone' + [Environment]::NewLine, $utf8)

                $sha = [Security.Cryptography.SHA256]::Create()
                try {
                    $afterHash = [BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($ledgerSlice)))
                }
                finally {
                    $sha.Dispose()
                }
                $afterCount = @(Get-Content -LiteralPath $ledgerSlice).Count
                $analyzerCount = @(Get-Content -LiteralPath $analyzerInput).Count
                if ($beforeHash -ne $afterHash -or $beforeCount -ne $afterCount) {
                    throw 'milestone merge mutated immutable ledger slice'
                }
                if ($analyzerCount -le $afterCount) {
                    throw 'analyzer input did not diverge independently from ledger slice'
                }
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }

            Write-Output 'gui-log-ledger-immutable-slice-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(result.ExitCode == 0, $"Ledger immutable-slice test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-log-ledger-immutable-slice-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeLiveRoute_TwoDirectionAuthorityUsesOneFaultAdmissionCursor()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var trackerBody = ExtractPowerShellFunctionBody(guiText, "Add-TunaGuiCompletedTerminalToTracker", "New-TunaGuiLiveSwitchOffProgressGateResult");
        var admissionTrackerBody = ExtractPowerShellFunctionBody(guiText, "New-TunaGuiFaultAdmissionTracker", "Update-TunaGuiFaultAdmissionTracker");
        var resetAdmissionBody = ExtractPowerShellFunctionBody(guiText, "Reset-TunaGuiFaultAdmissionActionWindow", "Get-TunaGuiFaultAdmissionScopeKey");
        var projectionBoundaryBody = ExtractPowerShellFunctionBody(guiText, "Set-TunaGuiFaultAdmissionProjectionBoundary", "Get-TunaGuiFaultAdmissionScopeKey");
        var scopeKeyBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiFaultAdmissionScopeKey", "Get-TunaGuiFaultAdmissionTargetKey");
        var targetKeyBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiFaultAdmissionTargetKey", "Update-TunaGuiFaultAdmissionAuthority");
        var projectionKeyBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiFaultAdmissionProjectionKey", "Update-TunaGuiFaultAdmissionAuthority");
        var authorityBody = ExtractPowerShellFunctionBody(guiText, "Update-TunaGuiFaultAdmissionAuthority", "Update-TunaGuiFaultAdmissionTracker");
        var updateBody = ExtractPowerShellFunctionBody(guiText, "Update-TunaGuiFaultAdmissionTracker", "Test-TunaGuiSwitchOffToggleReady");
        var waitBody = ExtractPowerShellFunctionBody(guiText, "Wait-TunaGuiAuthoritativeTunaFaultAdmission", "Test-TunaGuiSwitchOffToggleReady");
        var fallbackWaitBody = ExtractPowerShellFunctionBody(guiText, "Wait-TunaGuiAuthoritativeFallbackFaultObservation", "Test-TunaGuiSwitchOffToggleReady");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Add-TunaGuiCompletedTerminalToTracker { {{trackerBody}} }
            function New-TunaGuiFaultAdmissionTracker { {{admissionTrackerBody}} }
            function Reset-TunaGuiFaultAdmissionActionWindow { {{resetAdmissionBody}} }
            function Set-TunaGuiFaultAdmissionProjectionBoundary { {{projectionBoundaryBody}} }
            function Get-TunaGuiFaultAdmissionScopeKey { {{scopeKeyBody}} }
            function Get-TunaGuiFaultAdmissionTargetKey { {{targetKeyBody}} }
            function Get-TunaGuiFaultAdmissionProjectionKey { {{projectionKeyBody}} }
            function Update-TunaGuiFaultAdmissionAuthority { {{authorityBody}} }
            function Update-TunaGuiFaultAdmissionTracker { {{updateBody}} }
            function Wait-TunaGuiAuthoritativeTunaFaultAdmission { {{waitBody}} }
            function Wait-TunaGuiAuthoritativeFallbackFaultObservation { {{fallbackWaitBody}} }
            function Assert-GuiSmokeScenarioDeadline {}
            function Start-Sleep { param([int]$Milliseconds) }
            function ConvertFrom-GuiSmokeSemicolonFields {
                param([string]$Message)
                $script:parseCount++
                $fields = @{}
                foreach ($part in $Message.Split(';')) {
                    $pair = $part.Trim().Split('=', 2)
                    if ($pair.Count -eq 2) { $fields[$pair[0].Trim()] = $pair[1].Trim() }
                }
                return $fields
            }
            function Get-GuiSmokeFieldValue {
                param($Fields, [string]$Name, [string]$Default = '')
                if ($Fields.ContainsKey($Name)) { return [string]$Fields[$Name] }
                return $Default
            }
            function ConvertTo-GuiSmokeInt { param($Value, [int]$Default = 0); $parsed = 0; if ([int]::TryParse([string]$Value, [ref]$parsed)) { return $parsed }; return $Default }
            function Get-TunaGuiCompletedTerminalFromLogLine { return $null }
            function Sync-GuiSmokeAppLogLedger {}
            function Get-GuiSmokeLogLedgerBookmark { return $script:lines.Count }
            function Invoke-GuiSmokeLogLedgerRangeVisitor {
                param($State, [int]$StartIndex, [int]$EndIndex, [scriptblock]$Visitor)
                for ($i = $StartIndex; $i -lt $EndIndex; $i++) { [void](& $Visitor ([string]$script:lines[$i]) $i) }
            }

            $script:GuiSmokeAppLogLedger = [pscustomobject]@{}
            $script:lines = [Collections.Generic.List[string]]::new()
            $script:parseCount = 0
            foreach ($direction in @('outbound', 'inbound')) {
                $scope = "session_id=s; transfer_id=t; direction=$direction"
                $authority = "authority-$direction"
                $dataSessionIdentity = if ($direction -eq 'inbound') { 'data-session-inbound:1' } else { '(none)' }
                $script:lines.Add("event=filetransfer_route_transition_committed; $scope; authority_id=$authority; transition_generation=1; target_route=file_tuna_v4; target_protocol_version=4; target_live_route_epoch=1; target_runtime_generation=2; target_terminal_generation=0; target_leg_generation=2; target_route_publication_generation=2; target_route_commit_generation=1; source_transport_epoch=0; source_bridge_recovery_generation=0; checkpoint_request_id=(none); source_data_session_generation=1; data_session_identity=$dataSessionIdentity; decision_status=Committed")
                $script:lines.Add("event=filetransfer_route_hint_authorized_publication_accepted; $scope; route=file_tuna_v4; protocol_version=4; runtime_generation=2; terminal_generation=0; leg_generation=2; route_publication_generation=2; route_commit_generation=1; live_route_epoch=1")
                $script:lines.Add("event=filetransfer_route_transition_disposition_applied; $scope; authority_id=$authority; transition_generation=1; disposition=committed")
                $script:lines.Add("event=filetransfer_data_path_receive_result; $scope; route=file_tuna_v4; protocol_version=4; live_route_epoch=1; runtime_generation=2; terminal_generation=0; leg_generation=2; route_publication_generation=2; route_commit_generation=1")
            }
            $tracker = New-TunaGuiFaultAdmissionTracker -Bookmark 0
            [void](Update-TunaGuiFaultAdmissionTracker -Tracker $tracker)
            if ($null -eq $tracker.AuthoritativeTunaProjection) {
                throw "initial Tuna projection missing commits=$($tracker.TransitionCommits.Count) publications=$($tracker.TransitionPublications.Count) dispositions=$($tracker.TransitionDispositions.Count) payloads=$($tracker.TransitionPayloads.Count) directions=$($tracker.AuthoritativeTunaDirections.Count)"
            }
            $projection = Wait-TunaGuiAuthoritativeTunaFaultAdmission -Tracker $tracker -AfterBookmark 0 -TimeoutMs 100
            if ($projection -notlike '*authoritative_direction_count=2*' -or $tracker.LineVisitCount -ne 8 -or $tracker.Cursor -ne 8) {
                throw "two-direction projection mismatch: $projection visits=$($tracker.LineVisitCount) cursor=$($tracker.Cursor)"
            }
            for ($i = 0; $i -lt 100; $i++) {
                $script:lines.Add("event=filetransfer_chunk_batch_sent_as_batch; sequence=$i; route=file_tuna_v4")
            }
            $script:lines.Add('event=tuna_acceleration_timeline; status=header_switch_off')
            foreach ($direction in @('outbound', 'inbound')) {
                $scope = "session_id=s; transfer_id=t; direction=$direction"
                $authority = "fallback-authority-$direction"
                $script:lines.Add("event=filetransfer_route_transition_committed; $scope; authority_id=$authority; transition_generation=2; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_live_route_epoch=2; target_runtime_generation=3; target_terminal_generation=0; target_leg_generation=3; target_route_publication_generation=3; target_route_commit_generation=0; source_transport_epoch=0; source_bridge_recovery_generation=0; checkpoint_request_id=(none); source_data_session_generation=1; data_session_identity=(none); decision_status=Committed")
                $script:lines.Add("event=filetransfer_route_hint_authorized_publication_accepted; $scope; route=post_tuna_fallback_v6; protocol_version=6; runtime_generation=3; terminal_generation=0; leg_generation=3; route_publication_generation=3; route_commit_generation=0; live_route_epoch=2")
                $script:lines.Add("event=filetransfer_route_transition_disposition_applied; $scope; authority_id=$authority; transition_generation=2; target_route=post_tuna_fallback_v6; disposition=committed")
                $script:lines.Add("event=filetransfer_data_path_receive_result; $scope; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; route_publication_generation=3; route_commit_generation=0")
            }
            [void](Update-TunaGuiFaultAdmissionTracker -Tracker $tracker)
            $fallbackVisits = $tracker.LineVisitCount
            $fallback = Wait-TunaGuiAuthoritativeFallbackFaultObservation -Tracker $tracker -AfterBookmark 8 -TimeoutMs 100
            if ($fallback.StartedLine -notlike '*route=post_tuna_fallback_v6*' -or
                $fallback.RecoveredLine -notlike '*authoritative_direction_count=2*' -or
                $tracker.LineVisitCount -ne 226 -or $fallbackVisits -ne 117 -or $script:parseCount -ne 26) {
                throw "cached fallback projection mismatch: $($fallback.RecoveredLine) visits=$($tracker.LineVisitCount)"
            }
            [void](Update-TunaGuiFaultAdmissionTracker -Tracker $tracker)
            if ($tracker.LineVisitCount -ne 226 -or $script:parseCount -ne 26) { throw 'settled authority tracker rescanned or reparsed lines' }

            $step3Bookmark = $script:lines.Count
            foreach ($direction in @('inbound', 'outbound')) {
                $scope = "session_id=s; transfer_id=t; direction=$direction"
                $authority = "reactivation-authority-$direction"
                $script:lines.Add("event=filetransfer_route_transition_committed; $scope; authority_id=$authority; transition_generation=3; target_route=file_tuna_v4; target_protocol_version=4; target_live_route_epoch=3; target_runtime_generation=4; target_terminal_generation=0; target_leg_generation=4; target_route_publication_generation=4; target_route_commit_generation=2; source_transport_epoch=1; source_bridge_recovery_generation=1; checkpoint_request_id=(none); source_data_session_generation=1; data_session_identity=(none); decision_status=Committed")
                $script:lines.Add("event=filetransfer_route_hint_authorized_publication_accepted; $scope; route=file_tuna_v4; protocol_version=4; live_route_epoch=3; runtime_generation=4; terminal_generation=0; leg_generation=4; route_publication_generation=4; route_commit_generation=2")
                $script:lines.Add("event=filetransfer_route_transition_disposition_applied; $scope; authority_id=$authority; transition_generation=3; target_route=file_tuna_v4; disposition=committed")
                $script:lines.Add("event=filetransfer_data_path_receive_result; $scope; route=file_tuna_v4; protocol_version=4; live_route_epoch=3; runtime_generation=4; terminal_generation=0; leg_generation=4; route_publication_generation=4; route_commit_generation=2")
            }
            [void](Update-TunaGuiFaultAdmissionTracker -Tracker $tracker)
            if ($null -ne $tracker.AuthoritativeTunaProjection) { throw 'epoch-3 projection unexpectedly survived skipped payload indexing before reset' }
            if (-not (Reset-TunaGuiFaultAdmissionActionWindow -Tracker $tracker -ActionBookmark $step3Bookmark)) { throw 'step-3 action window reset failed' }
            if ($tracker.Cursor -ne $step3Bookmark -or $tracker.LastSynchronizedEndBookmark -ne $step3Bookmark) { throw 'step-3 action window did not rewind to its bookmark' }
            $reactivation = Wait-TunaGuiAuthoritativeTunaFaultAdmission -Tracker $tracker -AfterBookmark $step3Bookmark -TimeoutMs 100
            if ($reactivation -notlike '*live_route_epoch=3*' -or
                $reactivation -notlike '*action_window_generation=1*' -or
                $reactivation -notlike "*action_window_bookmark=$step3Bookmark*") {
                throw "replayed epoch-3 projection mismatch: $reactivation"
            }

            $step4Bookmark = $script:lines.Count
            $script:lines.Add('event=tuna_acceleration_timeline; status=header_switch_off')
            foreach ($direction in @('outbound', 'inbound')) {
                $scope = "session_id=s; transfer_id=t; direction=$direction"
                $authority = "final-fallback-authority-$direction"
                $script:lines.Add("event=filetransfer_route_transition_committed; $scope; authority_id=$authority; transition_generation=4; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_live_route_epoch=4; target_runtime_generation=5; target_terminal_generation=0; target_leg_generation=5; target_route_publication_generation=5; target_route_commit_generation=0; source_transport_epoch=1; source_bridge_recovery_generation=1; checkpoint_request_id=(none); source_data_session_generation=1; data_session_identity=(none); decision_status=Committed")
                $script:lines.Add("event=filetransfer_route_hint_authorized_publication_accepted; $scope; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; runtime_generation=5; terminal_generation=0; leg_generation=5; route_publication_generation=5; route_commit_generation=0")
                $script:lines.Add("event=filetransfer_route_transition_disposition_applied; $scope; authority_id=$authority; transition_generation=4; target_route=post_tuna_fallback_v6; disposition=committed")
                $script:lines.Add("event=filetransfer_data_path_receive_result; $scope; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; runtime_generation=5; terminal_generation=0; leg_generation=5; route_publication_generation=5; route_commit_generation=0")
            }
            $finalFallback = Wait-TunaGuiAuthoritativeFallbackFaultObservation -Tracker $tracker -AfterBookmark $step4Bookmark -TimeoutMs 100
            if ($finalFallback.LiveRouteEpoch -ne 4 -or
                $finalFallback.RecoveredLine -notlike '*action_window_generation=1*' -or
                $finalFallback.RecoveredLine -notlike "*action_window_bookmark=$step4Bookmark*") {
                throw "final epoch-4 fallback projection mismatch: $($finalFallback.RecoveredLine)"
            }

            $originalInboundPublication = $script:lines[5]
            $script:lines[5] = $originalInboundPublication.Replace('runtime_generation=2', 'runtime_generation=99')
            $mutated = New-TunaGuiFaultAdmissionTracker -Bookmark 0
            [void](Update-TunaGuiFaultAdmissionTracker -Tracker $mutated -EndBookmark 8 -SkipSynchronization)
            if ($null -ne $mutated.AuthoritativeTunaProjection) { throw 'mutated cross-direction target tuple admitted fault action' }
            $script:lines[5] = $originalInboundPublication

            $script:lines.RemoveAt(6)
            $incomplete = New-TunaGuiFaultAdmissionTracker -Bookmark 0
            [void](Update-TunaGuiFaultAdmissionTracker -Tracker $incomplete -EndBookmark 7 -SkipSynchronization)
            if ($null -ne $incomplete.AuthoritativeTunaProjection) { throw 'missing disposition admitted fault action' }
            Write-Output 'gui-live-route-two-direction-admission-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("gui-live-route-two-direction-admission-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeSwitchOffWait_IsTerminalAwareAndNeverClicksAfterTerminal()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var trackerBody = ExtractPowerShellFunctionBody(guiText, "Add-TunaGuiCompletedTerminalToTracker", "New-TunaGuiLiveSwitchOffProgressGateResult");
        var pairBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiCompletedTerminalPairAfterBookmark", "New-TunaGuiFaultAdmissionTracker");
        var admissionTrackerBody = ExtractPowerShellFunctionBody(guiText, "New-TunaGuiFaultAdmissionTracker", "Update-TunaGuiFaultAdmissionTracker");
        var resetAdmissionBody = ExtractPowerShellFunctionBody(guiText, "Reset-TunaGuiFaultAdmissionActionWindow", "Get-TunaGuiFaultAdmissionScopeKey");
        var scopeKeyBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiFaultAdmissionScopeKey", "Get-TunaGuiFaultAdmissionTargetKey");
        var targetKeyBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiFaultAdmissionTargetKey", "Update-TunaGuiFaultAdmissionAuthority");
        var authorityBody = ExtractPowerShellFunctionBody(guiText, "Update-TunaGuiFaultAdmissionAuthority", "Update-TunaGuiFaultAdmissionTracker");
        var updateTrackerBody = ExtractPowerShellFunctionBody(guiText, "Update-TunaGuiFaultAdmissionTracker", "Test-TunaGuiSwitchOffToggleReady");
        var toggleBody = ExtractPowerShellFunctionBody(guiText, "Test-TunaGuiSwitchOffToggleReady", "Wait-TunaGuiSwitchOffToggleOrTerminal");
        var waitBody = ExtractPowerShellFunctionBody(guiText, "Wait-TunaGuiSwitchOffToggleOrTerminal", "Try-PrearmTunaGuiSwitchOffToggle");
        var prearmBody = ExtractPowerShellFunctionBody(guiText, "Try-PrearmTunaGuiSwitchOffToggle", "Get-TunaGuiLogLineUtcText");
        var invokeBody = ExtractPowerShellFunctionBody(guiText, "Invoke-TunaGuiFallbackFault", "Add-TunaGuiLiveRouteEpochObservation");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'

            function Add-TunaGuiCompletedTerminalToTracker { {{trackerBody}} }
            function Get-TunaGuiCompletedTerminalPairAfterBookmark { {{pairBody}} }
            function New-TunaGuiFaultAdmissionTracker { {{admissionTrackerBody}} }
            function Reset-TunaGuiFaultAdmissionActionWindow { {{resetAdmissionBody}} }
            function Get-TunaGuiFaultAdmissionScopeKey { {{scopeKeyBody}} }
            function Get-TunaGuiFaultAdmissionTargetKey { {{targetKeyBody}} }
            function Update-TunaGuiFaultAdmissionAuthority { {{authorityBody}} }
            function Update-TunaGuiFaultAdmissionTracker { {{updateTrackerBody}} }
            function Test-TunaGuiSwitchOffToggleReady { {{toggleBody}} }
            function Get-TunaGuiSwitchOffClickPoint { param($Toggle) return [pscustomobject]@{ X = 100; Y = 100 } }
            function Click-TunaGuiSwitchOffControl { param($Toggle, $ClickPoint) $script:clickCount++ }
            function Wait-TunaGuiSwitchOffToggleOrTerminal { {{waitBody}} }
            function Try-PrearmTunaGuiSwitchOffToggle { {{prearmBody}} }
            function Invoke-TunaGuiFallbackFault { {{invokeBody}} }
            function Assert-GuiSmokeScenarioDeadline {}
            function ConvertFrom-GuiSmokeSemicolonFields {
                param([string]$Message)
                $fields = @{}
                foreach ($part in $Message.Split(';')) {
                    $pair = $part.Trim().Split('=', 2)
                    if ($pair.Count -eq 2) { $fields[$pair[0].Trim()] = $pair[1].Trim() }
                }
                return $fields
            }
            function Get-GuiSmokeFieldValue {
                param($Fields, [string]$Name, [string]$Default = '')
                if ($Fields.ContainsKey($Name)) { return [string]$Fields[$Name] }
                return $Default
            }

            function Get-TunaGuiCompletedTerminalFromLogLine {
                param([string]$Line)
                $match = [regex]::Match($Line, 'direction=(?<direction>inbound|outbound);\s*transfer_id=(?<transfer>[^;]+)')
                if (-not $match.Success) { return $null }
                return [pscustomobject]@{ Direction = $match.Groups['direction'].Value; TransferId = $match.Groups['transfer'].Value; Line = $Line }
            }
            function Sync-GuiSmokeAppLogLedger {
                $script:logPoll++
                $script:currentLines = if ($script:logPoll -ge $script:terminalAt) { @($script:terminalLines) } else { @() }
            }
            function Get-GuiSmokeLogLedgerBookmark { param($State) return $script:currentLines.Count }
            function Invoke-GuiSmokeLogLedgerRangeVisitor {
                param($State, [int]$StartIndex, [int]$EndIndex, [scriptblock]$Visitor)
                for ($i = $StartIndex; $i -lt $EndIndex; $i++) {
                    $value = & $Visitor ([string]$script:currentLines[$i]) $i
                    if ($null -ne $value) { return $value }
                }
            }
            function Find-VisibleByAutomationId {
                param($Root, [string]$AutomationId)
                $script:findPoll++
                if ($script:findPoll -ge $script:toggleAt) { return $script:testToggle }
                return $null
            }
            function Start-Sleep { param([int]$Milliseconds) }
            function Invoke-WaitCase {
                param([int]$ToggleAt, [int]$TerminalAt, [string[]]$TerminalLines)
                $script:findPoll = 0
                $script:logPoll = 0
                $script:toggleAt = $ToggleAt
                $script:terminalAt = $TerminalAt
                $script:terminalLines = @($TerminalLines)
                $script:currentLines = @()
                $script:GuiSmokeAppLogLedger = [pscustomobject]@{}
                return Wait-TunaGuiSwitchOffToggleOrTerminal -Window ([pscustomobject]@{}) -Bookmark 0 -TimeoutMs 100 -PollIntervalMs 1
            }

            $script:testToggle = [pscustomobject]@{ Current = [pscustomobject]@{ IsEnabled = $true; IsOffscreen = $false; ClickX = 100; ClickY = 100 } }
            $both = @('event=terminal; direction=inbound; transfer_id=tx-1', 'event=terminal; direction=outbound; transfer_id=tx-1')
            $inboundOnly = @('event=terminal; direction=inbound; transfer_id=tx-1')

            $immediate = Invoke-WaitCase -ToggleAt 1 -TerminalAt 99 -TerminalLines @()
            if ($immediate.Kind -ne 'toggle') { throw 'immediate toggle was not selected' }
            $toggleBeforeTerminal = Invoke-WaitCase -ToggleAt 2 -TerminalAt 3 -TerminalLines $both
            if ($toggleBeforeTerminal.Kind -ne 'toggle') { throw 'toggle available before terminal was not selected' }
            $terminalFirst = Invoke-WaitCase -ToggleAt 2 -TerminalAt 1 -TerminalLines $both
            if ($terminalFirst.Kind -ne 'terminal' -or $terminalFirst.TransferId -ne 'tx-1') { throw 'terminal pair did not win' }
            $singleDirection = Invoke-WaitCase -ToggleAt 2 -TerminalAt 1 -TerminalLines $inboundOnly
            if ($singleDirection.Kind -ne 'toggle') { throw 'single terminal direction stopped the fault window' }

            function Get-TunaGuiFaultTarget { param([string]$PayerMode) return 'helpee' }
            function Get-AppLogBookmark { return 0 }
            function Click-Element { param($Element) throw 'cached switch-off path unexpectedly used stale AutomationElement click' }
            $context = [pscustomobject]@{ HelperWindow = $null; HelpeeWindow = [pscustomobject]@{}; HelperProc = $null; HelpeeProc = [pscustomobject]@{} }
            $prearmTracker = New-TunaGuiFaultAdmissionTracker -Bookmark 0
            $script:toggleAt = 1
            $script:findPoll = 0
            $prearm = Try-PrearmTunaGuiSwitchOffToggle -Context $context -PayerMode 'helpee' -Bookmark 0 -AdmissionTracker $prearmTracker
            if ($prearm.Kind -ne 'toggle' -or $prearmTracker.AdmissionState -ne 'Unclaimed') { throw 'prearm claimed the fault winner' }

            $script:clickCount = 0
            function Wait-TunaGuiSwitchOffToggleOrTerminal {
                return [pscustomobject]@{ Kind = 'terminal'; Toggle = $null; TransferId = 'tx-before-click'; WaitMs = 1L; Prearmed = $false; ToggleReadyUtc = '(none)'; LineVisitCount = 1L; EndBookmark = 0 }
            }
            $terminalTracker = New-TunaGuiFaultAdmissionTracker -Bookmark 0
            $noClick = Invoke-TunaGuiFallbackFault -Context $context -FaultMode 'switch-off' -PayerMode 'helpee' -Bookmark 0 -AdmissionTracker $terminalTracker
            if ($noClick.Injected -or -not $noClick.TerminalCompletedBeforeFault -or $script:clickCount -ne 0) { throw 'terminal-first fault attempted a click' }

            function Wait-TunaGuiSwitchOffToggleOrTerminal {
                return [pscustomobject]@{ Kind = 'toggle'; Toggle = $script:testToggle; ClickPoint = [pscustomobject]@{ X = 100; Y = 100 }; TransferId = '(none)'; WaitMs = 1L; Prearmed = $true; ToggleReadyUtc = '2026-08-01 09:25:04Z'; LineVisitCount = 1L; EndBookmark = 0; ClickClaimedUtc = '2026-08-01T09:25:04Z' }
            }
            function Wait-TunaGuiSwitchOffRuntimeOutcome {
                return [pscustomobject]@{ Kind = 'switch_off'; Evidence = [pscustomobject]@{ Line = 'event=tuna_acceleration_reset; reason=header_switch_off'; LineIndex = 2 } }
            }
            $clickTracker = New-TunaGuiFaultAdmissionTracker -Bookmark 0
            $readyWinner = Invoke-TunaGuiFallbackFault -Context $context -FaultMode 'switch-off' -PayerMode 'helpee' -Bookmark 0 -PrearmedToggle $script:testToggle -AdmissionTracker $clickTracker
            if (-not $readyWinner.Injected -or $readyWinner.TerminalCompletedBeforeFault -or $script:clickCount -ne 1) { throw 'cached ready control did not win exactly once' }
            $duplicateWinner = Invoke-TunaGuiFallbackFault -Context $context -FaultMode 'switch-off' -PayerMode 'helpee' -Bookmark 0 -PrearmedToggle $script:testToggle -AdmissionTracker $clickTracker
            if (-not $duplicateWinner.Injected -or $script:clickCount -ne 1) { throw 'duplicate invocation clicked again' }
            if (-not (Reset-TunaGuiFaultAdmissionActionWindow -Tracker $clickTracker -ActionBookmark 0)) { throw 'nonterminal next action window was not reset' }
            $nextWinner = Invoke-TunaGuiFallbackFault -Context $context -FaultMode 'switch-off' -PayerMode 'helpee' -Bookmark 0 -PrearmedToggle $script:testToggle -AdmissionTracker $clickTracker
            if (-not $nextWinner.Injected -or $script:clickCount -ne 2) { throw 'next action window did not admit exactly one new click' }

            $completedTracker = New-TunaGuiFaultAdmissionTracker -Bookmark 0
            $completedTracker.FirstTerminal = [pscustomobject]@{ TransferId = 'tx-complete'; Direction = 'inbound'; Line = 'inbound'; LineIndex = 1 }
            $completedTracker.TerminalPair = [pscustomobject]@{ TransferId = 'tx-complete'; Line = 'outbound'; LineIndex = 2 }
            $completedTracker.InboundCompletedByTransfer['tx-complete'] = 'inbound'
            $completedTracker.OutboundCompletedByTransfer['tx-complete'] = 'outbound'
            if (Reset-TunaGuiFaultAdmissionActionWindow -Tracker $completedTracker -ActionBookmark 0) { throw 'terminal action window was reset' }
            if ($completedTracker.TerminalPair.TransferId -ne 'tx-complete' -or
                -not $completedTracker.InboundCompletedByTransfer.ContainsKey('tx-complete') -or
                -not $completedTracker.OutboundCompletedByTransfer.ContainsKey('tx-complete')) {
                throw 'terminal arbitration state was not preserved'
            }

            Write-Output 'gui-switch-off-terminal-aware-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(result.ExitCode == 0, $"Terminal-aware switch-off test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-switch-off-terminal-aware-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeFaultWindowTelemetry_IsPersistedBeforeLaterFailure()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var utcBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiLogLineUtcText", "New-TunaGuiFaultWindowDiagnosticLine");
        var diagnosticBody = ExtractPowerShellFunctionBody(guiText, "New-TunaGuiFaultWindowDiagnosticLine", "Add-TunaGuiFaultWindowEvidence");
        var persistBody = ExtractPowerShellFunctionBody(guiText, "Add-TunaGuiFaultWindowEvidence", "Invoke-TunaGuiFallbackFault");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Get-TunaGuiLogLineUtcText { {{utcBody}} }
            function New-TunaGuiFaultWindowDiagnosticLine { {{diagnosticBody}} }
            function Add-TunaGuiFaultWindowEvidence { {{persistBody}} }
            function Get-GuiSmokeLogLedgerDiagnostics {
                param($State)
                return [pscustomobject]@{ GenerationCount = 3; RotationCount = 2; TransientRescanCount = 1; GapCount = 0 }
            }
            function Write-TunaGuiMilestoneEvidence {
                param([string]$ArtifactDir, $ObservedEvidenceLines)
                [IO.File]::WriteAllText(
                    (Join-Path $ArtifactDir 'filetransfer-tuna-gui-milestone-evidence.log'),
                    (@($ObservedEvidenceLines.ToArray()) -join [Environment]::NewLine))
            }

            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-fault-window-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $script:GuiSmokeAppLogLedger = [pscustomobject]@{}
                $lines = [Collections.Generic.List[string]]::new()
                $fault = [pscustomobject]@{
                    Prearmed = $true
                    ToggleWaitMs = 7L
                    ClickUtc = '2026-07-10T18:04:44.0000000Z'
                    TerminalCompletedBeforeFault = $false
                    Injected = $true
                    ToggleReadyUtc = '2026-07-10T18:04:42.0000000Z'
                    Winner = 'click'
                    LineVisitCount = 17L
                }
                Add-TunaGuiFaultWindowEvidence `
                    -ArtifactDir $tempRoot `
                    -ObservedEvidenceLines $lines `
                    -ActivationLine '[2026-07-10 18:04:40Z] event=filetransfer_route_transitioned' `
                    -ActivationObservedUtc '2026-07-10T18:04:41.0000000Z' `
                    -FaultResult $fault

                $path = Join-Path $tempRoot 'filetransfer-tuna-gui-milestone-evidence.log'
                if (-not (Test-Path -LiteralPath $path)) { throw 'fault telemetry was not persisted immediately' }
                $content = [IO.File]::ReadAllText($path)
                if ($content -notlike '*event=filetransfer_tuna_gui_fault_window*') { throw 'fault event missing' }
                if ($content -notlike '*prearmed=1*' -or $content -notlike '*cursor_transient_rescan_count=1*' -or $content -notlike '*cursor_gap_count=0*' -or $content -notlike '*fault_injected=1*') { throw 'fault tuple incomplete' }

                try { throw 'simulated later runtime failure' } catch { }
                if (-not (Test-Path -LiteralPath $path)) { throw 'later failure removed fault telemetry' }
                Write-Output 'gui-fault-window-persisted-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(result.ExitCode == 0, $"Fault-window persistence test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-fault-window-persisted-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeFailureClassification_ExplicitClassPrecedesHistoricalRepairEvidence()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var atomicJsonBody = ExtractPowerShellFunctionBody(
            guiText,
            "Write-GuiSmokeAtomicJson",
            "New-TunaGuiFileTransferFailureClassificationResult");
        var explicitBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiExplicitFailureClassification", "Resolve-TunaGuiFailureClassification");
        var resolveBody = ExtractPowerShellFunctionBody(guiText, "Resolve-TunaGuiFailureClassification", "Write-TunaGuiTopLevelFailureSummaryIfMissing");
        var topLevelBody = ExtractPowerShellFunctionBody(guiText, "Write-TunaGuiTopLevelFailureSummaryIfMissing", "Get-TunaGuiFileTransferSetupFailureClassification");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Write-GuiSmokeAtomicJson { {{atomicJsonBody}} }
            function Get-TunaGuiExplicitFailureClassification { {{explicitBody}} }
            function Resolve-TunaGuiFailureClassification { {{resolveBody}} }
            function Write-TunaGuiTopLevelFailureSummaryIfMissing { {{topLevelBody}} }

            $resolved = Resolve-TunaGuiFailureClassification `
                -ErrorMessage 'route failed; failure_class=terminal_before_fallback_switch_off; retained_event=filetransfer_regular_v4_frontier_repair_stalled' `
                -HistoricalPhase 'regular_v4_frontier_repair' `
                -HistoricalReason 'regular_v4_frontier_repair_stalled'
            if ($resolved.Phase -ne 'live_route_proof' -or $resolved.Reason -ne 'terminal_before_fallback_switch_off') { throw 'explicit failure class lost precedence' }

            $regularTerminal = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=regular_v4_terminal_before_tuna_activation_commit' -HistoricalPhase 'unknown' -HistoricalReason 'unknown'
            if ($regularTerminal.Phase -ne 'live_route_proof') { throw 'regular V4 terminal class not mapped' }
            $faultWindow = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=filetransfer_gui_live_fallback_fault_window_missed' -HistoricalPhase 'setup' -HistoricalReason 'unknown'
            if ($faultWindow.Phase -ne 'live_route_proof' -or $faultWindow.Reason -ne 'fallback_switch_off_not_injected_before_completed_transfer') { throw 'fault-window class not mapped' }
            $retentionOverrun = Resolve-TunaGuiFailureClassification -ErrorMessage 'gui_smoke_log_cursor_gap: reason=active_generation_missing_after_reconciliation; reason=active_predecessor_not_observed' -HistoricalPhase 'setup' -HistoricalReason 'unknown'
            if ($retentionOverrun.Phase -ne 'evidence_harness' -or $retentionOverrun.Reason -ne 'active_log_generation_rotated_out_before_cursor_reconciliation') { throw 'ledger retention overrun class not mapped' }
            $fallbackPrefix = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=terminal_fallback_prefix_missing' -HistoricalPhase 'regular_v4_frontier_repair' -HistoricalReason 'regular_v4_frontier_repair_stalled'
            if ($fallbackPrefix.Phase -ne 'live_route_proof' -or $fallbackPrefix.Reason -ne 'terminal_fallback_prefix_missing') { throw 'terminal fallback prefix class lost precedence' }
            $publicationMismatch = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=runtime_unlock_retry_source_publication_mismatch; retained_event=filetransfer_regular_v4_frontier_repair_stalled' -HistoricalPhase 'regular_v4_frontier_repair' -HistoricalReason 'regular_v4_frontier_repair_stalled'
            if ($publicationMismatch.Phase -ne 'runtime_unlock_retry' -or $publicationMismatch.Reason -ne 'runtime_unlock_retry_source_publication_mismatch') { throw 'runtime unlock source publication mismatch lost precedence' }
            $tupleMismatch = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=runtime_unlock_retry_dispatch_authority_tuple_mismatch; retained_event=filetransfer_regular_v4_frontier_repair_stalled' -HistoricalPhase 'regular_v4_frontier_repair' -HistoricalReason 'regular_v4_frontier_repair_stalled'
            if ($tupleMismatch.Phase -ne 'runtime_unlock_retry' -or $tupleMismatch.Reason -ne 'runtime_unlock_retry_dispatch_authority_tuple_mismatch') { throw 'runtime unlock authority tuple mismatch lost precedence' }
            $missingSuccessor = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=runtime_unlock_retry_dispatch_successor_missing_after_missing_dispatch; retained_event=filetransfer_regular_v4_frontier_repair_stalled' -HistoricalPhase 'regular_v4_frontier_repair' -HistoricalReason 'regular_v4_frontier_repair_stalled'
            if ($missingSuccessor.Phase -ne 'runtime_unlock_retry' -or $missingSuccessor.Reason -ne 'runtime_unlock_retry_dispatch_successor_missing_after_missing_dispatch') { throw 'runtime unlock missing successor lost precedence' }
            $budgetBypass = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=runtime_unlock_retry_dispatch_grant_bypassed_by_retry_budget; retained_event=filetransfer_regular_v4_frontier_repair_stalled' -HistoricalPhase 'regular_v4_frontier_repair' -HistoricalReason 'regular_v4_frontier_repair_stalled'
            if ($budgetBypass.Phase -ne 'runtime_unlock_retry' -or $budgetBypass.Reason -ne 'runtime_unlock_retry_dispatch_grant_bypassed_by_retry_budget') { throw 'runtime unlock budget bypass lost precedence' }
            $unsettled = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=runtime_unlock_retry_dispatch_contract_unsettled; retained_event=filetransfer_regular_v4_frontier_repair_stalled' -HistoricalPhase 'regular_v4_frontier_repair' -HistoricalReason 'regular_v4_frontier_repair_stalled'
            if ($unsettled.Phase -ne 'runtime_unlock_retry' -or $unsettled.Reason -ne 'runtime_unlock_retry_dispatch_contract_unsettled') { throw 'runtime unlock unsettled contract lost precedence' }
            $postGrantHold = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=runtime_unlock_retry_dispatch_held_after_authority_granted; retained_event=runtime_unlock_retry_dispatch_held_by_active_negotiation' -HistoricalPhase 'progress_failure' -HistoricalReason 'gui_smoke_scenario_deadline_exceeded'
            if ($postGrantHold.Phase -ne 'runtime_unlock_retry' -or $postGrantHold.Reason -ne 'retry_dispatch_held_after_authority_granted') { throw 'runtime unlock post-grant hold lost precedence' }
            $pairedYieldSourceScope = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=paired_fallback_tail_yield_missing; failure_variant=paired_yield_retry_request_source_scope_not_current; retained_event=runtime_unlock_retry_dispatch_authority_request_queue_rejected' -HistoricalPhase 'progress_failure' -HistoricalReason 'gui_smoke_scenario_deadline_exceeded'
            if ($pairedYieldSourceScope.Phase -ne 'route_runtime' -or $pairedYieldSourceScope.Reason -ne 'fallback_paired_tail_yield_retry_request_source_scope_not_current') { throw 'paired-yield retry source-scope rejection lost precedence' }
            $pairedYieldIntentRecheck = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=paired_fallback_tail_yield_missing; failure_variant=paired_yield_retry_intent_not_rechecked_after_runtime_unlock_eligibility_restored; first_reason=fallback_paired_tail_yield_retry_intent_not_rechecked_after_runtime_unlock_eligibility_restored' -HistoricalPhase 'progress_failure' -HistoricalReason 'gui_smoke_scenario_deadline_exceeded'
            if ($pairedYieldIntentRecheck.Phase -ne 'route_runtime' -or $pairedYieldIntentRecheck.Reason -ne 'fallback_paired_tail_yield_retry_intent_not_rechecked_after_runtime_unlock_eligibility_restored') { throw 'paired-yield retry intent recheck lost precedence' }
            $initialResponderDrain = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=filetransfer_route_transition_incomplete; retained_event=filetransfer_route_transition_drain_receipt_decided; first_proof_chain_failure_reason=filetransfer_route_transition_initial_responder_boundary_not_current' -HistoricalPhase 'progress_failure' -HistoricalReason 'gui_smoke_scenario_deadline_exceeded'
            if ($initialResponderDrain.Phase -ne 'route_transition' -or $initialResponderDrain.Reason -ne 'filetransfer_route_transition_initial_responder_boundary_not_current') { throw 'initial responder drain rejection lost precedence' }
            $postFallbackResponderDrain = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=filetransfer_route_transition_incomplete; retained_event=filetransfer_route_transition_drain_receipt_decided; first_proof_chain_failure_reason=filetransfer_route_transition_fallback_release_not_current' -HistoricalPhase 'progress_failure' -HistoricalReason 'gui_smoke_scenario_deadline_exceeded'
            if ($postFallbackResponderDrain.Phase -ne 'route_transition' -or $postFallbackResponderDrain.Reason -ne 'filetransfer_route_transition_fallback_release_not_current') { throw 'post-fallback responder drain rejection lost precedence' }
            $postFallbackResponderPairedDrain = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=filetransfer_route_transition_incomplete; retained_event=filetransfer_route_transition_drain_receipt_decided; first_proof_chain_failure_reason=paired_fallback_tail_yield_missing' -HistoricalPhase 'progress_failure' -HistoricalReason 'gui_smoke_scenario_deadline_exceeded'
            if ($postFallbackResponderPairedDrain.Phase -ne 'route_transition' -or $postFallbackResponderPairedDrain.Reason -ne 'paired_fallback_tail_yield_missing') { throw 'post-fallback paired responder drain rejection lost precedence' }
            $postFallbackProbeTransition = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=runtime_unlock_precommit_probe_start_handoff_missing; first_proof_chain_failure_reason=post_fallback_initiator_probe_launch_accepted_without_route_transition_authority' -HistoricalPhase 'progress_failure' -HistoricalReason 'gui_smoke_scenario_deadline_exceeded'
            if ($postFallbackProbeTransition.Phase -ne 'route_runtime' -or $postFallbackProbeTransition.Reason -ne 'post_fallback_initiator_probe_launch_accepted_without_route_transition_authority') { throw 'post-fallback probe transition authority lost precedence' }
            $postFallbackGenericPause = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=runtime_fallback_separation_violation; first_proof_chain_failure_reason=post_tuna_fallback_regular_nkn_paused_by_stale_retry_receipt_despite_current_pause_suppression' -HistoricalPhase 'progress_failure' -HistoricalReason 'gui_smoke_scenario_deadline_exceeded'
            if ($postFallbackGenericPause.Phase -ne 'route_runtime' -or $postFallbackGenericPause.Reason -ne 'post_tuna_fallback_regular_nkn_paused_by_stale_retry_receipt_despite_current_pause_suppression') { throw 'post-fallback generic pause lost precedence' }
            $initialResponderMissingDrain = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=filetransfer_route_transition_incomplete; retained_event=filetransfer_route_transition_invalidated; first_proof_chain_failure_reason=initial_responder_route_transition_terminal_invalidated_without_exact_drain_receipt' -HistoricalPhase 'progress_failure' -HistoricalReason 'gui_smoke_scenario_deadline_exceeded'
            if ($initialResponderMissingDrain.Phase -ne 'route_transition' -or $initialResponderMissingDrain.Reason -ne 'initial_responder_route_transition_terminal_invalidated_without_exact_drain_receipt') { throw 'initial responder missing drain lost precedence' }
            $initialRecoveryHold = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=runtime_unlock_initial_offer_receive_recovery_hold_not_preserved; retained_event=tuna_acceleration_activation_offer_not_observed' -HistoricalPhase 'setup' -HistoricalReason 'offer_not_observed'
            if ($initialRecoveryHold.Phase -ne 'runtime_unlock_initial_activation' -or $initialRecoveryHold.Reason -ne 'initial_activation_retry_exhausted_while_receive_recovery_awaiting_proof') { throw 'initial activation recovery hold lost precedence' }
            $initialRecoveryHoldRelease = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing; retained_event=runtime_unlock_initial_activation_receive_recovery_hold_invalidation_rejected' -HistoricalPhase 'setup' -HistoricalReason 'offer_not_observed'
            if ($initialRecoveryHoldRelease.Phase -ne 'runtime_unlock_initial_activation' -or $initialRecoveryHoldRelease.Reason -ne 'released_hold_routed_through_exact_invalidation_boundary') { throw 'initial activation recovery hold release delivery lost precedence' }
            $initialRecoveryHoldDirectReleaseEvent = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing; retained_event=runtime_unlock_initial_activation_receive_recovery_hold_directive_delivery_decided' -HistoricalPhase 'setup' -HistoricalReason 'offer_not_observed'
            if ($initialRecoveryHoldDirectReleaseEvent.Phase -ne 'runtime_unlock_initial_activation' -or $initialRecoveryHoldDirectReleaseEvent.Reason -ne 'released_hold_directive_delivery_rejected') { throw 'direct initial activation hold release event lost precedence' }
            $initialRecoveryHoldDirectReleaseReason = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing; first_reason=released_hold_directive_delivery_rejected' -HistoricalPhase 'setup' -HistoricalReason 'offer_not_observed'
            if ($initialRecoveryHoldDirectReleaseReason.Phase -ne 'runtime_unlock_initial_activation' -or $initialRecoveryHoldDirectReleaseReason.Reason -ne 'released_hold_directive_delivery_rejected') { throw 'direct initial activation hold release reason lost precedence' }
            $initialRecoveryHoldRetiredRefresh = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing; retained_event=runtime_unlock_initial_activation_offer_envelope_refresh_rejected; first_reason=released_hold_refresh_predecessor_boundary_not_current' -HistoricalPhase 'setup' -HistoricalReason 'offer_not_observed'
            if ($initialRecoveryHoldRetiredRefresh.Phase -ne 'runtime_unlock_initial_activation' -or $initialRecoveryHoldRetiredRefresh.Reason -ne 'released_hold_refresh_predecessor_boundary_not_current') { throw 'retired initial activation refresh predecessor rejection lost precedence' }
            $initialAnswerPeerDelivery = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=runtime_unlock_initial_activation_answer_peer_delivery_missing; retained_event=tuna_acceleration_answer_ack_timeout' -HistoricalPhase 'setup' -HistoricalReason 'offer_not_observed'
            if ($initialAnswerPeerDelivery.Phase -ne 'runtime_unlock_initial_activation' -or $initialAnswerPeerDelivery.Reason -ne 'validated_offer_answer_not_delivered_to_initiator') { throw 'initial activation answer peer delivery lost precedence' }
            $receivedAnswerAuthorityRejected = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=runtime_unlock_initial_activation_answer_peer_delivery_missing; first_reason=validated_offer_answer_received_but_authority_tuple_rejected' -HistoricalPhase 'progress_failure' -HistoricalReason 'gui_smoke_scenario_deadline_exceeded'
            if ($receivedAnswerAuthorityRejected.Phase -ne 'runtime_unlock_initial_activation' -or $receivedAnswerAuthorityRejected.Reason -ne 'validated_offer_answer_received_but_authority_tuple_rejected') { throw 'received initial activation answer authority rejection lost precedence' }
            $installedRetryAnswerContinuationWake = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=runtime_unlock_initial_activation_answer_peer_delivery_missing; first_reason=initial_activation_wake_started_before_installed_retry_answer_continuation_completed' -HistoricalPhase 'progress_failure' -HistoricalReason 'gui_smoke_scenario_deadline_exceeded'
            if ($installedRetryAnswerContinuationWake.Phase -ne 'runtime_unlock_initial_activation' -or $installedRetryAnswerContinuationWake.Reason -ne 'initial_activation_wake_started_before_installed_retry_answer_continuation_completed') { throw 'installed retry answer continuation wake lost precedence' }
            $answerHandoffTransferIdentity = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=runtime_unlock_initial_activation_answer_peer_delivery_missing; first_reason=accepted_initial_activation_answer_handoff_rejected_on_unresolved_transfer_identity' -HistoricalPhase 'progress_failure' -HistoricalReason 'gui_smoke_scenario_deadline_exceeded'
            if ($answerHandoffTransferIdentity.Phase -ne 'runtime_unlock_initial_activation' -or $answerHandoffTransferIdentity.Reason -ne 'accepted_initial_activation_answer_handoff_rejected_on_unresolved_transfer_identity') { throw 'answer handoff transfer identity failure lost precedence' }
            $installedRetryPendingDial = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=runtime_unlock_initial_activation_answer_peer_delivery_missing; first_reason=installed_retry_offer_pending_duplicate_not_replayed_after_other_dial_failure' -HistoricalPhase 'progress_failure' -HistoricalReason 'gui_smoke_scenario_deadline_exceeded'
            if ($installedRetryPendingDial.Phase -ne 'runtime_unlock_initial_activation' -or $installedRetryPendingDial.Reason -ne 'installed_retry_offer_pending_duplicate_not_replayed_after_other_dial_failure') { throw 'installed retry pending dial failure lost precedence' }
            $initialRecoveryHoldRefreshRelease = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing; retained_event=runtime_unlock_initial_activation_receive_recovery_hold_send_prevalidated' -HistoricalPhase 'setup' -HistoricalReason 'offer_not_observed'
            if ($initialRecoveryHoldRefreshRelease.Phase -ne 'runtime_unlock_initial_activation' -or $initialRecoveryHoldRefreshRelease.Reason -ne 'committed_refresh_successor_envelope_not_accepted_by_release_prevalidation') { throw 'committed refresh release prevalidation lost precedence' }
            $initialRecoveryHoldExpired = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired; retained_event=tuna_acceleration_message_rejected' -HistoricalPhase 'regular_v4_frontier_repair' -HistoricalReason 'regular_v4_frontier_repair_stalled'
            if ($initialRecoveryHoldExpired.Phase -ne 'runtime_unlock_initial_activation' -or $initialRecoveryHoldExpired.Reason -ne 'initial_activation_offer_expired_during_receive_recovery_hold') { throw 'initial activation recovery hold expired offer lost precedence' }
            $initialRecoveryHoldPathLeaseExpired = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired; retained_event=tuna_listener_sidecar_event' -HistoricalPhase 'regular_v4_frontier_repair' -HistoricalReason 'regular_v4_frontier_repair_stalled'
            if ($initialRecoveryHoldPathLeaseExpired.Phase -ne 'runtime_unlock_initial_activation' -or $initialRecoveryHoldPathLeaseExpired.Reason -ne 'initial_activation_path_lease_listener_expired_during_receive_recovery_hold') { throw 'initial activation recovery hold path lease expiry lost precedence' }
            $workerHandoff = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=post_tuna_fallback_outbound_receive_worker_handoff_missing; retained_event=filetransfer_fallback_frontier_repair_stalled' -HistoricalPhase 'fallback_frontier_repair' -HistoricalReason 'fallback_frontier_repair_stalled'
            if ($workerHandoff.Phase -ne 'outbound_sparse_worker_handoff' -or $workerHandoff.Reason -ne 'post_tuna_fallback_outbound_receive_worker_handoff_missing') { throw 'outbound sparse worker handoff lost precedence' }
            $yieldAuthority = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=fallback_tail_yield_authority_invalid; retained_event=filetransfer_fallback_frontier_repair_stalled' -HistoricalPhase 'fallback_frontier_repair' -HistoricalReason 'fallback_frontier_repair_stalled'
            if ($yieldAuthority.Phase -ne 'fallback_tail_yield' -or $yieldAuthority.Reason -ne 'fallback_tail_yield_authority_invalid') { throw 'fallback-tail yield authority rejection lost precedence' }
            $cursor = Resolve-TunaGuiFailureClassification -ErrorMessage 'gui_smoke_log_cursor_truncated: path=x' -HistoricalPhase 'regular_v4_frontier_repair' -HistoricalReason 'regular_v4_frontier_repair_stalled'
            if ($cursor.Phase -ne 'harness_log_cursor' -or $cursor.Reason -ne 'gui_smoke_log_cursor_truncated') { throw 'cursor failure not classified as harness evidence' }
            $historical = Resolve-TunaGuiFailureClassification -ErrorMessage 'unmapped failure' -HistoricalPhase 'fallback_frontier_repair' -HistoricalReason 'fallback_frontier_repair_stalled'
            if ($historical.Phase -ne 'fallback_frontier_repair') { throw 'historical fallback changed without explicit class' }

            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-top-level-error-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $env:NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR = $tempRoot
                $env:NLINK_TUNA_GUI_ROUTE_MODE = 'live-regular-activation-cycle'
                Write-TunaGuiTopLevelFailureSummaryIfMissing -ErrorMessage 'gui_smoke_log_cursor_gap: generation moved'
                $errorPath = Join-Path $tempRoot 'filetransfer-tuna-gui-error.json'
                if (-not (Test-Path -LiteralPath $errorPath)) { throw 'top-level cursor error artifact missing' }
                $errorSummary = Get-Content -LiteralPath $errorPath -Raw | ConvertFrom-Json
                if ($errorSummary.failurePhase -ne 'harness_log_cursor' -or $errorSummary.failureReason -ne 'gui_smoke_log_cursor_gap') { throw 'top-level cursor classification was not preserved' }
                [IO.File]::WriteAllText($errorPath, '{"marker":"richer-existing-error"}')
                Write-TunaGuiTopLevelFailureSummaryIfMissing -ErrorMessage 'gui_smoke_log_cursor_truncated: replacement forbidden'
                if ([IO.File]::ReadAllText($errorPath) -notlike '*richer-existing-error*') { throw 'top-level writer replaced an existing error artifact' }
            }
            finally {
                Remove-Item Env:NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR -ErrorAction SilentlyContinue
                Remove-Item Env:NLINK_TUNA_GUI_ROUTE_MODE -ErrorAction SilentlyContinue
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }

            Write-Output 'gui-failure-classification-precedence-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(result.ExitCode == 0, $"Failure classification test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-failure-classification-precedence-ok", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("Resolve-TunaGuiFailureClassification", guiText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeFailureClassification_RetiredRefreshPredecessorRejectedPrecedesAnswerAndDeadlineFallbacks()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var resultBody = ExtractPowerShellFunctionBody(guiText, "New-TunaGuiFileTransferFailureClassificationResult", "Get-TunaGuiExplicitFailureClassification");
        var explicitBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiExplicitFailureClassification", "Resolve-TunaGuiFailureClassification");
        var classifierBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiFileTransferSetupFailureClassification", "Get-TunaGuiFailureClassificationBudgetMilliseconds");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            Set-StrictMode -Version Latest
            function New-TunaGuiFileTransferFailureClassificationResult { {{resultBody}} }
            function Get-TunaGuiExplicitFailureClassification { {{explicitBody}} }
            function Get-TunaGuiFileTransferSetupFailureClassification { {{classifierBody}} }
            function Get-AppLogLinesAfterBookmark { throw 'live ledger was read' }
            function Read-GuiSmokeProcessOutputLinesSnapshot { throw 'process output was read' }
            function Test-TunaGuiBridgeBootstrapNotReadyEvidence { return $false }
            function Test-TunaGuiLatestFallbackFrontierRequestDispatchMissing { return $false }
            function Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing { return $false }
            function Test-TunaGuiLogLinesContain { return $false }
            function Get-TunaGuiReadinessStateAfterBookmark {
                return [pscustomobject]@{ ListenerUnavailable = $false; ListenerReady = $false; TunaActive = $false }
            }
            function Resolve-TunaGuiFailureClassification {
                param($ErrorMessage, $HistoricalPhase, $HistoricalReason)
                return [pscustomobject]@{ Phase = $HistoricalPhase; Reason = $HistoricalReason }
            }
            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-retired-refresh-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $path = Join-Path $tempRoot 'evidence.log'
                Set-Content -LiteralPath $path -Value @()
                $empty = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($empty.Reason -eq 'released_hold_refresh_predecessor_boundary_not_current') {
                    throw 'empty retired refresh index produced an incident'
                }
                Set-Content -LiteralPath $path -Value 'event=runtime_unlock_initial_activation_offer_envelope_refresh_rejected; session_id=sess; transfer_id=transfer; direction=outbound; boundary=AnswerTimeout; offer_generation=1; refresh_generation=1; decision_reason=predecessor_boundary_not_current; log_writer_id=writer'
                $singleton = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($singleton.Reason -eq 'released_hold_refresh_predecessor_boundary_not_current') {
                    throw 'singleton retired refresh index produced an uncorroborated incident'
                }
                @(
                    'event=runtime_unlock_initial_activation_receive_recovery_hold_decided; session_id=sess; transfer_id=transfer; direction=outbound; hold_boundary=AnswerTimeout; offer_generation=1; hold_generation=1; receive_recovery_operation_generation=1; decision_status=Held; log_writer_id=writer',
                    'event=filetransfer_receive_recovery_operation_closure_adopted; session_id=sess; transfer_id=transfer; direction=outbound; recovery_operation_generation=1; closure_state=Adopted; log_writer_id=writer',
                    'event=runtime_unlock_initial_activation_offer_envelope_refresh_rejected; session_id=sess; transfer_id=transfer; direction=outbound; boundary=AnswerTimeout; offer_generation=1; refresh_generation=1; decision_reason=predecessor_boundary_not_current; log_writer_id=writer',
                    'event=runtime_unlock_initial_activation_receive_recovery_hold_decided; session_id=sess; transfer_id=transfer; direction=outbound; hold_boundary=AnswerTimeout; offer_generation=1; hold_generation=1; receive_recovery_operation_generation=1; refresh_generation=1; decision_status=Released; decision_reason=hold_directive_delivery_rejected; log_writer_id=writer',
                    'event=runtime_unlock_initial_activation_receive_recovery_hold_directive_delivery_decided; session_id=sess; transfer_id=transfer; direction=outbound; hold_boundary=AnswerTimeout; offer_generation=1; hold_generation=1; receive_recovery_operation_generation=1; refresh_generation=1; decision_status=Released; decision_reason=hold_directive_delivery_rejected; log_writer_id=writer',
                    'event=tuna_acceleration_answer_ack_timeout; session_id=sess; transfer_id=transfer; direction=outbound; offer_generation=1; log_writer_id=writer',
                    'event=file_transfer_outbound_terminal; session_id=sess; transfer_id=transfer; direction=outbound; state=Completed; log_writer_id=writer'
                ) | Set-Content -LiteralPath $path
                $result = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'Gui_Smoke_Scenario_Deadline_Exceeded' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($result.Phase -ne 'runtime_unlock_initial_activation' -or
                    $result.Reason -ne 'released_hold_refresh_predecessor_boundary_not_current') {
                    throw "retired refresh predecessor classification mismatch: $($result | ConvertTo-Json -Compress)"
                }
                Write-Output 'gui-retired-refresh-predecessor-fast-path-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(result.ExitCode == 0, $"Retired refresh GUI fast path failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-retired-refresh-predecessor-fast-path-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unresolved")]
    [InlineData("raw_only")]
    [InlineData("recovered")]
    [InlineData("late_hold")]
    [InlineData("deadline")]
    public async Task GuiSmokeFailureClassification_RuntimeUnlockRetryPostGrantHoldUsesIndexedProofBeforeDeadline(
        string mode)
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var resultBody = ExtractPowerShellFunctionBody(
            guiText,
            "New-TunaGuiFileTransferFailureClassificationResult",
            "Get-TunaGuiExplicitFailureClassification");
        var explicitBody = ExtractPowerShellFunctionBody(
            guiText,
            "Get-TunaGuiExplicitFailureClassification",
            "Resolve-TunaGuiFailureClassification");
        var classifierBody = ExtractPowerShellFunctionBody(
            guiText,
            "Get-TunaGuiFileTransferSetupFailureClassification",
            "Get-TunaGuiFailureClassificationBudgetMilliseconds");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function New-TunaGuiFileTransferFailureClassificationResult { {{resultBody}} }
            function Get-TunaGuiExplicitFailureClassification { {{explicitBody}} }
            function Get-TunaGuiFileTransferSetupFailureClassification { {{classifierBody}} }
            function Get-AppLogLinesAfterBookmark { throw 'live ledger was read' }
            function Read-GuiSmokeProcessOutputLinesSnapshot { throw 'process output was read' }
            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-post-grant-hold-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $path = Join-Path $tempRoot 'evidence.log'
                $tuple = 'authority_id=retry-a; authority_generation=1; session_id=sess; transfer_id=transfer; direction=outbound; contract_generation=1; retired_generation=0; dispatch_attempt_generation=2; missing_dispatch_successor_generation=1; install_generation=1; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; route_publication_generation=3; route_commit_generation=0; release_kind=FallbackPairedTailYield; release_authority_id=pair-a; release_generation=1; release_outcome=paired'
                $lines = @("event=runtime_unlock_retry_dispatch_authority_granted; $tuple; gui_evidence_sequence=10; log_writer_id=writer")
                if ('{{mode}}' -eq 'late_hold') {
                    $lines += "event=runtime_unlock_retry_dispatch_contract_settled; $tuple; gui_evidence_sequence=11; settlement_outcome=MissingDispatch; decision_reason=runtime_unlock_retry_dispatch_missing_before_completion; dispatch_observed=0; log_writer_id=writer"
                }
                $lines += "event=runtime_unlock_retry_dispatch_held_by_active_negotiation; $tuple; gui_evidence_sequence=12; decision_reason=runtime_unlock_retry_dispatch_queued_behind_active_negotiation; log_writer_id=writer"
                if ('{{mode}}' -in @('raw_only', 'recovered')) {
                    $lines += "event=runtime_unlock_retry_dispatch_observed; $tuple; gui_evidence_sequence=12; offer_generation=3; offer_nonce=nonce; log_writer_id=writer"
                }
                if ('{{mode}}' -eq 'recovered') {
                    $lines += "event=runtime_unlock_retry_dispatch_contract_settled; $tuple; gui_evidence_sequence=13; offer_generation=3; offer_nonce=nonce; settlement_outcome=DispatchObserved; dispatch_observed=1; decision_reason=peer_visible_offer_send_observed; log_writer_id=writer"
                    $lines += "event=runtime_unlock_retry_dispatch_settlement_reported; $tuple; gui_evidence_sequence=14; offer_generation=3; offer_nonce=nonce; settlement_outcome=DispatchObserved; settlement_reason=peer_visible_offer_send_observed; log_writer_id=writer"
                    $lines += "event=runtime_unlock_retry_dispatch_settlement_decided; $tuple; gui_evidence_sequence=15; offer_generation=3; offer_nonce=nonce; settlement_outcome=DispatchObserved; settlement_reason=peer_visible_offer_send_observed; decision_status=Accepted; log_writer_id=writer"
                }
                if ('{{mode}}' -eq 'deadline') {
                    $lines += "event=session_recovery_contract_listener_rearm_completed; $tuple; gui_evidence_sequence=13; listener_rearm_completed=1; log_writer_id=writer"
                    $lines += "event=session_recovery_contract_retry_dispatched; $tuple; gui_evidence_sequence=14; retry_dispatched=1; log_writer_id=writer"
                    $lines += "event=session_recovery_contract_retry_authority_offer_window_refreshed; $tuple; gui_evidence_sequence=15; offer_generation=3; log_writer_id=writer"
                    $lines += "event=runtime_unlock_retry_dispatch_deadline_evaluation_requested; $tuple; gui_evidence_sequence=16; observed_utc_ms=1010; subscriber_present=1; decision_reason=runtime_unlock_retry_dispatch_deadline_elapsed; log_writer_id=writer"
                    $lines += "event=runtime_unlock_retry_lifecycle_deadline_decided; $tuple; gui_evidence_sequence=17; lifecycle_state=Settled; coordinator_deadline_utc_ms=1000; observed_utc_ms=1010; decision_status=Accepted; decision_reason=retry_dispatch_settlement_accepted; log_writer_id=writer"
                    $lines += "event=runtime_unlock_retry_dispatch_contract_settled; $tuple; gui_evidence_sequence=18; offer_generation=3; settlement_outcome=MissingDispatch; dispatch_observed=0; decision_reason=retry_dispatch_deadline_elapsed; log_writer_id=writer"
                    $lines += "event=runtime_unlock_retry_dispatch_settlement_decided; $tuple; gui_evidence_sequence=19; settlement_outcome=MissingDispatch; settlement_reason=retry_dispatch_deadline_elapsed; decision_status=Accepted; decision_reason=retry_dispatch_settlement_accepted; log_writer_id=writer"
                    $lines += "event=runtime_unlock_retry_dispatch_chain_closed; $tuple; gui_evidence_sequence=20; decision_reason=retry_dispatch_missing_successor_limit_exhausted; log_writer_id=writer"
                }
                $lines | Set-Content -LiteralPath $path
                $result = Get-TunaGuiFileTransferSetupFailureClassification `
                    -Bookmark 0 `
                    -ErrorMessage 'gui_smoke_scenario_deadline_exceeded: scenario deadline elapsed' `
                    -RouteMode 'live-regular-activation-cycle' `
                    -ImmutableEvidencePaths @($path)
                if ('{{mode}}' -in @('unresolved', 'raw_only')) {
                    if ($result.Phase -ne 'runtime_unlock_retry' -or
                        $result.Reason -ne 'retry_dispatch_held_after_authority_granted' -or
                        $result.ClassificationSource -ne 'immutable_evidence_runtime_unlock_retry_post_grant_hold_index') {
                        throw "post-grant hold did not outrank deadline: $($result | ConvertTo-Json -Compress)"
                    }
                }
                elseif ($result.Phase -ne 'progress_failure' -or
                    $result.Reason -ne 'gui_smoke_scenario_deadline_exceeded') {
                    throw "exact dispatch did not recover post-grant hold: $($result | ConvertTo-Json -Compress)"
                }
                Write-Output 'gui-runtime-unlock-post-grant-hold-index-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(
            result.ExitCode == 0,
            $"Runtime-unlock post-grant hold GUI fast path failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-runtime-unlock-post-grant-hold-index-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeFailureClassification_MissingDispatchPredecessorCompletionUsesIndexedProofBeforeDeadline()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var resultBody = ExtractPowerShellFunctionBody(
            guiText,
            "New-TunaGuiFileTransferFailureClassificationResult",
            "Get-TunaGuiExplicitFailureClassification");
        var explicitBody = ExtractPowerShellFunctionBody(
            guiText,
            "Get-TunaGuiExplicitFailureClassification",
            "Resolve-TunaGuiFailureClassification");
        var classifierBody = ExtractPowerShellFunctionBody(
            guiText,
            "Get-TunaGuiFileTransferSetupFailureClassification",
            "Get-TunaGuiFailureClassificationBudgetMilliseconds");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function New-TunaGuiFileTransferFailureClassificationResult { {{resultBody}} }
            function Get-TunaGuiExplicitFailureClassification { {{explicitBody}} }
            function Get-TunaGuiFileTransferSetupFailureClassification { {{classifierBody}} }
            function Get-AppLogLinesAfterBookmark { throw 'live ledger was read' }
            function Read-GuiSmokeProcessOutputLinesSnapshot { throw 'process output was read' }
            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-missing-dispatch-completion-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $path = Join-Path $tempRoot 'evidence.log'
                $tuple = 'log_writer_id=writer; session_id=sess; transfer_id=transfer; direction=outbound; authority_id=retry-a; authority_generation=1; contract_generation=7; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; transport_epoch=1; bridge_recovery_generation=1; bridge_decision_generation=1; route_publication_generation=3; route_commit_generation=0; dispatch_attempt_generation=1; missing_dispatch_successor_generation=0; release_kind=FallbackPairedTailYield; release_authority_id=pair-a; release_generation=1; release_outcome=paired; offer_generation=2; offer_nonce=nonce-2'
                @(
                    "event=runtime_unlock_retry_dispatch_contract_settled; $tuple; settlement_outcome=MissingDispatch; dispatch_observed=0",
                    "event=runtime_unlock_retry_dispatch_settlement_decided; $tuple; settlement_outcome=MissingDispatch; decision_status=Accepted",
                    "event=runtime_unlock_retry_missing_dispatch_successor_intent_created; $tuple; failed_offer_generation=2; intent_disposition=Created",
                    'event=runtime_unlock_retry_negotiation_slot_decided; log_writer_id=writer; session_id=sess; transfer_id=transfer; contract_generation=7; source_publication_generation=3; source_transport_epoch=1; source_bridge_recovery_generation=1; intent_kind=SuccessorEvaluation; intent_generation=2; decision_status=Deferred; slot_generation=0; decision_reason=retry_dispatch_negotiation_slot_busy',
                    "event=runtime_unlock_post_tuna_fallback_activation_disposition; $tuple; operation_generation=5; disposition=completed; decision_reason=committed_authority_bound_to_current_offer",
                    "event=tuna_acceleration_negotiation_scheduling_completed; $tuple; operation_generation=5; decision_reason=committed_authority_bound_to_current_offer",
                    'event=file_transfer_outbound_terminal; log_writer_id=writer; session_id=sess; transfer_id=transfer; direction=outbound; state=Failed; error_code=peer_disconnected'
                ) | Set-Content -LiteralPath $path
                $result = Get-TunaGuiFileTransferSetupFailureClassification `
                    -Bookmark 0 `
                    -ErrorMessage 'gui_smoke_scenario_deadline_exceeded' `
                    -RouteMode 'live-regular-activation-cycle' `
                    -ImmutableEvidencePaths @($path)
                if ($result.Phase -ne 'runtime_unlock_retry' -or
                    $result.Reason -ne 'settled_missing_dispatch_predecessor_offer_not_retired_to_wake_exact_successor' -or
                    $result.ClassificationSource -ne 'immutable_evidence_missing_dispatch_completion_index') {
                    throw "missing-dispatch completion fast path mismatch: $($result | ConvertTo-Json -Compress)"
                }

                @("event=runtime_unlock_post_tuna_fallback_activation_disposition; $tuple; operation_generation=5; disposition=completed; decision_reason=committed_authority_bound_to_current_offer") |
                    Set-Content -LiteralPath $path
                $singleton = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'gui_smoke_scenario_deadline_exceeded' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($singleton.Reason -eq 'settled_missing_dispatch_predecessor_offer_not_retired_to_wake_exact_successor') {
                    throw 'singleton completion was classified without settlement, intent, busy, and scheduler evidence'
                }
                Write-Output 'gui-missing-dispatch-completion-index-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(
            result.ExitCode == 0,
            $"MissingDispatch completion GUI fast path failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-missing-dispatch-completion-index-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeFailureClassification_ProbeRollbackRetryCycleUsesIndexedProofBeforeDeadline()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var resultBody = ExtractPowerShellFunctionBody(
            guiText,
            "New-TunaGuiFileTransferFailureClassificationResult",
            "Get-TunaGuiExplicitFailureClassification");
        var explicitBody = ExtractPowerShellFunctionBody(
            guiText,
            "Get-TunaGuiExplicitFailureClassification",
            "Resolve-TunaGuiFailureClassification");
        var classifierBody = ExtractPowerShellFunctionBody(
            guiText,
            "Get-TunaGuiFileTransferSetupFailureClassification",
            "Get-TunaGuiFailureClassificationBudgetMilliseconds");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function New-TunaGuiFileTransferFailureClassificationResult { {{resultBody}} }
            function Get-TunaGuiExplicitFailureClassification { {{explicitBody}} }
            function Get-TunaGuiFileTransferSetupFailureClassification { {{classifierBody}} }
            function Get-AppLogLinesAfterBookmark { throw 'live ledger was read' }
            function Read-GuiSmokeProcessOutputLinesSnapshot { throw 'process output was read' }
            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-retry-cycle-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $path = Join-Path $tempRoot 'evidence.log'
                $scope = 'session_id=sess; transfer_id=transfer; direction=outbound; log_writer_id=writer'
                @(
                    "event=filetransfer_route_transition_disposition_applied; $scope; authority_id=transition-a; transition_generation=3; transition_kind=PostTunaFallbackV6ToTunaV4; source_route=post_tuna_fallback_v6; source_protocol_version=6; release_authority_id=yield-a; release_generation=1; install_generation=1; transaction_generation=1; offer_generation=2; tuna_path_lease_generation=2; disposition=rolledback; apply_status=applied",
                    "event=runtime_unlock_precommit_probe_failed; $scope; transaction_generation=1; offer_generation=2; tuna_path_lease_generation=2; reason=inbound_fallback_tail_not_yielded",
                    "event=filetransfer_runtime_unlock_probe_failed_fallback_resumed; $scope; route=post_tuna_fallback_v6; protocol_version=6; reason=inbound_fallback_tail_not_yielded",
                    "event=filetransfer_fallback_paired_tail_yield_runtime_unlock_retry_requested; $scope; authority_id=yield-a; yield_generation=1; yield_kind=Quiescent; decision_status=Held; decision_reason=fallback_paired_tail_yield_pending",
                    "event=file_transfer_outbound_terminal; $scope; state=Failed; error_code=route_cycle_missing"
                ) | Set-Content -LiteralPath $path
                $result = Get-TunaGuiFileTransferSetupFailureClassification `
                    -Bookmark 0 `
                    -ErrorMessage 'gui_smoke_scenario_deadline_exceeded' `
                    -RouteMode 'live-regular-activation-cycle' `
                    -ImmutableEvidencePaths @($path)
                if ($result.Phase -ne 'runtime_unlock_retry' -or
                    $result.Reason -ne 'pressure_rejected_precommit_probe_rollback_left_consumed_paired_yield_current_without_successor' -or
                    $result.ClassificationSource -ne 'immutable_evidence_fallback_tail_retry_cycle_successor_index' -or
                    -not [bool]$result.RuntimeUnlockRetryDispatchPairedYieldSuccessorMissingAfterProbeRollback) {
                    throw "retry-cycle successor fast path mismatch: $($result | ConvertTo-Json -Compress)"
                }

                @("event=filetransfer_fallback_paired_tail_yield_runtime_unlock_retry_requested; $scope; authority_id=yield-a; yield_generation=1; decision_status=Held; decision_reason=fallback_paired_tail_yield_pending") |
                    Set-Content -LiteralPath $path
                $singleton = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'gui_smoke_scenario_deadline_exceeded' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($singleton.Reason -eq 'pressure_rejected_precommit_probe_rollback_left_consumed_paired_yield_current_without_successor') {
                    throw 'singleton hold was classified without rollback, probe failure, and resume evidence'
                }
                Write-Output 'gui-retry-cycle-successor-index-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(
            result.ExitCode == 0,
            $"Retry-cycle successor GUI fast path failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-retry-cycle-successor-index-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuiSmokeFailureClassification_PairedYieldRetrySourceScopeFailureUsesIndexedProofBeforeDeadline(
        bool recovered)
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var resultBody = ExtractPowerShellFunctionBody(
            guiText,
            "New-TunaGuiFileTransferFailureClassificationResult",
            "Get-TunaGuiExplicitFailureClassification");
        var explicitBody = ExtractPowerShellFunctionBody(
            guiText,
            "Get-TunaGuiExplicitFailureClassification",
            "Resolve-TunaGuiFailureClassification");
        var classifierBody = ExtractPowerShellFunctionBody(
            guiText,
            "Get-TunaGuiFileTransferSetupFailureClassification",
            "Get-TunaGuiFailureClassificationBudgetMilliseconds");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function New-TunaGuiFileTransferFailureClassificationResult { {{resultBody}} }
            function Get-TunaGuiExplicitFailureClassification { {{explicitBody}} }
            function Get-TunaGuiFileTransferSetupFailureClassification { {{classifierBody}} }
            function Get-AppLogLinesAfterBookmark { throw 'live ledger was read' }
            function Read-GuiSmokeProcessOutputLinesSnapshot { throw 'process output was read' }

            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-paired-yield-source-scope-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $path = Join-Path $tempRoot 'evidence.log'
                $scope = 'session_id=sess; transfer_id=transfer; direction=outbound; authority_id=yield-a; yield_generation=1; log_writer_id=writer'
                $lines = @(
                    "event=filetransfer_fallback_tail_local_proof_receipt_decided; $scope; decision_status=Accepted",
                    "event=filetransfer_fallback_tail_peer_proof_receipt_decided; $scope; decision_status=Accepted",
                    "event=filetransfer_fallback_tail_pairing_committed; $scope; decision_status=Accepted",
                    "event=filetransfer_fallback_tail_paired_yield_granted; $scope",
                    'event=runtime_unlock_retry_dispatch_authority_request_queue_rejected; session_id=sess; transfer_id=transfer; direction=outbound; contract_generation=1; retired_generation=0; dispatch_attempt_generation=1; missing_dispatch_successor_generation=0; predecessor_authority_id=(none); source_live_route_epoch=2; source_runtime_generation=3; source_terminal_generation=0; source_leg_generation=3; source_route_publication_generation=3; retry_reason=fallback_paired_tail_yield; decision_reason=coordinator_request_source_scope_not_current; log_writer_id=writer',
                    "event=filetransfer_fallback_paired_tail_yield_runtime_unlock_retry_requested; $scope; decision_status=Rejected; decision_reason=fallback_paired_tail_yield_request_queue_rejected"
                )
                if (${{recovered.ToString().ToLowerInvariant()}}) {
                    $lines += "event=filetransfer_fallback_paired_tail_yield_runtime_unlock_retry_requested; $scope; decision_status=Queued"
                    $lines += 'event=runtime_unlock_retry_dispatch_authority_granted; session_id=sess; transfer_id=transfer; release_authority_id=yield-a; log_writer_id=writer'
                    $lines += "event=filetransfer_fallback_tail_paired_release_reserved; $scope"
                    $lines += "event=filetransfer_fallback_tail_paired_release_consumed; $scope"
                    $lines += 'event=filetransfer_route_transition_prepared; session_id=sess; transfer_id=transfer; direction=outbound; transition_kind=PostTunaFallbackV6ToTunaV4; fallback_yield_authority_id=yield-a; log_writer_id=writer'
                }
                [IO.File]::WriteAllLines($path, $lines)
                $result = Get-TunaGuiFileTransferSetupFailureClassification `
                    -Bookmark 0 `
                    -ErrorMessage 'gui_smoke_scenario_deadline_exceeded: scenario deadline elapsed' `
                    -RouteMode 'live-regular-activation-cycle' `
                    -ImmutableEvidencePaths @($path)
                if (-not ${{recovered.ToString().ToLowerInvariant()}}) {
                    if ($result.Phase -ne 'route_runtime' -or
                        $result.Reason -ne 'fallback_paired_tail_yield_retry_request_source_scope_not_current' -or
                        $result.ClassificationSource -ne 'immutable_evidence_paired_yield_retry_source_scope_index') {
                        throw "paired-yield source-scope failure did not outrank deadline: $($result | ConvertTo-Json -Compress)"
                    }
                }
                elseif ($result.Phase -ne 'progress_failure' -or
                    $result.Reason -ne 'gui_smoke_scenario_deadline_exceeded') {
                    throw "exact recovery did not close paired-yield source-scope failure: $($result | ConvertTo-Json -Compress)"
                }
                Write-Output 'gui-paired-yield-source-scope-index-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(
            result.ExitCode == 0,
            $"Paired-yield source-scope GUI fast path failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-paired-yield-source-scope-index-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeFailureClassification_ScenarioDeadlineUsesOnePassEvidenceScanBeforeFallback()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var resultBody = ExtractPowerShellFunctionBody(
            guiText,
            "New-TunaGuiFileTransferFailureClassificationResult",
            "Get-TunaGuiExplicitFailureClassification");
        var explicitBody = ExtractPowerShellFunctionBody(
            guiText,
            "Get-TunaGuiExplicitFailureClassification",
            "Resolve-TunaGuiFailureClassification");
        var classifierBody = ExtractPowerShellFunctionBody(
            guiText,
            "Get-TunaGuiFileTransferSetupFailureClassification",
            "Get-TunaGuiFailureClassificationBudgetMilliseconds");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function New-TunaGuiFileTransferFailureClassificationResult { {{resultBody}} }
            function Get-TunaGuiExplicitFailureClassification { {{explicitBody}} }
            function Get-TunaGuiFileTransferSetupFailureClassification { {{classifierBody}} }
            function Get-AppLogLinesAfterBookmark { throw 'live ledger was read' }
            function Read-GuiSmokeProcessOutputLinesSnapshot { throw 'process output was read' }

            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-explicit-one-pass-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $nearMatch = Get-TunaGuiExplicitFailureClassification `
                    -ErrorMessage 'gui_smoke_scenario_deadline_exceeded_suffix: not the exact marker'
                if ($null -ne $nearMatch) {
                    throw 'scenario deadline near-match was treated as the exact marker'
                }
                $largeEvidence = Join-Path $tempRoot 'large-evidence.log'
                $writer = [IO.StreamWriter]::new($largeEvidence, $false)
                try {
                    for ($index = 0; $index -lt 100000; $index++) {
                        $writer.WriteLine('event=filetransfer_regular_v4_frontier_repair_settlement_accepted')
                    }
                }
                finally {
                    $writer.Dispose()
                }
                $metrics = [pscustomobject]@{
                    evidenceLineVisitCount = -1L
                    evidenceLookupCount = -1L
                    classificationSource = ''
                }
                $result = Get-TunaGuiFileTransferSetupFailureClassification `
                    -Bookmark 0 `
                    -ErrorMessage 'GUI_SMOKE_SCENARIO_DEADLINE_EXCEEDED: scenario deadline elapsed' `
                    -RouteMode 'live-regular-activation-cycle' `
                    -ImmutableEvidencePaths @($largeEvidence) `
                    -TestMetrics $metrics
                if ($result.Phase -ne 'progress_failure' -or
                    $result.Reason -ne 'gui_smoke_scenario_deadline_exceeded' -or
                    $result.ClassificationSource -ne 'explicit_error' -or
                    -not $result.ClassificationComplete -or
                    [long]$result.EvidenceLineVisitCount -ne 100000 -or
                    [long]$result.EvidenceParsedRecordCount -ne 0 -or
                    [long]$result.EvidenceLookupCount -ne 0 -or
                    [long]$metrics.evidenceLineVisitCount -ne 100000 -or
                    [long]$metrics.evidenceLookupCount -ne 0 -or
                    $metrics.classificationSource -ne 'explicit_error') {
                    throw "scenario-deadline classification chronology arbitration mismatch: result=$($result | ConvertTo-Json -Compress); metrics=$($metrics | ConvertTo-Json -Compress)"
                }
                Write-Output 'gui-scenario-deadline-one-pass-fallback-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) {
                    Remove-Item -LiteralPath $tempRoot -Recurse -Force
                }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(
            result.ExitCode == 0,
            $"Scenario-deadline classification chronology arbitration failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-scenario-deadline-one-pass-fallback-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeFailureClassification_InstalledRetryOfferPendingBehindOlderDialUsesAnswerPeerDeliveryClass()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var resultBody = ExtractPowerShellFunctionBody(
            guiText,
            "New-TunaGuiFileTransferFailureClassificationResult",
            "Get-TunaGuiExplicitFailureClassification");
        var explicitBody = ExtractPowerShellFunctionBody(
            guiText,
            "Get-TunaGuiExplicitFailureClassification",
            "Resolve-TunaGuiFailureClassification");
        var classifierBody = ExtractPowerShellFunctionBody(
            guiText,
            "Get-TunaGuiFileTransferSetupFailureClassification",
            "Get-TunaGuiFailureClassificationBudgetMilliseconds");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function New-TunaGuiFileTransferFailureClassificationResult { {{resultBody}} }
            function Get-TunaGuiExplicitFailureClassification { {{explicitBody}} }
            function Get-TunaGuiFileTransferSetupFailureClassification { {{classifierBody}} }
            function Get-AppLogLinesAfterBookmark { throw 'live ledger was read' }
            function Read-GuiSmokeProcessOutputLinesSnapshot { throw 'process output was read' }

            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-installed-retry-pending-dial-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $path = Join-Path $tempRoot 'evidence.log'
                $base = @(
                    'event=runtime_unlock_tuna_dial_attempt_started; session_id=sess-1; payer_decision_id=6; dial_attempt_generation=2; log_writer_id=writer-responder',
                    'event=runtime_unlock_retry_dispatch_install_committed; session_id=sess-1; direction=outbound; authority_id=authority-1; install_generation=1; install_status=Installed; log_writer_id=writer-sender',
                    'event=runtime_unlock_retry_dispatch_install_confirmed; session_id=sess-1; direction=outbound; authority_id=authority-1; install_generation=1; log_writer_id=writer-sender',
                    'event=runtime_unlock_offer_authority_boundary_decided; session_id=sess-1; payer_decision_id=7; authority_mode=InstalledRetry; decision_status=accepted; retry_authority_id=authority-1; install_generation=1; log_writer_id=writer-sender',
                    'event=runtime_unlock_tuna_dial_offer_duplicate_ignored; session_id=sess-1; payer_decision_id=7; reason=other_dial_pending; disposition=PendingDuplicate; log_writer_id=writer-responder',
                    'event=runtime_unlock_retry_dispatch_observed; session_id=sess-1; authority_id=authority-1; install_generation=1; log_writer_id=writer-sender',
                    'event=runtime_unlock_tuna_dial_attempt_failed; session_id=sess-1; payer_decision_id=6; dial_attempt_generation=2; reason=runtime_unlock_tuna_dial_failed; log_writer_id=writer-responder'
                )
                [IO.File]::WriteAllLines($path, $base)
                $result = Get-TunaGuiFileTransferSetupFailureClassification `
                    -Bookmark 0 `
                    -ErrorMessage 'gui_smoke_scenario_deadline_exceeded' `
                    -RouteMode 'live-regular-activation-cycle' `
                    -ImmutableEvidencePaths @($path)
                if ($result.Phase -ne 'runtime_unlock_initial_activation' -or
                    $result.Reason -ne 'installed_retry_offer_pending_duplicate_not_replayed_after_other_dial_failure') {
                    throw "installed retry pending-dial classification mismatch: $($result | ConvertTo-Json -Compress)"
                }

                [IO.File]::WriteAllLines($path, @($base) + @(
                    'event=runtime_unlock_tuna_dial_attempt_started; session_id=sess-1; payer_decision_id=7; dial_attempt_generation=3; log_writer_id=writer-responder',
                    'event=filetransfer_route_transition_prepared; session_id=sess-1; direction=inbound; log_writer_id=writer-responder'
                ))
                $recovered = Get-TunaGuiFileTransferSetupFailureClassification `
                    -Bookmark 0 `
                    -ErrorMessage 'gui_smoke_scenario_deadline_exceeded' `
                    -RouteMode 'live-regular-activation-cycle' `
                    -ImmutableEvidencePaths @($path)
                if ($recovered.Reason -eq 'installed_retry_offer_pending_duplicate_not_replayed_after_other_dial_failure') {
                    throw 'exact successor replay did not recover the pending-dial candidate'
                }
                Write-Output 'gui-installed-retry-pending-dial-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) {
                    Remove-Item -LiteralPath $tempRoot -Recurse -Force
                }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(
            result.ExitCode == 0,
            $"InstalledRetry pending-dial classification failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-installed-retry-pending-dial-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeFailureClassification_InitialResponderDrainReceiptUsesRouteTransitionFastPath()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var resultBody = ExtractPowerShellFunctionBody(
            guiText,
            "New-TunaGuiFileTransferFailureClassificationResult",
            "Get-TunaGuiExplicitFailureClassification");
        var explicitBody = ExtractPowerShellFunctionBody(
            guiText,
            "Get-TunaGuiExplicitFailureClassification",
            "Resolve-TunaGuiFailureClassification");
        var classifierBody = ExtractPowerShellFunctionBody(
            guiText,
            "Get-TunaGuiFileTransferSetupFailureClassification",
            "Get-TunaGuiFailureClassificationBudgetMilliseconds");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function New-TunaGuiFileTransferFailureClassificationResult { {{resultBody}} }
            function Get-TunaGuiExplicitFailureClassification { {{explicitBody}} }
            function Get-TunaGuiFileTransferSetupFailureClassification { {{classifierBody}} }
            function Get-AppLogLinesAfterBookmark { throw 'live ledger was read' }
            function Read-GuiSmokeProcessOutputLinesSnapshot { throw 'process output was read' }

            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-responder-drain-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $path = Join-Path $tempRoot 'evidence.log'
                $tuple = 'direction=inbound; session_id=sess; transfer_id=transfer; log_writer_id=writer; authority_id=authority; transition_generation=1; transition_kind=RegularV4ToTunaV4; source_route=regular_nkn_v4_fast; source_protocol_version=4; source_live_route_epoch=0; source_runtime_generation=1; source_route_publication_generation=1; source_terminal_generation=0; source_leg_generation=1; source_transport_epoch=0; source_bridge_recovery_generation=0; source_data_session_generation=1; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; proof_boundary_kind=tuna_activation; responder_transition_mode=initial_regular_v4; activation_authority_mode=initial_responder; admission_generation=1; pending_precommit_generation=1; transaction_generation=1; offer_generation=6; launch_generation=1; tuna_path_lease_generation=1; retry_install_generation=0'
                @(
                    "event=filetransfer_route_transition_prepared; decision_status=Prepared; $tuple",
                    "event=filetransfer_route_transition_pause_bound; decision_status=Installed; $tuple",
                    "event=filetransfer_route_transition_draining; decision_status=Draining; $tuple",
                    "event=filetransfer_route_transition_drain_receipt_decided; decision_status=Rejected; decision_reason=filetransfer_route_transition_initial_responder_boundary_not_current; $tuple",
                    "event=filetransfer_route_transition_drain_receipt_decided; decision_status=Rejected; decision_reason=filetransfer_route_transition_initial_responder_boundary_not_current; $tuple"
                ) | Set-Content -LiteralPath $path
                $result = Get-TunaGuiFileTransferSetupFailureClassification `
                    -Bookmark 0 `
                    -ErrorMessage 'gui_smoke_scenario_deadline_exceeded: scenario deadline elapsed' `
                    -RouteMode 'live-regular-activation-cycle' `
                    -ImmutableEvidencePaths @($path)
                if ($result.Phase -ne 'route_transition' -or
                    $result.Reason -ne 'filetransfer_route_transition_initial_responder_boundary_not_current' -or
                    $result.ClassificationSource -ne 'immutable_evidence_initial_responder_drain_receipt_index') {
                    throw "initial responder drain classification mismatch: $($result | ConvertTo-Json -Compress)"
                }

                @(
                    "event=filetransfer_route_transition_drain_receipt_decided; decision_status=Accepted; drain_outcome=Completed; $tuple",
                    "event=filetransfer_route_transition_committed; decision_status=Committed; $tuple",
                    "event=filetransfer_route_transition_disposition_applied; decision_status=Applied; apply_status=Applied; $tuple",
                    'event=filetransfer_route_selected; direction=inbound; session_id=sess; transfer_id=transfer; log_writer_id=writer; route=file_tuna_v4'
                ) | Add-Content -LiteralPath $path
                $recovered = Get-TunaGuiFileTransferSetupFailureClassification `
                    -Bookmark 0 `
                    -ErrorMessage 'gui_smoke_scenario_deadline_exceeded: scenario deadline elapsed' `
                    -RouteMode 'live-regular-activation-cycle' `
                    -ImmutableEvidencePaths @($path)
                if ($recovered.Phase -ne 'progress_failure' -or
                    $recovered.Reason -ne 'gui_smoke_scenario_deadline_exceeded') {
                    throw "recovered responder drain retained route failure: $($recovered | ConvertTo-Json -Compress)"
                }

                $postFallbackTuple = 'direction=inbound; session_id=sess; transfer_id=transfer; log_writer_id=writer; authority_id=authority-post-fallback; transition_generation=3; transition_kind=PostTunaFallbackV6ToTunaV4; source_route=post_tuna_fallback_v6; source_protocol_version=6; source_live_route_epoch=2; source_runtime_generation=3; source_route_publication_generation=3; source_terminal_generation=0; source_leg_generation=3; source_transport_epoch=1; source_bridge_recovery_generation=1; source_data_session_generation=1; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; proof_boundary_kind=tuna_activation; responder_transition_mode=post_fallback_v6_paired; activation_authority_mode=responder; admission_generation=3; pending_precommit_generation=2; transaction_generation=1; offer_generation=9; launch_generation=4; tuna_path_lease_generation=2; retry_install_generation=0; fallback_yield_authority_id=yield-1; fallback_yield_generation=1; pairing_generation=1; pair_key=pair-1; responder_handoff_authority_id=handoff-1; responder_handoff_generation=1; checkpoint_request_id=(none); data_session_identity=data-session:1; checkpoint_generation=1; checkpoint_state_epoch=1039; checkpoint_contiguous_frontier=2586; checkpoint_durable_highest=3094; checkpoint_chunk_count=12484'
                @(
                    'event=filetransfer_fallback_tail_pairing_committed; decision_status=Accepted; direction=inbound; session_id=sess; transfer_id=transfer; log_writer_id=writer; authority_id=yield-1; yield_generation=1; pairing_generation=1; pair_key=pair-1; pairing_state=Paired; local_proof_identity=local-proof; peer_observation_identity=peer-proof',
                    'event=filetransfer_fallback_tail_paired_yield_granted; direction=inbound; session_id=sess; transfer_id=transfer; log_writer_id=writer; authority_id=yield-1; yield_generation=1; pair_key=pair-1',
                    'event=filetransfer_runtime_unlock_responder_peer_probe_route_transition_handoff_decided; decision_status=Accepted; direction=inbound; session_id=sess; transfer_id=transfer; log_writer_id=writer; responder_transition_mode=post_fallback_v6_paired; activation_authority_mode=responder; admission_generation=3; pending_precommit_generation=2; transaction_generation=1; offer_generation=9; launch_generation=4; tuna_path_lease_generation=2; fallback_yield_authority_id=yield-1; fallback_yield_generation=1; pairing_generation=1; pair_key=pair-1; responder_handoff_authority_id=handoff-1; responder_handoff_generation=1; checkpoint_generation=1; data_session_identity=data-session:1; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; pair_state=Paired; probe_state=acked; probe_transport=tuna; answer_acceptance_source=SemanticAnswerAck; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; route_publication_generation=3; terminal_generation=0; leg_generation=3; transport_epoch=1; bridge_recovery_generation=1; data_session_generation=1',
                    "event=filetransfer_route_transition_prepared; decision_status=Prepared; $postFallbackTuple",
                    "event=filetransfer_route_transition_pause_bound; decision_status=Installed; $postFallbackTuple",
                    "event=filetransfer_route_transition_draining; decision_status=Draining; $postFallbackTuple",
                    "event=filetransfer_route_transition_drain_receipt_decided; decision_status=Rejected; decision_reason=paired_fallback_tail_yield_missing; $postFallbackTuple",
                    "event=filetransfer_route_transition_invalidated; decision_status=Invalidated; decision_reason=inbound_transfer_terminal; $postFallbackTuple"
                ) | Set-Content -LiteralPath $path
                $postFallback = Get-TunaGuiFileTransferSetupFailureClassification `
                    -Bookmark 0 `
                    -ErrorMessage 'gui_smoke_scenario_deadline_exceeded: scenario deadline elapsed' `
                    -RouteMode 'live-regular-activation-cycle' `
                    -ImmutableEvidencePaths @($path)
                if ($postFallback.Phase -ne 'route_transition' -or
                    $postFallback.Reason -ne 'paired_fallback_tail_yield_missing') {
                    throw "post-fallback responder drain mismatch: $($postFallback | ConvertTo-Json -Compress)"
                }

                @(
                    "event=filetransfer_route_transition_prepared; decision_status=Prepared; $tuple",
                    "event=filetransfer_route_transition_pause_bound; decision_status=Installed; $tuple",
                    "event=filetransfer_route_transition_draining; decision_status=Draining; $tuple",
                    "event=filetransfer_route_transition_invalidated; decision_status=Invalidated; decision_reason=inbound_transfer_terminal; $tuple",
                    "event=filetransfer_route_transition_disposition_applied; decision_status=Applied; apply_status=Applied; disposition=terminalinvalidated; $tuple",
                    'event=file_transfer_inbound_terminal; direction=inbound; session_id=sess; transfer_id=transfer; log_writer_id=writer; state=Canceled; error=canceled_local'
                ) | Set-Content -LiteralPath $path
                $terminalInvalidation = Get-TunaGuiFileTransferSetupFailureClassification `
                    -Bookmark 0 `
                    -ErrorMessage 'gui_smoke_scenario_deadline_exceeded: scenario deadline elapsed' `
                    -RouteMode 'live-regular-activation-cycle' `
                    -ImmutableEvidencePaths @($path)
                if ($terminalInvalidation.Phase -ne 'route_transition' -or
                    $terminalInvalidation.Reason -ne 'initial_responder_route_transition_terminal_invalidated_without_exact_drain_receipt') {
                    throw "terminal-invalidated responder drain mismatch: $($terminalInvalidation | ConvertTo-Json -Compress)"
                }

                @(
                    "event=filetransfer_route_transition_prepared; decision_status=Prepared; $tuple",
                    "event=filetransfer_route_transition_pause_bound; decision_status=Installed; $tuple",
                    "event=filetransfer_route_transition_draining; decision_status=Draining; $tuple",
                    "event=filetransfer_route_transition_drain_receipt_decided; decision_status=Accepted; drain_outcome=Completed; $tuple",
                    "event=filetransfer_route_transition_committed; decision_status=Committed; $tuple",
                    "event=filetransfer_route_transition_disposition_applied; decision_status=Applied; apply_status=Applied; disposition=committed; $tuple",
                    "event=filetransfer_route_transition_invalidated; decision_status=Invalidated; decision_reason=inbound_transfer_terminal; $tuple",
                    "event=filetransfer_route_transition_disposition_applied; decision_status=Applied; apply_status=Applied; disposition=terminalinvalidated; $tuple"
                ) | Set-Content -LiteralPath $path
                $completedBeforeTerminal = Get-TunaGuiFileTransferSetupFailureClassification `
                    -Bookmark 0 `
                    -ErrorMessage 'gui_smoke_scenario_deadline_exceeded: scenario deadline elapsed' `
                    -RouteMode 'live-regular-activation-cycle' `
                    -ImmutableEvidencePaths @($path)
                if ($completedBeforeTerminal.Phase -ne 'progress_failure' -or
                    $completedBeforeTerminal.Reason -ne 'gui_smoke_scenario_deadline_exceeded') {
                    throw "completed responder drain was misclassified: $($completedBeforeTerminal | ConvertTo-Json -Compress)"
                }
                Write-Output 'gui-initial-responder-drain-fast-path-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) {
                    Remove-Item -LiteralPath $tempRoot -Recurse -Force
                }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(
            result.ExitCode == 0,
            $"Initial responder drain GUI fast path failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-initial-responder-drain-fast-path-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeFailureClassification_RetiredPendingAnswerUsesIndexedFastPath()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var resultBody = ExtractPowerShellFunctionBody(
            guiText,
            "New-TunaGuiFileTransferFailureClassificationResult",
            "Get-TunaGuiExplicitFailureClassification");
        var explicitBody = ExtractPowerShellFunctionBody(
            guiText,
            "Get-TunaGuiExplicitFailureClassification",
            "Resolve-TunaGuiFailureClassification");
        var classifierBody = ExtractPowerShellFunctionBody(
            guiText,
            "Get-TunaGuiFileTransferSetupFailureClassification",
            "Get-TunaGuiFailureClassificationBudgetMilliseconds");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function New-TunaGuiFileTransferFailureClassificationResult { {{resultBody}} }
            function Get-TunaGuiExplicitFailureClassification { {{explicitBody}} }
            function Get-TunaGuiFileTransferSetupFailureClassification { {{classifierBody}} }
            function Get-AppLogLinesAfterBookmark { throw 'live ledger was read' }
            function Read-GuiSmokeProcessOutputLinesSnapshot { throw 'process output was read' }

            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-retired-answer-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $path = Join-Path $tempRoot 'evidence.log'
                @(
                    'event=tuna_acceleration_offer_queued; reason=runtime_unlock; session_id=sess; payer_decision_id=4; generation=1; log_writer_id=writer',
                    'event=tuna_acceleration_outbound_offer_retired; reason=runtime_unlock_offer_peer_response_timeout_pending_runtime_unlock_answer; session_id=sess; payer_decision_id=4; log_writer_id=writer',
                    'event=tuna_acceleration_pending_runtime_unlock_answer_preserved; reason=runtime_unlock_offer_peer_response_timeout_pending_runtime_unlock_answer; session_id=sess; payer_decision_id=4; generation=1; log_writer_id=writer',
                    'event=tuna_acceleration_answer_received_raw; msg_id=answer; log_writer_id=writer',
                    'event=runtime_unlock_offer_authority_boundary_decided; boundary=answer_received; authority_mode=InitialActivation; decision_status=accepted; session_id=sess; payer_decision_id=4; offer_generation=1; transaction_generation=1; log_writer_id=writer',
                    'event=tuna_acceleration_retired_offer_answer_accepted; session_id=sess; payer_decision_id=4; log_writer_id=writer',
                    'event=tuna_acceleration_answer_ack_sent; session_id=sess; payer_decision_id=4; generation=1; log_writer_id=writer',
                    'event=runtime_unlock_answer_ack_probe_barrier_released; session_id=sess; payer_decision_id=4; log_writer_id=writer',
                    'event=filetransfer_runtime_unlock_handoff_pending_route_commit; session_id=sess; offer_generation=1; transaction_generation=1; state=offersentobserved; path_probe_state=none; log_writer_id=writer'
                ) | Set-Content -LiteralPath $path
                $result = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($result.Phase -ne 'runtime_unlock_initial_activation' -or
                    $result.Reason -ne 'accepted_retired_pending_answer_not_promoted_to_runtime_unlock_transaction' -or
                    -not $result.InitialActivationRetiredPendingAnswerHandoffMissing -or
                    $result.ClassificationSource -ne 'immutable_evidence_retired_pending_answer_index' -or
                    [long]$result.EvidenceLineVisitCount -ne 9) {
                    throw "retired pending answer fast path mismatch: $($result | ConvertTo-Json -Compress)"
                }
                Write-Output 'gui-retired-pending-answer-fast-path-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(
            result.ExitCode == 0,
            $"Retired pending-answer GUI fast path failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-retired-pending-answer-fast-path-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeFailureClassification_ResponderAckedProbeUsesIndexedFastPath()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var resultBody = ExtractPowerShellFunctionBody(guiText, "New-TunaGuiFileTransferFailureClassificationResult", "Get-TunaGuiExplicitFailureClassification");
        var explicitBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiExplicitFailureClassification", "Resolve-TunaGuiFailureClassification");
        var classifierBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiFileTransferSetupFailureClassification", "Get-TunaGuiFailureClassificationBudgetMilliseconds");
        Assert.Contains("$earlyPredicateEventNames", classifierBody, StringComparison.Ordinal);
        Assert.Contains("$firstEarlyRouteRuntimeFailure", classifierBody, StringComparison.Ordinal);
        Assert.Contains("Sort-Object Sequence", classifierBody, StringComparison.Ordinal);
        Assert.Contains("$answerAckEnvelopeByMessage[$envelopeKey].Add", classifierBody, StringComparison.Ordinal);
        Assert.Contains("$state.AdoptionRecheckObserved =", classifierBody, StringComparison.Ordinal);
        Assert.DoesNotContain("$state.PendingPrecommitGeneration | Where-Object", classifierBody, StringComparison.Ordinal);
        Assert.DoesNotContain("$state.ResponderHandoffGeneration |", classifierBody, StringComparison.Ordinal);
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            Set-StrictMode -Version Latest
            function New-TunaGuiFileTransferFailureClassificationResult { {{resultBody}} }
            function Get-TunaGuiExplicitFailureClassification { {{explicitBody}} }
            function Get-TunaGuiFileTransferSetupFailureClassification { {{classifierBody}} }
            function Get-AppLogLinesAfterBookmark { throw 'live ledger was read' }
            function Read-GuiSmokeProcessOutputLinesSnapshot { throw 'process output was read' }
            function Test-TunaGuiBridgeBootstrapNotReadyEvidence { return $false }
            function Test-TunaGuiLatestFallbackFrontierRequestDispatchMissing { return $false }
            function Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing { return $false }
            function Test-TunaGuiLogLinesContain { return $false }
            function Get-TunaGuiReadinessStateAfterBookmark {
                return [pscustomobject]@{ ListenerUnavailable = $false; ListenerReady = $false; TunaActive = $false }
            }
            function Resolve-TunaGuiFailureClassification {
                param($ErrorMessage, $HistoricalPhase, $HistoricalReason)
                return [pscustomobject]@{ Phase = $HistoricalPhase; Reason = $HistoricalReason }
            }
            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-responder-probe-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $path = Join-Path $tempRoot 'evidence.log'
                @(
                    'event=runtime_unlock_transaction_answer_received; session_id=sess; transaction_generation=1; offer_generation=6; answer_received=1; tuna_path_lease_generation=1; tuna_path_lease_current=1; tuna_path_lease_listener_run_id=peer-offer-dialer-1; log_writer_id=writer',
                    'event=tuna_acceleration_answer_ack_pending; session_id=sess; payer_decision_id=4; generation=6; log_writer_id=writer',
                    'event=runtime_unlock_transaction_path_probe_acked; session_id=sess; transaction_generation=1; offer_generation=6; path_probe_id=probe; path_probe_state=acked; path_probe_transport=tuna; path_probe_acked_utc_ms=100; tuna_path_lease_generation=1; log_writer_id=writer',
                    'event=runtime_unlock_precommit_probe_acked; session_id=sess; transfer_id=transfer; direction=inbound; transaction_generation=1; offer_generation=1; launch_generation=0; probe_id=probe; probe_state=acked; received_transport=tuna; tuna_path_lease_generation=1; log_writer_id=writer',
                    'event=nkn_inbound_envelope_received; envelope_type=TransportAccelerationAnswerAck; source_matches_expected=1; msg_id=answer-ack; log_writer_id=writer',
                    'event=tuna_acceleration_answer_ack_received_raw; msg_id=answer-ack; log_writer_id=writer',
                    'event=tuna_acceleration_answer_ack_received; session_id=sess; lanes=file; payer_decision_id=4; log_writer_id=writer',
                    'event=runtime_unlock_answer_ack_probe_barrier_received; session_id=sess; lanes=file; payer_decision_id=4; log_writer_id=writer',
                    'event=filetransfer_runtime_unlock_handoff_pending_route_commit; session_id=sess; lanes=file; transaction_generation=1; offer_generation=6; state=routecommitpending; path_probe_state=acked; route_commit_pending=1; route_committed=0; log_writer_id=writer',
                    'event=runtime_unlock_precommit_probe_launch_requested; session_id=sess; transfer_id=transfer; lanes=file; transaction_generation=1; offer_generation=6; launch_generation=2; tuna_path_lease_generation=1; tuna_path_lease_listener_run_id=peer-offer-dialer-1; path_probe_id=probe; path_probe_state=acked; launch_role=ResponderAdoptingAckedPeerProbe; peer_probe_adoption=1; duplicate=0; tuple_mutation=0; responder_waiting_for_peer_probe=0; log_writer_id=writer',
                    'event=runtime_unlock_retry_dispatch_held_by_active_negotiation; authority_id=unrelated; session_id=other; transfer_id=other; direction=outbound; decision_status=Held; decision_reason=active_negotiation_pending; log_writer_id=writer'
                ) | Set-Content -LiteralPath $path
                $result = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($result.Phase -ne 'route_runtime' -or
                    $result.Reason -ne 'late_answer_ack_did_not_bind_acked_peer_probe_to_responder_route_transition' -or
                    -not $result.RuntimeUnlockResponderPeerProbeHandoffMissing -or
                    -not $result.RuntimeUnlockResponderPeerProbeBindingMissing -or
                    $result.RuntimeUnlockResponderPeerProbeAdoptionRecheckMissingAfterAnswerAckBeforeProbe -or
                    $result.RuntimeUnlockResponderPeerProbeRouteTransitionHandoffMissingAfterBoundProbe -or
                    $result.ClassificationSource -ne 'immutable_evidence_responder_peer_probe_index' -or
                    [long]$result.EvidenceLineVisitCount -ne 11 -or
                    [long]$result.EvidenceParsedRecordCount -ne 10 -or
                    [long]$result.EvidenceLookupCount -le 0) {
                    throw "responder peer-probe fast path mismatch: $($result | ConvertTo-Json -Compress)"
                }
                $deadlineResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'Gui_Smoke_Scenario_Deadline_Exceeded: scenario deadline elapsed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($deadlineResult.Phase -ne $result.Phase -or
                    $deadlineResult.Reason -ne $result.Reason -or
                    $deadlineResult.ClassificationSource -ne $result.ClassificationSource -or
                    [long]$deadlineResult.EvidenceLineVisitCount -ne 11) {
                    throw "exact proof did not override scenario-deadline fallback: $($deadlineResult | ConvertTo-Json -Compress)"
                }
                Write-Output 'gui-responder-peer-probe-fast-path-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(result.ExitCode == 0, $"Responder peer-probe GUI fast path failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-responder-peer-probe-fast-path-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeFailureClassification_PairedRetryScopeMismatchUsesBindingFastPath()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var resultBody = ExtractPowerShellFunctionBody(guiText, "New-TunaGuiFileTransferFailureClassificationResult", "Get-TunaGuiExplicitFailureClassification");
        var explicitBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiExplicitFailureClassification", "Resolve-TunaGuiFailureClassification");
        var classifierBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiFileTransferSetupFailureClassification", "Get-TunaGuiFailureClassificationBudgetMilliseconds");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            Set-StrictMode -Version Latest
            function New-TunaGuiFileTransferFailureClassificationResult { {{resultBody}} }
            function Get-TunaGuiExplicitFailureClassification { {{explicitBody}} }
            function Get-TunaGuiFileTransferSetupFailureClassification { {{classifierBody}} }
            function Get-AppLogLinesAfterBookmark { throw 'live ledger was read' }
            function Read-GuiSmokeProcessOutputLinesSnapshot { throw 'process output was read' }
            function Test-TunaGuiBridgeBootstrapNotReadyEvidence { return $false }
            function Test-TunaGuiLatestFallbackFrontierRequestDispatchMissing { return $false }
            function Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing { return $false }
            function Test-TunaGuiLogLinesContain { return $false }
            function Get-TunaGuiReadinessStateAfterBookmark {
                return [pscustomobject]@{ ListenerUnavailable = $false; ListenerReady = $false; TunaActive = $false }
            }
            function Resolve-TunaGuiFailureClassification {
                param($ErrorMessage, $HistoricalPhase, $HistoricalReason)
                return [pscustomobject]@{ Phase = $HistoricalPhase; Reason = $HistoricalReason }
            }
            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-paired-retry-binding-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $path = Join-Path $tempRoot 'evidence.log'
                @(
                    'event=runtime_unlock_retry_dispatch_observed; authority_id=retry-authority; authority_generation=1; direction=outbound; session_id=sess; transfer_id=transfer; route=post_tuna_fallback_v6; protocol_version=6; retry_reason=fallback_paired_tail_yield; offer_generation=2; observed_lane=control_plane_peer_visible; log_writer_id=sender',
                    'event=tuna_acceleration_offer_queued; reason=fallback_paired_tail_yield; session_id=sess; payer_decision_id=5; generation=2; observed_lane=control_plane_peer_visible; queue_local_only=0; log_writer_id=sender',
                    'event=tuna_acceleration_answer_sent; accepted=1; payer_decision_id=5; generation=9; log_writer_id=responder',
                    'event=tuna_acceleration_answer_ack_received; session_id=sess; payer_decision_id=5; pending_answer_ack_generation=9; authority_mode=none; log_writer_id=responder',
                    'event=runtime_unlock_precommit_probe_ack_sent; direction=inbound; session_id=sess; transfer_id=transfer; transaction_generation=1; offer_generation=2; tuna_path_lease_generation=3; probe_id=probe; received_transport=tuna; log_writer_id=responder',
                    'event=runtime_unlock_peer_precommit_answer_acceptance_decided; status=Rejected; decision_status=Rejected; direction=inbound; session_id=sess; transfer_id=transfer; peer_transaction_generation=1; peer_offer_generation=2; peer_tuna_path_lease_generation=3; probe_id=probe; received_transport=tuna; responder_transition_mode=post_fallback_v6_paired; decision_reason=runtime_unlock_peer_precommit_answer_acceptance_scope_mismatch; log_writer_id=responder'
                ) | Set-Content -LiteralPath $path
                $result = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'Gui_Smoke_Scenario_Deadline_Exceeded' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($result.Phase -ne 'route_runtime' -or
                    $result.Reason -ne 'post_fallback_paired_retry_offer_not_bound_before_peer_probe_acceptance' -or
                    -not $result.RuntimeUnlockResponderPeerProbeHandoffMissing -or
                    $result.FailureVariant -ne 'runtime_unlock_responder_acked_probe_post_fallback_peer_acceptance_probe_mismatch' -or
                    $result.ClassificationSource -ne 'immutable_evidence_paired_retry_responder_binding_index') {
                    throw "paired-retry responder binding fast path mismatch: $($result | ConvertTo-Json -Compress)"
                }
                Write-Output 'gui-paired-retry-binding-fast-path-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(result.ExitCode == 0, $"Paired-retry responder-binding GUI fast path failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-paired-retry-binding-fast-path-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeFailureClassification_InitialResponderPeerAcceptanceScopeRejectionUsesEarliestExactStage()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var resultBody = ExtractPowerShellFunctionBody(guiText, "New-TunaGuiFileTransferFailureClassificationResult", "Get-TunaGuiExplicitFailureClassification");
        var explicitBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiExplicitFailureClassification", "Resolve-TunaGuiFailureClassification");
        var classifierBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiFileTransferSetupFailureClassification", "Get-TunaGuiFailureClassificationBudgetMilliseconds");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function New-TunaGuiFileTransferFailureClassificationResult { {{resultBody}} }
            function Get-TunaGuiExplicitFailureClassification { {{explicitBody}} }
            function Get-TunaGuiFileTransferSetupFailureClassification { {{classifierBody}} }
            function Get-AppLogLinesAfterBookmark { throw 'live ledger was read' }
            function Read-GuiSmokeProcessOutputLinesSnapshot { throw 'process output was read' }
            function Test-TunaGuiBridgeBootstrapNotReadyEvidence { return $false }
            function Test-TunaGuiLatestFallbackFrontierRequestDispatchMissing { return $false }
            function Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing { return $false }
            function Test-TunaGuiLogLinesContain { return $false }
            function Get-TunaGuiReadinessStateAfterBookmark {
                return [pscustomobject]@{ ListenerUnavailable = $false; ListenerReady = $false; TunaActive = $false }
            }
            function Resolve-TunaGuiFailureClassification {
                param($ErrorMessage, $HistoricalPhase, $HistoricalReason)
                return [pscustomobject]@{ Phase = $HistoricalPhase; Reason = $HistoricalReason }
            }
            function Assert-InitialScopeFailure {
                param($Result, [string]$Case)
                if ($Result.Phase -ne 'route_runtime' -or
                    $Result.Reason -ne 'initial_activation_zero_transport_epoch_peer_acceptance_scope_rejected_before_responder_convergence' -or
                    -not $Result.RuntimeUnlockResponderPeerProbeHandoffMissing -or
                    -not $Result.RuntimeUnlockResponderPeerProbeInitialActivationPeerAcceptanceScopeRejected -or
                    $Result.RuntimeUnlockResponderPeerProbeBindingMissing -or
                    $Result.RuntimeUnlockResponderPeerProbeAdoptionRecheckMissingAfterAnswerAckBeforeProbe -or
                    $Result.RuntimeUnlockResponderPeerProbeRouteTransitionHandoffMissingAfterBoundProbe -or
                    $Result.FailureVariant -ne 'runtime_unlock_responder_acked_probe_initial_activation_peer_acceptance_scope_rejected' -or
                    $Result.ClassificationSource -ne 'immutable_evidence_responder_peer_probe_index') {
                    throw "$Case did not identify the initial responder peer-acceptance scope edge: $($Result | ConvertTo-Json -Compress)"
                }
            }
            function Assert-ResponderStageFailure {
                param(
                    $Result,
                    [string]$Case,
                    [string]$ExpectedStage,
                    [string]$ExpectedReason,
                    [string]$ExpectedVariant)
                $expectedAdoption = $ExpectedStage -eq 'adoption'
                $expectedBinding = $ExpectedStage -eq 'binding'
                $expectedAfterBound = $ExpectedStage -eq 'after_bound'
                if ($Result.Phase -ne 'route_runtime' -or
                    $Result.Reason -ne $ExpectedReason -or
                    -not $Result.RuntimeUnlockResponderPeerProbeHandoffMissing -or
                    $Result.RuntimeUnlockResponderPeerProbeInitialActivationPeerAcceptanceScopeRejected -or
                    ([bool]$Result.RuntimeUnlockResponderPeerProbeAdoptionRecheckMissingAfterAnswerAckBeforeProbe -ne $expectedAdoption) -or
                    ([bool]$Result.RuntimeUnlockResponderPeerProbeBindingMissing -ne $expectedBinding) -or
                    ([bool]$Result.RuntimeUnlockResponderPeerProbeRouteTransitionHandoffMissingAfterBoundProbe -ne $expectedAfterBound) -or
                    $Result.FailureVariant -ne $ExpectedVariant -or
                    $Result.ClassificationSource -ne 'immutable_evidence_responder_peer_probe_index') {
                    throw "$Case did not identify the exclusive $ExpectedStage responder stage: $($Result | ConvertTo-Json -Compress)"
                }
            }
            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-initial-peer-scope-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $path = Join-Path $tempRoot 'evidence.log'
                $initialScope = 'runtime_owner_session_id=sess; runtime_owner_transfer_id=transfer; runtime_owner_direction=inbound; runtime_owner_route=regularnknv4fast; runtime_owner_route_token=regular_nkn_v4_fast; runtime_owner_protocol_version=4; runtime_owner_live_route_epoch=0; runtime_generation=1; runtime_owner_terminal_generation=0; route_publication_session_id=sess; route_publication_transfer_id=transfer; route_publication_direction=inbound; route_publication_route=regularnknv4fast; route_publication_route_token=regular_nkn_v4_fast; route_publication_protocol_version=4; route_publication_live_route_epoch=0; route_publication_runtime_generation=1; route_publication_terminal_generation=0; route_publication_leg_generation=1; route_publication_commit_generation=0; route_publication_generation=1; terminal_generation=0; leg_generation=1; transport_epoch=0; bridge_recovery_generation=0; checkpoint_request_id=(none); checkpoint_generation=0; data_session_identity=data-session; data_session_generation=1'
                $prefix = @(
                    'event=runtime_unlock_transaction_answer_received; session_id=sess; transaction_generation=1; offer_generation=6; answer_received=1; tuna_path_lease_generation=1; tuna_path_lease_current=1; tuna_path_lease_listener_run_id=peer-offer-dialer-1; log_writer_id=responder',
                    'event=tuna_acceleration_answer_ack_pending; session_id=sess; payer_decision_id=4; generation=6; log_writer_id=responder',
                    'event=tuna_acceleration_answer_sent; accepted=1; lanes=file; payer_decision_id=4; generation=6; msg_id=answer-message; reply_deadline_utc_ms=1200; log_writer_id=responder',
                    'event=tuna_acceleration_answer_ack_received; session_id=sess; lanes=file; payer_decision_id=4; ack_message_id=answer-ack; ack_correlation_id=ack-correlation; ack_frame_fingerprint=ack-frame; ack_source_fingerprint=ack-fingerprint; pending_answer_ack_generation=6; authority_mode=InitialActivation; log_writer_id=responder',
                    'event=runtime_unlock_responder_answer_probe_convergence_decided; status=Held; decision_status=Held; substate=AwaitingPeerProbe; decision_reason=semantic_answer_ack_retained_waiting_for_peer_probe; direction=inbound; session_id=sess; transfer_id=transfer; transaction_generation=1; offer_generation=6; pending_answer_ack_generation=6; payer_decision_id=4; lanes=file; authority_mode=InitialActivation; nonce=ack-correlation; answer_message_id=answer-message; answer_deadline_utc_ms=1200; tuna_path_lease_generation=1; tuna_path_lease_listener_run_id=peer-offer-dialer-1; responder_transition_mode=initial_regular_v4; ack_message_id=answer-ack; ack_source_fingerprint=ack-fingerprint; ack_frame_fingerprint=ack-frame; peer_transaction_generation=0; peer_offer_generation=0; probe_state=None; probe_transport=unknown; probe_acked_utc_ms=0; launch_generation=0; log_writer_id=responder',
                    'event=runtime_unlock_answer_ack_probe_barrier_received; session_id=sess; lanes=file; payer_decision_id=4; reason=answer_ack_received_before_tuna_probe; log_writer_id=responder',
                    'event=filetransfer_runtime_unlock_handoff_pending_route_commit; session_id=sess; reason=runtime_unlock_answer_ack_probe_ready; lanes=file; transaction_generation=1; offer_generation=6; state=answerreceived; path_probe_state=none; route_commit_pending=0; route_committed=0; log_writer_id=responder',
                    'event=runtime_unlock_precommit_probe_launch_requested; session_id=sess; transfer_id=transfer; lanes=file; transaction_generation=1; offer_generation=6; launch_generation=1; tuna_path_lease_generation=1; tuna_path_lease_listener_run_id=peer-offer-dialer-1; path_probe_id=none; path_probe_state=none; duplicate=0; tuple_mutation=0; responder_waiting_for_peer_probe=1; launch_role=ResponderWaitingForPeerProbe; peer_probe_adoption=0; log_writer_id=responder',
                    'event=runtime_unlock_precommit_probe_receive_dispatched; direction=inbound; transfer_id=transfer; session_id=sess; frame_type=filetransfer.runtime_unlock.precommit_probe; transaction_generation=1; offer_generation=1; tuna_path_lease_generation=1; probe_id=peer-probe; actual_transport=tuna; delivery_transport=tuna; lane=bulk; priority=1; queued_frames=0; queued_bytes=0; decision_reason=dispatched; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation=1; route_publication_generation=1; route_commit_generation=0; terminal_generation=0; leg_generation=1; log_writer_id=responder',
                    'event=runtime_unlock_tuna_probe_received; transport=tuna; direction=inbound; transfer_id=transfer; session_id=sess; transaction_generation=1; offer_generation=1; tuna_path_lease_generation=1; probe_id=peer-probe; target_transport=tuna; log_writer_id=responder',
                    "event=filetransfer_runtime_unlock_admission_granted; session_id=sess; transfer_id=transfer; direction=inbound; source=precommit_probe_received; admission_generation=1; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; $initialScope; log_writer_id=responder",
                    'event=runtime_unlock_precommit_probe_ack_delivery_result; session_id=sess; transfer_id=transfer; frame_type=filetransfer.runtime_unlock.precommit_probe_ack; transaction_generation=1; offer_generation=1; tuna_path_lease_generation=1; probe_id=peer-probe; ack_authority_source=runtime_unlock_precommit_ack_authority; ack_authority_generation=1; intended_transport=tuna; actual_transport=tuna; lane=bulk; payload_bytes=512; delivery_result=sent; rejection_reason=(none); log_writer_id=responder',
                    'event=runtime_unlock_precommit_probe_ack_sent; direction=inbound; transfer_id=transfer; session_id=sess; transaction_generation=1; offer_generation=1; tuna_path_lease_generation=1; probe_id=peer-probe; received_transport=tuna; log_writer_id=responder',
                    'event=runtime_unlock_peer_precommit_answer_acceptance_observed; status=Rejected; direction=inbound; transfer_id=transfer; session_id=sess; peer_transaction_generation=1; peer_offer_generation=1; peer_tuna_path_lease_generation=1; probe_id=peer-probe; decision_reason=current; log_writer_id=responder',
                    'event=runtime_unlock_tuna_probe_ack_received; transport=tuna; direction=outbound; transfer_id=transfer; session_id=sess; transaction_generation=1; offer_generation=1; tuna_path_lease_generation=1; probe_id=peer-probe; accepted=1; reason=precommit_probe_commit_confirmed; log_writer_id=initiator',
                    'event=runtime_unlock_transaction_path_probe_acked; session_id=sess; transaction_generation=1; offer_generation=1; path_probe_id=peer-probe; path_probe_state=acked; path_probe_transport=tuna; path_probe_acked_utc_ms=1300; tuna_path_lease_generation=1; log_writer_id=initiator',
                    'event=runtime_unlock_precommit_probe_acked; direction=outbound; transfer_id=transfer; session_id=sess; transaction_generation=1; offer_generation=1; launch_generation=1; tuna_path_lease_generation=1; probe_id=peer-probe; probe_state=Acked; received_transport=tuna; log_writer_id=initiator',
                    'event=nkn_inbound_envelope_received; envelope_type=TransportAccelerationAnswerAck; source_matches_expected=1; msg_id=answer-ack; ack_correlation_id=ack-correlation; ack_frame_fingerprint=ack-frame; ack_source_fingerprint=ack-fingerprint; log_writer_id=responder',
                    'event=tuna_acceleration_answer_ack_received_raw; msg_id=answer-ack; ack_correlation_id=ack-correlation; ack_frame_fingerprint=ack-frame; ack_source_fingerprint=ack-fingerprint; session_id=sess; payer_decision_id=4; log_writer_id=responder'
                )
                $prefix | Set-Content -LiteralPath $path
                $result = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'evidence_enrichment_timeout' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                Assert-InitialScopeFailure -Result $result -Case 'historical epoch-zero scope rejection'

                foreach ($requiredIngressEvent in @(
                        'runtime_unlock_precommit_probe_receive_dispatched',
                        'runtime_unlock_precommit_probe_ack_delivery_result')) {
                    @($prefix | Where-Object { $_ -notlike "event=$requiredIngressEvent;*" }) |
                        Set-Content -LiteralPath $path
                    $missingIngress = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'evidence_enrichment_timeout' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                    if ($missingIngress.RuntimeUnlockResponderPeerProbeInitialActivationPeerAcceptanceScopeRejected) {
                        throw "missing $requiredIngressEvent was classified: $($missingIngress | ConvertTo-Json -Compress)"
                    }
                }

                $ingressMutations = [ordered]@{
                    runtime_unlock_precommit_probe_receive_dispatched = @('live_route_epoch=0', 'live_route_epoch=9')
                    runtime_unlock_precommit_probe_ack_delivery_result = @('actual_transport=tuna', 'actual_transport=nkn')
                }
                foreach ($ingressMutation in $ingressMutations.GetEnumerator()) {
                    $mutatedIngress = @($prefix | ForEach-Object {
                            $_
                            if ($_ -like "event=$($ingressMutation.Key);*") {
                                $_.Replace($ingressMutation.Value[0], $ingressMutation.Value[1])
                            }
                        })
                    $mutatedIngress | Set-Content -LiteralPath $path
                    $mutatedIngressResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'evidence_enrichment_timeout' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                    if ($mutatedIngressResult.RuntimeUnlockResponderPeerProbeInitialActivationPeerAcceptanceScopeRejected) {
                        throw "mutated duplicate $($ingressMutation.Key) was classified: $($mutatedIngressResult | ConvertTo-Json -Compress)"
                    }
                }

                @($prefix | Where-Object { $_ -notlike 'event=runtime_unlock_precommit_probe_acked; direction=outbound*' }) |
                    Set-Content -LiteralPath $path
                $uncorroborated = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'evidence_enrichment_timeout' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($uncorroborated.RuntimeUnlockResponderPeerProbeInitialActivationPeerAcceptanceScopeRejected) {
                    throw "historical current without the peer ACKed callback was classified: $($uncorroborated | ConvertTo-Json -Compress)"
                }

                $modern = @($prefix | ForEach-Object {
                    if ($_ -like 'event=runtime_unlock_peer_precommit_answer_acceptance_observed;*') {
                        $_ -replace 'decision_reason=current', 'decision_reason=runtime_unlock_peer_precommit_answer_acceptance_scope_mode_invalid; responder_transition_mode=initial_regular_v4'
                    }
                    else { $_ }
                })
                $modern | Set-Content -LiteralPath $path
                $modernResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'failure_class=regular_v4_frontier_repair_settlement_disposition_unverifiable' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                Assert-InitialScopeFailure -Result $modernResult -Case 'modern typed scope rejection before later fallback'

                foreach ($peerAckEvent in @(
                        'runtime_unlock_tuna_probe_ack_received',
                        'runtime_unlock_transaction_path_probe_acked',
                        'runtime_unlock_precommit_probe_acked')) {
                    @($modern | Where-Object { $_ -notlike "event=$peerAckEvent;*" }) |
                        Set-Content -LiteralPath $path
                    $missingPeerAck = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'evidence_enrichment_timeout' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                    if ($missingPeerAck.RuntimeUnlockResponderPeerProbeInitialActivationPeerAcceptanceScopeRejected) {
                        throw "modern typed rejection without $peerAckEvent was classified: $($missingPeerAck | ConvertTo-Json -Compress)"
                    }
                    $peerAckLine = @($prefix | Where-Object {
                            $_ -like "event=$peerAckEvent;*"
                        } | Select-Object -First 1)[0]
                    @($prefix) + @($peerAckLine) | Set-Content -LiteralPath $path
                    Assert-InitialScopeFailure `
                        -Result (Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'evidence_enrichment_timeout' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)) `
                        -Case "exact duplicate $peerAckEvent corroboration"
                }

                $peerAckMutations = [ordered]@{
                    runtime_unlock_tuna_probe_ack_received = @('accepted=1', 'accepted=0')
                    runtime_unlock_transaction_path_probe_acked = @('path_probe_acked_utc_ms=1300', 'path_probe_acked_utc_ms=1301')
                    runtime_unlock_precommit_probe_acked = @('transfer_id=transfer', 'transfer_id=mutated-transfer')
                }
                foreach ($peerAckMutation in $peerAckMutations.GetEnumerator()) {
                    $peerAckLine = @($prefix | Where-Object {
                            $_ -like "event=$($peerAckMutation.Key);*"
                        } | Select-Object -First 1)[0]
                    $mutatedPeerAck = $peerAckLine.Replace(
                        [string]$peerAckMutation.Value[0], [string]$peerAckMutation.Value[1])
                    @($prefix) + @($mutatedPeerAck) | Set-Content -LiteralPath $path
                    $mutatedPeerAckResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'evidence_enrichment_timeout' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                    if ($mutatedPeerAckResult.RuntimeUnlockResponderPeerProbeHandoffMissing) {
                        throw "mutated $($peerAckMutation.Key) corroboration retained responder evidence: $($mutatedPeerAckResult | ConvertTo-Json -Compress)"
                    }
                }

                $replacedScope = @($prefix | ForEach-Object {
                    $_
                    if ($_ -like 'event=filetransfer_runtime_unlock_admission_granted;*') {
                        'event=filetransfer_runtime_owner_changed; direction=inbound; session_id=sess; transfer_id=transfer; route=file_tuna_v4; protocol_version=4; live_route_epoch=1; runtime_generation=2; route_publication_generation=2; terminal_generation=0; leg_generation=2; transport_epoch=0; bridge_recovery_generation=0; checkpoint_request_id=(none); checkpoint_generation=0; data_session_identity=data-session; data_session_generation=1; log_writer_id=responder'
                    }
                })
                $replacedScope | Set-Content -LiteralPath $path
                $replacedResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'evidence_enrichment_timeout' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($replacedResult.RuntimeUnlockResponderPeerProbeInitialActivationPeerAcceptanceScopeRejected) {
                    throw "replaced initial scope was classified: $($replacedResult | ConvertTo-Json -Compress)"
                }

                $initialPeerScope = 'peer_runtime_owner_session_id=sess; peer_runtime_owner_transfer_id=transfer; peer_runtime_owner_direction=inbound; peer_runtime_owner_route=regularnknv4fast; peer_runtime_owner_route_token=regular_nkn_v4_fast; peer_runtime_owner_protocol_version=4; peer_runtime_owner_live_route_epoch=0; peer_runtime_generation=1; peer_runtime_owner_terminal_generation=0; peer_route_publication_session_id=sess; peer_route_publication_transfer_id=transfer; peer_route_publication_direction=inbound; peer_route_publication_route=regularnknv4fast; peer_route_publication_route_token=regular_nkn_v4_fast; peer_route_publication_protocol_version=4; peer_route_publication_live_route_epoch=0; peer_route_publication_runtime_generation=1; peer_route_publication_terminal_generation=0; peer_route_publication_leg_generation=1; peer_route_publication_commit_generation=0; peer_route_publication_generation=1; peer_terminal_generation=0; peer_leg_generation=1; peer_transport_epoch=0; peer_bridge_recovery_generation=0; peer_checkpoint_request_id=(none); peer_checkpoint_generation=0; peer_data_session_identity=data-session; peer_data_session_generation=1'
                $initialTransitionScope = 'session_id=sess; transfer_id=transfer; direction=inbound; transaction_generation=1; offer_generation=6; launch_generation=1; tuna_path_lease_generation=1; responder_transition_mode=initial_regular_v4; source_route=regular_nkn_v4_fast; source_protocol_version=4; source_transport=regular_nkn; source_live_route_epoch=0; source_runtime_generation=1; source_route_publication_generation=1; source_route_commit_generation=0; source_terminal_generation=0; source_leg_generation=1; source_transport_epoch=0; source_bridge_recovery_generation=0; source_data_session_generation=1; data_session_identity=data-session; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; activation_authority_mode=initial_responder; retry_install_generation=0; admission_generation=1; pending_precommit_generation=1; responder_handoff_generation=0; fallback_yield_generation=0; pairing_generation=0; checkpoint_generation=1; checkpoint_request_id=(none)'
                $exactRecovery = @(
                    "event=runtime_unlock_peer_precommit_answer_acceptance_decided; status=Held; decision_status=Held; session_id=sess; transfer_id=transfer; direction=inbound; peer_transaction_generation=1; peer_offer_generation=1; peer_tuna_path_lease_generation=1; local_transaction_generation=1; local_offer_generation=6; local_tuna_path_lease_generation=1; pending_answer_ack_generation=6; launch_generation=1; probe_id=peer-probe; probe_frame_fingerprint=probe-fingerprint; received_transport=tuna; received_utc_ms=1250; probe_ack_sent_utc_ms=1275; peer_admission_generation=1; $initialPeerScope; data_session_generation=1; responder_transition_mode=initial_regular_v4; decision_reason=runtime_unlock_peer_precommit_probe_ack_pending; log_writer_id=responder",
                    "event=runtime_unlock_responder_answer_probe_convergence_decided; status=Held; decision_status=Held; substate=AwaitingProbeAck; decision_reason=semantic_answer_ack_joined_waiting_for_probe_ack; direction=inbound; session_id=sess; transfer_id=transfer; transaction_generation=1; offer_generation=6; pending_answer_ack_generation=6; payer_decision_id=4; lanes=file; authority_mode=InitialActivation; nonce=ack-correlation; answer_message_id=answer-message; answer_deadline_utc_ms=1200; tuna_path_lease_generation=1; tuna_path_lease_listener_run_id=peer-offer-dialer-1; responder_transition_mode=initial_regular_v4; ack_message_id=answer-ack; ack_source_fingerprint=ack-fingerprint; ack_frame_fingerprint=ack-frame; peer_transaction_generation=1; peer_offer_generation=1; peer_tuna_path_lease_generation=1; probe_id=peer-probe; peer_probe_frame_fingerprint=probe-fingerprint; received_transport=tuna; received_utc_ms=1250; probe_ack_sent_utc_ms=1275; peer_admission_generation=1; $initialPeerScope; data_session_generation=1; probe_state=Started; probe_transport=tuna; probe_acked_utc_ms=0; launch_generation=1; log_writer_id=responder",
                    'event=runtime_unlock_transaction_path_probe_acked; session_id=sess; transaction_generation=1; offer_generation=6; path_probe_id=peer-probe; path_probe_state=acked; path_probe_transport=tuna; path_probe_acked_utc_ms=1300; tuna_path_lease_generation=1; log_writer_id=responder',
                    "event=runtime_unlock_responder_answer_probe_convergence_decided; status=Accepted; decision_status=Accepted; substate=Complete; decision_reason=runtime_unlock_responder_answer_probe_completed; direction=inbound; session_id=sess; transfer_id=transfer; transaction_generation=1; offer_generation=6; pending_answer_ack_generation=6; payer_decision_id=4; lanes=file; authority_mode=InitialActivation; nonce=ack-correlation; answer_message_id=answer-message; answer_deadline_utc_ms=1200; tuna_path_lease_generation=1; tuna_path_lease_listener_run_id=peer-offer-dialer-1; responder_transition_mode=initial_regular_v4; ack_message_id=answer-ack; ack_source_fingerprint=ack-fingerprint; ack_frame_fingerprint=ack-frame; peer_transaction_generation=1; peer_offer_generation=1; peer_tuna_path_lease_generation=1; probe_id=peer-probe; peer_probe_frame_fingerprint=probe-fingerprint; received_transport=tuna; received_utc_ms=1250; probe_ack_sent_utc_ms=1275; peer_admission_generation=1; $initialPeerScope; data_session_generation=1; probe_state=Acked; probe_transport=tuna; probe_acked_utc_ms=1300; launch_generation=1; log_writer_id=responder",
                    'event=runtime_unlock_paired_commit_confirmed; direction=inbound; transfer_id=transfer; session_id=sess; transaction_generation=1; offer_generation=1; tuna_path_lease_generation=1; probe_id=peer-probe; reason=precommit_probe_commit_confirmed; log_writer_id=responder',
                    'event=runtime_unlock_precommit_probe_acked; direction=inbound; transfer_id=transfer; session_id=sess; transaction_generation=1; offer_generation=1; launch_generation=1; tuna_path_lease_generation=1; probe_id=peer-probe; probe_state=Acked; received_transport=tuna; log_writer_id=responder',
                    'event=runtime_unlock_precommit_probe_launch_requested; session_id=sess; transfer_id=transfer; transaction_generation=1; offer_generation=6; launch_generation=1; tuna_path_lease_generation=1; tuna_path_lease_listener_run_id=peer-offer-dialer-1; path_probe_id=peer-probe; path_probe_state=acked; duplicate=0; tuple_mutation=0; responder_waiting_for_peer_probe=0; launch_role=ResponderAdoptingAckedPeerProbe; peer_probe_adoption=1; peer_transaction_generation=1; peer_offer_generation=1; peer_tuna_path_lease_generation=1; log_writer_id=responder',
                    "event=runtime_unlock_precommit_responder_peer_probe_handoff_bound; status=Accepted; decision_status=Accepted; direction=inbound; session_id=sess; transfer_id=transfer; transaction_generation=1; offer_generation=6; launch_generation=1; tuna_path_lease_generation=1; tuna_path_lease_listener_run_id=peer-offer-dialer-1; probe_id=peer-probe; probe_state=Acked; probe_transport=tuna; probe_acked_utc_ms=1300; ack_message_id=answer-ack; ack_source_fingerprint=ack-fingerprint; peer_probe_frame_fingerprint=probe-fingerprint; responder_transition_mode=initial_regular_v4; peer_transaction_generation=1; peer_offer_generation=1; peer_tuna_path_lease_generation=1; peer_admission_generation=1; $initialPeerScope; launch_role=ResponderAdoptingAckedPeerProbe; answer_acceptance_source=SemanticAnswerAck; normalized_reason=exact_responder_peer_probe_adopted; log_writer_id=responder",
                    'event=filetransfer_runtime_unlock_responder_peer_probe_route_transition_handoff_decided; status=Accepted; decision_status=Accepted; decision_reason=runtime_unlock_responder_peer_probe_route_transition_handoff_accepted; direction=inbound; session_id=sess; transfer_id=transfer; transaction_generation=1; offer_generation=6; launch_generation=1; tuna_path_lease_generation=1; tuna_path_lease_listener_run_id=peer-offer-dialer-1; lanes=file; payer_decision_id=4; pending_answer_ack_generation=6; answer_acceptance_source=SemanticAnswerAck; ack_message_id=answer-ack; ack_source_fingerprint=ack-fingerprint; peer_probe_frame_fingerprint=probe-fingerprint; probe_id=peer-probe; probe_state=Acked; probe_transport=tuna; probe_acked_utc_ms=1300; transition_authority_id=(none); transition_generation=0; responder_transition_mode=initial_regular_v4; activation_authority_mode=initial_responder; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation=1; route_publication_generation=1; route_commit_generation=0; terminal_generation=0; leg_generation=1; transport_epoch=0; bridge_recovery_generation=0; checkpoint_request_id=(none); checkpoint_generation=0; data_session_identity=data-session; data_session_generation=1; admission_generation=1; pending_precommit_generation=1; fallback_yield_authority_id=(none); fallback_yield_generation=0; pairing_generation=0; pair_key=(none); responder_handoff_authority_id=(none); responder_handoff_generation=0; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; log_writer_id=responder',
                    "event=filetransfer_route_transition_prepared; transition_kind=RegularV4ToTunaV4; authority_id=transition-1; transition_generation=1; $initialTransitionScope; log_writer_id=responder",
                    "event=filetransfer_route_transition_pause_bound; transition_kind=RegularV4ToTunaV4; authority_id=transition-1; transition_generation=1; decision_status=Installed; $initialTransitionScope; log_writer_id=responder",
                    "event=filetransfer_route_transition_draining; transition_kind=RegularV4ToTunaV4; authority_id=transition-1; transition_generation=1; $initialTransitionScope; log_writer_id=responder",
                    "event=filetransfer_route_transition_drain_receipt_decided; transition_kind=RegularV4ToTunaV4; authority_id=transition-1; transition_generation=1; decision_status=Accepted; drain_outcome=Completed; $initialTransitionScope; log_writer_id=responder",
                    "event=filetransfer_route_transition_committed; transition_kind=RegularV4ToTunaV4; authority_id=transition-1; transition_generation=1; $initialTransitionScope; log_writer_id=responder",
                    "event=filetransfer_route_transition_disposition_applied; transition_kind=RegularV4ToTunaV4; authority_id=transition-1; transition_generation=1; apply_status=Applied; resulting_live_route_epoch=1; resulting_runtime_generation=2; resulting_route_publication_generation=2; resulting_route_commit_generation=1; resulting_terminal_generation=0; resulting_leg_generation=2; $initialTransitionScope; log_writer_id=responder",
                    'event=filetransfer_live_route_epoch_recovered; session_id=sess; transfer_id=transfer; direction=inbound; transition_authority_id=transition-1; transition_generation=1; route=file_tuna_v4; protocol_version=4; live_route_epoch=1; runtime_generation=2; route_publication_generation=2; route_commit_generation=1; terminal_generation=0; leg_generation=2; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; route_publication_action_kind=RouteCommitPublication; coordinator_decision_reason=filetransfer_route_transition_committed; projection_source=coordinator_route_transition; log_writer_id=responder'
                )
                $missingConvergenceChains = [ordered]@{
                    awaiting_peer_probe = @($prefix | Where-Object {
                            $_ -notlike '*substate=AwaitingPeerProbe*'
                        }) + @($exactRecovery)
                    awaiting_probe_ack = @($prefix) + @($exactRecovery | Where-Object {
                            $_ -notlike '*substate=AwaitingProbeAck*'
                        })
                    paired_commit_confirmation = @($prefix) + @($exactRecovery | Where-Object {
                            $_ -notlike 'event=runtime_unlock_paired_commit_confirmed;*'
                        })
                }
                foreach ($missingConvergenceChain in $missingConvergenceChains.GetEnumerator()) {
                    @($missingConvergenceChain.Value) | Set-Content -LiteralPath $path
                    Assert-InitialScopeFailure `
                        -Result (Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'evidence_enrichment_timeout' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)) `
                        -Case "missing $($missingConvergenceChain.Key) convergence proof"
                }

                $awaitingPeerProbeHold = @($prefix | Where-Object {
                        $_ -like '*substate=AwaitingPeerProbe*'
                    } | Select-Object -First 1)[0]
                @($prefix) + @($exactRecovery) + @(
                    $awaitingPeerProbeHold,
                    $exactRecovery[1],
                    $exactRecovery[4]) | Set-Content -LiteralPath $path
                $duplicateConvergenceResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'evidence_enrichment_timeout' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($duplicateConvergenceResult.RuntimeUnlockResponderPeerProbeHandoffMissing) {
                    throw "exact duplicate convergence callbacks remained unresolved: $($duplicateConvergenceResult | ConvertTo-Json -Compress)"
                }

                $convergenceMutations = @(
                    $awaitingPeerProbeHold.Replace('launch_generation=0', 'launch_generation=9'),
                    $exactRecovery[1].Replace(
                        'peer_probe_frame_fingerprint=probe-fingerprint',
                        'peer_probe_frame_fingerprint=mutated-probe-fingerprint'),
                    $exactRecovery[4].Replace('offer_generation=1', 'offer_generation=9'))
                foreach ($convergenceMutation in $convergenceMutations) {
                    @($prefix) + @($exactRecovery) + @($convergenceMutation) |
                        Set-Content -LiteralPath $path
                    $mutatedConvergenceResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'evidence_enrichment_timeout' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                    if ($mutatedConvergenceResult.RuntimeUnlockResponderPeerProbeHandoffMissing) {
                        throw "mutated convergence callback retained responder evidence: $($mutatedConvergenceResult | ConvertTo-Json -Compress)"
                    }
                }

                $scopeInvalidators = @(
                    'event=filetransfer_route_hint_authorized_publication_accepted; direction=inbound; session_id=sess; transfer_id=transfer; route=file_tuna_v4; protocol_version=4; live_route_epoch=1; route_publication_generation=9; runtime_generation=2; terminal_generation=0; leg_generation=2; log_writer_id=responder',
                    'event=filetransfer_terminalized_by_peer_down; direction=inbound; session_id=sess; transfer_id=transfer; reason=peer_down; terminal_state=failed; log_writer_id=responder',
                    'event=filetransfer_live_route_epoch_terminal; direction=inbound; session_id=sess; transfer_id=transfer; live_route_epoch=0; route=regular_nkn_v4_fast; protocol_version=4; terminal_state=failed; reason=route_terminal; log_writer_id=responder',
                    'event=filetransfer_v6_epoch_terminal; direction=inbound; session_id=sess; transfer_id=transfer; transport_epoch=0; reason=v6_terminal; log_writer_id=responder')
                foreach ($scopeInvalidator in $scopeInvalidators) {
                    @($prefix) + @($scopeInvalidator) | Set-Content -LiteralPath $path
                    $invalidatedScopeResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'evidence_enrichment_timeout' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                    if ($invalidatedScopeResult.RuntimeUnlockResponderPeerProbeHandoffMissing) {
                        throw "terminal or publication scope replacement retained responder evidence: $($invalidatedScopeResult | ConvertTo-Json -Compress)"
                    }
                }

                $stageCases = @(
                    [pscustomobject]@{
                        Name = 'exact ACKed convergence without adopting recheck'
                        LastRecoveryIndex = 5
                        Stage = 'adoption'
                        Reason = 'answer_ack_before_probe_acked_peer_probe_not_rechecked_into_responder_adopting_launch'
                        Variant = 'runtime_unlock_responder_acked_probe_adoption_recheck_missing_after_answer_ack_before_probe'
                    },
                    [pscustomobject]@{
                        Name = 'exact adopting recheck without accepted binding'
                        LastRecoveryIndex = 6
                        Stage = 'binding'
                        Reason = 'late_answer_ack_did_not_bind_acked_peer_probe_to_responder_route_transition'
                        Variant = 'runtime_unlock_responder_acked_probe_binding_missing'
                    },
                    [pscustomobject]@{
                        Name = 'accepted binding without current handoff transition'
                        LastRecoveryIndex = 7
                        Stage = 'after_bound'
                        Reason = 'bound_responder_acked_peer_probe_route_transition_handoff_invalidated'
                        Variant = 'runtime_unlock_responder_acked_probe_route_transition_handoff_missing_after_bound_probe'
                    }
                )
                foreach ($stageCase in $stageCases) {
                    @($prefix) + @($exactRecovery[0..([int]$stageCase.LastRecoveryIndex)]) |
                        Set-Content -LiteralPath $path
                    Assert-ResponderStageFailure `
                        -Result (Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'evidence_enrichment_timeout' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)) `
                        -Case $stageCase.Name `
                        -ExpectedStage $stageCase.Stage `
                        -ExpectedReason $stageCase.Reason `
                        -ExpectedVariant $stageCase.Variant
                }

                @($prefix) + @($exactRecovery) | Set-Content -LiteralPath $path
                $recoveredResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'evidence_enrichment_timeout' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($recoveredResult.RuntimeUnlockResponderPeerProbeHandoffMissing) {
                    throw "exact initial responder recovery remained unresolved: $($recoveredResult | ConvertTo-Json -Compress)"
                }

                $mutatedPeerProbe = @($prefix | Where-Object {
                        $_ -like 'event=runtime_unlock_tuna_probe_received;*'
                    } | Select-Object -First 1)[0].Replace('offer_generation=1', 'offer_generation=9')
                @($prefix) + @($mutatedPeerProbe) + @($exactRecovery) | Set-Content -LiteralPath $path
                $mutatedThenRecovered = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'evidence_enrichment_timeout' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                Assert-InitialScopeFailure -Result $mutatedThenRecovered -Case 'mutated duplicate before otherwise exact recovery'

                @($prefix) + @($prefix[3]) | Set-Content -LiteralPath $path
                Assert-InitialScopeFailure `
                    -Result (Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'evidence_enrichment_timeout' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)) `
                    -Case 'exact duplicate initial semantic ACK remains idempotent'

                foreach ($semanticMutation in @(
                        @('ack_message_id=answer-ack', 'ack_message_id=mutated-answer-ack'),
                        @('ack_correlation_id=ack-correlation', 'ack_correlation_id=mutated-correlation'),
                        @('payer_decision_id=4', 'payer_decision_id=9'),
                        @('pending_answer_ack_generation=6', 'pending_answer_ack_generation=9'),
                        @('lanes=file', 'lanes=screen'))) {
                    $mutatedSemanticAck = $prefix[3].Replace(
                        [string]$semanticMutation[0], [string]$semanticMutation[1])
                    @($prefix) + @($mutatedSemanticAck) | Set-Content -LiteralPath $path
                    $mutatedSemanticResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'evidence_enrichment_timeout' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                    if ($mutatedSemanticResult.RuntimeUnlockResponderPeerProbeHandoffMissing) {
                        throw "same-lineage initial semantic ACK mutation retained responder evidence ($($semanticMutation[0])): $($mutatedSemanticResult | ConvertTo-Json -Compress)"
                    }
                }
                Write-Output 'gui-initial-peer-acceptance-scope-rejection-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(result.ExitCode == 0, $"Initial responder scope GUI fast path failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-initial-peer-acceptance-scope-rejection-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeFailureClassification_AnswerAckBeforeProbeUsesAdoptionRecheckStage()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var resultBody = ExtractPowerShellFunctionBody(guiText, "New-TunaGuiFileTransferFailureClassificationResult", "Get-TunaGuiExplicitFailureClassification");
        var explicitBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiExplicitFailureClassification", "Resolve-TunaGuiFailureClassification");
        var classifierBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiFileTransferSetupFailureClassification", "Get-TunaGuiFailureClassificationBudgetMilliseconds");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function New-TunaGuiFileTransferFailureClassificationResult { {{resultBody}} }
            function Get-TunaGuiExplicitFailureClassification { {{explicitBody}} }
            function Get-TunaGuiFileTransferSetupFailureClassification { {{classifierBody}} }
            function Get-AppLogLinesAfterBookmark { throw 'live ledger was read' }
            function Read-GuiSmokeProcessOutputLinesSnapshot { throw 'process output was read' }
            function Test-TunaGuiBridgeBootstrapNotReadyEvidence { return $false }
            function Test-TunaGuiLatestFallbackFrontierRequestDispatchMissing { return $false }
            function Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing { return $false }
            function Test-TunaGuiLogLinesContain { return $false }
            function Get-TunaGuiReadinessStateAfterBookmark {
                return [pscustomobject]@{ ListenerUnavailable = $false; ListenerReady = $false; TunaActive = $false }
            }
            function Resolve-TunaGuiFailureClassification {
                param($ErrorMessage, $HistoricalPhase, $HistoricalReason)
                return [pscustomobject]@{ Phase = $HistoricalPhase; Reason = $HistoricalReason }
            }
            function Assert-AdoptionRecheckFailure {
                param($Result, [string]$Case)
                if ($Result.Phase -ne 'route_runtime' -or
                    $Result.Reason -ne 'answer_ack_before_probe_acked_peer_probe_not_rechecked_into_responder_adopting_launch' -or
                    -not $Result.RuntimeUnlockResponderPeerProbeHandoffMissing -or
                    -not $Result.RuntimeUnlockResponderPeerProbeAdoptionRecheckMissingAfterAnswerAckBeforeProbe -or
                    $Result.RuntimeUnlockResponderPeerProbeBindingMissing -or
                    $Result.RuntimeUnlockResponderPeerProbeRouteTransitionHandoffMissingAfterBoundProbe -or
                    $Result.FailureVariant -ne 'runtime_unlock_responder_acked_probe_adoption_recheck_missing_after_answer_ack_before_probe' -or
                    $Result.ClassificationSource -ne 'immutable_evidence_responder_peer_probe_index') {
                    throw "$Case did not identify the ACK-before-probe adoption-recheck edge: $($Result | ConvertTo-Json -Compress)"
                }
            }
            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-ack-before-probe-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $path = Join-Path $tempRoot 'evidence.log'
                $currentScope = 'runtime_owner_session_id=sess; runtime_owner_transfer_id=transfer; runtime_owner_direction=inbound; runtime_owner_route=post_tuna_fallback_v6; runtime_owner_route_token=route-token; runtime_owner_protocol_version=6; runtime_owner_live_route_epoch=2; runtime_generation=2; runtime_owner_terminal_generation=0; route_publication_session_id=sess; route_publication_transfer_id=transfer; route_publication_direction=inbound; route_publication_route=post_tuna_fallback_v6; route_publication_route_token=route-token; route_publication_protocol_version=6; route_publication_live_route_epoch=2; route_publication_runtime_generation=2; route_publication_terminal_generation=0; route_publication_leg_generation=4; route_publication_commit_generation=2; route_publication_generation=3; terminal_generation=0; leg_generation=4; transport_epoch=5; bridge_recovery_generation=1; checkpoint_request_id=checkpoint-1; checkpoint_generation=1; data_session_identity=data-session; data_session_generation=1'
                $peerScope = 'peer_runtime_owner_session_id=sess; peer_runtime_owner_transfer_id=transfer; peer_runtime_owner_direction=inbound; peer_runtime_owner_route=post_tuna_fallback_v6; peer_runtime_owner_route_token=route-token; peer_runtime_owner_protocol_version=6; peer_runtime_owner_live_route_epoch=2; peer_runtime_generation=2; peer_runtime_owner_terminal_generation=0; peer_route_publication_session_id=sess; peer_route_publication_transfer_id=transfer; peer_route_publication_direction=inbound; peer_route_publication_route=post_tuna_fallback_v6; peer_route_publication_route_token=route-token; peer_route_publication_protocol_version=6; peer_route_publication_live_route_epoch=2; peer_route_publication_runtime_generation=2; peer_route_publication_terminal_generation=0; peer_route_publication_leg_generation=4; peer_route_publication_commit_generation=2; peer_route_publication_generation=3; peer_terminal_generation=0; peer_leg_generation=4; peer_transport_epoch=5; peer_bridge_recovery_generation=1; peer_checkpoint_request_id=checkpoint-1; peer_checkpoint_generation=1; peer_data_session_identity=data-session; peer_data_session_generation=1'
                $prefix = @(
                    'event=runtime_unlock_transaction_answer_received; session_id=sess; transaction_generation=1; offer_generation=11; answer_received=1; tuna_path_lease_generation=3; tuna_path_lease_current=1; tuna_path_lease_listener_run_id=peer-offer-dialer-3; log_writer_id=writer',
                    'event=tuna_acceleration_answer_ack_pending; session_id=sess; payer_decision_id=7; generation=17; log_writer_id=writer',
                    'event=tuna_acceleration_answer_sent; accepted=1; lanes=file; payer_decision_id=7; generation=17; msg_id=answer-message; reply_deadline_utc_ms=1200; log_writer_id=writer',
                    'event=nkn_inbound_envelope_received; envelope_type=TransportAccelerationAnswerAck; source_matches_expected=1; msg_id=answer-ack; ack_correlation_id=ack-correlation; ack_frame_fingerprint=ack-frame; ack_source_fingerprint=ack-fingerprint; log_writer_id=writer',
                    'event=tuna_acceleration_answer_ack_received_raw; msg_id=answer-ack; ack_correlation_id=ack-correlation; ack_frame_fingerprint=ack-frame; ack_source_fingerprint=ack-fingerprint; session_id=sess; payer_decision_id=7; log_writer_id=writer',
                    'event=tuna_acceleration_answer_ack_received; session_id=sess; lanes=file; payer_decision_id=7; ack_message_id=answer-ack; ack_correlation_id=ack-correlation; ack_frame_fingerprint=ack-frame; ack_source_fingerprint=ack-fingerprint; pending_answer_ack_generation=17; authority_mode=InitialActivation; log_writer_id=writer',
                    'event=runtime_unlock_answer_ack_probe_barrier_received; session_id=sess; lanes=file; payer_decision_id=7; reason=answer_ack_received_before_tuna_probe; log_writer_id=writer',
                    'event=filetransfer_runtime_unlock_handoff_pending_route_commit; session_id=sess; reason=runtime_unlock_answer_ack_probe_ready; lanes=file; transaction_generation=1; offer_generation=11; state=answerreceived; path_probe_state=none; route_commit_pending=0; route_committed=0; log_writer_id=writer',
                    'event=runtime_unlock_precommit_probe_launch_requested; session_id=sess; transfer_id=transfer; lanes=file; transaction_generation=1; offer_generation=11; launch_generation=6; tuna_path_lease_generation=3; tuna_path_lease_listener_run_id=peer-offer-dialer-3; path_probe_id=none; path_probe_state=none; duplicate=0; tuple_mutation=0; responder_waiting_for_peer_probe=1; launch_role=ResponderWaitingForPeerProbe; peer_probe_adoption=0; log_writer_id=writer',
                    "event=filetransfer_runtime_unlock_admission_granted; session_id=sess; transfer_id=transfer; direction=inbound; source=precommit_probe_responder; admission_generation=1; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; $currentScope; log_writer_id=writer",
                    "event=runtime_unlock_peer_precommit_answer_acceptance_decided; status=Held; decision_status=Held; session_id=sess; transfer_id=transfer; direction=inbound; peer_transaction_generation=1; peer_offer_generation=3; peer_tuna_path_lease_generation=3; local_transaction_generation=1; local_offer_generation=11; local_tuna_path_lease_generation=3; pending_answer_ack_generation=17; launch_generation=6; probe_id=peer-probe; probe_frame_fingerprint=probe-fingerprint; received_transport=tuna; received_utc_ms=850; probe_ack_sent_utc_ms=875; peer_admission_generation=1; $peerScope; data_session_generation=1; decision_reason=runtime_unlock_peer_precommit_probe_ack_pending; log_writer_id=writer",
                    'event=runtime_unlock_transaction_path_probe_acked; session_id=sess; transaction_generation=1; offer_generation=11; path_probe_id=peer-probe; path_probe_state=acked; path_probe_transport=tuna; path_probe_acked_utc_ms=900; tuna_path_lease_generation=3; log_writer_id=writer',
                    'event=runtime_unlock_precommit_probe_acked; direction=inbound; transfer_id=transfer; session_id=sess; transaction_generation=1; offer_generation=3; launch_generation=0; tuna_path_lease_generation=3; probe_id=peer-probe; probe_state=Acked; received_transport=tuna; log_writer_id=writer'
                )
                $prefix | Set-Content -LiteralPath $path
                $result = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'evidence_enrichment_timeout' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                Assert-AdoptionRecheckFailure -Result $result -Case 'artifact-shaped chronology'
                if ([long]$result.EvidenceLineVisitCount -ne 13 -or
                    [long]$result.EvidenceParsedRecordCount -ne 13 -or
                    [long]$result.EvidenceLookupCount -le 0) {
                    throw "ACK-before-probe evidence index metrics mismatch: $($result | ConvertTo-Json -Compress)"
                }

                $explicitLaterResult = Get-TunaGuiFileTransferSetupFailureClassification `
                    -Bookmark 0 `
                    -ErrorMessage 'failure_class=filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing' `
                    -RouteMode 'live-regular-activation-cycle' `
                    -ImmutableEvidencePaths @($path)
                Assert-AdoptionRecheckFailure `
                    -Result $explicitLaterResult `
                    -Case 'earlier indexed responder proof precedes later explicit checkpoint fallback'

                @('event=file_transfer_inbound_terminal; direction=inbound; session_id=other; transfer_id=other; state=Failed; log_writer_id=writer') |
                    Set-Content -LiteralPath $path
                $explicitOnlyResult = Get-TunaGuiFileTransferSetupFailureClassification `
                    -Bookmark 0 `
                    -ErrorMessage 'failure_class=filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing' `
                    -RouteMode 'live-regular-activation-cycle' `
                    -ImmutableEvidencePaths @($path)
                if (-not $explicitOnlyResult.FallbackFrontierCheckpointAcceptanceRecheckMissing -or
                    $explicitOnlyResult.Phase -ne 'route_runtime' -or
                    $explicitOnlyResult.Reason -ne 'checkpoint_pending_peer_frontier_request_not_rechecked_after_exact_checkpoint_acceptance' -or
                    $explicitOnlyResult.ClassificationSource -ne 'explicit_error') {
                    throw "explicit classification did not remain the no-proof fallback: $($explicitOnlyResult | ConvertTo-Json -Compress)"
                }
                $prefix | Set-Content -LiteralPath $path

                $reusedPayerAndGeneration = @(
                    'event=runtime_unlock_transaction_answer_received; session_id=sess; transaction_generation=1; offer_generation=5; answer_received=1; tuna_path_lease_generation=2; tuna_path_lease_current=1; tuna_path_lease_listener_run_id=peer-offer-dialer-2; log_writer_id=writer',
                    'event=tuna_acceleration_answer_ack_pending; session_id=sess; payer_decision_id=7; generation=17; log_writer_id=writer',
                    'event=tuna_acceleration_answer_sent; accepted=1; lanes=file; payer_decision_id=7; generation=17; msg_id=historical-answer-message; reply_deadline_utc_ms=1100; log_writer_id=writer'
                ) + $prefix
                $reusedPayerAndGeneration | Set-Content -LiteralPath $path
                Assert-AdoptionRecheckFailure `
                    -Result (Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)) `
                    -Case 'historical local answer excluded when payer and pending generation are reused'

                $legacyReceipt = @($prefix | ForEach-Object {
                        if ($_ -like 'event=nkn_inbound_envelope_received;*') {
                            'event=nkn_inbound_envelope_received; envelope_type=TransportAccelerationAnswerAck; source_matches_expected=1; msg_id=answer-ack; log_writer_id=writer'
                        }
                        elseif ($_ -like 'event=tuna_acceleration_answer_ack_received_raw;*') {
                            'event=tuna_acceleration_answer_ack_received_raw; msg_id=answer-ack; log_writer_id=writer'
                        }
                        elseif ($_ -like 'event=tuna_acceleration_answer_ack_received;*') {
                            'event=tuna_acceleration_answer_ack_received; session_id=sess; lanes=file; payer_decision_id=7; log_writer_id=writer'
                        }
                        elseif ($_ -like 'event=runtime_unlock_peer_precommit_answer_acceptance_decided;*') {
                            'event=runtime_unlock_peer_precommit_answer_acceptance_decided; status=NotApplicable; decision_status=NotApplicable; session_id=sess; transfer_id=transfer; direction=inbound; peer_transaction_generation=1; peer_offer_generation=3; peer_tuna_path_lease_generation=3; local_transaction_generation=0; local_offer_generation=0; local_tuna_path_lease_generation=0; pending_answer_ack_generation=0; launch_generation=0; probe_id=peer-probe; probe_frame_fingerprint=probe-fingerprint; received_utc_ms=850; probe_ack_sent_utc_ms=875; data_session_generation=1; decision_reason=runtime_unlock_peer_precommit_answer_ack_not_pending; log_writer_id=writer'
                        }
                        elseif ($_ -like 'event=filetransfer_runtime_unlock_admission_granted;*') {
                            'event=filetransfer_runtime_unlock_admission_granted; session_id=sess; transfer_id=transfer; direction=inbound; source=precommit_probe_responder; admission_generation=1; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=2; terminal_generation=0; leg_generation=4; log_writer_id=writer'
                        }
                        else { $_ }
                    })
                $legacyReceipt | Set-Content -LiteralPath $path
                Assert-AdoptionRecheckFailure `
                    -Result (Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)) `
                    -Case 'legacy exact answer-ACK-not-pending receipt'

                $legacyPartialRaw = @($legacyReceipt | ForEach-Object {
                        if ($_ -like 'event=tuna_acceleration_answer_ack_received_raw;*') {
                            $_.Replace('; log_writer_id=writer', '; ack_frame_fingerprint=partial-frame; log_writer_id=writer')
                        }
                        else { $_ }
                    })
                $legacyPartialRaw | Set-Content -LiteralPath $path
                $legacyPartialRawResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($legacyPartialRawResult.RuntimeUnlockResponderPeerProbeHandoffMissing) {
                    throw "partially enriched legacy raw ACK was accepted: $($legacyPartialRawResult | ConvertTo-Json -Compress)"
                }

                $partialModernScope = @($prefix | ForEach-Object {
                        if ($_ -like 'event=runtime_unlock_peer_precommit_answer_acceptance_decided;*') {
                            $_.Replace('peer_admission_generation=1; ', '')
                        }
                        else { $_ }
                    })
                $partialModernScope | Set-Content -LiteralPath $path
                $partialModernScopeResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($partialModernScopeResult.RuntimeUnlockResponderPeerProbeHandoffMissing) {
                    throw "partial modern Held peer scope was retained: $($partialModernScopeResult | ConvertTo-Json -Compress)"
                }

                $installedRetrySemanticAck = @($prefix | ForEach-Object {
                        if ($_ -like 'event=tuna_acceleration_answer_ack_received;*') {
                            $_.Replace('authority_mode=InitialActivation', 'authority_mode=InstalledRetry')
                        }
                        else { $_ }
                    })
                $installedRetrySemanticAck | Set-Content -LiteralPath $path
                $installedRetrySemanticAckResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($installedRetrySemanticAckResult.RuntimeUnlockResponderPeerProbeHandoffMissing) {
                    throw "InstalledRetry semantic ACK crossed the InitialActivation-only responder boundary: $($installedRetrySemanticAckResult | ConvertTo-Json -Compress)"
                }

                $missingModernTransport = @($prefix | ForEach-Object {
                        if ($_ -like 'event=runtime_unlock_peer_precommit_answer_acceptance_decided;*') {
                            $_.Replace('received_transport=tuna; ', '')
                        }
                        else { $_ }
                    })
                $missingModernTransport | Set-Content -LiteralPath $path
                $missingModernTransportResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($missingModernTransportResult.RuntimeUnlockResponderPeerProbeHandoffMissing) {
                    throw "modern Held evidence without received_transport=tuna was retained: $($missingModernTransportResult | ConvertTo-Json -Compress)"
                }

                $duplicateAdmission = @($prefix[0..9]) + @($prefix[9]) + @($prefix[10..12])
                $duplicateAdmission | Set-Content -LiteralPath $path
                Assert-AdoptionRecheckFailure `
                    -Result (Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)) `
                    -Case 'exact duplicate current-scope admission'

                $mutatedDuplicateAdmission = @($prefix[0..9]) + @(
                    $prefix[9].Replace('transport_epoch=5', 'transport_epoch=6')) + @($prefix[10..12])
                $mutatedDuplicateAdmission | Set-Content -LiteralPath $path
                $mutatedDuplicateAdmissionResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($mutatedDuplicateAdmissionResult.RuntimeUnlockResponderPeerProbeHandoffMissing) {
                    throw "mutated duplicate current-scope admission was retained: $($mutatedDuplicateAdmissionResult | ConvertTo-Json -Compress)"
                }

                $admissionScopeMutations = [ordered]@{
                    admission = @('admission_generation=1', 'admission_generation=9')
                    runtime_owner = @('runtime_owner_route=post_tuna_fallback_v6', 'runtime_owner_route=file_tuna_v4')
                    runtime_generation = @(' runtime_generation=2;', ' runtime_generation=9;')
                    publication_generation = @('route_publication_generation=3', 'route_publication_generation=9')
                    publication_commit = @('route_publication_commit_generation=2', 'route_publication_commit_generation=9')
                    terminal = @(' terminal_generation=0; leg_generation=4;', ' terminal_generation=9; leg_generation=4;')
                    leg = @(' leg_generation=4; transport_epoch=5;', ' leg_generation=9; transport_epoch=5;')
                    transport = @('transport_epoch=5', 'transport_epoch=9')
                    bridge = @('bridge_recovery_generation=1', 'bridge_recovery_generation=9')
                    checkpoint_id = @('checkpoint_request_id=checkpoint-1', 'checkpoint_request_id=checkpoint-9')
                    checkpoint_generation = @('checkpoint_generation=1', 'checkpoint_generation=9')
                    data_session_identity = @('data_session_identity=data-session', 'data_session_identity=stale-session')
                    data_session_generation = @('data_session_generation=1', 'data_session_generation=9')
                }
                foreach ($scopeMutationName in $admissionScopeMutations.Keys) {
                    $tokens = $admissionScopeMutations[$scopeMutationName]
                    $staleAdmission = @($prefix | ForEach-Object {
                            if ($_ -like 'event=filetransfer_runtime_unlock_admission_granted;*') {
                                $_.Replace([string]$tokens[0], [string]$tokens[1])
                            }
                            else { $_ }
                        })
                    $staleAdmission | Set-Content -LiteralPath $path
                    $staleAdmissionResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                    if ($staleAdmissionResult.RuntimeUnlockResponderPeerProbeHandoffMissing) {
                        throw "stale $scopeMutationName current admission was treated as the peer receive scope: $($staleAdmissionResult | ConvertTo-Json -Compress)"
                    }
                }

                $legacyFullScope = @($prefix | ForEach-Object {
                        if ($_ -like 'event=runtime_unlock_peer_precommit_answer_acceptance_decided;*') {
                            $_.Replace('status=Held; decision_status=Held', 'status=NotApplicable; decision_status=NotApplicable').Replace(
                                'local_transaction_generation=1; local_offer_generation=11; local_tuna_path_lease_generation=3; pending_answer_ack_generation=17; launch_generation=6',
                                'local_transaction_generation=0; local_offer_generation=0; local_tuna_path_lease_generation=0; pending_answer_ack_generation=0; launch_generation=0').Replace(
                                'decision_reason=runtime_unlock_peer_precommit_probe_ack_pending',
                                'decision_reason=runtime_unlock_peer_precommit_answer_ack_not_pending')
                        }
                        else { $_ }
                    })
                $legacyFullScope | Set-Content -LiteralPath $path
                $legacyFullScopeResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($legacyFullScopeResult.RuntimeUnlockResponderPeerProbeHandoffMissing) {
                    throw "non-legacy full-scope NotApplicable evidence was retained: $($legacyFullScopeResult | ConvertTo-Json -Compress)"
                }

                $delayedEnvelope = @($prefix[0..2]) + @($prefix[4..12]) + @($prefix[3])
                $delayedEnvelope | Set-Content -LiteralPath $path
                Assert-AdoptionRecheckFailure `
                    -Result (Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)) `
                    -Case 'delayed expected-source envelope telemetry'

                $delayedRawAndEnvelope = @($prefix[0..2]) + @($prefix[5..12]) + @($prefix[4], $prefix[3])
                $delayedRawAndEnvelope | Set-Content -LiteralPath $path
                Assert-AdoptionRecheckFailure `
                    -Result (Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)) `
                    -Case 'semantic ACK and barrier before delayed raw and envelope telemetry'

                $rawBeforePending = @($prefix[4]) + @($prefix[0..3]) + @($prefix[5..12])
                $rawBeforePending | Set-Content -LiteralPath $path
                $rawBeforePendingResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($rawBeforePendingResult.RuntimeUnlockResponderPeerProbeHandoffMissing) {
                    throw "raw Answer-ACK before the pending tuple was borrowed: $($rawBeforePendingResult | ConvertTo-Json -Compress)"
                }

                $rawAfterRetirement = @($prefix[0..2]) + @($prefix[5..12]) + @(
                    'event=transfer_terminal; direction=inbound; session_id=sess; transfer_id=transfer; terminal_state=failed; log_writer_id=writer',
                    $prefix[4],
                    $prefix[3])
                $rawAfterRetirement | Set-Content -LiteralPath $path
                $rawAfterRetirementResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($rawAfterRetirementResult.RuntimeUnlockResponderPeerProbeHandoffMissing) {
                    throw "raw Answer-ACK after scope retirement was borrowed: $($rawAfterRetirementResult | ConvertTo-Json -Compress)"
                }

                $productionEnvelope = @($prefix | ForEach-Object {
                        if ($_ -like 'event=nkn_inbound_envelope_received;*') {
                            'event=nkn_inbound_envelope_received; envelope_type=TransportAccelerationAnswerAck; source_matches_expected=1; msg_id=answer-ack; log_writer_id=writer'
                        }
                        else { $_ }
                    })
                $productionEnvelope | Set-Content -LiteralPath $path
                Assert-AdoptionRecheckFailure `
                    -Result (Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)) `
                    -Case 'production-shaped source-only envelope with authoritative detailed raw ACK'

                $duplicateRawAndEnvelope = @($prefix) + @($prefix[4], $prefix[3])
                $duplicateRawAndEnvelope | Set-Content -LiteralPath $path
                Assert-AdoptionRecheckFailure `
                    -Result (Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)) `
                    -Case 'exact duplicate raw and envelope telemetry'

                foreach ($answerSentMutation in @(
                        @('msg_id=answer-message', 'msg_id=mutated-answer-message'),
                        @('reply_deadline_utc_ms=1200', 'reply_deadline_utc_ms=1201'))) {
                    $mutatedAnswerSent = @($prefix) + @(
                        $prefix[2].Replace($answerSentMutation[0], $answerSentMutation[1]))
                    $mutatedAnswerSent | Set-Content -LiteralPath $path
                    $mutatedAnswerSentResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                    if ($mutatedAnswerSentResult.RuntimeUnlockResponderPeerProbeHandoffMissing) {
                        throw "mutated authoritative answer-sent tuple retained responder evidence ($($answerSentMutation[0])): $($mutatedAnswerSentResult | ConvertTo-Json -Compress)"
                    }
                }

                $duplicateSemanticAndProbeCallbacks = @($prefix) + @($prefix[5], $prefix[11], $prefix[12])
                $duplicateSemanticAndProbeCallbacks | Set-Content -LiteralPath $path
                Assert-AdoptionRecheckFailure `
                    -Result (Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)) `
                    -Case 'exact duplicate semantic ACK and probe callbacks preserve first chronology'

                foreach ($semanticMutation in @(
                        @('ack_message_id=answer-ack', 'ack_message_id=mutated-answer-ack'),
                        @('ack_correlation_id=ack-correlation', 'ack_correlation_id=mutated-correlation'),
                        @('payer_decision_id=7', 'payer_decision_id=9'),
                        @('pending_answer_ack_generation=17', 'pending_answer_ack_generation=19'),
                        @('lanes=file', 'lanes=screen'))) {
                    $mutatedSemanticAck = $prefix[5].Replace(
                        [string]$semanticMutation[0], [string]$semanticMutation[1])
                    @($prefix) + @($mutatedSemanticAck) | Set-Content -LiteralPath $path
                    $mutatedSemanticResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                    if ($mutatedSemanticResult.RuntimeUnlockResponderPeerProbeHandoffMissing) {
                        throw "same-lineage post-fallback semantic ACK mutation retained responder evidence ($($semanticMutation[0])): $($mutatedSemanticResult | ConvertTo-Json -Compress)"
                    }
                }

                $combinedRawMutation = @($prefix) + @(
                    $prefix[4].Replace('msg_id=answer-ack', 'msg_id=mutated-answer-ack').Replace(
                        'ack_correlation_id=ack-correlation',
                        'ack_correlation_id=mutated-correlation'))
                $combinedRawMutation | Set-Content -LiteralPath $path
                $combinedRawMutationResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($combinedRawMutationResult.RuntimeUnlockResponderPeerProbeHandoffMissing) {
                    throw "combined raw ACK message/correlation mutation retained responder evidence: $($combinedRawMutationResult | ConvertTo-Json -Compress)"
                }

                $peerReceiptTransportMutation = @($prefix[0..10]) + @(
                    $prefix[10].Replace('received_transport=tuna', 'received_transport=nkn')) + @($prefix[11..12])
                $peerReceiptTransportMutation | Set-Content -LiteralPath $path
                $peerReceiptTransportMutationResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($peerReceiptTransportMutationResult.RuntimeUnlockResponderPeerProbeHandoffMissing) {
                    throw "mutated duplicate peer receipt retained the original responder evidence: $($peerReceiptTransportMutationResult | ConvertTo-Json -Compress)"
                }

                $sourceMutation = $delayedEnvelope + @(
                    $prefix[3].Replace('source_matches_expected=1', 'source_matches_expected=0'))
                $sourceMutation | Set-Content -LiteralPath $path
                $sourceMutationResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($sourceMutationResult.RuntimeUnlockResponderPeerProbeHandoffMissing) {
                    throw "same-ID expected-source mutation was treated as semantic ACK evidence: $($sourceMutationResult | ConvertTo-Json -Compress)"
                }

                $correlationMutation = $delayedEnvelope + @(
                    $prefix[3].Replace('ack_correlation_id=ack-correlation', 'ack_correlation_id=mutated-correlation'))
                $correlationMutation | Set-Content -LiteralPath $path
                $correlationMutationResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($correlationMutationResult.RuntimeUnlockResponderPeerProbeHandoffMissing) {
                    throw "same-ID correlation mutation was treated as semantic ACK evidence: $($correlationMutationResult | ConvertTo-Json -Compress)"
                }

                $sameCorrelationEnvelopeMutation = @($prefix) + @(
                    $prefix[3].Replace('msg_id=answer-ack', 'msg_id=mutated-answer-ack').Replace(
                        'ack_frame_fingerprint=ack-frame',
                        'ack_frame_fingerprint=mutated-frame'))
                $sameCorrelationEnvelopeMutation | Set-Content -LiteralPath $path
                $sameCorrelationEnvelopeMutationResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($sameCorrelationEnvelopeMutationResult.RuntimeUnlockResponderPeerProbeHandoffMissing) {
                    throw "same-correlation changed-message envelope mutation retained responder evidence: $($sameCorrelationEnvelopeMutationResult | ConvertTo-Json -Compress)"
                }

                $explicit = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'failure_class=runtime_unlock_responder_acked_probe_route_transition_handoff_missing; failure_variant=runtime_unlock_responder_acked_probe_adoption_recheck_missing_after_answer_ack_before_probe' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if (-not $explicit.RuntimeUnlockResponderPeerProbeAdoptionRecheckMissingAfterAnswerAckBeforeProbe -or
                    $explicit.RuntimeUnlockResponderPeerProbeBindingMissing -or
                    $explicit.RuntimeUnlockResponderPeerProbeRouteTransitionHandoffMissingAfterBoundProbe) {
                    throw "explicit ACK-before-probe variant mismatch: $($explicit | ConvertTo-Json -Compress)"
                }

                $crossWriter = @($prefix[0..11]) + @($prefix[12].Replace('log_writer_id=writer', 'log_writer_id=other-writer'))
                $crossWriter | Set-Content -LiteralPath $path
                $crossWriterResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($crossWriterResult.RuntimeUnlockResponderPeerProbeHandoffMissing) {
                    throw "cross-writer peer probe was borrowed: $($crossWriterResult | ConvertTo-Json -Compress)"
                }

                $fixedCallback = @($prefix[0..11]) + @(
                    "event=runtime_unlock_responder_answer_probe_convergence_decided; status=Accepted; decision_status=Accepted; substate=Complete; session_id=sess; transfer_id=transfer; direction=inbound; transaction_generation=1; offer_generation=11; pending_answer_ack_generation=17; authority_mode=InitialActivation; payer_decision_id=7; lanes=file; nonce=ack-correlation; answer_message_id=answer-message; answer_deadline_utc_ms=1200; ack_message_id=answer-ack; ack_source_fingerprint=ack-fingerprint; ack_frame_fingerprint=ack-frame; peer_transaction_generation=1; peer_offer_generation=3; peer_tuna_path_lease_generation=3; probe_id=peer-probe; peer_probe_frame_fingerprint=probe-fingerprint; received_transport=tuna; received_utc_ms=850; probe_ack_sent_utc_ms=875; probe_state=Acked; probe_transport=tuna; probe_acked_utc_ms=900; launch_generation=6; tuna_path_lease_generation=3; tuna_path_lease_listener_run_id=peer-offer-dialer-3; peer_admission_generation=1; $peerScope; decision_reason=runtime_unlock_responder_answer_probe_completed; log_writer_id=writer",
                    'event=filetransfer_runtime_unlock_handoff_pending_route_commit; session_id=sess; reason=runtime_unlock_responder_peer_probe_acked; lanes=file; transaction_generation=1; offer_generation=11; state=routecommitpending; path_probe_state=acked; route_commit_pending=1; route_committed=0; log_writer_id=writer',
                    'event=runtime_unlock_precommit_probe_launch_requested; session_id=sess; transfer_id=transfer; lanes=file; transaction_generation=1; offer_generation=11; peer_transaction_generation=1; peer_offer_generation=3; peer_tuna_path_lease_generation=3; launch_generation=6; tuna_path_lease_generation=3; tuna_path_lease_listener_run_id=peer-offer-dialer-3; path_probe_id=peer-probe; path_probe_state=acked; duplicate=0; tuple_mutation=0; responder_waiting_for_peer_probe=0; launch_role=ResponderAdoptingAckedPeerProbe; peer_probe_adoption=1; log_writer_id=writer',
                    "event=runtime_unlock_precommit_responder_peer_probe_handoff_bound; status=Accepted; decision_status=Accepted; direction=inbound; session_id=sess; transfer_id=transfer; transaction_generation=1; offer_generation=11; peer_transaction_generation=1; peer_offer_generation=3; peer_tuna_path_lease_generation=3; launch_generation=6; tuna_path_lease_generation=3; tuna_path_lease_listener_run_id=peer-offer-dialer-3; probe_id=peer-probe; probe_state=Acked; probe_transport=tuna; probe_acked_utc_ms=900; launch_role=ResponderAdoptingAckedPeerProbe; answer_acceptance_source=SemanticAnswerAck; ack_message_id=answer-ack; ack_source_fingerprint=ack-fingerprint; peer_probe_frame_fingerprint=probe-fingerprint; peer_admission_generation=1; $peerScope; normalized_reason=exact_responder_peer_probe_adopted; log_writer_id=writer",
                    $prefix[12].Replace('launch_generation=0', 'launch_generation=6')
                )
                $fixedCallback | Set-Content -LiteralPath $path
                $fixedCallbackResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($fixedCallbackResult.RuntimeUnlockResponderPeerProbeAdoptionRecheckMissingAfterAnswerAckBeforeProbe -or
                    $fixedCallbackResult.RuntimeUnlockResponderPeerProbeBindingMissing -or
                    -not $fixedCallbackResult.RuntimeUnlockResponderPeerProbeRouteTransitionHandoffMissingAfterBoundProbe) {
                    throw "synchronous convergence/adoption/binding did not advance classification to the after-bound edge: $($fixedCallbackResult | ConvertTo-Json -Compress)"
                }

                $probeFirst = @(
                    'event=runtime_unlock_transaction_answer_received; session_id=sess; transaction_generation=1; offer_generation=11; answer_received=1; tuna_path_lease_generation=3; tuna_path_lease_current=1; tuna_path_lease_listener_run_id=peer-offer-dialer-3; log_writer_id=writer',
                    'event=tuna_acceleration_answer_ack_pending; session_id=sess; payer_decision_id=7; generation=17; log_writer_id=writer',
                    'event=tuna_acceleration_answer_sent; accepted=1; lanes=file; payer_decision_id=7; generation=17; msg_id=answer-message; reply_deadline_utc_ms=1200; log_writer_id=writer',
                    "event=filetransfer_runtime_unlock_admission_granted; session_id=sess; transfer_id=transfer; direction=inbound; source=precommit_probe_responder; admission_generation=1; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; $currentScope; log_writer_id=writer",
                    "event=runtime_unlock_peer_precommit_answer_acceptance_decided; status=Held; decision_status=Held; session_id=sess; transfer_id=transfer; direction=inbound; peer_transaction_generation=1; peer_offer_generation=3; peer_tuna_path_lease_generation=3; local_transaction_generation=1; local_offer_generation=11; local_tuna_path_lease_generation=3; pending_answer_ack_generation=17; launch_generation=0; probe_id=peer-probe; probe_frame_fingerprint=probe-fingerprint; received_transport=tuna; received_utc_ms=850; probe_ack_sent_utc_ms=875; peer_admission_generation=1; $peerScope; data_session_generation=1; decision_reason=runtime_unlock_peer_precommit_semantic_answer_ack_pending; log_writer_id=writer",
                    "event=runtime_unlock_responder_answer_probe_convergence_decided; status=Held; decision_status=Held; substate=AwaitingProbeAck; session_id=sess; transfer_id=transfer; direction=inbound; transaction_generation=1; offer_generation=11; pending_answer_ack_generation=17; authority_mode=InitialActivation; payer_decision_id=7; lanes=file; nonce=ack-correlation; answer_message_id=answer-message; answer_deadline_utc_ms=1200; ack_message_id=none; ack_source_fingerprint=none; ack_frame_fingerprint=none; peer_transaction_generation=1; peer_offer_generation=3; peer_tuna_path_lease_generation=3; probe_id=peer-probe; peer_probe_frame_fingerprint=probe-fingerprint; received_transport=tuna; received_utc_ms=850; probe_ack_sent_utc_ms=875; probe_state=None; probe_transport=unknown; probe_acked_utc_ms=0; launch_generation=0; tuna_path_lease_generation=3; tuna_path_lease_listener_run_id=peer-offer-dialer-3; peer_admission_generation=1; $peerScope; decision_reason=runtime_unlock_peer_precommit_semantic_answer_ack_pending; log_writer_id=writer",
                    'event=runtime_unlock_precommit_probe_launch_requested; session_id=sess; transfer_id=transfer; lanes=file; transaction_generation=1; offer_generation=11; launch_generation=6; tuna_path_lease_generation=3; tuna_path_lease_listener_run_id=peer-offer-dialer-3; path_probe_id=peer-probe; path_probe_state=started; duplicate=0; tuple_mutation=0; responder_waiting_for_peer_probe=0; launch_role=ResponderAdoptingStartedPeerProbe; peer_probe_adoption=1; log_writer_id=writer',
                    'event=nkn_inbound_envelope_received; envelope_type=TransportAccelerationAnswerAck; source_matches_expected=1; msg_id=answer-ack; ack_correlation_id=ack-correlation; ack_frame_fingerprint=ack-frame; ack_source_fingerprint=ack-fingerprint; log_writer_id=writer',
                    'event=tuna_acceleration_answer_ack_received_raw; msg_id=answer-ack; ack_correlation_id=ack-correlation; ack_frame_fingerprint=ack-frame; ack_source_fingerprint=ack-fingerprint; session_id=sess; payer_decision_id=7; log_writer_id=writer',
                    'event=tuna_acceleration_answer_ack_received; session_id=sess; lanes=file; payer_decision_id=7; ack_message_id=answer-ack; ack_correlation_id=ack-correlation; ack_frame_fingerprint=ack-frame; ack_source_fingerprint=ack-fingerprint; pending_answer_ack_generation=17; authority_mode=InitialActivation; log_writer_id=writer',
                    "event=runtime_unlock_responder_answer_probe_convergence_decided; status=Held; decision_status=Held; substate=AwaitingProbeAck; session_id=sess; transfer_id=transfer; direction=inbound; transaction_generation=1; offer_generation=11; pending_answer_ack_generation=17; authority_mode=InitialActivation; payer_decision_id=7; lanes=file; nonce=ack-correlation; answer_message_id=answer-message; answer_deadline_utc_ms=1200; ack_message_id=answer-ack; ack_source_fingerprint=ack-fingerprint; ack_frame_fingerprint=ack-frame; peer_transaction_generation=1; peer_offer_generation=3; peer_tuna_path_lease_generation=3; probe_id=peer-probe; peer_probe_frame_fingerprint=probe-fingerprint; received_transport=tuna; received_utc_ms=850; probe_ack_sent_utc_ms=875; probe_state=Started; probe_transport=tuna; probe_acked_utc_ms=0; launch_generation=6; tuna_path_lease_generation=3; tuna_path_lease_listener_run_id=peer-offer-dialer-3; peer_admission_generation=1; $peerScope; decision_reason=semantic_answer_ack_joined_waiting_for_probe_ack; log_writer_id=writer",
                    'event=runtime_unlock_answer_ack_probe_barrier_received; session_id=sess; lanes=file; payer_decision_id=7; reason=answer_ack_received_before_tuna_probe; log_writer_id=writer',
                    'event=runtime_unlock_transaction_path_probe_acked; session_id=sess; transaction_generation=1; offer_generation=11; path_probe_id=peer-probe; path_probe_state=acked; path_probe_transport=tuna; path_probe_acked_utc_ms=900; tuna_path_lease_generation=3; log_writer_id=writer',
                    "event=runtime_unlock_responder_answer_probe_convergence_decided; status=Accepted; decision_status=Accepted; substate=Complete; session_id=sess; transfer_id=transfer; direction=inbound; transaction_generation=1; offer_generation=11; pending_answer_ack_generation=17; authority_mode=InitialActivation; payer_decision_id=7; lanes=file; nonce=ack-correlation; answer_message_id=answer-message; answer_deadline_utc_ms=1200; ack_message_id=answer-ack; ack_source_fingerprint=ack-fingerprint; ack_frame_fingerprint=ack-frame; peer_transaction_generation=1; peer_offer_generation=3; peer_tuna_path_lease_generation=3; probe_id=peer-probe; peer_probe_frame_fingerprint=probe-fingerprint; received_transport=tuna; received_utc_ms=850; probe_ack_sent_utc_ms=875; probe_state=Acked; probe_transport=tuna; probe_acked_utc_ms=900; launch_generation=6; tuna_path_lease_generation=3; tuna_path_lease_listener_run_id=peer-offer-dialer-3; peer_admission_generation=1; $peerScope; decision_reason=runtime_unlock_responder_answer_probe_completed; log_writer_id=writer",
                    'event=runtime_unlock_precommit_probe_launch_requested; session_id=sess; transfer_id=transfer; lanes=file; transaction_generation=1; offer_generation=11; peer_transaction_generation=1; peer_offer_generation=3; peer_tuna_path_lease_generation=3; launch_generation=6; tuna_path_lease_generation=3; tuna_path_lease_listener_run_id=peer-offer-dialer-3; path_probe_id=peer-probe; path_probe_state=acked; duplicate=0; tuple_mutation=0; responder_waiting_for_peer_probe=0; launch_role=ResponderAdoptingAckedPeerProbe; peer_probe_adoption=1; log_writer_id=writer',
                    "event=runtime_unlock_precommit_responder_peer_probe_handoff_bound; status=Accepted; decision_status=Accepted; direction=inbound; session_id=sess; transfer_id=transfer; transaction_generation=1; offer_generation=11; peer_transaction_generation=1; peer_offer_generation=3; peer_tuna_path_lease_generation=3; launch_generation=6; tuna_path_lease_generation=3; tuna_path_lease_listener_run_id=peer-offer-dialer-3; probe_id=peer-probe; probe_state=Acked; probe_transport=tuna; probe_acked_utc_ms=900; launch_role=ResponderAdoptingAckedPeerProbe; answer_acceptance_source=SemanticAnswerAck; ack_message_id=answer-ack; ack_source_fingerprint=ack-fingerprint; peer_probe_frame_fingerprint=probe-fingerprint; peer_admission_generation=1; $peerScope; normalized_reason=exact_responder_peer_probe_adopted; log_writer_id=writer",
                    'event=runtime_unlock_precommit_probe_acked; direction=inbound; transfer_id=transfer; session_id=sess; transaction_generation=1; offer_generation=3; launch_generation=6; tuna_path_lease_generation=3; probe_id=peer-probe; probe_state=Acked; received_transport=tuna; log_writer_id=writer'
                )
                $probeFirst | Set-Content -LiteralPath $path
                $probeFirstResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($probeFirstResult.RuntimeUnlockResponderPeerProbeAdoptionRecheckMissingAfterAnswerAckBeforeProbe -or
                    $probeFirstResult.RuntimeUnlockResponderPeerProbeBindingMissing -or
                    -not $probeFirstResult.RuntimeUnlockResponderPeerProbeRouteTransitionHandoffMissingAfterBoundProbe) {
                    throw "probe-first retained receipt and semantic ACK did not converge through the same started launch: $($probeFirstResult | ConvertTo-Json -Compress)"
                }
                $probeFirstDuplicateConvergence = @($probeFirst[0..5]) + @($probeFirst[5]) + @($probeFirst[6..16])
                $probeFirstDuplicateConvergence | Set-Content -LiteralPath $path
                $probeFirstDuplicateConvergenceResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if (-not $probeFirstDuplicateConvergenceResult.RuntimeUnlockResponderPeerProbeRouteTransitionHandoffMissingAfterBoundProbe) {
                    throw "exact duplicate probe-first convergence decision changed the retained progression: $($probeFirstDuplicateConvergenceResult | ConvertTo-Json -Compress)"
                }
                $probeFirstAdmissionMutation = @($probeFirst[0..5]) + @(
                    $probeFirst[3].Replace('transport_epoch=5', 'transport_epoch=6')) + @($probeFirst[6..16])
                $probeFirstAdmissionMutation | Set-Content -LiteralPath $path
                $probeFirstAdmissionMutationResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($probeFirstAdmissionMutationResult.RuntimeUnlockResponderPeerProbeHandoffMissing) {
                    throw "probe-first post-receipt admission mutation retained responder authority: $($probeFirstAdmissionMutationResult | ConvertTo-Json -Compress)"
                }

                foreach ($boundMutation in @(
                        @('direction=inbound', 'direction=outbound'),
                        @('decision_status=Accepted', 'decision_status=Rejected'))) {
                    $mutatedModernBinding = @($fixedCallback | ForEach-Object {
                            if ($_ -like 'event=runtime_unlock_precommit_responder_peer_probe_handoff_bound;*') {
                                $_.Replace($boundMutation[0], $boundMutation[1])
                            }
                            else { $_ }
                        })
                    $mutatedModernBinding | Set-Content -LiteralPath $path
                    $mutatedModernBindingResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                    if (-not $mutatedModernBindingResult.RuntimeUnlockResponderPeerProbeBindingMissing -or
                        $mutatedModernBindingResult.RuntimeUnlockResponderPeerProbeRouteTransitionHandoffMissingAfterBoundProbe) {
                        throw "modern responder binding mutation crossed the binding boundary ($($boundMutation[0])): $($mutatedModernBindingResult | ConvertTo-Json -Compress)"
                    }
                }

                foreach ($convergenceMutation in @(
                        @('decision_status=Accepted', 'decision_status=Invalidated'),
                        @('authority_mode=InitialActivation', 'authority_mode=InstalledRetry'),
                        @('transfer_id=transfer', 'transfer_id=mutated-transfer'),
                        @('answer_message_id=answer-message', 'answer_message_id=mutated-answer-message'),
                        @('answer_deadline_utc_ms=1200', 'answer_deadline_utc_ms=1201'),
                        @('decision_reason=runtime_unlock_responder_answer_probe_completed', 'decision_reason=mutated_completion_reason'))) {
                    $mutatedConvergence = @($fixedCallback) + @(
                        $fixedCallback[12].Replace($convergenceMutation[0], $convergenceMutation[1]))
                    $mutatedConvergence | Set-Content -LiteralPath $path
                    $mutatedConvergenceResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                    if ($mutatedConvergenceResult.RuntimeUnlockResponderPeerProbeHandoffMissing) {
                        throw "mutated duplicate convergence local tuple retained responder evidence ($($convergenceMutation[0])): $($mutatedConvergenceResult | ConvertTo-Json -Compress)"
                    }
                }

                $adoptingWithoutBound = @($fixedCallback[0..11]) + @(
                    $fixedCallback[13], $fixedCallback[14], $fixedCallback[16])
                $adoptingWithoutBound | Set-Content -LiteralPath $path
                $adoptingWithoutBoundResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($adoptingWithoutBoundResult.RuntimeUnlockResponderPeerProbeAdoptionRecheckMissingAfterAnswerAckBeforeProbe -or
                    -not $adoptingWithoutBoundResult.RuntimeUnlockResponderPeerProbeBindingMissing -or
                    $adoptingWithoutBoundResult.RuntimeUnlockResponderPeerProbeRouteTransitionHandoffMissingAfterBoundProbe) {
                    throw "exact adopting launch without a bound did not advance to binding-missing: $($adoptingWithoutBoundResult | ConvertTo-Json -Compress)"
                }

                $boundWithoutConvergence = @($fixedCallback[0..11]) + @($fixedCallback[13..16])
                $boundWithoutConvergence | Set-Content -LiteralPath $path
                $boundWithoutConvergenceResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($boundWithoutConvergenceResult.RuntimeUnlockResponderPeerProbeAdoptionRecheckMissingAfterAnswerAckBeforeProbe -or
                    $boundWithoutConvergenceResult.RuntimeUnlockResponderPeerProbeBindingMissing -or
                    -not $boundWithoutConvergenceResult.RuntimeUnlockResponderPeerProbeRouteTransitionHandoffMissingAfterBoundProbe) {
                    throw "accepted bound without convergence did not advance to after-bound: $($boundWithoutConvergenceResult | ConvertTo-Json -Compress)"
                }

                $admissionAlias = @($fixedCallback | ForEach-Object {
                        $_.Replace('peer_admission_generation=1', 'admission_generation=1')
                    })
                $admissionAlias | Set-Content -LiteralPath $path
                $admissionAliasResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if (-not $admissionAliasResult.RuntimeUnlockResponderPeerProbeRouteTransitionHandoffMissingAfterBoundProbe) {
                    throw "admission_generation alias did not preserve the exact peer scope join: $($admissionAliasResult | ConvertTo-Json -Compress)"
                }

                $scopeMutation = @($fixedCallback | ForEach-Object {
                        if ($_ -like 'event=runtime_unlock_responder_answer_probe_convergence_decided;*') {
                            $_.Replace('peer_transport_epoch=5', 'peer_transport_epoch=6')
                        }
                        else { $_ }
                    })
                $scopeMutation | Set-Content -LiteralPath $path
                $scopeMutationResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($scopeMutationResult.RuntimeUnlockResponderPeerProbeHandoffMissing -or
                    $scopeMutationResult.RuntimeUnlockResponderPeerProbeAdoptionRecheckMissingAfterAnswerAckBeforeProbe -or
                    $scopeMutationResult.RuntimeUnlockResponderPeerProbeBindingMissing -or
                    $scopeMutationResult.RuntimeUnlockResponderPeerProbeRouteTransitionHandoffMissingAfterBoundProbe) {
                    throw "mutated peer-receipt scope produced provisional responder authority: $($scopeMutationResult | ConvertTo-Json -Compress)"
                }

                $peerReceiptMutation = @($fixedCallback | ForEach-Object {
                        if ($_ -like 'event=runtime_unlock_responder_answer_probe_convergence_decided;*') {
                            $_.Replace('received_utc_ms=850', 'received_utc_ms=851')
                        }
                        else { $_ }
                    })
                $peerReceiptMutation | Set-Content -LiteralPath $path
                $peerReceiptMutationResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($peerReceiptMutationResult.RuntimeUnlockResponderPeerProbeHandoffMissing -or
                    $peerReceiptMutationResult.RuntimeUnlockResponderPeerProbeAdoptionRecheckMissingAfterAnswerAckBeforeProbe -or
                    $peerReceiptMutationResult.RuntimeUnlockResponderPeerProbeBindingMissing -or
                    $peerReceiptMutationResult.RuntimeUnlockResponderPeerProbeRouteTransitionHandoffMissingAfterBoundProbe) {
                    throw "mutated peer-receipt chronology produced provisional responder authority: $($peerReceiptMutationResult | ConvertTo-Json -Compress)"
                }

                $callbackInvalidation = @($prefix[0..11]) + @(
                    "event=runtime_unlock_responder_answer_probe_convergence_decided; status=Invalidated; decision_status=Invalidated; substate=Invalidated; session_id=sess; transfer_id=transfer; direction=inbound; transaction_generation=1; offer_generation=11; pending_answer_ack_generation=17; authority_mode=InitialActivation; payer_decision_id=7; lanes=file; nonce=ack-correlation; answer_message_id=answer-message; answer_deadline_utc_ms=1200; ack_message_id=answer-ack; ack_source_fingerprint=ack-fingerprint; ack_frame_fingerprint=ack-frame; peer_transaction_generation=1; peer_offer_generation=3; peer_tuna_path_lease_generation=3; probe_id=peer-probe; peer_probe_frame_fingerprint=probe-fingerprint; received_transport=tuna; received_utc_ms=850; probe_ack_sent_utc_ms=875; probe_state=Acked; probe_transport=tuna; probe_acked_utc_ms=900; launch_generation=6; tuna_path_lease_generation=3; tuna_path_lease_listener_run_id=peer-offer-dialer-3; peer_admission_generation=1; $peerScope; decision_reason=runtime_unlock_responder_answer_probe_current_scope_replaced; log_writer_id=writer",
                    $prefix[12])
                $callbackInvalidation | Set-Content -LiteralPath $path
                $callbackInvalidationResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($callbackInvalidationResult.RuntimeUnlockResponderPeerProbeHandoffMissing -or
                    $callbackInvalidationResult.RuntimeUnlockResponderPeerProbeAdoptionRecheckMissingAfterAnswerAckBeforeProbe -or
                    $callbackInvalidationResult.RuntimeUnlockResponderPeerProbeBindingMissing -or
                    $callbackInvalidationResult.RuntimeUnlockResponderPeerProbeRouteTransitionHandoffMissingAfterBoundProbe) {
                    throw "exact convergence invalidation inside the ACK callback created an incident: $($callbackInvalidationResult | ConvertTo-Json -Compress)"
                }

                $mutatedCallbackInvalidation = @($callbackInvalidation | ForEach-Object {
                        if ($_ -like 'event=runtime_unlock_responder_answer_probe_convergence_decided;*') {
                            $_.Replace('ack_frame_fingerprint=ack-frame', 'ack_frame_fingerprint=mutated-frame')
                        }
                        else { $_ }
                    })
                $mutatedCallbackInvalidation | Set-Content -LiteralPath $path
                $mutatedCallbackInvalidationResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($mutatedCallbackInvalidationResult.RuntimeUnlockResponderPeerProbeHandoffMissing) {
                    throw "mutated convergence invalidation was treated as an exact callback retirement: $($mutatedCallbackInvalidationResult | ConvertTo-Json -Compress)"
                }
                Write-Output 'gui-ack-before-probe-adoption-recheck-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(result.ExitCode == 0, $"ACK-before-probe GUI stage classification failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-ack-before-probe-adoption-recheck-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeFailureClassification_BoundResponderProbeUsesLocalOfferAndExactPeerProbeIndexes()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var resultBody = ExtractPowerShellFunctionBody(guiText, "New-TunaGuiFileTransferFailureClassificationResult", "Get-TunaGuiExplicitFailureClassification");
        var explicitBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiExplicitFailureClassification", "Resolve-TunaGuiFailureClassification");
        var classifierBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiFileTransferSetupFailureClassification", "Get-TunaGuiFailureClassificationBudgetMilliseconds");
        Assert.Contains("source_matches_expected", classifierBody, StringComparison.Ordinal);
        Assert.Contains("Test-TunaGuiResponderTransitionAuthorityScope", classifierBody, StringComparison.Ordinal);
        Assert.Contains("retry_install_generation", classifierBody, StringComparison.Ordinal);
        Assert.Contains("$decisionTransitionAuthorityId -notin @('(none)', 'none')", classifierBody, StringComparison.Ordinal);
        Assert.Contains("if (-not $state.Bound -or $state.BoundIndex -le 0)", classifierBody, StringComparison.Ordinal);
        Assert.Contains("file_transfer_inbound_terminal", classifierBody, StringComparison.Ordinal);
        Assert.Contains("filetransfer_runtime_owner_changed", classifierBody, StringComparison.Ordinal);
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function New-TunaGuiFileTransferFailureClassificationResult { {{resultBody}} }
            function Get-TunaGuiExplicitFailureClassification { {{explicitBody}} }
            function Get-TunaGuiFileTransferSetupFailureClassification { {{classifierBody}} }
            function Get-AppLogLinesAfterBookmark { throw 'live ledger was read' }
            function Read-GuiSmokeProcessOutputLinesSnapshot { throw 'process output was read' }
            function Test-TunaGuiBridgeBootstrapNotReadyEvidence { return $false }
            function Test-TunaGuiLatestFallbackFrontierRequestDispatchMissing { return $false }
            function Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing { return $false }
            function Test-TunaGuiLogLinesContain { return $false }
            function Get-TunaGuiReadinessStateAfterBookmark {
                return [pscustomobject]@{ ListenerUnavailable = $false; ListenerReady = $false; TunaActive = $false }
            }
            function Resolve-TunaGuiFailureClassification {
                param($ErrorMessage, $HistoricalPhase, $HistoricalReason)
                return [pscustomobject]@{ Phase = $HistoricalPhase; Reason = $HistoricalReason }
            }
            function Assert-NoResponderPeerProbeFailure {
                param($Result, [string]$Case)
                if ($Result.RuntimeUnlockResponderPeerProbeHandoffMissing -or
                    $Result.RuntimeUnlockResponderPeerProbeBindingMissing -or
                    $Result.RuntimeUnlockResponderPeerProbeAdoptionRecheckMissingAfterAnswerAckBeforeProbe -or
                    $Result.RuntimeUnlockResponderPeerProbeRouteTransitionHandoffMissingAfterBoundProbe) {
                    throw "$Case unexpectedly produced responder handoff authority: $($Result | ConvertTo-Json -Compress)"
                }
            }
            function Assert-AfterBoundResponderPeerProbeFailure {
                param($Result, [string]$Case)
                if ($Result.Phase -ne 'route_runtime' -or
                    -not $Result.RuntimeUnlockResponderPeerProbeHandoffMissing -or
                    $Result.RuntimeUnlockResponderPeerProbeBindingMissing -or
                    -not $Result.RuntimeUnlockResponderPeerProbeRouteTransitionHandoffMissingAfterBoundProbe) {
                    throw "$Case did not preserve the after-bound responder failure: $($Result | ConvertTo-Json -Compress)"
                }
            }
            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-bound-responder-probe-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $path = Join-Path $tempRoot 'evidence.log'
                @('event=file_transfer_inbound_terminal; direction=inbound; session_id=other; transfer_id=other; state=Failed; log_writer_id=writer') |
                    Set-Content -LiteralPath $path
                $explicit = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'failure_class=runtime_unlock_responder_acked_probe_route_transition_handoff_missing; failure_variant=runtime_unlock_responder_acked_probe_route_transition_handoff_missing_after_bound_probe' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($explicit.Phase -ne 'route_runtime' -or
                    $explicit.Reason -ne 'bound_responder_acked_peer_probe_route_transition_handoff_invalidated' -or
                    -not $explicit.RuntimeUnlockResponderPeerProbeHandoffMissing -or
                    $explicit.RuntimeUnlockResponderPeerProbeBindingMissing -or
                    -not $explicit.RuntimeUnlockResponderPeerProbeRouteTransitionHandoffMissingAfterBoundProbe) {
                    throw "explicit responder variant mismatch: $($explicit | ConvertTo-Json -Compress)"
                }
                $explicitTransferReplaced = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'failure_class=runtime_unlock_responder_acked_probe_route_transition_handoff_missing; failure_variant=runtime_unlock_responder_acked_probe_route_transition_handoff_missing_after_bound_probe; first_proof_chain_failure_reason=bound_responder_acked_peer_probe_route_transition_handoff_invalidated_as_transfer_context_replaced' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($explicitTransferReplaced.Reason -ne 'bound_responder_acked_peer_probe_route_transition_handoff_invalidated_as_transfer_context_replaced') {
                    throw "explicit transfer replacement reason mismatch: $($explicitTransferReplaced | ConvertTo-Json -Compress)"
                }
                $prefix = @(
                    'event=runtime_unlock_transaction_answer_received; session_id=sess; transaction_generation=1; offer_generation=6; answer_received=1; tuna_path_lease_generation=1; tuna_path_lease_current=1; tuna_path_lease_listener_run_id=peer-offer-dialer-1; log_writer_id=writer',
                    'event=tuna_acceleration_answer_ack_pending; session_id=sess; payer_decision_id=4; generation=6; log_writer_id=writer',
                    'event=runtime_unlock_transaction_answer_received; session_id=sess; transaction_generation=1; offer_generation=12; answer_received=1; tuna_path_lease_generation=3; tuna_path_lease_current=1; tuna_path_lease_listener_run_id=peer-offer-dialer-3; log_writer_id=writer',
                    'event=tuna_acceleration_answer_ack_pending; session_id=sess; payer_decision_id=7; generation=12; log_writer_id=writer',
                    'event=runtime_unlock_transaction_path_probe_acked; session_id=sess; transaction_generation=1; offer_generation=12; path_probe_id=probe-3; path_probe_state=acked; path_probe_transport=tuna; path_probe_acked_utc_ms=900; tuna_path_lease_generation=3; log_writer_id=writer',
                    'event=runtime_unlock_precommit_probe_acked; session_id=sess; transfer_id=transfer; direction=inbound; transaction_generation=1; offer_generation=3; launch_generation=0; probe_id=probe-3; probe_state=acked; received_transport=tuna; tuna_path_lease_generation=3; log_writer_id=writer',
                    'event=nkn_inbound_envelope_received; envelope_type=TransportAccelerationAnswerAck; source_matches_expected=1; msg_id=answer-ack-12; log_writer_id=writer',
                    'event=tuna_acceleration_answer_ack_received_raw; msg_id=answer-ack-12; log_writer_id=writer',
                    'event=tuna_acceleration_answer_ack_received; session_id=sess; lanes=file; payer_decision_id=7; log_writer_id=writer',
                    'event=runtime_unlock_answer_ack_probe_barrier_received; session_id=sess; lanes=file; payer_decision_id=7; log_writer_id=writer',
                    'event=filetransfer_runtime_unlock_handoff_pending_route_commit; session_id=sess; lanes=file; transaction_generation=1; offer_generation=12; state=routecommitpending; path_probe_state=acked; route_commit_pending=1; route_committed=0; log_writer_id=writer',
                    'event=runtime_unlock_precommit_probe_launch_requested; session_id=sess; transfer_id=transfer; lanes=file; transaction_generation=1; offer_generation=12; launch_generation=6; tuna_path_lease_generation=3; tuna_path_lease_listener_run_id=peer-offer-dialer-3; path_probe_id=probe-3; path_probe_state=acked; launch_role=ResponderAdoptingAckedPeerProbe; peer_probe_adoption=1; duplicate=0; tuple_mutation=0; responder_waiting_for_peer_probe=0; log_writer_id=writer',
                    'event=runtime_unlock_precommit_responder_peer_probe_handoff_bound; status=Accepted; session_id=sess; transfer_id=transfer; transaction_generation=1; offer_generation=12; launch_generation=6; tuna_path_lease_generation=3; tuna_path_lease_listener_run_id=peer-offer-dialer-3; probe_id=probe-3; probe_state=Acked; probe_transport=tuna; probe_acked_utc_ms=900; launch_role=ResponderAdoptingAckedPeerProbe; answer_acceptance_source=SemanticAnswerAck; ack_message_id=answer-ack-12; ack_source_fingerprint=ack-fingerprint; peer_probe_frame_fingerprint=probe-frame-fingerprint; normalized_reason=exact_responder_peer_probe_adopted; log_writer_id=writer'
                )
                $invalidated = $prefix + @(
                    'event=filetransfer_runtime_unlock_precommit_probe_launch_handoff_decided; direction=outbound; session_id=sess; transfer_id=transfer; transaction_generation=1; offer_generation=12; launch_generation=6; tuna_path_lease_generation=3; tuna_path_lease_listener_run_id=peer-offer-dialer-3; status=Invalidated; decision_reason=transfer_context_replaced; log_writer_id=writer',
                    'event=transfer_terminal; direction=inbound; session_id=sess; transfer_id=transfer; terminal_state=failed; log_writer_id=writer'
                )
                $invalidated | Set-Content -LiteralPath $path
                $result = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'evidence_enrichment_timeout; child_exit_code_unavailable' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($result.Phase -ne 'route_runtime' -or
                    $result.Reason -ne 'bound_responder_acked_peer_probe_route_transition_handoff_invalidated_as_transfer_context_replaced' -or
                    -not $result.RuntimeUnlockResponderPeerProbeHandoffMissing -or
                    $result.RuntimeUnlockResponderPeerProbeBindingMissing -or
                    -not $result.RuntimeUnlockResponderPeerProbeRouteTransitionHandoffMissingAfterBoundProbe -or
                    [long]$result.EvidenceLineVisitCount -ne $invalidated.Count) {
                    throw "bound responder peer-probe mismatch: $($result | ConvertTo-Json -Compress)"
                }

                $dataSessionUnavailable = $prefix + @(
                    'event=filetransfer_runtime_unlock_precommit_probe_launch_handoff_decided; direction=inbound; session_id=sess; transfer_id=transfer; transaction_generation=1; offer_generation=12; launch_generation=6; tuna_path_lease_generation=3; tuna_path_lease_listener_run_id=peer-offer-dialer-3; status=Invalidated; decision_reason=data_session_unavailable; log_writer_id=writer'
                )
                $dataSessionUnavailable | Set-Content -LiteralPath $path
                $dataSessionUnavailableResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($dataSessionUnavailableResult.Reason -ne 'bound_responder_acked_peer_probe_route_transition_handoff_invalidated' -or
                    -not $dataSessionUnavailableResult.RuntimeUnlockResponderPeerProbeRouteTransitionHandoffMissingAfterBoundProbe -or
                    [long]$dataSessionUnavailableResult.EvidenceLineVisitCount -ne $dataSessionUnavailable.Count) {
                    throw "data-session-unavailable responder mismatch: $($dataSessionUnavailableResult | ConvertTo-Json -Compress)"
                }

                $legacyBound = $prefix[-1].Replace(
                    '; ack_message_id=answer-ack-12; ack_source_fingerprint=ack-fingerprint; peer_probe_frame_fingerprint=probe-frame-fingerprint',
                    '')
                $legacyInvalidated = @($prefix[0..($prefix.Count - 2)]) + @(
                    $legacyBound,
                    $invalidated[-2],
                    $invalidated[-1]
                )
                $legacyInvalidated | Set-Content -LiteralPath $path
                $legacyResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'evidence_enrichment_timeout; child_exit_code_unavailable' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($legacyResult.Phase -ne 'route_runtime' -or
                    $legacyResult.Reason -ne 'bound_responder_acked_peer_probe_route_transition_handoff_invalidated_as_transfer_context_replaced' -or
                    -not $legacyResult.RuntimeUnlockResponderPeerProbeHandoffMissing -or
                    $legacyResult.RuntimeUnlockResponderPeerProbeBindingMissing -or
                    -not $legacyResult.RuntimeUnlockResponderPeerProbeRouteTransitionHandoffMissingAfterBoundProbe) {
                    throw "legacy bound responder peer-probe mismatch: $($legacyResult | ConvertTo-Json -Compress)"
                }

                $exactBoundDuplicate = $prefix + @($prefix[-1].Replace('status=Accepted', 'status=Idempotent'))
                $exactBoundDuplicate | Set-Content -LiteralPath $path
                Assert-AfterBoundResponderPeerProbeFailure `
                    -Result (Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)) `
                    -Case 'exact idempotent responder binding duplicate'

                $partialLegacyBound = $legacyBound + '; ack_message_id=answer-ack-12'
                $partialLegacyInvalidated = @($prefix[0..($prefix.Count - 2)]) + @(
                    $partialLegacyBound,
                    $invalidated[-2]
                )
                $partialLegacyInvalidated | Set-Content -LiteralPath $path
                $partialLegacyResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if (-not $partialLegacyResult.RuntimeUnlockResponderPeerProbeHandoffMissing -or
                    -not $partialLegacyResult.RuntimeUnlockResponderPeerProbeBindingMissing -or
                    $partialLegacyResult.RuntimeUnlockResponderPeerProbeRouteTransitionHandoffMissingAfterBoundProbe) {
                    throw "partial legacy responder binding did not fail closed: $($partialLegacyResult | ConvertTo-Json -Compress)"
                }

                $terminalized = $prefix + @(
                    'event=transfer_terminal; direction=inbound; session_id=sess; transfer_id=transfer; terminal_state=failed; log_writer_id=writer'
                )
                $terminalized | Set-Content -LiteralPath $path
                $terminalizedResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'evidence_enrichment_timeout' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if (-not $terminalizedResult.RuntimeUnlockResponderPeerProbeHandoffMissing -or
                    $terminalizedResult.RuntimeUnlockResponderPeerProbeBindingMissing -or
                    -not $terminalizedResult.RuntimeUnlockResponderPeerProbeRouteTransitionHandoffMissingAfterBoundProbe) {
                    throw "terminal cleanup erased the bound responder handoff failure: $($terminalizedResult | ConvertTo-Json -Compress)"
                }

                $interleavedRaw = @($prefix[0..7]) + @(
                    'event=nkn_inbound_envelope_received; envelope_type=TransportAccelerationAnswerAck; source_matches_expected=1; msg_id=other-session-answer-ack; log_writer_id=writer',
                    'event=tuna_acceleration_answer_ack_received_raw; msg_id=other-session-answer-ack; log_writer_id=writer'
                ) + @($prefix[8..($prefix.Count - 1)]) + @($invalidated[-2], $invalidated[-1])
                $interleavedRaw | Set-Content -LiteralPath $path
                $interleavedResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($interleavedResult.RuntimeUnlockResponderPeerProbeHandoffMissing -or
                    $interleavedResult.RuntimeUnlockResponderPeerProbeBindingMissing -or
                    $interleavedResult.RuntimeUnlockResponderPeerProbeRouteTransitionHandoffMissingAfterBoundProbe) {
                    throw "interleaved cross-session raw ACK was borrowed: $($interleavedResult | ConvertTo-Json -Compress)"
                }

                $wrongSource = @($invalidated | ForEach-Object {
                        $_.Replace('source_matches_expected=1', 'source_matches_expected=0')
                    })
                $wrongSource | Set-Content -LiteralPath $path
                Assert-NoResponderPeerProbeFailure `
                    -Result (Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)) `
                    -Case 'unexpected AnswerAck source'

                $rawBeforeCoreProbe = @($prefix[0..4]) + @($prefix[6], $prefix[7], $prefix[5]) +
                    @($prefix[8..($prefix.Count - 1)]) + @($invalidated[-2])
                $rawBeforeCoreProbe | Set-Content -LiteralPath $path
                Assert-NoResponderPeerProbeFailure `
                    -Result (Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)) `
                    -Case 'raw AnswerAck before Core probe ACK'

                $crossTransferLaunch = @($prefix | ForEach-Object {
                        if ($_ -like 'event=runtime_unlock_precommit_probe_launch_requested;*') {
                            $_.Replace('transfer_id=transfer', 'transfer_id=other-transfer')
                        }
                        else { $_ }
                    }) + @($invalidated[-2])
                $crossTransferLaunch | Set-Content -LiteralPath $path
                Assert-NoResponderPeerProbeFailure `
                    -Result (Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)) `
                    -Case 'cross-transfer responder launch'

                $crossTransferBound = @($prefix | ForEach-Object {
                        if ($_ -like 'event=runtime_unlock_precommit_responder_peer_probe_handoff_bound;*') {
                            $_.Replace('transfer_id=transfer', 'transfer_id=other-transfer')
                        }
                        else { $_ }
                    }) + @($invalidated[-2])
                $crossTransferBound | Set-Content -LiteralPath $path
                $crossTransferBoundResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if (-not $crossTransferBoundResult.RuntimeUnlockResponderPeerProbeHandoffMissing -or
                    -not $crossTransferBoundResult.RuntimeUnlockResponderPeerProbeBindingMissing -or
                    $crossTransferBoundResult.RuntimeUnlockResponderPeerProbeRouteTransitionHandoffMissingAfterBoundProbe) {
                    throw "cross-transfer responder binding did not fail closed before binding: $($crossTransferBoundResult | ConvertTo-Json -Compress)"
                }

                $crossTransferBoundDuplicate = $prefix + @(
                    $prefix[-1].Replace('transfer_id=transfer', 'transfer_id=other-transfer'),
                    $invalidated[-2]
                )
                $crossTransferBoundDuplicate | Set-Content -LiteralPath $path
                Assert-NoResponderPeerProbeFailure `
                    -Result (Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)) `
                    -Case 'cross-transfer responder binding duplicate'

                $launchMutations = @(
                    [pscustomobject]@{ From = 'duplicate=0'; To = 'duplicate=1' },
                    [pscustomobject]@{ From = 'tuple_mutation=0'; To = 'tuple_mutation=1' },
                    [pscustomobject]@{ From = 'responder_waiting_for_peer_probe=0'; To = 'responder_waiting_for_peer_probe=1' }
                )
                foreach ($launchMutation in $launchMutations) {
                    $mutatedLaunch = @($prefix | ForEach-Object {
                            if ($_ -like 'event=runtime_unlock_precommit_probe_launch_requested;*') {
                                $_.Replace([string]$launchMutation.From, [string]$launchMutation.To)
                            }
                            else { $_ }
                        }) + @($invalidated[-2])
                    $mutatedLaunch | Set-Content -LiteralPath $path
                    Assert-NoResponderPeerProbeFailure `
                        -Result (Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)) `
                        -Case ([string]$launchMutation.To)
                }

                $acceptedDecision = 'event=filetransfer_runtime_unlock_responder_peer_probe_route_transition_handoff_decided; direction=inbound; status=Accepted; decision_status=Accepted; decision_reason=runtime_unlock_responder_peer_probe_handoff_bound; session_id=sess; transfer_id=transfer; transaction_generation=1; offer_generation=12; pending_answer_ack_generation=12; payer_decision_id=7; lanes=file; answer_acceptance_source=SemanticAnswerAck; ack_message_id=answer-ack-12; ack_source_fingerprint=ack-fingerprint; peer_probe_frame_fingerprint=probe-frame-fingerprint; launch_generation=6; tuna_path_lease_generation=3; tuna_path_lease_listener_run_id=peer-offer-dialer-3; probe_id=probe-3; probe_state=Acked; probe_transport=tuna; probe_acked_utc_ms=900; responder_handoff_authority_id=handoff; responder_handoff_generation=1; fallback_yield_authority_id=pair; fallback_yield_generation=1; pairing_generation=1; pair_key=pair-key; pair_state=Paired; local_yield_authority_id=local-yield; local_yield_generation=5; local_proof_identity=local-proof; local_frame_fingerprint=local-frame; peer_yield_authority_id=peer-yield; peer_yield_generation=6; peer_observation_identity=peer-observation; peer_frame_fingerprint=peer-frame; peer_owner_key=peer-owner; peer_pair_key=peer-pair; admission_generation=4; pending_precommit_generation=3; activation_authority_mode=responder; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; route_publication_generation=3; route_commit_generation=0; terminal_generation=0; leg_generation=3; transport_epoch=1; bridge_recovery_generation=1; checkpoint_generation=1; checkpoint_request_id=(none); data_session_identity=data-session-1; data_session_generation=1; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; transition_authority_id=(none); transition_generation=0; log_writer_id=writer'
                $currentCompatibilityInvalidation = 'event=filetransfer_runtime_unlock_precommit_probe_launch_handoff_decided; direction=inbound; session_id=sess; transfer_id=transfer; transaction_generation=1; offer_generation=12; launch_generation=6; tuna_path_lease_generation=3; tuna_path_lease_listener_run_id=peer-offer-dialer-3; status=Invalidated; decision_reason=transfer_context_replaced; log_writer_id=writer'
                $typedNoContextInvalidation = 'event=filetransfer_runtime_unlock_responder_peer_probe_route_transition_handoff_decided; direction=inbound; status=Invalidated; decision_status=Invalidated; decision_reason=transfer_context_replaced; session_id=sess; transfer_id=transfer; transaction_generation=1; offer_generation=12; pending_answer_ack_generation=12; payer_decision_id=7; lanes=file; answer_acceptance_source=SemanticAnswerAck; ack_message_id=answer-ack-12; ack_source_fingerprint=ack-fingerprint; peer_probe_frame_fingerprint=probe-frame-fingerprint; launch_generation=6; tuna_path_lease_generation=3; tuna_path_lease_listener_run_id=peer-offer-dialer-3; probe_id=probe-3; probe_state=Acked; probe_transport=tuna; probe_acked_utc_ms=900; fallback_yield_authority_id=(none); fallback_yield_generation=0; pairing_generation=0; pair_key=(none); pair_state=None; responder_handoff_authority_id=(none); responder_handoff_generation=0; admission_generation=0; pending_precommit_generation=0; transition_authority_id=(none); transition_generation=0; activation_authority_mode=none; route=(none); protocol_version=0; live_route_epoch=0; runtime_generation=0; route_publication_generation=0; route_commit_generation=0; terminal_generation=0; leg_generation=0; transport_epoch=0; bridge_recovery_generation=0; checkpoint_request_id=(none); checkpoint_generation=0; data_session_identity=(none); data_session_generation=0; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; log_writer_id=writer'
                $typedNoContext = $prefix + @($typedNoContextInvalidation)
                $typedNoContext | Set-Content -LiteralPath $path
                Assert-AfterBoundResponderPeerProbeFailure `
                    -Result (Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)) `
                    -Case 'typed no-context invalidation'

                $currentNoContext = $prefix + @(
                    $currentCompatibilityInvalidation,
                    $typedNoContextInvalidation,
                    'event=transfer_terminal; direction=inbound; session_id=sess; transfer_id=transfer; terminal_state=failed; log_writer_id=writer'
                )
                $currentNoContext | Set-Content -LiteralPath $path
                Assert-AfterBoundResponderPeerProbeFailure `
                    -Result (Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)) `
                    -Case 'current inbound compatibility plus typed no-context invalidation'

                $dataSessionUnavailableSiblings = $prefix + @(
                    $currentCompatibilityInvalidation.Replace('transfer_context_replaced', 'data_session_unavailable'),
                    $typedNoContextInvalidation.Replace('transfer_context_replaced', 'data_session_unavailable'),
                    'event=transfer_terminal; direction=inbound; session_id=sess; transfer_id=transfer; terminal_state=failed; log_writer_id=writer'
                )
                $dataSessionUnavailableSiblings | Set-Content -LiteralPath $path
                $dataSessionUnavailableSiblingResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($dataSessionUnavailableSiblingResult.Reason -ne 'bound_responder_acked_peer_probe_route_transition_handoff_invalidated' -or
                    -not $dataSessionUnavailableSiblingResult.RuntimeUnlockResponderPeerProbeRouteTransitionHandoffMissingAfterBoundProbe -or
                    [long]$dataSessionUnavailableSiblingResult.EvidenceLineVisitCount -ne $dataSessionUnavailableSiblings.Count) {
                    throw "generic plus typed data-session-unavailable mismatch: $($dataSessionUnavailableSiblingResult | ConvertTo-Json -Compress)"
                }

                $mutatedDecision = $prefix + @(
                    $acceptedDecision,
                    ($acceptedDecision -replace 'data_session_identity=data-session-1', 'data_session_identity=data-session-2')
                )
                $mutatedDecision | Set-Content -LiteralPath $path
                $mutatedResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($mutatedResult.RuntimeUnlockResponderPeerProbeHandoffMissing -or
                    $mutatedResult.RuntimeUnlockResponderPeerProbeBindingMissing -or
                    $mutatedResult.RuntimeUnlockResponderPeerProbeRouteTransitionHandoffMissingAfterBoundProbe) {
                    throw "mutated duplicate responder handoff was accepted: $($mutatedResult | ConvertTo-Json -Compress)"
                }

                $decisionStatusMutation = $prefix + @(
                    $acceptedDecision.Replace('decision_status=Accepted', 'decision_status=Rejected'))
                $decisionStatusMutation | Set-Content -LiteralPath $path
                Assert-AfterBoundResponderPeerProbeFailure `
                    -Result (Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)) `
                    -Case 'conflicting typed coordinator decision status'

                $transitionScope = 'transaction_generation=1; offer_generation=12; launch_generation=6; tuna_path_lease_generation=3; source_route=post_tuna_fallback_v6; source_protocol_version=6; source_transport=regular_nkn; source_live_route_epoch=2; source_runtime_generation=3; source_route_publication_generation=3; source_route_commit_generation=0; source_terminal_generation=0; source_leg_generation=3; source_transport_epoch=1; source_bridge_recovery_generation=1; source_data_session_generation=1; data_session_identity=data-session-1; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; activation_authority_mode=responder; retry_install_generation=0; admission_generation=4; pending_precommit_generation=3; responder_handoff_authority_id=handoff; responder_handoff_generation=1; fallback_yield_authority_id=pair; fallback_yield_generation=1; pairing_generation=1; pair_key=pair-key; checkpoint_generation=1; checkpoint_request_id=(none)'
                $recovered = $prefix + @(
                    $acceptedDecision,
                    "event=filetransfer_route_transition_prepared; direction=inbound; session_id=sess; transfer_id=transfer; authority_id=transition; transition_generation=4; transition_kind=PostTunaFallbackV6ToTunaV4; $transitionScope; log_writer_id=writer",
                    "event=filetransfer_route_transition_draining; direction=inbound; session_id=sess; transfer_id=transfer; authority_id=transition; transition_generation=4; transition_kind=PostTunaFallbackV6ToTunaV4; $transitionScope; log_writer_id=writer",
                    "event=filetransfer_route_transition_committed; direction=inbound; session_id=sess; transfer_id=transfer; authority_id=transition; transition_generation=4; transition_kind=PostTunaFallbackV6ToTunaV4; $transitionScope; log_writer_id=writer",
                    "event=filetransfer_route_transition_disposition_applied; direction=inbound; session_id=sess; transfer_id=transfer; authority_id=transition; transition_generation=4; transition_kind=PostTunaFallbackV6ToTunaV4; $transitionScope; apply_status=applied; resulting_live_route_epoch=3; resulting_runtime_generation=4; resulting_route_publication_generation=4; resulting_route_commit_generation=1; resulting_terminal_generation=0; resulting_leg_generation=4; log_writer_id=writer",
                    'event=filetransfer_live_route_epoch_recovered; direction=inbound; session_id=sess; transfer_id=transfer; live_route_epoch=3; route=file_tuna_v4; protocol_version=4; runtime_generation=4; route_publication_generation=4; route_commit_generation=1; terminal_generation=0; leg_generation=4; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; route_publication_action_kind=RoutePublication; coordinator_decision_reason=filetransfer_route_transition_committed; transition_authority_id=transition; transition_generation=4; projection_source=coordinator_route_transition; log_writer_id=writer'
                )
                $recovered | Set-Content -LiteralPath $path
                $recoveredResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($recoveredResult.RuntimeUnlockResponderPeerProbeHandoffMissing -or
                    $recoveredResult.RuntimeUnlockResponderPeerProbeBindingMissing -or
                    $recoveredResult.RuntimeUnlockResponderPeerProbeRouteTransitionHandoffMissingAfterBoundProbe) {
                    throw "recovered responder peer-probe remained unresolved: $($recoveredResult | ConvertTo-Json -Compress)"
                }

                $recoveryTailStart = $prefix.Count
                $recoveryTail = @($recovered[$recoveryTailStart..($recovered.Count - 1)])
                $preassignedTransitionDecision = $acceptedDecision.Replace(
                    'transition_authority_id=(none); transition_generation=0',
                    'transition_authority_id=transition; transition_generation=4')
                $preassignedTransitionRecovery = $prefix + @($preassignedTransitionDecision) + @($recoveryTail[1..($recoveryTail.Count - 1)])
                $preassignedTransitionRecovery | Set-Content -LiteralPath $path
                Assert-AfterBoundResponderPeerProbeFailure `
                    -Result (Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)) `
                    -Case 'pre-Prepare transition authority mutation'

                $heldDecision = $acceptedDecision.Replace(
                    'status=Accepted; decision_status=Accepted; decision_reason=runtime_unlock_responder_peer_probe_handoff_bound',
                    'status=Held; decision_status=Held; decision_reason=inbound_owner_work_pending')
                $heldTerminal = $prefix + @(
                    $heldDecision,
                    'event=transfer_terminal; direction=inbound; session_id=sess; transfer_id=transfer; terminal_state=failed; log_writer_id=writer'
                )
                $heldTerminal | Set-Content -LiteralPath $path
                Assert-AfterBoundResponderPeerProbeFailure `
                    -Result (Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)) `
                    -Case 'terminal Held responder handoff'

                $heldThenRecovered = $heldTerminal + $recoveryTail
                $heldThenRecovered | Set-Content -LiteralPath $path
                Assert-NoResponderPeerProbeFailure `
                    -Result (Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)) `
                    -Case 'exact recovery after terminal Held'

                $oppositeDirectionTerminalThenRecovered = $prefix + @(
                    'event=transfer_terminal; direction=outbound; session_id=sess; transfer_id=transfer; terminal_state=failed; log_writer_id=writer'
                ) + $recoveryTail
                $oppositeDirectionTerminalThenRecovered | Set-Content -LiteralPath $path
                Assert-NoResponderPeerProbeFailure `
                    -Result (Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)) `
                    -Case 'opposite-direction terminal'

                $inboundTerminalThenRecovery = $prefix + @(
                    'event=transfer_terminal; direction=inbound; session_id=sess; transfer_id=transfer; terminal_state=failed; log_writer_id=writer'
                ) + $recoveryTail
                $inboundTerminalThenRecovery | Set-Content -LiteralPath $path
                Assert-AfterBoundResponderPeerProbeFailure `
                    -Result (Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)) `
                    -Case 'exact inbound terminal recovery veto'

                $intrinsicInboundTerminalThenRecovery = $prefix + @(
                    'event=file_transfer_inbound_terminal; session_id=sess; transfer_id=transfer; state=failed; log_writer_id=writer'
                ) + $recoveryTail
                $intrinsicInboundTerminalThenRecovery | Set-Content -LiteralPath $path
                Assert-AfterBoundResponderPeerProbeFailure `
                    -Result (Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)) `
                    -Case 'intrinsic inbound terminal recovery veto'

                $runtimeTerminalThenRecovery = $prefix + @(
                    'event=filetransfer_runtime_terminalized; direction=inbound; session_id=sess; transfer_id=transfer; log_writer_id=writer'
                ) + $recoveryTail
                $runtimeTerminalThenRecovery | Set-Content -LiteralPath $path
                Assert-AfterBoundResponderPeerProbeFailure `
                    -Result (Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)) `
                    -Case 'runtime terminal recovery veto'

                $runtimeOwnerChangedThenRecovery = $prefix + @(
                    $acceptedDecision,
                    'event=filetransfer_runtime_owner_changed; direction=inbound; session_id=sess; transfer_id=transfer; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=4; route_publication_generation=3; route_commit_generation=0; terminal_generation=0; leg_generation=3; transport_epoch=1; bridge_recovery_generation=1; checkpoint_generation=1; checkpoint_request_id=(none); data_session_identity=data-session-1; data_session_generation=1; log_writer_id=writer'
                ) + @($recoveryTail[1..($recoveryTail.Count - 1)])
                $runtimeOwnerChangedThenRecovery | Set-Content -LiteralPath $path
                Assert-AfterBoundResponderPeerProbeFailure `
                    -Result (Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)) `
                    -Case 'runtime owner replacement recovery veto'

                $mutatedDrain = @($recovered | ForEach-Object {
                        if ($_ -like 'event=filetransfer_route_transition_draining;*') {
                            $_.Replace('retry_install_generation=0', 'retry_install_generation=1')
                        }
                        else { $_ }
                    })
                $mutatedDrain | Set-Content -LiteralPath $path
                Assert-AfterBoundResponderPeerProbeFailure `
                    -Result (Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)) `
                    -Case 'mutated Draining responder scope'

                $projectionOnly = @($recovered | ForEach-Object {
                        if ($_ -like 'event=filetransfer_live_route_epoch_recovered;*') {
                            $_.Replace('event=filetransfer_live_route_epoch_recovered', 'event=filetransfer_live_route_epoch_started')
                        }
                        else { $_ }
                    })
                $projectionOnly | Set-Content -LiteralPath $path
                Assert-AfterBoundResponderPeerProbeFailure `
                    -Result (Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)) `
                    -Case 'projection-only route publication'
                Write-Output 'gui-bound-responder-peer-probe-fast-path-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(result.ExitCode == 0, $"Bound responder peer-probe GUI fast path failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-bound-responder-peer-probe-fast-path-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeFailureClassification_PostFallbackGenericPauseUsesIndexedProofBeforeDeadline()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var resultBody = ExtractPowerShellFunctionBody(guiText, "New-TunaGuiFileTransferFailureClassificationResult", "Get-TunaGuiExplicitFailureClassification");
        var explicitBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiExplicitFailureClassification", "Resolve-TunaGuiFailureClassification");
        var classifierBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiFileTransferSetupFailureClassification", "Get-TunaGuiFailureClassificationBudgetMilliseconds");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function New-TunaGuiFileTransferFailureClassificationResult { {{resultBody}} }
            function Get-TunaGuiExplicitFailureClassification { {{explicitBody}} }
            function Get-TunaGuiFileTransferSetupFailureClassification { {{classifierBody}} }
            function Get-AppLogLinesAfterBookmark { throw 'live ledger was read' }
            function Read-GuiSmokeProcessOutputLinesSnapshot { throw 'process output was read' }
            function Test-TunaGuiBridgeBootstrapNotReadyEvidence { return $false }
            function Test-TunaGuiLatestFallbackFrontierRequestDispatchMissing { return $false }
            function Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing { return $false }
            function Test-TunaGuiLogLinesContain { return $false }
            function Get-TunaGuiReadinessStateAfterBookmark {
                return [pscustomobject]@{ ListenerUnavailable = $false; ListenerReady = $false; TunaActive = $false }
            }
            function Resolve-TunaGuiFailureClassification {
                param($ErrorMessage, $HistoricalPhase, $HistoricalReason)
                return [pscustomobject]@{ Phase = $HistoricalPhase; Reason = $HistoricalReason }
            }
            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-post-fallback-generic-pause-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $path = Join-Path $tempRoot 'evidence.log'
                $writer = 'writer-a'
                @(
                    "event=runtime_unlock_retry_dispatch_authority_granted; authority_id=retry-a; session_id=sess; transfer_id=transfer; direction=outbound; source_publication_route=post_tuna_fallback_v6; source_publication_protocol_version=6; source_publication_live_route_epoch=2; source_publication_runtime_generation=3; source_publication_terminal_generation=0; source_publication_leg_generation=3; source_publication_generation=3; log_writer_id=$writer; log_writer_sequence=4",
                    "event=filetransfer_route_transition_disposition_applied; authority_id=transition-a; transition_generation=3; direction=outbound; session_id=sess; transfer_id=transfer; source_route=post_tuna_fallback_v6; source_protocol_version=6; disposition=rolledback; apply_status=applied; log_writer_id=$writer; log_writer_sequence=5",
                    "event=tuna_payer_decision_started; payer_decision_id=10; reason=remote_payer_intent; log_writer_id=$writer; log_writer_sequence=6",
                    "event=tuna_acceleration_payer_intent_queued; intent=will_listen; trigger=remote_payer_intent; payer_decision_id=10; log_writer_id=$writer; log_writer_sequence=7",
                    "event=filetransfer_tuna_activation_negotiation_regular_nkn_paused; session_id=sess; reason=activation_negotiation_pending; trigger=offer_send_prepare; log_writer_id=$writer; log_writer_sequence=8",
                    "event=filetransfer_data_session_availability_observed; session_id=sess; transfer_id=transfer; is_available=0; reason=tuna_activation_negotiating; log_writer_id=$writer; log_writer_sequence=9",
                    "event=filetransfer_post_tuna_fallback_tuna_activation_pause_suppressed; direction=outbound; session_id=sess; transfer_id=transfer; route=post_tuna_fallback_v6; protocol_version=6; recovery_active=0; log_writer_id=$writer; log_writer_sequence=10",
                    "event=filetransfer_fallback_frontier_repair_authority_granted; session_id=sess; transfer_id=transfer; source_kind=SenderTimeoutRetry; log_writer_id=$writer; log_writer_sequence=11",
                    'event=filetransfer_gui_progress_timeout; session_id=sess; transfer_id=transfer; reason=normal_to_tuna_live_epoch_missing'
                ) | Set-Content -LiteralPath $path
                $result = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'gui_smoke_scenario_deadline_exceeded' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($result.Phase -ne 'route_runtime' -or
                    $result.Reason -ne 'post_tuna_fallback_regular_nkn_paused_by_stale_retry_receipt_despite_current_pause_suppression' -or
                    $result.ClassificationSource -ne 'immutable_evidence_post_fallback_generic_pause_index') {
                    throw "post-fallback generic pause fast path mismatch: $($result | ConvertTo-Json -Compress)"
                }

                @("event=filetransfer_tuna_activation_negotiation_regular_nkn_paused; session_id=sess; trigger=offer_send_prepare; log_writer_id=$writer; log_writer_sequence=8") |
                    Set-Content -LiteralPath $path
                $singleton = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'gui_smoke_scenario_deadline_exceeded' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($singleton.Reason -eq 'post_tuna_fallback_regular_nkn_paused_by_stale_retry_receipt_despite_current_pause_suppression') {
                    throw 'singleton pause was classified without authority and suppression evidence'
                }
                Write-Output 'gui-post-fallback-generic-pause-fast-path-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(
            result.ExitCode == 0,
            $"Post-fallback generic-pause GUI fast path failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-post-fallback-generic-pause-fast-path-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeFailureClassification_PostFallbackRetryBlockedByGenericUsesIndexedProofBeforeDeadline()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var resultBody = ExtractPowerShellFunctionBody(guiText, "New-TunaGuiFileTransferFailureClassificationResult", "Get-TunaGuiExplicitFailureClassification");
        var explicitBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiExplicitFailureClassification", "Resolve-TunaGuiFailureClassification");
        var classifierBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiFileTransferSetupFailureClassification", "Get-TunaGuiFailureClassificationBudgetMilliseconds");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function New-TunaGuiFileTransferFailureClassificationResult { {{resultBody}} }
            function Get-TunaGuiExplicitFailureClassification { {{explicitBody}} }
            function Get-TunaGuiFileTransferSetupFailureClassification { {{classifierBody}} }
            function Get-AppLogLinesAfterBookmark { throw 'live ledger was read' }
            function Read-GuiSmokeProcessOutputLinesSnapshot { throw 'process output was read' }
            function Test-TunaGuiBridgeBootstrapNotReadyEvidence { return $false }
            function Test-TunaGuiLatestFallbackFrontierRequestDispatchMissing { return $false }
            function Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing { return $false }
            function Test-TunaGuiLogLinesContain { return $false }
            function Get-TunaGuiReadinessStateAfterBookmark {
                return [pscustomobject]@{ ListenerUnavailable = $false; ListenerReady = $false; TunaActive = $false }
            }
            function Resolve-TunaGuiFailureClassification {
                param($ErrorMessage, $HistoricalPhase, $HistoricalReason)
                return [pscustomobject]@{ Phase = $HistoricalPhase; Reason = $HistoricalReason }
            }
            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-retry-blocked-generic-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $path = Join-Path $tempRoot 'evidence.log'
                $writer = 'writer-a'
                @(
                    "event=filetransfer_route_selected; session_id=sess; transfer_id=transfer; direction=outbound; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; route_publication_generation=3; log_writer_id=$writer",
                    "event=filetransfer_fallback_tail_local_proof_receipt_decided; authority_id=yield-a; yield_generation=1; log_writer_id=$writer",
                    "event=filetransfer_fallback_tail_peer_proof_receipt_decided; authority_id=yield-a; yield_generation=1; log_writer_id=$writer",
                    "event=tuna_payer_decision_started; payer_decision_id=6; reason=runtime_unlock; log_writer_id=$writer",
                    "event=runtime_unlock_filetransfer_owner_accepted_before_path_lease; session_id=sess; transfer_id=transfer; direction=outbound; route=post_tuna_fallback_v6; trigger=runtime_unlock; log_writer_id=$writer",
                    "event=tuna_acceleration_payer_intent_queued; payer_decision_id=6; intent=will_listen; trigger=runtime_unlock; lanes=file; log_writer_id=$writer",
                    "event=tuna_path_lease_started; session_id=sess; transfer_id=transfer; payer_decision_id=6; transaction_generation=0; offer_generation=0; lease_generation=2; listener_run_id=listener-2; reason=runtime_unlock; log_writer_id=$writer",
                    "event=filetransfer_fallback_tail_pairing_committed; session_id=sess; transfer_id=transfer; authority_id=yield-a; yield_generation=1; yield_kind=Quiescent; decision_status=Accepted; log_writer_id=$writer",
                    "event=filetransfer_fallback_tail_paired_yield_granted; authority_id=yield-a; yield_generation=1; log_writer_id=$writer",
                    "event=runtime_unlock_retry_dispatch_authority_request_queued; session_id=sess; transfer_id=transfer; contract_generation=1; queue_generation=1; source_route=post_tuna_fallback_v6; source_route_publication_generation=3; log_writer_id=$writer",
                    "event=filetransfer_fallback_paired_tail_yield_runtime_unlock_retry_requested; session_id=sess; transfer_id=transfer; authority_id=yield-a; yield_generation=1; decision_status=Queued; log_writer_id=$writer",
                    "event=runtime_unlock_retry_negotiation_slot_decided; session_id=sess; transfer_id=transfer; decision_status=Deferred; intent_kind=CoordinatorPreparedRequest; intent_generation=1; slot_generation=0; contract_generation=1; source_route=post_tuna_fallback_v6; source_publication_generation=3; decision_reason=retry_dispatch_negotiation_slot_busy; log_writer_id=$writer",
                    'event=filetransfer_gui_progress_timeout; session_id=sess; transfer_id=transfer; reason=normal_to_tuna_live_epoch_missing'
                ) | Set-Content -LiteralPath $path
                $result = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'gui_smoke_scenario_deadline_exceeded' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($result.Phase -ne 'route_runtime' -or
                    $result.Reason -ne 'fallback_paired_tail_yield_retry_intent_blocked_by_preexisting_initial_activation' -or
                    $result.ClassificationSource -ne 'immutable_evidence_post_fallback_retry_blocked_by_generic_index') {
                    throw "post-fallback retry-blocked fast path mismatch: $($result | ConvertTo-Json -Compress)"
                }

                @("event=runtime_unlock_retry_negotiation_slot_decided; decision_status=Deferred; intent_kind=CoordinatorPreparedRequest; intent_generation=1; slot_generation=0; decision_reason=retry_dispatch_negotiation_slot_busy; log_writer_id=$writer") |
                    Set-Content -LiteralPath $path
                $singleton = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'gui_smoke_scenario_deadline_exceeded' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($singleton.Reason -eq 'fallback_paired_tail_yield_retry_intent_blocked_by_preexisting_initial_activation') {
                    throw 'singleton slot-busy event was classified without paired and generic ownership evidence'
                }
                Write-Output 'gui-post-fallback-retry-blocked-fast-path-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(
            result.ExitCode == 0,
            $"Post-fallback retry-blocked GUI fast path failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-post-fallback-retry-blocked-fast-path-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeFailureClassification_PostFallbackRetryIntentNotRecheckedUsesIndexedProofBeforeDeadline()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var resultBody = ExtractPowerShellFunctionBody(guiText, "New-TunaGuiFileTransferFailureClassificationResult", "Get-TunaGuiExplicitFailureClassification");
        var explicitBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiExplicitFailureClassification", "Resolve-TunaGuiFailureClassification");
        var classifierBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiFileTransferSetupFailureClassification", "Get-TunaGuiFailureClassificationBudgetMilliseconds");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function New-TunaGuiFileTransferFailureClassificationResult { {{resultBody}} }
            function Get-TunaGuiExplicitFailureClassification { {{explicitBody}} }
            function Get-TunaGuiFileTransferSetupFailureClassification { {{classifierBody}} }
            function Get-AppLogLinesAfterBookmark { throw 'live ledger was read' }
            function Read-GuiSmokeProcessOutputLinesSnapshot { throw 'process output was read' }
            function Test-TunaGuiBridgeBootstrapNotReadyEvidence { return $false }
            function Test-TunaGuiLatestFallbackFrontierRequestDispatchMissing { return $false }
            function Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing { return $false }
            function Test-TunaGuiLogLinesContain { return $false }
            function Get-TunaGuiReadinessStateAfterBookmark {
                return [pscustomobject]@{ ListenerUnavailable = $false; ListenerReady = $false; TunaActive = $false }
            }
            function Resolve-TunaGuiFailureClassification {
                param($ErrorMessage, $HistoricalPhase, $HistoricalReason)
                return [pscustomobject]@{ Phase = $HistoricalPhase; Reason = $HistoricalReason }
            }
            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-retry-recheck-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $path = Join-Path $tempRoot 'evidence.log'
                $writer = 'writer-a'
                @(
                    "event=filetransfer_route_selected; session_id=sess; transfer_id=transfer; direction=outbound; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; route_publication_generation=3; log_writer_id=$writer",
                    "event=tuna_runtime_locked; source=header; stop_requested=1; reason=header_switch_off; log_writer_id=$writer",
                    "event=filetransfer_fallback_tail_pairing_committed; session_id=sess; transfer_id=transfer; authority_id=yield-a; yield_generation=1; yield_kind=Quiescent; decision_status=Accepted; log_writer_id=$writer",
                    "event=filetransfer_fallback_tail_paired_yield_granted; authority_id=yield-a; yield_generation=1; log_writer_id=$writer",
                    "event=runtime_unlock_retry_dispatch_authority_request_queued; session_id=sess; transfer_id=transfer; contract_generation=1; queue_generation=1; source_route=post_tuna_fallback_v6; source_route_publication_generation=3; log_writer_id=$writer",
                    "event=runtime_unlock_retry_negotiation_slot_decided; session_id=sess; transfer_id=transfer; decision_status=Deferred; intent_kind=CoordinatorPreparedRequest; intent_generation=1; slot_generation=0; contract_generation=1; source_route=post_tuna_fallback_v6; source_publication_generation=3; decision_reason=retry_dispatch_authority_backoff_pending; log_writer_id=$writer",
                    "event=runtime_unlock_retry_negotiation_slot_decided; session_id=sess; transfer_id=transfer; decision_status=Deferred; intent_kind=CoordinatorPreparedRequest; intent_generation=1; slot_generation=0; contract_generation=1; source_route=post_tuna_fallback_v6; source_publication_generation=3; decision_reason=retry_dispatch_negotiation_slot_ineligible; log_writer_id=$writer",
                    "event=runtime_unlock_post_fallback_negotiation_deferred; session_id=sess; transfer_id=transfer; direction=outbound; decision_status=Deferred; authority_mode=InitialActivation; authority_effect=none; trigger=runtime_unlock; decision_reason=post_fallback_retry_authority_pending; source_route=post_tuna_fallback_v6; source_publication_generation=3; log_writer_id=$writer",
                    'event=filetransfer_gui_progress_timeout; session_id=sess; transfer_id=transfer; reason=normal_to_tuna_live_epoch_missing'
                ) | Set-Content -LiteralPath $path
                $result = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'gui_smoke_scenario_deadline_exceeded' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($result.Phase -ne 'route_runtime' -or
                    $result.Reason -ne 'fallback_paired_tail_yield_retry_intent_not_rechecked_after_runtime_unlock_eligibility_restored' -or
                    $result.ClassificationSource -ne 'immutable_evidence_post_fallback_retry_intent_recheck_index') {
                    throw "post-fallback retry recheck fast path mismatch: $($result | ConvertTo-Json -Compress)"
                }

                @("event=runtime_unlock_post_fallback_negotiation_deferred; decision_status=Deferred; trigger=runtime_unlock; decision_reason=post_fallback_retry_authority_pending; source_route=post_tuna_fallback_v6; log_writer_id=$writer") |
                    Set-Content -LiteralPath $path
                $singleton = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'gui_smoke_scenario_deadline_exceeded' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($singleton.Reason -eq 'fallback_paired_tail_yield_retry_intent_not_rechecked_after_runtime_unlock_eligibility_restored') {
                    throw 'singleton unlock deferral was classified without exact intent history'
                }
                Write-Output 'gui-post-fallback-retry-recheck-fast-path-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(
            result.ExitCode == 0,
            $"Post-fallback retry-recheck GUI fast path failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-post-fallback-retry-recheck-fast-path-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeFailureClassification_CurrentListenerAdoptionUsesExactIndexedProjection()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var resultBody = ExtractPowerShellFunctionBody(guiText, "New-TunaGuiFileTransferFailureClassificationResult", "Get-TunaGuiExplicitFailureClassification");
        var explicitBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiExplicitFailureClassification", "Resolve-TunaGuiFailureClassification");
        var classifierBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiFileTransferSetupFailureClassification", "Get-TunaGuiFailureClassificationBudgetMilliseconds");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function New-TunaGuiFileTransferFailureClassificationResult { {{resultBody}} }
            function Get-TunaGuiExplicitFailureClassification { {{explicitBody}} }
            function Get-TunaGuiFileTransferSetupFailureClassification { {{classifierBody}} }
            function Get-AppLogLinesAfterBookmark { throw 'live ledger was read' }
            function Read-GuiSmokeProcessOutputLinesSnapshot { throw 'process output was read' }
            function Test-TunaGuiBridgeBootstrapNotReadyEvidence { return $false }
            function Test-TunaGuiLatestFallbackFrontierRequestDispatchMissing { return $false }
            function Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing { return $false }
            function Test-TunaGuiLogLinesContain { return $false }
            function Get-TunaGuiReadinessStateAfterBookmark {
                return [pscustomobject]@{ ListenerUnavailable = $false; ListenerReady = $false; TunaActive = $false }
            }
            function Resolve-TunaGuiFailureClassification {
                param($ErrorMessage, $HistoricalPhase, $HistoricalReason)
                return [pscustomobject]@{ Phase = $HistoricalPhase; Reason = $HistoricalReason }
            }
            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-current-listener-adoption-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $path = Join-Path $tempRoot 'evidence.log'
                $writer = 'writer-a'
                $scope = 'session_id=sess; transfer_id=transfer; direction=outbound; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; transport_epoch=1; bridge_recovery_generation=1; bridge_decision_generation=2; route_publication_generation=3; route_commit_generation=0; contract_generation=1; receive_recovery_operation_generation=0; data_session_generation=0; checkpoint_request_id=(none)'
                $releaseScope = $scope.Replace('data_session_generation=0', 'data_session_generation=1')
                $release = 'release_authority_id=pair; release_generation=1; release_kind=FallbackPairedTailYield'
                $lines = @(
                    "event=tuna_path_lease_started; session_id=sess; transfer_id=transfer; transaction_generation=0; offer_generation=0; lease_generation=2; listener_run_id=listener-start-2; payer_decision_id=7; state=starting; reason=runtime_unlock; log_writer_id=$writer",
                    "event=tuna_sidecar_process_started; role=listener; pid=21084; run_id=process-run-2; log_writer_id=$writer",
                    "event=tuna_path_lease_listener_ready; session_id=sess; transfer_id=transfer; transaction_generation=0; offer_generation=0; lease_generation=2; listener_run_id=listener-start-2; payer_decision_id=7; state=listenerready; current=1; reason=runtime_unlock; log_writer_id=$writer",
                    "event=filetransfer_fallback_tail_paired_release_reserved; $releaseScope; authority_id=pair; pairing_generation=1; retry_authority_id=retry-a; decision_status=Accepted; log_writer_id=$writer",
                    "event=runtime_unlock_retry_dispatch_install_committed; $scope; authority_id=retry-a; predecessor_authority_id=(none); install_generation=1; $release; install_status=Installed; log_writer_id=$writer",
                    "event=runtime_unlock_retry_dispatch_install_confirmed; $scope; authority_id=retry-a; predecessor_authority_id=(none); install_generation=1; $release; decision_reason=coordinator_install_committed; log_writer_id=$writer",
                    "event=filetransfer_fallback_tail_paired_release_consumed; $releaseScope; authority_id=pair; pairing_generation=1; retry_authority_id=retry-a; install_generation=1; decision_status=Accepted; log_writer_id=$writer",
                    "event=runtime_unlock_retry_dispatch_authority_granted; $scope; authority_id=retry-a; install_generation=1; $release; listener_rearm_requirement=None; listener_execution_mode=AdoptCurrentCoordinatorScoped; log_writer_id=$writer",
                    "event=runtime_unlock_retry_dispatch_listener_adoption_decided; $scope; authority_id=retry-a; install_generation=1; $release; listener_rearm_requirement=None; listener_execution_mode=AdoptCurrentCoordinatorScoped; status=Held; physical_process_id=0; decision_reason=current_coordinator_scoped_listener_missing; log_writer_id=$writer"
                )
                $lines | Set-Content -LiteralPath $path
                $result = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'evidence_enrichment_timeout; child_exit_code_unavailable' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($result.Phase -ne 'route_runtime' -or
                    $result.Reason -ne 'live_coordinator_scoped_initial_activation_listener_reported_missing_for_fallback_paired_retry' -or
                    -not $result.RuntimeUnlockRetryCurrentListenerAdoptionMissing -or
                    -not $result.RuntimeUnlockRetryCurrentListenerAdoptionLiveProjectionMissing -or
                    $result.ClassificationSource -ne 'immutable_evidence_current_listener_adoption_index' -or
                    [long]$result.EvidenceLineVisitCount -ne 9) {
                    throw "current-listener adoption fast path mismatch: $($result | ConvertTo-Json -Compress)"
                }

                $explicitPath = Join-Path $tempRoot 'explicit-only.log'
                @('event=file_transfer_inbound_terminal; direction=inbound; session_id=other; transfer_id=other; state=Failed; log_writer_id=writer') |
                    Set-Content -LiteralPath $explicitPath
                $restartExplicit = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'failure_class=runtime_unlock_retry_dispatch_current_listener_adoption_missing; failure_variant=listener_rearm_none_restarted_current_coordinator_scoped_listener_after_unlock_expired' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($explicitPath)
                if (-not $restartExplicit.RuntimeUnlockRetryCurrentListenerAdoptionMissing -or
                    -not $restartExplicit.RuntimeUnlockRetryCurrentListenerAdoptionRestartExpired -or
                    $restartExplicit.RuntimeUnlockRetryCurrentListenerAdoptionLiveProjectionMissing -or
                    $restartExplicit.Reason -ne 'installed_fallback_retry_did_not_adopt_current_coordinator_scoped_listener') {
                    throw "existing restart-expired adoption variant was not preserved: $($restartExplicit | ConvertTo-Json -Compress)"
                }

                $stopped = @($lines[0..7]) + @(
                    "event=tuna_sidecar_stop_requested; role=listener; pid=21084; run_id=process-run-2; log_writer_id=$writer",
                    $lines[8]
                )
                $stopped | Set-Content -LiteralPath $path
                $stoppedResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($stoppedResult.RuntimeUnlockRetryCurrentListenerAdoptionMissing) {
                    throw 'stopped listener was treated as current adoption evidence'
                }

                $ambiguous = @($lines[0..2]) + @(
                    "event=tuna_path_lease_started; session_id=sess; transfer_id=transfer; transaction_generation=0; offer_generation=0; lease_generation=4; listener_run_id=listener-start-4; payer_decision_id=8; state=starting; reason=runtime_unlock; log_writer_id=$writer",
                    "event=tuna_sidecar_process_started; role=listener; pid=21085; run_id=process-run-4; log_writer_id=$writer",
                    "event=tuna_path_lease_listener_ready; session_id=sess; transfer_id=transfer; transaction_generation=0; offer_generation=0; lease_generation=4; listener_run_id=listener-start-4; payer_decision_id=8; state=listenerready; current=1; reason=runtime_unlock; log_writer_id=$writer"
                ) + @($lines[3..8])
                $ambiguous | Set-Content -LiteralPath $path
                $ambiguousResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($ambiguousResult.RuntimeUnlockRetryCurrentListenerAdoptionMissing) {
                    throw 'ambiguous live initial-activation listeners were treated as one current listener'
                }

                $recovered = @($lines) + @(
                    "event=runtime_unlock_retry_dispatch_install_committed; $scope; authority_id=retry-b; predecessor_authority_id=retry-a; install_generation=2; $release; install_status=Installed; log_writer_id=$writer",
                    "event=runtime_unlock_retry_dispatch_install_confirmed; $scope; authority_id=retry-b; predecessor_authority_id=retry-a; install_generation=2; $release; decision_reason=coordinator_install_committed; log_writer_id=$writer",
                    "event=runtime_unlock_retry_dispatch_authority_granted; $scope; authority_id=retry-b; predecessor_authority_id=retry-a; install_generation=2; $release; listener_rearm_requirement=None; listener_execution_mode=AdoptCurrentCoordinatorScoped; log_writer_id=$writer",
                    "event=runtime_unlock_retry_dispatch_listener_adoption_decided; $scope; authority_id=retry-b; predecessor_authority_id=retry-a; install_generation=2; $release; listener_rearm_requirement=None; listener_execution_mode=AdoptCurrentCoordinatorScoped; status=Accepted; physical_process_id=21084; physical_process_run_id=process-run-2; physical_process_started_utc_ms=123456; previous_request_identity=request-a; successor_request_identity=request-b; decision_reason=current_coordinator_scoped_listener_exact; log_writer_id=$writer",
                    "event=runtime_unlock_retry_dispatch_listener_adoption_applied; $scope; authority_id=retry-b; predecessor_authority_id=retry-a; install_generation=2; $release; status=Accepted; physical_process_id=21084; physical_process_run_id=process-run-2; physical_process_started_utc_ms=123456; tuna_address=tuna://listener; previous_request_identity=request-a; successor_request_identity=request-b; decision_reason=current_coordinator_scoped_listener_adopted; log_writer_id=$writer",
                    "event=tuna_path_lease_started; session_id=sess; transfer_id=transfer; lease_generation=3; listener_run_id=listener-start-3; state=starting; current=1; log_writer_id=$writer",
                    "event=tuna_path_lease_listener_ready; session_id=sess; transfer_id=transfer; lease_generation=3; listener_run_id=listener-start-3; state=listenerready; current=1; log_writer_id=$writer",
                    "event=runtime_unlock_retry_dispatch_observed; $scope; authority_id=retry-b; install_generation=2; $release; offer_generation=1; offer_nonce=nonce-1; decision_reason=peer_visible_offer_send_observed; log_writer_id=$writer",
                    'event=tuna_acceleration_offer_received_raw; msg_id=offer-wire-1; log_writer_id=peer-writer',
                    'event=runtime_unlock_tuna_dial_attempt_started; session_id=sess; payer_decision_id=9; nonce_len=7; dial_attempt_generation=1; log_writer_id=peer-writer',
                    'event=tuna_acceleration_answer_sent; accepted=1; payer_decision_id=9; generation=1; inbound_msg_id=offer-wire-1; msg_id=answer-wire-1; target_peer_fingerprint=source-peer; log_writer_id=peer-writer',
                    "event=tuna_acceleration_answer_received_raw; msg_id=answer-wire-1; log_writer_id=$writer",
                    "event=runtime_unlock_offer_authority_boundary_decided; session_id=sess; transfer_id=transfer; boundary=answer_received; authority_mode=InstalledRetry; decision_status=accepted; retry_authority_id=retry-b; install_generation=2; offer_generation=1; transaction_generation=1; payer_decision_id=9; tuna_path_lease_generation=3; tuna_path_lease_listener_run_id=listener-start-3; log_writer_id=$writer",
                    "event=runtime_unlock_transaction_answer_received; session_id=sess; transfer_id=transfer; offer_generation=1; transaction_generation=1; tuna_path_lease_generation=3; tuna_path_lease_listener_run_id=listener-start-3; answer_received=1; tuna_path_lease_current=1; log_writer_id=$writer",
                    "event=tuna_acceleration_answer_ack_sent; session_id=sess; payer_decision_id=9; generation=1; msg_id=ack-1; target_peer_fingerprint=peer; log_writer_id=$writer",
                    'event=tuna_acceleration_answer_ack_received_raw; msg_id=ack-1; log_writer_id=peer-writer',
                    'event=tuna_acceleration_answer_ack_received; session_id=sess; payer_decision_id=9; log_writer_id=peer-writer',
                    "event=runtime_unlock_precommit_probe_launch_requested; session_id=sess; transfer_id=transfer; direction=outbound; offer_generation=1; transaction_generation=1; tuna_path_lease_generation=3; tuna_path_lease_listener_run_id=listener-start-3; log_writer_id=$writer",
                    "event=filetransfer_route_transition_prepared; session_id=sess; transfer_id=transfer; direction=outbound; source_route=post_tuna_fallback_v6; source_protocol_version=6; source_live_route_epoch=2; source_runtime_generation=3; source_terminal_generation=0; source_leg_generation=3; source_transport_epoch=1; source_bridge_recovery_generation=1; source_route_publication_generation=3; source_route_commit_generation=0; source_data_session_generation=1; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; retry_install_generation=2; fallback_yield_authority_id=pair; fallback_yield_generation=1; decision_status=Prepared; log_writer_id=$writer"
                )
                $recovered | Set-Content -LiteralPath $path
                $recoveredResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($recoveredResult.RuntimeUnlockRetryCurrentListenerAdoptionMissing) {
                    throw 'exact generation-2 adopted offer/receipt/answer/ACK/precommit/Prepared lineage did not recover the candidate'
                }

                $startedMutation = @($recovered)
                $startedMutation[13] = $startedMutation[13].Replace('physical_process_started_utc_ms=123456', 'physical_process_started_utc_ms=123457')
                $startedMutation | Set-Content -LiteralPath $path
                $startedMutationResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if (-not $startedMutationResult.RuntimeUnlockRetryCurrentListenerAdoptionMissing) {
                    throw 'mutated listener physical start time recovered the held candidate'
                }

                $crossRelease = @($recovered)
                $crossRelease[11] = $crossRelease[11].Replace('release_authority_id=pair', 'release_authority_id=other-pair')
                $crossRelease | Set-Content -LiteralPath $path
                $crossReleaseResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if (-not $crossReleaseResult.RuntimeUnlockRetryCurrentListenerAdoptionMissing) {
                    throw 'cross-release generation-2 adoption recovered the held candidate'
                }

                $crossLineage = @($recovered)
                $crossLineage[12] = $crossLineage[12].Replace('predecessor_authority_id=retry-a', 'predecessor_authority_id=retry-z')
                $crossLineage | Set-Content -LiteralPath $path
                $crossLineageResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if (-not $crossLineageResult.RuntimeUnlockRetryCurrentListenerAdoptionMissing) {
                    throw 'cross-lineage generation-2 adoption recovered the held candidate'
                }

                $crossPreparedScope = @($recovered)
                $crossPreparedScope[-1] = $crossPreparedScope[-1].Replace('runtime_generation=3', 'runtime_generation=4')
                $crossPreparedScope | Set-Content -LiteralPath $path
                $crossPreparedResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if (-not $crossPreparedResult.RuntimeUnlockRetryCurrentListenerAdoptionMissing) {
                    throw 'cross-scope Prepared recovered the held candidate'
                }

                $runtimeReplaced = @($recovered[0..8]) + @(
                    "event=filetransfer_runtime_started; $($scope.Replace('runtime_generation=3', 'runtime_generation=4').Replace('route_publication_generation=3', 'route_publication_generation=4')); log_writer_id=$writer"
                ) + @($recovered[9..($recovered.Count - 1)])
                $runtimeReplaced | Set-Content -LiteralPath $path
                $runtimeReplacedResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if (-not $runtimeReplacedResult.RuntimeUnlockRetryCurrentListenerAdoptionMissing) {
                    throw 'runtime/publication replacement before adoption recovered the held candidate'
                }

                $missingRawReceipt = @($recovered | Where-Object { $_ -notlike 'event=tuna_acceleration_offer_received_raw*' })
                $missingRawReceipt | Set-Content -LiteralPath $path
                $missingRawResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if (-not $missingRawResult.RuntimeUnlockRetryCurrentListenerAdoptionMissing) {
                    throw 'local offer lifecycle recovered without a raw peer receipt'
                }

                $missingAckReceipt = @($recovered | Where-Object { $_ -notlike 'event=tuna_acceleration_answer_ack_received_raw*' })
                $missingAckReceipt | Set-Content -LiteralPath $path
                $missingAckResult = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if (-not $missingAckResult.RuntimeUnlockRetryCurrentListenerAdoptionMissing) {
                    throw 'local ACK submission recovered without a responder raw ACK receipt'
                }

                Write-Output 'gui-current-listener-adoption-fast-path-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(
            result.ExitCode == 0,
            $"Current-listener adoption GUI fast path failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-current-listener-adoption-fast-path-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task GuiSmokeFailureClassification_PostFallbackStallGraceUsesExactIndexedFastPath(
        bool recovered,
        bool expectMissing)
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var resultBody = ExtractPowerShellFunctionBody(guiText, "New-TunaGuiFileTransferFailureClassificationResult", "Get-TunaGuiExplicitFailureClassification");
        var explicitBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiExplicitFailureClassification", "Resolve-TunaGuiFailureClassification");
        var classifierBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiFileTransferSetupFailureClassification", "Get-TunaGuiFailureClassificationBudgetMilliseconds");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function New-TunaGuiFileTransferFailureClassificationResult { {{resultBody}} }
            function Get-TunaGuiExplicitFailureClassification { {{explicitBody}} }
            function Get-TunaGuiFileTransferSetupFailureClassification { {{classifierBody}} }
            function Get-AppLogLinesAfterBookmark { throw 'live ledger was read' }
            function Read-GuiSmokeProcessOutputLinesSnapshot { throw 'process output was read' }
            function Test-TunaGuiBridgeBootstrapNotReadyEvidence { return $false }
            function Test-TunaGuiLatestFallbackFrontierRequestDispatchMissing { return $false }
            function Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing { return $false }
            function Test-TunaGuiLogLinesContain { return $false }
            function Get-TunaGuiReadinessStateAfterBookmark {
                return [pscustomobject]@{ ListenerUnavailable = $false; ListenerReady = $false; TunaActive = $false }
            }
            function Resolve-TunaGuiFailureClassification {
                param($ErrorMessage, $HistoricalPhase, $HistoricalReason)
                return [pscustomobject]@{ Phase = $HistoricalPhase; Reason = $HistoricalReason }
            }
            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-frontier-stall-grace-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $path = Join-Path $tempRoot 'evidence.log'
                $scope = 'direction=inbound; session_id=sess; transfer_id=transfer; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; runtime_generation=5; terminal_generation=0; leg_generation=5; transport_epoch=1; bridge_recovery_generation=1; bridge_decision_generation=0; checkpoint_request_id=(none); route_publication_generation=5; route_commit_generation=0; log_writer_id=writer'
                $repair = "authority_id=repair; repair_generation=4; repair_request_key=fallback-frontier:40:1:range; range_fingerprint=range; frontier_chunk_index=40; source_kind=ReceiverRequest; primary_protocol_repair_request_id=v6-frontier:40:1; primary_request_correlation_id=correlation; $scope"
                $lines = @(
                    "event=filetransfer_fallback_frontier_repair_authority_granted; $repair",
                    "event=filetransfer_fallback_frontier_repair_contract_settled; $repair; settlement_outcome=ProgressObserved",
                    "event=filetransfer_fallback_frontier_repair_progress_observed; $repair; previous_frontier_chunk_index=40; current_frontier_chunk_index=41",
                    "event=filetransfer_v6_fallback_frontier_repair_progress_state_forced; $repair; previous_frontier_chunk_index=40; current_frontier_chunk_index=41; receiver_state_epoch=7; data_session_identity=data-session; data_session_generation=1; decision_status=Accepted",
                    "event=filetransfer_v6_receiver_state_sent; $scope; epoch=7; contiguous_committed_chunk_index=41; durable_received_highest_chunk_index=60; log_writer_attempt_utc_ticks=639000000000000000",
                    'event=filetransfer_v6_frontier_request_deferred; direction=inbound; session_id=sess; transfer_id=transfer; frontier_chunk_index=41; highest_received_chunk_index=60; elapsed_ms=0; stall_grace_ms=750; stall_started_utc=2026-04-26T11:17:59Z; log_writer_id=writer; log_writer_attempt_utc_ticks=639000000001000000'
                )
                if (${{recovered.ToString().ToLowerInvariant()}}) {
                    $lines += 'event=filetransfer_v6_sparse_write_committed; transfer_id=transfer; session_id=sess; written_chunk_count=1; contiguous_chunks_committed=1; next_chunk_index=42; highest_received_chunk_index=60; log_writer_id=writer; log_writer_attempt_utc_ticks=639000000002000000'
                    $lines += 'event=file_transfer_inbound_terminal; transfer_id=transfer; session_id=sess; state=Completed; error_code=(none); saved_path=C:\received\payload.bin; log_writer_id=writer'
                }
                $lines | Set-Content -LiteralPath $path
                $result = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ([bool]$result.FallbackFrontierStallGraceRecheckMissing -ne ${{expectMissing.ToString().ToLowerInvariant()}}) {
                    throw "stall-grace indexed result mismatch: $($result | ConvertTo-Json -Compress)"
                }
                if (${{expectMissing.ToString().ToLowerInvariant()}} -and
                    ($result.Phase -ne 'fallback_frontier_repair' -or
                        $result.Reason -ne 'post_tuna_fallback_frontier_stall_grace_expired_without_exact_receiver_request_recheck' -or
                        $result.ClassificationSource -ne 'immutable_evidence_fallback_frontier_stall_grace_recheck_index' -or
                        [long]$result.EvidenceLineVisitCount -ne $lines.Count)) {
                    throw "stall-grace indexed precedence mismatch: $($result | ConvertTo-Json -Compress)"
                }
                Write-Output 'gui-frontier-stall-grace-fast-path-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(
            result.ExitCode == 0,
            $"Stall-grace GUI fast path failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-frontier-stall-grace-fast-path-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task GuiSmokeFailureClassification_PostFallbackTerminalFrontierUsesExactIndexedFastPath(
        bool omitFinalize,
        bool expectMissing)
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var resultBody = ExtractPowerShellFunctionBody(guiText, "New-TunaGuiFileTransferFailureClassificationResult", "Get-TunaGuiExplicitFailureClassification");
        var explicitBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiExplicitFailureClassification", "Resolve-TunaGuiFailureClassification");
        var classifierBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiFileTransferSetupFailureClassification", "Get-TunaGuiFailureClassificationBudgetMilliseconds");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function New-TunaGuiFileTransferFailureClassificationResult { {{resultBody}} }
            function Get-TunaGuiExplicitFailureClassification { {{explicitBody}} }
            function Get-TunaGuiFileTransferSetupFailureClassification { {{classifierBody}} }
            function Get-AppLogLinesAfterBookmark { throw 'live ledger was read' }
            function Read-GuiSmokeProcessOutputLinesSnapshot { throw 'process output was read' }
            function Test-TunaGuiBridgeBootstrapNotReadyEvidence { return $false }
            function Test-TunaGuiLatestFallbackFrontierRequestDispatchMissing { return $false }
            function Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing { return $false }
            function Test-TunaGuiLogLinesContain { return $false }
            function Get-TunaGuiReadinessStateAfterBookmark {
                return [pscustomobject]@{ ListenerUnavailable = $false; ListenerReady = $false; TunaActive = $false }
            }
            function Resolve-TunaGuiFailureClassification {
                param($ErrorMessage, $HistoricalPhase, $HistoricalReason)
                return [pscustomobject]@{ Phase = $HistoricalPhase; Reason = $HistoricalReason }
            }
            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-terminal-frontier-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $path = Join-Path $tempRoot 'evidence.log'
                $scope = 'direction=inbound; session_id=sess; transfer_id=transfer; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; runtime_generation=5; terminal_generation=0; leg_generation=5; transport_epoch=1; bridge_recovery_generation=1; bridge_decision_generation=0; checkpoint_request_id=(none); route_publication_generation=5; route_commit_generation=0; log_writer_id=writer'
                $predecessor = "authority_id=repair-predecessor; repair_generation=4; repair_request_key=fallback-frontier:40:1:predecessor; range_fingerprint=predecessor; frontier_chunk_index=40; durable_received_highest_chunk_index=60; source_kind=ReceiverRequest; primary_protocol_repair_request_id=v6-frontier:40:1; primary_request_correlation_id=correlation-predecessor; $scope"
                $successor = "authority_id=repair-successor; repair_generation=5; repair_request_key=fallback-frontier:41:1:successor; range_fingerprint=successor; frontier_chunk_index=41; durable_received_highest_chunk_index=60; source_kind=ReceiverRequest; primary_protocol_repair_request_id=v6-frontier:41:2; primary_request_correlation_id=correlation-successor; $scope"
                $lines = @(
                    "event=filetransfer_fallback_frontier_repair_authority_granted; $predecessor",
                    "event=filetransfer_fallback_frontier_repair_contract_settled; $predecessor; settlement_outcome=ProgressObserved",
                    "event=filetransfer_fallback_frontier_repair_progress_observed; $predecessor; previous_frontier_chunk_index=40; current_frontier_chunk_index=41",
                    "event=filetransfer_v6_fallback_frontier_repair_progress_state_forced; $predecessor; previous_frontier_chunk_index=40; current_frontier_chunk_index=41; receiver_state_epoch=7; data_session_identity=data-session; data_session_generation=1; decision_status=Accepted",
                    "event=filetransfer_v6_receiver_state_sent; $scope; epoch=7; contiguous_committed_chunk_index=41; durable_received_highest_chunk_index=60; log_writer_attempt_utc_ticks=639000000000000000",
                    "event=filetransfer_v6_frontier_request_deferred; $scope; data_session_identity=data-session; data_session_generation=1; frontier_chunk_index=41; highest_received_chunk_index=60; elapsed_ms=0; stall_grace_ms=750; stall_started_utc=2026-04-26T11:17:59.0000000+00:00; wake_generation=8; wake_deadline_utc=2026-04-26T11:17:59.7500000+00:00; log_writer_attempt_utc_ticks=639000000001000000",
                    "event=filetransfer_fallback_frontier_repair_authority_granted; $successor",
                    "event=filetransfer_control_plane_delivery_observed; direction=inbound; session_id=sess; transfer_id=transfer; log_writer_id=writer; kind=frontier_request; reason=frontier_stall_grace_elapsed; repair_request_id=v6-frontier:41:2; request_correlation_id=correlation-successor; peer_visible_any=1; accepted_any=1",
                    "event=filetransfer_fallback_frontier_repair_dispatched; $successor; dispatch_observed=1",
                    "event=filetransfer_v6_frontier_request_sent; $successor; repair_request_id=v6-frontier:41:2; request_correlation_id=correlation-successor; reason=frontier_stall_grace_elapsed",
                    "event=filetransfer_v6_frontier_stall_grace_recheck_decided; $scope; data_session_identity=data-session; data_session_generation=1; frontier_chunk_index=41; highest_received_chunk_index=60; stall_started_utc=2026-04-26T11:17:59.0000000+00:00; wake_generation=8; wake_deadline_utc=2026-04-26T11:17:59.7500000+00:00; decision_status=dispatched; decision_reason=exact_receiver_request_dispatched; authority_effect=none",
                    "event=filetransfer_fallback_frontier_repair_contract_settled; $successor; settlement_outcome=ProgressObserved",
                    "event=filetransfer_fallback_frontier_repair_progress_observed; $successor; previous_frontier_chunk_index=41; current_frontier_chunk_index=61",
                    'event=filetransfer_fallback_tail_terminal_proof_consumed; direction=inbound; session_id=sess; transfer_id=transfer; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=5; transport_epoch=1; committed_chunk=61; chunk_count=61; log_writer_id=writer',
                    'event=integrity_verify_passed; direction=inbound; session_id=sess; transfer_id=transfer; file_size_bytes=268435456; log_writer_id=writer'
                )
                if (-not ${{omitFinalize.ToString().ToLowerInvariant()}}) {
                    $lines += 'event=temp_finalize_succeeded; direction=inbound; session_id=sess; transfer_id=transfer; file_size_bytes=268435456; saved_path=C:\received\payload.bin; log_writer_id=writer'
                }
                $lines += 'event=file_transfer_inbound_terminal; session_id=sess; transfer_id=transfer; state=Completed; error_code=(none); saved_path=C:\received\payload.bin; log_writer_id=writer'
                $lines | Set-Content -LiteralPath $path
                $result = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ([bool]$result.FallbackFrontierStallGraceRecheckMissing -ne ${{expectMissing.ToString().ToLowerInvariant()}}) {
                    throw "terminal-frontier indexed result mismatch: $($result | ConvertTo-Json -Compress)"
                }
                Write-Output 'gui-terminal-frontier-fast-path-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(
            result.ExitCode == 0,
            $"Terminal-frontier GUI fast path failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-terminal-frontier-fast-path-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeFailureClassification_PostFallbackDuplicateStateReplayUsesIndexedFastPath()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var resultBody = ExtractPowerShellFunctionBody(guiText, "New-TunaGuiFileTransferFailureClassificationResult", "Get-TunaGuiExplicitFailureClassification");
        var explicitBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiExplicitFailureClassification", "Resolve-TunaGuiFailureClassification");
        var classifierBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiFileTransferSetupFailureClassification", "Get-TunaGuiFailureClassificationBudgetMilliseconds");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function New-TunaGuiFileTransferFailureClassificationResult { {{resultBody}} }
            function Get-TunaGuiExplicitFailureClassification { {{explicitBody}} }
            function Get-TunaGuiFileTransferSetupFailureClassification { {{classifierBody}} }
            function Get-AppLogLinesAfterBookmark { throw 'live ledger was read' }
            function Read-GuiSmokeProcessOutputLinesSnapshot { throw 'process output was read' }
            function Test-TunaGuiBridgeBootstrapNotReadyEvidence { return $false }
            function Test-TunaGuiLatestFallbackFrontierRequestDispatchMissing { return $false }
            function Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing { return $false }
            function Test-TunaGuiLogLinesContain { return $false }
            function Get-TunaGuiReadinessStateAfterBookmark {
                return [pscustomobject]@{ ListenerUnavailable = $false; ListenerReady = $false; TunaActive = $false }
            }
            function Resolve-TunaGuiFailureClassification {
                param($ErrorMessage, $HistoricalPhase, $HistoricalReason)
                return [pscustomobject]@{ Phase = $HistoricalPhase; Reason = $HistoricalReason }
            }
            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-duplicate-state-replay-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $path = Join-Path $tempRoot 'evidence.log'
                $scope = 'direction=outbound; session_id=sess; transfer_id=transfer; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; runtime_generation=5; terminal_generation=0; leg_generation=5; transport_epoch=1; bridge_recovery_generation=1; route_publication_generation=5; route_commit_generation=0; log_writer_id=writer'
                $lines = @(
                    "event=filetransfer_v4_state_received; $scope; epoch=8; applied=1; stale=0; duplicate=0; v4_state_wire_fingerprint=v4sf1-state; contiguous_committed_chunk_index=40",
                    "event=filetransfer_v4_state_received; $scope; epoch=8; applied=0; duplicate=1; v4_state_wire_fingerprint=v4sf1-state; contiguous_committed_chunk_index=40",
                    "event=filetransfer_fallback_frontier_repair_authority_granted; $scope; authority_id=repair-a; repair_generation=1; repair_request_key=fallback-frontier:42:64:range; range_fingerprint=range; frontier_chunk_index=42; requested_chunk_count=64; source_kind=SenderLivenessRescue; authority_decision_reason=duplicate_frontier_rebind; protocol_repair_request_id=transport_rebind_safety_replay:1:40:42:64",
                    'event=filetransfer_transport_rebind_safety_replay_started; session_id=sess; transfer_id=transfer; log_writer_id=writer; reason=duplicate_frontier_rebind; rebind_generation=1; remote_next_expected_chunk_index=40; replay_start_chunk_index=42; replay_end_chunk_exclusive=106; requested_chunk_count=64; scheduled_chunk_count=64; post_tuna_v6_frontier_sweep=1'
                )
                $lines | Set-Content -LiteralPath $path
                $result = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if (-not [bool]$result.PostTunaFallbackDuplicateStateReplayAmplification -or
                    $result.Phase -ne 'fallback_frontier_repair' -or
                    $result.Reason -ne 'duplicate_current_receiver_state_created_sender_liveness_replay_authority' -or
                    $result.FailureVariant -ne 'redundant_control_bulk_state_copy_started_64_chunk_repair_sweep' -or
                    $result.ClassificationSource -ne 'immutable_evidence_post_tuna_fallback_duplicate_state_replay_index') {
                    throw "duplicate-state replay indexed result mismatch: $($result | ConvertTo-Json -Compress)"
                }
                Write-Output 'gui-duplicate-state-replay-fast-path-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(
            result.ExitCode == 0,
            $"Duplicate-state replay GUI fast path failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-duplicate-state-replay-fast-path-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unresolved", true)]
    [InlineData("recovered", false)]
    [InlineData("mutated_duplicate", false)]
    [InlineData("correlation_alias", false)]
    [InlineData("cross_checkpoint", false)]
    [InlineData("scope_mutation", true)]
    [InlineData("settlement_mutation", true)]
    [InlineData("pair_mutation", true)]
    [InlineData("idempotent_recheck", false)]
    [InlineData("incoherent_recheck", true)]
    [InlineData("reservation_only", true)]
    [InlineData("wrong_recheck_order", true)]
    [InlineData("production_repair_scope", false)]
    [InlineData("stale_scope_callback", true)]
    [InlineData("settled_before_acceptance", false)]
    [InlineData("advanced_before_acceptance", false)]
    [InlineData("settled_before_rejection", false)]
    [InlineData("wrong_settlement_progress_order", true)]
    public async Task GuiSmokeFailureClassification_CheckpointAcceptanceFrontierRecheckUsesExactIndexedFastPath(
        string scenario,
        bool expectMissing)
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var resultBody = ExtractPowerShellFunctionBody(guiText, "New-TunaGuiFileTransferFailureClassificationResult", "Get-TunaGuiExplicitFailureClassification");
        var explicitBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiExplicitFailureClassification", "Resolve-TunaGuiFailureClassification");
        var classifierBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiFileTransferSetupFailureClassification", "Get-TunaGuiFailureClassificationBudgetMilliseconds");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function New-TunaGuiFileTransferFailureClassificationResult { {{resultBody}} }
            function Get-TunaGuiExplicitFailureClassification { {{explicitBody}} }
            function Get-TunaGuiFileTransferSetupFailureClassification { {{classifierBody}} }
            function Get-AppLogLinesAfterBookmark { throw 'live ledger was read' }
            function Read-GuiSmokeProcessOutputLinesSnapshot { throw 'process output was read' }
            function Test-TunaGuiBridgeBootstrapNotReadyEvidence { return $false }
            function Test-TunaGuiLatestFallbackFrontierRequestDispatchMissing { return $false }
            function Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing { return $false }
            function Test-TunaGuiLogLinesContain { return $false }
            function Get-TunaGuiReadinessStateAfterBookmark {
                return [pscustomobject]@{ ListenerUnavailable = $false; ListenerReady = $false; TunaActive = $false }
            }
            function Resolve-TunaGuiFailureClassification {
                param($ErrorMessage, $HistoricalPhase, $HistoricalReason)
                return [pscustomobject]@{ Phase = $HistoricalPhase; Reason = $HistoricalReason }
            }
            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-checkpoint-frontier-recheck-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $path = Join-Path $tempRoot 'evidence.log'
                $scope = 'session_id=sess; transfer_id=transfer; direction=outbound; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; transport_epoch=1; bridge_recovery_generation=1; route_publication_generation=3; route_commit_generation=0; data_session_identity=data:1; data_session_generation=1'
                $peerScope = 'session_id=sess; transfer_id=transfer; direction=inbound; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; transport_epoch=1; bridge_recovery_generation=1; route_publication_generation=3; route_commit_generation=0'
                $lines = @(
                    "event=filetransfer_receive_recovery_operation_issued; $scope; receive_recovery_operation_authority_id=recovery; receive_recovery_operation_generation=3; checkpoint_request_id=(none); log_writer_id=writer",
                    'event=filetransfer_receive_recovery_fallback_checkpoint_bound; session_id=sess; transfer_id=transfer; direction=outbound; recovery_operation_authority_id=recovery; recovery_operation_generation=3; effective_checkpoint_request_id=checkpoint; checkpoint_binding_generation=1; checkpoint_binding_state=pending; frame_fingerprint=checkpoint-frame; transport_epoch=1; log_writer_id=writer',
                    "event=filetransfer_fallback_frontier_repair_authority_granted; $peerScope; authority_id=peer-78; predecessor_authority_id=peer-77; repair_generation=87; frontier_chunk_index=2151; range_count=2; source_kind=ReceiverRequest; primary_protocol_repair_request_id=request-78; primary_request_correlation_id=correlation-78; log_writer_id=peer-writer",
                    "event=filetransfer_v6_frontier_request_receive_dispatch_rejected; $scope; checkpoint_request_id=checkpoint; repair_request_id=request-78; request_correlation_id=correlation-78; first_start_chunk_index=2151; first_chunk_count=3; range_count=2; received_transport=regular_nkn; decision_reason=checkpoint_pending_frontier_request; log_writer_id=writer",
                    "event=filetransfer_fallback_frontier_repair_authority_granted; $peerScope; authority_id=peer-79; predecessor_authority_id=peer-78; repair_generation=88; frontier_chunk_index=2151; range_count=2; source_kind=ReceiverRequest; primary_protocol_repair_request_id=request-79; primary_request_correlation_id=correlation-79; log_writer_id=peer-writer",
                    "event=filetransfer_v6_frontier_request_receive_dispatch_rejected; $scope; checkpoint_request_id=checkpoint; repair_request_id=request-79; request_correlation_id=correlation-79; first_start_chunk_index=2151; first_chunk_count=3; range_count=2; received_transport=regular_nkn; decision_reason=checkpoint_pending_frontier_request; log_writer_id=writer",
                    "event=filetransfer_v6_frontier_request_receive_dispatch_rejected; $scope; checkpoint_request_id=checkpoint; repair_request_id=request-79; request_correlation_id=correlation-79; first_start_chunk_index=2151; first_chunk_count=3; range_count=2; received_transport=regular_nkn; decision_reason=checkpoint_pending_frontier_request; log_writer_id=writer",
                    "event=filetransfer_fallback_frontier_repair_authority_granted; $peerScope; authority_id=peer-80; predecessor_authority_id=peer-79; repair_generation=89; frontier_chunk_index=2151; range_count=2; source_kind=ReceiverRequest; primary_protocol_repair_request_id=request-80; primary_request_correlation_id=correlation-80; log_writer_id=peer-writer",
                    "event=filetransfer_v6_frontier_request_receive_dispatch_rejected; $scope; checkpoint_request_id=checkpoint; repair_request_id=request-80; request_correlation_id=correlation-80; first_start_chunk_index=2151; first_chunk_count=3; range_count=2; received_transport=regular_nkn; decision_reason=checkpoint_pending_frontier_request; log_writer_id=writer",
                    "event=filetransfer_v6_frontier_request_receive_dispatch_rejected; $scope; checkpoint_request_id=checkpoint; repair_request_id=request-80; request_correlation_id=correlation-80; first_start_chunk_index=2151; first_chunk_count=3; range_count=2; received_transport=regular_nkn; decision_reason=checkpoint_pending_frontier_request; log_writer_id=writer",
                    "event=filetransfer_fallback_frontier_repair_authority_granted; $peerScope; authority_id=peer-81; predecessor_authority_id=peer-80; repair_generation=90; frontier_chunk_index=2151; range_count=2; source_kind=ReceiverRequest; primary_protocol_repair_request_id=request-81; primary_request_correlation_id=correlation-81; log_writer_id=peer-writer",
                    "event=filetransfer_receive_recovery_fallback_checkpoint_frontier_request_hold_decided; $scope; recovery_operation_authority_id=recovery; recovery_operation_generation=3; effective_checkpoint_request_id=checkpoint; checkpoint_binding_generation=1; checkpoint_binding_state=pending; checkpoint_frame_fingerprint=checkpoint-frame; repair_request_id=request-81; request_correlation_id=correlation-81; range_fingerprint=range-81; frontier_frame_fingerprint=frontier-frame-81; hold_generation=4; dispatch_path=NativeV6; received_transport=regular_nkn; status=accepted; outcome=held; log_writer_id=writer",
                    "event=filetransfer_v6_frontier_request_receive_dispatch_rejected; $scope; checkpoint_request_id=checkpoint; repair_request_id=request-81; request_correlation_id=correlation-81; first_start_chunk_index=2151; first_chunk_count=3; range_count=2; received_transport=regular_nkn; decision_reason=checkpoint_pending_frontier_request; log_writer_id=writer",
                    "event=filetransfer_v6_frontier_request_receive_dispatch_rejected; $scope; checkpoint_request_id=checkpoint; repair_request_id=request-81; request_correlation_id=correlation-81; first_start_chunk_index=2151; first_chunk_count=3; range_count=2; received_transport=regular_nkn; decision_reason=checkpoint_pending_frontier_request; log_writer_id=writer",
                    'event=filetransfer_receive_recovery_fallback_checkpoint_lifecycle_advanced; session_id=sess; transfer_id=transfer; direction=outbound; recovery_operation_authority_id=recovery; recovery_operation_generation=3; effective_checkpoint_request_id=checkpoint; checkpoint_binding_generation=1; checkpoint_binding_state=accepted; frame_fingerprint=checkpoint-frame; transport_epoch=1; log_writer_id=writer',
                    "event=filetransfer_fallback_tail_yield_blocked_by_frontier_obligation; $scope; checkpoint_request_id=(none); fallback_start_chunk_index=1878; committed_chunk_index=2151; highest_observed_chunk_index=2561; log_writer_id=writer"
                )
                $recoveryLines = @(
                    "event=filetransfer_receive_recovery_fallback_checkpoint_frontier_request_recheck_issued; $scope; recovery_operation_authority_id=recovery; recovery_operation_generation=3; effective_checkpoint_request_id=checkpoint; checkpoint_binding_generation=1; checkpoint_binding_state=accepted; checkpoint_frame_fingerprint=checkpoint-frame; repair_request_id=request-81; request_correlation_id=correlation-81; range_fingerprint=range-81; frontier_frame_fingerprint=frontier-frame-81; hold_generation=4; recheck_generation=1; dispatch_path=NativeV6; received_transport=regular_nkn; status=issued; log_writer_id=writer",
                    "event=filetransfer_fallback_frontier_repair_authority_granted; $scope; authority_id=repair; source_kind=PeerRequest; primary_protocol_repair_request_id=request-81; primary_request_correlation_id=correlation-81; range_fingerprint=range-81; frontier_chunk_index=2151; log_writer_id=writer",
                    "event=filetransfer_receive_recovery_fallback_checkpoint_frontier_request_recheck_applied; $scope; recovery_operation_authority_id=recovery; recovery_operation_generation=3; effective_checkpoint_request_id=checkpoint; checkpoint_binding_generation=1; checkpoint_binding_state=accepted; checkpoint_frame_fingerprint=checkpoint-frame; repair_request_id=request-81; request_correlation_id=correlation-81; range_fingerprint=range-81; frontier_frame_fingerprint=frontier-frame-81; hold_generation=4; recheck_generation=1; dispatch_path=NativeV6; received_transport=regular_nkn; receipt_status=applied; decision_status=accepted; outcome=applied; log_writer_id=writer",
                    "event=filetransfer_fallback_frontier_repair_dispatched; $scope; authority_id=repair; dispatch_observed=1; log_writer_id=writer",
                    "event=filetransfer_fallback_frontier_repair_contract_settled; $scope; authority_id=repair; successor_authority_id=(none); settlement_outcome=ProgressObserved; dispatch_observed=1; log_writer_id=writer",
                    "event=filetransfer_fallback_frontier_repair_progress_observed; $scope; authority_id=repair; previous_frontier_chunk_index=2151; current_frontier_chunk_index=2154; log_writer_id=writer",
                    "event=filetransfer_fallback_tail_frontier_repair_cleared; $scope; previous_frontier_only_start_chunk_index=2151; committed_chunk=2154; highest_observed_chunk=2561; log_writer_id=writer",
                    "event=filetransfer_fallback_tail_pairing_committed; $scope; authority_id=pair; yield_generation=1; pairing_generation=1; pair_key=pair-key; decision_status=Accepted; log_writer_id=writer",
                    "event=filetransfer_fallback_tail_paired_release_reserved; $scope; authority_id=pair; yield_generation=1; pairing_generation=1; pair_key=pair-key; reservation_generation=1; retry_authority_id=retry; decision_status=Accepted; log_writer_id=writer",
                    "event=filetransfer_fallback_tail_paired_release_consumed; $scope; authority_id=pair; yield_generation=1; pairing_generation=1; pair_key=pair-key; reservation_generation=1; retry_authority_id=retry; install_generation=1; decision_status=Accepted; log_writer_id=writer"
                )
                switch ('{{scenario}}') {
                    'recovered' {
                        $lines += $recoveryLines
                    }
                    'mutated_duplicate' {
                        $mutated = $false
                        $lines = @($lines | ForEach-Object {
                            if (-not $mutated -and
                                $_ -like '*event=filetransfer_v6_frontier_request_receive_dispatch_rejected*' -and
                                $_ -like '*repair_request_id=request-79*') {
                                $mutated = $true
                                $_.Replace('first_chunk_count=3', 'first_chunk_count=4')
                            }
                            else { $_ }
                        })
                    }
                    'correlation_alias' {
                        $acceptedIndex = 0
                        for (; $acceptedIndex -lt $lines.Count; $acceptedIndex++) {
                            if ($lines[$acceptedIndex] -like '*checkpoint_binding_state=accepted*') { break }
                        }
                        $alias = @(
                            "event=filetransfer_fallback_frontier_repair_authority_granted; $peerScope; authority_id=peer-alias; predecessor_authority_id=peer-81; repair_generation=91; frontier_chunk_index=2151; range_count=2; source_kind=ReceiverRequest; primary_protocol_repair_request_id=request-alias; primary_request_correlation_id=correlation-81; log_writer_id=peer-writer",
                            "event=filetransfer_v6_frontier_request_receive_dispatch_rejected; $scope; checkpoint_request_id=checkpoint; repair_request_id=request-alias; request_correlation_id=correlation-81; first_start_chunk_index=2151; first_chunk_count=3; range_count=2; received_transport=regular_nkn; decision_reason=checkpoint_pending_frontier_request; log_writer_id=writer"
                        )
                        $lines = @($lines[0..($acceptedIndex - 1)] + $alias + $lines[$acceptedIndex..($lines.Count - 1)])
                    }
                    'cross_checkpoint' {
                        $lines = @($lines | ForEach-Object {
                            if ($_ -like '*checkpoint_binding_state=accepted*') {
                                $_.Replace('effective_checkpoint_request_id=checkpoint', 'effective_checkpoint_request_id=other-checkpoint')
                            }
                            else { $_ }
                        })
                    }
                    'scope_mutation' {
                        $lines += @($recoveryLines | ForEach-Object {
                            if ($_ -like '*source_kind=PeerRequest*') { $_.Replace('runtime_generation=3', 'runtime_generation=4') }
                            else { $_ }
                        })
                    }
                    'settlement_mutation' {
                        $lines += @($recoveryLines | ForEach-Object {
                            if ($_ -like '*repair_contract_settled*') {
                                $_.Replace('settlement_outcome=ProgressObserved', 'settlement_outcome=Superseded').Replace('successor_authority_id=(none)', 'successor_authority_id=successor')
                            }
                            else { $_ }
                        })
                    }
                    'pair_mutation' {
                        $lines += @($recoveryLines | ForEach-Object {
                            if ($_ -like '*paired_release_reserved*') { $_.Replace('pair_key=pair-key', 'pair_key=other-pair') }
                            else { $_ }
                        })
                    }
                    'idempotent_recheck' {
                        $lines += @($recoveryLines | ForEach-Object {
                            if ($_ -like '*frontier_request_recheck_applied*') {
                                $_.Replace('receipt_status=applied', 'receipt_status=idempotent').Replace('decision_status=accepted', 'decision_status=idempotent').Replace('outcome=applied', 'outcome=idempotent')
                            }
                            else { $_ }
                        })
                    }
                    'incoherent_recheck' {
                        $lines += @($recoveryLines | ForEach-Object {
                            if ($_ -like '*frontier_request_recheck_applied*') {
                                $_.Replace('receipt_status=applied', 'receipt_status=idempotent')
                            }
                            else { $_ }
                        })
                    }
                    'reservation_only' {
                        $lines += @($recoveryLines | Where-Object { $_ -notlike '*paired_release_consumed*' })
                    }
                    'wrong_recheck_order' {
                        $lines += @(
                            $recoveryLines[0]
                            $recoveryLines[2]
                            $recoveryLines[1]
                        ) + @($recoveryLines[3..($recoveryLines.Count - 1)])
                    }
                    'production_repair_scope' {
                        $lines += @($recoveryLines | ForEach-Object {
                            if ($_ -like '*filetransfer_fallback_frontier_repair_authority_granted*' -or
                                $_ -like '*filetransfer_fallback_frontier_repair_dispatched*' -or
                                $_ -like '*filetransfer_fallback_frontier_repair_progress_observed*' -or
                                $_ -like '*filetransfer_fallback_frontier_repair_contract_settled*') {
                                $_.Replace('; data_session_identity=data:1; data_session_generation=1', '')
                            }
                            else { $_ }
                        })
                    }
                    'stale_scope_callback' {
                        $mutatedScope = $scope.Replace('runtime_generation=3', 'runtime_generation=4')
                        $lines += "event=filetransfer_runtime_started; $mutatedScope; log_writer_id=writer"
                        $lines += $recoveryLines
                    }
                    'settled_before_acceptance' {
                        $acceptedIndex = 0
                        for (; $acceptedIndex -lt $lines.Count; $acceptedIndex++) {
                            if ($lines[$acceptedIndex] -like '*checkpoint_binding_state=accepted*') { break }
                        }
                        $settled = "event=filetransfer_fallback_frontier_repair_contract_settled; $peerScope; authority_id=peer-81; successor_authority_id=peer-82; settlement_outcome=Superseded; log_writer_id=peer-writer"
                        $lines = @($lines[0..($acceptedIndex - 1)] + $settled + $lines[$acceptedIndex..($lines.Count - 1)])
                    }
                    'advanced_before_acceptance' {
                        $acceptedIndex = 0
                        for (; $acceptedIndex -lt $lines.Count; $acceptedIndex++) {
                            if ($lines[$acceptedIndex] -like '*checkpoint_binding_state=accepted*') { break }
                        }
                        $advanced = "event=filetransfer_v6_frontier_request_advanced_remote_frontier; $scope; repair_request_id=request-81; request_correlation_id=correlation-81; log_writer_id=writer"
                        $lines = @($lines[0..($acceptedIndex - 1)] + $advanced + $lines[$acceptedIndex..($lines.Count - 1)])
                    }
                    'settled_before_rejection' {
                        $peerGrantIndex = 0
                        for (; $peerGrantIndex -lt $lines.Count; $peerGrantIndex++) {
                            if ($lines[$peerGrantIndex] -like '*authority_id=peer-81*' -and
                                $lines[$peerGrantIndex] -like '*source_kind=ReceiverRequest*') { break }
                        }
                        $settled = "event=filetransfer_fallback_frontier_repair_contract_settled; $peerScope; authority_id=peer-81; successor_authority_id=(none); settlement_outcome=ProgressObserved; log_writer_id=peer-writer"
                        $lines = @($lines[0..$peerGrantIndex] + $settled + $lines[($peerGrantIndex + 1)..($lines.Count - 1)])
                    }
                    'wrong_settlement_progress_order' {
                        $settlementIndex = 0
                        for (; $settlementIndex -lt $recoveryLines.Count; $settlementIndex++) {
                            if ($recoveryLines[$settlementIndex] -like '*repair_contract_settled*') { break }
                        }
                        $wrongOrder = @($recoveryLines)
                        $wrongOrder[$settlementIndex] = $recoveryLines[$settlementIndex + 1]
                        $wrongOrder[$settlementIndex + 1] = $recoveryLines[$settlementIndex]
                        $lines += $wrongOrder
                    }
                }
                $lines | Set-Content -LiteralPath $path
                $result = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'evidence_enrichment_timeout; child_exit_code_unavailable' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ([bool]$result.FallbackFrontierCheckpointAcceptanceRecheckMissing -ne ${{expectMissing.ToString().ToLowerInvariant()}} -or
                    [bool]$result.CheckpointPendingFrontierRequestNotRecheckedAfterCheckpointAcceptance -ne ${{expectMissing.ToString().ToLowerInvariant()}}) {
                    throw "checkpoint frontier recheck scenario '{{scenario}}' mismatch: $($result | ConvertTo-Json -Compress)"
                }
                if (${{expectMissing.ToString().ToLowerInvariant()}} -and
                    ($result.Phase -ne 'route_runtime' -or
                        $result.Reason -ne 'checkpoint_pending_peer_frontier_request_not_rechecked_after_exact_checkpoint_acceptance' -or
                        $result.ClassificationSource -ne 'immutable_evidence_fallback_frontier_checkpoint_acceptance_recheck_index' -or
                        [long]$result.EvidenceLineVisitCount -ne $lines.Count)) {
                    throw "checkpoint frontier recheck precedence mismatch: $($result | ConvertTo-Json -Compress)"
                }
                @('event=file_transfer_inbound_terminal; direction=inbound; session_id=other; transfer_id=other; state=Failed; log_writer_id=writer') |
                    Set-Content -LiteralPath $path
                $explicit = Get-TunaGuiFileTransferSetupFailureClassification `
                    -Bookmark 0 `
                    -ErrorMessage 'failure_class=filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing' `
                    -RouteMode 'live-regular-activation-cycle' `
                    -ImmutableEvidencePaths @($path)
                if (-not $explicit.FallbackFrontierCheckpointAcceptanceRecheckMissing -or
                    $explicit.Phase -ne 'route_runtime' -or
                    $explicit.Reason -ne 'checkpoint_pending_peer_frontier_request_not_rechecked_after_exact_checkpoint_acceptance') {
                    throw "checkpoint frontier explicit classification mismatch: $($explicit | ConvertTo-Json -Compress)"
                }
                Write-Output 'gui-checkpoint-frontier-recheck-fast-path-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(
            result.ExitCode == 0,
            $"Checkpoint frontier-recheck GUI fast path failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-checkpoint-frontier-recheck-fast-path-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GuiSmokeFailureClassification_FallbackRepairHoldBindingUsesIndexedFastPath(
        bool holdBound,
        bool bindingBeforeHeld)
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var resultBody = ExtractPowerShellFunctionBody(guiText, "New-TunaGuiFileTransferFailureClassificationResult", "Get-TunaGuiExplicitFailureClassification");
        var explicitBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiExplicitFailureClassification", "Resolve-TunaGuiFailureClassification");
        var classifierBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiFileTransferSetupFailureClassification", "Get-TunaGuiFailureClassificationBudgetMilliseconds");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function New-TunaGuiFileTransferFailureClassificationResult { {{resultBody}} }
            function Get-TunaGuiExplicitFailureClassification { {{explicitBody}} }
            function Get-TunaGuiFileTransferSetupFailureClassification { {{classifierBody}} }
            function Get-AppLogLinesAfterBookmark { throw 'live ledger was read' }
            function Read-GuiSmokeProcessOutputLinesSnapshot { throw 'process output was read' }
            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-fallback-repair-hold-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $path = Join-Path $tempRoot 'evidence.log'
                $repairScope = 'session_id=sess; transfer_id=transfer; direction=outbound; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; transport_epoch=1; bridge_recovery_generation=1; bridge_decision_generation=2; checkpoint_request_id=(none); route_publication_generation=3; route_commit_generation=0'
                $repairShape = 'repair_request_key=repair-key; range_fingerprint=range; frontier_chunk_index=10; durable_received_highest_chunk_index=9; range_count=1; requested_chunk_count=1; source_kind=PeerRequest; target_transport=regular_nkn'
                $holdTuple = 'contract_generation=1; retired_generation=0; dispatch_attempt_generation=1; missing_dispatch_successor_generation=0; predecessor_authority_id=(none); request_active_predecessor_supersession=0; listener_rearm_requirement=None; listener_execution_mode=Default; retry_reason=fallback_tail; recovery_reason=paired_release; regular_v4_liveness_generation=1; bridge_decision_generation=2; source_session_id=sess; source_transfer_id=transfer; source_direction=outbound; source_route=post_tuna_fallback_v6; source_protocol_version=6; source_live_route_epoch=2; source_runtime_generation=3; source_terminal_generation=0; source_leg_generation=3; source_route_publication_generation=3; source_route_commit_generation=(none); source_route_commit_generation_present=0; source_target_route=file_tuna_v4; source_target_protocol_version=4; source_target_transport=tuna; source_route_publication_action_kind=RuntimeUnlockRetry; source_coordinator_decision_reason=paired_release'
                $bindingTuple = 'contract_generation=1; retired_offer_generation=0; dispatch_attempt_generation=1; missing_dispatch_successor_generation=0; request_predecessor_authority_id=(none); request_active_predecessor_supersession=0; listener_rearm_requirement=None; listener_execution_mode=Default; retry_reason=fallback_tail; recovery_reason=paired_release; regular_v4_liveness_generation=1; request_bridge_decision_generation=2; request_session_id=sess; request_transfer_id=transfer; request_direction=outbound; source_publication_session_id=sess; source_publication_transfer_id=transfer; source_publication_direction=outbound; source_publication_route=post_tuna_fallback_v6; source_publication_protocol_version=6; source_publication_live_route_epoch=2; source_publication_runtime_generation=3; source_publication_terminal_generation=0; source_publication_leg_generation=3; source_publication_generation=3; source_publication_route_commit_generation=0; source_publication_route_commit_generation_present=0; source_publication_target_route=file_tuna_v4; source_publication_target_protocol_version=4; source_publication_target_transport=tuna; source_publication_action_kind=RuntimeUnlockRetry; source_publication_reason=paired_release'
                $lines = @(
                    'event=filetransfer_fallback_tail_paired_yield_granted; session_id=sess; transfer_id=transfer; direction=outbound; route=post_tuna_fallback_v6; protocol_version=6; authority_id=pair; log_writer_id=writer',
                    'event=filetransfer_fallback_paired_tail_yield_runtime_unlock_retry_requested; session_id=sess; transfer_id=transfer; direction=outbound; source_route=post_tuna_fallback_v6; source_protocol_version=6; source_live_route_epoch=2; source_runtime_generation=3; source_terminal_generation=0; source_leg_generation=3; source_route_publication_generation=3; source_route_commit_generation=0; authority_id=pair; recovery_contract_generation=1; retired_offer_generation=0; dispatch_attempt_generation=1; missing_dispatch_successor_generation=0; predecessor_authority_id=(none); request_active_predecessor_supersession=0; decision_status=Queued; log_writer_id=writer',
                    "event=filetransfer_fallback_frontier_repair_authority_granted; $repairScope; $repairShape; authority_id=repair-a; predecessor_authority_id=(none); repair_generation=17; log_writer_id=writer",
                    "event=runtime_unlock_retry_dispatch_authority_held; session_id=sess; transfer_id=transfer; direction=outbound; $holdTuple; decision_reason=fallback_paired_tail_yield_pending; log_writer_id=writer",
                    "event=filetransfer_fallback_frontier_repair_authority_granted; $repairScope; $repairShape; authority_id=repair-b; predecessor_authority_id=repair-a; repair_generation=18; log_writer_id=writer",
                    "event=filetransfer_fallback_frontier_repair_contract_settled; $repairScope; $repairShape; authority_id=repair-a; predecessor_authority_id=(none); repair_generation=17; successor_authority_id=repair-b; settlement_outcome=Superseded; log_writer_id=writer",
                    "event=filetransfer_fallback_frontier_repair_contract_settled; $repairScope; $repairShape; authority_id=repair-b; predecessor_authority_id=repair-a; repair_generation=18; successor_authority_id=(none); settlement_outcome=ProgressObserved; log_writer_id=writer",
                    'event=filetransfer_fallback_paired_tail_yield_runtime_unlock_retry_requested; session_id=sess; transfer_id=transfer; direction=outbound; source_route=post_tuna_fallback_v6; source_protocol_version=6; source_live_route_epoch=2; source_runtime_generation=3; source_terminal_generation=0; source_leg_generation=3; source_route_publication_generation=3; source_route_commit_generation=0; authority_id=pair; recovery_contract_generation=1; retired_offer_generation=0; dispatch_attempt_generation=1; missing_dispatch_successor_generation=0; predecessor_authority_id=(none); request_active_predecessor_supersession=0; decision_status=Queued; log_writer_id=writer'
                )
                if (${{bindingBeforeHeld.ToString().ToLowerInvariant()}}) {
                    $lines = @(
                        $lines[0],
                        $lines[1],
                        $lines[2],
                        "event=runtime_unlock_retry_dispatch_fallback_tail_hold_binding_decided; session_id=sess; transfer_id=transfer; direction=outbound; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; transport_epoch=1; bridge_recovery_generation=1; request_bridge_decision_generation=2; authority_checkpoint_request_id=(none); route_publication_generation=3; route_commit_generation=0; authority_id=repair-a; predecessor_authority_id=(none); repair_generation=17; release_authority_id=pair; release_generation=1; hold_generation=1; data_session_identity=data:1; data_session_generation=1; hold_data_session_identity=data:1; hold_data_session_generation=1; $bindingTuple; decision_status=Accepted; log_writer_id=writer",
                        $lines[3],
                        "event=filetransfer_fallback_frontier_repair_contract_settled; $repairScope; $repairShape; authority_id=repair-a; predecessor_authority_id=(none); repair_generation=17; successor_authority_id=(none); settlement_outcome=ProgressObserved; log_writer_id=writer",
                        $lines[7]
                    )
                }
                elseif (${{holdBound.ToString().ToLowerInvariant()}}) {
                    $lines = @($lines[0..4]) + @(
                        "event=runtime_unlock_retry_dispatch_fallback_tail_hold_binding_decided; session_id=sess; transfer_id=transfer; direction=outbound; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; transport_epoch=1; bridge_recovery_generation=1; request_bridge_decision_generation=2; authority_checkpoint_request_id=(none); route_publication_generation=3; route_commit_generation=0; authority_id=repair-b; predecessor_authority_id=repair-a; repair_generation=18; release_authority_id=pair; release_generation=1; hold_generation=1; data_session_identity=data:1; data_session_generation=1; hold_data_session_identity=data:1; hold_data_session_generation=1; $bindingTuple; decision_status=Accepted; log_writer_id=writer"
                    ) + @($lines[5..($lines.Count - 1)])
                }
                $lines | Set-Content -LiteralPath $path
                $result = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($result.Phase -ne 'route_runtime' -or
                    $result.Reason -ne $(if (${{holdBound.ToString().ToLowerInvariant()}}) { 'settled_fallback_frontier_repair_did_not_republish_held_paired_tail_retry_request' } else { 'unbound_held_fallback_tail_retry_could_not_recheck_after_blocking_repair_settlement' }) -or
                    -not $result.RuntimeUnlockRetryFallbackTailUnblockRecheckMissing -or
                    [bool]$result.RuntimeUnlockRetryFallbackFrontierRepairHoldBindingMissing -ne (-not ${{holdBound.ToString().ToLowerInvariant()}}) -or
                    [bool]$result.RuntimeUnlockRetryFallbackFrontierRepairSettlementRecheckAfterBoundHoldMissing -ne ${{holdBound.ToString().ToLowerInvariant()}} -or
                    $result.ClassificationSource -ne 'immutable_evidence_fallback_tail_repair_unblock_index' -or
                    [long]$result.EvidenceLineVisitCount -ne $(if (${{bindingBeforeHeld.ToString().ToLowerInvariant()}}) { 7 } elseif (${{holdBound.ToString().ToLowerInvariant()}}) { 9 } else { 8 })) {
                    throw "fallback repair hold-binding fast path mismatch: $($result | ConvertTo-Json -Compress)"
                }
                Write-Output 'gui-fallback-repair-hold-binding-fast-path-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(result.ExitCode == 0, $"Fallback repair hold-binding GUI fast path failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-fallback-repair-hold-binding-fast-path-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeFailureClassification_FallbackRepairProgressStateCoalescedUsesIndexedFastPath()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var resultBody = ExtractPowerShellFunctionBody(guiText, "New-TunaGuiFileTransferFailureClassificationResult", "Get-TunaGuiExplicitFailureClassification");
        var explicitBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiExplicitFailureClassification", "Resolve-TunaGuiFailureClassification");
        var classifierBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiFileTransferSetupFailureClassification", "Get-TunaGuiFailureClassificationBudgetMilliseconds");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function New-TunaGuiFileTransferFailureClassificationResult { {{resultBody}} }
            function Get-TunaGuiExplicitFailureClassification { {{explicitBody}} }
            function Get-TunaGuiFileTransferSetupFailureClassification { {{classifierBody}} }
            function Get-AppLogLinesAfterBookmark { throw 'live ledger was read' }
            function Read-GuiSmokeProcessOutputLinesSnapshot { throw 'process output was read' }
            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-repair-progress-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $path = Join-Path $tempRoot 'evidence.log'
                $scope = 'session_id=sess; transfer_id=transfer; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; transport_epoch=1; bridge_recovery_generation=1; checkpoint_request_id=(none); route_publication_generation=3; route_commit_generation=0'
                @(
                    "event=filetransfer_fallback_tail_paired_yield_granted; authority_id=pair; direction=outbound; $scope; log_writer_id=sender",
                    'event=filetransfer_fallback_paired_tail_yield_runtime_unlock_retry_requested; authority_id=pair; session_id=sess; transfer_id=transfer; direction=outbound; source_route=post_tuna_fallback_v6; source_protocol_version=6; decision_status=Queued; log_writer_id=sender',
                    "event=filetransfer_fallback_frontier_repair_authority_granted; authority_id=repair-out; direction=outbound; $scope; repair_generation=7; range_fingerprint=range; frontier_chunk_index=3192; primary_protocol_repair_request_id=req; primary_request_correlation_id=corr; log_writer_id=sender",
                    "event=filetransfer_fallback_frontier_repair_dispatched; authority_id=repair-out; direction=outbound; $scope; repair_generation=7; dispatch_observed=1; log_writer_id=sender",
                    "event=filetransfer_data_path_receive_result; direction=inbound; $scope; delivery_result=received; frame_repair_request_id=req; start_chunk_index=3192; last_end_chunk_exclusive=3193; log_writer_id=helper",
                    "event=filetransfer_fallback_frontier_repair_contract_settled; authority_id=repair-in; direction=inbound; $scope; settlement_outcome=ProgressObserved; primary_protocol_repair_request_id=req; primary_request_correlation_id=corr; range_fingerprint=range; log_writer_id=helper",
                    "event=filetransfer_fallback_frontier_repair_progress_observed; authority_id=repair-in; direction=inbound; $scope; primary_protocol_repair_request_id=req; primary_request_correlation_id=corr; range_fingerprint=range; previous_frontier_chunk_index=3192; current_frontier_chunk_index=3193; log_writer_id=helper",
                    'event=filetransfer_v6_sparse_write_committed; session_id=sess; transfer_id=transfer; next_chunk_index=3193; log_writer_id=helper',
                    'event=filetransfer_v6_receiver_state_coalesced; session_id=sess; transfer_id=transfer; previous_committed_chunk_index=3192; current_committed_chunk_index=3193; last_sent_committed_chunk_index=3192; log_writer_id=helper'
                ) | Set-Content -LiteralPath $path
                $result = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'gui_smoke_scenario_deadline_exceeded' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($result.Phase -ne 'route_runtime' -or
                    $result.Reason -ne 'fallback_frontier_repair_progress_receiver_state_coalesced_without_peer_publication' -or
                    -not $result.RuntimeUnlockRetryFallbackTailUnblockRecheckMissing -or
                    $result.ClassificationSource -ne 'immutable_evidence_fallback_repair_progress_state_index') {
                    throw "fallback repair progress-state fast path mismatch: $($result | ConvertTo-Json -Compress)"
                }
                Write-Output 'gui-fallback-repair-progress-state-fast-path-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(result.ExitCode == 0, $"Fallback repair progress-state GUI fast path failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-fallback-repair-progress-state-fast-path-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("healthy")]
    [InlineData("reverse_order")]
    [InlineData("cross_scope")]
    [InlineData("cross_request")]
    [InlineData("cross_release")]
    [InlineData("conflicting_binding")]
    [InlineData("idempotent_only")]
    [InlineData("hold_data_session_mutation")]
    [InlineData("wrong_predecessor")]
    [InlineData("mutated_initial_request")]
    [InlineData("settlement_generation_mutation")]
    [InlineData("settlement_range_mutation")]
    [InlineData("broken_lineage")]
    [InlineData("redacted_recheck")]
    [InlineData("production_redacted_recheck")]
    [InlineData("wrong_recheck_authority")]
    [InlineData("ambiguous_redacted_recheck")]
    public async Task GuiSmokeFailureClassification_FallbackRepairRecoveryRequiresExactOrderedLineage(
        string scenario)
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var resultBody = ExtractPowerShellFunctionBody(guiText, "New-TunaGuiFileTransferFailureClassificationResult", "Get-TunaGuiExplicitFailureClassification");
        var explicitBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiExplicitFailureClassification", "Resolve-TunaGuiFailureClassification");
        var classifierBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiFileTransferSetupFailureClassification", "Get-TunaGuiFailureClassificationBudgetMilliseconds");
        var publishedContract = scenario == "cross_request" ? "2" : "1";
        var expectMissing = scenario is not (
            "healthy" or
            "redacted_recheck" or
            "production_redacted_recheck" or
            "broken_lineage" or
            "mutated_initial_request" or
            "settlement_generation_mutation" or
            "settlement_range_mutation");
        var expectHoldBindingMissing = scenario is
            "conflicting_binding" or
            "ambiguous_redacted_recheck" or
            "idempotent_only" or
            "hold_data_session_mutation" or
            "wrong_predecessor";
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function New-TunaGuiFileTransferFailureClassificationResult { {{resultBody}} }
            function Get-TunaGuiExplicitFailureClassification { {{explicitBody}} }
            function Get-TunaGuiFileTransferSetupFailureClassification { {{classifierBody}} }
            function Get-AppLogLinesAfterBookmark { throw 'live ledger was read' }
            function Read-GuiSmokeProcessOutputLinesSnapshot { throw 'process output was read' }
            function Test-TunaGuiBridgeBootstrapNotReadyEvidence { return $false }
            function Test-TunaGuiLatestFallbackFrontierRequestDispatchMissing { return $false }
            function Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing { return $false }
            function Test-TunaGuiLogLinesContain { return $false }
            function Get-TunaGuiReadinessStateAfterBookmark {
                return [pscustomobject]@{ ListenerUnavailable = $false; ListenerReady = $false; TunaActive = $false }
            }
            function Resolve-TunaGuiFailureClassification {
                param($ErrorMessage, $HistoricalPhase, $HistoricalReason)
                return [pscustomobject]@{ Phase = $HistoricalPhase; Reason = $HistoricalReason }
            }
            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-fallback-repair-recovery-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $path = Join-Path $tempRoot 'evidence.log'
                $repairScope = 'session_id=sess; transfer_id=transfer; direction=outbound; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; transport_epoch=1; bridge_recovery_generation=1; bridge_decision_generation=2; checkpoint_request_id=(none); route_publication_generation=3; route_commit_generation=0'
                $repairShape = 'repair_request_key=repair-key; range_fingerprint=range; frontier_chunk_index=10; durable_received_highest_chunk_index=9; range_count=1; requested_chunk_count=1; source_kind=PeerRequest; target_transport=regular_nkn'
                $holdTuple = 'contract_generation=1; retired_generation=0; dispatch_attempt_generation=1; missing_dispatch_successor_generation=0; predecessor_authority_id=(none); request_active_predecessor_supersession=0; listener_rearm_requirement=None; listener_execution_mode=Default; retry_reason=fallback_tail; recovery_reason=paired_release; regular_v4_liveness_generation=1; bridge_decision_generation=2; source_session_id=sess; source_transfer_id=transfer; source_direction=outbound; source_route=post_tuna_fallback_v6; source_protocol_version=6; source_live_route_epoch=2; source_runtime_generation=3; source_terminal_generation=0; source_leg_generation=3; source_route_publication_generation=3; source_route_commit_generation=(none); source_route_commit_generation_present=0; source_target_route=file_tuna_v4; source_target_protocol_version=4; source_target_transport=tuna; source_route_publication_action_kind=RuntimeUnlockRetry; source_coordinator_decision_reason=paired_release'
                $bindingTuple = 'contract_generation=1; retired_offer_generation=0; dispatch_attempt_generation=1; missing_dispatch_successor_generation=0; request_predecessor_authority_id=(none); request_active_predecessor_supersession=0; listener_rearm_requirement=None; listener_execution_mode=Default; retry_reason=fallback_tail; recovery_reason=paired_release; regular_v4_liveness_generation=1; request_bridge_decision_generation=2; request_session_id=sess; request_transfer_id=transfer; request_direction=outbound; source_publication_session_id=sess; source_publication_transfer_id=transfer; source_publication_direction=outbound; source_publication_route=post_tuna_fallback_v6; source_publication_protocol_version=6; source_publication_live_route_epoch=2; source_publication_runtime_generation=3; source_publication_terminal_generation=0; source_publication_leg_generation=3; source_publication_generation=3; source_publication_route_commit_generation=0; source_publication_route_commit_generation_present=0; source_publication_target_route=file_tuna_v4; source_publication_target_protocol_version=4; source_publication_target_transport=tuna; source_publication_action_kind=RuntimeUnlockRetry; source_publication_reason=paired_release'
                $publishedTuple = "contract_generation={{publishedContract}}; retired_generation=0; dispatch_attempt_generation=1; missing_dispatch_successor_generation=0; predecessor_authority_id=(none); request_active_predecessor_supersession=0; listener_rearm_requirement=None; listener_execution_mode=Default; retry_reason=fallback_tail; recovery_reason=paired_release; regular_v4_liveness_generation=1; bridge_decision_generation=2; source_session_id=sess; source_transfer_id=transfer; source_direction=outbound; source_route=post_tuna_fallback_v6; source_protocol_version=6; source_live_route_epoch=2; source_runtime_generation=3; source_terminal_generation=0; source_leg_generation=3; source_route_publication_generation=3; source_route_commit_generation=0; source_route_commit_generation_present=0; source_target_route=file_tuna_v4; source_target_protocol_version=4; source_target_transport=tuna; source_route_publication_action_kind=RuntimeUnlockRetry; source_coordinator_decision_reason=paired_release"
                $installTuple = 'authority_schema=3; authority_id=retry; authority_generation=1; predecessor_authority_id=(none); session_id=sess; transfer_id=transfer; direction=outbound; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; transport_epoch=1; bridge_recovery_generation=1; bridge_decision_generation=2; route_publication_generation=3; route_commit_generation=0; contract_generation=1; retired_generation=0; dispatch_attempt_generation=1; missing_dispatch_successor_generation=0; listener_rearm_requirement=None; listener_execution_mode=Default; retry_reason=fallback_tail; recovery_reason=paired_release; release_kind=FallbackPairedTailYield; release_authority_id=pair; release_generation=1; release_outcome=paired; data_session_generation=1; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; source_publication_session_id=sess; source_publication_transfer_id=transfer; source_publication_direction=outbound; source_publication_route=post_tuna_fallback_v6; source_publication_protocol_version=6; source_publication_live_route_epoch=2; source_publication_runtime_generation=3; source_publication_terminal_generation=0; source_publication_leg_generation=3; source_publication_route_commit_generation=0; source_publication_target_route=file_tuna_v4; source_publication_target_protocol_version=4; source_publication_target_transport=tuna; source_publication_generation=3; source_publication_action_kind=RuntimeUnlockRetry; source_publication_reason=paired_release; request_regular_v4_liveness_generation=1; request_bridge_decision_generation=2; request_active_predecessor_supersession=0'
                $lines = @(
                    'event=filetransfer_fallback_tail_paired_yield_granted; session_id=sess; transfer_id=transfer; direction=outbound; route=post_tuna_fallback_v6; protocol_version=6; authority_id=pair; log_writer_id=writer',
                    'event=filetransfer_fallback_paired_tail_yield_runtime_unlock_retry_requested; session_id=sess; transfer_id=transfer; direction=outbound; source_route=post_tuna_fallback_v6; source_protocol_version=6; source_live_route_epoch=2; source_runtime_generation=3; source_terminal_generation=0; source_leg_generation=3; source_route_publication_generation=3; source_route_commit_generation=0; authority_id=pair; recovery_contract_generation=1; retired_offer_generation=0; dispatch_attempt_generation=1; missing_dispatch_successor_generation=0; predecessor_authority_id=(none); request_active_predecessor_supersession=0; decision_status=Queued; log_writer_id=writer',
                    "event=filetransfer_fallback_frontier_repair_authority_granted; $repairScope; $repairShape; authority_id=repair-a; predecessor_authority_id=(none); repair_generation=17; log_writer_id=writer",
                    "event=runtime_unlock_retry_dispatch_authority_held; session_id=sess; transfer_id=transfer; direction=outbound; $holdTuple; decision_reason=fallback_paired_tail_yield_pending; log_writer_id=writer",
                    "event=filetransfer_fallback_frontier_repair_authority_granted; $repairScope; $repairShape; authority_id=repair-b; predecessor_authority_id=repair-a; repair_generation=18; log_writer_id=writer",
                    "event=runtime_unlock_retry_dispatch_fallback_tail_hold_binding_decided; session_id=sess; transfer_id=transfer; direction=outbound; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; transport_epoch=1; bridge_recovery_generation=1; request_bridge_decision_generation=2; authority_checkpoint_request_id=(none); route_publication_generation=3; route_commit_generation=0; authority_id=repair-b; predecessor_authority_id=repair-a; repair_generation=18; release_authority_id=pair; release_generation=1; hold_generation=1; data_session_identity=data:1; data_session_generation=1; hold_data_session_identity=data:1; hold_data_session_generation=1; $bindingTuple; decision_status=Accepted; log_writer_id=writer",
                    "event=filetransfer_fallback_frontier_repair_contract_settled; $repairScope; $repairShape; authority_id=repair-a; predecessor_authority_id=(none); repair_generation=17; successor_authority_id=repair-b; settlement_outcome=Superseded; log_writer_id=writer",
                    "event=filetransfer_fallback_frontier_repair_contract_settled; $repairScope; $repairShape; authority_id=repair-b; predecessor_authority_id=repair-a; repair_generation=18; successor_authority_id=(none); settlement_outcome=ProgressObserved; log_writer_id=writer",
                    "event=runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_requested; session_id=sess; transfer_id=transfer; direction=outbound; $holdTuple; recovered_transport_epoch=1; handoff_kind=regular_nkn_recovery_to_regular_nkn; boundary_source_transport=regular_nkn_recovery; boundary_target_transport=regular_nkn; fallback_epoch=1; boundary_reason=repair_settled; boundary_identity=boundary; original_hold_reason=fallback_paired_tail_yield_pending; recheck_generation=1; queue_generation=7; decision_status=Queued; log_writer_id=writer",
                    'event=runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_decided; session_id=sess; transfer_id=transfer; direction=outbound; authority_id=pair; release_generation=1; repair_authority_id=repair-b; repair_generation=18; hold_generation=1; recheck_generation=1; decision_status=Accepted; log_writer_id=writer',
                    "event=runtime_unlock_retry_dispatch_authority_request_published; session_id=sess; transfer_id=transfer; direction=outbound; $publishedTuple; queue_generation=7; log_writer_id=writer",
                    'event=filetransfer_fallback_tail_paired_release_reserved; session_id=sess; transfer_id=transfer; direction=outbound; authority_id=pair; pairing_generation=1; retry_authority_id=retry; decision_status=Accepted; log_writer_id=writer',
                    "event=runtime_unlock_retry_dispatch_decision_staged; $installTuple; stage_status=Staged; log_writer_id=writer",
                    "event=runtime_unlock_retry_dispatch_install_receipt_issued; $installTuple; install_generation=1; log_writer_id=writer",
                    "event=runtime_unlock_retry_dispatch_install_committed; $installTuple; install_generation=1; install_status=Installed; log_writer_id=writer",
                    "event=runtime_unlock_retry_dispatch_authority_granted; $installTuple; install_generation=1; log_writer_id=writer",
                    'event=filetransfer_fallback_tail_paired_release_consumed; session_id=sess; transfer_id=transfer; direction=outbound; authority_id=pair; pairing_generation=1; retry_authority_id=retry; install_generation=1; decision_status=Accepted; log_writer_id=writer',
                    "event=runtime_unlock_retry_dispatch_observed; $installTuple; install_generation=1; offer_generation=1; offer_nonce=nonce; log_writer_id=writer",
                    'event=runtime_unlock_offer_authority_boundary_decided; boundary=answer_received; authority_mode=InstalledRetry; decision_status=accepted; session_id=sess; transfer_id=transfer; payer_decision_id=7; offer_generation=1; transaction_generation=1; tuna_path_lease_generation=1; tuna_path_lease_listener_run_id=listener; retry_authority_id=retry; install_generation=1; log_writer_id=writer',
                    'event=runtime_unlock_transaction_answer_received; session_id=sess; transaction_generation=1; offer_generation=1; tuna_path_lease_generation=1; tuna_path_lease_listener_run_id=listener; answer_received=1; tuna_path_lease_current=1; log_writer_id=writer',
                    'event=tuna_acceleration_answer_ack_sent; session_id=sess; payer_decision_id=7; generation=9; msg_id=ack; target_peer_fingerprint=peer; log_writer_id=writer',
                    'event=runtime_unlock_precommit_probe_launch_requested; session_id=sess; transfer_id=transfer; direction=outbound; transaction_generation=1; offer_generation=1; tuna_path_lease_generation=1; tuna_path_lease_listener_run_id=listener; log_writer_id=writer',
                    'event=filetransfer_fallback_paired_tail_yield_runtime_unlock_retry_requested; session_id=sess; transfer_id=transfer; direction=outbound; source_route=post_tuna_fallback_v6; source_protocol_version=6; source_live_route_epoch=2; source_runtime_generation=3; source_terminal_generation=0; source_leg_generation=3; source_route_publication_generation=3; source_route_commit_generation=0; authority_id=pair; recovery_contract_generation=1; retired_offer_generation=0; dispatch_attempt_generation=1; missing_dispatch_successor_generation=0; predecessor_authority_id=(none); request_active_predecessor_supersession=0; decision_status=Queued; log_writer_id=writer',
                    'event=runtime_unlock_transaction_answer_received; session_id=sentinel; transaction_generation=1; offer_generation=6; answer_received=1; tuna_path_lease_generation=1; tuna_path_lease_current=1; tuna_path_lease_listener_run_id=sentinel-listener; log_writer_id=sentinel-writer',
                    'event=tuna_acceleration_answer_ack_pending; session_id=sentinel; payer_decision_id=4; generation=6; log_writer_id=sentinel-writer',
                    'event=runtime_unlock_transaction_path_probe_acked; session_id=sentinel; transaction_generation=1; offer_generation=6; path_probe_id=sentinel-probe; path_probe_state=acked; path_probe_transport=tuna; tuna_path_lease_generation=1; log_writer_id=sentinel-writer',
                    'event=runtime_unlock_precommit_probe_acked; session_id=sentinel; transfer_id=sentinel-transfer; direction=inbound; transaction_generation=1; offer_generation=1; launch_generation=0; probe_id=sentinel-probe; probe_state=acked; received_transport=tuna; tuna_path_lease_generation=1; log_writer_id=sentinel-writer',
                    'event=tuna_acceleration_answer_ack_received; session_id=sentinel; payer_decision_id=4; log_writer_id=sentinel-writer',
                    'event=runtime_unlock_answer_ack_probe_barrier_received; session_id=sentinel; payer_decision_id=4; log_writer_id=sentinel-writer',
                    'event=filetransfer_runtime_unlock_handoff_pending_route_commit; session_id=sentinel; transaction_generation=1; offer_generation=6; state=routecommitpending; path_probe_state=acked; route_commit_pending=1; route_committed=0; log_writer_id=sentinel-writer'
                )
                $scenario = '{{scenario}}'
                switch ($scenario) {
                    'reverse_order' {
                        $swap = $lines[8]
                        $lines[8] = $lines[9]
                        $lines[9] = $swap
                    }
                    'cross_scope' {
                        $lines[8] = $lines[8].Replace('source_runtime_generation=3', 'source_runtime_generation=4')
                    }
                    'cross_release' {
                        $lines[11] = $lines[11].Replace('authority_id=pair', 'authority_id=other-pair')
                    }
                    'conflicting_binding' {
                        $conflict = $lines[5].Replace('release_generation=1', 'release_generation=2')
                        $lines = @($lines[0..5]) + @($conflict) + @($lines[6..($lines.Count - 1)])
                    }
                    'idempotent_only' {
                        $lines[5] = $lines[5].Replace('decision_status=Accepted', 'decision_status=Idempotent')
                    }
                    'hold_data_session_mutation' {
                        $lines[5] = $lines[5].Replace('hold_data_session_generation=1', 'hold_data_session_generation=2')
                    }
                    'wrong_predecessor' {
                        $lines[5] = $lines[5].Replace('predecessor_authority_id=repair-a', 'predecessor_authority_id=other-repair')
                    }
                    'mutated_initial_request' {
                        $lines[1] = $lines[1].Replace('source_runtime_generation=3', 'source_runtime_generation=4')
                    }
                    'settlement_generation_mutation' {
                        $lines[7] = $lines[7].Replace('repair_generation=18', 'repair_generation=19')
                    }
                    'settlement_range_mutation' {
                        $lines[7] = $lines[7].Replace('range_fingerprint=range', 'range_fingerprint=other-range')
                    }
                    'broken_lineage' {
                        $lines[6] = $lines[6].Replace('successor_authority_id=repair-b', 'successor_authority_id=other-repair')
                    }
                    'redacted_recheck' {
                        $lines[9] = $lines[9].Replace('repair_authority_id=repair-b', 'repair_authority_id=[redacted]')
                    }
                    'production_redacted_recheck' {
                        $lines[8] = $lines[8] `
                            -replace 'recovered_transport_epoch=1; ', '' `
                            -replace 'handoff_kind=regular_nkn_recovery_to_regular_nkn; ', '' `
                            -replace 'boundary_source_transport=regular_nkn_recovery; ', '' `
                            -replace 'boundary_target_transport=regular_nkn; ', '' `
                            -replace 'fallback_epoch=1; ', '' `
                            -replace 'boundary_reason=repair_settled; ', '' `
                            -replace 'boundary_identity=boundary; ', '' `
                            -replace 'original_hold_reason=fallback_paired_tail_yield_pending; ', ''
                        $lines[9] = $lines[9].Replace('repair_authority_id=repair-b', 'repair_authority_id=[redacted]')
                        foreach ($index in 12..15) {
                            $lines[$index] = $lines[$index].Replace('data_session_generation=1', 'data_session_generation=0')
                        }
                        $consumed = $lines[16]
                        $lines[16] = $lines[15]
                        $lines[15] = $consumed
                    }
                    'wrong_recheck_authority' {
                        $lines[9] = $lines[9].Replace('repair_authority_id=repair-b', 'repair_authority_id=other-repair')
                    }
                    'ambiguous_redacted_recheck' {
                        $lines[9] = $lines[9].Replace('repair_authority_id=repair-b', 'repair_authority_id=[redacted]')
                        $conflict = $lines[5].Replace('data_session_identity=data:1', 'data_session_identity=data:2').Replace('hold_data_session_identity=data:1', 'hold_data_session_identity=data:2')
                        $lines = @($lines[0..5]) + @($conflict) + @($lines[6..($lines.Count - 1)])
                    }
                }
                if ($scenario -in @('redacted_recheck', 'production_redacted_recheck')) {
                    $paddingCount = 56563 - $lines.Count
                    $padding = @(for ($index = 0; $index -lt $paddingCount; $index++) {
                        "diagnostic=fallback_recheck_unrelated; sample_index=$index"
                    })
                    $lines = @($padding) + @($lines)
                }
                $lines | Set-Content -LiteralPath $path
                $result = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ([bool]$result.RuntimeUnlockRetryFallbackTailUnblockRecheckMissing -ne ${{expectMissing.ToString().ToLowerInvariant()}}) {
                    throw "fallback repair scenario '$scenario' mismatch: $($result | ConvertTo-Json -Compress)"
                }
                if (${{expectMissing.ToString().ToLowerInvariant()}} -and
                    ([bool]$result.RuntimeUnlockRetryFallbackFrontierRepairHoldBindingMissing -ne ${{expectHoldBindingMissing.ToString().ToLowerInvariant()}} -or
                        [bool]$result.RuntimeUnlockRetryFallbackFrontierRepairSettlementRecheckAfterBoundHoldMissing -eq ${{expectHoldBindingMissing.ToString().ToLowerInvariant()}})) {
                    throw "fallback repair scenario '$scenario' selected the wrong subtype: $($result | ConvertTo-Json -Compress)"
                }
                Write-Output 'gui-fallback-repair-recovery-order-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(result.ExitCode == 0, $"Fallback repair recovery GUI fast path failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-fallback-repair-recovery-order-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeFailureClassification_FailedFallbackTailYieldUsesIndexedFastPath()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var resultBody = ExtractPowerShellFunctionBody(guiText, "New-TunaGuiFileTransferFailureClassificationResult", "Get-TunaGuiExplicitFailureClassification");
        var explicitBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiExplicitFailureClassification", "Resolve-TunaGuiFailureClassification");
        var classifierBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiFileTransferSetupFailureClassification", "Get-TunaGuiFailureClassificationBudgetMilliseconds");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function New-TunaGuiFileTransferFailureClassificationResult { {{resultBody}} }
            function Get-TunaGuiExplicitFailureClassification { {{explicitBody}} }
            function Get-TunaGuiFileTransferSetupFailureClassification { {{classifierBody}} }
            function Get-AppLogLinesAfterBookmark { throw 'live ledger was read' }
            function Read-GuiSmokeProcessOutputLinesSnapshot { throw 'process output was read' }
            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-failed-tail-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $path = Join-Path $tempRoot 'evidence.log'
                @(
                    'event=filetransfer_fallback_tail_yield_issued; session_id=sess; transfer_id=transfer; direction=outbound; route=post_tuna_fallback_v6; authority_id=failed; yield_generation=1; owner_key=owner; pair_key=pair; route_publication_generation=3; route_commit_generation=0; runtime_generation=3; terminal_generation=0; leg_generation=3; transport_epoch=1; bridge_recovery_generation=1; checkpoint_generation=1; checkpoint_request_id=(none); data_session_identity=data:1; data_session_generation=1; yield_kind=Failed; log_writer_id=writer',
                    'event=filetransfer_fallback_tail_quiescence_timeout; session_id=sess; transfer_id=transfer; direction=outbound; route=post_tuna_fallback_v6; log_writer_id=writer',
                    'event=filetransfer_v6_handoff_tail_unblocked; session_id=sess; transfer_id=transfer; direction=outbound; transport_epoch=1; log_writer_id=writer',
                    'event=filetransfer_runtime_owner_stale_update_rejected; session_id=sess; transfer_id=transfer; direction=outbound; route=post_tuna_fallback_v6; attempted_action=fallback_tail_quiescent; reason=fallback_tail_yield_operation_conflict; log_writer_id=writer'
                ) | Set-Content -LiteralPath $path
                $result = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($result.Phase -ne 'route_runtime' -or
                    $result.Reason -ne 'same_scope_failed_fallback_tail_yield_blocked_quiescent_successor' -or
                    -not $result.FallbackTailFailedYieldQuiescentSuccessorMissing -or
                    $result.ClassificationSource -ne 'immutable_evidence_fallback_tail_failed_yield_index' -or
                    [long]$result.EvidenceLineVisitCount -ne 4) {
                    throw "failed fallback-tail fast path mismatch: $($result | ConvertTo-Json -Compress)"
                }
                Write-Output 'gui-failed-fallback-tail-fast-path-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(result.ExitCode == 0, $"Failed fallback-tail GUI fast path failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-failed-fallback-tail-fast-path-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuiSmokeFailureClassification_FailedFallbackTailYieldAcceptsCarriedPeerProof(
        bool redactNestedAuthorities)
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var nestedFunctionText = guiText
            .Replace("\r\n    ", "\r\n", StringComparison.Ordinal)
            .Replace("\n    ", "\n", StringComparison.Ordinal);
        var fieldBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Get-TunaGuiLogLineFieldValue",
            "Get-TunaGuiRetiredPendingAnswerHandoffFailure");
        var fastPathBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Get-TunaGuiFallbackTailFailedYieldQuiescentSuccessorFailure",
            "Get-TunaGuiFallbackTailPeerProofBeforeLocalYieldFailure");
        var predecessorReference = redactNestedAuthorities ? "[redacted]" : "failed-authority";
        var successorReference = redactNestedAuthorities ? "[redacted]" : "successor-authority";
        var peerAuthorityReference = redactNestedAuthorities ? "[redacted]" : "peer-authority";
        var scope = "session_id=sess; transfer_id=transfer; direction=outbound; route=post_tuna_fallback_v6; protocol_version=6; owner_key=owner; pair_key=pair; route_publication_generation=3; route_commit_generation=0; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; transport_epoch=1; bridge_recovery_generation=1; checkpoint_generation=1; checkpoint_request_id=(none); data_session_identity=data:1; data_session_generation=1; log_writer_id=writer";
        var peerTuple = $"peer_observation_identity=peer-proof; peer_frame_fingerprint=peer-frame; peer_authority_id={peerAuthorityReference}; peer_predecessor_authority_id=(none); peer_yield_generation=1; peer_direction=inbound; peer_owner_key=peer-owner; peer_pair_key=peer-pair; peer_live_route_epoch=2; peer_leg_generation=3; peer_transport_epoch=1; peer_bridge_recovery_generation=1; peer_checkpoint_request_id=(none)";
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Get-TunaGuiLogLineFieldValue { {{fieldBody}} }
            function Get-TunaGuiFallbackTailFailedYieldQuiescentSuccessorFailure { {{fastPathBody}} }
            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-carried-tail-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $path = Join-Path $tempRoot 'evidence.log'
                @(
                    'event=filetransfer_fallback_tail_yield_issued; {{scope}}; authority_id=failed-authority; yield_generation=1; yield_kind=Failed; decision_status=Accepted',
                    'event=filetransfer_fallback_tail_quiescence_timeout; {{scope}}',
                    'event=filetransfer_v6_handoff_tail_unblocked; {{scope}}',
                    'event=filetransfer_runtime_owner_stale_update_rejected; {{scope}}; attempted_action=fallback_tail_quiescent; reason=fallback_tail_yield_operation_conflict',
                    'event=filetransfer_fallback_tail_peer_proof_receipt_decided; {{scope}}; authority_id=failed-authority; yield_generation=1; {{peerTuple.Replace(peerAuthorityReference, "peer-authority", StringComparison.Ordinal)}}; decision_status=Accepted',
                    'event=filetransfer_fallback_tail_yield_issued; {{scope}}; authority_id=successor-authority; yield_generation=2; yield_kind=Quiescent; failed_predecessor_authority_id={{predecessorReference}}; failed_predecessor_yield_generation=1; failed_successor_authority_id={{successorReference}}; failed_successor_yield_generation=2; failed_supersession_carried_peer_proof=1; decision_status=Accepted',
                    'event=filetransfer_fallback_tail_failed_yield_superseded; {{scope}}; authority_id=successor-authority; yield_generation=2; failed_predecessor_authority_id={{predecessorReference}}; failed_predecessor_yield_generation=1; failed_successor_authority_id={{successorReference}}; failed_successor_yield_generation=2; failed_supersession_carried_peer_proof=1; decision_status=Accepted',
                    'event=filetransfer_fallback_tail_local_proof_receipt_decided; {{scope}}; authority_id=successor-authority; yield_generation=2; local_proof_identity=local-proof; local_frame_fingerprint=local-frame; decision_status=Accepted',
                    'event=filetransfer_fallback_tail_pairing_committed; {{scope}}; authority_id=successor-authority; yield_generation=2; {{peerTuple}}; decision_status=Accepted',
                    'event=filetransfer_fallback_tail_paired_release_reserved; {{scope}}; authority_id=successor-authority; yield_generation=2; decision_status=Accepted',
                    'event=filetransfer_fallback_tail_paired_release_consumed; {{scope}}; authority_id=successor-authority; yield_generation=2; decision_status=Accepted',
                    'event=filetransfer_route_transition_prepared; session_id=sess; transfer_id=transfer; direction=outbound; source_route=post_tuna_fallback_v6; target_route=file_tuna_v4; fallback_yield_authority_id=successor-authority; log_writer_id=writer'
                ) | Set-Content -LiteralPath $path
                [string[]]$lines = @(Get-Content -LiteralPath $path)
                $result = Get-TunaGuiFallbackTailFailedYieldQuiescentSuccessorFailure -Lines $lines
                if ([bool]$result.Unresolved -or [long]$result.LineVisits -ne 12) {
                    throw "carried peer proof was rejected: $($result | ConvertTo-Json -Compress)"
                }
                Write-Output 'gui-carried-fallback-tail-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(result.ExitCode == 0, $"Carried fallback-tail GUI fast path failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-carried-fallback-tail-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuiSmokeFailureClassification_PeerFirstFallbackTailProofUsesIndexedFastPath(
        bool asymmetricRecoveryScope)
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var resultBody = ExtractPowerShellFunctionBody(guiText, "New-TunaGuiFileTransferFailureClassificationResult", "Get-TunaGuiExplicitFailureClassification");
        var explicitBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiExplicitFailureClassification", "Resolve-TunaGuiFailureClassification");
        var classifierBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiFileTransferSetupFailureClassification", "Get-TunaGuiFailureClassificationBudgetMilliseconds");
        var decisionReason = asymmetricRecoveryScope
            ? "fallback_tail_peer_proof_scope_mismatch"
            : "fallback_tail_yield_operation_missing";
        var expectedReason = asymmetricRecoveryScope
            ? "direction_local_recovery_generation_treated_as_shared_pair_scope"
            : "reciprocal_fallback_tail_peer_proof_not_rebound_after_local_quiescent_yield";
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function New-TunaGuiFileTransferFailureClassificationResult { {{resultBody}} }
            function Get-TunaGuiExplicitFailureClassification { {{explicitBody}} }
            function Get-TunaGuiFileTransferSetupFailureClassification { {{classifierBody}} }
            function Get-AppLogLinesAfterBookmark { throw 'live ledger was read' }
            function Read-GuiSmokeProcessOutputLinesSnapshot { throw 'process output was read' }
            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-peer-first-tail-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $path = Join-Path $tempRoot 'evidence.log'
                @(
                    'event=filetransfer_fallback_tail_peer_proof_receipt_decided; session_id=sess; transfer_id=transfer; direction=inbound; peer_pair_key=pair; peer_observation_identity=peer-proof; peer_frame_fingerprint=frame; decision_status=Rejected; decision_reason={{decisionReason}}; log_writer_id=writer-in',
                    'event=filetransfer_fallback_tail_yield_issued; session_id=sess; transfer_id=transfer; direction=inbound; route=post_tuna_fallback_v6; authority_id=inbound-yield; yield_generation=1; pair_key=pair; yield_kind=Quiescent; log_writer_id=writer-in',
                    'event=filetransfer_fallback_tail_local_proof_receipt_decided; session_id=sess; transfer_id=transfer; direction=inbound; authority_id=inbound-yield; yield_generation=1; local_proof_identity=inbound-local-proof; decision_status=Accepted; log_writer_id=writer-in',
                    'event=filetransfer_fallback_tail_pairing_committed; session_id=sess; transfer_id=transfer; direction=outbound; authority_id=outbound-yield; peer_direction=inbound; peer_observation_identity=inbound-local-proof; log_writer_id=writer-out',
                    'event=filetransfer_fallback_tail_paired_release_consumed; session_id=sess; transfer_id=transfer; direction=outbound; authority_id=outbound-yield; log_writer_id=writer-out',
                    'event=filetransfer_runtime_unlock_admission_denied; session_id=sess; transfer_id=transfer; direction=inbound; route=post_tuna_fallback_v6; admission_reason=paired_fallback_tail_yield_missing; log_writer_id=writer-in'
                ) | Set-Content -LiteralPath $path
                $result = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'scenario failed' -RouteMode 'live-regular-activation-cycle' -ImmutableEvidencePaths @($path)
                if ($result.Phase -ne 'route_runtime' -or
                    $result.Reason -ne '{{expectedReason}}' -or
                    -not $result.FallbackTailPeerProofBeforeLocalYieldNotRetained -or
                    [bool]$result.FallbackTailPeerProofBeforeLocalYieldAsymmetricRecoveryScopeNotRetained -ne ${{asymmetricRecoveryScope.ToString().ToLowerInvariant()}} -or
                    $result.ClassificationSource -ne 'immutable_evidence_fallback_tail_peer_first_index' -or
                    [long]$result.EvidenceLineVisitCount -ne 6) {
                    throw "peer-first fallback-tail fast path mismatch: $($result | ConvertTo-Json -Compress)"
                }
                Write-Output 'gui-peer-first-fallback-tail-fast-path-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(result.ExitCode == 0, $"Peer-first fallback-tail GUI fast path failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-peer-first-fallback-tail-fast-path-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeFailureClassification_InvalidFallbackBootstrapFrontierUsesIndexedFastPath()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var nestedFunctionText = guiText
            .Replace("\r\n    ", "\r\n", StringComparison.Ordinal)
            .Replace("\n    ", "\n", StringComparison.Ordinal);
        var fieldBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Get-TunaGuiLogLineFieldValue",
            "Get-TunaGuiRetiredPendingAnswerHandoffFailure");
        var fastPathMatch = System.Text.RegularExpressions.Regex.Match(
            nestedFunctionText,
            @"(?s)function\s+Get-TunaGuiFallbackV6ProofBootstrapInvalidFrontierFailure\s*\{(?<body>.*?)\r?\n\}\s*\r?\n\s*function\s+Get-TunaGuiFallbackTailFailedYieldQuiescentSuccessorFailure");
        Assert.True(
            fastPathMatch.Success,
            "Could not extract the fallback-V6 bootstrap GUI fast path.");
        var fastPathBody = fastPathMatch.Groups["body"].Value;
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Get-TunaGuiLogLineFieldValue { {{fieldBody}} }
            function Get-TunaGuiFallbackV6ProofBootstrapInvalidFrontierFailure { {{fastPathBody}} }

            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-invalid-fallback-bootstrap-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $path = Join-Path $tempRoot 'evidence.log'
                @(
                    'event=filetransfer_route_transition_committed; authority_id=transition; transition_generation=1; session_id=sess; transfer_id=transfer; direction=outbound; target_route=file_tuna_v4; target_live_route_epoch=1; target_runtime_generation=2; target_route_publication_generation=2; target_route_commit_generation=1; target_terminal_generation=0; target_leg_generation=2; decision_status=Committed; log_writer_id=writer',
                    'event=filetransfer_fallback_v6_proof_bootstrap_observed; session_id=sess; direction=outbound; observation_generation=1; fallback_epoch=1; proof_kind=file_transfer_v4_state_frame_received; log_writer_id=writer',
                    'event=filetransfer_fallback_v6_proof_bootstrap_decided; session_id=sess; transfer_id=transfer; direction=outbound; fallback_epoch=1; authority_id=bootstrap; bootstrap_generation=1; source_route=file_tuna_v4; source_protocol_version=4; source_live_route_epoch=1; source_runtime_generation=2; source_route_publication_generation=2; source_route_commit_generation=1; source_terminal_generation=0; source_leg_generation=2; source_transport_epoch=0; source_bridge_recovery_generation=0; source_data_session_generation=1; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn; target_transport_epoch=1; frame_kind=FrontierRequest; decision_status=Issued; log_writer_id=writer',
                    'event=filetransfer_fallback_v6_proof_bootstrap_execution_completed; session_id=sess; transfer_id=transfer; direction=outbound; fallback_epoch=1; authority_id=bootstrap; bootstrap_generation=1; target_transport_epoch=1; frame_kind=FrontierRequest; frame_fingerprint=none; receipt_status=Rejected; reason_code=execution_invalid; log_writer_id=writer',
                    'event=filetransfer_fallback_v6_proof_bootstrap_receipt_applied; session_id=sess; transfer_id=transfer; direction=outbound; fallback_epoch=1; authority_id=bootstrap; bootstrap_generation=1; target_transport_epoch=1; frame_kind=FrontierRequest; frame_fingerprint=(none); receipt_status=Rejected; application_status=Applied; reason_code=execution_invalid; log_writer_id=writer'
                ) | Set-Content -LiteralPath $path
                $result = Get-TunaGuiFallbackV6ProofBootstrapInvalidFrontierFailure -Lines @(Get-Content -LiteralPath $path)
                if (-not $result.Unresolved -or [long]$result.LineVisits -ne 5) {
                    throw "invalid fallback bootstrap fast path mismatch: $($result | ConvertTo-Json -Compress)"
                }
                Write-Output 'gui-invalid-fallback-bootstrap-fast-path-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(
            result.ExitCode == 0,
            $"Invalid fallback-bootstrap GUI fast path failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-invalid-fallback-bootstrap-fast-path-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeFailureClassification_BudgetLeavesHardDeadlineGrace()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var budgetBody = ExtractPowerShellFunctionBody(
            guiText,
            "Get-TunaGuiFailureClassificationBudgetMilliseconds",
            "ConvertTo-GuiSmokeProcessArgument");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Get-TunaGuiFailureClassificationBudgetMilliseconds { {{budgetBody}} }

            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-enrichment-budget-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
                $statePath = Join-Path $tempRoot 'state.json'
                $env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH = $statePath
                $env:NLINK_TUNA_GUI_FAILURE_CLASSIFICATION_TIMEOUT_SECONDS_TEST_ONLY = '30'
                [ordered]@{
                    deadlineUtc = [DateTimeOffset]::UtcNow.AddSeconds(9).ToString('O')
                } | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8
                $expiredBudget = Get-TunaGuiFailureClassificationBudgetMilliseconds -ArtifactDir $tempRoot
                if ([long]$expiredBudget -ne 0L) {
                    throw "enrichment crossed the reserved hard-deadline grace: $expiredBudget"
                }

                [ordered]@{
                    deadlineUtc = [DateTimeOffset]::UtcNow.AddSeconds(25).ToString('O')
                } | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8
                $boundedBudget = Get-TunaGuiFailureClassificationBudgetMilliseconds -ArtifactDir $tempRoot
                if ([long]$boundedBudget -le 0L -or [long]$boundedBudget -gt 15000L) {
                    throw "enrichment budget did not preserve ten-second grace: $boundedBudget"
                }
                Write-Output 'gui-enrichment-budget-ok'
            }
            finally {
                Remove-Item Env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH -ErrorAction SilentlyContinue
                Remove-Item Env:NLINK_TUNA_GUI_FAILURE_CLASSIFICATION_TIMEOUT_SECONDS_TEST_ONLY -ErrorAction SilentlyContinue
                if (Test-Path -LiteralPath $tempRoot) {
                    Remove-Item -LiteralPath $tempRoot -Recurse -Force
                }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(
            result.ExitCode == 0,
            $"Failure-classification budget test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-enrichment-budget-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeFailureClassification_EnrichmentOutcomesPreserveBaseUntilCompletedResponse()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var resultBody = ExtractPowerShellFunctionBody(
            guiText,
            "New-TunaGuiFileTransferFailureClassificationResult",
            "Get-TunaGuiExplicitFailureClassification");
        var budgetBody = ExtractPowerShellFunctionBody(
            guiText,
            "Get-TunaGuiFailureClassificationBudgetMilliseconds",
            "ConvertTo-GuiSmokeProcessArgument");
        var argumentBody = ExtractPowerShellFunctionBody(
            guiText,
            "ConvertTo-GuiSmokeProcessArgument",
            "New-TunaGuiFailureClassificationEnrichmentReceipt");
        var receiptBody = ExtractPowerShellFunctionBody(
            guiText,
            "New-TunaGuiFailureClassificationEnrichmentReceipt",
            "Test-TunaGuiCompletedFailureClassificationResult");
        var completedBody = ExtractPowerShellFunctionBody(
            guiText,
            "Test-TunaGuiCompletedFailureClassificationResult",
            "Invoke-TunaGuiFailureClassificationRequest");
        var enrichmentBody = ExtractPowerShellFunctionBody(
            guiText,
            "Invoke-TunaGuiFailureClassificationEnrichment",
            "Wait-TunaGuiFileTransferAcceptOrThrow");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function New-TunaGuiFileTransferFailureClassificationResult { {{resultBody}} }
            function Get-TunaGuiFailureClassificationBudgetMilliseconds { {{budgetBody}} }
            function ConvertTo-GuiSmokeProcessArgument { {{argumentBody}} }
            function New-TunaGuiFailureClassificationEnrichmentReceipt { {{receiptBody}} }
            function Test-TunaGuiCompletedFailureClassificationResult { {{completedBody}} }
            function Invoke-TunaGuiFailureClassificationEnrichment { {{enrichmentBody}} }
            function Write-GuiSmokeAtomicJson {
                param($Path, $Value)
                $Value | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $Path -Encoding UTF8
            }
            function New-FakeClassificationProcess {
                param([bool]$WaitResult, [int]$ExitCode)
                $process = [pscustomobject]@{
                    Id = 4242
                    ExitCode = $ExitCode
                    WaitResult = $WaitResult
                }
                $process | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value {
                    param($Milliseconds)
                    return [bool]$this.WaitResult
                }
                $process | Add-Member -MemberType ScriptMethod -Name Refresh -Value { }
                return $process
            }
            function Start-Process {
                param(
                    $FilePath,
                    $ArgumentList,
                    $WorkingDirectory,
                    $RedirectStandardOutput,
                    $RedirectStandardError,
                    $WindowStyle,
                    [switch]$PassThru)
                switch ($script:fakeProcessMode) {
                    'unexpected' { throw 'enrichment process was started with zero budget' }
                    'timeout' { return New-FakeClassificationProcess -WaitResult $false -ExitCode 0 }
                    'process-failed' { return New-FakeClassificationProcess -WaitResult $true -ExitCode 9 }
                    'missing-response' { return New-FakeClassificationProcess -WaitResult $true -ExitCode 0 }
                    'completed' {
                        $candidate = New-TunaGuiFileTransferFailureClassificationResult `
                            -Phase 'route_runtime' `
                            -Reason 'exact_proof_chain_failure' `
                            -RouteMode 'live-regular-activation-cycle' `
                            -ClassificationSource 'immutable_evidence_exact_proof'
                        [ordered]@{
                            schemaVersion = 1
                            event = 'filetransfer_tuna_gui_failure_classification_response'
                            classification = $candidate
                        } | ConvertTo-Json -Depth 32 | Set-Content `
                            -LiteralPath (Join-Path $script:artifactRoot 'filetransfer-tuna-gui-failure-classification-response.json') `
                            -Encoding UTF8
                        return New-FakeClassificationProcess -WaitResult $true -ExitCode 0
                    }
                    'invalid-response' {
                        [ordered]@{
                            schemaVersion = 1
                            event = 'filetransfer_tuna_gui_failure_classification_response'
                            classification = [ordered]@{
                                Phase = 'route_runtime'
                                Reason = 'partial'
                                ClassificationSource = 'immutable_evidence_partial'
                                ClassificationComplete = $true
                            }
                        } | ConvertTo-Json -Depth 32 | Set-Content `
                            -LiteralPath (Join-Path $script:artifactRoot 'filetransfer-tuna-gui-failure-classification-response.json') `
                            -Encoding UTF8
                        return New-FakeClassificationProcess -WaitResult $true -ExitCode 0
                    }
                    default { throw "unexpected fake process mode '$script:fakeProcessMode'" }
                }
            }
            function Stop-Process {
                param($Id, [switch]$Force, $ErrorAction)
                $script:stopCount++
            }
            function Assert-BaseClassificationPreserved {
                param($Receipt, [string]$Status, [string]$Reason)
                if ($Receipt.ClassificationEnrichmentStatus -ne $Status -or
                    $Receipt.ClassificationEnrichmentReason -ne $Reason -or
                    $Receipt.Classification.Phase -ne 'progress_failure' -or
                    $Receipt.Classification.Reason -ne 'gui_smoke_scenario_deadline_exceeded' -or
                    $Receipt.Classification.ClassificationSource -ne 'explicit_error' -or
                    -not $Receipt.Classification.ClassificationComplete) {
                    throw "primary classification was overwritten by diagnostic enrichment outcome: $($Receipt | ConvertTo-Json -Depth 8 -Compress)"
                }
            }

            $script:artifactRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-enrichment-receipt-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($script:artifactRoot)
            try {
                $base = New-TunaGuiFileTransferFailureClassificationResult `
                    -Phase 'progress_failure' `
                    -Reason 'gui_smoke_scenario_deadline_exceeded' `
                    -RouteMode 'live-regular-activation-cycle' `
                    -ClassificationSource 'explicit_error'
                $statePath = Join-Path $script:artifactRoot 'state.json'
                $env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH = $statePath
                $env:NLINK_TUNA_GUI_FAILURE_CLASSIFICATION_TIMEOUT_SECONDS_TEST_ONLY = '30'
                [ordered]@{ deadlineUtc = [DateTimeOffset]::UtcNow.AddSeconds(5).ToString('O') } |
                    ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8
                $script:fakeProcessMode = 'unexpected'
                $zeroBudget = Invoke-TunaGuiFailureClassificationEnrichment `
                    -ArtifactDir $script:artifactRoot `
                    -ErrorMessage 'gui_smoke_scenario_deadline_exceeded' `
                    -RouteMode 'live-regular-activation-cycle' `
                    -BaseClassification $base
                Assert-BaseClassificationPreserved $zeroBudget 'timed_out' 'evidence_enrichment_timeout'

                Remove-Item Env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH
                $script:fakeProcessMode = 'timeout'
                $script:stopCount = 0
                $forcedTimeout = Invoke-TunaGuiFailureClassificationEnrichment `
                    -ArtifactDir $script:artifactRoot `
                    -ErrorMessage 'gui_smoke_scenario_deadline_exceeded' `
                    -RouteMode 'live-regular-activation-cycle' `
                    -BaseClassification $base
                Assert-BaseClassificationPreserved $forcedTimeout 'timed_out' 'evidence_enrichment_timeout'
                if ($script:stopCount -ne 1) { throw 'timed-out enrichment child was not stopped exactly once' }

                $script:fakeProcessMode = 'process-failed'
                $processFailure = Invoke-TunaGuiFailureClassificationEnrichment -ArtifactDir $script:artifactRoot -ErrorMessage 'gui_smoke_scenario_deadline_exceeded' -RouteMode 'live-regular-activation-cycle' -BaseClassification $base
                Assert-BaseClassificationPreserved $processFailure 'failed' 'evidence_enrichment_child_exit_9'

                $script:fakeProcessMode = 'missing-response'
                $missingResponse = Invoke-TunaGuiFailureClassificationEnrichment -ArtifactDir $script:artifactRoot -ErrorMessage 'gui_smoke_scenario_deadline_exceeded' -RouteMode 'live-regular-activation-cycle' -BaseClassification $base
                Assert-BaseClassificationPreserved $missingResponse 'failed' 'evidence_enrichment_response_missing'

                $script:fakeProcessMode = 'invalid-response'
                $invalidResponse = Invoke-TunaGuiFailureClassificationEnrichment -ArtifactDir $script:artifactRoot -ErrorMessage 'gui_smoke_scenario_deadline_exceeded' -RouteMode 'live-regular-activation-cycle' -BaseClassification $base
                Assert-BaseClassificationPreserved $invalidResponse 'failed' 'evidence_enrichment_response_classification_invalid'

                $script:fakeProcessMode = 'completed'
                $completed = Invoke-TunaGuiFailureClassificationEnrichment -ArtifactDir $script:artifactRoot -ErrorMessage 'gui_smoke_scenario_deadline_exceeded' -RouteMode 'live-regular-activation-cycle' -BaseClassification $base
                if ($completed.ClassificationEnrichmentStatus -ne 'completed' -or
                    $completed.ClassificationEnrichmentReason -ne '(none)' -or
                    $completed.Classification.Phase -ne 'route_runtime' -or
                    $completed.Classification.Reason -ne 'exact_proof_chain_failure' -or
                    $completed.Classification.ClassificationSource -ne 'immutable_evidence_exact_proof' -or
                    -not $completed.Classification.ClassificationComplete) {
                    throw "valid completed enrichment response was not applied: $($completed | ConvertTo-Json -Depth 8 -Compress)"
                }
                Write-Output 'gui-enrichment-receipt-outcomes-ok'
            }
            finally {
                Remove-Item Env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH -ErrorAction SilentlyContinue
                Remove-Item Env:NLINK_TUNA_GUI_FAILURE_CLASSIFICATION_TIMEOUT_SECONDS_TEST_ONLY -ErrorAction SilentlyContinue
                if (Test-Path -LiteralPath $script:artifactRoot) {
                    Remove-Item -LiteralPath $script:artifactRoot -Recurse -Force
                }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(
            result.ExitCode == 0,
            $"Failure-classification enrichment receipt test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-enrichment-receipt-outcomes-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeFailureClassification_EnrichmentRealChildHydratesExitCodeBeforeValidation()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var resultBody = ExtractPowerShellFunctionBody(
            guiText,
            "New-TunaGuiFileTransferFailureClassificationResult",
            "Get-TunaGuiExplicitFailureClassification");
        var budgetBody = ExtractPowerShellFunctionBody(
            guiText,
            "Get-TunaGuiFailureClassificationBudgetMilliseconds",
            "ConvertTo-GuiSmokeProcessArgument");
        var argumentBody = ExtractPowerShellFunctionBody(
            guiText,
            "ConvertTo-GuiSmokeProcessArgument",
            "New-TunaGuiFailureClassificationEnrichmentReceipt");
        var receiptBody = ExtractPowerShellFunctionBody(
            guiText,
            "New-TunaGuiFailureClassificationEnrichmentReceipt",
            "Test-TunaGuiCompletedFailureClassificationResult");
        var completedBody = ExtractPowerShellFunctionBody(
            guiText,
            "Test-TunaGuiCompletedFailureClassificationResult",
            "Invoke-TunaGuiFailureClassificationRequest");
        var enrichmentBody = ExtractPowerShellFunctionBody(
            guiText,
            "Invoke-TunaGuiFailureClassificationEnrichment",
            "Wait-TunaGuiFileTransferAcceptOrThrow");
        var scriptText = $$"""
            param([string]$FailureClassificationRequestPath)
            $ErrorActionPreference = 'Stop'
            function New-TunaGuiFileTransferFailureClassificationResult { {{resultBody}} }
            function Get-TunaGuiFailureClassificationBudgetMilliseconds { {{budgetBody}} }
            function ConvertTo-GuiSmokeProcessArgument { {{argumentBody}} }
            function New-TunaGuiFailureClassificationEnrichmentReceipt { {{receiptBody}} }
            function Test-TunaGuiCompletedFailureClassificationResult { {{completedBody}} }
            function Invoke-TunaGuiFailureClassificationEnrichment { {{enrichmentBody}} }
            function Write-GuiSmokeAtomicJson {
                param($Path, $Value)
                $Value | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $Path -Encoding UTF8
            }

            if (-not [string]::IsNullOrWhiteSpace($FailureClassificationRequestPath)) {
                if ($env:NLINK_TEST_ENRICHMENT_CHILD_MODE -eq 'nonzero') { exit 9 }
                $request = Get-Content -LiteralPath $FailureClassificationRequestPath -Raw | ConvertFrom-Json
                $candidate = New-TunaGuiFileTransferFailureClassificationResult `
                    -Phase 'route_runtime' `
                    -Reason 'real_child_exact_proof' `
                    -RouteMode ([string]$request.routeMode) `
                    -ClassificationSource 'immutable_evidence_real_child'
                Write-GuiSmokeAtomicJson -Path ([string]$request.responsePath) -Value ([ordered]@{
                    schemaVersion = 1
                    event = 'filetransfer_tuna_gui_failure_classification_response'
                    classification = $candidate
                })
                exit 0
            }

            $artifactRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-enrichment-real-child-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($artifactRoot)
            try {
                $base = New-TunaGuiFileTransferFailureClassificationResult `
                    -Phase 'progress_failure' `
                    -Reason 'gui_smoke_scenario_deadline_exceeded' `
                    -RouteMode 'live-regular-activation-cycle' `
                    -ClassificationSource 'explicit_error'

                $env:NLINK_TEST_ENRICHMENT_CHILD_MODE = 'success'
                $completed = Invoke-TunaGuiFailureClassificationEnrichment `
                    -ArtifactDir $artifactRoot `
                    -ErrorMessage 'gui_smoke_scenario_deadline_exceeded' `
                    -RouteMode 'live-regular-activation-cycle' `
                    -BaseClassification $base
                if ($completed.ClassificationEnrichmentStatus -ne 'completed' -or
                    $completed.Classification.Reason -ne 'real_child_exact_proof') {
                    throw "real child response was rejected: $($completed | ConvertTo-Json -Depth 8 -Compress)"
                }

                $env:NLINK_TEST_ENRICHMENT_CHILD_MODE = 'nonzero'
                $failed = Invoke-TunaGuiFailureClassificationEnrichment `
                    -ArtifactDir $artifactRoot `
                    -ErrorMessage 'gui_smoke_scenario_deadline_exceeded' `
                    -RouteMode 'live-regular-activation-cycle' `
                    -BaseClassification $base
                if ($failed.ClassificationEnrichmentStatus -ne 'failed' -or
                    $failed.ClassificationEnrichmentReason -ne 'evidence_enrichment_child_exit_9' -or
                    $failed.Classification.Reason -ne 'gui_smoke_scenario_deadline_exceeded') {
                    throw "real child nonzero exit was not fail-closed: $($failed | ConvertTo-Json -Depth 8 -Compress)"
                }
                Write-Output 'gui-enrichment-real-child-exit-hydrated-ok'
            }
            finally {
                Remove-Item Env:NLINK_TEST_ENRICHMENT_CHILD_MODE -ErrorAction SilentlyContinue
                if (Test-Path -LiteralPath $artifactRoot) {
                    Remove-Item -LiteralPath $artifactRoot -Recurse -Force
                }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(
            result.ExitCode == 0,
            $"Real enrichment child process regression failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-enrichment-real-child-exit-hydrated-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeFailureClassification_ProvisionalAndFinalDtosPreservePrimaryDeadlineFailure()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var scenarioBody = ExtractPowerShellFunctionBody(
            guiText,
            "Run-ScenarioFileTransferTunaHandoffFallback",
            "Suspend-ProcessForRecoveryWindow");
        var summaryMatch = Regex.Match(
            scenarioBody,
            @"(?s)\$newFailureSummary\s*=\s*\{(?<body>.*?)\r?\n\s*\}\r?\n\s*try\s*\{");
        Assert.True(summaryMatch.Success, "Could not extract the Tuna GUI failure-summary projection.");
        var resultBody = ExtractPowerShellFunctionBody(
            guiText,
            "New-TunaGuiFileTransferFailureClassificationResult",
            "Get-TunaGuiExplicitFailureClassification");
        var receiptBody = ExtractPowerShellFunctionBody(
            guiText,
            "New-TunaGuiFailureClassificationEnrichmentReceipt",
            "Test-TunaGuiCompletedFailureClassificationResult");
        var summaryBody = summaryMatch.Groups["body"].Value;
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function New-TunaGuiFileTransferFailureClassificationResult { {{resultBody}} }
            function New-TunaGuiFailureClassificationEnrichmentReceipt { {{receiptBody}} }

            $direction = 'outbound'
            $payerMode = 'payer'
            $faultMode = 'off-on-off'
            $routeMode = 'live-regular-activation-cycle'
            $payloadSize = 268435456
            try {
                throw 'GUI_SMOKE_SCENARIO_DEADLINE_EXCEEDED: raw scenario error retained'
            }
            catch {
                $scenarioFailure = $_
            }
            $newFailureSummary = { {{summaryBody}} }
            $base = New-TunaGuiFileTransferFailureClassificationResult `
                -Phase 'progress_failure' `
                -Reason 'gui_smoke_scenario_deadline_exceeded' `
                -RouteMode $routeMode `
                -ClassificationSource 'explicit_error'
            $pending = New-TunaGuiFailureClassificationEnrichmentReceipt `
                -Classification $base `
                -Status 'pending' `
                -Reason '(none)'
            $timedOut = New-TunaGuiFailureClassificationEnrichmentReceipt `
                -Classification $base `
                -Status 'timed_out' `
                -Reason 'evidence_enrichment_timeout' `
                -ElapsedMilliseconds 123
            $provisional = & $newFailureSummary $base 'provisional' $pending
            $final = & $newFailureSummary $timedOut.Classification 'completed' $timedOut

            foreach ($field in @(
                    'completed',
                    'integrityOk',
                    'failurePhase',
                    'failureReason',
                    'failureVariant',
                    'classificationSource',
                    'classificationComplete',
                    'classificationEvidenceLineVisitCount',
                    'classificationEvidenceParsedRecordCount',
                    'classificationEvidenceLookupCount',
                    'classificationElapsedMilliseconds',
                    'error')) {
                if ([string]$provisional[$field] -cne [string]$final[$field]) {
                    throw "enrichment metadata overwrote primary field '$field': provisional=$($provisional[$field]); final=$($final[$field])"
                }
            }
            if ($provisional.failurePhase -ne 'progress_failure' -or
                $provisional.failureReason -ne 'gui_smoke_scenario_deadline_exceeded' -or
                $provisional.classificationSource -ne 'explicit_error' -or
                -not $provisional.classificationComplete -or
                $provisional.completed -or
                $provisional.integrityOk -or
                $provisional.error -ne 'GUI_SMOKE_SCENARIO_DEADLINE_EXCEEDED: raw scenario error retained') {
                throw "provisional primary deadline DTO was not retained: $($provisional | ConvertTo-Json -Depth 8 -Compress)"
            }
            if ($provisional.classificationStatus -ne 'provisional' -or
                $provisional.classificationEnrichmentStatus -ne 'pending' -or
                $provisional.classificationEnrichmentReason -ne '(none)' -or
                [long]$provisional.classificationEnrichmentElapsedMilliseconds -ne 0L -or
                $final.classificationStatus -ne 'completed' -or
                $final.classificationEnrichmentStatus -ne 'timed_out' -or
                $final.classificationEnrichmentReason -ne 'evidence_enrichment_timeout' -or
                [long]$final.classificationEnrichmentElapsedMilliseconds -ne 123L) {
                throw "classification enrichment metadata projection mismatch: provisional=$($provisional | ConvertTo-Json -Depth 8 -Compress); final=$($final | ConvertTo-Json -Depth 8 -Compress)"
            }
            Write-Output 'gui-enrichment-dto-preservation-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(
            result.ExitCode == 0,
            $"Failure-classification DTO preservation test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-enrichment-dto-preservation-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeFailureClassification_CorrelatesInitialActivationReceiveRecoveryHoldExhaustion()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        static string ExtractNestedFunction(
            string scriptText,
            string functionName,
            string nextFunctionName)
        {
            var pattern =
                @"(?s)function\s+" +
                Regex.Escape(functionName) +
                @"\s*\{(?<body>.*?)\r?\n\s*\}\s*\r?\n\s*function\s+" +
                Regex.Escape(nextFunctionName) +
                @"\b";
            var match = Regex.Match(scriptText, pattern);
            Assert.True(
                match.Success,
                $"Could not find nested PowerShell function body for {functionName}.");
            return match.Groups["body"].Value;
        }

        var fieldBody = ExtractNestedFunction(
            guiText,
            "Get-TunaGuiLogLineFieldValue",
            "Test-TunaGuiLogLineHasEvent");
        var eventBody = ExtractNestedFunction(
            guiText,
            "Test-TunaGuiLogLineHasEvent",
            "Test-TunaGuiLogLineFieldMatch");
        var correlationBody = ExtractNestedFunction(
            guiText,
            "Test-TunaGuiInitialActivationReceiveRecoveryHoldNotPreserved",
            "Test-TunaGuiReceiveRecoveryCompletedUnprovenSuccessorMissing");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Get-TunaGuiLogLineFieldValue { {{fieldBody}} }
            function Test-TunaGuiLogLineHasEvent { {{eventBody}} }
            function Test-TunaGuiInitialActivationReceiveRecoveryHoldNotPreserved { {{correlationBody}} }

            $scope = 'log_writer_id=writer-a; session_id=session-a; transfer_id=transfer-a'
            $operation = 'direction=outbound; receive_recovery_operation_authority_id=operation-a; receive_recovery_operation_generation=1; recovery_liveness_lease_authority_id=lease-a; recovery_liveness_lease_generation=1; closure_generation=1'
            $closure = "event=filetransfer_receive_recovery_operation_closure_retained; $scope; $operation; execution_state=Completed; execution_outcome=Completed; closure_state=Retained; proof_identity=(none)"
            $boundary2 = "event=runtime_unlock_offer_authority_boundary_decided; $scope; authority_mode=InitialActivation; decision_status=accepted; payer_decision_id=9; offer_generation=2; transaction_generation=1; tuna_path_lease_generation=2"
            $boundary3 = "event=runtime_unlock_offer_authority_boundary_decided; $scope; authority_mode=InitialActivation; decision_status=accepted; payer_decision_id=10; offer_generation=3; transaction_generation=1; tuna_path_lease_generation=3"
            $deferred = 'event=tuna_activation_control_send_deferred_for_regular_v4_recovery; log_writer_id=writer-a; session_id=session-a; purpose=offer; blocker_reason=receive_stall_recovery_awaiting_receive_proof'
            $exhausted = 'event=tuna_acceleration_retry_exhausted; log_writer_id=writer-a; reason=runtime_unlock_offer_send_not_observed'
            $failed = 'event=tuna_acceleration_activation_offer_not_observed; log_writer_id=writer-a; session_id=session-a; authority_mode=InitialActivation; payer_decision_id=10; generation=3; trigger=runtime_unlock_offer_send_not_observed; retry_scheduled=0; retry_after_recovery_armed=0; pause_deferred=1'
            $request = "$scope; direction=outbound; offer_nonce=offer-3; payer_decision_id=10; offer_generation=3; transaction_generation=1; tuna_path_lease_generation=3; tuna_path_lease_listener_run_id=listener-3; control_envelope_fingerprint=fingerprint-3"
            $held = "event=runtime_unlock_initial_activation_receive_recovery_hold_decided; $request; $operation; decision_status=Held"
            $adopted = "event=filetransfer_receive_recovery_operation_closure_adopted; $scope; $operation; closure_state=Adopted; proof_identity=proof-a"
            $released = "event=runtime_unlock_initial_activation_receive_recovery_hold_resumed; $request; $operation; decision_status=Released"
            $lines = @($closure, $boundary2, $deferred, $boundary3, $deferred, $exhausted, $failed)
            if (-not (Test-TunaGuiInitialActivationReceiveRecoveryHoldNotPreserved -Lines $lines)) {
                throw 'exact initial-activation recovery hold exhaustion was not classified'
            }

            $onTime = @($closure, $boundary2, $deferred, $boundary3, $deferred, $exhausted, $held, $adopted, $released, $failed)
            if (Test-TunaGuiInitialActivationReceiveRecoveryHoldNotPreserved -Lines $onTime) {
                throw 'on-time Held/adoption/release did not resolve GUI classification'
            }

            $releaseOnly = @($closure, $boundary2, $deferred, $boundary3, $deferred, $exhausted, $released, $failed)
            if (-not (Test-TunaGuiInitialActivationReceiveRecoveryHoldNotPreserved -Lines $releaseOnly)) {
                throw 'release without Held and proof adoption incorrectly resolved GUI classification'
            }

            $late = @($closure, $boundary2, $deferred, $boundary3, $deferred, $exhausted, $failed, $held, $adopted, $released)
            if (-not (Test-TunaGuiInitialActivationReceiveRecoveryHoldNotPreserved -Lines $late)) {
                throw 'late callbacks incorrectly recovered GUI classification'
            }

            $mismatchedRelease = $released.Replace('receive_recovery_operation_authority_id=operation-a', 'receive_recovery_operation_authority_id=operation-b')
            $mismatch = @($closure, $boundary2, $deferred, $boundary3, $deferred, $exhausted, $held, $adopted, $mismatchedRelease, $failed)
            if (-not (Test-TunaGuiInitialActivationReceiveRecoveryHoldNotPreserved -Lines $mismatch)) {
                throw 'mismatched release incorrectly recovered GUI classification'
            }

            $operationB = 'direction=outbound; receive_recovery_operation_authority_id=operation-b; receive_recovery_operation_generation=2; recovery_liveness_lease_authority_id=lease-b; recovery_liveness_lease_generation=2; closure_generation=2'
            $rebound = "event=runtime_unlock_initial_activation_receive_recovery_hold_rebound; $request; $operationB; previous_receive_recovery_operation_authority_id=operation-a; previous_receive_recovery_operation_generation=1; decision_status=Held"
            $adoptedB = "event=filetransfer_receive_recovery_operation_closure_adopted; $scope; $operationB; closure_state=Adopted; proof_identity=proof-b"
            $releasedB = "event=runtime_unlock_initial_activation_receive_recovery_hold_resumed; $request; $operationB; decision_status=Released"
            $reboundResolved = @($closure, $boundary2, $deferred, $boundary3, $deferred, $exhausted, $held, $rebound, $adoptedB, $releasedB, $failed)
            if (Test-TunaGuiInitialActivationReceiveRecoveryHoldNotPreserved -Lines $reboundResolved) {
                throw 'exact recovery-operation rebound did not resolve GUI classification'
            }

            $otherClosure = "event=filetransfer_receive_recovery_operation_closure_retained; log_writer_id=writer-a; session_id=session-a; transfer_id=transfer-b; direction=outbound; receive_recovery_operation_authority_id=operation-other; receive_recovery_operation_generation=7; recovery_liveness_lease_authority_id=lease-other; recovery_liveness_lease_generation=7; closure_generation=7; execution_state=Completed; execution_outcome=Completed; closure_state=Retained; proof_identity=(none)"
            $ambiguous = @($closure, $boundary2, $deferred, $boundary3, $otherClosure, $deferred, $exhausted, $failed)
            if (Test-TunaGuiInitialActivationReceiveRecoveryHoldNotPreserved -Lines $ambiguous) {
                throw 'cross-transfer deferral was wildcard-matched to the wrong recovery closure'
            }

            Write-Output 'gui-initial-activation-recovery-hold-correlation-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(
            result.ExitCode == 0,
            $"Initial-activation recovery-hold GUI classification failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains(
            "gui-initial-activation-recovery-hold-correlation-ok",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeFailureClassification_CorrelatesUnresolvedOutboundWorkerHandoff()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var containsAllBody = ExtractPowerShellFunctionBody(
            guiText,
            "Test-TunaGuiLogLineContainsAll",
            "Test-TunaGuiFallbackFrontierRequestDispatchTupleMatch");
        var handoffBody = ExtractPowerShellFunctionBody(
            guiText,
            "Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing",
            "Test-TunaGuiBridgeBootstrapNotReadyEvidence");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Test-TunaGuiLogLineContainsAll { {{containsAllBody}} }
            function Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing { {{handoffBody}} }

            $publication = 'event=filetransfer_runtime_started; direction=outbound; transfer_id=transfer-a; session_id=session-a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; route_publication_generation=5; route_commit_generation=0; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted'
            $stale = 'event=filetransfer_runtime_owner_stale_update_rejected; direction=outbound; transfer_id=transfer-a; session_id=session-a; source=v4_sender_failed; attempted_action=read_failed; reason=runtime_generation_mismatch; runtime_generation=1; current_runtime_generation=3; terminal_generation=0; current_terminal_generation=0; route=regular_nkn_v4_fast; current_route=post_tuna_fallback_v6; protocol_version=4; current_protocol_version=6; live_route_epoch=0; current_live_route_epoch=2'
            $delivery = 'event=filetransfer_control_plane_delivery_result; direction=inbound; transfer_id=transfer-a; session_id=session-a; kind=frontier_request; frame_type=filetransfer.frontier_request.v6; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; leg_generation=3; repair_request_id=v6-frontier:2709:1; request_correlation_id=frq-gui-handoff; peer_visible_any=1; accepted_any=1'
            if (Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing -Lines @($stale, $delivery)) { throw 'missing accepted publication was treated as worker authority' }
            if (-not (Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing -Lines @($publication, $stale, $delivery))) { throw 'current artifact correlation was not recognized' }

            $partialPublication = $publication.Replace('; route_publication_generation=5', '')
            if (Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing -Lines @($partialPublication, $stale, $delivery)) { throw 'partial publication manufactured worker authority' }
            $staleWithoutCurrentRuntime = $stale.Replace('current_runtime_generation=3; ', '')
            if (Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing -Lines @($publication, $staleWithoutCurrentRuntime, $delivery)) { throw 'attempted stale runtime generation substituted for missing current authority' }
            $wrongEpochDelivery = $delivery.Replace('live_route_epoch=2', 'live_route_epoch=9')
            if (Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing -Lines @($publication, $stale, $wrongEpochDelivery)) { throw 'wrong live-route epoch manufactured frontier evidence' }
            $wrongLegDelivery = $delivery.Replace('leg_generation=3', 'leg_generation=9')
            if (Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing -Lines @($publication, $stale, $wrongLegDelivery)) { throw 'wrong leg generation manufactured frontier evidence' }
            $uncorrelatedQueue = 'event=filetransfer_v6_frontier_request_transport_rejected; transfer_id=transfer-a; session_id=session-a; frame_type=filetransfer.frontier_request.v6; repair_request_id=v6-frontier:2709:99; request_correlation_id=frq-unbound-queue; decision_reason=priority_control_queue_capacity_exceeded'
            if (Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing -Lines @($publication, $stale, $uncorrelatedQueue)) { throw 'uncorrelated transport queue evidence manufactured a worker-handoff candidate' }

            $requiredInstall = 'event=filetransfer_outbound_sparse_worker_handoff_required; direction=outbound; transfer_id=transfer-a; session_id=session-a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; route_publication_generation=5; route_commit_generation=0; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn; install_generation=2; predecessor_install_generation=1; data_session_generation=2; runtime_family=post_tuna_fallback_v6; descriptor_terminal_generation=0; descriptor_leg_generation=3; installation_reason=stale_worker_superseded; runtime_owner_session_id=session-a; runtime_owner_transfer_id=transfer-a; runtime_owner_direction=outbound; runtime_owner_route=post_tuna_fallback_v6; runtime_owner_route_token=post_tuna_fallback_v6; runtime_owner_protocol_version=6; runtime_owner_live_route_epoch=2; runtime_owner_runtime_generation=3; runtime_owner_terminal_generation=0; runtime_owner_runtime_family=post_tuna_fallback_v6; runtime_owner_created_reason=post_tuna_file_fallback_active; route_publication_session_id=session-a; route_publication_transfer_id=transfer-a; route_publication_direction=outbound; route_publication_route=post_tuna_fallback_v6; route_publication_route_token=post_tuna_fallback_v6; route_publication_protocol_version=6; route_publication_live_route_epoch=2; route_publication_runtime_generation=3; route_publication_terminal_generation=0; route_publication_leg_generation=3; route_publication_route_commit_generation=0; route_publication_target_route=post_tuna_fallback_v6; route_publication_target_protocol_version=6; route_publication_target_transport=regular_nkn; route_publication_token_generation=5; route_publication_action_kind=RoutePublication; route_publication_reason=accepted; route_publication_owner_session_id=session-a; route_publication_owner_transfer_id=transfer-a; route_publication_owner_direction=outbound; route_publication_owner_route=post_tuna_fallback_v6; route_publication_owner_route_token=post_tuna_fallback_v6; route_publication_owner_protocol_version=6; route_publication_owner_live_route_epoch=2; route_publication_owner_runtime_generation=3; route_publication_owner_terminal_generation=0; route_publication_owner_runtime_family=post_tuna_fallback_v6; route_publication_owner_created_reason=post_tuna_file_fallback_active'
            $install = $requiredInstall.Replace('event=filetransfer_outbound_sparse_worker_handoff_required', 'event=filetransfer_outbound_sparse_worker_handoff_installed')
            $dispatch = $install.Replace('event=filetransfer_outbound_sparse_worker_handoff_installed', 'event=filetransfer_v6_frontier_request_receive_dispatched') + '; repair_request_id=v6-frontier:2709:1; request_correlation_id=frq-gui-handoff'
            if (Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing -Lines @($publication, $stale, $delivery, $requiredInstall, $install, $dispatch)) { throw 'valid required/install/dispatch interval did not resolve the candidate' }

            $progress = 'event=filetransfer_fallback_frontier_repair_progress_observed; direction=inbound; transfer_id=transfer-a; session_id=session-a; repair_request_key=v6-frontier:2709:1; reason=frontier_advanced; committed_frontier_before=2709; committed_frontier_after=2710; requested_chunk_count=1'
            if (-not (Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing -Lines @($publication, $stale, $delivery, $requiredInstall, $dispatch, $install))) { throw 'pre-install dispatch alone incorrectly proved a current consumer' }
            if (Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing -Lines @($publication, $stale, $delivery, $requiredInstall, $dispatch, $install, $progress)) { throw 'post-install correlated progress did not prove pending-dispatch consumption' }
            if (-not (Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing -Lines @($publication, $stale, $delivery, $requiredInstall, $dispatch, $progress, $install))) { throw 'progress before install incorrectly proved post-install consumption' }
            $wrongProgress = $progress.Replace('repair_request_key=v6-frontier:2709:1', 'repair_request_key=v6-frontier:unrelated:1')
            if (-not (Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing -Lines @($publication, $stale, $delivery, $requiredInstall, $dispatch, $install, $wrongProgress))) { throw 'unrelated progress incorrectly proved pending-dispatch consumption' }
            $nonAdvancingProgress = $progress.Replace('committed_frontier_after=2710', 'committed_frontier_after=2709')
            if (-not (Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing -Lines @($publication, $stale, $delivery, $requiredInstall, $dispatch, $install, $nonAdvancingProgress))) { throw 'non-advancing progress incorrectly proved pending-dispatch consumption' }

            if (-not (Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing -Lines @($publication, $stale, $delivery, $install, $dispatch))) { throw 'install without its required descriptor incorrectly resolved the candidate' }

            $wrongInstall = $install.Replace('runtime_generation=3', 'runtime_generation=4')
            if (-not (Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing -Lines @($publication, $stale, $delivery, $requiredInstall, $wrongInstall, $dispatch))) { throw 'mismatched publication tuple incorrectly resolved the candidate' }
            $wrongDataSessionInstall = $install.Replace('data_session_generation=2', 'data_session_generation=3')
            if (-not (Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing -Lines @($publication, $stale, $delivery, $requiredInstall, $wrongDataSessionInstall, $dispatch))) { throw 'mismatched data-session descriptor incorrectly resolved the candidate' }
            $partialInstall = $install.Replace('; predecessor_install_generation=1', '')
            if (-not (Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing -Lines @($publication, $stale, $delivery, $requiredInstall, $partialInstall, $dispatch))) { throw 'partial install descriptor incorrectly resolved the candidate' }
            $wrongDispatch = $dispatch.Replace('install_generation=2', 'install_generation=9')
            if (-not (Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing -Lines @($publication, $stale, $delivery, $requiredInstall, $install, $wrongDispatch))) { throw 'mismatched dispatch descriptor incorrectly resolved the candidate' }
            $missingInstallationReason = $install.Replace('; installation_reason=stale_worker_superseded', '')
            if (-not (Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing -Lines @($publication, $stale, $delivery, $requiredInstall, $missingInstallationReason, $dispatch))) { throw 'install without installation reason incorrectly resolved the candidate' }
            $wrongInstallationReason = $dispatch.Replace('installation_reason=stale_worker_superseded', 'installation_reason=wrong_install_reason')
            if (-not (Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing -Lines @($publication, $stale, $delivery, $requiredInstall, $install, $wrongInstallationReason))) { throw 'mismatched installation reason incorrectly resolved the candidate' }
            $wrongPublicationOwner = $dispatch.Replace('route_publication_owner_runtime_generation=3', 'route_publication_owner_runtime_generation=9')
            if (-not (Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing -Lines @($publication, $stale, $delivery, $requiredInstall, $install, $wrongPublicationOwner))) { throw 'mismatched publication owner incorrectly resolved the candidate' }
            $wrongDescriptorTerminal = $dispatch.Replace('descriptor_terminal_generation=0', 'descriptor_terminal_generation=9')
            if (-not (Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing -Lines @($publication, $stale, $delivery, $requiredInstall, $install, $wrongDescriptorTerminal))) { throw 'mismatched descriptor terminal generation incorrectly resolved the candidate' }

            $supervisorStopped = $install.Replace('event=filetransfer_outbound_sparse_worker_handoff_installed', 'event=filetransfer_outbound_sparse_receive_supervisor_stopped') + '; receive_supervisor_generation=1; terminal=0; decision_reason=read_failed'
            $laterDelivery = $delivery.Replace('kind=frontier_request', 'kind=fallback_checkpoint_request')
            if (-not (Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing -Lines @($publication, $stale, $delivery, $requiredInstall, $install, $dispatch, $supervisorStopped, $laterDelivery))) { throw 'nonterminal supervisor stop did not invalidate current-consumer proof' }

            $requiredInstall3 = $requiredInstall.Replace('install_generation=2', 'install_generation=3').Replace('predecessor_install_generation=1', 'predecessor_install_generation=2')
            $install3 = $requiredInstall3.Replace('event=filetransfer_outbound_sparse_worker_handoff_required', 'event=filetransfer_outbound_sparse_worker_handoff_installed')
            $dispatch3 = $install3.Replace('event=filetransfer_outbound_sparse_worker_handoff_installed', 'event=filetransfer_v6_frontier_request_receive_dispatched')
            if (Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing -Lines @($publication, $stale, $delivery, $requiredInstall, $install, $dispatch, $supervisorStopped, $laterDelivery, $requiredInstall3, $install3, $dispatch3)) { throw 'fresh complete install and dispatch did not restore current-consumer proof' }

            $terminal = 'event=file_transfer_outbound_terminal; direction=outbound; transfer_id=transfer-a; session_id=session-a; state=Failed'
            if (Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing -Lines @($publication, $stale, $delivery, $terminal)) { throw 'terminal closure left a worker-handoff candidate' }

            $unrelatedDelivery = $delivery.Replace('transfer_id=transfer-a', 'transfer_id=transfer-b')
            if (Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing -Lines @($publication, $stale, $unrelatedDelivery)) { throw 'unrelated frontier delivery was correlated' }
            $unboundDelivery = $delivery.Replace('; session_id=session-a', '')
            if (Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing -Lines @($publication, $stale, $unboundDelivery)) { throw 'frontier delivery without complete identity was correlated' }

            $successorPublication = $publication.Replace('live_route_epoch=2', 'live_route_epoch=4').Replace('runtime_generation=3', 'runtime_generation=5').Replace('leg_generation=3', 'leg_generation=5').Replace('route_publication_generation=5', 'route_publication_generation=8')
            $successorInstall = $install.Replace('live_route_epoch=2', 'live_route_epoch=4').Replace('runtime_generation=3', 'runtime_generation=5').Replace('leg_generation=3', 'leg_generation=5').Replace('route_publication_generation=5', 'route_publication_generation=8').Replace('install_generation=2', 'install_generation=3')
            $successorDispatch = $dispatch.Replace('live_route_epoch=2', 'live_route_epoch=4').Replace('runtime_generation=3', 'runtime_generation=5').Replace('leg_generation=3', 'leg_generation=5').Replace('route_publication_generation=5', 'route_publication_generation=8')
            if (Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing -Lines @($publication, $stale, $successorPublication, $successorInstall, $successorDispatch, $delivery)) { throw 'superseded publication retained a stale worker-handoff candidate' }

            $required = 'event=filetransfer_outbound_sparse_worker_handoff_required; direction=outbound; transfer_id=transfer-c; session_id=session-c; route=post_tuna_fallback_v6; runtime_generation=7; install_generation=4'
            $requiredDelivery = $delivery.Replace('transfer_id=transfer-a', 'transfer_id=transfer-c').Replace('session_id=session-a', 'session_id=session-c')
            if (Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing -Lines @($required, $requiredDelivery)) { throw 'legitimate staged handoff was misclassified without a stale worker failure' }

            Write-Output 'gui-worker-handoff-correlation-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(result.ExitCode == 0, $"Worker-handoff classification test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-worker-handoff-correlation-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeFailureClassification_DetectsRouteTransitionPressureLocalLifecycleSplit()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var nestedFunctionText = Regex.Replace(guiText, @"(?m)^ {4}", string.Empty);
        var fieldValueBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Get-TunaGuiLogLineFieldValue",
            "Get-TunaGuiRetiredPendingAnswerHandoffFailure");
        var classifierMatch = Regex.Match(
            nestedFunctionText,
            @"(?s)function\s+Get-TunaGuiRouteTransitionPressureLocalLifecycleFailure\s*\{(?<body>.*?)\r?\n\}\s*\r?\n\s*\$earlyPredicateEventNames\s*=\s*@\{");
        Assert.True(classifierMatch.Success, "Could not extract the GUI local-lifecycle correlator.");
        var classifierBody = classifierMatch.Groups["body"].Value;
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Get-TunaGuiLogLineFieldValue { {{fieldValueBody}} }
            function Get-TunaGuiRouteTransitionPressureLocalLifecycleFailure { {{classifierBody}} }

            $writer = 'writer-a'
            $repair = 'fta2-repair-a'
            $transition = 'fta2-transition-a'
            $scope = 'session_id=session-a; transfer_id=transfer-a; direction=outbound; route=file_tuna_v4; protocol_version=4; live_route_epoch=3; runtime_generation=4; terminal_generation=0; leg_generation=4; transport_epoch=0; bridge_recovery_generation=0; route_publication_generation=4; route_commit_generation=2; target_transport=tuna'
            $source = 'session_id=session-a; transfer_id=transfer-a; direction=outbound; source_route=file_tuna_v4; source_protocol_version=4; source_live_route_epoch=3; source_runtime_generation=4; source_terminal_generation=0; source_leg_generation=4; source_transport_epoch=0; source_bridge_recovery_generation=0; source_route_publication_generation=4; source_route_commit_generation=2; source_data_session_generation=1; transition_kind=TunaV4ToPostTunaFallbackV6; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn'
            $lines = @(
                "event=filetransfer_regular_v4_frontier_repair_pressure_binding_committed; authority_id=$repair; repair_request_key=40:2:40:2; install_generation=3; $scope; log_writer_id=$writer",
                "event=filetransfer_route_transition_prepared; authority_id=$transition; transition_generation=4; decision_status=Prepared; $source; log_writer_id=$writer",
                "event=filetransfer_route_transition_pause_bound; authority_id=$transition; transition_generation=4; $source; log_writer_id=$writer",
                "event=filetransfer_route_transition_draining; authority_id=$transition; transition_generation=4; decision_status=Draining; $source; log_writer_id=$writer",
                "event=filetransfer_regular_v4_frontier_repair_settlement_accepted; authority_id=$repair; repair_request_key=40:2:40:2; successor_install_generation=3; outcome=StaleRejected; settlement_reason=filetransfer_route_transition_pressure_handoff; $scope; log_writer_id=$writer",
                "event=filetransfer_regular_v4_frontier_repair_pressure_settlement_apply_decided; authority_id=[redacted]; install_generation=3; settlement_outcome=StaleRejected; apply_status=applied; current_authority_cleared=1; retained_predecessor_cleared=0; session_id=session-a; transfer_id=transfer-a; direction=outbound; log_writer_id=$writer",
                "event=filetransfer_route_transition_drain_receipt_decided; authority_id=$transition; transition_generation=4; drain_outcome=Completed; decision_status=Accepted; $source; log_writer_id=$writer",
                "event=filetransfer_route_transition_committed; authority_id=$transition; transition_generation=4; decision_status=Committed; $source; log_writer_id=$writer",
                "event=filetransfer_route_selected; transition_generation=4; route=post_tuna_fallback_v6; protocol_version=6; session_id=session-a; transfer_id=transfer-a; direction=outbound; log_writer_id=$writer",
                "event=filetransfer_route_transition_disposition_applied; authority_id=$transition; transition_generation=4; decision_status=Applied; $source; log_writer_id=$writer",
                "event=filetransfer_regular_v4_frontier_repair_settlement_rejected; authority_id=$repair; repair_request_key=40:2:40:2; attempted_outcome=TransferTerminal; gui_evidence_sequence=11; log_writer_id=$writer"
            )
            $failure = Get-TunaGuiRouteTransitionPressureLocalLifecycleFailure -Lines $lines
            if (-not $failure.Unresolved -or $failure.FirstSequence -ne 11) {
                throw 'exact local lifecycle split was not classified'
            }

            $settled = "event=filetransfer_regular_v4_frontier_repair_contract_settled; authority_id=$repair; repair_request_key=40:2:40:2; settlement_outcome=StaleRejected; settlement_source=route_transition_pressure_commit; log_writer_id=$writer"
            if ((Get-TunaGuiRouteTransitionPressureLocalLifecycleFailure -Lines @($lines[0..9] + $settled + $lines[10])).Unresolved) {
                throw 'commit-time local lifecycle adoption remained classified'
            }

            $wrongWriter = @($lines)
            $wrongWriter[5] = $wrongWriter[5].Replace("log_writer_id=$writer", 'log_writer_id=writer-b')
            if ((Get-TunaGuiRouteTransitionPressureLocalLifecycleFailure -Lines $wrongWriter).Unresolved) {
                throw 'cross-writer apply admitted the lifecycle incident'
            }
            $wrongDataSession = @($lines)
            $wrongDataSession[6] = $wrongDataSession[6].Replace('source_data_session_generation=1', 'source_data_session_generation=2')
            if ((Get-TunaGuiRouteTransitionPressureLocalLifecycleFailure -Lines $wrongDataSession).Unresolved) {
                throw 'mutated data-session lineage admitted the lifecycle incident'
            }

            Write-Output 'gui-route-transition-pressure-local-lifecycle-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(
            result.ExitCode == 0,
            $"GUI local-lifecycle classification test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-route-transition-pressure-local-lifecycle-ok", result.Stdout, StringComparison.Ordinal);
        Assert.Contains(
            "committed_route_transition_pressure_settlement_not_adopted_by_local_repair_lifecycle",
            guiText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeFailureClassification_SupersededRepairRequiresExactSuccessorCleanup()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var nestedFunctionText = Regex.Replace(guiText, @"(?m)^ {4}", string.Empty);
        var fieldValueBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Get-TunaGuiLogLineFieldValue",
            "Test-TunaGuiLogLineHasEvent");
        var hasEventBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiLogLineHasEvent",
            "Test-TunaGuiLogLineFieldMatch");
        var fieldMatchBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiLogLineFieldMatch",
            "Test-TunaGuiRuntimeUnlockFileTransferOwnerRejectionUnresolved");
        var settlementClosedBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiRegularV4FrontierRepairSettlementClosed",
            "Test-TunaGuiRegularV4PredecessorSettlementClosedSuccessor");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Get-TunaGuiLogLineFieldValue { {{fieldValueBody}} }
            function Test-TunaGuiLogLineHasEvent { {{hasEventBody}} }
            function Test-TunaGuiLogLineFieldMatch { {{fieldMatchBody}} }
            function Test-TunaGuiRegularV4FrontierRepairSettlementClosed { {{settlementClosedBody}} }

            $scope = 'session_id=session-a; transfer_id=transfer-a; direction=outbound; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation=1; terminal_generation=0; leg_generation=1; transport_epoch=0; bridge_recovery_generation=0; bridge_decision_generation=1; route_publication_generation=1; route_commit_generation=0; target_transport=regular_nkn'
            $predecessor = 'fta2-repair_replay-predecessor'
            $successor = 'fta2-repair_replay-successor'
            $repairKey = 'regular_v4_peer_silence_safety_replay:0:0:64:1'
            $lines = @(
                "event=filetransfer_regular_v4_frontier_repair_pressure_binding_committed; authority_id=$predecessor; predecessor_authority_id=(none); $scope; repair_request_key=$repairKey; install_generation=1; binding_state=Installed",
                "event=filetransfer_regular_v4_frontier_repair_pressure_binding_committed; authority_id=$successor; predecessor_authority_id=$predecessor; $scope; repair_request_key=regular_v4_peer_silence_safety_replay:0:0:64:2; install_generation=2; binding_state=Installed",
                "event=filetransfer_regular_v4_frontier_repair_settlement_accepted; authority_id=$predecessor; successor_authority_id=$successor; successor_install_generation=2; outcome=Superseded; repair_request_key=$repairKey; $scope",
                "event=filetransfer_regular_v4_frontier_repair_pressure_settlement_apply_decided; session_id=session-a; transfer_id=transfer-a; direction=outbound; authority_id=[redacted]; successor_authority_id=[redacted]; current_authority_id=[redacted]; retained_predecessor_authority_id=[redacted]; install_generation=2; settlement_outcome=Superseded; apply_status=applied; current_authority_cleared=0; retained_predecessor_cleared=1",
                "event=filetransfer_regular_v4_frontier_repair_pressure_binding_retired; authority_id=$predecessor; predecessor_authority_id=(none); $scope; repair_request_key=$repairKey; install_generation=1; binding_state=RetainedPredecessor; install_boundary=settlement",
                "event=filetransfer_regular_v4_frontier_repair_contract_settled; authority_id=$predecessor; successor_authority_id=$successor; repair_request_key=$repairKey; settlement_outcome=Superseded"
            )
            if (-not (Test-TunaGuiRegularV4FrontierRepairSettlementClosed -Lines $lines)) {
                throw 'exact Superseded cleanup was not accepted'
            }

            $mutated = @($lines)
            $mutated[3] = $mutated[3].Replace('current_authority_cleared=0', 'current_authority_cleared=1')
            if (Test-TunaGuiRegularV4FrontierRepairSettlementClosed -Lines $mutated) {
                throw 'mutated Superseded apply was accepted'
            }

            Write-Output 'gui-superseded-cleanup-correlation-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(
            result.ExitCode == 0,
            $"Superseded GUI classification test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-superseded-cleanup-correlation-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepairSettlementRequiresExactCleanup_ForCurrentPressureOutcomes()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var nestedFunctionText = Regex.Replace(guiText, @"(?m)^ {4}", string.Empty);
        var fieldValueBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Get-TunaGuiLogLineFieldValue",
            "Test-TunaGuiLogLineHasEvent");
        var hasEventBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiLogLineHasEvent",
            "Test-TunaGuiLogLineFieldMatch");
        var fieldMatchBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiLogLineFieldMatch",
            "Test-TunaGuiRuntimeUnlockFileTransferOwnerRejectionUnresolved");
        var settlementClosedBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiRegularV4FrontierRepairSettlementClosed",
            "Test-TunaGuiRegularV4PredecessorSettlementClosedSuccessor");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Get-TunaGuiLogLineFieldValue { {{fieldValueBody}} }
            function Test-TunaGuiLogLineHasEvent { {{hasEventBody}} }
            function Test-TunaGuiLogLineFieldMatch { {{fieldMatchBody}} }
            function Test-TunaGuiRegularV4FrontierRepairSettlementClosed { {{settlementClosedBody}} }

            $scope = 'session_id=session-a; transfer_id=transfer-a; direction=inbound; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation=1; terminal_generation=0; leg_generation=1; transport_epoch=0; bridge_recovery_generation=0; bridge_decision_generation=1; route_publication_generation=1; route_commit_generation=0; target_transport=regular_nkn'
            $authority = 'fta2-repair_replay-current'
            $repairKey = '42:12:42:12'
            foreach ($outcome in @('ProgressObserved', 'MissingDispatch', 'SettledStalled')) {
                $lines = @(
                    "event=filetransfer_regular_v4_frontier_repair_pressure_binding_committed; authority_id=$authority; predecessor_authority_id=(none); $scope; repair_request_key=$repairKey; install_generation=7; binding_state=Installed",
                    "event=filetransfer_regular_v4_frontier_repair_settlement_accepted; authority_id=$authority; successor_authority_id=(none); successor_install_generation=7; outcome=$outcome; repair_request_key=$repairKey; $scope",
                    "event=filetransfer_regular_v4_frontier_repair_pressure_settlement_apply_decided; session_id=session-a; transfer_id=transfer-a; direction=inbound; authority_id=[redacted]; successor_authority_id=(none); current_authority_id=[redacted]; retained_predecessor_authority_id=(none); install_generation=7; settlement_outcome=$outcome; apply_status=Applied; current_authority_cleared=1; retained_predecessor_cleared=0",
                    "event=filetransfer_regular_v4_frontier_repair_pressure_binding_retired; authority_id=$authority; predecessor_authority_id=(none); $scope; repair_request_key=$repairKey; install_generation=7; binding_state=Installed; install_boundary=settlement",
                    "event=filetransfer_regular_v4_frontier_repair_contract_settled; authority_id=$authority; successor_authority_id=(none); repair_request_key=$repairKey; settlement_outcome=$outcome"
                )
                if (-not (Test-TunaGuiRegularV4FrontierRepairSettlementClosed -Lines $lines)) {
                    throw "exact current-binding cleanup was not accepted for $outcome"
                }

                $ambiguous = @($lines[0], $lines[1], $lines[2],
                    $lines[2].Replace('apply_status=Applied', 'apply_status=Idempotent'),
                    $lines[3], $lines[4])
                if (Test-TunaGuiRegularV4FrontierRepairSettlementClosed -Lines $ambiguous) {
                    throw "ambiguous redacted current-binding apply was accepted for $outcome"
                }
            }

            foreach ($outcome in @('TransferTerminal', 'StaleRejected')) {
                $terminalLines = @(
                    "event=filetransfer_regular_v4_frontier_repair_pressure_binding_committed; authority_id=$authority; predecessor_authority_id=(none); $scope; repair_request_key=$repairKey; install_generation=7; binding_state=Installed",
                    "event=filetransfer_regular_v4_frontier_repair_settlement_accepted; authority_id=$authority; successor_authority_id=(none); successor_install_generation=7; outcome=$outcome; repair_request_key=$repairKey; $scope",
                    "event=filetransfer_regular_v4_frontier_repair_pressure_settlement_apply_decided; session_id=session-a; transfer_id=transfer-a; direction=inbound; authority_id=[redacted]; successor_authority_id=(none); current_authority_id=[redacted]; retained_predecessor_authority_id=(none); install_generation=7; settlement_outcome=$outcome; apply_status=Applied; current_authority_cleared=1; retained_predecessor_cleared=0",
                    "event=filetransfer_regular_v4_frontier_repair_pressure_binding_retired; authority_id=$authority; predecessor_authority_id=(none); $scope; repair_request_key=$repairKey; install_generation=7; binding_state=Installed; install_boundary=settlement",
                    "event=filetransfer_regular_v4_frontier_repair_contract_settled; authority_id=$authority; successor_authority_id=(none); repair_request_key=$repairKey; settlement_outcome=$outcome"
                )
                if (-not (Test-TunaGuiRegularV4FrontierRepairSettlementClosed -Lines $terminalLines)) {
                    throw "exact terminal diagnostic cleanup was not accepted for $outcome"
                }

                $missingContract = @($terminalLines[0..3])
                if (-not (Test-TunaGuiRegularV4FrontierRepairSettlementClosed -Lines $missingContract)) {
                    throw "terminal diagnostic cleanup required optional contract settlement for $outcome"
                }

                $wrongContract = @($terminalLines)
                $wrongContract[4] = $wrongContract[4].Replace(
                    "settlement_outcome=$outcome",
                    'settlement_outcome=ProgressObserved')
                if (-not (Test-TunaGuiRegularV4FrontierRepairSettlementClosed -Lines $wrongContract)) {
                    throw "terminal diagnostic cleanup treated optional contract telemetry as authority for $outcome"
                }

                $wrongFlags = @($terminalLines)
                $wrongFlags[2] = $wrongFlags[2].Replace(
                    'current_authority_cleared=1',
                    'current_authority_cleared=0')
                if (Test-TunaGuiRegularV4FrontierRepairSettlementClosed -Lines $wrongFlags) {
                    throw "terminal diagnostic cleanup accepted wrong clear flags for $outcome"
                }
            }

            Write-Output 'gui-current-pressure-cleanup-correlation-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(
            result.ExitCode == 0,
            $"Current-pressure GUI classification test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-current-pressure-cleanup-correlation-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepairSettlement_RegularV4ToTunaAtomicTransitionClosesStaleRejectedPressure()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var nestedFunctionText = Regex.Replace(guiText, @"(?m)^ {4}", string.Empty);
        var fieldValueBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Get-TunaGuiLogLineFieldValue",
            "Test-TunaGuiLogLineHasEvent");
        var hasEventBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiLogLineHasEvent",
            "Test-TunaGuiLogLineFieldMatch");
        var fieldMatchBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiLogLineFieldMatch",
            "Test-TunaGuiRuntimeUnlockFileTransferOwnerRejectionUnresolved");
        var settlementClosedBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiRegularV4FrontierRepairSettlementClosed",
            "Test-TunaGuiRegularV4PredecessorSettlementClosedSuccessor");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Get-TunaGuiLogLineFieldValue { {{fieldValueBody}} }
            function Test-TunaGuiLogLineHasEvent { {{hasEventBody}} }
            function Test-TunaGuiLogLineFieldMatch { {{fieldMatchBody}} }
            function Test-TunaGuiRegularV4FrontierRepairSettlementClosed { {{settlementClosedBody}} }

            $source = 'session_id=session-a; transfer_id=transfer-a; direction=outbound; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation=1; terminal_generation=0; leg_generation=1; transport_epoch=0; bridge_recovery_generation=0; bridge_decision_generation=0; route_publication_generation=1; route_commit_generation=0; target_transport=regular_nkn'
            $transition = 'session_id=session-a; transfer_id=transfer-a; direction=outbound; authority_id=transition-a; transition_generation=1; transition_kind=RegularV4ToTunaV4; source_route=regular_nkn_v4_fast; source_protocol_version=4; source_transport=regular_nkn; source_live_route_epoch=0; source_runtime_generation=1; source_terminal_generation=0; source_leg_generation=1; source_transport_epoch=0; source_bridge_recovery_generation=0; source_route_publication_generation=1; source_route_commit_generation=0; source_data_session_generation=1; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna'
            $lines = @(
                "event=filetransfer_regular_v4_frontier_repair_pressure_binding_committed; $source; authority_id=repair-a; install_generation=46; repair_request_key=600:12:600:6,609:6; log_writer_id=writer-a; log_writer_sequence=1",
                "event=filetransfer_route_transition_prepared; $transition; decision_status=Prepared; log_writer_id=writer-a; log_writer_sequence=2",
                "event=filetransfer_route_transition_pause_bound; $transition; decision_status=Installed; log_writer_id=writer-a; log_writer_sequence=3",
                "event=filetransfer_route_transition_draining; $transition; decision_status=Draining; log_writer_id=writer-a; log_writer_sequence=4",
                "event=filetransfer_regular_v4_frontier_repair_settlement_accepted; $source; authority_id=repair-a; successor_authority_id=(none); successor_install_generation=46; outcome=StaleRejected; settlement_reason=filetransfer_route_transition_pressure_handoff; repair_request_key=600:12:600:6,609:6; log_writer_id=writer-a; log_writer_sequence=5",
                'event=filetransfer_regular_v4_frontier_repair_pressure_settlement_apply_decided; session_id=session-a; transfer_id=transfer-a; direction=outbound; authority_id=[redacted]; successor_authority_id=(none); current_authority_id=[redacted]; retained_predecessor_authority_id=(none); install_generation=46; settlement_outcome=StaleRejected; apply_status=Applied; current_authority_cleared=1; retained_predecessor_cleared=0; log_writer_id=writer-a; log_writer_sequence=6',
                'event=filetransfer_regular_v4_frontier_repair_pressure_settlement_apply_decided; session_id=session-a; transfer_id=transfer-a; direction=outbound; authority_id=[redacted]; successor_authority_id=(none); current_authority_id=[redacted]; retained_predecessor_authority_id=(none); install_generation=46; settlement_outcome=StaleRejected; apply_status=Idempotent; current_authority_cleared=1; retained_predecessor_cleared=0; log_writer_id=writer-a; log_writer_sequence=7',
                "event=filetransfer_route_transition_drain_receipt_decided; $transition; decision_status=Accepted; log_writer_id=writer-a; log_writer_sequence=8",
                "event=filetransfer_route_transition_committed; $transition; target_live_route_epoch=1; target_runtime_generation=2; target_terminal_generation=0; target_leg_generation=2; target_route_commit_generation=1; target_route_publication_generation=2; decision_status=Committed; log_writer_id=writer-a; log_writer_sequence=9",
                "event=filetransfer_route_transition_disposition_applied; $transition; decision_status=Applied; log_writer_id=writer-a; log_writer_sequence=10",
                'event=filetransfer_route_selected; session_id=session-a; transfer_id=transfer-a; direction=outbound; route=file_tuna_v4; protocol_version=4; live_route_epoch=1; runtime_generation=2; terminal_generation=0; leg_generation=2; route_commit_generation=1; route_publication_generation=2; log_writer_id=writer-a; log_writer_sequence=11'
            )
            if (-not (Test-TunaGuiRegularV4FrontierRepairSettlementClosed -Lines $lines -RequireRecoveryProof)) {
                throw 'exact Regular-to-Tuna atomic settlement was not accepted'
            }

            $mutated = @($lines)
            $mutated[10] = $mutated[10].Replace('route_publication_generation=2', 'route_publication_generation=3')
            if (Test-TunaGuiRegularV4FrontierRepairSettlementClosed -Lines $mutated -RequireRecoveryProof) {
                throw 'mutated Tuna publication recovered the settlement'
            }

            Write-Output 'gui-regular-to-tuna-atomic-settlement-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(
            result.ExitCode == 0,
            $"Regular-to-Tuna GUI settlement test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-regular-to-tuna-atomic-settlement-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeFailureClassification_SettlementIndexVisitsArtifactScaleEvidenceOnce()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var nestedFunctionText = Regex.Replace(guiText, @"(?m)^ {4}", string.Empty);
        var fieldValueBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Get-TunaGuiLogLineFieldValue",
            "Test-TunaGuiLogLineHasEvent");
        var hasEventBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiLogLineHasEvent",
            "Test-TunaGuiLogLineFieldMatch");
        var fieldMatchBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiLogLineFieldMatch",
            "Test-TunaGuiRuntimeUnlockFileTransferOwnerRejectionUnresolved");
        var settlementClosedBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiRegularV4FrontierRepairSettlementClosed",
            "Test-TunaGuiRegularV4PredecessorSettlementClosedSuccessor");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Get-TunaGuiLogLineFieldValue { {{fieldValueBody}} }
            function Test-TunaGuiLogLineHasEvent { {{hasEventBody}} }
            function Test-TunaGuiLogLineFieldMatch { {{fieldMatchBody}} }
            function Test-TunaGuiRegularV4FrontierRepairSettlementClosed { {{settlementClosedBody}} }

            $lines = [System.Collections.Generic.List[string]]::new(100001)
            for ($index = 0; $index -lt 95000; $index++) {
                $lines.Add("event=unrelated_evidence; log_writer_id=writer-a; log_writer_sequence=$($index + 1)") | Out-Null
            }
            for ($chain = 0; $chain -lt 1000; $chain++) {
                $authority = "authority-$chain"
                $install = $chain + 1
                $repairKey = "repair-$chain"
                $baseSequence = 95001 + ($chain * 5)
                $scope = "session_id=session-a; transfer_id=transfer-a; direction=inbound; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation=1; terminal_generation=0; leg_generation=1; transport_epoch=0; bridge_recovery_generation=0; bridge_decision_generation=1; route_publication_generation=1; route_commit_generation=0; target_transport=regular_nkn"
                $lines.Add("event=filetransfer_regular_v4_frontier_repair_pressure_binding_committed; log_writer_id=writer-a; log_writer_sequence=$baseSequence; authority_id=$authority; predecessor_authority_id=(none); $scope; repair_request_key=$repairKey; install_generation=$install; binding_state=Installed") | Out-Null
                $lines.Add("event=filetransfer_regular_v4_frontier_repair_settlement_accepted; log_writer_id=writer-a; log_writer_sequence=$($baseSequence + 1); authority_id=$authority; successor_authority_id=(none); successor_install_generation=$install; outcome=ProgressObserved; repair_request_key=$repairKey; $scope") | Out-Null
                $lines.Add("event=filetransfer_regular_v4_frontier_repair_pressure_settlement_apply_decided; log_writer_id=writer-a; log_writer_sequence=$($baseSequence + 2); session_id=session-a; transfer_id=transfer-a; direction=inbound; authority_id=[redacted]; successor_authority_id=(none); current_authority_id=[redacted]; retained_predecessor_authority_id=(none); install_generation=$install; settlement_outcome=ProgressObserved; apply_status=Applied; current_authority_cleared=1; retained_predecessor_cleared=0") | Out-Null
                $lines.Add("event=filetransfer_regular_v4_frontier_repair_pressure_binding_retired; log_writer_id=writer-a; log_writer_sequence=$($baseSequence + 3); authority_id=$authority; predecessor_authority_id=(none); $scope; repair_request_key=$repairKey; install_generation=$install; binding_state=Installed; install_boundary=settlement") | Out-Null
                $lines.Add("event=filetransfer_regular_v4_frontier_repair_contract_settled; log_writer_id=writer-a; log_writer_sequence=$($baseSequence + 4); authority_id=$authority; successor_authority_id=(none); repair_request_key=$repairKey; settlement_outcome=ProgressObserved") | Out-Null
            }
            $lines.Add($lines[95000]) | Out-Null

            $metrics = [pscustomobject]@{
                parsedRecordCount = 0L
                settlementLookupCount = 0L
            }
            if (-not (Test-TunaGuiRegularV4FrontierRepairSettlementClosed -Lines $lines.ToArray() -Metrics $metrics)) {
                throw 'artifact-scale settlement evidence did not close'
            }
            if ([long]$metrics.parsedRecordCount -ne 100000L) {
                throw "writer-copy deduplication or single-pass parsing failed: $($metrics | ConvertTo-Json -Compress)"
            }
            if ([long]$metrics.settlementLookupCount -gt 5001L) {
                throw "settlement correlation exceeded bounded keyed lookups: $($metrics | ConvertTo-Json -Compress)"
            }
            Write-Output 'gui-settlement-index-scale-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(
            scriptText,
            Array.Empty<string>());

        Assert.True(
            result.ExitCode == 0,
            $"Artifact-scale settlement index test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-settlement-index-scale-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitialActivationAnswerPeerDeliveryDetectsPeerRecoveryReplayWindow()
    {
        var guiText = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var nestedFunctionText = Regex.Replace(guiText, @"(?m)^ {4}", string.Empty);
        var fieldValueBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Get-TunaGuiLogLineFieldValue",
            "Test-TunaGuiLogLineHasEvent");
        var hasEventBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiLogLineHasEvent",
            "Test-TunaGuiLogLineFieldMatch");
        var classifierBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiInitialActivationAnswerPeerRecoveryReplayWindowMissing",
            "Test-TunaGuiInitialActivationReceiveRecoveryHoldNotPreserved");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Get-TunaGuiLogLineFieldValue { {{fieldValueBody}} }
            function Test-TunaGuiLogLineHasEvent { {{hasEventBody}} }
            function Test-TunaGuiInitialActivationAnswerPeerRecoveryReplayWindowMissing {
                {{classifierBody}}
            }
            $scope = 'session_id=session-answer; payer_decision_id=4; generation=6; log_writer_id=writer-responder'
            $lines = @(
                "event=runtime_unlock_acceleration_reply_target_decided; $scope; purpose=answer; status=accepted; target_mode=validated_inbound_source",
                "event=tuna_acceleration_answer_sent; $scope; msg_id=answer-1",
                "event=tuna_acceleration_answer_replay_sent; $scope; attempt=16; msg_id=answer-1",
                'event=bridge_ready; session_id=session-answer; log_writer_id=writer-initiator',
                "event=tuna_acceleration_answer_ack_timeout; $scope; trigger=runtime_unlock; retry_reason=runtime_unlock_answer_ack_timeout"
            )
            if (-not (Test-TunaGuiInitialActivationAnswerPeerRecoveryReplayWindowMissing -Lines $lines)) {
                throw 'peer recovery replay-window boundary was not detected'
            }
            $lines = @($lines[0..3]) +
                "event=tuna_acceleration_answer_replay_sent; $scope; attempt=17; msg_id=answer-1" +
                $lines[4]
            if (Test-TunaGuiInitialActivationAnswerPeerRecoveryReplayWindowMissing -Lines $lines) {
                throw 'post-readiness replay did not resolve the replay-window boundary'
            }
            Write-Output 'gui-answer-peer-recovery-replay-window-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(
            result.ExitCode == 0,
            $"GUI answer replay-window test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains(
            "gui-answer-peer-recovery-replay-window-ok",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitialActivationAnswerPeerDeliveryDetectsReceivedAuthorityTupleRejectionBeforeTimeout()
    {
        var guiText = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var nestedFunctionText = Regex.Replace(guiText, @"(?m)^ {4}", string.Empty);
        var fieldValueBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Get-TunaGuiLogLineFieldValue",
            "Test-TunaGuiLogLineHasEvent");
        var hasEventBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiLogLineHasEvent",
            "Test-TunaGuiLogLineFieldMatch");
        var classifierBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiRetiredInitialActivationAnswerAuthorityTupleRejected",
            "Test-TunaGuiInitialActivationAnswerPeerDeliveryMissing");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Get-TunaGuiLogLineFieldValue { {{fieldValueBody}} }
            function Test-TunaGuiLogLineHasEvent { {{hasEventBody}} }
            function Test-TunaGuiRetiredInitialActivationAnswerAuthorityTupleRejected {
                {{classifierBody}}
            }
            $scope = 'session_id=session-answer; payer_decision_id=6; offer_generation=2; log_writer_id=writer-initiator'
            $lines = @(
                "event=tuna_acceleration_pending_runtime_unlock_answer_preserved; $scope; generation=2",
                'event=runtime_unlock_retry_negotiation_slot_decided; session_id=session-answer; decision_status=Reserved; slot_generation=3; log_writer_id=writer-initiator',
                'event=runtime_unlock_retry_dispatch_install_committed; session_id=session-answer; retry_authority_id=retry-3; install_generation=3; install_status=Installed; log_writer_id=writer-initiator',
                'event=runtime_unlock_retry_dispatch_install_confirmed; session_id=session-answer; retry_authority_id=retry-3; install_generation=3; log_writer_id=writer-initiator',
                'event=tuna_acceleration_answer_received_raw; session_id=session-answer; payer_decision_id=6; log_writer_id=writer-initiator',
                "event=runtime_unlock_offer_authority_boundary_decided; boundary=answer_received; authority_mode=InitialActivation; decision_status=rejected; decision_reason=runtime_unlock_offer_authority_tuple_mismatch; $scope",
                'event=tuna_acceleration_answer_ack_timeout; session_id=session-answer; payer_decision_id=6; generation=10; trigger=runtime_unlock; log_writer_id=writer-responder'
            )
            if (-not (Test-TunaGuiRetiredInitialActivationAnswerAuthorityTupleRejected -Lines $lines)) {
                throw 'received answer authority rejection was not detected'
            }
            $lines += "event=runtime_unlock_offer_authority_boundary_decided; boundary=answer_received; authority_mode=InitialActivation; decision_status=accepted; decision_reason=runtime_unlock_offer_authority_current; $scope"
            if (Test-TunaGuiRetiredInitialActivationAnswerAuthorityTupleRejected -Lines $lines) {
                throw 'exact accepted answer boundary did not recover the rejection'
            }
            Write-Output 'gui-received-answer-authority-rejection-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(
            result.ExitCode == 0,
            $"GUI received-answer rejection test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains(
            "gui-received-answer-authority-rejection-ok",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitialActivationAnswerContinuationDetectsSuccessorSlotCollisionAndExactRecovery()
    {
        var guiText = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var nestedFunctionText = Regex.Replace(guiText, @"(?m)^ {4}", string.Empty);
        var fieldValueBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Get-TunaGuiLogLineFieldValue",
            "Test-TunaGuiLogLineHasEvent");
        var hasEventBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiLogLineHasEvent",
            "Test-TunaGuiLogLineFieldMatch");
        var classifierBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Get-TunaGuiInitialActivationAnswerContinuationSlotCollision",
            "Test-TunaGuiRetiredInitialActivationAnswerAuthorityTupleRejected");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Get-TunaGuiLogLineFieldValue { {{fieldValueBody}} }
            function Test-TunaGuiLogLineHasEvent { {{hasEventBody}} }
            function Get-TunaGuiInitialActivationAnswerContinuationSlotCollision {
                {{classifierBody}}
            }
            $scope = 'session_id=session-answer-continuation; transfer_id=transfer-1'
            $lines = @(
                "event=runtime_unlock_offer_authority_boundary_decided; boundary=answer_received; authority_mode=InitialActivation; decision_status=accepted; payer_decision_id=6; offer_generation=2; transaction_generation=1; tuna_path_lease_generation=2; $scope; log_writer_id=writer-initiator",
                "event=runtime_unlock_transaction_answer_received; offer_generation=2; transaction_generation=1; tuna_path_lease_generation=2; $scope; log_writer_id=writer-initiator",
                "event=runtime_unlock_retry_negotiation_slot_decided; intent_kind=SuccessorEvaluation; intent_generation=2; slot_generation=7; decision_status=Reserved; authority_effect=none; $scope; log_writer_id=writer-initiator"
            )
            $result = Get-TunaGuiInitialActivationAnswerContinuationSlotCollision -Lines $lines
            if (-not $result.Unresolved) { throw 'successor slot collision was not detected' }
            $lines += @(
                "event=runtime_unlock_retry_negotiation_slot_decided; intent_kind=SuccessorEvaluation; intent_generation=2; slot_generation=7; decision_status=Released; authority_effect=none; $scope; log_writer_id=writer-initiator",
                "event=tuna_acceleration_answer_ack_sent; payer_decision_id=6; $scope; log_writer_id=writer-initiator",
                "event=runtime_unlock_answer_ack_probe_barrier_released; payer_decision_id=6; $scope; log_writer_id=writer-initiator",
                "event=tuna_acceleration_answer_ack_received_raw; payer_decision_id=6; $scope; log_writer_id=writer-responder",
                "event=runtime_unlock_precommit_responder_peer_probe_handoff_bound; launch_generation=1; $scope; log_writer_id=writer-responder",
                "event=filetransfer_route_transition_prepared; direction=inbound; transition_kind=PostTunaFallbackV6ToTunaV4; $scope; log_writer_id=writer-responder",
                "event=filetransfer_route_transition_draining; direction=inbound; transition_kind=PostTunaFallbackV6ToTunaV4; $scope; log_writer_id=writer-responder"
            )
            $result = Get-TunaGuiInitialActivationAnswerContinuationSlotCollision -Lines $lines
            if ($result.Unresolved) { throw 'exact continuation did not recover slot collision' }
            Write-Output 'gui-answer-continuation-slot-collision-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(
            result.ExitCode == 0,
            $"GUI answer-continuation slot test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains(
            "gui-answer-continuation-slot-collision-ok",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstalledRetryRecoveryJoinDetectsExactPostFallbackMissAndHandlesSmallInputs()
    {
        var guiText = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var nestedFunctionText = Regex.Replace(guiText, @"(?m)^ {4}", string.Empty);
        var fieldValueBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Get-TunaGuiLogLineFieldValue",
            "Test-TunaGuiLogLineHasEvent");
        var hasEventBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiLogLineHasEvent",
            "Test-TunaGuiLogLineFieldMatch");
        var classifierBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Get-TunaGuiInstalledRetryRecoveryJoinFailure",
            "Get-TunaGuiInstalledRetryAnswerContinuationWakeFailure");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Get-TunaGuiLogLineFieldValue { {{fieldValueBody}} }
            function Test-TunaGuiLogLineHasEvent { {{hasEventBody}} }
            function Get-TunaGuiInstalledRetryRecoveryJoinFailure {
                {{classifierBody}}
            }
            $session = 'session_id=session-recovery-join; transfer_id=transfer-1'
            $lines = @(
                "event=runtime_unlock_retry_dispatch_observed; authority_id=retry-1; offer_generation=2; $session; log_writer_id=writer-initiator",
                "event=runtime_unlock_post_fallback_paired_retry_offer_classified; decision_status=Accepted; payer_decision_id=5; $session; log_writer_id=writer-responder",
                'event=tuna_acceleration_answer_sent; accepted=1; payer_decision_id=5; generation=10; log_writer_id=writer-responder',
                "event=filetransfer_bridge_recovery_execution_command_issued; direction=outbound; route=post_tuna_fallback_v6; bridge_decision_generation=2; $session; log_writer_id=writer-initiator",
                "event=filetransfer_bridge_recovery_execution_start_decided; status=started; bridge_decision_generation=2; $session; log_writer_id=writer-initiator",
                "event=nkn_bridge_receive_stall_recovery_rejected_no_filetransfer_authority; purpose=offer_replay; rejection_reason=transport_callback_without_coordinator_authority; $session; log_writer_id=writer-initiator",
                "event=runtime_unlock_offer_authority_boundary_decided; boundary=offer_send_recovery_before_request; authority_mode=InstalledRetry; decision_status=accepted; payer_decision_id=5; transaction_generation=1; offer_generation=2; tuna_path_lease_generation=2; tuna_path_lease_listener_run_id=listener-start-2; retry_authority_id=retry-1; install_generation=1; $session; log_writer_id=writer-initiator",
                'event=tuna_activation_control_send_recovery_requested; purpose=offer_replay; trigger=observed_send_timeout; accepted=0; post_tuna_fallback_offer_recovery=1; session_id=session-recovery-join; log_writer_id=writer-initiator',
                'event=tuna_acceleration_offer_replay_rejected; session_id=session-recovery-join; payer_decision_id=5; generation=2; log_writer_id=writer-initiator'
            )
            $result = Get-TunaGuiInstalledRetryRecoveryJoinFailure -Lines $lines
            if (-not $result.Unresolved) { throw 'exact recovery join miss was not detected' }
            $result = Get-TunaGuiInstalledRetryRecoveryJoinFailure -Lines @()
            if ($result.Unresolved -or $result.ParsedRecords -ne 0) {
                throw 'empty indexed input did not remain safe'
            }
            $result = Get-TunaGuiInstalledRetryRecoveryJoinFailure -Lines @($lines[-1])
            if ($result.Unresolved -or $result.ParsedRecords -ne 1) {
                throw 'singleton indexed input did not remain safe'
            }
            Write-Output 'gui-installed-retry-recovery-join-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(
            result.ExitCode == 0,
            $"GUI installed-retry recovery-join test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains(
            "gui-installed-retry-recovery-join-ok",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostFallbackProbeLaunchDetectsMissingPreparedTransitionAndExactRecovery()
    {
        var guiText = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var nestedFunctionText = Regex.Replace(guiText, @"(?m)^ {4}", string.Empty);
        var fieldValueBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Get-TunaGuiLogLineFieldValue",
            "Test-TunaGuiLogLineHasEvent");
        var hasEventBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiLogLineHasEvent",
            "Test-TunaGuiLogLineFieldMatch");
        var classifierBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Get-TunaGuiPostFallbackProbeLaunchTransitionAuthorityFailure",
            "Get-TunaGuiInitialActivationAnswerHandoffTransferIdentityFailure");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Get-TunaGuiLogLineFieldValue { {{fieldValueBody}} }
            function Test-TunaGuiLogLineHasEvent { {{hasEventBody}} }
            function Get-TunaGuiPostFallbackProbeLaunchTransitionAuthorityFailure {
                {{classifierBody}}
            }
            $tuple = 'session_id=session-probe-transition; transfer_id=transfer-1; direction=outbound; transaction_generation=1; offer_generation=2; log_writer_id=writer-initiator'
            $lines = @(
                "event=filetransfer_runtime_unlock_precommit_probe_launch_handoff_decided; status=Accepted; decision_reason=route_commit_send_drain_staged; drain_generation=0; route=post_tuna_fallback_v6; protocol_version=6; $tuple"
            )
            $result = Get-TunaGuiPostFallbackProbeLaunchTransitionAuthorityFailure -Lines $lines
            if (-not $result.Unresolved) { throw 'zero-generation post-fallback launch was not detected' }
            $transition = 'transition_kind=PostTunaFallbackV6ToTunaV4; transition_generation=7'
            $recovered = @($lines) + @(
                "event=filetransfer_route_transition_prepared; decision_status=Prepared; $transition; $tuple",
                "event=filetransfer_route_transition_pause_bound; decision_status=Installed; $transition; $tuple",
                "event=filetransfer_route_transition_draining; decision_status=Draining; $transition; $tuple",
                "event=filetransfer_route_transition_drain_receipt_decided; decision_status=Accepted; $transition; $tuple",
                "event=filetransfer_route_transition_committed; decision_status=Committed; $transition; $tuple",
                "event=filetransfer_route_transition_disposition_applied; decision_status=Applied; $transition; $tuple"
            )
            $result = Get-TunaGuiPostFallbackProbeLaunchTransitionAuthorityFailure -Lines $recovered
            if ($result.Unresolved) { throw 'exact transition lifecycle did not recover launch' }
            $result = Get-TunaGuiPostFallbackProbeLaunchTransitionAuthorityFailure -Lines @()
            if ($result.Unresolved -or $result.ParsedRecords -ne 0) { throw 'empty input was not safe' }
            $result = Get-TunaGuiPostFallbackProbeLaunchTransitionAuthorityFailure -Lines @($recovered[-1])
            if ($result.Unresolved -or $result.ParsedRecords -ne 1) { throw 'singleton input was not safe' }
            Write-Output 'gui-post-fallback-probe-transition-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(
            result.ExitCode == 0,
            $"GUI post-fallback probe-transition test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains(
            "gui-post-fallback-probe-transition-ok",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstalledRetryAnswerContinuationWakeDetectsPhysicalOwnershipBeforeOriginalAck()
    {
        var guiText = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var nestedFunctionText = Regex.Replace(guiText, @"(?m)^ {4}", string.Empty);
        var fieldValueBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Get-TunaGuiLogLineFieldValue",
            "Test-TunaGuiLogLineHasEvent");
        var hasEventBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiLogLineHasEvent",
            "Test-TunaGuiLogLineFieldMatch");
        var classifierBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Get-TunaGuiInstalledRetryAnswerContinuationWakeFailure",
            "Get-TunaGuiInitialActivationAnswerContinuationSlotCollision");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Get-TunaGuiLogLineFieldValue { {{fieldValueBody}} }
            function Test-TunaGuiLogLineHasEvent { {{hasEventBody}} }
            function Get-TunaGuiInstalledRetryAnswerContinuationWakeFailure {
                {{classifierBody}}
            }
            $scope = 'session_id=session-installed-answer; transfer_id=transfer-1; log_writer_id=writer-initiator'
            $lines = @(
                "event=runtime_unlock_offer_authority_boundary_decided; boundary=answer_received; authority_mode=InstalledRetry; decision_status=accepted; retry_authority_id=retry-1; install_generation=1; payer_decision_id=5; offer_generation=2; transaction_generation=1; tuna_path_lease_generation=2; $scope",
                "event=runtime_unlock_transaction_answer_received; payer_decision_id=5; offer_generation=2; transaction_generation=1; tuna_path_lease_generation=2; $scope",
                "event=tuna_acceleration_runtime_unlock_pending_negotiation_wake_scheduled; authority_mode=InitialActivation; trigger=answer_ack_cleared; log_writer_id=writer-initiator",
                "event=tuna_payer_decision_started; payer_decision_id=6; $scope",
                "event=tuna_path_lease_started; payer_decision_id=6; tuna_path_lease_generation=3; $scope",
                "event=tuna_acceleration_answer_ack_sent; payer_decision_id=5; $scope"
            )
            $result = Get-TunaGuiInstalledRetryAnswerContinuationWakeFailure -Lines $lines
            if (-not $result.Unresolved) { throw 'installed retry answer continuation wake was not detected' }
            $safeLines = @($lines[0], $lines[1], $lines[2], $lines[5])
            $result = Get-TunaGuiInstalledRetryAnswerContinuationWakeFailure -Lines $safeLines
            if ($result.Unresolved) { throw 'wake without physical ownership was not recovered' }
            Write-Output 'gui-installed-retry-answer-continuation-wake-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(
            result.ExitCode == 0,
            $"GUI installed-retry answer continuation test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains(
            "gui-installed-retry-answer-continuation-wake-ok",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstalledRetryPendingAnswerClearedDetectsFailedDialAnswerReset()
    {
        var guiText = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var nestedFunctionText = Regex.Replace(guiText, @"(?m)^ {4}", string.Empty);
        var fieldValueBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Get-TunaGuiLogLineFieldValue",
            "Test-TunaGuiLogLineHasEvent");
        var hasEventBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiLogLineHasEvent",
            "Test-TunaGuiLogLineFieldMatch");
        var classifierBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Get-TunaGuiInstalledRetryPendingAnswerClearedFailure",
            "Get-TunaGuiInitialActivationAnswerContinuationSlotCollision");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Get-TunaGuiLogLineFieldValue { {{fieldValueBody}} }
            function Test-TunaGuiLogLineHasEvent { {{hasEventBody}} }
            function Get-TunaGuiInstalledRetryPendingAnswerClearedFailure {
                {{classifierBody}}
            }
            $session = 'session_id=session-negative-answer; transfer_id=transfer-1'
            $lines = @(
                "event=runtime_unlock_retry_dispatch_observed; authority_id=retry-1; install_generation=1; payer_decision_id=5; offer_generation=2; $session; log_writer_id=writer-initiator",
                "event=runtime_unlock_post_fallback_paired_retry_offer_classified; decision_status=Accepted; payer_decision_id=5; $session; log_writer_id=writer-responder",
                "event=runtime_unlock_tuna_dial_attempt_started; payer_decision_id=5; dial_attempt_generation=9; $session; log_writer_id=writer-responder",
                "event=runtime_unlock_tuna_dial_attempt_failed; payer_decision_id=5; dial_attempt_generation=9; reason=runtime_unlock_tuna_dial_failed; $session; log_writer_id=writer-responder",
                'event=tuna_acceleration_answer_sent; accepted=0; reason=runtime_unlock_tuna_dial_failed; payer_decision_id=5; msg_id=answer-negative-1; log_writer_id=writer-responder',
                'event=tuna_listener_sidecar_event; sidecar_event=tuna_stream_rejected; sidecar_reason=session closed; log_writer_id=writer-initiator',
                'event=tuna_acceleration_state_changed; available=0; reason=listener_stream_rejected_session closed; log_writer_id=writer-initiator',
                'event=tuna_acceleration_runtime_unlock_pending_negotiation_wake_scheduled; authority_mode=InitialActivation; trigger=answer_ack_cleared; log_writer_id=writer-initiator',
                'event=tuna_acceleration_answer_received_raw; msg_id=answer-negative-1; log_writer_id=writer-initiator'
            )
            $result = Get-TunaGuiInstalledRetryPendingAnswerClearedFailure -Lines $lines
            if (-not $result.Unresolved) { throw 'failed-dial answer reset was not detected' }
            $safe = @($lines | Where-Object { $_ -notmatch 'tuna_acceleration_state_changed' })
            $result = Get-TunaGuiInstalledRetryPendingAnswerClearedFailure -Lines $safe
            if ($result.Unresolved) { throw 'stream rejection without listener-down was classified' }
            Write-Output 'gui-installed-retry-pending-answer-cleared-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(
            result.ExitCode == 0,
            $"GUI pending-answer reset test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains(
            "gui-installed-retry-pending-answer-cleared-ok",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitialActivationAnswerHandoffTransferIdentityFailureOutranksDeadline()
    {
        var guiText = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var nestedFunctionText = Regex.Replace(guiText, @"(?m)^ {4}", string.Empty);
        var fieldValueBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Get-TunaGuiLogLineFieldValue",
            "Test-TunaGuiLogLineHasEvent");
        var hasEventBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiLogLineHasEvent",
            "Test-TunaGuiLogLineFieldMatch");
        var classifierMatch = Regex.Match(
            nestedFunctionText,
            @"(?s)function\s+Get-TunaGuiInitialActivationAnswerHandoffTransferIdentityFailure\s*\{(?<body>.*?)\r?\n\}\s*\r?\n\s*\$earlyEvidenceLineVisitCount\b");
        Assert.True(classifierMatch.Success, "Could not find answer-handoff transfer-identity classifier body.");
        var classifierBody = classifierMatch.Groups["body"].Value;
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Get-TunaGuiLogLineFieldValue { {{fieldValueBody}} }
            function Test-TunaGuiLogLineHasEvent { {{hasEventBody}} }
            function Get-TunaGuiInitialActivationAnswerHandoffTransferIdentityFailure {
                {{classifierBody}}
            }
            $scope = 'session_id=session-answer-handoff; transfer_id=transfer-1'
            $lines = @(
                "event=runtime_unlock_offer_authority_boundary_decided; boundary=answer_received; authority_mode=InitialActivation; decision_status=accepted; payer_decision_id=4; offer_generation=1; transaction_generation=1; tuna_path_lease_generation=1; tuna_path_lease_listener_run_id=listener-start-1; $scope; log_writer_id=writer-initiator",
                "event=runtime_unlock_transaction_answer_received; offer_generation=1; transaction_generation=1; tuna_path_lease_generation=1; tuna_path_lease_listener_run_id=listener-start-1; $scope; log_writer_id=writer-initiator",
                "event=runtime_unlock_retired_pending_answer_slot_decided; decision_status=Held; barrier_generation=1; barrier_phase=AwaitingHandoffDisposition; payer_decision_id=4; offer_generation=1; transaction_generation=1; tuna_path_lease_generation=1; tuna_path_lease_listener_run_id=listener-start-1; $scope; log_writer_id=writer-initiator",
                "event=tuna_acceleration_answer_ack_sent; payer_decision_id=4; $scope; log_writer_id=writer-initiator",
                "event=runtime_unlock_answer_ack_probe_barrier_released; payer_decision_id=4; $scope; log_writer_id=writer-initiator",
                "event=runtime_unlock_answer_handoff_boundary_rejected; decision_status=Rejected; decision_reason=answer_continuation_transaction_replaced; expected_transaction_generation=1; expected_offer_generation=1; transaction_generation=1; offer_generation=1; $scope; log_writer_id=writer-initiator; gui_evidence_sequence=6",
                "event=runtime_unlock_retired_pending_answer_slot_decided; decision_status=Released; barrier_generation=1; barrier_phase=AwaitingHandoffDisposition; offer_generation=1; transaction_generation=1; $scope; log_writer_id=writer-initiator"
            )
            $result = Get-TunaGuiInitialActivationAnswerHandoffTransferIdentityFailure -Lines $lines
            if (-not $result.Unresolved -or $result.FirstSequence -ne 6) {
                throw 'answer handoff transfer identity failure was not detected'
            }
            $lines += @(
                "event=runtime_unlock_precommit_probe_launch_requested; offer_generation=1; transaction_generation=1; $scope; log_writer_id=writer-initiator",
                "event=runtime_unlock_precommit_responder_peer_probe_handoff_bound; launch_generation=1; $scope; log_writer_id=writer-responder",
                "event=filetransfer_route_transition_prepared; direction=inbound; transition_kind=PostTunaFallbackV6ToTunaV4; $scope; log_writer_id=writer-responder",
                "event=filetransfer_route_transition_draining; direction=inbound; transition_kind=PostTunaFallbackV6ToTunaV4; $scope; log_writer_id=writer-responder"
            )
            $result = Get-TunaGuiInitialActivationAnswerHandoffTransferIdentityFailure -Lines $lines
            if ($result.Unresolved) { throw 'exact handoff continuation did not recover' }
            Write-Output 'gui-answer-handoff-transfer-identity-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(
            result.ExitCode == 0,
            $"GUI answer-handoff transfer identity test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains(
            "gui-answer-handoff-transfer-identity-ok",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReceiveRecoveryProofStatePublicationRechecksAfterPostDeliveryHeartbeat()
    {
        var guiText = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var nestedFunctionText = Regex.Replace(
            guiText,
            @"(?m)^ {4}",
            string.Empty);
        var fieldValueBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Get-TunaGuiLogLineFieldValue",
            "Test-TunaGuiLogLineHasEvent");
        var hasEventBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiLogLineHasEvent",
            "Test-TunaGuiLogLineFieldMatch");
        var classifierBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiReceiveRecoveryCompletedRetainedProofStatePublicationFailure",
            "Test-TunaGuiReceiveRecoveryProofStateRecheckHeartbeatSuppressionFailure");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Get-TunaGuiLogLineFieldValue { {{fieldValueBody}} }
            function Test-TunaGuiLogLineHasEvent { {{hasEventBody}} }
            function Test-TunaGuiReceiveRecoveryCompletedRetainedProofStatePublicationFailure {
                {{classifierBody}}
            }

            $shared = 'session_id=session-proof-state; transfer_id=transfer-proof-state; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; route_publication_generation=1; runtime_generation=1; terminal_generation=0; leg_generation=1; transport_epoch=0; bridge_recovery_generation=0; data_session_generation=1'
            $inboundScope = "$shared; direction=inbound; log_writer_id=writer-inbound"
            $outboundScope = "$shared; direction=outbound; log_writer_id=writer-outbound"
            $inboundOperation = 'receive_recovery_operation_generation=1; receive_recovery_operation_authority_id=operation-inbound; bridge_decision_generation=1'
            $outboundOperation = 'receive_recovery_operation_generation=1; receive_recovery_operation_authority_id=operation-outbound; bridge_decision_generation=1'
            $lines = @(
                "event=filetransfer_receive_recovery_operation_issued; $outboundOperation; $outboundScope",
                "event=filetransfer_receive_recovery_operation_issued; $inboundOperation; $inboundScope",
                "event=filetransfer_receive_recovery_operation_execution_receipt_applied; $inboundOperation; $inboundScope; execution_outcome=Completed; closure_state=Retained; closure_generation=1; proof_identity=(none)",
                "event=filetransfer_receive_recovery_operation_closure_retained; $inboundOperation; $inboundScope; execution_outcome=Completed; closure_state=Retained; closure_generation=1; proof_identity=(none)",
                'event=bridge_ready; pid=42; session_id=session-proof-state; log_writer_id=writer-inbound',
                'event=session_liveness_proof_received; session_id=session-proof-state; generation=1; sequence=18; proof_kind=heartbeat_received; lane=bulk; log_writer_id=writer-inbound',
                'event=filetransfer_bridge_recovery_lifecycle_diagnostic_only; lifecycle=receivestallrecoveryreceiveresumed; reason=coordinator_operation_receipt_or_proof_required; bridge_decision_generation=1; log_writer_id=writer-inbound',
                "event=filetransfer_receive_recovery_proof_state_publication_decided; session_id=session-proof-state; transfer_id=transfer-proof-state; direction=inbound; recovery_operation_generation=1; recovery_operation_authority_id=operation-inbound; closure_generation=1; publication_generation=1; decision_status=Accepted; log_writer_id=writer-inbound",
                "event=filetransfer_v4_state_sent; $inboundOperation; $inboundScope; closure_generation=1; repair_publication_kind=receive_recovery_proof_state; publication_generation=1; epoch=225; v4_state_wire_fingerprint=wire-state-1",
                'event=filetransfer_receive_recovery_proof_state_publication_receipt_applied; session_id=session-proof-state; transfer_id=transfer-proof-state; direction=inbound; recovery_operation_generation=1; recovery_operation_authority_id=operation-inbound; closure_generation=1; publication_generation=1; v4_state_wire_fingerprint=wire-state-1; publication_outcome=Delivered; decision_status=Accepted; log_writer_id=writer-inbound',
                "event=filetransfer_receive_recovery_operation_execution_receipt_applied; $outboundOperation; $outboundScope; execution_outcome=Completed; closure_state=Retained; closure_generation=1; proof_identity=(none)",
                "event=filetransfer_receive_recovery_operation_closure_retained; $outboundOperation; $outboundScope; execution_outcome=Completed; closure_state=Retained; closure_generation=1; proof_identity=(none)",
                'event=session_liveness_heartbeat_received; session_id=session-proof-state; generation=1; sequence=19; lane=control; log_writer_id=writer-inbound',
                'event=session_liveness_proof_received; session_id=session-proof-state; generation=1; sequence=19; proof_kind=heartbeat_received; lane=control; log_writer_id=writer-inbound'
            )
            if (-not (Test-TunaGuiReceiveRecoveryCompletedRetainedProofStatePublicationFailure -Lines $lines)) {
                throw 'post-delivery heartbeat recheck failure was not classified'
            }

            $naturallyProven = @($lines[0..11])
            $naturallyProven += "event=filetransfer_receive_recovery_operation_proof_observed; $outboundOperation; $outboundScope; closure_generation=1; closure_state=Adopted; proof_identity=file_transfer_v4_state:225"
            $naturallyProven += "event=filetransfer_receive_recovery_operation_closure_adopted; $outboundOperation; $outboundScope; closure_generation=1; closure_state=Adopted; proof_identity=file_transfer_v4_state:225"
            $naturallyProven += $lines[12..13]
            if (Test-TunaGuiReceiveRecoveryCompletedRetainedProofStatePublicationFailure -Lines $naturallyProven) {
                throw 'naturally proven operation was classified as missing a recheck'
            }

            $recovered = @($lines)
            $recovered += 'event=filetransfer_receive_recovery_proof_state_publication_recheck_decided; session_id=session-proof-state; transfer_id=transfer-proof-state; direction=inbound; recovery_operation_generation=1; recovery_operation_authority_id=operation-inbound; closure_generation=1; predecessor_publication_generation=1; predecessor_frame_fingerprint=wire-state-1; heartbeat_generation=1; heartbeat_sequence=19; proof_kind=heartbeat_received; lane=control; publication_generation=2; decision_status=Accepted; log_writer_id=writer-inbound'
            $recovered += "event=filetransfer_v4_state_sent; $inboundOperation; $inboundScope; closure_generation=1; repair_publication_kind=receive_recovery_proof_state; publication_generation=2; receive_recovery_publication_attempt=post_delivery_recheck; receive_recovery_predecessor_publication_generation=1; epoch=226; v4_state_wire_fingerprint=wire-state-2"
            $recovered += 'event=filetransfer_receive_recovery_proof_state_publication_receipt_applied; session_id=session-proof-state; transfer_id=transfer-proof-state; direction=inbound; recovery_operation_generation=1; recovery_operation_authority_id=operation-inbound; closure_generation=1; publication_generation=2; publication_attempt=post_delivery_recheck; predecessor_publication_generation=1; v4_state_wire_fingerprint=wire-state-2; publication_outcome=Delivered; decision_status=Accepted; log_writer_id=writer-inbound'
            $recovered += "event=filetransfer_v4_state_received; $outboundScope; epoch=226; v4_state_wire_fingerprint=wire-state-2; applied=1"
            $recovered += "event=filetransfer_v4_state_received; $outboundScope; epoch=226; v4_state_wire_fingerprint=wire-state-2; applied=1; duplicate=1"
            $recovered += "event=filetransfer_receive_recovery_operation_proof_observed; $outboundOperation; $outboundScope; closure_generation=1; closure_state=Adopted; proof_identity=file_transfer_v4_state:226"
            $recovered += "event=filetransfer_receive_recovery_operation_closure_adopted; $outboundOperation; $outboundScope; closure_generation=1; closure_state=Adopted; proof_identity=file_transfer_v4_state:226"
            $recovered += 'event=filetransfer_regular_v4_physical_send_lease_registered; session_id=session-proof-state; transfer_id=transfer-proof-state; direction=outbound; source_route=regular_nkn_v4_fast; source_protocol_version=4; source_live_route_epoch=0; source_route_publication_generation=1; source_runtime_generation=1; source_terminal_generation=0; source_leg_generation=1; source_transport_epoch=0; source_bridge_recovery_generation=0; source_data_session_generation=1; send_kind=RegularV4Payload; log_writer_id=writer-outbound'
            if (Test-TunaGuiReceiveRecoveryCompletedRetainedProofStatePublicationFailure -Lines $recovered) {
                throw 'exact generation-2 publication and peer receipt did not recover the boundary'
            }

            $missingPeerReceipt = @($recovered | Where-Object {
                $_ -notmatch 'event=filetransfer_v4_state_received'
            })
            if (-not (Test-TunaGuiReceiveRecoveryCompletedRetainedProofStatePublicationFailure -Lines $missingPeerReceipt)) {
                throw 'recovery without the exact peer state receipt was accepted'
            }

            Write-Output 'gui-receive-recovery-proof-state-recheck-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(
            scriptText,
            Array.Empty<string>());

        Assert.True(
            result.ExitCode == 0,
            $"Receive-recovery proof-state GUI classification failed.{Environment.NewLine}" +
            $"{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains(
            "gui-receive-recovery-proof-state-recheck-ok",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReceiveRecoveryProofStatePublicationActivationPauseRequiresHeldResume()
    {
        var guiText = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var nestedFunctionText = Regex.Replace(guiText, @"(?m)^ {4}", string.Empty);
        var fieldValueBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Get-TunaGuiLogLineFieldValue",
            "Test-TunaGuiLogLineHasEvent");
        var hasEventBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiLogLineHasEvent",
            "Test-TunaGuiLogLineFieldMatch");
        var classifierBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiReceiveRecoveryCompletedRetainedProofStatePublicationFailure",
            "Test-TunaGuiReceiveRecoveryProofStateRecheckHeartbeatSuppressionFailure");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Get-TunaGuiLogLineFieldValue { {{fieldValueBody}} }
            function Test-TunaGuiLogLineHasEvent { {{hasEventBody}} }
            function Test-TunaGuiReceiveRecoveryCompletedRetainedProofStatePublicationFailure {
                {{classifierBody}}
            }

            $shared = 'session_id=session-pause; transfer_id=transfer-pause; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; route_publication_generation=1; runtime_generation=1; terminal_generation=0; leg_generation=1; transport_epoch=0; bridge_recovery_generation=0; checkpoint_generation=1; data_session_generation=1'
            $inbound = "$shared; direction=inbound; log_writer_id=writer-inbound"
            $outbound = "$shared; direction=outbound; log_writer_id=writer-outbound"
            $inboundOperation = 'receive_recovery_operation_generation=2; receive_recovery_operation_authority_id=pause-inbound; bridge_decision_generation=2'
            $outboundOperation = 'receive_recovery_operation_generation=2; receive_recovery_operation_authority_id=pause-outbound; bridge_decision_generation=2'
            $rejected = @(
                "event=filetransfer_receive_recovery_operation_issued; $outboundOperation; $outbound",
                "event=filetransfer_receive_recovery_operation_issued; $inboundOperation; $inbound",
                "event=filetransfer_receive_recovery_operation_closure_retained; $inboundOperation; $inbound; closure_generation=2; closure_state=Retained",
                "event=filetransfer_tuna_activation_transport_pause_control_suppressed; $inbound; paused=1; reason=tuna_activation_negotiating",
                "event=filetransfer_receive_recovery_proof_state_publication_decided; $inboundOperation; $inbound; closure_generation=2; publication_generation=1; decision_status=Accepted",
                "event=filetransfer_receive_recovery_proof_state_publication_receipt_applied; $inboundOperation; $inbound; closure_generation=2; publication_generation=1; publication_outcome=Rejected; publication_reason=filetransfer_receive_recovery_proof_state_not_sent; decision_status=Accepted"
            )
            if (-not (Test-TunaGuiReceiveRecoveryCompletedRetainedProofStatePublicationFailure -Lines $rejected)) {
                throw 'activation-pause rejection was not classified'
            }

            $recovered = @($rejected[0..4])
            $recovered += "event=filetransfer_receive_recovery_proof_state_publication_receipt_applied; $inboundOperation; $inbound; closure_generation=2; publication_generation=1; publication_outcome=Held; publication_reason=filetransfer_receive_recovery_proof_state_held_for_tuna_activation_pause; decision_status=Accepted"
            $recovered += "event=filetransfer_tuna_activation_local_barrier_released; $inbound; paused=0; reason=tuna_activation_answer_ack_timeout"
            $recovered += "event=filetransfer_receive_recovery_proof_state_publication_resume_decided; $inboundOperation; $inbound; closure_generation=2; publication_generation=1; decision_status=Accepted"
            $recovered += "event=filetransfer_v4_state_sent; $inboundOperation; $inbound; closure_generation=2; publication_generation=1; repair_publication_kind=receive_recovery_proof_state; epoch=44; v4_state_wire_fingerprint=pause-state-1"
            $recovered += "event=filetransfer_receive_recovery_proof_state_publication_receipt_applied; $inboundOperation; $inbound; closure_generation=2; publication_generation=1; publication_outcome=Delivered; decision_status=Accepted; v4_state_wire_fingerprint=pause-state-1"
            $recovered += "event=filetransfer_v4_state_received; $outbound; epoch=44; v4_state_wire_fingerprint=pause-state-1; applied=1"
            $recovered += "event=filetransfer_receive_recovery_operation_proof_observed; $outboundOperation; $outbound; closure_generation=2; closure_state=Adopted; proof_identity=file_transfer_v4_state:44"
            $recovered += "event=filetransfer_receive_recovery_operation_closure_adopted; $outboundOperation; $outbound; closure_generation=2; closure_state=Adopted; proof_identity=file_transfer_v4_state:44"
            $recovered += "event=filetransfer_bridge_recovery_payload_submission_resumed; $outboundOperation; $outbound; closure_generation=2; closure_state=Adopted; proof_identity=file_transfer_v4_state:44"
            if (Test-TunaGuiReceiveRecoveryCompletedRetainedProofStatePublicationFailure -Lines $recovered) {
                throw 'exact held/resume chain did not recover the activation-pause boundary'
            }

            Write-Output 'gui-proof-state-activation-pause-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(
            result.ExitCode == 0,
            $"Activation-pause proof-state GUI classification failed.{Environment.NewLine}" +
            $"{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-proof-state-activation-pause-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReceiveRecoveryProofStateRecheckHeartbeatSuppressionRequiresPreterminalRecoveryChain()
    {
        var guiText = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var nestedFunctionText = Regex.Replace(guiText, @"(?m)^ {4}", string.Empty);
        var fieldValueBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Get-TunaGuiLogLineFieldValue",
            "Test-TunaGuiLogLineHasEvent");
        var hasEventBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiLogLineHasEvent",
            "Test-TunaGuiLogLineFieldMatch");
        var classifierBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiReceiveRecoveryProofStateRecheckHeartbeatSuppressionFailure",
            "Test-TunaGuiBridgeRecoveryExecutionSubmissionBarrierDrainFailure");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Get-TunaGuiLogLineFieldValue { {{fieldValueBody}} }
            function Test-TunaGuiLogLineHasEvent { {{hasEventBody}} }
            function Test-TunaGuiReceiveRecoveryProofStateRecheckHeartbeatSuppressionFailure {
                {{classifierBody}}
            }

            $scope = 'session_id=session-a; transfer_id=transfer-a; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; route_publication_generation=1; runtime_generation=1; terminal_generation=0; leg_generation=1; transport_epoch=0; bridge_recovery_generation=0; data_session_generation=1'
            $inbound = "$scope; direction=inbound; log_writer_id=writer-inbound"
            $outbound = "$scope; direction=outbound; log_writer_id=writer-outbound"
            $lines = @(
                "event=filetransfer_receive_recovery_proof_state_publication_recheck_armed; $inbound; recovery_operation_generation=1; recovery_operation_authority_id=inbound-operation; closure_generation=1; predecessor_publication_generation=1; predecessor_frame_fingerprint=state-1",
                "event=filetransfer_receive_recovery_operation_closure_retained; $outbound; receive_recovery_operation_generation=1; receive_recovery_operation_authority_id=outbound-operation; closure_generation=1; closure_state=Retained",
                "event=session_liveness_heartbeat_skipped_for_active_filetransfer_progress; $outbound; log_writer_sequence=2772; outbound_progress=1024"
            )
            if (-not (Test-TunaGuiReceiveRecoveryProofStateRecheckHeartbeatSuppressionFailure -Lines $lines)) {
                throw 'heartbeat suppression was not classified'
            }

            $postterminal = @($lines)
            $postterminal += "event=file_transfer_outbound_terminal; $outbound; state=Completed"
            $postterminal += "event=session_liveness_heartbeat_required_for_receive_recovery_proof_state_recheck; $outbound"
            if (-not (Test-TunaGuiReceiveRecoveryProofStateRecheckHeartbeatSuppressionFailure -Lines $postterminal)) {
                throw 'postterminal heartbeat incorrectly recovered the suppression boundary'
            }

            $proofBeforeSkip = @($lines[0], $lines[1])
            $proofBeforeSkip += "event=filetransfer_receive_recovery_operation_proof_observed; $outbound; receive_recovery_operation_generation=1; receive_recovery_operation_authority_id=outbound-operation; closure_generation=1; closure_state=Adopted"
            $proofBeforeSkip += $lines[2]
            if (Test-TunaGuiReceiveRecoveryProofStateRecheckHeartbeatSuppressionFailure -Lines $proofBeforeSkip) {
                throw 'heartbeat skip after exact receive proof was classified as suppression'
            }

            $recovered = @($lines)
            $recovered += "event=session_liveness_heartbeat_required_for_receive_recovery_proof_state_recheck; $outbound"
            $recovered += "event=session_liveness_heartbeat_sent; $outbound; generation=1; sequence=9"
            $recovered += "event=session_liveness_heartbeat_received; $inbound; generation=1; sequence=9"
            $recovered += "event=filetransfer_receive_recovery_proof_state_publication_recheck_decided; $inbound; publication_generation=2; decision_status=Accepted"
            $recovered += "event=filetransfer_v4_state_sent; $inbound; publication_generation=2; repair_publication_kind=receive_recovery_proof_state; v4_state_wire_fingerprint=state-2"
            $recovered += "event=filetransfer_receive_recovery_proof_state_publication_receipt_applied; $inbound; publication_generation=2; publication_outcome=Delivered; v4_state_wire_fingerprint=state-2"
            $recovered += "event=filetransfer_v4_state_received; $outbound; publication_generation=2; v4_state_wire_fingerprint=state-2"
            $recovered += "event=filetransfer_receive_recovery_operation_proof_observed; $outbound; closure_generation=1; closure_state=Adopted"
            $recovered += "event=filetransfer_receive_recovery_operation_closure_adopted; $outbound; closure_generation=1; closure_state=Adopted"
            $recovered += "event=filetransfer_bridge_recovery_payload_submission_resumed; $outbound; closure_generation=1; closure_state=Adopted"
            if (Test-TunaGuiReceiveRecoveryProofStateRecheckHeartbeatSuppressionFailure -Lines $recovered) {
                throw 'exact preterminal recovery chain did not recover the boundary'
            }

            Write-Output 'gui-recovery-heartbeat-suppression-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(
            result.ExitCode == 0,
            $"Recovery-heartbeat suppression GUI classification failed.{Environment.NewLine}" +
            $"{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-recovery-heartbeat-suppression-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RouteTransitionTransportGraceRequiresExactCoordinatorInvalidation()
    {
        var guiText = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var nestedFunctionText = Regex.Replace(
            guiText,
            @"(?m)^ {4}",
            string.Empty);
        var fieldValueBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Get-TunaGuiLogLineFieldValue",
            "Test-TunaGuiLogLineHasEvent");
        var hasEventBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiLogLineHasEvent",
            "Test-TunaGuiLogLineFieldMatch");
        var fieldMatchBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiLogLineFieldMatch",
            "Test-TunaGuiRuntimeUnlockFileTransferOwnerRejectionUnresolved");
        var graceMatch = Regex.Match(
            nestedFunctionText,
            @"(?s)function\s+Test-TunaGuiRouteTransitionDrainingTransportGraceFailure\s*\{" +
            @"(?<body>.*?)\r?\n\}\s*\r?\n\s*\$bridgeBootstrapNotReady");
        Assert.True(
            graceMatch.Success,
            "Could not extract route-transition transport-grace classifier.");
        var graceBody = graceMatch.Groups["body"].Value;
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Get-TunaGuiLogLineFieldValue { {{fieldValueBody}} }
            function Test-TunaGuiLogLineHasEvent { {{hasEventBody}} }
            function Test-TunaGuiLogLineFieldMatch { {{fieldMatchBody}} }
            function Test-TunaGuiRouteTransitionDrainingTransportGraceFailure {
                {{graceBody}}
            }

            $writer = 'writer-route-transition-grace'
            $scope = 'session_id=session-a; transfer_id=transfer-a; direction=outbound'
            $transitionScope = 'transition_kind=RegularV4ToTunaV4; source_route=regular_nkn_v4_fast; source_protocol_version=4; source_transport=regular_nkn; source_live_route_epoch=0; source_runtime_generation=1; source_route_publication_generation=1; source_route_commit_generation=0; source_terminal_generation=0; source_leg_generation=1; source_transport_epoch=0; source_bridge_recovery_generation=0; source_data_session_generation=1; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna'
            $old = 'authority_id=transition-old; transition_generation=1'
            $current = 'authority_id=transition-current; transition_generation=2'
            $lines = @(
                "event=filetransfer_route_transition_prepared; $scope; $old; $transitionScope; decision_status=Prepared; log_writer_id=$writer",
                "event=filetransfer_route_transition_pause_bound; $scope; $old; $transitionScope; decision_status=Installed; log_writer_id=$writer",
                "event=filetransfer_route_transition_draining; $scope; $old; $transitionScope; decision_status=Draining; log_writer_id=$writer",
                "event=filetransfer_route_transition_committed; $scope; $old; $transitionScope; decision_status=Committed; log_writer_id=$writer",
                "event=filetransfer_route_transition_prepared; $scope; $current; $transitionScope; decision_status=Prepared; log_writer_id=$writer",
                "event=filetransfer_route_transition_pause_bound; $scope; $current; $transitionScope; decision_status=Installed; log_writer_id=$writer",
                "event=filetransfer_route_transition_draining; $scope; $current; $transitionScope; decision_status=Draining; log_writer_id=$writer",
                "event=filetransfer_transport_grace_exhausted; $scope; reason=filetransfer_route_transition_draining; log_writer_id=$writer",
                "event=filetransfer_route_transition_invalidated; $scope; $current; $transitionScope; decision_status=Invalidated; decision_reason=outbound_transfer_terminal; log_writer_id=$writer",
                "event=filetransfer_route_transition_disposition_applied; $scope; $current; $transitionScope; disposition=terminalinvalidated; apply_status=applied; log_writer_id=$writer",
                "event=file_transfer_outbound_terminal; session_id=session-a; transfer_id=transfer-a; state=Failed; error_code=peer_disconnected; log_writer_id=$writer"
            )
            if (-not (Test-TunaGuiRouteTransitionDrainingTransportGraceFailure -Lines $lines)) {
                throw 'exact transition-grace terminalization was not classified'
            }

            $mutated = @($lines)
            $mutated[9] = $mutated[9].Replace(
                'source_runtime_generation=1',
                'source_runtime_generation=2')
            if (Test-TunaGuiRouteTransitionDrainingTransportGraceFailure -Lines $mutated) {
                throw 'mutated disposition scope was classified'
            }

            $safe = @($lines[0..7])
            $safe += "event=filetransfer_route_transition_committed; $scope; $current; $transitionScope; decision_status=Committed; log_writer_id=$writer"
            if (Test-TunaGuiRouteTransitionDrainingTransportGraceFailure -Lines $safe) {
                throw 'safe post-ACK commit was classified'
            }

            Write-Output 'gui-route-transition-grace-correlation-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(
            scriptText,
            Array.Empty<string>());

        Assert.True(
            result.ExitCode == 0,
            $"Route-transition grace GUI classification failed.{Environment.NewLine}" +
            $"{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains(
            "gui-route-transition-grace-correlation-ok",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostTunaSubmissionEpochTerminalizationRequiresExactObservedFailureChain()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var nestedFunctionText = Regex.Replace(guiText, @"(?m)^ {4}", string.Empty);
        var fieldValueBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Get-TunaGuiLogLineFieldValue",
            "Test-TunaGuiLogLineHasEvent");
        var hasEventBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiLogLineHasEvent",
            "Test-TunaGuiLogLineFieldMatch");
        var fieldMatchBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiLogLineFieldMatch",
            "Test-TunaGuiRuntimeUnlockFileTransferOwnerRejectionUnresolved");
        var correlationBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Get-TunaGuiPostTunaSubmissionEpochTerminalizationFailure",
            "Test-TunaGuiPostTunaSubmissionEpochTerminalizationFailure");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Get-TunaGuiLogLineFieldValue { {{fieldValueBody}} }
            function Test-TunaGuiLogLineHasEvent { {{hasEventBody}} }
            function Test-TunaGuiLogLineFieldMatch { {{fieldMatchBody}} }
            function Get-TunaGuiPostTunaSubmissionEpochTerminalizationFailure { {{correlationBody}} }

            $writer = 'writer-post-tuna-hold'
            $authority = 'fta2-filetransfer_receive_recovery_operation-post-tuna'
            $scope = 'session_id=session-a; transfer_id=transfer-a; direction=outbound; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; route_publication_generation=3; terminal_generation=0; leg_generation=3; transport_epoch=1; bridge_recovery_generation=1; checkpoint_request_id=(none); data_session_generation=1'
            $lines = @(
                "event=filetransfer_receive_recovery_operation_issued; $scope; receive_recovery_operation_authority_id=$authority; receive_recovery_operation_generation=3; log_writer_id=$writer",
                "event=filetransfer_receive_recovery_operation_execution_start_decided; $scope; receive_recovery_operation_authority_id=$authority; receive_recovery_operation_generation=3; execution_state=Running; log_writer_id=$writer",
                "event=nkn_bridge_recovery_submission_barrier_closed; bridge_recovery_generation=1; submission_epoch=4; recovery_barrier_active=1; log_writer_id=$writer",
                "event=nkn_bridge_recovery_execution_hard_restart_decided; bridge_recovery_generation=1; decision_status=Started; remaining_total_ms=44980; log_writer_id=$writer",
                "event=nkn_bridge_submission_rejected; reason=bridge_submission_epoch_changed; stage=pre_submit; channel=bulk; expected_submission_epoch=3; submission_epoch=4; expected_process_epoch=3; ready_process_epoch=0; recovery_barrier_active=1; log_writer_id=$writer",
                "event=filetransfer_post_tuna_fallback_recovery_submission_failure_observed; $scope; receive_recovery_operation_authority_id=$authority; receive_recovery_operation_generation=3; decision_status=Accepted; typed_rejection_reason=SubmissionEpochChanged; rejection_stage=pre_submit; rejection_channel=bulk; expected_submission_epoch=3; current_submission_epoch=4; expected_process_epoch=3; current_process_epoch=0; recovery_barrier_active=1; log_writer_id=$writer",
                "event=filetransfer_v4_sender_failed; session_id=session-a; transfer_id=transfer-a; log_writer_id=$writer",
                "event=filetransfer_receive_recovery_operation_invalidated; receive_recovery_operation_authority_id=$authority; receive_recovery_operation_generation=3; decision_reason=terminal_state; log_writer_id=$writer"
            )
            if (-not (Get-TunaGuiPostTunaSubmissionEpochTerminalizationFailure -Lines $lines).Unresolved) {
                throw 'exact post-Tuna submission-epoch terminalization was not classified'
            }

            $missingObservation = @($lines[0..4], $lines[6..7])
            if ((Get-TunaGuiPostTunaSubmissionEpochTerminalizationFailure -Lines $missingObservation).Unresolved) {
                throw 'raw barrier rejection without accepted Core observation was classified'
            }

            $wrongWriter = @($lines)
            $wrongWriter[4] = $wrongWriter[4].Replace("log_writer_id=$writer", 'log_writer_id=writer-other')
            if ((Get-TunaGuiPostTunaSubmissionEpochTerminalizationFailure -Lines $wrongWriter).Unresolved) {
                throw 'cross-writer raw rejection was classified'
            }

            $wrongEpoch = @($lines)
            $wrongEpoch[5] = $wrongEpoch[5].Replace('current_submission_epoch=4', 'current_submission_epoch=5')
            if ((Get-TunaGuiPostTunaSubmissionEpochTerminalizationFailure -Lines $wrongEpoch).Unresolved) {
                throw 'mutated submission epoch was classified'
            }

            $released = @($lines[0..5])
            $released += "event=filetransfer_post_tuna_fallback_recovery_submission_failure_finalized; $scope; receive_recovery_operation_authority_id=$authority; receive_recovery_operation_generation=3; decision_status=Accepted; physical_send_lease_generation=2392; log_writer_id=$writer"
            $released += "event=filetransfer_post_tuna_fallback_recovery_submission_failure_deferred; $scope; receive_recovery_operation_authority_id=$authority; receive_recovery_operation_generation=3; decision_status=Accepted; log_writer_id=$writer"
            $released += "event=filetransfer_receive_recovery_operation_execution_receipt_applied; $scope; receive_recovery_operation_authority_id=$authority; receive_recovery_operation_generation=3; execution_state=Completed; proof_identity=proof-a; log_writer_id=$writer"
            $released += "event=filetransfer_outbound_payload_resumed_after_receive_recovery; $scope; recovery_operation_authority_id=$authority; recovery_operation_generation=3; proof_identity=proof-a; log_writer_id=$writer"
            if ((Get-TunaGuiPostTunaSubmissionEpochTerminalizationFailure -Lines $released).Unresolved) {
                throw 'held and resumed recovery without sender failure was classified'
            }

            if ((Get-TunaGuiPostTunaSubmissionEpochTerminalizationFailure -Lines @()).Unresolved) {
                throw 'empty evidence was classified'
            }
            if ((Get-TunaGuiPostTunaSubmissionEpochTerminalizationFailure -Lines @($lines[6])).Unresolved) {
                throw 'singleton sender failure was classified'
            }

            Write-Output 'gui-post-tuna-submission-epoch-terminalization-correlation-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(
            result.ExitCode == 0,
            $"Post-Tuna submission-epoch GUI classification test failed.{Environment.NewLine}" +
            $"{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains(
            "gui-post-tuna-submission-epoch-terminalization-correlation-ok",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task BridgeRecoveryPayloadHoldRequiresExactRepairEscalation()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var nestedFunctionText = Regex.Replace(guiText, @"(?m)^ {4}", string.Empty);
        var fieldValueBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Get-TunaGuiLogLineFieldValue",
            "Test-TunaGuiLogLineHasEvent");
        var hasEventBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiLogLineHasEvent",
            "Test-TunaGuiLogLineFieldMatch");
        var fieldMatchBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiLogLineFieldMatch",
            "Test-TunaGuiRuntimeUnlockFileTransferOwnerRejectionUnresolved");
        var payloadHoldBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiBridgeRecoveryExecutionPayloadHoldFailure",
            "Test-TunaGuiBridgeRecoveryExecutionSubmissionBarrierDrainFailure");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Get-TunaGuiLogLineFieldValue { {{fieldValueBody}} }
            function Test-TunaGuiLogLineHasEvent { {{hasEventBody}} }
            function Test-TunaGuiLogLineFieldMatch { {{fieldMatchBody}} }
            function Test-TunaGuiBridgeRecoveryExecutionPayloadHoldFailure { {{payloadHoldBody}} }

            $writer = 'writer-payload-hold'
            $scope = 'session_id=session-a; transfer_id=transfer-a; direction=outbound; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation=1; terminal_generation=0; leg_generation=1; transport_epoch=0; bridge_recovery_generation=0; bridge_decision_generation=1'
            $sourceScope = 'source_route=regular_nkn_v4_fast; source_protocol_version=4; source_live_route_epoch=0; source_runtime_generation=1; source_terminal_generation=0; source_leg_generation=1; source_transport_epoch=0; source_bridge_recovery_generation=0'
            $authority = 'fta2-repair-replay-payload-hold'
            $repairKey = 'regular_v4_peer_silence_safety_replay:0:0:64:1'
            $lines = @(
                "event=filetransfer_bridge_recovery_execution_command_issued; $scope; recovery_liveness_lease_generation=1; log_writer_id=$writer",
                "event=nkn_bridge_recovery_execution_started; $scope; lease_generation=1; log_writer_id=$writer",
                "event=nkn_bridge_recovery_submission_barrier_closed; $scope; lease_generation=1; submission_epoch=2; recovery_barrier_active=1; log_writer_id=$writer",
                "event=filetransfer_regular_v4_frontier_repair_pressure_binding_committed; $scope; authority_id=$authority; install_generation=1; repair_request_key=$repairKey; log_writer_id=$writer",
                "event=filetransfer_regular_v4_frontier_repair_authority_granted; $scope; authority_id=$authority; install_generation=1; repair_request_key=$repairKey; log_writer_id=$writer",
                "event=nkn_bridge_recovery_execution_hard_restart_decided; $scope; lease_generation=1; decision_status=Started; remaining_total_ms=87503; log_writer_id=$writer",
                "event=filetransfer_regular_v4_physical_send_lease_registered; session_id=session-a; transfer_id=transfer-a; direction=outbound; $sourceScope; send_kind=RegularV4Repair; repair_send=1; repair_request_key=$repairKey; log_writer_id=$writer",
                "event=nkn_bridge_submission_rejected; stage=pre_wait; reason=bridge_submission_epoch_not_ready; channel=control; submission_epoch=2; recovery_barrier_active=1; log_writer_id=$writer",
                "event=nkn_bridge_submission_rejected; stage=pre_wait; reason=bridge_submission_epoch_not_ready; channel=bulk; submission_epoch=2; recovery_barrier_active=1; log_writer_id=$writer",
                "event=filetransfer_v4_repair_delivery_both_failed; session_id=session-a; transfer_id=transfer-a; log_writer_id=$writer",
                "event=filetransfer_v4_sender_failed; session_id=session-a; transfer_id=transfer-a; log_writer_id=$writer"
            )
            if (-not (Test-TunaGuiBridgeRecoveryExecutionPayloadHoldFailure -Lines $lines)) {
                throw 'exact repair payload-hold escalation was not classified'
            }

            $wrongSendKind = @($lines)
            $wrongSendKind[6] = $wrongSendKind[6].Replace(
                'send_kind=RegularV4Repair',
                'send_kind=SessionHeartbeat')
            if (Test-TunaGuiBridgeRecoveryExecutionPayloadHoldFailure -Lines $wrongSendKind) {
                throw 'non-repair payload rejection was classified'
            }

            $releasedBeforeFailure = @(
                $lines[0..9]
                "event=nkn_bridge_recovery_submission_barrier_released; $scope; lease_generation=1; submission_epoch=2; log_writer_id=$writer"
                $lines[10]
            )
            if (Test-TunaGuiBridgeRecoveryExecutionPayloadHoldFailure -Lines $releasedBeforeFailure) {
                throw 'post-release sender failure was classified'
            }

            $heartbeatOnly = @($lines[0..5], $lines[7], $lines[8], $lines[10])
            if (Test-TunaGuiBridgeRecoveryExecutionPayloadHoldFailure -Lines $heartbeatOnly) {
                throw 'unlinked barrier rejection was classified'
            }

            Write-Output 'gui-bridge-recovery-payload-hold-correlation-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(
            result.ExitCode == 0,
            $"Bridge-recovery payload-hold GUI classification test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains(
            "gui-bridge-recovery-payload-hold-correlation-ok",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task BridgeRecoverySubmissionBarrierDrainRequiresExactSourceEpochTimeout()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var nestedFunctionText = Regex.Replace(guiText, @"(?m)^ {4}", string.Empty);
        var fieldValueBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Get-TunaGuiLogLineFieldValue",
            "Test-TunaGuiLogLineHasEvent");
        var hasEventBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiLogLineHasEvent",
            "Test-TunaGuiLogLineFieldMatch");
        var fieldMatchBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiLogLineFieldMatch",
            "Test-TunaGuiRuntimeUnlockFileTransferOwnerRejectionUnresolved");
        var drainBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiBridgeRecoveryExecutionSubmissionBarrierDrainFailure",
            "Test-TunaGuiRegularV4FrontierRepairSettlementClosed");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Get-TunaGuiLogLineFieldValue { {{fieldValueBody}} }
            function Test-TunaGuiLogLineHasEvent { {{hasEventBody}} }
            function Test-TunaGuiLogLineFieldMatch { {{fieldMatchBody}} }
            function Test-TunaGuiBridgeRecoveryExecutionSubmissionBarrierDrainFailure { {{drainBody}} }

            $writer = 'writer-submission-drain'
            $scope = 'session_id=session-a; transfer_id=transfer-a; direction=outbound; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation=1; terminal_generation=0; leg_generation=1; transport_epoch=0; bridge_recovery_generation=0; bridge_decision_generation=1'
            $sourceScope = 'session_id=session-a; transfer_id=transfer-a; direction=outbound; source_route=regular_nkn_v4_fast; source_protocol_version=4; source_live_route_epoch=0; source_runtime_generation=1; source_terminal_generation=0; source_leg_generation=1; source_transport_epoch=0; source_bridge_recovery_generation=0'
            $processScope = 'submission_epoch=2; source_process_epoch=1; source_process_id=4242'
            $lines = @(
                "event=filetransfer_bridge_recovery_execution_command_issued; $scope; recovery_liveness_lease_generation=1; log_writer_id=$writer",
                "event=nkn_bridge_recovery_execution_started; $scope; lease_generation=1; log_writer_id=$writer",
                "event=nkn_bridge_recovery_submission_barrier_closed; $scope; lease_generation=1; $processScope; log_writer_id=$writer",
                "event=filetransfer_bridge_recovery_payload_submission_held; $scope; recovery_execution_state=Running; recovery_liveness_lease_generation=1; log_writer_id=$writer",
                "event=filetransfer_outbound_physical_send_lease_retired; $sourceScope; registered_send_count=144; retired_send_count=144; log_writer_id=$writer",
                "event=nkn_bridge_recovery_submission_barrier_invalidated; $scope; lease_generation=1; $processScope; process_epoch=1; process_id=4242; reason=bridge_recovery_execution_restart_not_ready; log_writer_id=$writer",
                "event=nkn_bridge_recovery_execution_completed; $scope; lease_generation=1; execution_mode=hard; outcome=exhausted; physical_execution_terminated=1; total_deadline_expired=1; reason=bridge_recovery_execution_timeout; log_writer_id=$writer",
                "event=filetransfer_bridge_recovery_execution_receipt_decision_observed; $scope; recovery_liveness_lease_generation=1; execution_outcome=exhausted; accepted=1; disposition=accepted; log_writer_id=$writer"
            )
            if (-not (Test-TunaGuiBridgeRecoveryExecutionSubmissionBarrierDrainFailure -Lines $lines)) {
                throw 'exact source-epoch submission drain timeout was not classified'
            }

            $partialDrain = @($lines)
            $partialDrain[4] = $partialDrain[4].Replace(
                'retired_send_count=144',
                'retired_send_count=143')
            if (Test-TunaGuiBridgeRecoveryExecutionSubmissionBarrierDrainFailure -Lines $partialDrain) {
                throw 'partial Core lease retirement was classified'
            }

            $hardAccepted = @(
                $lines[0..4]
                "event=nkn_bridge_recovery_execution_hard_restart_decided; $scope; lease_generation=1; decision_status=Started; log_writer_id=$writer"
                $lines[5..7]
            )
            if (Test-TunaGuiBridgeRecoveryExecutionSubmissionBarrierDrainFailure -Lines $hardAccepted) {
                throw 'timeout after accepted hard restart entered source-gate class'
            }

            $wrongProcess = @($lines)
            $wrongProcess[5] = $wrongProcess[5].Replace(
                'process_id=4242',
                'process_id=5252')
            if (Test-TunaGuiBridgeRecoveryExecutionSubmissionBarrierDrainFailure -Lines $wrongProcess) {
                throw 'replacement-process invalidation was classified as source-gate timeout'
            }

            Write-Output 'gui-bridge-recovery-submission-drain-correlation-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(
            result.ExitCode == 0,
            $"Bridge-recovery submission-drain GUI classification test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains(
            "gui-bridge-recovery-submission-drain-correlation-ok",
            result.Stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReceiveRecoveryCompletedUnprovenSuccessorRequiresExactUnresolvedWindow()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var nestedFunctionText = Regex.Replace(guiText, @"(?m)^ {4}", string.Empty);
        var fieldValueBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Get-TunaGuiLogLineFieldValue",
            "Test-TunaGuiLogLineHasEvent");
        var hasEventBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiLogLineHasEvent",
            "Test-TunaGuiLogLineFieldMatch");
        var completedUnprovenBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiReceiveRecoveryCompletedUnprovenSuccessorMissing",
            "Test-TunaGuiBridgeRecoveryExecutionPayloadHoldFailure");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Get-TunaGuiLogLineFieldValue { {{fieldValueBody}} }
            function Test-TunaGuiLogLineHasEvent { {{hasEventBody}} }
            function Test-TunaGuiReceiveRecoveryCompletedUnprovenSuccessorMissing {
                {{completedUnprovenBody}}
            }

            $writer = 'writer-completed-unproven'
            $scope = 'session_id=session-a; transfer_id=transfer-a; direction=outbound; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; route_publication_generation=1; route_commit_generation=0; runtime_generation=1; terminal_generation=0; leg_generation=1; transport_epoch=0; bridge_recovery_generation=0; checkpoint_request_id=(none); data_session_identity=data-session-a; data_session_generation=1'
            $operationOne = 'receive_recovery_operation_generation=1; receive_recovery_operation_authority_id=operation-a; bridge_decision_generation=1; recovery_liveness_lease_authority_id=lease-a; recovery_liveness_lease_generation=1'
            $rejectionScope = 'session_id=session-a; transfer_id=transfer-a; direction=outbound; current_route=regular_nkn_v4_fast; current_protocol_version=4; current_live_route_epoch=0; current_runtime_generation=1; current_terminal_generation=0; attempted_route=regular_nkn_v4_fast; attempted_protocol_version=4; attempted_live_route_epoch=0; attempted_runtime_generation=1; attempted_terminal_generation=0; attempted_leg_generation=1; attempted_transport_epoch=0; attempted_bridge_recovery_generation=0'
            $lines = @(
                "event=filetransfer_receive_recovery_operation_issued; $scope; $operationOne; execution_state=Issued; closure_state=None; log_writer_id=$writer",
                "event=filetransfer_receive_recovery_operation_execution_start_decided; $scope; $operationOne; execution_state=Running; execution_start_status=Started; closure_state=None; log_writer_id=$writer",
                "event=filetransfer_receive_recovery_operation_execution_receipt_applied; $scope; $operationOne; execution_state=Completed; execution_start_status=Started; execution_outcome=Completed; proof_identity=(none); proof_observed_utc_ms=0; closure_generation=1; closure_state=Retained; log_writer_id=$writer",
                "event=filetransfer_receive_recovery_operation_closure_retained; $scope; $operationOne; execution_state=Completed; execution_start_status=Started; execution_outcome=Completed; proof_identity=(none); proof_observed_utc_ms=0; closure_generation=1; closure_state=Retained; log_writer_id=$writer",
                "event=nkn_bridge_recovery_evaluation_requested; observation_kind=ping_timeout; observation_generation=2; transfer_id=transfer-a; log_writer_id=$writer",
                "event=filetransfer_recovery_authority_rejected; $rejectionScope; action_kind=ReceiveRecoveryOperation; attempted_action=bridge_ping_timeout; rejection_reason=filetransfer_receive_recovery_operation_conflict; log_writer_id=$writer",
                "event=filetransfer_receive_recovery_evaluation_requested; session_id=session-a; transfer_id=transfer-a; direction=outbound; observation_kind=ping_timeout; observation_generation=2; receive_recovery_operation_issued=0; log_writer_id=$writer"
            )
            if (-not (Test-TunaGuiReceiveRecoveryCompletedUnprovenSuccessorMissing -Lines $lines)) {
                throw 'exact completed-unproven successor gap was not classified'
            }

            $proofBeforeEvaluation = @(
                $lines[0..3]
                "event=filetransfer_receive_recovery_operation_proof_observed; $scope; $operationOne; execution_state=Completed; execution_outcome=Completed; proof_identity=proof-a; proof_observed_utc_ms=123; closure_generation=1; closure_state=Adopted; log_writer_id=$writer"
                "event=filetransfer_receive_recovery_operation_closure_adopted; $scope; $operationOne; execution_state=Completed; execution_outcome=Completed; proof_identity=proof-a; proof_observed_utc_ms=123; closure_generation=1; closure_state=Adopted; log_writer_id=$writer"
                $lines[4..6]
            )
            if (Test-TunaGuiReceiveRecoveryCompletedUnprovenSuccessorMissing -Lines $proofBeforeEvaluation) {
                throw 'proof-adopted closure was classified as completed-unproven'
            }

            $commandIssued = @($lines)
            $commandIssued[6] = $commandIssued[6].Replace(
                'receive_recovery_operation_issued=0',
                'receive_recovery_operation_issued=1')
            if (Test-TunaGuiReceiveRecoveryCompletedUnprovenSuccessorMissing -Lines $commandIssued) {
                throw 'evaluation that issued a command was classified as blocked'
            }

            $operationTwo = 'receive_recovery_operation_generation=2; receive_recovery_operation_authority_id=operation-b; bridge_decision_generation=2; recovery_liveness_lease_authority_id=lease-b; recovery_liveness_lease_generation=2'
            $ambiguousLegacy = @(
                $lines[0..3]
                "event=filetransfer_receive_recovery_operation_issued; $scope; $operationTwo; execution_state=Issued; closure_state=None; log_writer_id=$writer"
                "event=filetransfer_receive_recovery_operation_execution_start_decided; $scope; $operationTwo; execution_state=Running; execution_start_status=Started; closure_state=None; log_writer_id=$writer"
                "event=filetransfer_receive_recovery_operation_execution_receipt_applied; $scope; $operationTwo; execution_state=Completed; execution_start_status=Started; execution_outcome=Completed; proof_identity=(none); proof_observed_utc_ms=0; closure_generation=2; closure_state=Retained; log_writer_id=$writer"
                "event=filetransfer_receive_recovery_operation_closure_retained; $scope; $operationTwo; execution_state=Completed; execution_start_status=Started; execution_outcome=Completed; proof_identity=(none); proof_observed_utc_ms=0; closure_generation=2; closure_state=Retained; log_writer_id=$writer"
                $lines[4..6]
            )
            if (Test-TunaGuiReceiveRecoveryCompletedUnprovenSuccessorMissing -Lines $ambiguousLegacy) {
                throw 'ambiguous legacy same-writer windows were classified'
            }

            $superseded = @(
                $lines
                "event=filetransfer_receive_recovery_operation_superseded; session_id=session-a; transfer_id=transfer-a; direction=outbound; predecessor_receive_recovery_operation_authority_id=operation-a; predecessor_receive_recovery_operation_generation=1; predecessor_closure_generation=1; successor_receive_recovery_operation_authority_id=operation-b; successor_receive_recovery_operation_generation=2; log_writer_id=$writer"
                "event=filetransfer_receive_recovery_operation_issued; $scope; $operationTwo; predecessor_receive_recovery_operation_authority_id=operation-a; predecessor_receive_recovery_operation_generation=1; execution_state=Issued; closure_state=None; log_writer_id=$writer"
                "event=filetransfer_receive_recovery_operation_execution_start_decided; $scope; $operationTwo; execution_state=Running; execution_start_status=Started; closure_state=None; log_writer_id=$writer"
                "event=filetransfer_receive_recovery_operation_execution_receipt_applied; $scope; $operationTwo; execution_state=Completed; execution_start_status=Started; execution_outcome=Completed; proof_identity=(none); proof_observed_utc_ms=0; closure_generation=2; closure_state=Retained; log_writer_id=$writer"
            )
            if (Test-TunaGuiReceiveRecoveryCompletedUnprovenSuccessorMissing -Lines $superseded) {
                throw 'completed successor did not recover predecessor classification'
            }

            Write-Output 'gui-completed-unproven-successor-correlation-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(
            result.ExitCode == 0,
            $"Completed-unproven successor GUI classification test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains(
            "gui-completed-unproven-successor-correlation-ok",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Contains(
            "completed_unproven_receive_recovery_operation_blocked_fresh_recovery",
            guiText,
            StringComparison.Ordinal);

        var drainBranch = guiText.IndexOf(
            "elseif ($bridgeRecoveryExecutionSubmissionBarrierDrainMissing)",
            StringComparison.Ordinal);
        var completedUnprovenBranch = guiText.IndexOf(
            "elseif ($receiveRecoveryCompletedUnprovenSuccessorMissing)",
            StringComparison.Ordinal);
        var genericOwnerBranch = guiText.IndexOf(
            "elseif ($runtimeUnlockFileTransferOwnerMissingBeforePathLease)",
            StringComparison.Ordinal);
        Assert.True(
            drainBranch >= 0 &&
            completedUnprovenBranch > drainBranch &&
            genericOwnerBranch > completedUnprovenBranch,
            "Completed-unproven successor classification must follow earlier bridge barriers and precede generic runtime symptoms.");
    }

    [Fact]
    public async Task CompletedProvenSuccessorAndAtomicStallRequireExactScope()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var nestedFunctionText = Regex.Replace(guiText, @"(?m)^ {4}", string.Empty);
        var fieldValueBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Get-TunaGuiLogLineFieldValue",
            "Test-TunaGuiLogLineHasEvent");
        var hasEventBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiLogLineHasEvent",
            "Test-TunaGuiLogLineFieldMatch");
        var fieldMatchBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiLogLineFieldMatch",
            "Test-TunaGuiRuntimeUnlockFileTransferOwnerRejectionUnresolved");
        var completedProvenBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiReceiveRecoveryCompletedProvenSuccessorMissing",
            "Test-TunaGuiReceiveRecoveryCompletedUnprovenSuccessorMissing");
        var settlementBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiRegularV4FrontierRepairSettlementClosed",
            "Test-TunaGuiRegularV4FrontierRepairStallUnresolved");
        var stallBody = ExtractPowerShellFunctionBody(
            nestedFunctionText,
            "Test-TunaGuiRegularV4FrontierRepairStallUnresolved",
            "Test-TunaGuiRegularV4PredecessorSettlementClosedSuccessor");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Get-TunaGuiLogLineFieldValue { {{fieldValueBody}} }
            function Test-TunaGuiLogLineHasEvent { {{hasEventBody}} }
            function Test-TunaGuiLogLineFieldMatch { {{fieldMatchBody}} }
            function Test-TunaGuiReceiveRecoveryCompletedProvenSuccessorMissing {
                {{completedProvenBody}}
            }
            function Test-TunaGuiRegularV4FrontierRepairSettlementClosed {
                {{settlementBody}}
            }
            function Test-TunaGuiRegularV4FrontierRepairStallUnresolved {
                {{stallBody}}
            }

            $writer = 'writer-proven'
            $scope = 'session_id=session-a; transfer_id=transfer-a; direction=outbound; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; route_publication_generation=1; runtime_generation=1; terminal_generation=0; leg_generation=1; transport_epoch=0; bridge_recovery_generation=0; bridge_decision_generation=1; data_session_generation=1; recovery_liveness_lease_authority_id=lease-a; recovery_liveness_lease_generation=1'
            $operation = 'receive_recovery_operation_generation=1; receive_recovery_operation_authority_id=operation-a'
            $rejection = 'session_id=session-a; transfer_id=transfer-a; direction=outbound; current_route=regular_nkn_v4_fast; current_protocol_version=4; current_live_route_epoch=0; current_runtime_generation=1; current_terminal_generation=0; attempted_leg_generation=1; attempted_transport_epoch=0; attempted_bridge_recovery_generation=0'
            $proven = @(
                "event=filetransfer_receive_recovery_operation_issued; $scope; $operation; execution_state=Issued; log_writer_id=$writer",
                "event=filetransfer_receive_recovery_operation_execution_start_decided; $scope; $operation; execution_state=Running; execution_start_status=Started; log_writer_id=$writer",
                "event=filetransfer_receive_recovery_operation_execution_receipt_applied; $scope; $operation; execution_state=Completed; execution_outcome=Completed; closure_generation=1; closure_state=Retained; log_writer_id=$writer",
                "event=filetransfer_receive_recovery_operation_proof_observed; $scope; $operation; execution_state=Completed; execution_outcome=Completed; proof_identity=proof-a; closure_generation=1; closure_state=Adopted; log_writer_id=$writer",
                "event=filetransfer_receive_recovery_operation_closure_adopted; $scope; $operation; execution_state=Completed; execution_outcome=Completed; proof_identity=proof-a; closure_generation=1; closure_state=Adopted; log_writer_id=$writer",
                "event=nkn_bridge_recovery_evaluation_requested; observation_kind=ping_timeout; observation_generation=2; transfer_id=transfer-a; log_writer_id=$writer",
                "event=filetransfer_recovery_authority_rejected; $rejection; action_kind=ReceiveRecoveryOperation; attempted_action=ping_timeout; rejection_reason=filetransfer_receive_recovery_operation_conflict; log_writer_id=$writer",
                "event=filetransfer_receive_recovery_evaluation_requested; session_id=session-a; transfer_id=transfer-a; direction=outbound; observation_kind=ping_timeout; observation_generation=2; receive_recovery_operation_issued=0; log_writer_id=$writer"
            )
            if (-not (Test-TunaGuiReceiveRecoveryCompletedProvenSuccessorMissing -Lines $proven)) {
                throw 'exact completed-proven successor gap was not classified'
            }
            $reserved = @(
                $proven[0..4]
                "event=filetransfer_receive_recovery_operation_closure_reserved; $scope; $operation; closure_generation=1; closure_state=Reserved; retry_authority_id=retry-a; retry_install_generation=1; log_writer_id=$writer"
                $proven[5..7]
            )
            if (Test-TunaGuiReceiveRecoveryCompletedProvenSuccessorMissing -Lines $reserved) {
                throw 'reserved adopted closure was classified as supersedable'
            }

            $stallScope = 'session_id=session-stall-a; transfer_id=transfer-stall-a; direction=inbound; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation=1; terminal_generation=0; leg_generation=1; transport_epoch=0; bridge_recovery_generation=0; bridge_decision_generation=1; route_publication_generation=1; route_commit_generation=0; target_transport=regular_nkn'
            $stallOperation = 'receive_recovery_operation_generation=1; receive_recovery_operation_authority_id=operation-stall; recovery_liveness_lease_authority_id=lease-stall; recovery_liveness_lease_generation=1'
            $stallLines = @(
                "event=filetransfer_regular_v4_frontier_repair_stalled; $stallScope; repair_request_key=10:2:10:2",
                "event=filetransfer_regular_v4_frontier_repair_pressure_binding_committed; $stallScope; authority_id=repair-a; install_generation=1; repair_request_key=10:2:10:2",
                "event=filetransfer_regular_v4_frontier_repair_pressure_recovery_handoff_prepared; $stallScope; authority_id=repair-a; install_generation=1; repair_request_key=10:2:10:2; source_bridge_decision_generation=1",
                "event=filetransfer_regular_v4_frontier_repair_settlement_accepted; $stallScope; authority_id=repair-a; successor_install_generation=1; repair_request_key=10:2:10:2; outcome=StaleRejected",
                "event=filetransfer_regular_v4_frontier_repair_pressure_settlement_apply_decided; $stallScope; authority_id=[redacted]; install_generation=1; repair_request_key=10:2:10:2; settlement_outcome=StaleRejected; apply_status=Applied; current_authority_cleared=1; retained_predecessor_cleared=0",
                "event=filetransfer_regular_v4_frontier_repair_pressure_recovery_handoff_committed; $stallScope; authority_id=repair-a; install_generation=1; repair_request_key=10:2:10:2; settlement_outcome=StaleRejected; apply_status=Applied; current_authority_cleared=1; retained_predecessor_cleared=0; source_bridge_decision_generation=1; recovery_operation_generation=1; $stallOperation",
                "event=filetransfer_receive_recovery_operation_issued; $stallScope; $stallOperation; execution_state=Issued",
                "event=filetransfer_regular_v4_frontier_repair_contract_settled; authority_id=repair-a; repair_request_key=10:2:10:2; settlement_outcome=StaleRejected",
                "event=filetransfer_receive_recovery_operation_execution_receipt_applied; $stallScope; $stallOperation; execution_state=Completed; execution_outcome=Completed; closure_generation=1; closure_state=Retained",
                "event=filetransfer_receive_recovery_operation_proof_observed; $stallScope; $stallOperation; execution_state=Completed; execution_outcome=Completed; proof_identity=proof-stall; closure_generation=1; closure_state=Adopted",
                "event=filetransfer_receive_recovery_operation_closure_adopted; $stallScope; $stallOperation; execution_state=Completed; execution_outcome=Completed; proof_identity=proof-stall; closure_generation=1; closure_state=Adopted"
            )
            if (Test-TunaGuiRegularV4FrontierRepairStallUnresolved -Lines $stallLines) {
                throw 'exact atomic StaleRejected recovery did not resolve its stall'
            }
            $startupScope = 'session_id=session-startup; transfer_id=transfer-startup; direction=outbound; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation=1; terminal_generation=0; leg_generation=1; transport_epoch=0; bridge_recovery_generation=0; bridge_decision_generation=0; route_publication_generation=1; route_commit_generation=0; target_transport=regular_nkn'
            $startupLines = @(
                "event=filetransfer_regular_v4_frontier_repair_pressure_binding_committed; $startupScope; authority_id=repair-startup; install_generation=1; repair_request_key=42:5:42:5; log_writer_id=writer-startup",
                "event=filetransfer_regular_v4_frontier_repair_authority_granted; $startupScope; authority_id=repair-startup; install_generation=1; repair_request_key=42:5:42:5; frontier_chunk_index=42; durable_received_highest_chunk_index=31; requested_chunk_count=5; scheduled_chunk_count=0; log_writer_id=writer-startup",
                "event=filetransfer_regular_v4_frontier_repair_stalled; $startupScope; repair_request_key=42:5:42:5; reason=no_schedulable_repair_chunks; frontier_chunk_index=42; durable_received_highest_chunk_index=31; requested_chunk_count=5; log_writer_id=writer-startup",
                "event=filetransfer_v4_repair_scheduled; $startupScope; repair_request_key=42:5:42:5; requested_chunk_count=5; scheduled_chunk_count=0; first_start_chunk_index=42; last_end_chunk_exclusive=47; skipped_future_count=5; skipped_obsolete_count=0; skipped_out_of_bounds_count=0; log_writer_id=writer-startup",
                "event=filetransfer_regular_v4_frontier_repair_settlement_accepted; $startupScope; authority_id=repair-startup; successor_authority_id=(none); successor_install_generation=1; repair_request_key=42:5:42:5; frontier_chunk_index=42; durable_received_highest_chunk_index=31; requested_chunk_count=5; outcome=StaleRejected; settlement_reason=tuna_activation_pause_tuna_activation_negotiating; settlement_disposition=accepted; cleared_pressure=1; log_writer_id=writer-startup",
                "event=filetransfer_regular_v4_frontier_repair_pressure_settlement_apply_decided; session_id=session-startup; transfer_id=transfer-startup; direction=outbound; authority_id=[redacted]; successor_authority_id=(none); current_authority_id=[redacted]; retained_predecessor_authority_id=(none); install_generation=1; settlement_outcome=StaleRejected; apply_status=Applied; current_authority_cleared=1; retained_predecessor_cleared=0; log_writer_id=writer-startup",
                "event=filetransfer_regular_v4_frontier_repair_pressure_binding_retired; $startupScope; authority_id=repair-startup; install_generation=1; repair_request_key=42:5:42:5; binding_state=Installed; install_boundary=settlement; log_writer_id=writer-startup"
            )
            if (Test-TunaGuiRegularV4FrontierRepairStallUnresolved -Lines $startupLines) {
                throw 'exact startup future-unsent StaleRejected cleanup did not resolve its stall'
            }
            $scheduled = @($startupLines)
            $scheduled[1] = $scheduled[1].Replace('scheduled_chunk_count=0', 'scheduled_chunk_count=1')
            if (-not (Test-TunaGuiRegularV4FrontierRepairStallUnresolved -Lines $scheduled)) {
                throw 'scheduled startup repair was recovered without proof'
            }
            $badSkippedFuture = @($startupLines)
            $badSkippedFuture[3] = $badSkippedFuture[3].Replace('skipped_future_count=5', 'skipped_future_count=4')
            if (-not (Test-TunaGuiRegularV4FrontierRepairStallUnresolved -Lines $badSkippedFuture)) {
                throw 'partially skipped future range was treated as exact cleanup'
            }
            $wrongWriter = @($startupLines)
            $wrongWriter[1] = $wrongWriter[1].Replace('log_writer_id=writer-startup', 'log_writer_id=writer-other')
            if (-not (Test-TunaGuiRegularV4FrontierRepairStallUnresolved -Lines $wrongWriter)) {
                throw 'cross-writer startup grant recovered a stall'
            }
            $crossScope = @(
                $stallLines
                "event=filetransfer_regular_v4_frontier_repair_stalled; session_id=session-stall-b; transfer_id=transfer-stall-b; direction=inbound; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; leg_generation=1; transport_epoch=0; bridge_recovery_generation=0; repair_request_key=10:2:10:2"
            )
            if (-not (Test-TunaGuiRegularV4FrontierRepairStallUnresolved -Lines $crossScope)) {
                throw 'cross-scope atomic recovery masked an unrelated stall'
            }

            $tunaScope = 'session_id=session-tuna; transfer_id=transfer-tuna; direction=inbound; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation=1; terminal_generation=0; leg_generation=1; transport_epoch=0; bridge_recovery_generation=0; route_publication_generation=1; route_commit_generation=0; log_writer_id=writer-tuna'
            $tunaLines = @(
                "event=filetransfer_regular_v4_frontier_repair_stalled; $tunaScope; repair_request_key=96:12:96:12; frontier_chunk_index=96; requested_chunk_count=12",
                "event=filetransfer_regular_v4_frontier_repair_settlement_accepted; $tunaScope; authority_id=repair-tuna; successor_install_generation=4; repair_request_key=96:12:96:12; outcome=StaleRejected; settlement_reason=tuna_activation_pause_tuna_activation_negotiating",
                'event=filetransfer_regular_v4_frontier_repair_pressure_settlement_apply_decided; session_id=session-tuna; transfer_id=transfer-tuna; direction=inbound; install_generation=4; settlement_outcome=StaleRejected; apply_status=Applied; current_authority_cleared=1; log_writer_id=writer-tuna',
                "event=filetransfer_regular_v4_frontier_repair_pressure_binding_retired; $tunaScope; authority_id=repair-tuna; install_generation=4; repair_request_key=96:12:96:12; install_boundary=settlement",
                'event=filetransfer_route_transition_committed; session_id=session-tuna; transfer_id=transfer-tuna; direction=inbound; transition_kind=RegularV4ToTunaV4; source_route=regular_nkn_v4_fast; source_live_route_epoch=0; source_runtime_generation=1; source_leg_generation=1; source_route_publication_generation=1; target_route=file_tuna_v4; target_live_route_epoch=1; decision_status=Committed; log_writer_id=writer-tuna',
                'event=filetransfer_file_tuna_v4_frontier_repair_progress_observed; session_id=session-tuna; transfer_id=transfer-tuna; direction=inbound; repair_request_key=96:12:96:12; live_route_epoch=1; committed_frontier_before=96; committed_frontier_after=99; log_writer_id=writer-tuna',
                'event=filetransfer_v4_repair_cleared; session_id=session-tuna; transfer_id=transfer-tuna; repair_request_key=96:12:96:12; reason=frontier_advanced; log_writer_id=writer-tuna'
            )
            if (Test-TunaGuiRegularV4FrontierRepairStallUnresolved -Lines $tunaLines) {
                throw 'exact Tuna progress did not recover the Regular-V4 stall'
            }
            $wrongTunaKey = @($tunaLines)
            $wrongTunaKey[5] = $wrongTunaKey[5].Replace('repair_request_key=96:12:96:12', 'repair_request_key=96:11:96:11')
            if (-not (Test-TunaGuiRegularV4FrontierRepairStallUnresolved -Lines $wrongTunaKey)) {
                throw 'wrong-key Tuna progress recovered the Regular-V4 stall'
            }

            Write-Output 'gui-completed-proven-and-atomic-stall-correlation-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(
            result.ExitCode == 0,
            $"Completed-proven/atomic-stall GUI classification test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains(
            "gui-completed-proven-and-atomic-stall-correlation-ok",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Contains(
            "completed_proven_unreserved_receive_recovery_operation_blocked_fresh_recovery",
            guiText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task FallbackAuthorityMatcher_CurrentSchemaRequiresCompleteIdentityAndIsolatesLegacy()
    {
        var matcherPath = Path.Combine(FindRepoRoot(), "tools", "FileTransferAuthorityMatching.ps1")
            .Replace("'", "''", StringComparison.Ordinal);
        var scriptText = $$"""
            . '{{matcherPath}}'

            $current = 'event=grant; authority_schema=2; authority_id=fta-1; predecessor_authority_id=(none); transfer_id=transfer-a; session_id=session-a; direction=outbound; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; runtime_generation=7; terminal_generation=0; leg_generation=5; transport_epoch=3; bridge_recovery_generation=2; bridge_decision_generation=9; checkpoint_request_id=checkpoint-a; route_publication_generation=11; route_commit_generation=0; repair_generation=13; repair_request_key=fallback-frontier:10:2:frng-a; range_fingerprint=frng-a; frontier_chunk_index=10; durable_received_highest_chunk_index=12; range_count=1; requested_chunk_count=2; source_kind=PeerRequest; primary_protocol_repair_request_id=request-primary; primary_request_correlation_id=frq-primary; priority=frontier; recovery_mode=frontier_repair_only; target_transport=regular_nkn; authority_decision_reason=fallback_frontier_request_received'
            $same = $current.Replace('event=grant', 'event=settled')
            if (-not (Test-FileTransferAuthorityTupleMatch -LeftLine $current -RightLine $same)) { exit 10 }

            $immutableFields = @(
                'predecessor_authority_id',
                'live_route_epoch',
                'runtime_generation',
                'terminal_generation',
                'leg_generation',
                'transport_epoch',
                'bridge_recovery_generation',
                'bridge_decision_generation',
                'route_publication_generation',
                'route_commit_generation',
                'repair_generation',
                'frontier_chunk_index',
                'durable_received_highest_chunk_index',
                'range_count',
                'requested_chunk_count',
                'source_kind',
                'primary_protocol_repair_request_id',
                'primary_request_correlation_id',
                'priority',
                'recovery_mode',
                'target_transport',
                'authority_decision_reason')
            foreach ($field in $immutableFields) {
                $mismatched = [regex]::Replace($same, ('(?i)(?:^|;\s*)' + [regex]::Escape($field) + '=)[^;]*'), ('$1' + '999'), 1)
                if (Test-FileTransferAuthorityTupleMatch -LeftLine $current -RightLine $mismatched) { exit 11 }
                $omitted = [regex]::Replace($same, ('(?i);\s*' + [regex]::Escape($field) + '=[^;]*'), '', 1)
                if (Test-FileTransferAuthorityTupleMatch -LeftLine $current -RightLine $omitted) { exit 12 }
            }

            if ((ConvertTo-FileTransferAuthorityNormalizedValue 'none') -ne '(none)') { exit 13 }
            if ((ConvertTo-FileTransferAuthorityNormalizedValue ' (none) ') -ne '(none)') { exit 14 }
            $currentWithoutCheckpoint = $current.Replace('checkpoint_request_id=checkpoint-a', 'checkpoint_request_id=(none)')
            $sameWithoutCheckpoint = $same.Replace('checkpoint_request_id=checkpoint-a', 'checkpoint_request_id=none')
            if (-not (Test-FileTransferAuthorityTupleMatch -LeftLine $currentWithoutCheckpoint -RightLine $sameWithoutCheckpoint)) { exit 18 }
            $omittedCheckpoint = $sameWithoutCheckpoint.Replace('; checkpoint_request_id=none', '')
            if (Test-FileTransferAuthorityTupleMatch -LeftLine $currentWithoutCheckpoint -RightLine $omittedCheckpoint) { exit 19 }
            $legacy = $same.Replace('authority_schema=2; ', '').Replace('authority_id=fta-1; ', '')
            if (Test-FileTransferAuthorityTupleMatch -LeftLine $current -RightLine $legacy) { exit 15 }

            $peerAccepted = 'event=delivery; authority_schema=2; transfer_id=transfer-a; session_id=session-a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=5; transport_epoch=3; bridge_recovery_generation=2; checkpoint_request_id=checkpoint-a; repair_request_id=request-a; repair_request_key=fallback-frontier:10:2:frng-a; range_fingerprint=frng-a; request_correlation_id=frq-a'
            $peerReceived = $peerAccepted.Replace('event=delivery', 'event=received')
            if (-not (Test-FileTransferAuthorityTupleMatch -LeftLine $peerAccepted -RightLine $peerReceived -PeerCorrelation)) { exit 16 }
            if (Test-FileTransferAuthorityTupleMatch -LeftLine $peerAccepted -RightLine ($peerReceived.Replace('request_correlation_id=frq-a', 'request_correlation_id=frq-b')) -PeerCorrelation) { exit 17 }

            Write-Output 'authority-matcher-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(
            result.ExitCode == 0,
            $"Authority matcher failed with exit code {result.ExitCode}.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("authority-matcher-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void RunFileTransferTunaGuiSmoke_PreactivationAcceptFailuresAreClassified()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1");
        var scriptText = File.ReadAllText(scriptPath);
        var authorityMatchingText = File.ReadAllText(Path.Combine(repoRoot, "tools", "FileTransferAuthorityMatching.ps1"));
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
        Assert.Contains("post_tuna_fallback_outbound_receive_worker_handoff_missing", scriptText, StringComparison.Ordinal);
        Assert.Contains("Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing", scriptText, StringComparison.Ordinal);
        Assert.Contains("filetransfer_outbound_sparse_worker_handoff_installed", scriptText, StringComparison.Ordinal);
        Assert.Contains("Test-TunaGuiLatestFallbackFrontierRequestDispatchMissing", scriptText, StringComparison.Ordinal);
        Assert.Contains("Test-TunaGuiFallbackFrontierRequestDispatchTupleMatch", scriptText, StringComparison.Ordinal);
        Assert.Contains("FileTransferAuthorityMatching.ps1", scriptText, StringComparison.Ordinal);
        Assert.Contains("Test-FileTransferAuthorityTupleMatch", scriptText, StringComparison.Ordinal);
        Assert.Contains("'authority_id'", authorityMatchingText, StringComparison.Ordinal);
        Assert.Contains("'live_route_epoch'", authorityMatchingText, StringComparison.Ordinal);
        Assert.Contains("'runtime_generation'", authorityMatchingText, StringComparison.Ordinal);
        Assert.Contains("'terminal_generation'", authorityMatchingText, StringComparison.Ordinal);
        Assert.Contains("'leg_generation'", authorityMatchingText, StringComparison.Ordinal);
        Assert.Contains("'transport_epoch'", authorityMatchingText, StringComparison.Ordinal);
        Assert.Contains("'bridge_recovery_generation'", authorityMatchingText, StringComparison.Ordinal);
        Assert.Contains("'bridge_decision_generation'", authorityMatchingText, StringComparison.Ordinal);
        Assert.Contains("'route_publication_generation'", authorityMatchingText, StringComparison.Ordinal);
        Assert.Contains("'route_commit_generation'", authorityMatchingText, StringComparison.Ordinal);
        Assert.Contains("'repair_generation'", authorityMatchingText, StringComparison.Ordinal);
        Assert.Contains("'repair_request_id'", authorityMatchingText, StringComparison.Ordinal);
        Assert.Contains("'request_correlation_id'", authorityMatchingText, StringComparison.Ordinal);
        Assert.Contains("'checkpoint_request_id'", authorityMatchingText, StringComparison.Ordinal);
        Assert.Contains("'range_fingerprint'", authorityMatchingText, StringComparison.Ordinal);
        Assert.Contains("'primary_protocol_repair_request_id'", authorityMatchingText, StringComparison.Ordinal);
        Assert.Contains("'primary_request_correlation_id'", authorityMatchingText, StringComparison.Ordinal);
        Assert.Contains("'authority_decision_reason'", authorityMatchingText, StringComparison.Ordinal);
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
        Assert.Contains("post_tuna_fallback_outbound_receive_worker_handoff_missing", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("fallback_frontier_repair_stalled", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("runtime_unlock_retry_source_publication_mismatch", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("runtime_unlock_retry_dispatch_authority_tuple_mismatch", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("runtime_unlock_retry_dispatch_successor_missing_after_missing_dispatch", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("runtime_unlock_retry_dispatch_grant_bypassed_by_retry_budget", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("runtime_unlock_retry_dispatch_contract_unsettled", routeAcceptanceText, StringComparison.Ordinal);
        Assert.True(
            routeAcceptanceText.IndexOf("return 'runtime_unlock_retry_source_publication_mismatch'", StringComparison.Ordinal) <
            routeAcceptanceText.IndexOf("return 'regular_v4_frontier_repair_liveness_settlement_missing'", StringComparison.Ordinal));
        Assert.True(
            routeAcceptanceText.IndexOf("return 'runtime_unlock_retry_dispatch_authority_tuple_mismatch'", StringComparison.Ordinal) <
            routeAcceptanceText.IndexOf("return 'regular_v4_frontier_repair_liveness_settlement_missing'", StringComparison.Ordinal));
        Assert.True(
            routeAcceptanceText.IndexOf("return 'runtime_unlock_retry_dispatch_successor_missing_after_missing_dispatch'", StringComparison.Ordinal) <
            routeAcceptanceText.IndexOf("return 'regular_v4_frontier_repair_liveness_settlement_missing'", StringComparison.Ordinal));
        Assert.Contains("Tuna GUI setup failure:", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("regularV4FrontierRepairSettledStalledEvent", scriptText, StringComparison.Ordinal);
        Assert.Contains("regularV4FrontierRepairPrecontractSettlementConsumed", scriptText, StringComparison.Ordinal);
        Assert.Contains("regularV4ReceiveRecoveryClosedNoDeferral", scriptText, StringComparison.Ordinal);
        Assert.Contains("regularV4ReceiveRecoveryRejectedBeforeRuntimeUnlock", scriptText, StringComparison.Ordinal);
        Assert.Contains("regular_v4_receive_recovery_rejected_before_runtime_unlock", scriptText, StringComparison.Ordinal);
        Assert.Contains("Test-TunaGuiRegularV4PredecessorSettlementClosedSuccessor", scriptText, StringComparison.Ordinal);
        Assert.Contains("regularV4RecoveryPredecessorSettlementClosedSuccessor", scriptText, StringComparison.Ordinal);
        Assert.Contains("regular_v4_recovery_predecessor_settlement_closed_successor", scriptText, StringComparison.Ordinal);
        Assert.Contains("regularV4FrontierRepairSettlementClosed", scriptText, StringComparison.Ordinal);
        Assert.Contains("regularV4FrontierRepairSettledStalledUnclosed", scriptText, StringComparison.Ordinal);
        Assert.Contains("runtimeUnlockRetryDispatchMissingAfterAuthorityGranted", scriptText, StringComparison.Ordinal);
        Assert.Contains("runtime_unlock_retry_dispatch_missing_after_authority_granted", scriptText, StringComparison.Ordinal);
        Assert.Contains("Test-TunaGuiRuntimeUnlockMissingDispatchSuccessorUnresolved", scriptText, StringComparison.Ordinal);
        Assert.Contains("Get-TunaGuiRuntimeUnlockUnsettledGrantReason", scriptText, StringComparison.Ordinal);
        Assert.Contains("runtime_unlock_retry_dispatch_grant_bypassed_by_retry_budget", scriptText, StringComparison.Ordinal);
        Assert.Contains("runtime_unlock_retry_dispatch_contract_unsettled", scriptText, StringComparison.Ordinal);
        Assert.Contains("requested_predecessor_authority_id", scriptText, StringComparison.Ordinal);
        Assert.Contains("requested_dispatch_attempt_generation", scriptText, StringComparison.Ordinal);
        Assert.Contains("$terminalDirection", scriptText, StringComparison.Ordinal);
        Assert.Contains("runtime_unlock_retry_dispatch_successor_missing_after_missing_dispatch", scriptText, StringComparison.Ordinal);
        Assert.Contains("runtimeUnlockFileTransferOwnerMissingBeforePathLease", scriptText, StringComparison.Ordinal);
        Assert.Contains("runtime_unlock_filetransfer_owner_missing_before_path_lease", scriptText, StringComparison.Ordinal);
        Assert.Contains("runtimeUnlockFileTransferOwnerStaleBeforeOfferDispatch", scriptText, StringComparison.Ordinal);
        Assert.Contains("runtime_unlock_filetransfer_owner_stale_before_offer_dispatch", scriptText, StringComparison.Ordinal);
        Assert.Contains("Test-TunaGuiRuntimeUnlockFileTransferOwnerRejectionUnresolved", scriptText, StringComparison.Ordinal);
        Assert.Contains("[AllowEmptyCollection()][AllowEmptyString()][string[]]$Lines", scriptText, StringComparison.Ordinal);
        Assert.Contains("filetransfer_regular_v4_frontier_repair_successor_authorized", scriptText, StringComparison.Ordinal);
        Assert.Contains("Test-TunaGuiRegularV4FrontierRepairSettlementClosed", scriptText, StringComparison.Ordinal);
        Assert.Contains("Test-TunaGuiRegularV4FrontierRepairPrecontractClosureMatch", scriptText, StringComparison.Ordinal);
        Assert.Contains("Test-TunaGuiRegularV4FrontierRepairMatch", scriptText, StringComparison.Ordinal);
        Assert.Contains("filetransfer_regular_v4_frontier_repair_settlement_accepted", scriptText, StringComparison.Ordinal);
        Assert.Contains("regular_v4_frontier_repair_liveness_settlement_missing", scriptText, StringComparison.Ordinal);
        Assert.Contains("runtime_unlock_retry_source_publication_mismatch", scriptText, StringComparison.Ordinal);
        Assert.Contains("runtime_unlock_retry_dispatch_authority_tuple_mismatch", scriptText, StringComparison.Ordinal);
        Assert.True(
            scriptText.IndexOf("elseif ($runtimeUnlockRetrySourcePublicationMismatch)", StringComparison.Ordinal) <
            scriptText.IndexOf("elseif ($regularV4FrontierRepairSettledStalledUnclosed)", StringComparison.Ordinal));
        Assert.True(
            scriptText.IndexOf("elseif ($runtimeUnlockRetryDispatchAuthorityTupleMismatch)", StringComparison.Ordinal) <
            scriptText.IndexOf("elseif ($regularV4FrontierRepairSettledStalledUnclosed)", StringComparison.Ordinal));
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
            scriptText.IndexOf("elseif ($postTunaFallbackOutboundReceiveWorkerHandoffMissing)", StringComparison.Ordinal) <
            scriptText.IndexOf("elseif ($fallbackFrontierRequestDispatchMissing)", StringComparison.Ordinal),
            "A missing outbound worker handoff must classify before downstream missing frontier dispatch.");
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
        Assert.Contains("Wait-TunaGuiAuthoritativeTunaFaultAdmission", cycleBody, StringComparison.Ordinal);
        Assert.Contains("'route_publication_generation'", scriptText, StringComparison.Ordinal);
        Assert.Contains("'runtime_generation'", scriptText, StringComparison.Ordinal);
        Assert.Contains("'terminal_generation'", scriptText, StringComparison.Ordinal);
        Assert.Contains("'leg_generation'", scriptText, StringComparison.Ordinal);
        Assert.Contains("RequireZeroRouteCommitGeneration $true", cycleBody, StringComparison.Ordinal);
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
        Assert.Contains("Wait-TunaGuiAuthoritativeFallbackFaultObservation -Tracker $faultAdmissionTracker -AfterBookmark ([int]$faultResult.AdmissionBookmark)", guiText, StringComparison.Ordinal);
        Assert.Contains("$fallbackRouteLine = if ($null -ne $fallbackProjection)", guiText, StringComparison.Ordinal);
        Assert.DoesNotContain("$fallbackRouteLine = [string](Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $stepBookmark -NeedleSets @(", guiText, StringComparison.Ordinal);
        Assert.Contains("@('event=filetransfer_route_selected', 'route=post_tuna_fallback_v6', 'protocol_version=6')", guiText, StringComparison.Ordinal);
        Assert.Contains("$activationProjectionLine = Wait-TunaGuiAuthoritativeTunaFaultAdmission -Tracker $faultAdmissionTracker -AfterBookmark $stepBookmark", guiText, StringComparison.Ordinal);
        Assert.DoesNotContain("$activationRouteLine = [string](Wait-TunaGuiFileTunaV4PublicationAfterBookmark -Bookmark $stepBookmark", guiText, StringComparison.Ordinal);
        Assert.DoesNotContain("$fallbackRouteLine = if ($fallbackStartedLine.IndexOf('route=post_tuna_fallback_v6'", guiText, StringComparison.Ordinal);
        Assert.DoesNotContain("$activationRouteLine = if ($activationStartedLine.IndexOf('route=file_tuna_v4'", guiText, StringComparison.Ordinal);
        Assert.Contains("same-transfer strict live-route epoch cycling", guiText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeWindows_LongLiveRouteWaitUsesRemainingScenarioBudget()
    {
        var repoRoot = FindRepoRoot();
        var guiText = File.ReadAllText(Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1"));
        var remainingTimeoutBody = ExtractPowerShellFunctionBody(
            guiText,
            "Get-GuiSmokeScenarioRemainingTimeoutMs",
            "Wait-Until");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Assert-GuiSmokeScenarioDeadline { throw 'scenario-deadline' }
            function Get-GuiSmokeScenarioRemainingTimeoutMs { {{remainingTimeoutBody}} }

            $script:GuiSmokeScenarioDeadlineUtc = [DateTimeOffset]::MaxValue
            if ((Get-GuiSmokeScenarioRemainingTimeoutMs -DefaultTimeoutMs 240000) -ne 240000) {
                throw 'default timeout was not preserved without a scenario deadline'
            }

            $script:GuiSmokeScenarioDeadlineUtc = [DateTimeOffset]::UtcNow.AddMilliseconds(1200)
            $remaining = Get-GuiSmokeScenarioRemainingTimeoutMs -DefaultTimeoutMs 240000
            if ($remaining -lt 1 -or $remaining -gt 1200) {
                throw "remaining scenario timeout was not bounded: $remaining"
            }

            $script:GuiSmokeScenarioDeadlineUtc = [DateTimeOffset]::UtcNow.AddMilliseconds(-1)
            try {
                [void](Get-GuiSmokeScenarioRemainingTimeoutMs -DefaultTimeoutMs 240000)
                throw 'expired scenario deadline was accepted'
            }
            catch {
                if ($_.Exception.Message -ne 'scenario-deadline') { throw }
            }
            Write-Output 'scenario-budget-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(result.ExitCode == 0, $"Scenario budget test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("scenario-budget-ok", result.Stdout, StringComparison.Ordinal);
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
        Assert.Contains("function Get-GuiSmokeScenarioRemainingTimeoutMs", guiText, StringComparison.Ordinal);
        Assert.Contains("Get-GuiSmokeScenarioRemainingTimeoutMs -DefaultTimeoutMs 240000", guiText, StringComparison.Ordinal);
        Assert.DoesNotContain("$activationProofTimeoutMs = 240000", guiText, StringComparison.Ordinal);
        Assert.Contains("$regularActivationCycleCapBytes = [Math]::Max(1L, [long]($PayloadSizeBytes / 4))", guiText, StringComparison.Ordinal);
        Assert.Contains("AfterLiveRouteEpoch", guiText, StringComparison.Ordinal);
        Assert.Contains("[string]$AfterLine = ''", guiText, StringComparison.Ordinal);
        Assert.Contains("-AfterLine $AfterLine", guiText, StringComparison.Ordinal);
        Assert.Contains("$lastObservedLiveRouteEpoch", guiText, StringComparison.Ordinal);
        Assert.Contains("FallbackBookmark", guiText, StringComparison.Ordinal);
        Assert.Contains("-FallbackBookmark $bookmark", guiText, StringComparison.Ordinal);
        Assert.Contains("Wait-TunaGuiAuthoritativeTunaFaultAdmission -Tracker $faultAdmissionTracker -AfterBookmark $stepBookmark", guiText, StringComparison.Ordinal);
        Assert.DoesNotContain("-AfterLine $activationStartedLine", guiText, StringComparison.Ordinal);
        Assert.Contains("-AfterLine $fallbackStartedLine", guiText, StringComparison.Ordinal);
        Assert.Contains("final_off_after_reactivation", guiText, StringComparison.Ordinal);
        Assert.Contains("regular_activation_cycle_off_after_live_tuna_epoch", guiText, StringComparison.Ordinal);
        Assert.Contains("file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", guiText, StringComparison.Ordinal);
        Assert.DoesNotContain("$terminalBeforeFallbackAllowedSequences", guiText, StringComparison.Ordinal);
        Assert.DoesNotContain("$terminalBeforeFallbackSequenceAllowed", guiText, StringComparison.Ordinal);
        Assert.Contains("$terminalBeforeFallbackExpectedRoutes = @($joinedRouteSequence.Split(',', [System.StringSplitOptions]::RemoveEmptyEntries))", guiText, StringComparison.Ordinal);
        Assert.Contains("fallbackEvidenceObserved", guiText, StringComparison.Ordinal);
        Assert.Contains("failure_class=filetransfer_gui_live_fallback_fault_window_missed", guiText, StringComparison.Ordinal);
        Assert.Contains("regular-to-Tuna and fallback/reactivation strict live-route epoch cycling", guiText, StringComparison.Ordinal);
        Assert.Contains("immediate_after_fallback_recovered=1", guiText, StringComparison.Ordinal);
        Assert.Contains("prearmed_on_step_{0}_immediate_after_fallback_recovered", guiText, StringComparison.Ordinal);
        Assert.Contains("Try-PrearmTunaGuiSwitchOffToggle", guiText, StringComparison.Ordinal);
        Assert.Contains("filetransfer_tuna_gui_fault_window", guiText, StringComparison.Ordinal);
        Assert.Contains("fresh header switch-off runtime evidence", guiText, StringComparison.Ordinal);
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
        Assert.Contains("filetransfer-tuna-gui-milestone-evidence.log", wrapperText, StringComparison.Ordinal);
        Assert.Contains("faultActionClaimed = [bool]$faultWindow.faultActionClaimed", wrapperText, StringComparison.Ordinal);
        Assert.Contains("faultInjected = [bool]$faultWindow.faultInjected", wrapperText, StringComparison.Ordinal);
        Assert.Contains("faultInjectedBeforeTerminal = [bool]$faultWindow.faultInjectedBeforeTerminal", wrapperText, StringComparison.Ordinal);
        Assert.Contains("terminalCompletedBeforeFault = [bool]$faultWindow.terminalCompletedBeforeFault", wrapperText, StringComparison.Ordinal);
        Assert.Contains("faultInjected = $faultInjectedBeforeTerminal", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("fault_injected_before_terminal={0}", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("post_tuna_fallback_v6,file_tuna_v4", guiText, StringComparison.Ordinal);
        Assert.Contains("Second transfer after live reactivation failed route/integrity check", guiText, StringComparison.Ordinal);
    }

    [Fact]
    public void RunFileTransferTunaGuiSmoke_RetainedTimeoutIsSemanticallyUnavailableNotSetup()
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
            Assert.Contains("$acceptanceFailureClass = 'incomplete_proof'", scriptText, StringComparison.Ordinal);
            Assert.Contains("$operatorVerdict = 'INCONCLUSIVE'", scriptText, StringComparison.Ordinal);
            Assert.Contains("semantic_failure_domain = 'unavailable'", scriptText, StringComparison.Ordinal);
            Assert.Contains("retained_analysis_completed = 0", scriptText, StringComparison.Ordinal);
            Assert.Contains("retained_analysis_status = 'timed_out'", scriptText, StringComparison.Ordinal);
            Assert.DoesNotContain("if ($terminalSuccessTimeout)", scriptText, StringComparison.Ordinal);
            Assert.DoesNotContain("$acceptanceFailureClass = if ($terminalSuccessWithProof) { 'none' } else { 'setup' }", scriptText, StringComparison.Ordinal);
            Assert.DoesNotContain("$operatorVerdict = if ($terminalSuccessWithProof) { 'PASS' } else { 'INVALID_SETUP' }", scriptText, StringComparison.Ordinal);
        }

        Assert.Contains("$guiSummaryRaw", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("\"completed\"\\s*:\\s*(true|false)", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("\"integrityOk\"\\s*:\\s*(true|false)", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("Test-RouteAcceptanceSemanticAnalysisUnavailableFailure", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("$result.semanticFailureDomain = 'unavailable'", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("Tuna GUI semantic analysis unavailable", routeAcceptanceText, StringComparison.Ordinal);
    }

    [Fact]
    public void RouteAcceptance_CheckpointsEvidenceBeforeSemanticAssertionsAndEnrichment()
    {
        var repoRoot = FindRepoRoot();
        var wrapperText = File.ReadAllText(Path.Combine(repoRoot, "tools", "Run-FileTransferTunaGuiSmoke.ps1"));
        var routeAcceptanceText = File.ReadAllText(Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"));
        var scenarioBody = ExtractPowerShellFunctionBody(
            routeAcceptanceText,
            "Invoke-Phase4RouteAcceptanceScenario",
            "New-RouteAcceptanceSummaryRunDto");
        var completionBody = ExtractPowerShellFunctionBody(
            routeAcceptanceText,
            "Complete-RouteAcceptanceRunEvidence",
            "Checkpoint-RouteAcceptanceRunEvidence");

        var firstAttemptRegistration = scenarioBody.IndexOf("Add-RouteAcceptanceScenarioAttemptEvidence", StringComparison.Ordinal);
        var firstAttemptSelection = scenarioBody.IndexOf("Select-RouteAcceptanceScenarioAttemptEvidence", StringComparison.Ordinal);
        var firstCheckpoint = scenarioBody.IndexOf("Checkpoint-RouteAcceptanceRunEvidence", StringComparison.Ordinal);
        var firstAssertion = scenarioBody.IndexOf("$result = Assert-Phase4ScenarioRun", StringComparison.Ordinal);
        Assert.True(firstAttemptRegistration >= 0 && firstAttemptRegistration < firstAssertion);
        Assert.True(firstAttemptSelection >= 0 && firstAttemptSelection < firstAssertion);
        Assert.True(firstCheckpoint >= 0 && firstCheckpoint < firstAssertion);

        var rerunAssertion = scenarioBody.IndexOf("$rerunResult = Assert-Phase4ScenarioRun", StringComparison.Ordinal);
        var rerunCheckpoint = scenarioBody.LastIndexOf("Checkpoint-RouteAcceptanceRunEvidence", rerunAssertion, StringComparison.Ordinal);
        Assert.True(rerunCheckpoint >= 0 && rerunCheckpoint < rerunAssertion);
        Assert.Contains("Set-RouteAcceptanceEvidenceManifest", completionBody, StringComparison.Ordinal);
        Assert.Contains("$script:RouteAcceptanceSelectedScenarioAttempts", completionBody, StringComparison.Ordinal);
        Assert.Contains("TrySelect-RouteAcceptanceScenarioAttemptEvidence", scenarioBody, StringComparison.Ordinal);
        Assert.DoesNotContain("-not $script:RouteAcceptanceSelectedScenarioAttempts.ContainsKey", scenarioBody, StringComparison.Ordinal);
        Assert.Contains("Get-RouteAcceptanceAttemptFinalization", completionBody, StringComparison.Ordinal);
        Assert.Contains("-AcceptanceRunId $script:RouteAcceptanceRunId", completionBody, StringComparison.Ordinal);
        Assert.Contains("-ScenarioRunId ([string]$attempt.ScenarioRunId)", completionBody, StringComparison.Ordinal);
        Assert.Contains("$selected = $artifactFinalized -and", completionBody, StringComparison.Ordinal);
        var attemptProjectionStart = completionBody.IndexOf("$scenarioEvidence = @(", StringComparison.Ordinal);
        var attemptProjectionEnd = completionBody.IndexOf("$finalGitState =", attemptProjectionStart, StringComparison.Ordinal);
        Assert.True(attemptProjectionStart >= 0 && attemptProjectionEnd > attemptProjectionStart);
        Assert.DoesNotContain("$script:RunResults.ToArray()", completionBody[attemptProjectionStart..attemptProjectionEnd], StringComparison.Ordinal);
        Assert.Contains("Get-RouteAcceptanceDirectAttemptManifestPath", completionBody, StringComparison.Ordinal);
        Assert.Contains("Get-RouteAcceptanceEvidenceSha256 -Path $directManifestPath", completionBody, StringComparison.Ordinal);

        var wrapperMain = wrapperText[wrapperText.IndexOf("$guiSmokeResult = Invoke-TunaGuiSmokeWithHardDeadline", StringComparison.Ordinal)..];
        var minimalSummaryCheckpoint = wrapperMain.IndexOf("$summaryMissingFromChild", StringComparison.Ordinal);
        var directManifestCheckpoint = wrapperMain.IndexOf("Write-TunaGuiDirectEvidenceManifest", StringComparison.Ordinal);
        var retainedAnalysis = wrapperMain.IndexOf("Invoke-TunaGuiMeasuredFallbackRetainedAnalysisBestEffort", StringComparison.Ordinal);
        Assert.True(minimalSummaryCheckpoint >= 0 && minimalSummaryCheckpoint < retainedAnalysis);
        Assert.True(directManifestCheckpoint >= 0 && directManifestCheckpoint < retainedAnalysis);
        Assert.Contains("$summaryCurrentValid", wrapperMain, StringComparison.Ordinal);
        Assert.DoesNotContain("$summaryMissingAfterGuiChild", wrapperMain, StringComparison.Ordinal);
        Assert.Contains("guiExitCodeAvailable", wrapperText, StringComparison.Ordinal);
        Assert.Contains("retainedAnalysisCompleted", wrapperText, StringComparison.Ordinal);
        Assert.Contains("retainedAnalysisStatus", wrapperText, StringComparison.Ordinal);
    }

    [Fact]
    public void TunaGuiDirectManifest_ValidatesFullCaptureAndCompactAnalyzerInputIndependently()
    {
        var wrapperText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "Run-FileTransferTunaGuiSmoke.ps1"));
        var functionStart = wrapperText.IndexOf(
            "function Write-TunaGuiDirectEvidenceManifest",
            StringComparison.Ordinal);
        var functionEnd = wrapperText.IndexOf(
            "$repoRoot =",
            functionStart,
            StringComparison.Ordinal);
        Assert.True(functionStart >= 0 && functionEnd > functionStart);
        var manifestBody = wrapperText[functionStart..functionEnd];

        Assert.Contains("$ledger.retainedSlice.sha256", manifestBody, StringComparison.Ordinal);
        Assert.Contains("$ledger.analyzerInput.captureSha256", manifestBody, StringComparison.Ordinal);
        Assert.Contains("$captureMatches -and", manifestBody, StringComparison.Ordinal);
        Assert.Contains("$analyzerInputMatches", manifestBody, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$captureSha, $ledger.analyzerInput.captureSha256",
            manifestBody,
            StringComparison.Ordinal);
        Assert.Contains("filetransfer-retained-log-slice-derived-receipt.json", manifestBody, StringComparison.Ordinal);
        Assert.Contains("sourceAnalyzerInputSha256", manifestBody, StringComparison.Ordinal);
        Assert.Contains("derivedAnalyzerInputSha256", manifestBody, StringComparison.Ordinal);
        Assert.Contains("milestoneSha256", manifestBody, StringComparison.Ordinal);
        Assert.Contains("$retainedAnalysisCompleted", manifestBody, StringComparison.Ordinal);
        Assert.Contains("-not $sourceDirty", manifestBody, StringComparison.Ordinal);
        var sourceDirtyCapture = manifestBody.IndexOf("$sourceDirty =", StringComparison.Ordinal);
        var proofComputation = manifestBody.IndexOf("$currentRuntimeProof =", StringComparison.Ordinal);
        Assert.True(sourceDirtyCapture >= 0 && proofComputation > sourceDirtyCapture);
    }

    [Fact]
    public async Task TunaGuiMilestoneMerge_WritesExactDerivedAnalyzerInputReceipt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var wrapperText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "Run-FileTransferTunaGuiSmoke.ps1"));
        var atomicJsonBody = ExtractPowerShellFunctionBody(wrapperText, "Write-TunaGuiAtomicJson", "Read-TunaGuiReportValue");
        var sortKeyBody = ExtractPowerShellFunctionBody(wrapperText, "Get-TunaGuiRetainedLogLineSortKey", "Sort-TunaGuiRetainedLogLinesChronologically");
        var sortBody = ExtractPowerShellFunctionBody(wrapperText, "Sort-TunaGuiRetainedLogLinesChronologically", "Get-TunaGuiStrictUtf8LineCount");
        var lineCountBody = ExtractPowerShellFunctionBody(wrapperText, "Get-TunaGuiStrictUtf8LineCount", "Write-TunaGuiDerivedAnalyzerInputReceipt");
        var receiptBody = ExtractPowerShellFunctionBody(wrapperText, "Write-TunaGuiDerivedAnalyzerInputReceipt", "Merge-TunaGuiMilestoneEvidenceIntoRetainedLogSlice");
        var mergeBody = ExtractPowerShellFunctionBody(wrapperText, "Merge-TunaGuiMilestoneEvidenceIntoRetainedLogSlice", "Ensure-TunaGuiSecondTransferRetainedLogSlice");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
            function Get-FileHash {
                param([string]$LiteralPath, [string]$Algorithm)
                $sha = [Security.Cryptography.SHA256]::Create()
                try {
                    $hash = ([BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($LiteralPath))) -replace '-', '')
                    return [pscustomobject]@{ Hash = $hash }
                }
                finally { $sha.Dispose() }
            }
            function Write-TunaGuiAtomicJson { {{atomicJsonBody}} }
            function Get-TunaGuiRetainedLogLineSortKey { {{sortKeyBody}} }
            function Sort-TunaGuiRetainedLogLinesChronologically { {{sortBody}} }
            function Get-TunaGuiStrictUtf8LineCount { {{lineCountBody}} }
            function Write-TunaGuiDerivedAnalyzerInputReceipt { {{receiptBody}} }
            function Merge-TunaGuiMilestoneEvidenceIntoRetainedLogSlice { {{mergeBody}} }

            $root = Join-Path ([IO.Path]::GetTempPath()) ('derived-input-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($root)
            try {
                $utf8 = [Text.UTF8Encoding]::new($false)
                $input = Join-Path $root 'filetransfer-retained-log-slice.log'
                $milestone = Join-Path $root 'filetransfer-tuna-gui-milestone-evidence.log'
                $sourceLine = '[2026-08-13 10:00:00Z] event=runtime_unlock_retry_dispatch_observed'
                $milestoneLine = '[2026-08-13 10:00:01Z] event=filetransfer_route_selected; route=file_tuna_v4'
                [IO.File]::WriteAllText($input, $sourceLine + [Environment]::NewLine, $utf8)
                [IO.File]::WriteAllText($milestone, $milestoneLine + [Environment]::NewLine, $utf8)
                $sourceSha = (Get-FileHash -LiteralPath $input -Algorithm SHA256).Hash.ToLowerInvariant()
                [IO.File]::WriteAllText(
                    (Join-Path $root 'filetransfer-gui-log-ledger.json'),
                    (@{ analyzerInput = @{ path = $input; captureSha256 = $sourceSha; captureLineCount = 1 } } | ConvertTo-Json -Depth 8),
                    $utf8)

                Merge-TunaGuiMilestoneEvidenceIntoRetainedLogSlice -ArtifactDir $root
                $receipt = Get-Content -Raw (Join-Path $root 'filetransfer-retained-log-slice-derived-receipt.json') | ConvertFrom-Json
                $derivedSha = (Get-FileHash -LiteralPath $input -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($receipt.sourceAnalyzerInputSha256 -ne $sourceSha -or
                    $receipt.derivedAnalyzerInputSha256 -ne $derivedSha -or
                    [long]$receipt.derivedAnalyzerInputLineCount -ne 2 -or
                    $receipt.milestoneSha256 -ne (Get-FileHash -LiteralPath $milestone -Algorithm SHA256).Hash.ToLowerInvariant()) {
                    throw 'derived analyzer input receipt did not bind exact source, milestones, and result'
                }
            }
            finally {
                if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
            }
            Write-Output 'derived-input-receipt-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());
        Assert.True(result.ExitCode == 0, $"Derived-input receipt test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("derived-input-receipt-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RouteAcceptanceAttemptFinalization_RequiresExactDirectClaimsAndIdentity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var routeAcceptanceText = File.ReadAllText(Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"));
        var directPathBody = ExtractPowerShellFunctionBody(
            routeAcceptanceText,
            "Get-RouteAcceptanceDirectAttemptManifestPath",
            "Get-RouteAcceptanceAttemptFinalization");
        var finalizationBody = ExtractPowerShellFunctionBody(
            routeAcceptanceText,
            "Get-RouteAcceptanceAttemptFinalization",
            "Test-RouteAcceptanceAttemptFinalized");
        var finalizedBody = ExtractPowerShellFunctionBody(
            routeAcceptanceText,
            "Test-RouteAcceptanceAttemptFinalized",
            "TrySelect-RouteAcceptanceScenarioAttemptEvidence");
        var trySelectBody = ExtractPowerShellFunctionBody(
            routeAcceptanceText,
            "TrySelect-RouteAcceptanceScenarioAttemptEvidence",
            "Test-Phase4RerunnableMeasurementFailure");
        var scriptText = $$"""
            Set-StrictMode -Version Latest
            $ErrorActionPreference = 'Stop'
            function Test-RouteAcceptanceEvidenceFinalizationTimedOut {
                param([string]$ArtifactDir)
                return Test-Path -LiteralPath (Join-Path $ArtifactDir 'filetransfer-gui-evidence-finalization-timeout.json') -PathType Leaf
            }
            function Get-RouteAcceptanceDirectAttemptManifestPath {
            {{directPathBody}}
            }
            function Get-RouteAcceptanceAttemptFinalization {
            {{finalizationBody}}
            }
            function Test-RouteAcceptanceAttemptFinalized {
            {{finalizedBody}}
            }
            $script:selectedCount = 0
            function Select-RouteAcceptanceScenarioAttemptEvidence {
                param([string]$ScenarioName, [int]$AttemptIndex, [string]$ArtifactDir)
                $script:selectedCount++
            }
            function TrySelect-RouteAcceptanceScenarioAttemptEvidence {
            {{trySelectBody}}
            }

            function Assert-Finalized {
                param([bool]$Expected, [string]$Message)
                $actual = Test-RouteAcceptanceAttemptFinalized -ArtifactDir $tempRoot -AcceptanceRunId $runId -ScenarioRunId $scenarioRunId
                if ($actual -ne $Expected) { throw "$Message expected=$Expected actual=$actual" }
            }

            function Write-DirectManifest {
                param($ArtifactFinalized, $CurrentRuntimeProof, [string]$AcceptanceRunId = $runId, [string]$ScenarioRunId = $scenarioRunId, [string]$Name = 'filetransfer-route-attempt-evidence-manifest.json')
                [ordered]@{
                    acceptanceRunId = $AcceptanceRunId
                    scenarioRunId = $ScenarioRunId
                    artifactFinalized = $ArtifactFinalized
                    currentRuntimeProof = $CurrentRuntimeProof
                } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $tempRoot $Name) -Encoding UTF8
            }

            $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('nlink-direct-finalization-' + [guid]::NewGuid().ToString('N'))
            $runId = 'run-identity'
            $scenarioRunId = 'run-identity:scenario:1'
            New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
            try {
                Write-DirectManifest -ArtifactFinalized $true -CurrentRuntimeProof $true
                Assert-Finalized $true 'native true claims with exact identity must finalize'

                New-Item -ItemType File -Force -Path (Join-Path $tempRoot 'filetransfer-gui-log-ledger.json') | Out-Null
                New-Item -ItemType File -Force -Path (Join-Path $tempRoot 'filetransfer-retained-log-slice.log') | Out-Null
                New-Item -ItemType File -Force -Path (Join-Path $tempRoot 'filetransfer-retained-analysis-stage-diagnostics.json') | Out-Null
                Write-DirectManifest -ArtifactFinalized $false -CurrentRuntimeProof $true
                Assert-Finalized $false 'false artifact claim must fail despite analyzer diagnostics'
                $script:selectedCount = 0
                $selected = TrySelect-RouteAcceptanceScenarioAttemptEvidence -ScenarioName 'scenario' -AttemptIndex 1 -ArtifactDir $tempRoot -AcceptanceRunId $runId -ScenarioRunId $scenarioRunId
                if ($selected -or $script:selectedCount -ne 0) { throw 'false direct claim was selected' }

                Write-DirectManifest -ArtifactFinalized $true -CurrentRuntimeProof $false
                Assert-Finalized $false 'false current-runtime claim must fail'
                Write-DirectManifest -ArtifactFinalized 'true' -CurrentRuntimeProof $true
                Assert-Finalized $false 'string artifact claim must fail'
                Write-DirectManifest -ArtifactFinalized $true -CurrentRuntimeProof 'true'
                Assert-Finalized $false 'string current-runtime claim must fail'
                Write-DirectManifest -ArtifactFinalized $true -CurrentRuntimeProof $true -AcceptanceRunId 'wrong-run'
                Assert-Finalized $false 'acceptance identity mismatch must fail'
                Write-DirectManifest -ArtifactFinalized $true -CurrentRuntimeProof $true -ScenarioRunId 'wrong-scenario'
                Assert-Finalized $false 'scenario identity mismatch must fail'

                Write-DirectManifest -ArtifactFinalized $true -CurrentRuntimeProof $true
                Write-DirectManifest -ArtifactFinalized $false -CurrentRuntimeProof $false -Name 'filetransfer-tuna-gui-evidence-manifest.json'
                Assert-Finalized $false 'false Tuna wrapper must override a true generic manifest'
                Remove-Item -LiteralPath (Join-Path $tempRoot 'filetransfer-tuna-gui-evidence-manifest.json') -Force
                Assert-Finalized $true 'generic direct manifest must resume after Tuna wrapper removal'
                $script:selectedCount = 0
                $selected = TrySelect-RouteAcceptanceScenarioAttemptEvidence -ScenarioName 'scenario' -AttemptIndex 1 -ArtifactDir $tempRoot -AcceptanceRunId $runId -ScenarioRunId $scenarioRunId
                if (-not $selected -or $script:selectedCount -ne 1) { throw 'valid direct claim was not selected exactly once' }
                Write-Output 'direct-attempt-finalization-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(result.ExitCode == 0, $"Direct attempt finalization test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("direct-attempt-finalization-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RouteAcceptanceRetainedMarkerIndex_StreamsOnceCachesAndInvalidates()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var routeAcceptanceText = File.ReadAllText(Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"));
        var markerIndexBody = ExtractPowerShellFunctionBody(
            routeAcceptanceText,
            "Get-RouteAcceptanceRetainedMarkerIndex",
            "Assert-RouteAcceptanceRegularNknTransportEvidence");
        var manifestBindingIndex = markerIndexBody.IndexOf("analyzerInputSha256", StringComparison.Ordinal);
        var fallbackHashIndex = markerIndexBody.IndexOf("[System.Security.Cryptography.SHA256]::Create()", StringComparison.Ordinal);
        Assert.True(manifestBindingIndex >= 0 && fallbackHashIndex > manifestBindingIndex);
        Assert.Contains("[System.IO.FileStream]::new", markerIndexBody, StringComparison.Ordinal);
        Assert.Contains("[System.IO.StreamReader]::new", markerIndexBody, StringComparison.Ordinal);
        Assert.DoesNotContain("lastWriteTicks", markerIndexBody, StringComparison.Ordinal);
        var scriptText = $$"""
            Set-StrictMode -Version Latest
            $ErrorActionPreference = 'Stop'
            $script:RouteAcceptanceRetainedMarkerIndexCache = @{}
            function Get-RouteAcceptanceRetainedMarkerIndex {
            {{markerIndexBody}}
            }

            $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('nlink-marker-index-' + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
            try {
                $path = Join-Path $tempRoot 'filetransfer-retained-log-slice.log'
                $lines = [System.Collections.Generic.List[string]]::new()
                $lines.Add('Active transport selected: NKN')
                $lines.Add('event=nkn_bridge_receive_stall_detected')
                for ($i = 0; $i -lt 149; $i++) { $lines.Add("event=filler_$i") }
                $lines.Add('event=filetransfer_route_selected; route=file_tuna_v4')
                $lines.Add('event=nkn_bridge_receive_stall_recovery_started')
                $lines.Add('event=transfer_terminal; route=file_tuna_v4')
                $lines.Add('event=session_liveness_timeout; reason=no_progress')
                $lines.Add('event=session_liveness_timeout_deferred; reason=recovery')
                $lines.Add('event=filetransfer_route_selected; route=file_tuna_v6')
                $lines | Set-Content -LiteralPath $path -Encoding UTF8

                $first = Get-RouteAcceptanceRetainedMarkerIndex -LogPath $path
                $second = Get-RouteAcceptanceRetainedMarkerIndex -LogPath $path
                if (-not [object]::ReferenceEquals($first, $second)) { throw 'cache did not return the indexed object' }
                if ([long]$first.lineVisitCount -ne $lines.Count) { throw "line visits mismatch: $($first.lineVisitCount)" }
                if ([long]$first.measuredWindowBridgeRecoveryCount -ne 2) { throw "measured recovery count mismatch: $($first.measuredWindowBridgeRecoveryCount)" }
                if ([long]$first.sessionLivenessTimeoutCount -ne 1) { throw "liveness token boundary mismatch: $($first.sessionLivenessTimeoutCount)" }
                if (-not $first.hasNknTransport -or -not $first.hasFileTunaV6 -or -not $first.hasForbiddenV6Route) { throw 'required marker missing' }

                $originalLength = (Get-Item -LiteralPath $path).Length
                $originalLastWriteUtc = (Get-Item -LiteralPath $path).LastWriteTimeUtc
                $sameLengthText = [System.IO.File]::ReadAllText($path).Replace('route=file_tuna_v6', 'route=file_tuna_v4')
                [System.IO.File]::WriteAllText($path, $sameLengthText, [System.Text.UTF8Encoding]::new($true))
                if ((Get-Item -LiteralPath $path).Length -ne $originalLength) { throw 'same-length mutation changed file length' }
                [System.IO.File]::SetLastWriteTimeUtc($path, $originalLastWriteUtc)
                $third = Get-RouteAcceptanceRetainedMarkerIndex -LogPath $path
                if ([object]::ReferenceEquals($first, $third)) { throw 'cache did not invalidate after same-length same-mtime mutation' }
                if ([long]$third.lineVisitCount -ne $lines.Count) { throw "same-length rescan line visits mismatch: $($third.lineVisitCount)" }
                if ($third.hasFileTunaV6 -or $third.hasForbiddenV6Route) { throw 'same-length mutation retained stale v6 markers' }
                $thirdAgain = Get-RouteAcceptanceRetainedMarkerIndex -LogPath $path
                if (-not [object]::ReferenceEquals($third, $thirdAgain)) { throw 'SHA cache did not reuse unchanged index' }

                Add-Content -LiteralPath $path -Value 'event=session_liveness_timeout; reason=appended' -Encoding UTF8
                $fourth = Get-RouteAcceptanceRetainedMarkerIndex -LogPath $path
                if ([object]::ReferenceEquals($third, $fourth)) { throw 'cache did not invalidate after append' }
                if ([long]$fourth.lineVisitCount -ne ($lines.Count + 1)) { throw "rescan line visits mismatch: $($fourth.lineVisitCount)" }
                if ([long]$fourth.sessionLivenessTimeoutCount -ne 2) { throw "appended liveness count mismatch: $($fourth.sessionLivenessTimeoutCount)" }
                Write-Output 'retained-marker-index-ok'
            }
            finally {
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(result.ExitCode == 0, $"Retained marker index test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("retained-marker-index-ok", result.Stdout, StringComparison.Ordinal);
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
    public void GuiSmokeWindows_TerminalEvidenceProjectionIsIncrementalAndReusedBySummary()
    {
        var repoRoot = FindRepoRoot();
        var scriptText = File.ReadAllText(Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1"));
        var trackerBody = ExtractPowerShellFunctionBody(
            scriptText,
            "Update-TunaGuiTerminalEvidenceTracker",
            "Get-TopLevelWindowElementsByProcessId");
        var waitBody = ExtractPowerShellFunctionBody(
            scriptText,
            "Wait-FileTransferTerminalPairAfterBookmark",
            "Append-FileTransferLiveCycleArtifact");
        var summaryBody = ExtractPowerShellFunctionBody(
            scriptText,
            "Get-TunaGuiEvidenceSummary",
            "Invoke-FileTransferTunaPostFallbackPrecondition");
        var cycleBody = ExtractPowerShellFunctionBody(
            scriptText,
            "Invoke-FileTransferTunaHandoffFallbackCycle",
            "Wait-FileTransferTerminalOrProgressBeforeAction");

        Assert.Contains("Invoke-GuiSmokeLogLedgerRangeVisitor", trackerBody, StringComparison.Ordinal);
        Assert.Contains("MaximumBatchLineCount = 4096", trackerBody, StringComparison.Ordinal);
        Assert.Contains("$Tracker.Cursor = $lineIndex + 1", trackerBody, StringComparison.Ordinal);
        Assert.Contains("Assert-GuiSmokeScenarioDeadline", trackerBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-AppLogLinesAfterBookmark", waitBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-AppLogLinesAfterBookmark", summaryBody, StringComparison.Ordinal);
        Assert.Contains("$EvidenceTracker.SummaryLines.ToArray()", summaryBody, StringComparison.Ordinal);
        Assert.Contains("$terminalEvidenceTracker = New-TunaGuiTerminalEvidenceTracker", cycleBody, StringComparison.Ordinal);
        Assert.Contains("-EvidenceTracker $terminalEvidenceTracker", cycleBody, StringComparison.Ordinal);
        Assert.Contains(
            "filetransfer_tuna_gui_terminal_evidence_projection_completed",
            waitBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeWindows_TerminalEvidenceProjectionVisitsProductionWidthLedgerOnlyOnce()
    {
        var scriptText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var relevantBody = ExtractPowerShellFunctionBody(
            scriptText,
            "Test-TunaGuiEvidenceSummaryRelevantLine",
            "New-TunaGuiTerminalEvidenceTracker");
        var newTrackerBody = ExtractPowerShellFunctionBody(
            scriptText,
            "New-TunaGuiTerminalEvidenceTracker",
            "Assert-TunaGuiTerminalEvidenceTrackerScope");
        var updateBody = ExtractPowerShellFunctionBody(
            scriptText,
            "Update-TunaGuiTerminalEvidenceTracker",
            "Get-TopLevelWindowElementsByProcessId");
        var powerShell = $$"""
            $ErrorActionPreference = 'Stop'
            Set-StrictMode -Version Latest
            $script:GuiSmokeTerminalEvidenceGeneration = 0L
            $script:GuiSmokeAppLogLedger = [pscustomobject]@{}
            $script:lines = [System.Collections.Generic.List[string]]::new(250002)
            for ($i = 0; $i -lt 250000; $i++) { $script:lines.Add('event=noise; value=1') }
            $script:lines.Add('event=file_transfer_inbound_terminal; transfer_id=t; direction=inbound; role=helper; state=Completed; error_code=(none); saved_path=(none)')
            $script:lines.Add('event=file_transfer_outbound_terminal; transfer_id=t; direction=outbound; role=helpee; state=Completed; error_code=(none)')
            function Assert-GuiSmokeScenarioDeadline {}
            function Sync-GuiSmokeAppLogLedger {}
            function Get-GuiSmokeLogLedgerBookmark { return $script:lines.Count }
            function Invoke-GuiSmokeLogLedgerRangeVisitor {
                param($State, [int]$StartIndex, [int]$EndIndex, [scriptblock]$Visitor)
                for ($lineIndex = $StartIndex; $lineIndex -lt $EndIndex; $lineIndex++) {
                    $result = & $Visitor $script:lines[$lineIndex] $lineIndex
                    if ($null -ne $result) { return $result }
                }
            }
            function ConvertFrom-GuiSmokeSemicolonFields {
                param([string]$Message)
                $fields = @{}
                foreach ($part in @($Message -split ';')) {
                    $pair = $part.Trim() -split '=', 2
                    if ($pair.Count -eq 2) { $fields[$pair[0]] = $pair[1] }
                }
                return $fields
            }
            function Get-GuiSmokeFieldValue {
                param($Fields, [string]$Name, [string]$Default = '')
                if ($null -ne $Fields -and $Fields.ContainsKey($Name)) { return [string]$Fields[$Name] }
                return $Default
            }
            function Get-GuiSmokeFileTransferTerminalDirection {
                param([string]$EventName, $Fields)
                if ($EventName -eq 'file_transfer_inbound_terminal') { return 'inbound' }
                if ($EventName -eq 'file_transfer_outbound_terminal') { return 'outbound' }
                return [string](Get-GuiSmokeFieldValue -Fields $Fields -Name 'direction')
            }
            function Set-GuiSmokeFileTransferTerminalDefaults { param($Fields) }
            function Get-FileTransferLiveProgressScore { return 0L }
            function Get-GuiSmokeInt64FieldValue { return 0L }
            function Test-TunaGuiPeerVisibleFileTunaV4DataPathLine { return $false }
            function Test-TunaGuiEvidenceSummaryRelevantLine { {{relevantBody}} }
            function New-TunaGuiTerminalEvidenceTracker { {{newTrackerBody}} }
            function Update-TunaGuiTerminalEvidenceTracker { {{updateBody}} }
            $tracker = New-TunaGuiTerminalEvidenceTracker -Bookmark 0 -ExpectedInboundRole helper -ExpectedOutboundRole helpee
            $stopwatch = [Diagnostics.Stopwatch]::StartNew()
            [void](Update-TunaGuiTerminalEvidenceTracker -Tracker $tracker)
            [void](Update-TunaGuiTerminalEvidenceTracker -Tracker $tracker)
            $stopwatch.Stop()
            if ([long]$tracker.LineVisitCount -ne 250002L) { throw "line replay detected: $($tracker.LineVisitCount)" }
            if ([int]$tracker.Cursor -ne 250002) { throw "cursor mismatch: $($tracker.Cursor)" }
            if ([int]$tracker.MaxBatchLineCount -gt 4096) { throw "batch exceeded bound: $($tracker.MaxBatchLineCount)" }
            if ($tracker.ByTransfer.Count -ne 1) { throw "terminal transfer missing: $($tracker.ByTransfer.Count)" }
            if ($stopwatch.ElapsedMilliseconds -ge 30000) { throw "projection exceeded 30 seconds: $($stopwatch.ElapsedMilliseconds)" }
            Write-Output "terminal-evidence-production-width-ok elapsed_ms=$($stopwatch.ElapsedMilliseconds)"
            """;

        var result = await RunPowerShellScriptTextAsync(powerShell, Array.Empty<string>());
        Assert.True(
            result.ExitCode == 0,
            $"Production-width terminal evidence projection failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("terminal-evidence-production-width-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuiSmokeWindows_TerminalEvidenceProjectionPreservesSummarySemanticsWithoutReplay()
    {
        var scriptText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var relevantBody = ExtractPowerShellFunctionBody(scriptText, "Test-TunaGuiEvidenceSummaryRelevantLine", "New-TunaGuiTerminalEvidenceTracker");
        var newTrackerBody = ExtractPowerShellFunctionBody(scriptText, "New-TunaGuiTerminalEvidenceTracker", "Assert-TunaGuiTerminalEvidenceTrackerScope");
        var updateBody = ExtractPowerShellFunctionBody(scriptText, "Update-TunaGuiTerminalEvidenceTracker", "Get-TopLevelWindowElementsByProcessId");
        var summaryBody = ExtractPowerShellFunctionBody(scriptText, "Get-TunaGuiEvidenceSummary", "Invoke-FileTransferTunaPostFallbackPrecondition");
        var powerShell = $$"""
            $ErrorActionPreference = 'Stop'
            Set-StrictMode -Version Latest
            $script:GuiSmokeTerminalEvidenceGeneration = 0L
            $script:GuiSmokeAppLogLedger = [pscustomobject]@{}
            $script:lines = @(
                'event=tuna_acceleration_negotiated',
                'event=runtime_unlock_transaction_answer_received; peer_visible_proof=1; answer_received=1; tuna_path_lease_current=1; tuna_path_lease_state=listenerready',
                'event=filetransfer_route_selected; route=file_tuna_v4',
                'event=filetransfer_binary_frame_sent; raw_chunk_bytes=10',
                'event=filetransfer_binary_frame_received; raw_chunk_bytes=11',
                'event=filetransfer_v6_sparse_write_committed; written_bytes=12',
                'event=filetransfer_tuna_data_path_peer_visible; payload_bytes=13',
                'event=filetransfer_v6_sender_started',
                'event=file_transfer_inbound_terminal; transfer_id=t; direction=inbound; state=Completed; error_code=(none)',
                'event=file_transfer_outbound_terminal; transfer_id=t; direction=outbound; state=Completed; error_code=(none)',
                'event=peer_disconnected',
                'event=transport_state_changed; to=Failed')
            function Assert-GuiSmokeScenarioDeadline {}
            function Sync-GuiSmokeAppLogLedger {}
            function Get-GuiSmokeLogLedgerBookmark { return $script:lines.Count }
            function Invoke-GuiSmokeLogLedgerRangeVisitor {
                param($State, [int]$StartIndex, [int]$EndIndex, [scriptblock]$Visitor)
                for ($lineIndex = $StartIndex; $lineIndex -lt $EndIndex; $lineIndex++) {
                    $result = & $Visitor $script:lines[$lineIndex] $lineIndex
                    if ($null -ne $result) { return $result }
                }
            }
            function ConvertFrom-GuiSmokeSemicolonFields {
                param([string]$Message)
                $fields = @{}
                foreach ($part in @($Message -split ';')) {
                    $pair = $part.Trim() -split '=', 2
                    if ($pair.Count -eq 2) { $fields[$pair[0]] = $pair[1] }
                }
                return $fields
            }
            function Get-GuiSmokeFieldValue {
                param($Fields, [string]$Name, [string]$Default = '')
                if ($null -ne $Fields -and $Fields.ContainsKey($Name)) { return [string]$Fields[$Name] }
                return $Default
            }
            function Get-GuiSmokeInt64FieldValue {
                param($Fields, [string]$Name)
                $value = Get-GuiSmokeFieldValue -Fields $Fields -Name $Name -Default '0'
                return [long]$value
            }
            function Get-GuiSmokeFileTransferTerminalDirection {
                param([string]$EventName, $Fields)
                if ($EventName -eq 'file_transfer_inbound_terminal') { return 'inbound' }
                if ($EventName -eq 'file_transfer_outbound_terminal') { return 'outbound' }
                return [string](Get-GuiSmokeFieldValue -Fields $Fields -Name 'direction')
            }
            function Set-GuiSmokeFileTransferTerminalDefaults { param($Fields) }
            function Get-FileTransferLiveProgressScore { return 0L }
            function Test-TunaGuiPeerVisibleFileTunaV4DataPathLine { return $true }
            function Test-TunaGuiAuthorizedFileTunaV4PublicationLine { param([string]$Line) return $Line -like '*event=filetransfer_route_selected*' }
            function Test-TunaGuiEvidenceSummaryRelevantLine { {{relevantBody}} }
            function New-TunaGuiTerminalEvidenceTracker { {{newTrackerBody}} }
            function Update-TunaGuiTerminalEvidenceTracker { {{updateBody}} }
            function Get-TunaGuiEvidenceSummary { {{summaryBody}} }
            $tracker = New-TunaGuiTerminalEvidenceTracker -Bookmark 0
            [void](Update-TunaGuiTerminalEvidenceTracker -Tracker $tracker)
            $visits = [long]$tracker.LineVisitCount
            $summary = Get-TunaGuiEvidenceSummary -Bookmark 0 -EvidenceTracker $tracker
            if ([long]$tracker.LineVisitCount -ne $visits) { throw 'summary replayed consumed ledger lines' }
            if (-not $summary.tunaNegotiated -or -not $summary.diagnosticTunaReadinessObserved -or -not $summary.fileTunaV4PublicationObserved) { throw 'route summary flags diverged' }
            if (-not $summary.terminalPairCompleted -or [long]$summary.senderChunkBytes -ne 10L -or [long]$summary.receiverChunkBytes -ne 23L) { throw 'terminal or byte summary diverged' }
            if (-not $summary.peerVisibleTunaPayloadObserved -or [int]$summary.tunaAcceleratedFileFrameCount -ne 1 -or [long]$summary.tunaAcceleratedFilePayloadBytes -ne 13L) { throw 'Tuna payload summary diverged' }
            if ([int]$summary.peerDisconnectedAfterTerminalCount -ne 1 -or [int]$summary.transportFailedAfterTerminalCount -ne 1) { throw 'post-terminal summary ordering diverged' }
            Write-Output 'terminal-evidence-summary-parity-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(powerShell, Array.Empty<string>());
        Assert.True(
            result.ExitCode == 0,
            $"Terminal evidence summary parity failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("terminal-evidence-summary-parity-ok", result.Stdout, StringComparison.Ordinal);
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
    public void GuiSmokeWindows_LiveTunaReactivationServicesWalletPromptBeforeRouteWait()
    {
        var repoRoot = FindRepoRoot();
        var scriptText = File.ReadAllText(Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1"));
        var unlockFunctionBody = ExtractPowerShellFunctionBody(scriptText, "Unlock-TunaFromSessionHeader", "Get-TunaGuiPayerRole");
        var liveCycleFunctionBody = ExtractPowerShellFunctionBody(scriptText, "Invoke-FileTransferTunaHandoffFallbackCycle", "Wait-FileTransferTerminalOrProgressBeforeAction");

        Assert.Contains("unlock_entry_existing_prompt", unlockFunctionBody, StringComparison.Ordinal);
        Assert.Contains("runtime_evidence_late_wallet_prompt", unlockFunctionBody, StringComparison.Ordinal);
        Assert.Contains("Submit-TunaWalletPasswordDialogsForPayersIfPresent", scriptText, StringComparison.Ordinal);
        Assert.Contains("prearmed_on_step_", liveCycleFunctionBody, StringComparison.Ordinal);
        Assert.DoesNotContain("activation_route_wait_step_", liveCycleFunctionBody, StringComparison.Ordinal);
        Assert.DoesNotContain("-OnPoll $serviceWalletPrompts", liveCycleFunctionBody, StringComparison.Ordinal);
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
