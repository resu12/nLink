namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("FileTransferGuiSuite", "PayerHandoff")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferPayerHandoffGuiTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task PayerHandoffGui_ImmediateReactivationPrearmsTheFinalOffPayer(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$RepoRoot)
            $ErrorActionPreference = 'Stop'
            Set-StrictMode -Version Latest
            $tokens = $null; $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'), [ref]$tokens, [ref]$errors)
            if ($errors.Count) { throw ($errors | Out-String) }
            foreach ($name in @('Get-TunaGuiLiveTogglePayer', 'Get-TunaGuiFaultTarget',
                    'Test-TunaGuiSwitchOffToggleReady', 'Get-TunaGuiSwitchOffClickPoint', 'Try-PrearmTunaGuiSwitchOffToggle')) {
                Invoke-Expression $ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name}, $true).Extent.Text
            }
            $cycle = $ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Invoke-FileTransferTunaHandoffFallbackCycle'}, $true)
            $branches = @($cycle.FindAll({param($n) $n -is [Management.Automation.Language.IfStatementAst] -and
                $n.Clauses[0].Item1.Extent.Text.Contains("`$RouteMode -eq 'live-regular-activation-cycle'") -and
                $n.Clauses[0].Item1.Extent.Text.Contains('$nextAction') -and
                $n.Clauses[0].Item1.Extent.Text.Contains('$prearmedActivationBookmark')}, $true))
            if ($branches.Count -ne 1) { throw 'immediate reactivation branch missing or duplicated' }
            # Execute the real branch through pre-arm, stopping before the live route wait.
            $statements = [Collections.Generic.List[string]]::new()
            foreach ($statement in $branches[0].Clauses[0].Item2.Statements) {
                if ($statement -is [Management.Automation.Language.AssignmentStatementAst] -and
                    $statement.Left.Extent.Text -ceq '$activationProofTimeoutMs') { break }
                $statements.Add($statement.Extent.Text)
            }
            $prearmBranch = [scriptblock]::Create($statements -join "`n")
            function Assert-GuiSmokeScenarioDeadline {}
            function Find-VisibleByAutomationId { param($Root, $AutomationId) return $Root.Toggle }
            function Submit-TunaWalletPasswordDialogsForPayersIfPresent {
                param($Context, $PayerMode, $Password, $Reason)
                $script:dialogPayer = $PayerMode
            }
            $helperToggle = [pscustomobject]@{Current=[pscustomobject]@{IsEnabled=$true; IsOffscreen=$false; ClickX=100; ClickY=100}}
            $helpeeToggle = [pscustomobject]@{Current=[pscustomobject]@{IsEnabled=$true; IsOffscreen=$false; ClickX=300; ClickY=100}}
            $Context = [pscustomobject]@{HelperWindow=[pscustomobject]@{Toggle=$helperToggle}; HelpeeWindow=[pscustomobject]@{Toggle=$helpeeToggle}}
            $RouteMode = 'live-regular-activation-cycle'; $sequence = @('on','off','on','off')
            $WalletPassword = 'fixture'; $bookmark = 0
            foreach ($PayerMode in @('helper-to-helpee', 'helper', 'helpee', 'both')) {
                $stepIndex = 1; $stepNumber = 2; $nextAction = 'on'; $prearmedActivationBookmark = 10
                $stepPayerMode = Get-TunaGuiLiveTogglePayer -PayerMode $PayerMode -StepIndex $stepIndex -RouteMode $RouteMode
                $observedEvidenceLines = [Collections.Generic.List[string]]::new()
                $faultAdmissionTracker = [pscustomobject]@{CachedSwitchOffControl=$helperToggle; CachedSwitchOffClickPoint=[pscustomobject]@{X=100;Y=100}; LineVisitCount=0}
                . $prearmBranch
                $finalPayer = Get-TunaGuiLiveTogglePayer -PayerMode $PayerMode -StepIndex 3 -RouteMode $RouteMode
                $target = Get-TunaGuiFaultTarget -PayerMode $finalPayer
                $expectedToggle = if ($target -eq 'helper') { $helperToggle } else { $helpeeToggle }
                if (-not [object]::ReferenceEquals($prearmedSwitchOffToggle, $expectedToggle) -or
                    -not [object]::ReferenceEquals($faultAdmissionTracker.CachedSwitchOffControl, $expectedToggle) -or
                    $faultAdmissionTracker.CachedSwitchOffClickPoint.X -ne $expectedToggle.Current.ClickX) {
                    throw "wrong prearmed switch-off control: payer=$PayerMode; expected=$target"
                }
                if ($stepIndex -ne 2 -or $stepNumber -ne 3 -or $stepPayerMode -cne $finalPayer -or
                    $script:dialogPayer -cne $finalPayer -or $null -ne $prearmedActivationBookmark -or
                    $lastActivationPayloadBookmark -ne 10) { throw 'reactivation lost step payer or action bookmark' }
            }
            'PASS'
            """, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("PASS", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task PayerHandoffGui_RegularStartKeepsHelperForFirstOnOffThenHandsToHelpee(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$RepoRoot)
            $ErrorActionPreference = 'Stop'
            Set-StrictMode -Version Latest
            function Read-ScriptAst([string]$Name) {
                $tokens = $null; $errors = $null
                $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot ('tools/' + $Name)), [ref]$tokens, [ref]$errors)
                if ($errors.Count) { throw ($errors | Out-String) }
                return $ast
            }
            $gui = Read-ScriptAst 'GuiSmoke-Windows.ps1'
            $wrapper = Read-ScriptAst 'Run-FileTransferTunaGuiSmoke.ps1'
            $acceptance = Read-ScriptAst 'Run-FileTransferRouteAcceptance.ps1'
            foreach ($name in @('New-Phase4RouteAcceptanceScenario', 'Get-Phase5RouteAcceptanceScenarios', 'Test-Phase5ScenarioRequiresIsolation')) {
                Invoke-Expression $acceptance.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name}, $true).Extent.Text
            }
            $baseline = [pscustomobject]@{Scenarios=@{}}
            $name = 'regular-v4-helper-to-helpee-payer-handoff-256mb'
            $defaults = @(Get-Phase5RouteAcceptanceScenarios -BaselineManifest $baseline)
            if ($defaults.Count -ne 7 -or @($defaults | Where-Object Name -eq $name).Count -ne 0) { throw 'existing release matrix changed' }
            $scenarios = @(Get-Phase5RouteAcceptanceScenarios -BaselineManifest $baseline -IncludeRegularPayerHandoff)
            $matches = @($scenarios | Where-Object Name -eq $name)
            if ($scenarios.Count -ne 8 -or $matches.Count -ne 1) { throw 'targeted reproduction missing or duplicated' }
            $scenario = $matches[0]
            if ($scenario.PayerMode -cne 'helper-to-helpee' -or $scenario.Direction -cne 'helper-to-helpee' -or
                $scenario.RouteMode -cne 'live-regular-activation-cycle' -or $scenario.LiveToggleSequence -cne 'on,off,on,off' -or
                $scenario.Fault -cne 'switch-off' -or $scenario.LiveProofMode -cne 'RegularActivationCycle' -or
                $scenario.PayloadBytes -ne 268435456 -or $null -ne $scenario.Baseline -or
                ($scenario.ExpectedRouteChanges -join ',') -cne 'regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6') { throw 'reproduction contract mismatch' }
            if (-not (Test-Phase5ScenarioRequiresIsolation -Scenario $scenario)) { throw 'strict isolation missing' }
            Invoke-Expression $gui.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Get-TunaGuiLiveTogglePayer'}, $true).Extent.Text
            $roles = @(0..3 | ForEach-Object { Get-TunaGuiLiveTogglePayer -PayerMode $scenario.PayerMode -RouteMode $scenario.RouteMode -StepIndex $_ })
            if (($roles -join ',') -cne 'helper,helper,helpee,helpee') { throw 'regular-start payer ordering mismatch' }
            foreach ($mode in @('helper', 'helpee', 'both')) {
                foreach ($step in 0..3) {
                    if ((Get-TunaGuiLiveTogglePayer -PayerMode $mode -RouteMode $scenario.RouteMode -StepIndex $step) -cne $mode) { throw 'fixed payer changed' }
                }
            }
            $cycle = $gui.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Invoke-FileTransferTunaHandoffFallbackCycle'}, $true).Extent.Text
            foreach ($call in @('Get-TunaGuiLiveTogglePayer -PayerMode $PayerMode -StepIndex $stepIndex -RouteMode $RouteMode',
                'Get-TunaGuiLiveTogglePayer -PayerMode $PayerMode -StepIndex ($stepIndex + 1) -RouteMode $RouteMode')) {
                if (-not $cycle.Contains($call)) { throw 'normal or prearmed action lost route mode' }
            }
            $guard = @($wrapper.FindAll({param($n) $n -is [Management.Automation.Language.IfStatementAst] -and
                $n.Extent.Text.Contains('Helper-to-helpee payer handoff requires')}, $true))
            if ($guard.Count -ne 1) { throw 'wrapper guard missing' }
            $PayerMode = $scenario.PayerMode; $RouteMode = $scenario.RouteMode; $Fault = $scenario.Fault
            foreach ($LiveToggleSequence in @('', 'on,off,on,off')) { Invoke-Expression $guard[0].Extent.Text }
            foreach ($LiveToggleSequence in @('off,on,off', 'on,off')) {
                $rejected = $false
                try { Invoke-Expression $guard[0].Extent.Text } catch { $rejected = $true }
                if (-not $rejected) { throw 'wrong regular-start sequence admitted' }
            }
            $LiveToggleSequence = 'on,off,on,off'; $Fault = 'none'; $rejected = $false
            try { Invoke-Expression $guard[0].Extent.Text } catch { $rejected = $true }
            if (-not $rejected) { throw 'handoff without switch-off admitted' }
            $invoke = $acceptance.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Invoke-TunaRouteAcceptanceRun'}, $true).Extent.Text
            if ($invoke.Contains("'-Direction', 'helpee-to-helper'") -or $invoke.Contains("-Direction 'helpee-to-helper'")) { throw 'direction hardcoded in execution or evidence' }
            if ([regex]::Matches($invoke, "'-Direction', \`$Direction").Count -ne 2 -or
                [regex]::Matches($invoke, '-Direction \$Direction').Count -ne 2) { throw 'execution and receipt direction not forwarded' }
            Invoke-Expression $invoke
            function Invoke-RouteAcceptanceChildScriptNoThrow {
                param($ScriptPath, $Arguments)
                $script:childArguments = $Arguments
                return 0
            }
            function Invoke-RouteAcceptanceRetainedAnalysis {}
            function Update-FileTransferTunaDirectEvidenceManifestFromReceipt {
                param($Direction)
                $script:receiptDirection = $Direction
                return [pscustomobject]@{CurrentRuntimeProof=$true}
            }
            $TimeoutSeconds = 900; $ProgressTimeoutSeconds = 180; $script:RouteAcceptanceExecutionMode = 'fake'
            foreach ($direction in @('helper-to-helpee', 'helpee-to-helper')) {
                Invoke-TunaRouteAcceptanceRun -RepoRoot $RepoRoot -ArtifactDir 'fixture' -ResolvedExePath 'fixture.exe' `
                    -ResolvedWalletPath 'fixture-wallet.json' -ResolvedSidecarPath 'fixture-sidecar.exe' -EffectiveWalletPassword 'fixture' `
                    -RouteMode $scenario.RouteMode -Fault $scenario.Fault -PayerMode $scenario.PayerMode -Direction $direction `
                    -PayloadSize '256MiB' -LiveToggleSequence $scenario.LiveToggleSequence -LiveProofMode $scenario.LiveProofMode
                $index = [array]::IndexOf($script:childArguments, '-Direction')
                if ($index -lt 0 -or $script:childArguments[$index + 1] -cne $direction -or $script:receiptDirection -cne $direction) { throw 'direction changed between GUI dispatch and evidence receipt' }
            }
            'PASS'
            """, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("PASS", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task PayerHandoffGui_AlreadyPublishedFallbackCannotAdmitSwitchOff(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$RepoRoot)
            $ErrorActionPreference = 'Stop'
            Set-StrictMode -Version Latest
            $tokens = $null; $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'), [ref]$tokens, [ref]$errors)
            if ($errors.Count) { throw ($errors | Out-String) }
            foreach ($name in @('New-TunaGuiFaultAdmissionTracker', 'Get-TunaGuiFaultAdmissionScopeKey',
                    'Get-TunaGuiFaultAdmissionTargetKey', 'Get-TunaGuiFaultAdmissionProjectionKey',
                    'Update-TunaGuiFaultAdmissionAuthority', 'Update-TunaGuiFaultAdmissionTracker',
                    'Assert-TunaGuiSwitchOffRouteCurrent', 'Wait-TunaGuiSwitchOffToggleOrTerminal',
                    'Get-TunaGuiExplicitFailureClassification')) {
                $definition = $ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name}, $true)
                if ($null -ne $definition) { Invoke-Expression $definition.Extent.Text }
            }
            function Assert-GuiSmokeScenarioDeadline {}
            function Sync-GuiSmokeAppLogLedger {}
            function Get-GuiSmokeLogLedgerBookmark { return $script:lines.Count }
            function Get-TunaGuiCompletedTerminalFromLogLine { return $null }
            function Test-TunaGuiSwitchOffToggleReady { param($Toggle) return $null -ne $Toggle }
            function Get-TunaGuiSwitchOffClickPoint { param($Toggle) return [pscustomobject]@{X=1; Y=1} }
            function Find-VisibleByAutomationId { return [pscustomobject]@{} }
            function Get-GuiSmokeFieldValue {
                param($Fields, [string]$Name, [string]$Default = '')
                if ($Fields.ContainsKey($Name)) { return [string]$Fields[$Name] }
                return $Default
            }
            function ConvertFrom-GuiSmokeSemicolonFields {
                param([string]$Message)
                $fields = @{}
                foreach ($part in $Message.Split(';')) {
                    $pair = $part.Trim().Split('=', 2)
                    if ($pair.Count -eq 2) { $fields[$pair[0].Trim()] = $pair[1].Trim() }
                }
                return $fields
            }
            function Invoke-GuiSmokeLogLedgerRangeVisitor {
                param($State, [int]$StartIndex, [int]$EndIndex, [scriptblock]$Visitor)
                for ($i = $StartIndex; $i -lt $EndIndex; $i++) { [void](& $Visitor $script:lines[$i] $i) }
            }
            function Add-RouteRound([int]$Epoch, [bool]$Fallback) {
                $sourceEpoch = $Epoch - 1
                $generation = $Epoch + 1
                $source = if ($Fallback) { 'file_tuna_v4' } elseif ($Epoch -eq 1) { 'regular_nkn_v4_fast' } else { 'post_tuna_fallback_v6' }
                $sourceProtocol = if ($source -eq 'post_tuna_fallback_v6') { 6 } else { 4 }
                $route = if ($Fallback) { 'post_tuna_fallback_v6' } else { 'file_tuna_v4' }
                $protocol = if ($Fallback) { 6 } else { 4 }
                $kind = if ($Fallback) { 'TunaV4ToPostTunaFallbackV6' } elseif ($Epoch -eq 1) { 'RegularV4ToTunaV4' } else { 'PostTunaFallbackV6ToTunaV4' }
                $pair = if ($Epoch -gt 1 -and -not $Fallback) { "s|t|$source|$sourceProtocol|$sourceEpoch|0|(none)" } else { '(none)' }
                foreach ($direction in @('outbound', 'inbound')) {
                    $scope = "session_id=s; transfer_id=t; direction=$direction; log_writer_id=writer-$direction"
                    $authority = "authority-$Epoch-$direction"
                    $script:lines.Add("event=filetransfer_route_transition_committed; $scope; authority_id=$authority; transition_generation=$Epoch; transition_kind=$kind; source_route=$source; source_protocol_version=$sourceProtocol; source_live_route_epoch=$sourceEpoch; source_transport_epoch=0; checkpoint_request_id=(none); pair_key=$pair; target_route=$route; target_protocol_version=$protocol; target_live_route_epoch=$Epoch; target_runtime_generation=$generation; target_terminal_generation=0; target_leg_generation=$generation; target_route_publication_generation=$generation; target_route_commit_generation=0; decision_status=Committed")
                    $script:lines.Add("event=filetransfer_route_hint_authorized_publication_accepted; $scope; route=$route; protocol_version=$protocol; live_route_epoch=$Epoch; runtime_generation=$generation; terminal_generation=0; leg_generation=$generation; route_publication_generation=$generation; route_commit_generation=0")
                    $script:lines.Add("event=filetransfer_route_transition_disposition_applied; $scope; authority_id=$authority; transition_generation=$Epoch; target_route=$route; disposition=committed")
                    $script:lines.Add("event=filetransfer_data_path_receive_result; $scope; route=$route; protocol_version=$protocol; live_route_epoch=$Epoch; runtime_generation=$generation; terminal_generation=0; leg_generation=$generation; route_publication_generation=$generation; route_commit_generation=0")
                }
            }
            function Assert-AdmissionRejected($Tracker) {
                $message = ''
                try { [void](Wait-TunaGuiSwitchOffToggleOrTerminal -Window ([pscustomobject]@{}) -Bookmark 0 -AdmissionTracker $Tracker -TimeoutMs 100) }
                catch { $message = $_.Exception.Message }
                if ($message -notlike '*failure_class=filetransfer_gui_fallback_before_switch_off*') {
                    throw "already-published fallback admitted switch-off: state=$($Tracker.AdmissionState); error=$message"
                }
                if ($Tracker.AdmissionState -ne 'Unclaimed' -or $Tracker.ClickClaimedUtc -ne '(none)' -or
                    $Tracker.ClickInvokedUtc -ne '(none)' -or $null -eq $Tracker.AuthoritativeFallbackProjection) {
                    throw 'pre-fault fallback rejection claimed an action or discarded its evidence'
                }
                $classification = Get-TunaGuiExplicitFailureClassification -ErrorMessage $message
                if ($classification.Phase -ne 'live_route_proof' -or $classification.Reason -ne 'filetransfer_gui_fallback_before_switch_off') {
                    throw 'confirmed pre-fault fallback was not directly classified'
                }
            }
            $script:GuiSmokeAppLogLedger = [pscustomobject]@{}
            $script:lines = [Collections.Generic.List[string]]::new()
            Add-RouteRound 1 $false
            $tracker = New-TunaGuiFaultAdmissionTracker -Bookmark 0
            [void](Update-TunaGuiFaultAdmissionTracker -Tracker $tracker)
            if ($null -eq $tracker.AuthoritativeTunaProjection) { throw 'fixture did not establish the Tuna projection' }
            Add-RouteRound 2 $true
            Assert-AdmissionRejected $tracker
            if ($null -ne $tracker.HeaderSwitchEvidence) { throw 'fixture unexpectedly injected switch-off' }

            # A later coordinator-published Tuna epoch supersedes the historical fallback.
            Add-RouteRound 3 $false
            $reactivated = New-TunaGuiFaultAdmissionTracker -Bookmark 0
            $ready = Wait-TunaGuiSwitchOffToggleOrTerminal -Window ([pscustomobject]@{}) -Bookmark 0 -AdmissionTracker $reactivated -TimeoutMs 100
            if ($ready.Kind -ne 'toggle' -or $reactivated.AdmissionState -ne 'ClickClaimed') { throw 'historical fallback blocked valid reactivation' }
            Add-RouteRound 4 $true
            $secondFallback = New-TunaGuiFaultAdmissionTracker -Bookmark 0
            Assert-AdmissionRejected $secondFallback

            # An incomplete/mismatched publication is not a paired authoritative fallback.
            $script:lines[29] = $script:lines[29].Replace('protocol_version=6', 'protocol_version=4')
            $incomplete = New-TunaGuiFaultAdmissionTracker -Bookmark 24
            [void](Update-TunaGuiFaultAdmissionTracker -Tracker $incomplete)
            if ($null -ne $incomplete.AuthoritativeFallbackProjection) { throw 'mismatched protocol was treated as coordinator fallback proof' }
            Assert-TunaGuiSwitchOffRouteCurrent -Tracker $incomplete

            # Initial Tuna publication has no transition commit of its own.
            $script:lines.Clear()
            foreach ($direction in @('outbound', 'inbound')) {
                $script:lines.Add("event=filetransfer_route_hint_authorized_publication_accepted; session_id=s; transfer_id=t; direction=$direction; log_writer_id=writer-$direction; route=file_tuna_v4; protocol_version=4; live_route_epoch=0; runtime_generation=1; terminal_generation=0; leg_generation=1; route_publication_generation=1; route_commit_generation=0")
            }
            $initial = New-TunaGuiFaultAdmissionTracker -Bookmark 0
            [void](Update-TunaGuiFaultAdmissionTracker -Tracker $initial)
            Add-RouteRound 1 $true
            Assert-AdmissionRejected $initial

            $cycle = $ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Invoke-FileTransferTunaHandoffFallbackCycle'}, $true).Extent.Text
            $guardIndex = $cycle.IndexOf('Assert-TunaGuiSwitchOffRouteCurrent -Tracker $faultAdmissionTracker')
            if ($guardIndex -lt 0 -or $guardIndex -gt $cycle.IndexOf('Reset-TunaGuiFaultAdmissionActionWindow')) {
                throw 'cycle discarded pre-fault route evidence before checking admission'
            }
            'PASS'
            """, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("PASS", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task PayerHandoffGui_MatrixRequiresHelperThenHelpeeWithPayloadBeforeBothFaults(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$RepoRoot)
            $ErrorActionPreference = 'Stop'
            Set-StrictMode -Version Latest
            function Read-ScriptAst([string]$Name) {
                $tokens = $null; $errors = $null
                $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot ('tools/' + $Name)), [ref]$tokens, [ref]$errors)
                if ($errors.Count) { throw ($errors | Out-String) }
                return $ast
            }
            $gui = Read-ScriptAst 'GuiSmoke-Windows.ps1'
            $wrapper = Read-ScriptAst 'Run-FileTransferTunaGuiSmoke.ps1'
            $acceptance = Read-ScriptAst 'Run-FileTransferRouteAcceptance.ps1'
            foreach ($name in @('New-Phase4RouteAcceptanceScenario', 'Get-Phase5RouteAcceptanceScenarios', 'Test-Phase5ScenarioRequiresIsolation')) {
                $definition = $acceptance.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name}, $true)
                Invoke-Expression $definition.Extent.Text
            }
            $scenarios = @(Get-Phase5RouteAcceptanceScenarios -BaselineManifest ([pscustomobject]@{Scenarios=@{}}))
            $scenario = @($scenarios | Where-Object Name -eq 'helper-to-helpee-payer-handoff-128mb')
            if ($scenarios.Count -ne 7 -or $scenario.Count -ne 1) { throw 'handoff matrix row missing or duplicated' }
            $scenario = $scenario[0]
            if ($scenario.PayerMode -cne 'helper-to-helpee' -or $scenario.RouteMode -cne 'live-multi-toggle' -or
                $scenario.LiveToggleSequence -cne 'off,on,off' -or $scenario.Fault -cne 'switch-off' -or
                $scenario.LiveProofMode -cne 'MultiToggle' -or $scenario.PayloadBytes -ne 134217728 -or
                ($scenario.ExpectedRouteChanges -join ',') -cne 'file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6' -or
                $null -ne $scenario.Baseline) { throw 'handoff contract changed' }
            if (-not (Test-Phase5ScenarioRequiresIsolation -Scenario $scenario)) { throw 'handoff isolation preflight missing' }
            foreach ($name in @('Get-TunaGuiLiveTogglePayer', 'Get-TunaGuiFaultTarget', 'Unlock-TunaPayers')) {
                $definition = $gui.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name}, $true)
                Invoke-Expression $definition.Extent.Text
            }
            $roles = @(0..2 | ForEach-Object { Get-TunaGuiLiveTogglePayer -PayerMode $scenario.PayerMode -StepIndex $_ })
            if (($roles -join ',') -cne 'helper,helpee,helpee') { throw 'toggle role sequence changed' }
            foreach ($mode in @('helpee', 'helper', 'both')) {
                if ((Get-TunaGuiLiveTogglePayer -PayerMode $mode -StepIndex 2) -cne $mode) { throw 'existing payer changed' }
            }
            if ((Get-TunaGuiFaultTarget -PayerMode $roles[0]) -cne 'helper' -or
                (Get-TunaGuiFaultTarget -PayerMode $roles[2]) -cne 'helpee') { throw 'fault targets changed' }
            $script:unlocks = [Collections.Generic.List[string]]::new()
            function Unlock-TunaFromSessionHeader { param($Process, $Window, $Password, $RoleLabel, $WaitForRuntimeEvidence) $script:unlocks.Add($RoleLabel) }
            $context = [pscustomobject]@{HelpeeProc=1; HelpeeWindow=2; HelperProc=3; HelperWindow=4}
            Unlock-TunaPayers -Context $context -PayerMode $scenario.PayerMode -Password 'fixture'
            Unlock-TunaPayers -Context $context -PayerMode $roles[1] -Password 'fixture'
            if (($script:unlocks -join ',') -cne 'helper,helpee') { throw 'wrong endpoint unlocked' }
            $cycle = $gui.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Invoke-FileTransferTunaHandoffFallbackCycle'}, $true)
            $body = $cycle.Extent.Text
            foreach ($required in @(
                'Invoke-TunaGuiFallbackFault -Context $Context -FaultMode $FaultMode -PayerMode $stepPayerMode',
                'Unlock-TunaPayers -Context $Context -PayerMode $nextPayerMode',
                'Try-PrearmTunaGuiSwitchOffToggle -Context $Context -PayerMode $stepPayerMode',
                "`$RouteMode -eq 'live-multi-toggle' -and `$PayerMode -ne 'helper-to-helpee'",
                'Wait-TunaGuiAcceleratedFilePayloadBeforeFault -Bookmark $progressBookmark -MinimumTotalPayloadBytes 1048576L',
                'Wait-TunaGuiLiveSwitchOffTransferProgressBeforeFault -Bookmark $bookmark')) {
                if (-not $body.Contains($required)) { throw ('handoff action or payload gate missing: ' + $required) }
            }
            if ($wrapper.ParamBlock.Extent.Text -notmatch '"helper-to-helpee"') { throw 'wrapper cannot select handoff' }
            $guard = @($wrapper.FindAll({param($n) $n -is [Management.Automation.Language.IfStatementAst] -and
                $n.Extent.Text.Contains('Helper-to-helpee payer handoff requires')}, $true))
            if ($guard.Count -ne 1) { throw 'wrapper handoff guard missing' }
            $PayerMode = 'helper-to-helpee'; $RouteMode = 'live-multi-toggle'; $Fault = 'switch-off'; $LiveToggleSequence = 'off,on,off'
            Invoke-Expression $guard[0].Extent.Text
            $RouteMode = 'preactivated'; $rejected = $false
            try { Invoke-Expression $guard[0].Extent.Text } catch { $rejected = $true }
            if (-not $rejected) { throw 'unsupported handoff mode admitted' }
            'PASS'
            """, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("PASS", result.Stdout, StringComparison.Ordinal);
    }
}

