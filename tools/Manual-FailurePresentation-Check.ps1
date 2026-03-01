param([string]$ExePath = '.\src\nLink.App\bin\Release\net8.0\nLink.exe')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

function Wait-Until {
    param([int]$TimeoutMs,[int]$PollMs,[scriptblock]$Condition,[string]$OnTimeoutMessage)
    $sw=[System.Diagnostics.Stopwatch]::StartNew()
    while($sw.ElapsedMilliseconds -lt $TimeoutMs){
        try { $r=& $Condition; if($r){ return $r } } catch {}
        Start-Sleep -Milliseconds $PollMs
    }
    throw $OnTimeoutMessage
}

function Get-WindowElementByProcessId([int]$ProcessId){
    $root=[System.Windows.Automation.AutomationElement]::RootElement
    $procProp=[System.Windows.Automation.AutomationElement]::ProcessIdProperty
    $nameProp=[System.Windows.Automation.AutomationElement]::NameProperty
    $cond=New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($procProp,$ProcessId)),
        (New-Object System.Windows.Automation.PropertyCondition($nameProp,'nLink')))
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Children,$cond)
}

function Find-ByNameAndType($Root,[string]$Name,$ControlType){
    $nameProp=[System.Windows.Automation.AutomationElement]::NameProperty
    $typeProp=[System.Windows.Automation.AutomationElement]::ControlTypeProperty
    $cond=New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($nameProp,$Name)),
        (New-Object System.Windows.Automation.PropertyCondition($typeProp,$ControlType)))
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$cond)
}

function Find-AllByType($Root,$ControlType){
    $typeProp=[System.Windows.Automation.AutomationElement]::ControlTypeProperty
    $cond=New-Object System.Windows.Automation.PropertyCondition($typeProp,$ControlType)
    return $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants,$cond)
}

function Find-VisibleByAutomationId($Root,[string]$AutomationId){
    $idProp=[System.Windows.Automation.AutomationElement]::AutomationIdProperty
    $cond=New-Object System.Windows.Automation.PropertyCondition($idProp,$AutomationId)
    $all=$Root.FindAll([System.Windows.Automation.TreeScope]::Descendants,$cond)
    foreach($el in @($all)){ if($el -and -not $el.Current.IsOffscreen){ return $el } }
    return $null
}

function Find-VisibleByAutomationIdOrName($Root,[string]$AutomationId,[string]$FallbackName){
    $byId=Find-VisibleByAutomationId -Root $Root -AutomationId $AutomationId
    if($byId){ return $byId }
    $byName=Find-ByNameAndType -Root $Root -Name $FallbackName -ControlType ([System.Windows.Automation.ControlType]::Button)
    if($byName -and -not $byName.Current.IsOffscreen){ return $byName }
    return $null
}

function Click-Element($Element){
    if(-not $Element.Current.IsEnabled){ throw "Element disabled: $($Element.Current.Name)" }
    try {
        $pattern=$Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        ([System.Windows.Automation.InvokePattern]$pattern).Invoke(); return
    } catch {}
    $rect=$Element.Current.BoundingRectangle
    if($rect.IsEmpty){ throw 'Cannot click element without bounds.' }
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point([int]($rect.Left + $rect.Width/2),[int]($rect.Top + $rect.Height/2))
    [System.Windows.Forms.SendKeys]::SendWait(' ')
}

function Set-Text($Element,[string]$Text){
    try {
        $vp=$Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        ([System.Windows.Automation.ValuePattern]$vp).SetValue($Text)
        return
    } catch {}
    [System.Windows.Forms.SendKeys]::SendWait('^a')
    Start-Sleep -Milliseconds 80
    [System.Windows.Forms.SendKeys]::SendWait($Text)
}

function Wait-Window($Process){
    return Wait-Until -TimeoutMs 20000 -PollMs 200 -OnTimeoutMessage "Timeout waiting window pid=$($Process.Id)" -Condition { Get-WindowElementByProcessId -ProcessId $Process.Id }
}

function Click-HomeButton($Window,[string]$Text){
    $btn=Wait-Until -TimeoutMs 12000 -PollMs 200 -OnTimeoutMessage "Timeout waiting home button '$Text'" -Condition {
        $b=Find-ByNameAndType -Root $Window -Name $Text -ControlType ([System.Windows.Automation.ControlType]::Button)
        if($b -and -not $b.Current.IsOffscreen){ return $b }
        return $null
    }
    Click-Element $btn
}

