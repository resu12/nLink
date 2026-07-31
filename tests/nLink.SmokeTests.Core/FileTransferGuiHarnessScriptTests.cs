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
                    -TimeoutMs 2000

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

        Assert.Contains("Write-GuiSmokeLogLedgerRangeCapture", writerBody, StringComparison.Ordinal);
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
            function Stop-TunaGuiSmokeProcessTree { {{stopTreeBody}} }
            function Stop-TunaGuiSmokeApplicationDescendants { {{stopApplicationsBody}} }
            function Request-TunaGuiScenarioDeadline { {{requestDeadlineBody}} }
            function Invoke-TunaGuiSmokeWithHardDeadline { {{watchdogBody}} }
            function Assert-GuiSmokeScenarioDeadline { {{scenarioDeadlineBody}} }

            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-tuna-gui-watchdog-' + [guid]::NewGuid().ToString('N'))
            $toolsDir = Join-Path $tempRoot 'tools'
            $cooperativeArtifact = Join-Path $tempRoot 'cooperative-artifact'
            $routeWaitArtifact = Join-Path $tempRoot 'route-wait-artifact'
            $timeoutArtifact = Join-Path $tempRoot 'timeout-artifact'
            $successArtifact = Join-Path $tempRoot 'success-artifact'
            [void][IO.Directory]::CreateDirectory($toolsDir)
            [void][IO.Directory]::CreateDirectory($cooperativeArtifact)
            [void][IO.Directory]::CreateDirectory($routeWaitArtifact)
            [void][IO.Directory]::CreateDirectory($timeoutArtifact)
            [void][IO.Directory]::CreateDirectory($successArtifact)
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
                if ($completed.TimedOut -or [int]$completed.ExitCode -ne 0) {
                    throw "natural child exit lost the timeout race: $($completed | ConvertTo-Json -Compress)"
                }
                if (Test-Path -LiteralPath (Join-Path $successArtifact 'filetransfer-gui-evidence-finalization-timeout.json')) {
                    throw 'natural child exit wrote a timeout marker'
                }
                $successState = Get-Content -LiteralPath (Join-Path $successArtifact 'filetransfer-gui-evidence-finalization-state.json') -Raw | ConvertFrom-Json
                if ($successState.lastCompletedStage -ne 'gui_child_exited' -or
                    $successState.timedOut -or
                    [int]$successState.exitCode -ne 0) {
                    throw "natural child state was invalid: $($successState | ConvertTo-Json -Compress)"
                }

                Write-Output 'tuna-gui-watchdog-ok'
            }
            finally {
                Remove-Item Env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID -ErrorAction SilentlyContinue
                Remove-Item Env:NLINK_FILETRANSFER_SCENARIO_RUN_ID -ErrorAction SilentlyContinue
                Remove-Item Env:NLINK_FILETRANSFER_GUI_FINALIZATION_GRACE_SECONDS_TEST_ONLY -ErrorAction SilentlyContinue
                Remove-Item Env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH -ErrorAction SilentlyContinue
                Remove-Item Env:NLINK_FILETRANSFER_GUI_SCENARIO_DEADLINE_REQUEST_PATH -ErrorAction SilentlyContinue
                if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
            }
            """;

        var result = await RunPowerShellScriptTextAsync(scriptText, Array.Empty<string>());

        Assert.True(result.ExitCode == 0, $"Tuna GUI watchdog test failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("tuna-gui-watchdog-ok", result.Stdout, StringComparison.Ordinal);
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
        Assert.DoesNotContain(
            "Get-TunaGuiFileTransferSetupFailureClassification",
            scenarioBody,
            StringComparison.Ordinal);
        Assert.Contains("consumedIndexSha256", guiText, StringComparison.Ordinal);
        Assert.Contains("cursorIntegrityVerdict", guiText, StringComparison.Ordinal);
        Assert.Contains("[string]$FileName = 'filetransfer-retained-log-slice.log'", wrapperText, StringComparison.Ordinal);
        Assert.DoesNotContain("filetransfer-gui-log-ledger-slice.log", wrapperText, StringComparison.Ordinal);
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
            $payloadHasher = $null
            try {
                $script:GuiSmokeAppLogLedger = New-GuiSmokeLogLedgerState
                $script:GuiSmokeAppLogLedger.Lines = [Collections.Generic.List[string]]::new(200000)
                $markerPath = Join-Path $outputRoot 'scale.stderr.log'
                $markerWriter = [IO.StreamWriter]::new(
                    $markerPath,
                    $false,
                    [Text.UTF8Encoding]::new($false),
                    65536)
                $payloadHasher = [Security.Cryptography.SHA256]::Create()
                $utf8 = [Text.UTF8Encoding]::new($false)
                for ($sequence = 1; $sequence -le 100000; $sequence++) {
                    $event = if ($sequence -eq 100000) { 'operational_log_writer_drained' } else { 'payload' }
                    $lineA = '[a] event=' + $event +
                        '; acceptance_run_id=run-scale; scenario_run_id=run-scale:scenario:1' +
                        '; log_writer_id=writer-a; log_writer_sequence=' + $sequence +
                        '; log_writer_batch_generation=' + $sequence +
                        '; log_writer_batch_first_sequence=' + $sequence +
                        '; log_writer_batch_last_sequence=' + $sequence +
                        '; log_writer_attempt_utc_ticks=' + (1000000 + $sequence)
                    $lineB = '[b] event=' + $event +
                        '; acceptance_run_id=run-scale; scenario_run_id=run-scale:scenario:1' +
                        '; log_writer_id=writer-b; log_writer_sequence=' + $sequence +
                        '; log_writer_batch_generation=' + $sequence +
                        '; log_writer_batch_first_sequence=' + $sequence +
                        '; log_writer_batch_last_sequence=' + $sequence +
                        '; log_writer_attempt_utc_ticks=' + (2000000 + $sequence)
                    $script:GuiSmokeAppLogLedger.Lines.Add($lineA)
                    $script:GuiSmokeAppLogLedger.Lines.Add($lineB)
                    if ($sequence -le 50000) {
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

                Write-FileTransferGuiLogLedgerEvidence `
                    -ArtifactDir $artifactDir `
                    -Bookmark 0 `
                    -InitialSnapshot $initialSnapshot `
                    -TestMetrics $metrics
                $ledger = Get-Content -LiteralPath (Join-Path $artifactDir 'filetransfer-gui-log-ledger.json') -Raw | ConvertFrom-Json
                if ([long]$metrics.ledgerLineVisitCount -ne 200000 -or
                    [long]$metrics.markerLineVisitCount -ne 100000 -or
                    [long]$metrics.deferredLookupCount -ne 50000 -or
                    [long]$metrics.recoveredLookupCount -ne 50000) {
                    throw "nonlinear visit metrics: $($metrics | ConvertTo-Json -Compress)"
                }
                if ($ledger.writerDeferredBatchCount -ne 50000 -or
                    $ledger.writerRecoveredBatchCount -ne 50000 -or
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
                Write-Output 'gui-log-ledger-scale-linear-ok'
            }
            finally {
                if ($markerWriter) { $markerWriter.Dispose() }
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
    public async Task GuiSmokeSwitchOffWait_IsTerminalAwareAndNeverClicksAfterTerminal()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var trackerBody = ExtractPowerShellFunctionBody(guiText, "Add-TunaGuiCompletedTerminalToTracker", "New-TunaGuiLiveSwitchOffProgressGateResult");
        var pairBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiCompletedTerminalPairAfterBookmark", "Test-TunaGuiSwitchOffToggleReady");
        var toggleBody = ExtractPowerShellFunctionBody(guiText, "Test-TunaGuiSwitchOffToggleReady", "Wait-TunaGuiSwitchOffToggleOrTerminal");
        var waitBody = ExtractPowerShellFunctionBody(guiText, "Wait-TunaGuiSwitchOffToggleOrTerminal", "Try-PrearmTunaGuiSwitchOffToggle");
        var prearmBody = ExtractPowerShellFunctionBody(guiText, "Try-PrearmTunaGuiSwitchOffToggle", "Get-TunaGuiLogLineUtcText");
        var invokeBody = ExtractPowerShellFunctionBody(guiText, "Invoke-TunaGuiFallbackFault", "Add-TunaGuiLiveRouteEpochObservation");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'

            function Add-TunaGuiCompletedTerminalToTracker { {{trackerBody}} }
            function Get-TunaGuiCompletedTerminalPairAfterBookmark { {{pairBody}} }
            function Test-TunaGuiSwitchOffToggleReady { {{toggleBody}} }
            function Wait-TunaGuiSwitchOffToggleOrTerminal { {{waitBody}} }
            function Try-PrearmTunaGuiSwitchOffToggle { {{prearmBody}} }
            function Invoke-TunaGuiFallbackFault { {{invokeBody}} }
            function Assert-GuiSmokeScenarioDeadline {}

            function Get-TunaGuiCompletedTerminalFromLogLine {
                param([string]$Line)
                $match = [regex]::Match($Line, 'direction=(?<direction>inbound|outbound);\s*transfer_id=(?<transfer>[^;]+)')
                if (-not $match.Success) { return $null }
                return [pscustomobject]@{ Direction = $match.Groups['direction'].Value; TransferId = $match.Groups['transfer'].Value; Line = $Line }
            }
            function Get-AppLogLinesAfterBookmark {
                param([int]$Bookmark)
                $script:logPoll++
                if ($script:logPoll -ge $script:terminalAt) { return @($script:terminalLines) }
                return @()
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
                return Wait-TunaGuiSwitchOffToggleOrTerminal -Window ([pscustomobject]@{}) -Bookmark 0 -TimeoutMs 100 -PollIntervalMs 1
            }

            $script:testToggle = [pscustomobject]@{ Current = [pscustomobject]@{ IsEnabled = $true; IsOffscreen = $false } }
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
            function Click-Element { param($Element) $script:clickCount++ }
            $context = [pscustomobject]@{ HelperWindow = $null; HelpeeWindow = [pscustomobject]@{}; HelperProc = $null; HelpeeProc = [pscustomobject]@{} }
            function Wait-TunaGuiSwitchOffToggleOrTerminal { throw 'gui_smoke_log_cursor_gap: generation missing' }
            $cursorPropagated = $false
            try { [void](Try-PrearmTunaGuiSwitchOffToggle -Context $context -PayerMode 'helpee' -Bookmark 0) } catch { $cursorPropagated = $_.Exception.Message -like 'gui_smoke_log_cursor_gap*' }
            if (-not $cursorPropagated) { throw 'prearm swallowed cursor failure' }

            $script:clickCount = 0
            function Wait-TunaGuiSwitchOffToggleOrTerminal {
                return [pscustomobject]@{ Kind = 'terminal'; Toggle = $null; TransferId = 'tx-before-click'; WaitMs = 1L; Prearmed = $false }
            }
            $noClick = Invoke-TunaGuiFallbackFault -Context $context -FaultMode 'switch-off' -PayerMode 'helpee' -Bookmark 0
            if ($noClick.Injected -or -not $noClick.TerminalCompletedBeforeFault -or $script:clickCount -ne 0) { throw 'terminal-first fault attempted a click' }

            function Wait-TunaGuiSwitchOffToggleOrTerminal {
                return [pscustomobject]@{ Kind = 'toggle'; Toggle = $script:testToggle; TransferId = '(none)'; WaitMs = 1L; Prearmed = $true }
            }
            function Get-TunaGuiCompletedTerminalPairAfterBookmark {
                return [pscustomobject]@{ TransferId = 'tx-immediate-check'; Line = 'terminal' }
            }
            $noLateClick = Invoke-TunaGuiFallbackFault -Context $context -FaultMode 'switch-off' -PayerMode 'helpee' -Bookmark 0 -PrearmedToggle $script:testToggle
            if ($noLateClick.Injected -or -not $noLateClick.TerminalCompletedBeforeFault -or $script:clickCount -ne 0) { throw 'immediate pre-click terminal check attempted a click' }

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
            $initialRecoveryHold = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=runtime_unlock_initial_offer_receive_recovery_hold_not_preserved; retained_event=tuna_acceleration_activation_offer_not_observed' -HistoricalPhase 'setup' -HistoricalReason 'offer_not_observed'
            if ($initialRecoveryHold.Phase -ne 'runtime_unlock_initial_activation' -or $initialRecoveryHold.Reason -ne 'initial_activation_retry_exhausted_while_receive_recovery_awaiting_proof') { throw 'initial activation recovery hold lost precedence' }
            $initialRecoveryHoldRelease = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing; retained_event=runtime_unlock_initial_activation_receive_recovery_hold_invalidation_rejected' -HistoricalPhase 'setup' -HistoricalReason 'offer_not_observed'
            if ($initialRecoveryHoldRelease.Phase -ne 'runtime_unlock_initial_activation' -or $initialRecoveryHoldRelease.Reason -ne 'released_hold_routed_through_exact_invalidation_boundary') { throw 'initial activation recovery hold release delivery lost precedence' }
            $initialRecoveryHoldRefreshRelease = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing; retained_event=runtime_unlock_initial_activation_receive_recovery_hold_send_prevalidated' -HistoricalPhase 'setup' -HistoricalReason 'offer_not_observed'
            if ($initialRecoveryHoldRefreshRelease.Phase -ne 'runtime_unlock_initial_activation' -or $initialRecoveryHoldRefreshRelease.Reason -ne 'committed_refresh_successor_envelope_not_accepted_by_release_prevalidation') { throw 'committed refresh release prevalidation lost precedence' }
            $initialRecoveryHoldExpired = Resolve-TunaGuiFailureClassification -ErrorMessage 'failure_class=runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired; retained_event=tuna_acceleration_message_rejected' -HistoricalPhase 'regular_v4_frontier_repair' -HistoricalReason 'regular_v4_frontier_repair_stalled'
            if ($initialRecoveryHoldExpired.Phase -ne 'runtime_unlock_initial_activation' -or $initialRecoveryHoldExpired.Reason -ne 'initial_activation_offer_expired_during_receive_recovery_hold') { throw 'initial activation recovery hold expired offer lost precedence' }
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
    public async Task GuiSmokeFailureClassification_ExplicitClassReadsNoEvidence()
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

            $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('nlink-gui-explicit-no-read-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($tempRoot)
            try {
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
                    -ErrorMessage 'failure_class=regular_v4_terminal_before_tuna_activation_commit' `
                    -RouteMode 'live-regular-activation-cycle' `
                    -ImmutableEvidencePaths @($largeEvidence) `
                    -TestMetrics $metrics
                if ($result.Phase -ne 'live_route_proof' -or
                    $result.ClassificationSource -ne 'explicit_error' -or
                    [long]$result.EvidenceLineVisitCount -ne 0 -or
                    [long]$metrics.evidenceLineVisitCount -ne 0 -or
                    [long]$metrics.evidenceLookupCount -ne 0) {
                    throw "explicit classification touched evidence: result=$($result | ConvertTo-Json -Compress); metrics=$($metrics | ConvertTo-Json -Compress)"
                }
                Write-Output 'gui-explicit-classification-no-read-ok'
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
            $"Explicit classification read evidence.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("gui-explicit-classification-no-read-ok", result.Stdout, StringComparison.Ordinal);
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
            "Test-TunaGuiBridgeRecoveryExecutionSubmissionBarrierDrainFailure");
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
            $recovered += "event=filetransfer_receive_recovery_operation_proof_observed; $outboundOperation; $outboundScope; closure_generation=1; closure_state=Adopted; proof_identity=file_transfer_v4_state:226"
            $recovered += "event=filetransfer_receive_recovery_operation_closure_adopted; $outboundOperation; $outboundScope; closure_generation=1; closure_state=Adopted; proof_identity=file_transfer_v4_state:226"
            $recovered += "event=filetransfer_bridge_recovery_payload_submission_resumed; $outboundOperation; $outboundScope; closure_generation=1; closure_state=Adopted; proof_identity=file_transfer_v4_state:226"
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
            $crossScope = @(
                $stallLines
                "event=filetransfer_regular_v4_frontier_repair_stalled; session_id=session-stall-b; transfer_id=transfer-stall-b; direction=inbound; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; leg_generation=1; transport_epoch=0; bridge_recovery_generation=0; repair_request_key=10:2:10:2"
            )
            if (-not (Test-TunaGuiRegularV4FrontierRepairStallUnresolved -Lines $crossScope)) {
                throw 'cross-scope atomic recovery masked an unrelated stall'
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
        Assert.DoesNotContain("$terminalBeforeFallbackAllowedSequences", guiText, StringComparison.Ordinal);
        Assert.DoesNotContain("$terminalBeforeFallbackSequenceAllowed", guiText, StringComparison.Ordinal);
        Assert.Contains("$terminalBeforeFallbackExpectedRoutes = @($joinedRouteSequence.Split(',', [System.StringSplitOptions]::RemoveEmptyEntries))", guiText, StringComparison.Ordinal);
        Assert.Contains("fallbackEvidenceObserved", guiText, StringComparison.Ordinal);
        Assert.Contains("failure_class=terminal_before_fallback_switch_off", guiText, StringComparison.Ordinal);
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