[Trait("Area", "Core")]
[Trait("FileTransferGuiSuite", "PayerHandoff")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferPayerHandoffAcceptanceTests : FileTransferAcceptancePhase5TestBase
{
    [Theory]
    [InlineData("helper-to-helpee-payer-handoff-128mb", "file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", false)]
    [InlineData("regular-v4-helper-to-helpee-payer-handoff-256mb", "regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", true)]
    public async Task PayerHandoffGui_FakeCampaignEnforcesRouteProofForSelectedScenario(string scenarioName, string routeSequence, bool missingActivationDelivery)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var artifacts = Path.Combine(root, "artifacts", "payer-handoff-20261009", "fake", Guid.NewGuid().ToString("N"));
        var result = await RunPowerShellFileAsync(root, Path.Combine(root, "tools", "Run-FileTransferRouteAcceptance.ps1"),
            ["-MatrixMode", "phase5-analyzer-gui-acceptance", "-ScenarioName", scenarioName,
             "-ArtifactRoot", artifacts, "-TimeoutSeconds", "30", "-ProgressTimeoutSeconds", "30",
             "-GoodputOnlyRerunLimit", "0", "-SetupOnlyRerunLimit", "0"], BuildFakeRouteAcceptanceEnvironment("phase5-pass"));
        var summary = ReadArtifactReport(Path.Combine(artifacts, "phase5-pass"), "phase5-analyzer-gui-acceptance-summary.txt");
        Assert.Equal("1", summary["run_count"]);
        Assert.Equal(routeSequence, summary[scenarioName + ".route_sequence"]);
        if (missingActivationDelivery)
        {
            // The generic fake fixture has route labels, not authenticated delivery
            // for the second activation. It must not qualify this reproduction.
            Assert.NotEqual(0, result.ExitCode);
            Assert.Equal("FAIL", summary["correctness_verdict"]);
            Assert.Equal("incomplete_proof", summary["runtime_correctness_verdict"]);
            Assert.Equal("filetransfer_tuna_activation_delivery_unproven", summary["runtime_failure_candidate"]);
            Assert.Equal("activation_2", summary["failure_variant"]);
            Assert.Equal("0", summary["current_runtime_proof"]);
            return;
        }
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Equal("PASS", summary["correctness_verdict"]);
        Assert.Equal("pass", summary[scenarioName + ".live_route_epoch_proof_verdict"]);
    }
}
