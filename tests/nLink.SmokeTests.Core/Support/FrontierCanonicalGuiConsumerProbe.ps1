param([string]$ExePath,[int]$TimeoutSeconds)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$RepoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$Directory=$env:NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
. (Join-Path $PSScriptRoot 'FrontierAttemptDispatchProjection.ps1')
$ast=Read-AttemptAst (Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1')
foreach($name in @('Get-GuiSmokeFrontierLaunchOwner','Open-GuiSmokeCanonicalAttempt','Invoke-Scenario')) {
    Invoke-Expression (Get-AttemptFunction $ast $name)
}
$private:owner=Get-GuiSmokeFrontierLaunchOwner
$case=$env:NLINK_TEST_CANONICAL_GUI_DISPATCH
if(($null -eq $owner) -ne ($case -ceq 'disabled')) {throw 'Original GUI opt-in changed'}
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$private:ticket=$null
try {
    if($null -ne [NLink.Tools.LocalGuiWriterRequest].GetMethod('TryBindAttemptForTest') -or
        $null -ne [NLink.Tools.LocalGuiWriterRequest+FrontierLaunchOwner].GetMethod('TryCreateAttemptRoleRequestForTest')) {throw 'Fixture API loaded in production chain'}
    if($case -ceq 'gui-environment') {$env:NLINK_FILETRANSFER_SOAK_PROGRESS_TIMEOUT_SECONDS='31'}
    $private:projection=[string[]]@($ExePath,$env:NLINK_FILETRANSFER_SOAK_DIRECTION,$env:NLINK_TUNA_GUI_PAYER_MODE,
        $env:NLINK_TUNA_GUI_FAULT,$env:NLINK_TUNA_GUI_ROUTE_MODE,$env:NLINK_FILETRANSFER_SOAK_PAYLOAD_SIZES,
        $env:NLINK_TUNA_GUI_LIVE_MULTI_TOGGLE_SEQUENCE,[string]$TimeoutSeconds,
        $env:NLINK_FILETRANSFER_SOAK_PROGRESS_TIMEOUT_SECONDS,'RegularActivationCycle',
        (Get-AttemptPhase $env:NLINK_TUNA_GUI_ROUTE_MODE),(Get-Process -Id $PID).Path)
    if($case -ceq 'enabled') {
        for($i=0;$i -lt $projection.Length;$i++) {
            $changed=[string[]]$projection.Clone(); $changed[$i]+='-changed'
            if($i -cin @(7,8)) {$changed[$i]=[string]([int]$projection[$i]+1)}
            if($null -ne $owner.TryOpenCanonicalAttemptDispatch(1,$changed)) {throw ('Changed projection admitted: '+$i)}
        }
        if($null -ne $owner.TryOpenCanonicalAttemptDispatch(2,$projection)) {throw 'Repeated attempt admitted'}
        $private:copy=[object].GetMethod('MemberwiseClone',$flags).Invoke($owner,$null)
        if($null -ne (Open-GuiSmokeCanonicalAttempt -FrontierOwner $copy -ExePath $ExePath -TimeoutSeconds $TimeoutSeconds)) {throw 'Copied owner admitted'}
        $copy.Dispose()
        if(-not $owner.IsCurrent) {throw 'Copied owner retired original'}
    }
    # Execute the actual guarded entry block, without UI, log deletion or apps.
    $blocks=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.IfStatementAst] -and
        $n.Extent.Text.StartsWith('if ($scenarioList.Count -eq 1') -and $n.Extent.Text.Contains('Open-GuiSmokeCanonicalAttempt')},$true))
    if($blocks.Count -ne 1) {throw 'Original GUI consumption entry missing'}
    $private:guiFrontierOwner=$owner; $private:guiCanonicalAttempt=$null
    $ctx=[pscustomobject]@{ExePath=$ExePath}
    $scenarioList=@('FILETRANSFER_TUNA_HANDOFF_FALLBACK','A')
    Invoke-Expression $blocks[0].Extent.Text
    if($null -ne $guiCanonicalAttempt) {throw 'Multiple scenarios consumed attempt'}
    $scenarioList=@('FILETRANSFER_TUNA_HANDOFF_FALLBACK')
    if($case -ceq 'unconsumed-ticket') {
        $ticket=$owner.TryOpenCanonicalAttemptDispatch(1,$projection)
        if($null -eq $ticket) {throw 'Unconsumed original ticket missing'}
    }
    Invoke-Expression $blocks[0].Extent.Text
    $consumed=$null -ne $guiCanonicalAttempt
    if($case -ceq 'unconsumed-ticket') {$guiCanonicalAttempt=$ticket}
    else {$ticket=$guiCanonicalAttempt}
    if($consumed -ne ($case -cin @('enabled','gui-nonzero','copied-ticket','retired-ticket','parent-cancelled'))) {throw 'Actual GUI environment admitted a different attempt'}
    if($consumed) {
        $private:copy=[object].GetMethod('MemberwiseClone',$flags).Invoke($ticket,$null)
        if($copy.IsCurrent -or $null -ne $copy.TryConsume($owner,1,$projection) -or
            $null -ne $copy.TryTakeScenarioOwner()) {throw 'Copied dispatch admitted'}
        $copy.Dispose()
        if(-not $ticket.IsCurrent -or $null -ne $ticket.TryConsume($owner,1,$projection) -or
            $null -ne (Open-GuiSmokeCanonicalAttempt -FrontierOwner $owner -ExePath $ExePath -TimeoutSeconds $TimeoutSeconds)) {throw 'One-shot dispatch changed'}
        if($owner.LiveProofEligible -or $owner.NativeWriterCustodyEstablished -or $owner.PublicationCompletionIssued) {throw 'Attempt granted authority'}
    }
    if($case -ceq 'copied-ticket') {$guiCanonicalAttempt=$copy}
    if($case -ceq 'retired-ticket') {$ticket.Dispose()}
    if($case -ceq 'parent-cancelled') {[void]$ticket.GetType().GetField('originClosed',$flags).GetValue($ticket).Set()}
    $calls=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.CommandAst] -and
        $n.GetCommandName() -ceq 'Invoke-Scenario' -and $n.Extent.Text.StartsWith("Invoke-Scenario -Name 'filetransfer_tuna_handoff_fallback'")},$true))
    if($calls.Count -ne 1 -or -not $calls[0].Extent.Text.Contains('-CanonicalAttempt $guiCanonicalAttempt') -or
        -not $calls[0].Extent.Text.Contains('$CanonicalAttempt.TryTakeScenarioOwner()')) {throw 'Guarded top-level dispatch missing'}
    # Execute the production runner and its actual action, replacing only the
    # scenario body with assertions. No UI, network, log deletion or apps run.
    $script:expectedOwner=$owner; $script:expectedTicket=$ticket
    $script:expectOwned=$case -cin @('enabled','gui-nonzero')
    $scenarioOwned=$script:expectOwned
    $script:scenarioCalls=0; $script:deadlineCalls=0
    $script:GuiSmokeScenarioDeadlineUtc=[DateTimeOffset]::MaxValue
    $script:GuiSmokeScenarioDeadlineName='outer'
    $script:GuiSmokeTerminalPhaseGeneration=7L
    function Assert-PrivateDispatchInputs {
        foreach($name in @('owner','ticket','copy','projection','guiFrontierOwner','guiCanonicalAttempt',
            'CanonicalAttempt','FrontierOwner','scenarioFrontierOwner')) {
            if($null -ne (Get-Variable $name -ErrorAction SilentlyContinue)) {throw ('Dispatch capability leaked: '+$name)}
        }
    }
    function Assert-GuiSmokeScenarioDeadline {Assert-PrivateDispatchInputs; $script:deadlineCalls++}
    function Run-ScenarioFileTransferTunaHandoffFallback {
        param($Context,$FrontierOwner,$CanonicalAttempt)
        Set-Variable -Name FrontierOwner,CanonicalAttempt -Option Private
        if($script:expectOwned) {
            if(-not [object]::ReferenceEquals($FrontierOwner,$script:expectedOwner) -or
                -not [object]::ReferenceEquals($CanonicalAttempt,$script:expectedTicket)) {throw 'Original pair not dispatched'}
        } elseif($null -ne $FrontierOwner -or $null -ne $CanonicalAttempt) {throw 'Rejected attempt reached scenario'}
        if(@($Context.PSObject.Properties).Count -ne 1 -or
            $Context.PSObject.Properties.Name -cne 'ExePath') {throw 'Scenario context carries ownership'}
        Assert-PrivateDispatchInputs
        $script:scenarioCalls++
    }
    Invoke-Expression $calls[0].Extent.Text
    $script:expectOwned=$false
    Invoke-Expression $calls[0].Extent.Text
    if($script:scenarioCalls -ne 2 -or $script:deadlineCalls -ne 2 -or
        $script:GuiSmokeScenarioDeadlineUtc -ne [DateTimeOffset]::MaxValue -or
        $script:GuiSmokeScenarioDeadlineName -cne 'outer' -or
        $script:GuiSmokeTerminalPhaseGeneration -ne 7L) {throw 'Scenario outcome or deadline changed'}
    if($case -ceq 'copied-ticket' -and -not [object]::ReferenceEquals($ticket.TryTakeScenarioOwner(),$owner)) {throw 'Copied dispatch spent original ticket'}
    if($case -ceq 'unconsumed-ticket') {
        if(-not [object]::ReferenceEquals($ticket.TryConsume($owner,1,$projection),$owner) -or
            -not [object]::ReferenceEquals($ticket.TryTakeScenarioOwner(),$owner)) {throw 'Unconsumed rejection spent original ticket'}
    }
    if($null -ne $ticket -and $null -ne $ticket.TryTakeScenarioOwner()) {throw 'Scenario dispatch was repeatable'}
    if($null -ne $owner -and ($owner.LiveProofEligible -or $owner.NativeWriterCustodyEstablished -or $owner.PublicationCompletionIssued)) {throw 'Dispatch granted authority'}
    if($null -ne $owner) {$owner.Dispose()}
    if($null -ne $ticket -and ($ticket.IsCurrent -or $ticket.GetType().GetField('disposed',$flags).GetValue($ticket) -ne 1)) {throw 'Owner cleanup retained attempt handles'}
    [ordered]@{Pid=$PID;Consumed=$consumed;ScenarioOwned=$scenarioOwned;ScenarioCalls=$script:scenarioCalls;
        ReplayRejected=$true;PrivateInputs=$true;Retired=$true} | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $Directory 'canonical-gui-consumer.json') -Encoding UTF8
} finally {
    if($null -ne $ticket) {$ticket.Dispose()}
    if($null -ne $owner) {$owner.Dispose()}
}
if($case -ceq 'gui-nonzero') {exit 7}
