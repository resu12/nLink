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
    public void GuiSmokeTunaScenario_WritesDurableLogLedgerEvidenceOnce()
    {
        var guiText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        var wrapperText = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "Run-FileTransferTunaGuiSmoke.ps1"));
        var scenarioBody = ExtractPowerShellFunctionBody(
            guiText,
            "Run-ScenarioFileTransferTunaHandoffFallback",
            "Suspend-ProcessForRecoveryWindow");

        Assert.Contains("Get-GuiSmokeLogLedgerSnapshot", guiText, StringComparison.Ordinal);
        Assert.Contains("Write-FileTransferGuiLogLedgerEvidence", guiText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-gui-log-ledger.json", guiText, StringComparison.Ordinal);
        Assert.Contains("filetransfer-gui-log-ledger-slice.log", guiText, StringComparison.Ordinal);
        Assert.Contains("-WriteOnce", guiText, StringComparison.Ordinal);
        Assert.Contains("analyzerInput = $analyzerInputEvidence", guiText, StringComparison.Ordinal);
        Assert.Contains("[System.IO.FileMode]::CreateNew", guiText, StringComparison.Ordinal);
        Assert.Contains("NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID", guiText, StringComparison.Ordinal);
        Assert.Contains("NLINK_FILETRANSFER_SCENARIO_RUN_ID", guiText, StringComparison.Ordinal);
        Assert.Contains("duplicateIndexCount", guiText, StringComparison.Ordinal);
        Assert.Contains("consumedIndexSha256", guiText, StringComparison.Ordinal);
        Assert.Contains("cursorIntegrityVerdict", guiText, StringComparison.Ordinal);
        Assert.Contains("[string]$FileName = 'filetransfer-retained-log-slice.log'", wrapperText, StringComparison.Ordinal);
        Assert.DoesNotContain("filetransfer-gui-log-ledger-slice.log", wrapperText, StringComparison.Ordinal);

        var helpeeStartIndex = scenarioBody.IndexOf("Start-HelpeeFlow -Context $Context", StringComparison.Ordinal);
        var bookmarkIndex = scenarioBody.IndexOf("$runBookmark = Get-AppLogBookmark", StringComparison.Ordinal);
        var initialSnapshotIndex = scenarioBody.IndexOf("$initialLogLedgerSnapshot = Get-GuiSmokeLogLedgerSnapshot", StringComparison.Ordinal);
        var helperStartIndex = scenarioBody.IndexOf("Start-HelperFlow -Context $Context", StringComparison.Ordinal);
        Assert.True(
            helpeeStartIndex >= 0 &&
            bookmarkIndex > helpeeStartIndex &&
            initialSnapshotIndex > bookmarkIndex &&
            helperStartIndex > initialSnapshotIndex,
            "The Tuna ledger bookmark and initial generation snapshot must be captured after the first app establishes nlink.log and before the helper contributes scenario events.");
        Assert.Contains("gui_smoke_log_cursor_initial_generation_missing", scenarioBody, StringComparison.Ordinal);
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
        var explicitBody = ExtractPowerShellFunctionBody(guiText, "Get-TunaGuiExplicitFailureClassification", "Resolve-TunaGuiFailureClassification");
        var resolveBody = ExtractPowerShellFunctionBody(guiText, "Resolve-TunaGuiFailureClassification", "Write-TunaGuiTopLevelFailureSummaryIfMissing");
        var topLevelBody = ExtractPowerShellFunctionBody(guiText, "Write-TunaGuiTopLevelFailureSummaryIfMissing", "Get-TunaGuiFileTransferSetupFailureClassification");
        var scriptText = $$"""
            $ErrorActionPreference = 'Stop'
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
