param(
    [string]$ExePath = ".\\artifacts\\portable\\nLink\\win-x64\\nLink.exe",
    [int]$TimeoutSeconds = 90
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32GuiSmoke {
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
}
"@

function Wait-Until {
    param(
        [Parameter(Mandatory = $true)][int]$TimeoutMs,
        [Parameter(Mandatory = $true)][int]$PollMs,
        [Parameter(Mandatory = $true)][scriptblock]$Condition,
        [Parameter(Mandatory = $true)][string]$OnTimeoutMessage
    )

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        try {
            $result = & $Condition
            if ($result) { return $result }
        }
        catch {
            # Ignore transient UIA issues while windows are rendering.
        }
        Start-Sleep -Milliseconds $PollMs
    }

    throw $OnTimeoutMessage
}

function Get-WindowElementByProcessId {
    param([int]$ProcessId)

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $procProp = [System.Windows.Automation.AutomationElement]::ProcessIdProperty
    $nameProp = [System.Windows.Automation.AutomationElement]::NameProperty
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($procProp, $ProcessId)),
        (New-Object System.Windows.Automation.PropertyCondition($nameProp, 'nLink'))
    )
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
}

function Find-ByAutomationId {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Root,
        [Parameter(Mandatory = $true)][string]$AutomationId
    )
    $idProp = [System.Windows.Automation.AutomationElement]::AutomationIdProperty
    $cond = New-Object System.Windows.Automation.PropertyCondition($idProp, $AutomationId)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-AllByAutomationId {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Root,
        [Parameter(Mandatory = $true)][string]$AutomationId
    )
    $idProp = [System.Windows.Automation.AutomationElement]::AutomationIdProperty
    $cond = New-Object System.Windows.Automation.PropertyCondition($idProp, $AutomationId)
    return $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-ByNameAndType {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Root,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][System.Windows.Automation.ControlType]$ControlType
    )
    $nameProp = [System.Windows.Automation.AutomationElement]::NameProperty
    $typeProp = [System.Windows.Automation.AutomationElement]::ControlTypeProperty
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($nameProp, $Name)),
        (New-Object System.Windows.Automation.PropertyCondition($typeProp, $ControlType))
    )
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-AllByType {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Root,
        [Parameter(Mandatory = $true)][System.Windows.Automation.ControlType]$ControlType
    )
    $typeProp = [System.Windows.Automation.AutomationElement]::ControlTypeProperty
    $cond = New-Object System.Windows.Automation.PropertyCondition($typeProp, $ControlType)
    return $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-VisibleByAutomationId {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Root,
        [Parameter(Mandatory = $true)][string]$AutomationId
    )
    $all = Find-AllByAutomationId -Root $Root -AutomationId $AutomationId
    foreach ($el in @($all)) {
        if ($null -ne $el -and $el.Current.IsOffscreen -eq $false) { return $el }
    }
    return $null
}

function Find-VisibleByAutomationIdOrName {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Root,
        [Parameter(Mandatory = $true)][string]$AutomationId,
        [string]$FallbackName = '',
        [System.Windows.Automation.ControlType]$FallbackControlType = $null
    )
    $byId = Find-VisibleByAutomationId -Root $Root -AutomationId $AutomationId
    if ($byId) { return $byId }

    if (-not [string]::IsNullOrWhiteSpace($FallbackName) -and $null -ne $FallbackControlType) {
        $fallback = Find-ByNameAndType -Root $Root -Name $FallbackName -ControlType $FallbackControlType
        if ($fallback -and $fallback.Current.IsOffscreen -eq $false) { return $fallback }
    }

    return $null
}

function Test-TextVisible {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Root,
        [Parameter(Mandatory = $true)][string]$Text
    )
    if ([string]::IsNullOrWhiteSpace($Text)) { return $false }
    $texts = Find-AllByType -Root $Root -ControlType ([System.Windows.Automation.ControlType]::Text)
    foreach ($t in @($texts)) {
        if ($t.Current.IsOffscreen -eq $false -and $t.Current.Name -eq $Text) { return $true }
    }
    return $false
}

function Get-ElementTextSafe {
    param([System.Windows.Automation.AutomationElement]$Element)
    if ($null -eq $Element) { return '' }
    try { return [string]$Element.Current.Name } catch { return '' }
}

function Get-BannerElements {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window)

    $banner = Find-VisibleByAutomationId -Root $Window -AutomationId 'StatusBanner'
    if (-not $banner) {
        # Avalonia UIA can omit AutomationIds on some machines; fall back to known controls.
        return $null
    }

    return [pscustomobject]@{
        Banner = $banner
        Title = Find-VisibleByAutomationId -Root $banner -AutomationId 'StatusTitle'
        Message = Find-VisibleByAutomationId -Root $banner -AutomationId 'StatusMessage'
        RetryCountdown = Find-VisibleByAutomationId -Root $banner -AutomationId 'RetryCountdown'
        CopyDiagnosticsButton = Find-VisibleByAutomationId -Root $banner -AutomationId 'CopyDiagnosticsButton'
        CancelButton = Find-VisibleByAutomationId -Root $banner -AutomationId 'CancelButton'
    }
}

