param([string]$ExePath = '.\\src\\nLink.App\\bin\\Release\\net8.0\\nLink.exe')

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
    return Wait-Until -TimeoutMs 15000 -PollMs 200 -OnTimeoutMessage "Timeout waiting window pid=$($Process.Id)" -Condition { Get-WindowElementByProcessId -ProcessId $Process.Id }
}

function Click-HomeButton($Window,[string]$Text){
    $btn=Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage "Timeout waiting home button '$Text'" -Condition {
        $b=Find-ByNameAndType -Root $Window -Name $Text -ControlType ([System.Windows.Automation.ControlType]::Button)
        if($b -and -not $b.Current.IsOffscreen){ return $b }; return $null
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
    $copyBtn=Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage 'Timeout waiting copy code button' -Condition {
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
    $input=Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage 'Timeout waiting helper code input' -Condition {
        $byId=Find-VisibleByAutomationId -Root $HelperWindow -AutomationId 'Helper.CodeInput'
        if($byId){ return $byId }
        $edits=Find-AllByType -Root $HelperWindow -ControlType ([System.Windows.Automation.ControlType]::Edit)
        foreach($e in @($edits)){ if(-not $e.Current.IsOffscreen){ return $e } }
        return $null
    }
    Set-Text -Element $input -Text $Code
    $connectBtn=Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage 'Timeout waiting connect button' -Condition {
        $b=Find-VisibleByAutomationIdOrName -Root $HelperWindow -AutomationId 'Helper.Connect' -FallbackName 'Connect'
        if($b -and $b.Current.IsEnabled){ return $b }; return $null
    }
    Click-Element $connectBtn
}

function Wait-AllowButton($HelpeeWindow){
    return Wait-Until -TimeoutMs 25000 -PollMs 200 -OnTimeoutMessage 'Timeout waiting helpee Allow button' -Condition {
        $b=Find-VisibleByAutomationIdOrName -Root $HelpeeWindow -AutomationId 'Helpee.Allow' -FallbackName 'Allow'
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

function Find-RecoveryText($Window){
    $tokens=@('Connection lost. Reconnecting','Connection failed. You can retry','Retry available in')
    $vals=Get-VisibleTextValues -Window $Window
    foreach($v in $vals){
        foreach($t in $tokens){
            if($v.IndexOf($t,[System.StringComparison]::OrdinalIgnoreCase) -ge 0){
                return $v
            }
        }
    }
    return $null
}

function Has-RecoveryText($Window){ return [bool](Find-RecoveryText -Window $Window) }

$resolvedExe=(Resolve-Path $ExePath).Path
$procs=@()
try{
    if([string]::IsNullOrWhiteSpace($env:NLINK_TRANSPORT)){ $env:NLINK_TRANSPORT='DEVLOCAL' }

    # Check 1 + 2: fail path transient text + no-spam toggle behavior
    $helpee=Start-Process -FilePath $resolvedExe -PassThru; $procs += $helpee
    $helpeeWindow=Wait-Window -Process $helpee
    Click-HomeButton -Window $helpeeWindow -Text 'I need help'
    [void](Try-ClickRoleButtonIfPresent -Window $helpeeWindow -RoleButtonText 'I need help')

    $helper=Start-Process -FilePath $resolvedExe -PassThru; $procs += $helper
    $helperWindow=Wait-Window -Process $helper
    Click-HomeButton -Window $helperWindow -Text 'I want to help someone'
    [void](Try-ClickRoleButtonIfPresent -Window $helperWindow -RoleButtonText 'I want to help someone')

    $code=Copy-HelpeeCode -HelpeeWindow $helpeeWindow
    Enter-HelperCodeAndConnect -HelperWindow $helperWindow -Code $code
    [void](Wait-AllowButton -HelpeeWindow $helpeeWindow) # intentionally do not click allow

    $recoveryText=Wait-Until -TimeoutMs 45000 -PollMs 250 -OnTimeoutMessage 'Transient recovery text not observed on fail/disconnect path.' -Condition {
        Find-RecoveryText -Window $helperWindow
    }

    $sw=[System.Diagnostics.Stopwatch]::StartNew()
    $lastPresent=$false
    $toggles=0
    while($sw.ElapsedMilliseconds -lt 8000){
        $present=Has-RecoveryText -Window $helperWindow
        if($present -ne $lastPresent){ $toggles++; $lastPresent=$present }
        Start-Sleep -Milliseconds 250
    }

    # Check 3: connected flow should hide transient recovery text
    foreach($p in @($procs)){ try { if($p -and -not $p.HasExited){ Stop-Process -Id $p.Id -Force } } catch {} }
    $procs=@()

    $helpee2=Start-Process -FilePath $resolvedExe -PassThru; $procs += $helpee2
    $helpeeWindow2=Wait-Window -Process $helpee2
    Click-HomeButton -Window $helpeeWindow2 -Text 'I need help'
    [void](Try-ClickRoleButtonIfPresent -Window $helpeeWindow2 -RoleButtonText 'I need help')

    $helper2=Start-Process -FilePath $resolvedExe -PassThru; $procs += $helper2
    $helperWindow2=Wait-Window -Process $helper2
    Click-HomeButton -Window $helperWindow2 -Text 'I want to help someone'
    [void](Try-ClickRoleButtonIfPresent -Window $helperWindow2 -RoleButtonText 'I want to help someone')

    $code2=Copy-HelpeeCode -HelpeeWindow $helpeeWindow2
    Enter-HelperCodeAndConnect -HelperWindow $helperWindow2 -Code $code2
    $allow2=Wait-AllowButton -HelpeeWindow $helpeeWindow2
    Click-Element $allow2

    [void](Wait-Until -TimeoutMs 12000 -PollMs 200 -OnTimeoutMessage 'Timeout waiting connected state text.' -Condition {
        $vals=Get-VisibleTextValues -Window $helperWindow2
        foreach($v in $vals){ if($v -eq 'Connected'){ return $true } }
        return $null
    })

    $helperHasTransient=Has-RecoveryText -Window $helperWindow2
    $helpeeHasTransient=Has-RecoveryText -Window $helpeeWindow2

    Write-Host "[RecoveryCheck] FAIL_PATH_TRANSIENT_TEXT: PASS => $recoveryText" -ForegroundColor Green
    Write-Host "[RecoveryCheck] FAIL_PATH_NO_SPAM_TOGGLE_COUNT: $toggles (<=3 expected)" -ForegroundColor Green
    Write-Host "[RecoveryCheck] CONNECTED_HIDES_TRANSIENT_HELPER: $([bool](-not $helperHasTransient))" -ForegroundColor Green
    Write-Host "[RecoveryCheck] CONNECTED_HIDES_TRANSIENT_HELPERPEE: $([bool](-not $helpeeHasTransient))" -ForegroundColor Green

    if($toggles -gt 3){ throw "Transient banner appears to toggle excessively (toggles=$toggles)." }
    if($helperHasTransient -or $helpeeHasTransient){ throw 'Transient recovery text still visible after connected.' }
}
finally{
    foreach($p in @($procs)){ try { if($p -and -not $p.HasExited){ Stop-Process -Id $p.Id -Force } } catch {} }
}