function Try-ClickRoleButtonIfPresent($Window,[string]$RoleButtonText){
    $title=Find-ByNameAndType -Root $Window -Name 'Choose your role' -ControlType ([System.Windows.Automation.ControlType]::Text)
    if($title -and -not $title.Current.IsOffscreen){
        $btn=Find-ByNameAndType -Root $Window -Name $RoleButtonText -ControlType ([System.Windows.Automation.ControlType]::Button)
        if($btn -and -not $btn.Current.IsOffscreen -and $btn.Current.IsEnabled){ Click-Element $btn; return $true }
    }
    return $false
}

function Copy-HelpeeCode($HelpeeWindow){
    $copyBtn=Wait-Until -TimeoutMs 12000 -PollMs 200 -OnTimeoutMessage 'Timeout waiting copy code button' -Condition {
        Find-VisibleByAutomationIdOrName -Root $HelpeeWindow -AutomationId 'Helpee.CopyCode' -FallbackName 'Copy code'
    }
    Click-Element $copyBtn
    $raw=Wait-Until -TimeoutMs 5000 -PollMs 150 -OnTimeoutMessage 'Timeout waiting code on clipboard' -Condition {
        $txt=[string](Get-Clipboard)
        $m=[regex]::Match($txt,'\d{3}\s?\d{3}')
        if($m.Success){ return $m.Value }
        return $null
    }
    return ([string]$raw -replace '\D','')
}

function Enter-HelperCodeAndConnect($HelperWindow,[string]$Code){
    $input=Wait-Until -TimeoutMs 12000 -PollMs 200 -OnTimeoutMessage 'Timeout waiting helper code input' -Condition {
        $byId=Find-VisibleByAutomationId -Root $HelperWindow -AutomationId 'Helper.CodeInput'
        if($byId){ return $byId }
        $edits=Find-AllByType -Root $HelperWindow -ControlType ([System.Windows.Automation.ControlType]::Edit)
        foreach($e in @($edits)){ if(-not $e.Current.IsOffscreen){ return $e } }
        return $null
    }
    Set-Text -Element $input -Text $Code
    $connectBtn=Wait-Until -TimeoutMs 12000 -PollMs 200 -OnTimeoutMessage 'Timeout waiting connect button' -Condition {
        $b=Find-VisibleByAutomationIdOrName -Root $HelperWindow -AutomationId 'Helper.Connect' -FallbackName 'Connect'
        if($b -and $b.Current.IsEnabled){ return $b }
        return $null
    }
    Click-Element $connectBtn
}

function Wait-AllowButton($HelpeeWindow){
    return Wait-Until -TimeoutMs 25000 -PollMs 200 -OnTimeoutMessage 'Timeout waiting Allow button' -Condition {
        $b=Find-VisibleByAutomationIdOrName -Root $HelpeeWindow -AutomationId 'Helpee.Allow' -FallbackName 'Allow'
        if($b -and $b.Current.IsEnabled){ return $b }
        return $null
    }
}

function Wait-DeclineButton($HelpeeWindow){
    return Wait-Until -TimeoutMs 12000 -PollMs 200 -OnTimeoutMessage 'Timeout waiting Decline button' -Condition {
        $b=Find-VisibleByAutomationIdOrName -Root $HelpeeWindow -AutomationId 'Helpee.Decline' -FallbackName 'Decline'
        if($b -and $b.Current.IsEnabled){ return $b }
        return $null
    }
}

function Get-VisibleTextValues($Window){
    $texts=Find-AllByType -Root $Window -ControlType ([System.Windows.Automation.ControlType]::Text)
    $vals=New-Object System.Collections.Generic.List[string]
    foreach($t in @($texts)){
        if($t.Current.IsOffscreen){ continue }
        $n=[string]$t.Current.Name
        if([string]::IsNullOrWhiteSpace($n)){ continue }
        [void]$vals.Add($n)
    }
    return $vals
}

function Wait-TextContainsAny($Window,[string[]]$Tokens,[int]$TimeoutMs=30000){
    return Wait-Until -TimeoutMs $TimeoutMs -PollMs 250 -OnTimeoutMessage ("Timed out waiting for text tokens: " + ($Tokens -join ', ')) -Condition {
        $vals=Get-VisibleTextValues -Window $Window
        foreach($v in $vals){
            foreach($t in $Tokens){
                if($v.IndexOf($t,[System.StringComparison]::OrdinalIgnoreCase) -ge 0){ return $v }
            }
        }
        return $null
    }
}

function Start-HelpeeFlow($Exe){
    $p=Start-Process -FilePath $Exe -PassThru
    $w=Wait-Window -Process $p
    Click-HomeButton -Window $w -Text 'I need help'
    [void](Try-ClickRoleButtonIfPresent -Window $w -RoleButtonText 'I need help')
    return @{ Proc=$p; Window=$w }
}