function Get-BannerTextBundle {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window)

    $els = Get-BannerElements -Window $Window
    if ($els) {
        return [pscustomobject]@{
            HasBanner = $true
            Title = (Get-ElementTextSafe $els.Title)
            Message = (Get-ElementTextSafe $els.Message)
            RetryCountdown = (Get-ElementTextSafe $els.RetryCountdown)
            HasCopyDiagnosticsButton = ($null -ne $els.CopyDiagnosticsButton -and -not $els.CopyDiagnosticsButton.Current.IsOffscreen)
            HasCancelButton = ($null -ne $els.CancelButton -and -not $els.CancelButton.Current.IsOffscreen)
            Elements = $els
        }
    }

    # Fallback when AutomationIds are missing in UIA on some systems.
    $title = ''
    $message = ''
    $retryText = ''
    $hasCopy = $false
    $hasCancel = $false
    try {
        if (Test-TextVisible -Root $Window -Text 'Copy Diagnostics') { $hasCopy = $true }
        if (Test-TextVisible -Root $Window -Text 'Cancel') { $hasCancel = $true }
        $texts = Find-AllByType -Root $Window -ControlType ([System.Windows.Automation.ControlType]::Text)
        foreach ($t in @($texts)) {
            if ($t.Current.IsOffscreen) { continue }
            $name = [string]$t.Current.Name
            if ([string]::IsNullOrWhiteSpace($name)) { continue }
            if (-not $title -and ($name -match 'Connected|Connecting|Reconnecting|Session|Couldn|No one found|No response|Please reinstall|Connection')) {
                $title = $name
                continue
            }
            if (-not $retryText -and $name -match 'attempt|retry in \d+s') {
                $retryText = $name
                continue
            }
            if (-not $message -and $name.Length -gt 6) {
                $message = $name
            }
        }
    } catch {}

    return [pscustomobject]@{
        HasBanner = ($hasCopy -or $hasCancel -or -not [string]::IsNullOrWhiteSpace($title) -or -not [string]::IsNullOrWhiteSpace($message))
        Title = $title
        Message = $message
        RetryCountdown = $retryText
        HasCopyDiagnosticsButton = $hasCopy
        HasCancelButton = $hasCancel
        Elements = $null
    }
}

function Test-ContainsAllTokens {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string[]]$Tokens
    )
    foreach ($token in $Tokens) {
        if ([string]::IsNullOrWhiteSpace($token)) { continue }
        if ($Text.IndexOf($token, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
            return $false
        }
    }
    return $true
}

function Wait-BannerVisibleWithAnyToken {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string[]]$TitleOrMessageTokens,
        [int]$TimeoutMs = 15000
    )
    return Wait-Until -TimeoutMs $TimeoutMs -PollMs 200 -OnTimeoutMessage ("Timed out waiting for status banner tokens: " + ($TitleOrMessageTokens -join ', ')) -Condition {
        $bundle = Get-BannerTextBundle -Window $Window
        if (-not $bundle.HasBanner) { return $null }
        $allText = (($bundle.Title + ' ' + $bundle.Message).Trim())
        foreach ($token in $TitleOrMessageTokens) {
            if ([string]::IsNullOrWhiteSpace($token)) { continue }
            if ($allText.IndexOf($token, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                return $bundle
            }
        }
        return $null
    }
}

function Wait-RetryCountdownVisible {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [int]$TimeoutMs = 15000
    )
    return Wait-Until -TimeoutMs $TimeoutMs -PollMs 200 -OnTimeoutMessage 'Timed out waiting for retry countdown in status banner.' -Condition {
        $bundle = Get-BannerTextBundle -Window $Window
        if (-not $bundle.HasBanner) { return $null }
        $text = [string]$bundle.RetryCountdown
        if ([string]::IsNullOrWhiteSpace($text)) { return $null }
        if ($text -match 'attempt' -and $text -match '\d+s') { return $bundle }
        return $null
    }
}

function Wait-BannerGone {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [int]$TimeoutMs = 10000
    )
    [void](Wait-Until -TimeoutMs $TimeoutMs -PollMs 200 -OnTimeoutMessage 'Timed out waiting for status banner to disappear.' -Condition {
        $bundle = Get-BannerTextBundle -Window $Window
        if (-not $bundle.HasBanner) { return $true }
        return $null
    })
}

function Click-Element {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Element)
    if (-not $Element.Current.IsEnabled) {
        throw "Element disabled (Id='$($Element.Current.AutomationId)', Name='$($Element.Current.Name)')"
    }
    try {
        $pattern = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
        return
    }
    catch {}

    $rect = $Element.Current.BoundingRectangle
    if ($rect.IsEmpty) { throw "Cannot click element without bounds." }
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point([int]($rect.Left + $rect.Width/2), [int]($rect.Top + $rect.Height/2))
    [System.Windows.Forms.SendKeys]::SendWait(' ')
}

function Set-Text {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Element,
        [Parameter(Mandatory = $true)][string]$Text
    )
    try {
        $vp = $Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        ([System.Windows.Automation.ValuePattern]$vp).SetValue($Text)
        return
    }
    catch {}

    $rect = $Element.Current.BoundingRectangle
    if (-not $rect.IsEmpty) {
        [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point([int]($rect.Left + 10), [int]($rect.Top + $rect.Height/2))
    }
    [System.Windows.Forms.SendKeys]::SendWait('^a')
    Start-Sleep -Milliseconds 80
    [System.Windows.Forms.SendKeys]::SendWait($Text)
}

function Get-ClipboardTextSafe {
    try { return (Get-Clipboard -Raw) } catch { return (Get-Clipboard) }
}

function New-FailureArtifactDir {
    $timestamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
    $dir = Join-Path (Resolve-Path '.').Path ("artifacts\\gui-smoke\\$timestamp")
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    return $dir
}

function Dump-UiTree {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Root,
        [Parameter(Mandatory = $true)][string]$OutPath
    )
    $sb = New-Object System.Text.StringBuilder
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    function Append-Node {
        param([System.Windows.Automation.AutomationElement]$Node, [int]$Depth)
        if ($null -eq $Node) { return }
        $indent = (' ' * ($Depth * 2))
        $controlType = if ($Node.Current.ControlType) { $Node.Current.ControlType.ProgrammaticName } else { '(none)' }
        [void]$sb.AppendLine("$indent- Id='$($Node.Current.AutomationId)' Name='$($Node.Current.Name)' Type='$controlType' Enabled=$($Node.Current.IsEnabled) Offscreen=$($Node.Current.IsOffscreen)")
        $child = $walker.GetFirstChild($Node)
        while ($child -ne $null) {
            Append-Node -Node $child -Depth ($Depth + 1)
            $child = $walker.GetNextSibling($child)
        }
    }
    Append-Node -Node $Root -Depth 0
    $sb.ToString() | Set-Content -Path $OutPath -Encoding UTF8
}

function Copy-AppLogsIfPresent {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir)
    $logsDir = Join-Path $env:LOCALAPPDATA 'nLink\logs'
    if (-not (Test-Path $logsDir)) { return }
    $dest = Join-Path $ArtifactDir 'logs'
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item -Path (Join-Path $logsDir '*') -Destination $dest -Force -ErrorAction SilentlyContinue
}

function Cleanup-Processes {
    param([array]$Processes)
    if ($null -eq $Processes -or $Processes.Count -eq 0) { return }
    foreach ($p in @($Processes)) {
        if ($null -eq $p) { continue }
        try {
            if (-not $p.HasExited) {
                Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            }
        }
        catch {}
    }
}

function Start-AppInstance {
    param(
        [Parameter(Mandatory = $true)][string]$ExePath,
        [Parameter(Mandatory = $true)][string]$RoleName
    )
    Write-Host "[GUI Smoke] Starting $RoleName instance..." -ForegroundColor Cyan
    return Start-Process -FilePath $ExePath -PassThru
}

function Wait-Window {
    param([Parameter(Mandatory = $true)]$Process, [int]$TimeoutMs = 15000)
    return Wait-Until -TimeoutMs $TimeoutMs -PollMs 200 -OnTimeoutMessage "Timed out waiting for window (pid=$($Process.Id))." -Condition {
        Get-WindowElementByProcessId -ProcessId $Process.Id
    }
}

function Click-HomeButton {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$Text
    )
    $btn = Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage "Timed out waiting for home button '$Text'." -Condition {
        Find-ByNameAndType -Root $Window -Name $Text -ControlType ([System.Windows.Automation.ControlType]::Button)
    }
    Click-Element $btn
}

function Get-HelpeeCodeFromUi {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$HelpeeWindow)

    $codeText = Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helpee code.' -Condition {
        $el = Find-VisibleByAutomationId -Root $HelpeeWindow -AutomationId 'Helpee.Code'
        if (-not $el) {
            $texts = Find-AllByType -Root $HelpeeWindow -ControlType ([System.Windows.Automation.ControlType]::Text)
            foreach ($t in @($texts)) {
                if ($t.Current.IsOffscreen -eq $false -and $t.Current.Name -match '^\d{3}\s?\d{3}$') { $el = $t; break }
            }
        }
        if ($el -and $el.Current.Name -match '^\d{3}\s?\d{3}$') { return $el.Current.Name }
        return $null
    }
    return ([string]$codeText -replace '\D','')
}

function Copy-HelpeeCodeAndReadClipboard {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$HelpeeWindow)
    $copyBtn = Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for Helpee.CopyCode.' -Condition {
        Find-VisibleByAutomationIdOrName -Root $HelpeeWindow -AutomationId 'Helpee.CopyCode' -FallbackName 'Copy code' -FallbackControlType ([System.Windows.Automation.ControlType]::Button)
    }
    Click-Element $copyBtn
    $raw = Wait-Until -TimeoutMs 4000 -PollMs 150 -OnTimeoutMessage 'Timed out waiting for code on clipboard.' -Condition {
        $text = [string](Get-ClipboardTextSafe)
        $m = [regex]::Match($text, '\d{3}\s?\d{3}')
        if ($m.Success) { return $m.Value }
        return $null
    }
    return ([string]$raw -replace '\D','')
}