function Start-HelperFlow($Exe){
    $p=Start-Process -FilePath $Exe -PassThru
    $w=Wait-Window -Process $p
    Click-HomeButton -Window $w -Text 'I want to help someone'
    [void](Try-ClickRoleButtonIfPresent -Window $w -RoleButtonText 'I want to help someone')
    return @{ Proc=$p; Window=$w }
}

function Stop-Flow($procs){
    foreach($p in @($procs)){ try { if($p -and -not $p.HasExited){ Stop-Process -Id $p.Id -Force } } catch {} }
}

$resolvedExe=(Resolve-Path $ExePath).Path
$oldTransport=$env:NLINK_TRANSPORT
$allOk=$true
$lines=New-Object System.Collections.Generic.List[string]

try{
    if([string]::IsNullOrWhiteSpace($env:NLINK_TRANSPORT)){ $env:NLINK_TRANSPORT='DEVLOCAL' }

    # Case 1: Rejected path
    $procs=@()
    try {
        $helpee=Start-HelpeeFlow -Exe $resolvedExe; $procs += $helpee.Proc
        $helper=Start-HelperFlow -Exe $resolvedExe; $procs += $helper.Proc
        $code=Copy-HelpeeCode -HelpeeWindow $helpee.Window
        Enter-HelperCodeAndConnect -HelperWindow $helper.Window -Code $code
        [void](Wait-AllowButton -HelpeeWindow $helpee.Window)
        $decline=Wait-DeclineButton -HelpeeWindow $helpee.Window
        Click-Element $decline
        $txt=Wait-TextContainsAny -Window $helper.Window -Tokens @('rejected','declined','request was rejected') -TimeoutMs 25000
        [void]$lines.Add("Rejected path helper text: $txt")
    }
    catch {
        $allOk=$false
        [void]$lines.Add("Rejected path FAILED: $($_.Exception.Message)")
    }
    finally { Stop-Flow -procs $procs }

    # Case 2: Helper failed timeout path (no allow)
    $procs=@()
    try {
        $helpee=Start-HelpeeFlow -Exe $resolvedExe; $procs += $helpee.Proc
        $helper=Start-HelperFlow -Exe $resolvedExe; $procs += $helper.Proc
        $code=Copy-HelpeeCode -HelpeeWindow $helpee.Window
        Enter-HelperCodeAndConnect -HelperWindow $helper.Window -Code $code
        [void](Wait-AllowButton -HelpeeWindow $helpee.Window)
        $txt=Wait-TextContainsAny -Window $helper.Window -Tokens @('Connection failed','couldn''t connect','did not respond','connection lost','retry','connection problem','session ended') -TimeoutMs 50000
        [void]$lines.Add("Helper failed/disconnected path text: $txt")
    }
    catch {
        $allOk=$false
        [void]$lines.Add("Helper failed/disconnected path FAILED: $($_.Exception.Message)")
    }
    finally { Stop-Flow -procs $procs }

    # Case 3: Helpee disconnect path (kill helper after connected)
    $procs=@()
    try {
        $helpee=Start-HelpeeFlow -Exe $resolvedExe; $procs += $helpee.Proc
        $helper=Start-HelperFlow -Exe $resolvedExe; $procs += $helper.Proc
        $code=Copy-HelpeeCode -HelpeeWindow $helpee.Window
        Enter-HelperCodeAndConnect -HelperWindow $helper.Window -Code $code
        $allow=Wait-AllowButton -HelpeeWindow $helpee.Window
        Click-Element $allow
        [void](Wait-TextContainsAny -Window $helper.Window -Tokens @('Connected') -TimeoutMs 12000)
        Stop-Process -Id $helper.Proc.Id -Force
        $txt=Wait-TextContainsAny -Window $helpee.Window -Tokens @('Connection lost','Waiting for helper','couldn''t connect','failed','other side ended','session ended') -TimeoutMs 30000
        [void]$lines.Add("Helpee failed/disconnected path text: $txt")
    }
    catch {
        $allOk=$false
        [void]$lines.Add("Helpee failed/disconnected path FAILED: $($_.Exception.Message)")
    }
    finally { Stop-Flow -procs $procs }

    foreach($line in $lines){ Write-Host "[FailureManual] $line" }
    if(-not $allOk){ exit 1 }
    Write-Host '[FailureManual] PASS' -ForegroundColor Green
}
finally {
    if($null -eq $oldTransport){ Remove-Item Env:NLINK_TRANSPORT -ErrorAction SilentlyContinue } else { $env:NLINK_TRANSPORT=$oldTransport }
}