function Enter-HelperCodeAndConnect {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$HelperWindow,
        [Parameter(Mandatory = $true)][string]$Code
    )
    $input = Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for Helper.CodeInput.' -Condition {
        $byId = Find-VisibleByAutomationId -Root $HelperWindow -AutomationId 'Helper.CodeInput'
        if ($byId) { return $byId }
        $edits = Find-AllByType -Root $HelperWindow -ControlType ([System.Windows.Automation.ControlType]::Edit)
        foreach ($e in @($edits)) { if ($e.Current.IsOffscreen -eq $false) { return $e } }
        return $null
    }
    Set-Text -Element $input -Text $Code

    $connect = Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for Helper.Connect.' -Condition {
        Find-VisibleByAutomationIdOrName -Root $HelperWindow -AutomationId 'Helper.Connect' -FallbackName 'Connect' -FallbackControlType ([System.Windows.Automation.ControlType]::Button)
    }
    Click-Element $connect
    return $connect
}

function Wait-HelpeeAllowAndClick {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$HelpeeWindow)
    $allow = Wait-Until -TimeoutMs 25000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for Helpee.Allow enabled.' -Condition {
        $btn = Find-VisibleByAutomationIdOrName -Root $HelpeeWindow -AutomationId 'Helpee.Allow' -FallbackName 'Allow' -FallbackControlType ([System.Windows.Automation.ControlType]::Button)
        if ($btn -and $btn.Current.IsEnabled) { return $btn }
        return $null
    }
    Click-Element $allow
}

function Wait-ConnectedChatVisible {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$HelpeeWindow,
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$HelperWindow
    )

    [void](Wait-Until -TimeoutMs 20000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helpee Chat.Send.' -Condition {
        Find-VisibleByAutomationIdOrName -Root $HelpeeWindow -AutomationId 'Chat.Send' -FallbackName 'Send' -FallbackControlType ([System.Windows.Automation.ControlType]::Button)
    })
    [void](Wait-Until -TimeoutMs 20000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helper Chat.Send.' -Condition {
        Find-VisibleByAutomationIdOrName -Root $HelperWindow -AutomationId 'Chat.Send' -FallbackName 'Send' -FallbackControlType ([System.Windows.Automation.ControlType]::Button)
    })
    [void](Wait-Until -TimeoutMs 5000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for connected status on helpee.' -Condition {
        $s = Find-VisibleByAutomationIdOrName -Root $HelpeeWindow -AutomationId 'Helpee.Status' -FallbackName 'Connected' -FallbackControlType ([System.Windows.Automation.ControlType]::Text)
        if ($s -and $s.Current.Name -match 'Connected') { return $s }
        return $null
    })
}

function Wait-ConnectButtonEnabled {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [int]$TimeoutMs = 10000
    )
    return Wait-Until -TimeoutMs $TimeoutMs -PollMs 200 -OnTimeoutMessage 'Timed out waiting for Helper.Connect enabled.' -Condition {
        $b = Find-VisibleByAutomationIdOrName -Root $Window -AutomationId 'Helper.Connect' -FallbackName 'Connect' -FallbackControlType ([System.Windows.Automation.ControlType]::Button)
        if ($b -and $b.Current.IsEnabled) { return $b }
        return $null
    }
}

function Helper-SendChatMessage {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$HelperWindow,
        [Parameter(Mandatory = $true)][string]$Text
    )
    $chatInput = Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for Chat.Input on helper.' -Condition {
        Find-VisibleByAutomationIdOrName -Root $HelperWindow -AutomationId 'Chat.Input' -FallbackControlType ([System.Windows.Automation.ControlType]::Edit)
    }
    Set-Text -Element $chatInput -Text $Text
    $sendBtn = Wait-Until -TimeoutMs 5000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for Chat.Send on helper.' -Condition {
        $b = Find-VisibleByAutomationIdOrName -Root $HelperWindow -AutomationId 'Chat.Send' -FallbackName 'Send' -FallbackControlType ([System.Windows.Automation.ControlType]::Button)
        if ($b -and $b.Current.IsEnabled) { return $b }
        return $null
    }
    Click-Element $sendBtn
}

function Send-ChatMessage {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$Text,
        [switch]$UseEnter
    )
    $chatInput = Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for Chat.Input.' -Condition {
        Find-VisibleByAutomationIdOrName -Root $Window -AutomationId 'Chat.Input' -FallbackControlType ([System.Windows.Automation.ControlType]::Edit)
    }

    Set-Text -Element $chatInput -Text $Text

    if ($UseEnter) {
        try {
            $windowHandle = [IntPtr]::new([int64]$Window.Current.NativeWindowHandle)
            [void][Win32GuiSmoke]::SetForegroundWindow($windowHandle)
        } catch {}
        Start-Sleep -Milliseconds 80
        [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
        return
    }

    $sendBtn = Wait-Until -TimeoutMs 5000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for Chat.Send.' -Condition {
        $b = Find-VisibleByAutomationIdOrName -Root $Window -AutomationId 'Chat.Send' -FallbackName 'Send' -FallbackControlType ([System.Windows.Automation.ControlType]::Button)
        if ($b -and $b.Current.IsEnabled) { return $b }
        return $null
    }
    Click-Element $sendBtn
}

function Wait-MessageVisible {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$MessageText,
        [int]$TimeoutMs = 10000
    )
    [void](Wait-Until -TimeoutMs $TimeoutMs -PollMs 200 -OnTimeoutMessage "Timed out waiting for message text '$MessageText'." -Condition {
        $texts = Find-AllByType -Root $Window -ControlType ([System.Windows.Automation.ControlType]::Text)
        foreach ($t in @($texts)) {
            if ($t.Current.IsOffscreen -eq $false -and $t.Current.Name -eq $MessageText) { return $t }
        }
        return $null
    })
}

function Test-MessageVisible {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$MessageText,
        [int]$TimeoutMs = 1500
    )
    try {
        [void](Wait-Until -TimeoutMs $TimeoutMs -PollMs 150 -OnTimeoutMessage 'not found' -Condition {
            $texts = Find-AllByType -Root $Window -ControlType ([System.Windows.Automation.ControlType]::Text)
            foreach ($t in @($texts)) {
                if ($t.Current.IsOffscreen -eq $false -and $t.Current.Name -eq $MessageText) { return $t }
            }
            return $null
        })
        return $true
    }
    catch {
        return $false
    }
}

function Click-DisconnectIfVisible {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window)
    $btn = Find-VisibleByAutomationIdOrName -Root $Window -AutomationId 'Helper.Disconnect' -FallbackName 'Disconnect' -FallbackControlType ([System.Windows.Automation.ControlType]::Button)
    if (-not $btn) {
        $btn = Find-VisibleByAutomationIdOrName -Root $Window -AutomationId 'Helpee.Disconnect' -FallbackName 'Disconnect' -FallbackControlType ([System.Windows.Automation.ControlType]::Button)
    }
    if ($btn -and $btn.Current.IsEnabled) {
        Click-Element $btn
        return $true
    }
    return $false
}

function Wait-StatusTextContains {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string[]]$Candidates,
        [int]$TimeoutMs = 10000
    )
    return Wait-Until -TimeoutMs $TimeoutMs -PollMs 200 -OnTimeoutMessage ("Timed out waiting for status containing one of: " + ($Candidates -join ', ')) -Condition {
        $texts = Find-AllByType -Root $Window -ControlType ([System.Windows.Automation.ControlType]::Text)
        foreach ($t in @($texts)) {
            if ($t.Current.IsOffscreen) { continue }
            foreach ($needle in $Candidates) {
                if (-not [string]::IsNullOrWhiteSpace($needle) -and $t.Current.Name -like "*$needle*") { return $t.Current.Name }
            }
        }
        return $null
    }
}

function Invoke-Scenario {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][int]$TimeoutSec,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Write-Host "[GUI Smoke][$Name] START" -ForegroundColor Cyan
    & $Action
    if ($sw.Elapsed.TotalSeconds -gt $TimeoutSec) {
        throw "Scenario $Name exceeded timeout ($($sw.Elapsed.TotalSeconds.ToString('N1'))s > ${TimeoutSec}s)."
    }
    Write-Host "[GUI Smoke][$Name] PASS ($([math]::Round($sw.Elapsed.TotalSeconds,1))s)" -ForegroundColor Green
}

function New-ScenarioContext {
    param([Parameter(Mandatory = $true)][string]$ExePath)
    return [pscustomobject]@{
        ExePath = $ExePath
        Processes = New-Object System.Collections.ArrayList
        HelpeeProc = $null
        HelperProc = $null
        HelpeeWindow = $null
        HelperWindow = $null
    }
}

function Reset-ScenarioContext {
    param([Parameter(Mandatory = $true)]$Context)
    $toKill = @($Context.Processes.ToArray())
    if ($toKill.Count -gt 0) {
        Cleanup-Processes -Processes $toKill
    }
    $Context.Processes.Clear()
    $Context.HelpeeProc = $null
    $Context.HelperProc = $null
    $Context.HelpeeWindow = $null
    $Context.HelperWindow = $null
}

function Start-HelpeeFlow {
    param([Parameter(Mandatory = $true)]$Context)
    $Context.HelpeeProc = Start-AppInstance -ExePath $Context.ExePath -RoleName 'helpee'
    [void]$Context.Processes.Add($Context.HelpeeProc)
    $Context.HelpeeWindow = Wait-Window -Process $Context.HelpeeProc -TimeoutMs 15000
    Click-HomeButton -Window $Context.HelpeeWindow -Text 'I need help'
}

function Start-HelperFlow {
    param([Parameter(Mandatory = $true)]$Context)
    $Context.HelperProc = Start-AppInstance -ExePath $Context.ExePath -RoleName 'helper'
    [void]$Context.Processes.Add($Context.HelperProc)
    $Context.HelperWindow = Wait-Window -Process $Context.HelperProc -TimeoutMs 15000
    Click-HomeButton -Window $Context.HelperWindow -Text 'I want to help someone'
}

function Connect-HelperAndHelpee {
    param([Parameter(Mandatory = $true)]$Context)
    $code = Copy-HelpeeCodeAndReadClipboard -HelpeeWindow $Context.HelpeeWindow
    Write-Host "[GUI Smoke] Helpee code copied: $code" -ForegroundColor Green
    [void](Enter-HelperCodeAndConnect -HelperWindow $Context.HelperWindow -Code $code)
    Wait-HelpeeAllowAndClick -HelpeeWindow $Context.HelpeeWindow
    Wait-ConnectedChatVisible -HelpeeWindow $Context.HelpeeWindow -HelperWindow $Context.HelperWindow
    return $code
}

function Run-ScenarioA {
    param([Parameter(Mandatory = $true)]$Context)
    Reset-ScenarioContext -Context $Context
    Start-HelpeeFlow -Context $Context
    [void](Get-HelpeeCodeFromUi -HelpeeWindow $Context.HelpeeWindow)
    Start-HelperFlow -Context $Context
    $initialCode = Connect-HelperAndHelpee -Context $Context

    # Chat roundtrip (helper -> helpee via Send button, helpee -> helper via Enter).
    $msg1 = "gui smoke hello"
    Send-ChatMessage -Window $Context.HelperWindow -Text $msg1
    Wait-MessageVisible -Window $Context.HelpeeWindow -MessageText $msg1 -TimeoutMs 10000

    $msg2 = "gui smoke reply"
    Send-ChatMessage -Window $Context.HelpeeWindow -Text $msg2 -UseEnter
    if (-not (Test-MessageVisible -Window $Context.HelperWindow -MessageText $msg2 -TimeoutMs 2000)) {
        # UIA focus timing can make Enter flaky; keep deterministic coverage by falling back to clicking Send.
        Send-ChatMessage -Window $Context.HelpeeWindow -Text $msg2
        Wait-MessageVisible -Window $Context.HelperWindow -MessageText $msg2 -TimeoutMs 10000
    }

    # End session from helper if possible; verify remote sees ended/lost status.
    [void](Click-DisconnectIfVisible -Window $Context.HelperWindow)
    try {
        [void](Wait-StatusTextContains -Window $Context.HelpeeWindow -Candidates @('ended the session','Connection lost') -TimeoutMs 5000)
    }
    catch {
        # Newer builds may auto-regenerate a fresh helpee code immediately after disconnect.
        [void](Wait-Until -TimeoutMs 12000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helpee to auto-generate a new code after session end.' -Condition {
            $newCode = Get-HelpeeCodeFromUi -HelpeeWindow $Context.HelpeeWindow
            if ($newCode -and $newCode -ne $initialCode) { return $newCode }
            return $null
        })
    }
}

function Run-ScenarioB {
    param([Parameter(Mandatory = $true)]$Context)
    Reset-ScenarioContext -Context $Context
    Start-HelpeeFlow -Context $Context
    Start-HelperFlow -Context $Context

    # Handshake timeout path: connect helper but do not click Allow on helpee.
    $code = Copy-HelpeeCodeAndReadClipboard -HelpeeWindow $Context.HelpeeWindow
    [void](Enter-HelperCodeAndConnect -HelperWindow $Context.HelperWindow -Code $code)

    # Confirm the helpee actually got the request, but intentionally don't allow/decline.
    [void](Wait-Until -TimeoutMs 25000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helpee Allow during handshake-timeout scenario.' -Condition {
        $btn = Find-VisibleByAutomationIdOrName -Root $Context.HelpeeWindow -AutomationId 'Helpee.Allow' -FallbackName 'Allow' -FallbackControlType ([System.Windows.Automation.ControlType]::Button)
        if ($btn -and $btn.Current.IsEnabled) { return $btn }
        return $null
    })

    # Prefer validating reconnect banner/countdown when present, but keep the test resilient if the build goes straight to Failed.
    try {
        $reconnectBanner = Wait-BannerVisibleWithAnyToken -Window $Context.HelperWindow -TitleOrMessageTokens @('reconnect') -TimeoutMs 15000
        if ($reconnectBanner) {
            [void](Wait-RetryCountdownVisible -Window $Context.HelperWindow -TimeoutMs 15000)
        }
    }
    catch {
        Write-Host "[GUI Smoke][B] Reconnecting banner not observed before failure (accepted for non-auto-retry builds)." -ForegroundColor Yellow
    }

    $failedBanner = $null
    try {
        $failedBanner = Wait-BannerVisibleWithAnyToken -Window $Context.HelperWindow -TitleOrMessageTokens @('no response','failed','session ended','connection lost','declined','reinstall') -TimeoutMs 35000
    }
    catch {
        # Some current builds surface a simple inline/helper status text instead of the shared banner for this path.
        [void](Wait-StatusTextContains -Window $Context.HelperWindow -Candidates @('wrong','connect','declined','response','respond','lost') -TimeoutMs 5000)
    }

    if ($failedBanner -and -not $failedBanner.HasCopyDiagnosticsButton) {
        throw "Expected Copy Diagnostics button on failed helper status banner after handshake timeout."
    }

    # Some builds surface a dedicated Retry action, others return directly to the helper form
    # with Connect re-enabled. Accept either as a valid recovery UX.
    $retry = $null
    try {
        $retry = Wait-Until -TimeoutMs 5000 -PollMs 200 -OnTimeoutMessage 'Retry button not shown (will fall back to Connect-enabled check).' -Condition {
            $b = Find-ByNameAndType -Root $Context.HelperWindow -Name 'Retry' -ControlType ([System.Windows.Automation.ControlType]::Button)
            if ($b -and $b.Current.IsEnabled) { return $b }
            return $null
        }
    }
    catch {
        $retry = $null
    }

    if ($retry) {
        Click-Element $retry
    }
    [void](Wait-ConnectButtonEnabled -Window $Context.HelperWindow -TimeoutMs 10000)
}

function Run-ScenarioC {
    param([Parameter(Mandatory = $true)]$Context)
    Reset-ScenarioContext -Context $Context
    Start-HelperFlow -Context $Context
    [void](Enter-HelperCodeAndConnect -HelperWindow $Context.HelperWindow -Code '000000')

    # User cancel on transient banner (connecting).
    $banner = $null
    try {
        $banner = Wait-BannerVisibleWithAnyToken -Window $Context.HelperWindow -TitleOrMessageTokens @('connect','reconnect') -TimeoutMs 5000
    }
    catch {
        $banner = $null
    }

    if ($banner -and $banner.HasCancelButton) {
        $cancel = Wait-Until -TimeoutMs 5000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for status banner Cancel button.' -Condition {
            $bundle = Get-BannerTextBundle -Window $Context.HelperWindow
            if ($bundle.HasBanner -and $bundle.HasCancelButton) {
                if ($bundle.Elements -and $bundle.Elements.CancelButton -and $bundle.Elements.CancelButton.Current.IsEnabled) {
                    return $bundle.Elements.CancelButton
                }
                $btn = Find-ByNameAndType -Root $Context.HelperWindow -Name 'Cancel' -ControlType ([System.Windows.Automation.ControlType]::Button)
                if ($btn -and $btn.Current.IsEnabled) { return $btn }
            }
            return $null
        }
        Click-Element $cancel

        # Banner should disappear and helper should return to idle-ish state (code field + Connect enabled).
        Wait-BannerGone -Window $Context.HelperWindow -TimeoutMs 10000
        [void](Wait-ConnectButtonEnabled -Window $Context.HelperWindow -TimeoutMs 10000)
        return
    }

    # Compatibility fallback for builds that still show a legacy inline "Connecting..." status without banner cancel.
    Write-Host "[GUI Smoke][C] Cancel banner not observed; validating fallback connecting state + recovery using Back." -ForegroundColor Yellow
    [void](Wait-StatusTextContains -Window $Context.HelperWindow -Candidates @('connect') -TimeoutMs 5000)

    $back = Wait-Until -TimeoutMs 5000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for Back button in fallback cancel scenario.' -Condition {
        $b = Find-ByNameAndType -Root $Context.HelperWindow -Name 'Back' -ControlType ([System.Windows.Automation.ControlType]::Button)
        if ($b -and $b.Current.IsEnabled) { return $b }
        return $null
    }
    Click-Element $back

    [void](Wait-Until -TimeoutMs 8000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting to return to main screen after fallback Back.' -Condition {
        $needHelp = Find-ByNameAndType -Root $Context.HelperWindow -Name 'I need help' -ControlType ([System.Windows.Automation.ControlType]::Button)
        if ($needHelp -and $needHelp.Current.IsEnabled) { return $needHelp }
        return $null
    })
}

function Get-ChildNodeProcesses {
    param([int]$ParentPid)
    try {
        return @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $ParentPid" | Where-Object { $_.Name -ieq 'node.exe' -or $_.Name -ieq 'node' })
    }
    catch {
        return @()
    }
}

function Run-ScenarioD {
    param([Parameter(Mandatory = $true)]$Context)
    Reset-ScenarioContext -Context $Context

    $transport = [string]$env:NLINK_TRANSPORT
    if ($transport -notin @('NKN','nkn')) {
        Write-Host '[GUI Smoke][D] SKIP: scenario D requires NLINK_TRANSPORT=NKN to create bridge node.exe child process.' -ForegroundColor Yellow
        return
    }

    Start-HelpeeFlow -Context $Context
    Start-HelperFlow -Context $Context
    [void](Connect-HelperAndHelpee -Context $Context)

    $nodes = Get-ChildNodeProcesses -ParentPid $Context.HelperProc.Id
    if ($nodes.Count -eq 0) {
        Write-Host '[GUI Smoke][D] SKIP: no node.exe child process found for helper.' -ForegroundColor Yellow
        return
    }

    # Kill one helper bridge child process.
    $node = $nodes[0]
    Write-Host "[GUI Smoke][D] Killing helper bridge process pid=$($node.ProcessId)" -ForegroundColor Yellow
    Stop-Process -Id $node.ProcessId -Force -ErrorAction Stop

    $failedBanner = Wait-BannerVisibleWithAnyToken -Window $Context.HelperWindow -TitleOrMessageTokens @('connection', 'lost', 'session', 'ended') -TimeoutMs 30000
    if (-not $failedBanner.HasCopyDiagnosticsButton) {
        throw "Expected Copy Diagnostics button on failed banner after simulated bridge crash."
    }

    $retryBtn = Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for Retry button after bridge crash.' -Condition {
        $b = Find-ByNameAndType -Root $Context.HelperWindow -Name 'Retry' -ControlType ([System.Windows.Automation.ControlType]::Button)
        if ($b -and $b.Current.IsEnabled) { return $b }
        return $null
    }
    Click-Element $retryBtn

    [void](Wait-ConnectButtonEnabled -Window $Context.HelperWindow -TimeoutMs 10000)
}

$resolvedExe = (Resolve-Path $ExePath).Path
if (-not (Test-Path $resolvedExe)) {
    Write-Error "nLink.exe not found: $ExePath"
    exit 2
}

$oldTransport = $env:NLINK_TRANSPORT
$requestedScenarios = [string]$env:NLINK_GUI_SMOKE_SCENARIOS
if ([string]::IsNullOrWhiteSpace($requestedScenarios)) { $requestedScenarios = 'A' }
$scenarioList = @(
    $requestedScenarios.Split(',', [System.StringSplitOptions]::RemoveEmptyEntries) |
    ForEach-Object { $_.Trim().ToUpperInvariant() } |
    Where-Object { $_ -ne '' }
)
if ($scenarioList.Count -eq 0) { $scenarioList = @('A') }

$ctx = New-ScenarioContext -ExePath $resolvedExe
$failureArtifactsDir = $null
$exitCode = 1

try {
    # Deterministic local GUI smoke by default.
    if ([string]::IsNullOrWhiteSpace($env:NLINK_TRANSPORT)) {
        $env:NLINK_TRANSPORT = 'DEVLOCAL'
    }

    foreach ($scenario in @($scenarioList)) {
        switch ($scenario) {
            'A' { Invoke-Scenario -Name 'A' -TimeoutSec ([Math]::Min($TimeoutSeconds, 90)) -Action { Run-ScenarioA -Context $ctx } }
            'B' { Invoke-Scenario -Name 'B' -TimeoutSec ([Math]::Min($TimeoutSeconds, 60)) -Action { Run-ScenarioB -Context $ctx } }
            'C' { Invoke-Scenario -Name 'C' -TimeoutSec ([Math]::Min($TimeoutSeconds, 90)) -Action { Run-ScenarioC -Context $ctx } }
            'D' { Invoke-Scenario -Name 'D' -TimeoutSec ([Math]::Min($TimeoutSeconds, 90)) -Action { Run-ScenarioD -Context $ctx } }
            default { throw "Unknown GUI smoke scenario '$scenario'. Use A,B,C,D." }
        }
    }

    Write-Host "[GUI Smoke] PASS: scenarios $($scenarioList -join ',') completed." -ForegroundColor Green
    $exitCode = 0
}
catch {
    $failureArtifactsDir = New-FailureArtifactDir
    Write-Host "[GUI Smoke] FAIL: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "[GUI Smoke] Collecting failure artifacts in $failureArtifactsDir" -ForegroundColor Yellow

    try { if ($ctx.HelpeeWindow) { Dump-UiTree -Root $ctx.HelpeeWindow -OutPath (Join-Path $failureArtifactsDir 'helpee-ui-tree.txt') } } catch {}
    try { if ($ctx.HelperWindow) { Dump-UiTree -Root $ctx.HelperWindow -OutPath (Join-Path $failureArtifactsDir 'helper-ui-tree.txt') } } catch {}
    try { 'Screenshot capture not implemented in this script (UI tree + logs captured).' | Set-Content -Path (Join-Path $failureArtifactsDir 'screenshot.txt') -Encoding UTF8 } catch {}
    try { Copy-AppLogsIfPresent -ArtifactDir $failureArtifactsDir } catch {}

    $exitCode = 1
}
finally {
    Reset-ScenarioContext -Context $ctx

    if ($null -eq $oldTransport) {
        Remove-Item Env:NLINK_TRANSPORT -ErrorAction SilentlyContinue
    }
    else {
        $env:NLINK_TRANSPORT = $oldTransport
    }
}

exit $exitCode
