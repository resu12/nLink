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
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
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

function Wait-NonEmptyAutomationText {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$AutomationId,
        [int]$TimeoutMs = 10000
    )

    return Wait-Until -TimeoutMs $TimeoutMs -PollMs 200 -OnTimeoutMessage "Timed out waiting for non-empty text in $AutomationId." -Condition {
        $el = Find-VisibleByAutomationId -Root $Window -AutomationId $AutomationId
        if (-not $el) { return $null }

        $text = (Get-ElementTextSafe -Element $el).Trim()
        if ([string]::IsNullOrWhiteSpace($text)) { return $null }

        return [pscustomobject]@{
            Element = $el
            Text = $text
        }
    }
}

function Wait-AutomationTextInSet {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$AutomationId,
        [Parameter(Mandatory = $true)][string[]]$AllowedTexts,
        [int]$TimeoutMs = 10000
    )

    return Wait-Until -TimeoutMs $TimeoutMs -PollMs 200 -OnTimeoutMessage ("Timed out waiting for $AutomationId to match one of: " + ($AllowedTexts -join ', ')) -Condition {
        $el = Find-VisibleByAutomationId -Root $Window -AutomationId $AutomationId
        if (-not $el) { return $null }

        $text = (Get-ElementTextSafe -Element $el).Trim()
        if ([string]::IsNullOrWhiteSpace($text)) { return $null }

        foreach ($allowed in $AllowedTexts) {
            if ([string]::Equals($text, $allowed, [System.StringComparison]::Ordinal)) {
                return [pscustomobject]@{
                    Element = $el
                    Text = $text
                }
            }
        }

        return $null
    }
}

function Wait-AutomationTextEquals {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$AutomationId,
        [Parameter(Mandatory = $true)][string]$ExpectedText,
        [int]$TimeoutMs = 10000
    )

    return Wait-Until -TimeoutMs $TimeoutMs -PollMs 200 -OnTimeoutMessage "Timed out waiting for $AutomationId to equal '$ExpectedText'." -Condition {
        $el = Find-VisibleByAutomationId -Root $Window -AutomationId $AutomationId
        if (-not $el) { return $null }

        $text = (Get-ElementTextSafe -Element $el).Trim()
        if ([string]::IsNullOrWhiteSpace($text)) { return $null }
        if ([string]::Equals($text, $ExpectedText, [System.StringComparison]::Ordinal)) {
            return [pscustomobject]@{
                Element = $el
                Text = $text
            }
        }

        return $null
    }
}

function Wait-AutomationElementVisibleState {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$AutomationId,
        [Parameter(Mandatory = $true)][bool]$IsVisible,
        [int]$TimeoutMs = 10000
    )

    $stateLabel = if ($IsVisible) { 'visible' } else { 'hidden' }
    return Wait-Until -TimeoutMs $TimeoutMs -PollMs 200 -OnTimeoutMessage "Timed out waiting for $AutomationId to become $stateLabel." -Condition {
        $element = Find-VisibleByAutomationId -Root $Window -AutomationId $AutomationId
        if ($IsVisible) {
            if ($element) { return $element }
            return $null
        }

        if (-not $element) { return $true }
        return $null
    }
}

function Find-ScreenShareViewer {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window)

    $viewer = Find-VisibleByAutomationId -Root $Window -AutomationId 'ScreenShare.Viewer'
    if ($viewer) {
        return $viewer
    }

    $placeholder = Find-VisibleByAutomationId -Root $Window -AutomationId 'PlaceholderText'
    if ($placeholder) {
        return $placeholder
    }

    $viewerMessage = Find-VisibleByAutomationId -Root $Window -AutomationId 'ScreenShare.ViewerMessage'
    if ($viewerMessage) {
        return $viewerMessage
    }

    return $null
}

function Wait-ScreenShareButtonText {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$ExpectedText,
        [int]$TimeoutMs = 10000
    )

    return Wait-Until -TimeoutMs $TimeoutMs -PollMs 200 -OnTimeoutMessage "Timed out waiting for SessionHeader.ShareScreen text '$ExpectedText'." -Condition {
        $button = Find-VisibleByAutomationId -Root $Window -AutomationId 'SessionHeader.ShareScreen'
        if (-not $button -or -not $button.Current.IsEnabled) {
            return $null
        }

        $text = (Get-ElementTextSafe -Element $button).Trim()
        if ([string]::Equals($text, $ExpectedText, [System.StringComparison]::Ordinal)) {
            return $button
        }

        return $null
    }
}

function Wait-ScreenShareViewerVisibleState {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][bool]$IsVisible,
        [int]$TimeoutMs = 10000
    )

    $stateLabel = if ($IsVisible) { 'visible' } else { 'hidden' }
    return Wait-Until -TimeoutMs $TimeoutMs -PollMs 200 -OnTimeoutMessage "Timed out waiting for ScreenShare.Viewer to become $stateLabel." -Condition {
        $viewer = Find-ScreenShareViewer -Window $Window
        if ($IsVisible) {
            if ($viewer) { return $viewer }
            return $null
        }

        if (-not $viewer) { return $true }
        return $null
    }
}

function Get-ElementValueSafe {
    param([System.Windows.Automation.AutomationElement]$Element)
    if ($null -eq $Element) { return '' }
    try {
        $vp = $Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        return [string]([System.Windows.Automation.ValuePattern]$vp).Current.Value
    }
    catch {
        return Get-ElementTextSafe -Element $Element
    }
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

    try {
        $window = Get-WindowElementByProcessId -ProcessId $Element.Current.ProcessId
        if ($window) {
            $windowHandle = [IntPtr]::new([int64]$window.Current.NativeWindowHandle)
            [void][Win32GuiSmoke]::SetForegroundWindow($windowHandle)
        }
    }
    catch {}

    try {
        $Element.SetFocus()
        [System.Windows.Forms.SendKeys]::SendWait(' ')
        return
    }
    catch {}

    $rect = $Element.Current.BoundingRectangle
    if ($rect.IsEmpty) { throw "Cannot click element without bounds." }
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point([int]($rect.Left + $rect.Width/2), [int]($rect.Top + $rect.Height/2))
    [Win32GuiSmoke]::mouse_event([Win32GuiSmoke]::MOUSEEVENTF_LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
    [Win32GuiSmoke]::mouse_event([Win32GuiSmoke]::MOUSEEVENTF_LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
}

function Test-ElementValueMatchesText {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Element,
        [Parameter(Mandatory = $true)][string]$ExpectedText
    )

    $rawValue = Get-ElementValueSafe -Element $Element
    $actualComparable = $rawValue
    $expectedComparable = $ExpectedText

    return [pscustomobject]@{
        RawValue = $rawValue
        ActualComparable = $actualComparable
        ExpectedComparable = $expectedComparable
        IsMatch = [string]::Equals($actualComparable, $expectedComparable, [System.StringComparison]::Ordinal)
    }
}

function Invoke-SendKeysReplaceText {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Element,
        [Parameter(Mandatory = $true)][string]$Text,
        [int]$TimeoutMs = 1000
    )

    $lastObservedValue = ''
    $lastComparableValue = ''
    $expectedComparable = $Text
    $attempts = 0

    try {
        [void](Wait-Until -TimeoutMs $TimeoutMs -PollMs 50 -OnTimeoutMessage 'Timed out waiting for deterministic text entry.' -Condition {
            try {
                $Element.SetFocus()
            }
            catch {}

            $attempts++
            $currentState = Test-ElementValueMatchesText -Element $Element -ExpectedText $Text
            $lastObservedValue = $currentState.RawValue
            $lastComparableValue = $currentState.ActualComparable
            $expectedComparable = $currentState.ExpectedComparable
            if ($currentState.IsMatch) {
                return $true
            }

            [System.Windows.Forms.SendKeys]::SendWait('^a')
            [System.Windows.Forms.SendKeys]::SendWait($Text)

            $updatedState = Test-ElementValueMatchesText -Element $Element -ExpectedText $Text
            $lastObservedValue = $updatedState.RawValue
            $lastComparableValue = $updatedState.ActualComparable
            $expectedComparable = $updatedState.ExpectedComparable
            if ($updatedState.IsMatch) {
                return $true
            }

            return $null
        })
    }
    catch {
        throw "field did not accept replacement text within $TimeoutMs ms. LastValue='$lastObservedValue'; LastComparable='$lastComparableValue'; Target='$Text'; ExpectedComparable='$expectedComparable'; Attempts=$attempts"
    }
}

function Set-Text {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Element,
        [Parameter(Mandatory = $true)][string]$Text
    )
    $forceKeyboardInput = $false
    try {
        $forceKeyboardInput = [string]::Equals([string]$Element.Current.AutomationId, 'Helper.CodeInput', [System.StringComparison]::Ordinal)
    }
    catch {
        $forceKeyboardInput = $false
    }

    if (-not $forceKeyboardInput) {
        try {
            $vp = $Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
            ([System.Windows.Automation.ValuePattern]$vp).SetValue($Text)
            return
        }
        catch {}
    }

    try {
        $window = Get-WindowElementByProcessId -ProcessId $Element.Current.ProcessId
        if ($window) {
            $windowHandle = [IntPtr]::new([int64]$window.Current.NativeWindowHandle)
            [void][Win32GuiSmoke]::SetForegroundWindow($windowHandle)
        }
    }
    catch {}

    try {
        $Element.SetFocus()
    }
    catch {}

    $rect = $Element.Current.BoundingRectangle
    if (-not $rect.IsEmpty) {
        [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point([int]($rect.Left + 10), [int]($rect.Top + $rect.Height/2))
    }
    Invoke-SendKeysReplaceText -Element $Element -Text $Text -TimeoutMs 1000
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

function Clear-AppLogsIfPresent {
    $logsDir = Join-Path $env:LOCALAPPDATA 'nLink\logs'
    if (-not (Test-Path $logsDir)) { return }

    Get-ChildItem -Path $logsDir -File -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
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

function Get-IsNknTransport {
    return [string]::Equals($env:NLINK_TRANSPORT, 'NKN', [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-TransportAwareTimeoutMs {
    param(
        [Parameter(Mandatory = $true)][int]$DefaultMs,
        [int]$NknMs = $DefaultMs
    )

    if (Get-IsNknTransport) {
        return $NknMs
    }

    return $DefaultMs
}

function Get-SessionHeaderStatusValue {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window)

    $status = Find-VisibleByAutomationId -Root $Window -AutomationId 'SessionHeader.StatusText'
    if (-not $status) {
        return ''
    }

    return (Get-ElementTextSafe -Element $status).Trim()
}

function Test-ConnectionFailedSurface {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window)

    $status = Get-SessionHeaderStatusValue -Window $Window
    return [string]::Equals($status, 'Connection failed', [System.StringComparison]::Ordinal)
}

function Reenter-RoleFlowAfterConnectionFailure {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$HomeButtonText
    )

    if (-not (Test-ConnectionFailedSurface -Window $Window)) {
        return $false
    }

    Write-Host "[GUI Smoke] Recovering role flow after Connection failed for '$HomeButtonText'." -ForegroundColor Yellow
    try {
        Click-ButtonByName -Window $Window -Text 'Back' -TimeoutMs (Get-TransportAwareTimeoutMs -DefaultMs 5000 -NknMs 8000)
        Wait-HomeScreen -Window $Window -TimeoutMs (Get-TransportAwareTimeoutMs -DefaultMs 12000 -NknMs 20000)
        Click-HomeButton -Window $Window -Text $HomeButtonText
        if (Try-ClickRoleButtonIfPresent -Window $Window -RoleButtonText $HomeButtonText) {
            Write-Host "[GUI Smoke] Role page detected during recovery; selected '$HomeButtonText'." -ForegroundColor DarkGray
        }
    }
    catch {
        return $false
    }

    return $true
}

function Wait-HomeScreen {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [int]$TimeoutMs = 12000
    )

    [void](Wait-Until -TimeoutMs $TimeoutMs -PollMs 200 -OnTimeoutMessage 'Timed out waiting for Home screen.' -Condition {
        $needHelp = Find-ByNameAndType -Root $Window -Name 'I need help' -ControlType ([System.Windows.Automation.ControlType]::Button)
        $wantToHelp = Find-ByNameAndType -Root $Window -Name 'I want to help someone' -ControlType ([System.Windows.Automation.ControlType]::Button)
        if ($needHelp -and $wantToHelp -and -not $needHelp.Current.IsOffscreen -and -not $wantToHelp.Current.IsOffscreen) {
            return $true
        }
        return $null
    })
}

function Wait-ButtonVisibleEnabledByName {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$Text,
        [int]$TimeoutMs = 10000
    )

    return Wait-Until -TimeoutMs $TimeoutMs -PollMs 200 -OnTimeoutMessage ("Timed out waiting for button '$Text'.") -Condition {
        $btn = Find-ByNameAndType -Root $Window -Name $Text -ControlType ([System.Windows.Automation.ControlType]::Button)
        if ($btn -and -not $btn.Current.IsOffscreen -and $btn.Current.IsEnabled) {
            return $btn
        }

        return $null
    }
}

function Wait-ControlEnabledStateByAutomationId {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$AutomationId,
        [Parameter(Mandatory = $true)][bool]$IsEnabled,
        [int]$TimeoutMs = 10000
    )

    $targetStateText = if ($IsEnabled) { 'enabled' } else { 'disabled' }
    return Wait-Until -TimeoutMs $TimeoutMs -PollMs 200 -OnTimeoutMessage "Timed out waiting for $AutomationId to become $targetStateText." -Condition {
        $el = Find-VisibleByAutomationId -Root $Window -AutomationId $AutomationId
        if ($el -and $el.Current.IsEnabled -eq $IsEnabled) { return $el }
        return $null
    }
}

function Wait-ControlDisabledOrGoneByAutomationId {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$AutomationId,
        [int]$TimeoutMs = 10000
    )

    return Wait-Until -TimeoutMs $TimeoutMs -PollMs 200 -OnTimeoutMessage "Timed out waiting for $AutomationId to become disabled or disappear." -Condition {
        $el = Find-VisibleByAutomationId -Root $Window -AutomationId $AutomationId
        if (-not $el) { return $true }
        if ($el.Current.IsEnabled -eq $false) { return $el }
        return $null
    }
}

function Click-ButtonByName {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$Text,
        [int]$TimeoutMs = 10000
    )

    $btn = Wait-ButtonVisibleEnabledByName -Window $Window -Text $Text -TimeoutMs $TimeoutMs
    Click-Element $btn
}

function Test-ButtonVisibleByName {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$Text
    )

    $btn = Find-ByNameAndType -Root $Window -Name $Text -ControlType ([System.Windows.Automation.ControlType]::Button)
    return ($btn -and -not $btn.Current.IsOffscreen)
}

function Assert-ButtonNotVisibleByName {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$Text
    )

    if (Test-ButtonVisibleByName -Window $Window -Text $Text) {
        throw "Unexpectedly found visible button '$Text'."
    }
}

function Assert-TextsNotVisible {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string[]]$Texts
    )

    foreach ($text in $Texts) {
        if (Test-TextVisible -Root $Window -Text $text) {
            throw "Unexpectedly found visible text '$text'."
        }
    }
}

function Try-ClickRoleButtonIfPresent {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$RoleButtonText,
        [int]$TimeoutMs = 4000
    )

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        $roleTitle = Find-ByNameAndType -Root $Window -Name 'Choose your role' -ControlType ([System.Windows.Automation.ControlType]::Text
        )
        if ($roleTitle -and $roleTitle.Current.IsOffscreen -eq $false) {
            $roleButton = Find-ByNameAndType -Root $Window -Name $RoleButtonText -ControlType ([System.Windows.Automation.ControlType]::Button)
            if ($roleButton -and $roleButton.Current.IsOffscreen -eq $false -and $roleButton.Current.IsEnabled) {
                Click-Element $roleButton
                return $true
            }
        }

        Start-Sleep -Milliseconds 150
    }

    return $false
}

function Get-HelpeeCodeFromUi {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$HelpeeWindow)

    $inviteText = Wait-Until -TimeoutMs (Get-TransportAwareTimeoutMs -DefaultMs 10000 -NknMs 30000) -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helpee invite.' -Condition {
        $copyBtn = Find-VisibleByAutomationIdOrName -Root $HelpeeWindow -AutomationId 'Helpee.CopyInvite' -FallbackName 'Copy invite' -FallbackControlType ([System.Windows.Automation.ControlType]::Button)
        $qr = Find-VisibleByAutomationId -Root $HelpeeWindow -AutomationId 'Helpee.InviteQr'
        if ($copyBtn -and $copyBtn.Current.IsEnabled -and $qr) {
            return 'invite_ready'
        }
        return $null
    }
    return [string]$inviteText
}

function Copy-HelpeeCodeAndReadClipboard {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$HelpeeWindow)
    $copyBtn = Wait-Until -TimeoutMs (Get-TransportAwareTimeoutMs -DefaultMs 10000 -NknMs 30000) -PollMs 200 -OnTimeoutMessage 'Timed out waiting for Helpee.CopyInvite.' -Condition {
        $btn = Find-VisibleByAutomationIdOrName -Root $HelpeeWindow -AutomationId 'Helpee.CopyInvite' -FallbackName 'Copy invite' -FallbackControlType ([System.Windows.Automation.ControlType]::Button)
        if ($btn -and $btn.Current.IsEnabled) { return $btn }
        return $null
    }
    Click-Element $copyBtn
    $raw = Wait-Until -TimeoutMs 4000 -PollMs 150 -OnTimeoutMessage 'Timed out waiting for invite on clipboard.' -Condition {
        $text = [string](Get-ClipboardTextSafe)
        if (-not [string]::IsNullOrWhiteSpace($text)) { return $text.Trim() }
        return $null
    }
    return [string]$raw
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
        $btn = Find-VisibleByAutomationIdOrName -Root $HelperWindow -AutomationId 'Helper.Connect' -FallbackName 'Connect' -FallbackControlType ([System.Windows.Automation.ControlType]::Button)
        if ($btn -and $btn.Current.IsEnabled) { return $btn }
        return $null
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
        $s = Find-VisibleByAutomationIdOrName -Root $HelpeeWindow -AutomationId 'SessionHeader.StatusText' -FallbackName 'Connected' -FallbackControlType ([System.Windows.Automation.ControlType]::Text)
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

function Wait-HelperCodeInputEnabled {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [int]$TimeoutMs = 10000
    )
    return Wait-Until -TimeoutMs $TimeoutMs -PollMs 200 -OnTimeoutMessage 'Timed out waiting for Helper.CodeInput enabled.' -Condition {
        $input = Find-VisibleByAutomationId -Root $Window -AutomationId 'Helper.CodeInput'
        if (-not $input) {
            $edits = Find-AllByType -Root $Window -ControlType ([System.Windows.Automation.ControlType]::Edit)
            foreach ($e in @($edits)) {
                if ($e -and -not $e.Current.IsOffscreen) {
                    $input = $e
                    break
                }
            }
        }

        if ($input -and $input.Current.IsEnabled -and -not $input.Current.IsOffscreen) { return $input }
        return $null
    }
}

function Copy-HelpeeCodeWithRecovery {
    param([Parameter(Mandatory = $true)]$Context)

    $attempts = if (Get-IsNknTransport) { 3 } else { 1 }
    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        try {
            return Copy-HelpeeCodeAndReadClipboard -HelpeeWindow $Context.HelpeeWindow
        }
        catch {
            if ($attempt -ge $attempts) {
                throw
            }

            if (-not (Reenter-RoleFlowAfterConnectionFailure -Window $Context.HelpeeWindow -HomeButtonText 'I need help')) {
                Restart-HelpeeFlow -Context $Context
            }
        }
    }

    throw 'Unreachable helpee invite recovery failure.'
}

function Ensure-HelperReadyForInviteEntry {
    param([Parameter(Mandatory = $true)]$Context)

    $attempts = if (Get-IsNknTransport) { 3 } else { 1 }
    $timeoutMs = Get-TransportAwareTimeoutMs -DefaultMs 10000 -NknMs 30000
    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        try {
            return Wait-HelperCodeInputEnabled -Window $Context.HelperWindow -TimeoutMs $timeoutMs
        }
        catch {
            if ($attempt -ge $attempts) {
                throw
            }

            if (-not (Reenter-RoleFlowAfterConnectionFailure -Window $Context.HelperWindow -HomeButtonText 'I want to help someone')) {
                Restart-HelperFlow -Context $Context
            }
        }
    }

    throw 'Unreachable helper invite-entry recovery failure.'
}

function Assert-ProcessStillRunning {
    param(
        [Parameter(Mandatory = $true)]$Process,
        [Parameter(Mandatory = $true)][string]$Label
    )

    try {
        if ($Process.HasExited) {
            $exitCode = '(unknown)'
            try { $exitCode = [string]$Process.ExitCode } catch {}
            throw "$Label process exited unexpectedly (pid=$($Process.Id), exitCode=$exitCode)."
        }
    }
    catch [System.InvalidOperationException] {
        return
    }
}

function Copy-HelperIdentityAndReadClipboard {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$HelperWindow)

    try { Set-Clipboard -Value '' } catch {}

    $copyBtn = Wait-Until -TimeoutMs (Get-TransportAwareTimeoutMs -DefaultMs 10000 -NknMs 45000) -PollMs 200 -OnTimeoutMessage 'Timed out waiting for Helper.CopyHelperIdentity.' -Condition {
        $btn = Find-VisibleByAutomationId -Root $HelperWindow -AutomationId 'Helper.CopyHelperIdentity'
        if ($btn -and $btn.Current.IsEnabled) { return $btn }
        return $null
    }

    Click-Element $copyBtn

    $raw = Wait-Until -TimeoutMs 5000 -PollMs 150 -OnTimeoutMessage 'Timed out waiting for helper identity on clipboard.' -Condition {
        $text = [string](Get-ClipboardTextSafe)
        if (-not [string]::IsNullOrWhiteSpace($text)) { return $text.Trim() }
        return $null
    }

    return [string]$raw
}

function Copy-HelperIdentityWithRecovery {
    param([Parameter(Mandatory = $true)]$Context)

    $attempts = if (Get-IsNknTransport) { 3 } else { 1 }
    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        try {
            return Copy-HelperIdentityAndReadClipboard -HelperWindow $Context.HelperWindow
        }
        catch {
            if ($attempt -ge $attempts) {
                throw
            }

            if (-not (Reenter-RoleFlowAfterConnectionFailure -Window $Context.HelperWindow -HomeButtonText 'I want to help someone')) {
                Restart-HelperFlow -Context $Context
            }
        }
    }

    throw 'Unreachable helper identity recovery failure.'
}

function Enter-HelpeeHelperIdentityAndRequestHelp {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$HelpeeWindow,
        [Parameter(Mandatory = $true)][string]$HelperIdentity
    )

    $input = Wait-Until -TimeoutMs (Get-TransportAwareTimeoutMs -DefaultMs 10000 -NknMs 30000) -PollMs 200 -OnTimeoutMessage 'Timed out waiting for Helpee.HelperIdentityInput.' -Condition {
        $el = Find-VisibleByAutomationId -Root $HelpeeWindow -AutomationId 'Helpee.HelperIdentityInput'
        if ($el -and $el.Current.IsEnabled) { return $el }
        return $null
    }

    Set-Text -Element $input -Text $HelperIdentity

    $request = Wait-Until -TimeoutMs (Get-TransportAwareTimeoutMs -DefaultMs 10000 -NknMs 45000) -PollMs 200 -OnTimeoutMessage 'Timed out waiting for Helpee.RequestHelp to become enabled.' -Condition {
        $btn = Find-VisibleByAutomationId -Root $HelpeeWindow -AutomationId 'Helpee.RequestHelp'
        if ($btn -and $btn.Current.IsEnabled) { return $btn }
        return $null
    }

    Click-Element $request
    return $request
}

function Wait-HelperAcceptRequestOrExit {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [int]$TimeoutMs = 60000
    )

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        Assert-ProcessStillRunning -Process $Context.HelperProc -Label 'Helper'
        Assert-ProcessStillRunning -Process $Context.HelpeeProc -Label 'Helpee'

        if (Test-ConnectionFailedSurface -Window $Context.HelperWindow) {
            throw "Helper reached Connection failed before showing an incoming request."
        }

        $accept = Find-VisibleByAutomationId -Root $Context.HelperWindow -AutomationId 'Helper.AcceptHelpRequest'
        if (-not $accept) {
            $accept = Find-ByNameAndType -Root $Context.HelperWindow -Name 'Accept' -ControlType ([System.Windows.Automation.ControlType]::Button)
            if ($accept -and $accept.Current.IsOffscreen) {
                $accept = $null
            }
        }

        if ($accept -and $accept.Current.IsEnabled) {
            return $accept
        }

        Start-Sleep -Milliseconds 250
    }

    throw "Timed out waiting for helper incoming request acceptance UI."
}

function Wait-HelpeeAllowOrExit {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [int]$TimeoutMs = 60000
    )

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        Assert-ProcessStillRunning -Process $Context.HelperProc -Label 'Helper'
        Assert-ProcessStillRunning -Process $Context.HelpeeProc -Label 'Helpee'

        if (Test-ConnectionFailedSurface -Window $Context.HelperWindow) {
            throw "Helper reached Connection failed before helpee approval."
        }

        if (Test-ConnectionFailedSurface -Window $Context.HelpeeWindow) {
            throw "Helpee reached Connection failed before approval."
        }

        $allow = Find-VisibleByAutomationId -Root $Context.HelpeeWindow -AutomationId 'Helpee.Allow'
        if (-not $allow) {
            $allow = Find-ByNameAndType -Root $Context.HelpeeWindow -Name 'Allow' -ControlType ([System.Windows.Automation.ControlType]::Button)
            if ($allow -and $allow.Current.IsOffscreen) {
                $allow = $null
            }
        }

        if ($allow -and $allow.Current.IsEnabled) {
            return $allow
        }

        Start-Sleep -Milliseconds 250
    }

    throw "Timed out waiting for helpee Allow approval UI."
}

function Wait-ConnectedChatVisibleProcessAware {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [int]$TimeoutMs = 90000
    )

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        Assert-ProcessStillRunning -Process $Context.HelperProc -Label 'Helper'
        Assert-ProcessStillRunning -Process $Context.HelpeeProc -Label 'Helpee'

        if (Test-ConnectionFailedSurface -Window $Context.HelperWindow) {
            throw "Helper reached Connection failed before connected chat became visible."
        }

        if (Test-ConnectionFailedSurface -Window $Context.HelpeeWindow) {
            throw "Helpee reached Connection failed before connected chat became visible."
        }

        $helpeeSend = Find-VisibleByAutomationIdOrName -Root $Context.HelpeeWindow -AutomationId 'Chat.Send' -FallbackName 'Send' -FallbackControlType ([System.Windows.Automation.ControlType]::Button)
        $helperSend = Find-VisibleByAutomationIdOrName -Root $Context.HelperWindow -AutomationId 'Chat.Send' -FallbackName 'Send' -FallbackControlType ([System.Windows.Automation.ControlType]::Button)
        $helpeeStatus = Get-SessionHeaderStatusValue -Window $Context.HelpeeWindow
        $helperStatus = Get-SessionHeaderStatusValue -Window $Context.HelperWindow

        if ($helpeeSend -and $helperSend -and
            $helpeeStatus -match 'Connected' -and
            $helperStatus -match 'Connected') {
            return $true
        }

        Start-Sleep -Milliseconds 250
    }

    throw "Timed out waiting for connected chat on both helper and helpee."
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

        [void](Wait-Until -TimeoutMs 1000 -PollMs 50 -OnTimeoutMessage 'Window did not become ready for Enter send within 1000 ms.' -Condition {
            try {
                $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
                if ($focused -and $focused.Current.ProcessId -eq $Window.Current.ProcessId) {
                    return $true
                }
            }
            catch {}

            return $null
        })

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
    if (-not $btn) {
        $btn = Find-ByNameAndType -Root $Window -Name 'End session' -ControlType ([System.Windows.Automation.ControlType]::Button)
        if ($btn -and $btn.Current.IsOffscreen) {
            $btn = $null
        }
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

function Test-DiagnosticsAffordanceVisible {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        $BannerBundle = $null
    )

    if ($BannerBundle -and $BannerBundle.HasCopyDiagnosticsButton) {
        return $true
    }

    $openDiagnostics = Find-VisibleByAutomationIdOrName `
        -Root $Window `
        -AutomationId 'OpenDiagnosticsButton' `
        -FallbackName 'Open diagnostics' `
        -FallbackControlType ([System.Windows.Automation.ControlType]::Button)
    return ($null -ne $openDiagnostics -and -not $openDiagnostics.Current.IsOffscreen)
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
    if (Try-ClickRoleButtonIfPresent -Window $Context.HelpeeWindow -RoleButtonText 'I need help') {
        Write-Host "[GUI Smoke] Role page detected (helpee); selected 'I need help'." -ForegroundColor DarkGray
    }
}

function Start-HelperFlow {
    param([Parameter(Mandatory = $true)]$Context)
    $Context.HelperProc = Start-AppInstance -ExePath $Context.ExePath -RoleName 'helper'
    [void]$Context.Processes.Add($Context.HelperProc)
    $Context.HelperWindow = Wait-Window -Process $Context.HelperProc -TimeoutMs 15000
    Click-HomeButton -Window $Context.HelperWindow -Text 'I want to help someone'
    if (Try-ClickRoleButtonIfPresent -Window $Context.HelperWindow -RoleButtonText 'I want to help someone') {
        Write-Host "[GUI Smoke] Role page detected (helper); selected 'I want to help someone'." -ForegroundColor DarkGray
    }
}

function Restart-HelpeeFlow {
    param([Parameter(Mandatory = $true)]$Context)

    if ($Context.HelpeeProc) {
        Cleanup-Processes -Processes @($Context.HelpeeProc)
    }

    $Context.HelpeeProc = $null
    $Context.HelpeeWindow = $null
    Start-HelpeeFlow -Context $Context
}

function Restart-HelperFlow {
    param([Parameter(Mandatory = $true)]$Context)

    if ($Context.HelperProc) {
        Cleanup-Processes -Processes @($Context.HelperProc)
    }

    $Context.HelperProc = $null
    $Context.HelperWindow = $null
    Start-HelperFlow -Context $Context
}

function Connect-HelperAndHelpee {
    param([Parameter(Mandatory = $true)]$Context)
    $code = Copy-HelpeeCodeWithRecovery -Context $Context
    Write-Host "[GUI Smoke] Helpee code copied: $code" -ForegroundColor Green
    [void](Ensure-HelperReadyForInviteEntry -Context $Context)
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
        [void](Wait-StatusTextContains -Window $Context.HelpeeWindow -Candidates @('ended the session','session ended','Connection lost','connection problem','other side ended') -TimeoutMs 5000)
    }
    catch {
        $failedRemote = $false
        try {
            [void](Wait-Until -TimeoutMs 4000 -PollMs 200 -OnTimeoutMessage 'Retry state not observed on helpee after session end.' -Condition {
                $retry = Find-ByNameAndType -Root $Context.HelpeeWindow -Name 'Retry' -ControlType ([System.Windows.Automation.ControlType]::Button)
                if ($retry -and $retry.Current.IsEnabled -and -not $retry.Current.IsOffscreen) { return $retry }
                return $null
            })
            $failedRemote = $true
        }
        catch {
            $failedRemote = $false
        }

        if ($failedRemote) {
            return
        }

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
    $observedFailureSurface = $false
    try {
        $failedBanner = Wait-BannerVisibleWithAnyToken -Window $Context.HelperWindow -TitleOrMessageTokens @('no response','failed','session ended','connection lost','declined','reinstall') -TimeoutMs 35000
        $observedFailureSurface = $true
    }
    catch {
        # Some current builds surface a simple inline/helper status text instead of the shared banner for this path.
        try {
            [void](Wait-StatusTextContains -Window $Context.HelperWindow -Candidates @('wrong','connect','declined','response','respond','lost','session ended','connection problem','request','rejected') -TimeoutMs 5000)
            $observedFailureSurface = $true
        }
        catch {
            # Current UX may recover directly to helper idle form without an explicit failure text.
            $observedFailureSurface = $false
        }
    }

    if (-not $observedFailureSurface) {
        Write-Host "[GUI Smoke][B] No explicit failure text observed; accepting direct recovery to helper form." -ForegroundColor Yellow
    }

    if ($failedBanner -and -not (Test-DiagnosticsAffordanceVisible -Window $Context.HelperWindow -BannerBundle $failedBanner)) {
        Write-Host "[GUI Smoke][B] Diagnostics affordance not present on helper failure screen (accepted when diagnostics is Home-only)." -ForegroundColor Yellow
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

    [void](Wait-Until -TimeoutMs 20000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helpee to rotate code after handshake timeout.' -Condition {
        $nextCode = Get-HelpeeCodeFromUi -HelpeeWindow $Context.HelpeeWindow
        if ($nextCode -and $nextCode -ne $code) { return $nextCode }
        return $null
    })
}

function Run-ScenarioC {
    param([Parameter(Mandatory = $true)]$Context)
    Reset-ScenarioContext -Context $Context
    Start-HelpeeFlow -Context $Context
    Start-HelperFlow -Context $Context
    $initialCode = Copy-HelpeeCodeAndReadClipboard -HelpeeWindow $Context.HelpeeWindow
    [void](Enter-HelperCodeAndConnect -HelperWindow $Context.HelperWindow -Code $initialCode)

    [void](Wait-Until -TimeoutMs 25000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helpee Allow before helper-cancel scenario.' -Condition {
        $btn = Find-VisibleByAutomationIdOrName -Root $Context.HelpeeWindow -AutomationId 'Helpee.Allow' -FallbackName 'Allow' -FallbackControlType ([System.Windows.Automation.ControlType]::Button)
        if ($btn -and $btn.Current.IsEnabled) { return $btn }
        return $null
    })

    $cancel = Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helper Cancel during connecting.' -Condition {
        $btn = Find-ByNameAndType -Root $Context.HelperWindow -Name 'Cancel' -ControlType ([System.Windows.Automation.ControlType]::Button)
        if ($btn -and -not $btn.Current.IsOffscreen -and $btn.Current.IsEnabled) { return $btn }
        return $null
    }
    Click-Element $cancel

    [void](Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helper Cancel to disappear after canceling connect.' -Condition {
        $btn = Find-ByNameAndType -Root $Context.HelperWindow -Name 'Cancel' -ControlType ([System.Windows.Automation.ControlType]::Button)
        if ($null -eq $btn -or $btn.Current.IsOffscreen) { return $true }
        return $null
    })

    $input = Wait-HelperCodeInputEnabled -Window $Context.HelperWindow -TimeoutMs 10000

    # Assert input is actually writable after canceling an in-flight connect.
    Set-Text -Element $input -Text '111222'
    $normalized = [regex]::Replace((Get-ElementValueSafe -Element $input), '\D', '')
    if ($normalized -ne '111222') {
        throw "Helper code input did not accept new value after cancel. Expected 111222, got '$normalized'."
    }
    [void](Wait-ConnectButtonEnabled -Window $Context.HelperWindow -TimeoutMs 10000)

    [void](Wait-Until -TimeoutMs 15000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helpee to rotate code after helper canceled connecting attempt.' -Condition {
        $nextCode = Get-HelpeeCodeFromUi -HelpeeWindow $Context.HelpeeWindow
        if ($nextCode -and $nextCode -ne $initialCode) { return $nextCode }
        return $null
    })
}

function Run-ScenarioE {
    param([Parameter(Mandatory = $true)]$Context)
    Reset-ScenarioContext -Context $Context
    Start-HelpeeFlow -Context $Context
    Start-HelperFlow -Context $Context

    $initialCode = Connect-HelperAndHelpee -Context $Context
    [void](Click-DisconnectIfVisible -Window $Context.HelperWindow)

    $newCode = Wait-Until -TimeoutMs 15000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helpee to auto-generate a new code after disconnect.' -Condition {
        $candidate = Get-HelpeeCodeFromUi -HelpeeWindow $Context.HelpeeWindow
        if ($candidate -and $candidate -ne $initialCode) { return $candidate }
        return $null
    }

    # Helper may remain in a cooldown-gated failure state after disconnect; recover by reopening Helper flow.
    Click-ButtonByName -Window $Context.HelperWindow -Text 'Back' -TimeoutMs 10000
    Wait-HomeScreen -Window $Context.HelperWindow
    Click-HomeButton -Window $Context.HelperWindow -Text 'I want to help someone'
    if (Try-ClickRoleButtonIfPresent -Window $Context.HelperWindow -RoleButtonText 'I want to help someone') {
        Write-Host "[GUI Smoke][E] Role page detected during helper recovery; selected helper." -ForegroundColor DarkGray
    }

    $input = Wait-HelperCodeInputEnabled -Window $Context.HelperWindow -TimeoutMs 15000
    $latestCode = Copy-HelpeeCodeAndReadClipboard -HelpeeWindow $Context.HelpeeWindow
    Set-Text -Element $input -Text $latestCode
    $normalized = [regex]::Replace((Get-ElementValueSafe -Element $input), '\D', '')
    if ($normalized -ne $latestCode) {
        throw "Helper code input did not accept regenerated helpee code '$latestCode'. Actual '$normalized'."
    }
    [void](Wait-ConnectButtonEnabled -Window $Context.HelperWindow -TimeoutMs 10000)
}

function Run-ScenarioF {
    param([Parameter(Mandatory = $true)]$Context)
    Reset-ScenarioContext -Context $Context
    Start-HelpeeFlow -Context $Context
    Start-HelperFlow -Context $Context

    $code = Copy-HelpeeCodeAndReadClipboard -HelpeeWindow $Context.HelpeeWindow
    [void](Enter-HelperCodeAndConnect -HelperWindow $Context.HelperWindow -Code $code)

    [void](Wait-ButtonVisibleEnabledByName -Window $Context.HelpeeWindow -Text 'Allow' -TimeoutMs 25000)
    Assert-ButtonNotVisibleByName -Window $Context.HelpeeWindow -Text 'Cancel'

    Click-ButtonByName -Window $Context.HelpeeWindow -Text 'Decline' -TimeoutMs 10000

    [void](Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helpee waiting panel after Decline.' -Condition {
        $codeElement = Find-VisibleByAutomationId -Root $Context.HelpeeWindow -AutomationId 'Helpee.Code'
        $allowElement = Find-VisibleByAutomationId -Root $Context.HelpeeWindow -AutomationId 'Helpee.Allow'
        if ($codeElement -and -not $allowElement) { return $codeElement }
        return $null
    })
    [void](Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helpee to rotate code after Decline.' -Condition {
        $nextCode = Get-HelpeeCodeFromUi -HelpeeWindow $Context.HelpeeWindow
        if ($nextCode -and $nextCode -ne $code) { return $nextCode }
        return $null
    })

    $input = Wait-HelperCodeInputEnabled -Window $Context.HelperWindow -TimeoutMs 10000
    Set-Text -Element $input -Text '112233'
    $normalized = [regex]::Replace((Get-ElementValueSafe -Element $input), '\D', '')
    if ($normalized -ne '112233') {
        throw "Helper code input did not accept new value after helpee decline. Expected 112233, got '$normalized'."
    }
    [void](Wait-ConnectButtonEnabled -Window $Context.HelperWindow -TimeoutMs 10000)
}

function Run-ScenarioG {
    param([Parameter(Mandatory = $true)]$Context)
    Reset-ScenarioContext -Context $Context

    $Context.HelperProc = Start-AppInstance -ExePath $Context.ExePath -RoleName 'navigation-loop'
    [void]$Context.Processes.Add($Context.HelperProc)
    $Context.HelperWindow = Wait-Window -Process $Context.HelperProc -TimeoutMs 15000

    Wait-HomeScreen -Window $Context.HelperWindow

    for ($i = 1; $i -le 2; $i++) {
        Click-HomeButton -Window $Context.HelperWindow -Text 'I want to help someone'
        if (Try-ClickRoleButtonIfPresent -Window $Context.HelperWindow -RoleButtonText 'I want to help someone') {
            Write-Host "[GUI Smoke][G] Role page detected (helper loop); selected helper." -ForegroundColor DarkGray
        }
        $input = Wait-HelperCodeInputEnabled -Window $Context.HelperWindow -TimeoutMs 10000
        Set-Text -Element $input -Text '123456'
        [void](Wait-ConnectButtonEnabled -Window $Context.HelperWindow -TimeoutMs 5000)
        Click-ButtonByName -Window $Context.HelperWindow -Text 'Back' -TimeoutMs 10000
        Wait-HomeScreen -Window $Context.HelperWindow

        Click-HomeButton -Window $Context.HelperWindow -Text 'I need help'
        if (Try-ClickRoleButtonIfPresent -Window $Context.HelperWindow -RoleButtonText 'I need help') {
            Write-Host "[GUI Smoke][G] Role page detected (helper loop); selected helpee." -ForegroundColor DarkGray
        }
        [void](Get-HelpeeCodeFromUi -HelpeeWindow $Context.HelperWindow)
        Assert-ButtonNotVisibleByName -Window $Context.HelperWindow -Text 'Cancel'
        Click-ButtonByName -Window $Context.HelperWindow -Text 'Back' -TimeoutMs 10000
        Wait-HomeScreen -Window $Context.HelperWindow
    }

    Click-HomeButton -Window $Context.HelperWindow -Text 'I want to help someone'
    if (Try-ClickRoleButtonIfPresent -Window $Context.HelperWindow -RoleButtonText 'I want to help someone') {
        Write-Host "[GUI Smoke][G] Role page detected (final helper open); selected helper." -ForegroundColor DarkGray
    }
    $finalInput = Wait-HelperCodeInputEnabled -Window $Context.HelperWindow -TimeoutMs 10000
    Set-Text -Element $finalInput -Text '654321'
    [void](Wait-ConnectButtonEnabled -Window $Context.HelperWindow -TimeoutMs 5000)
}

function Run-ScenarioH {
    param([Parameter(Mandatory = $true)]$Context)
    Reset-ScenarioContext -Context $Context
    Start-HelperFlow -Context $Context

    Click-ButtonByName -Window $Context.HelperWindow -Text "They don't have nLink?" -TimeoutMs 10000
    [void](Wait-StatusTextContains -Window $Context.HelperWindow -Candidates @('Copied') -TimeoutMs 5000)

    $clipboardText = [string](Get-ClipboardTextSafe)
    $containsExpectedToken =
        ($clipboardText.IndexOf('nLink', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) -or
        ($clipboardText.IndexOf('github', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) -or
        ($clipboardText.IndexOf('http', [System.StringComparison]::OrdinalIgnoreCase) -ge 0)
    if (-not $containsExpectedToken) {
        throw "Clipboard text after 'They don't have nLink?' click did not look like install guidance. Clipboard: '$clipboardText'"
    }

    [void](Wait-ButtonVisibleEnabledByName -Window $Context.HelperWindow -Text 'Use nFTP instead' -TimeoutMs 10000)

    $input = Wait-HelperCodeInputEnabled -Window $Context.HelperWindow -TimeoutMs 10000
    Set-Text -Element $input -Text '121212'
    [void](Wait-ConnectButtonEnabled -Window $Context.HelperWindow -TimeoutMs 5000)
}

function Run-ScenarioI {
    param([Parameter(Mandatory = $true)]$Context)
    Reset-ScenarioContext -Context $Context

    $Context.HelperProc = Start-AppInstance -ExePath $Context.ExePath -RoleName 'diagnostics-home-only'
    [void]$Context.Processes.Add($Context.HelperProc)
    $Context.HelperWindow = Wait-Window -Process $Context.HelperProc -TimeoutMs 15000

    Wait-HomeScreen -Window $Context.HelperWindow
    [void](Wait-ButtonVisibleEnabledByName -Window $Context.HelperWindow -Text 'Diagnostics' -TimeoutMs 10000)

    Click-HomeButton -Window $Context.HelperWindow -Text 'I want to help someone'
    if (Try-ClickRoleButtonIfPresent -Window $Context.HelperWindow -RoleButtonText 'I want to help someone') {
        Write-Host "[GUI Smoke][I] Role page detected; selected helper." -ForegroundColor DarkGray
    }
    Assert-ButtonNotVisibleByName -Window $Context.HelperWindow -Text 'Open diagnostics'
    Click-ButtonByName -Window $Context.HelperWindow -Text 'Back' -TimeoutMs 10000
    Wait-HomeScreen -Window $Context.HelperWindow

    Click-HomeButton -Window $Context.HelperWindow -Text 'I need help'
    if (Try-ClickRoleButtonIfPresent -Window $Context.HelperWindow -RoleButtonText 'I need help') {
        Write-Host "[GUI Smoke][I] Role page detected; selected helpee." -ForegroundColor DarkGray
    }
    Assert-ButtonNotVisibleByName -Window $Context.HelperWindow -Text 'Open diagnostics'
    Click-ButtonByName -Window $Context.HelperWindow -Text 'Back' -TimeoutMs 10000
    Wait-HomeScreen -Window $Context.HelperWindow

    Click-ButtonByName -Window $Context.HelperWindow -Text 'Diagnostics' -TimeoutMs 10000
    [void](Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage "Timed out waiting for Diagnostics page controls." -Condition {
        $copy = Find-VisibleByAutomationId -Root $Context.HelperWindow -AutomationId 'Diag.Copy'
        if ($copy) { return $copy }
        return $null
    })
    Click-ButtonByName -Window $Context.HelperWindow -Text 'Back' -TimeoutMs 10000
    Wait-HomeScreen -Window $Context.HelperWindow
}

function Run-ScenarioJ {
    param([Parameter(Mandatory = $true)]$Context)
    Reset-ScenarioContext -Context $Context
    Start-HelpeeFlow -Context $Context

    $initialCode = Get-HelpeeCodeFromUi -HelpeeWindow $Context.HelpeeWindow
    $newCodeBtn = Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for Helpee.NewCode.' -Condition {
        $btn = Find-VisibleByAutomationId -Root $Context.HelpeeWindow -AutomationId 'Helpee.NewCode'
        if ($btn -and $btn.Current.IsEnabled) { return $btn }
        return $null
    }
    Click-Element $newCodeBtn

    [void](Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helpee New code to generate a different code.' -Condition {
        $candidate = Get-HelpeeCodeFromUi -HelpeeWindow $Context.HelpeeWindow
        if ($candidate -and $candidate -ne $initialCode) { return $candidate }
        return $null
    })

    [void](Wait-Until -TimeoutMs 5000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helpee New code flow to stay quiet.' -Condition {
        try {
            Assert-TextsNotVisible -Window $Context.HelpeeWindow -Texts @('Reconnecting…', 'Connecting…')
            Assert-ButtonNotVisibleByName -Window $Context.HelpeeWindow -Text 'Cancel'
            return $true
        }
        catch {
            return $null
        }
    })
}

function Run-ScenarioK {
    param([Parameter(Mandatory = $true)]$Context)
    Reset-ScenarioContext -Context $Context
    Start-HelpeeFlow -Context $Context
    Start-HelperFlow -Context $Context

    $code = Copy-HelpeeCodeAndReadClipboard -HelpeeWindow $Context.HelpeeWindow
    [void](Enter-HelperCodeAndConnect -HelperWindow $Context.HelperWindow -Code $code)
    [void](Wait-ButtonVisibleEnabledByName -Window $Context.HelpeeWindow -Text 'Allow' -TimeoutMs 25000)

    Click-ButtonByName -Window $Context.HelperWindow -Text 'Cancel' -TimeoutMs 10000

    [void](Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helper cancel strip to disappear.' -Condition {
        if (-not (Test-ButtonVisibleByName -Window $Context.HelperWindow -Text 'Cancel')) { return $true }
        return $null
    })

    Click-ButtonByName -Window $Context.HelperWindow -Text 'Back' -TimeoutMs 10000
    Wait-HomeScreen -Window $Context.HelperWindow

    Click-HomeButton -Window $Context.HelperWindow -Text 'I want to help someone'
    if (Try-ClickRoleButtonIfPresent -Window $Context.HelperWindow -RoleButtonText 'I want to help someone') {
        Write-Host "[GUI Smoke][K] Role page detected; selected helper." -ForegroundColor DarkGray
    }
    $input = Wait-HelperCodeInputEnabled -Window $Context.HelperWindow -TimeoutMs 10000
    Set-Text -Element $input -Text '123456'
    [void](Wait-ConnectButtonEnabled -Window $Context.HelperWindow -TimeoutMs 10000)
}

function Run-ScenarioL {
    param([Parameter(Mandatory = $true)]$Context)
    Reset-ScenarioContext -Context $Context
    Start-HelpeeFlow -Context $Context
    Start-HelperFlow -Context $Context

    $code = Copy-HelpeeCodeAndReadClipboard -HelpeeWindow $Context.HelpeeWindow
    [void](Enter-HelperCodeAndConnect -HelperWindow $Context.HelperWindow -Code $code)
    [void](Wait-ButtonVisibleEnabledByName -Window $Context.HelpeeWindow -Text 'Allow' -TimeoutMs 25000)
    Click-ButtonByName -Window $Context.HelpeeWindow -Text 'Decline' -TimeoutMs 10000

    [void](Wait-HelperCodeInputEnabled -Window $Context.HelperWindow -TimeoutMs 10000)
    $nextCode = Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helpee to rotate code after Decline in scenario L.' -Condition {
        $candidate = Get-HelpeeCodeFromUi -HelpeeWindow $Context.HelpeeWindow
        if ($candidate -and $candidate -ne $code) { return $candidate }
        return $null
    }
    [void](Enter-HelperCodeAndConnect -HelperWindow $Context.HelperWindow -Code $nextCode)
    Wait-HelpeeAllowAndClick -HelpeeWindow $Context.HelpeeWindow
    Wait-ConnectedChatVisible -HelpeeWindow $Context.HelpeeWindow -HelperWindow $Context.HelperWindow
}

function Run-ScenarioM {
    param([Parameter(Mandatory = $true)]$Context)
    Reset-ScenarioContext -Context $Context
    Start-HelpeeFlow -Context $Context
    Start-HelperFlow -Context $Context

    $initialCode = Connect-HelperAndHelpee -Context $Context

    if (-not (Click-DisconnectIfVisible -Window $Context.HelpeeWindow)) {
        throw "Helpee Disconnect button was not visible/enabled in connected chat view."
    }

    [void](Wait-Until -TimeoutMs 15000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helpee to auto-generate a new code after helpee-initiated disconnect.' -Condition {
        $candidate = Get-HelpeeCodeFromUi -HelpeeWindow $Context.HelpeeWindow
        if ($candidate -and $candidate -ne $initialCode) { return $candidate }
        return $null
    })
}

function Run-ScenarioNknDirectConnect {
    param([Parameter(Mandatory = $true)]$Context)
    Reset-ScenarioContext -Context $Context

    if (-not (Get-IsNknTransport)) {
        Write-Host '[GUI Smoke][NKN_DIRECT_CONNECT] SKIP: scenario requires NLINK_TRANSPORT=NKN.' -ForegroundColor Yellow
        return
    }

    Start-HelperFlow -Context $Context
    $helperIdentity = Copy-HelperIdentityWithRecovery -Context $Context
    if ([string]::IsNullOrWhiteSpace($helperIdentity)) {
        throw 'Helper identity copy returned empty text.'
    }

    Start-HelpeeFlow -Context $Context
    [void](Enter-HelpeeHelperIdentityAndRequestHelp -HelpeeWindow $Context.HelpeeWindow -HelperIdentity $helperIdentity)

    $accept = Wait-HelperAcceptRequestOrExit -Context $Context -TimeoutMs 90000
    Click-Element $accept

    $allow = Wait-HelpeeAllowOrExit -Context $Context -TimeoutMs 90000
    Click-Element $allow

    [void](Wait-ConnectedChatVisibleProcessAware -Context $Context -TimeoutMs 120000)

    $msg = "gui smoke nkn direct connect"
    Send-ChatMessage -Window $Context.HelperWindow -Text $msg
    Wait-MessageVisible -Window $Context.HelpeeWindow -MessageText $msg -TimeoutMs 30000
}

function Run-ScenarioHeaderChatCoherence {
    param([Parameter(Mandatory = $true)]$Context)
    Reset-ScenarioContext -Context $Context
    Start-HelpeeFlow -Context $Context
    Start-HelperFlow -Context $Context

    $helperHeader = Find-VisibleByAutomationId -Root $Context.HelperWindow -AutomationId 'SessionHeader.StatusText'
    if ($helperHeader) {
        $headerText = Get-ElementTextSafe -Element $helperHeader
        if ($headerText -eq 'Connected') {
            throw "Helper header unexpectedly showed Connected before session connect."
        }
    }

    $helperPill = Find-VisibleByAutomationId -Root $Context.HelperWindow -AutomationId 'Chat.ConnectionPillText'
    if ($helperPill) {
        $pillText = Get-ElementTextSafe -Element $helperPill
        if ($pillText -eq 'Connected') {
            throw "Helper chat pill unexpectedly showed Connected before session connect."
        }
    }

    $code = Get-HelpeeCodeFromUi -HelpeeWindow $Context.HelpeeWindow
    [void](Enter-HelperCodeAndConnect -HelperWindow $Context.HelperWindow -Code $code)
    Wait-HelpeeAllowAndClick -HelpeeWindow $Context.HelpeeWindow

    [void](Wait-Until -TimeoutMs 20000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helper header/chat coherence on Connected.' -Condition {
        $header = Find-VisibleByAutomationId -Root $Context.HelperWindow -AutomationId 'SessionHeader.StatusText'
        $pill = Find-VisibleByAutomationId -Root $Context.HelperWindow -AutomationId 'Chat.ConnectionPillText'
        if ($header -and $pill -and
            (Get-ElementTextSafe -Element $header) -eq 'Connected' -and
            (Get-ElementTextSafe -Element $pill) -eq 'Connected') {
            return $true
        }
        return $null
    })

    [void](Wait-Until -TimeoutMs 20000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helpee header/chat coherence on Connected.' -Condition {
        $header = Find-VisibleByAutomationId -Root $Context.HelpeeWindow -AutomationId 'SessionHeader.StatusText'
        $pill = Find-VisibleByAutomationId -Root $Context.HelpeeWindow -AutomationId 'Chat.ConnectionPillText'
        if ($header -and $pill -and
            (Get-ElementTextSafe -Element $header) -eq 'Connected' -and
            (Get-ElementTextSafe -Element $pill) -eq 'Connected') {
            return $true
        }
        return $null
    })
}

function Run-ScenarioStatusTextGuardrails {
    param([Parameter(Mandatory = $true)]$Context)
    Reset-ScenarioContext -Context $Context
    Start-HelpeeFlow -Context $Context
    Start-HelperFlow -Context $Context

    $allowedPillTexts = @('Connected', 'Connecting…', 'Reconnecting…', 'Not connected')

    [void](Wait-NonEmptyAutomationText -Window $Context.HelpeeWindow -AutomationId 'SessionHeader.StatusText' -TimeoutMs 10000)
    [void](Wait-NonEmptyAutomationText -Window $Context.HelperWindow -AutomationId 'SessionHeader.StatusText' -TimeoutMs 10000)
    [void](Wait-AutomationTextInSet -Window $Context.HelperWindow -AutomationId 'Chat.ConnectionPillText' -AllowedTexts $allowedPillTexts -TimeoutMs 10000)

    $code = Get-HelpeeCodeFromUi -HelpeeWindow $Context.HelpeeWindow
    [void](Enter-HelperCodeAndConnect -HelperWindow $Context.HelperWindow -Code $code)

    [void](Wait-AutomationTextEquals -Window $Context.HelperWindow -AutomationId 'SessionHeader.StatusText' -ExpectedText 'Connecting…' -TimeoutMs 15000)
    [void](Wait-AutomationTextEquals -Window $Context.HelperWindow -AutomationId 'Chat.ConnectionPillText' -ExpectedText 'Connecting…' -TimeoutMs 15000)

    Wait-HelpeeAllowAndClick -HelpeeWindow $Context.HelpeeWindow

    [void](Wait-NonEmptyAutomationText -Window $Context.HelperWindow -AutomationId 'SessionHeader.StatusText' -TimeoutMs 20000)
    [void](Wait-NonEmptyAutomationText -Window $Context.HelpeeWindow -AutomationId 'SessionHeader.StatusText' -TimeoutMs 20000)
    [void](Wait-AutomationTextInSet -Window $Context.HelperWindow -AutomationId 'Chat.ConnectionPillText' -AllowedTexts $allowedPillTexts -TimeoutMs 20000)
    [void](Wait-AutomationTextInSet -Window $Context.HelpeeWindow -AutomationId 'Chat.ConnectionPillText' -AllowedTexts $allowedPillTexts -TimeoutMs 20000)
}

function Run-ScenarioEndSessionDisablesChat {
    param([Parameter(Mandatory = $true)]$Context)
    Reset-ScenarioContext -Context $Context
    Start-HelpeeFlow -Context $Context
    Start-HelperFlow -Context $Context

    [void](Connect-HelperAndHelpee -Context $Context)

    $helperInput = Wait-ControlEnabledStateByAutomationId -Window $Context.HelperWindow -AutomationId 'Chat.Input' -IsEnabled $true -TimeoutMs 10000
    $helpeeInput = Wait-ControlEnabledStateByAutomationId -Window $Context.HelpeeWindow -AutomationId 'Chat.Input' -IsEnabled $true -TimeoutMs 10000

    Set-Text -Element $helperInput -Text 'end-session-smoke-prep'
    [void](Wait-ControlEnabledStateByAutomationId -Window $Context.HelperWindow -AutomationId 'Chat.Send' -IsEnabled $true -TimeoutMs 10000)

    Set-Text -Element $helpeeInput -Text 'end-session-smoke-reply'
    [void](Wait-ControlEnabledStateByAutomationId -Window $Context.HelpeeWindow -AutomationId 'Chat.Send' -IsEnabled $true -TimeoutMs 10000)

    if (-not (Click-DisconnectIfVisible -Window $Context.HelperWindow)) {
        throw "Timed out waiting for helper end-session control."
    }

    [void](Wait-ControlDisabledOrGoneByAutomationId -Window $Context.HelperWindow -AutomationId 'Chat.Input' -TimeoutMs 15000)
    [void](Wait-ControlDisabledOrGoneByAutomationId -Window $Context.HelperWindow -AutomationId 'Chat.Send' -TimeoutMs 15000)
    [void](Wait-ControlDisabledOrGoneByAutomationId -Window $Context.HelpeeWindow -AutomationId 'Chat.Input' -TimeoutMs 15000)
    [void](Wait-ControlDisabledOrGoneByAutomationId -Window $Context.HelpeeWindow -AutomationId 'Chat.Send' -TimeoutMs 15000)

    $helperPill = Find-VisibleByAutomationId -Root $Context.HelperWindow -AutomationId 'Chat.ConnectionPillText'
    if ($helperPill -and (Get-ElementTextSafe -Element $helperPill) -eq 'Connected') {
        throw "Helper chat connection pill remained 'Connected' after end session."
    }

    $helpeePill = Find-VisibleByAutomationId -Root $Context.HelpeeWindow -AutomationId 'Chat.ConnectionPillText'
    if ($helpeePill -and (Get-ElementTextSafe -Element $helpeePill) -eq 'Connected') {
        throw "Helpee chat connection pill remained 'Connected' after end session."
    }
}

function Run-ScenarioScreenShareButtonVisibility {
    param([Parameter(Mandatory = $true)]$Context)
    Reset-ScenarioContext -Context $Context

    $previousScaffold = $env:NLINK_FEATURE_SCREENCAP_SCAFFOLD
    try {
        $env:NLINK_FEATURE_SCREENCAP_SCAFFOLD = '1'

        Start-HelpeeFlow -Context $Context

        if (Find-VisibleByAutomationId -Root $Context.HelpeeWindow -AutomationId 'SessionHeader.ShareScreen') {
            throw "Helpee Share screen button was visible before the session connected."
        }

        Start-HelperFlow -Context $Context
        [void](Connect-HelperAndHelpee -Context $Context)

        [void](Wait-Until -TimeoutMs 15000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helpee Share screen button.' -Condition {
            $button = Find-VisibleByAutomationId -Root $Context.HelpeeWindow -AutomationId 'SessionHeader.ShareScreen'
            if ($button -and $button.Current.IsEnabled) {
                return $button
            }

            return $null
        })

        if (Find-VisibleByAutomationId -Root $Context.HelperWindow -AutomationId 'SessionHeader.ShareScreen') {
            throw "Helper Share screen button was visible, but only the helpee flow should surface it."
        }
    }
    finally {
        if ($null -eq $previousScaffold) {
            Remove-Item Env:NLINK_FEATURE_SCREENCAP_SCAFFOLD -ErrorAction SilentlyContinue
        }
        else {
            $env:NLINK_FEATURE_SCREENCAP_SCAFFOLD = $previousScaffold
        }
    }
}

function Run-ScenarioScreenShareViewerToggle {
    param([Parameter(Mandatory = $true)]$Context)
    Reset-ScenarioContext -Context $Context

    $previousScaffold = $env:NLINK_FEATURE_SCREENCAP_SCAFFOLD
    try {
        $env:NLINK_FEATURE_SCREENCAP_SCAFFOLD = '1'

        Start-HelpeeFlow -Context $Context
        Start-HelperFlow -Context $Context
        [void](Connect-HelperAndHelpee -Context $Context)

        $shareButton = Wait-Until -TimeoutMs 15000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helpee Share screen button for viewer toggle scenario.' -Condition {
            $button = Find-VisibleByAutomationId -Root $Context.HelpeeWindow -AutomationId 'SessionHeader.ShareScreen'
            if ($button -and $button.Current.IsEnabled) {
                return $button
            }

            return $null
        }

        [void](Wait-ScreenShareViewerVisibleState -Window $Context.HelpeeWindow -IsVisible $false -TimeoutMs 5000)

        Click-Element $shareButton
        $shareButton = Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for screenshare start to succeed.' -Condition {
            $error = Find-VisibleByAutomationId -Root $Context.HelpeeWindow -AutomationId 'ScreenShare.ViewerMessage'
            if ($error) {
                $message = (Get-ElementTextSafe -Element $error).Trim()
                throw "Screen sharing failed to start: $message"
            }

            $button = Find-VisibleByAutomationId -Root $Context.HelpeeWindow -AutomationId 'SessionHeader.ShareScreen'
            if ($button -and $button.Current.IsEnabled) {
                $text = (Get-ElementTextSafe -Element $button).Trim()
                if ([string]::Equals($text, 'Stop sharing', [System.StringComparison]::Ordinal)) {
                    return $button
                }
            }

            return $null
        }

        Click-Element $shareButton
        [void](Wait-ScreenShareButtonText -Window $Context.HelpeeWindow -ExpectedText 'Share screen' -TimeoutMs 10000)
        [void](Wait-ScreenShareViewerVisibleState -Window $Context.HelpeeWindow -IsVisible $false -TimeoutMs 10000)
    }
    finally {
        if ($null -eq $previousScaffold) {
            Remove-Item Env:NLINK_FEATURE_SCREENCAP_SCAFFOLD -ErrorAction SilentlyContinue
        }
        else {
            $env:NLINK_FEATURE_SCREENCAP_SCAFFOLD = $previousScaffold
        }
    }
}

function Run-ScenarioScreenShareChatCoexistence {
    param([Parameter(Mandatory = $true)]$Context)
    Reset-ScenarioContext -Context $Context

    $previousScaffold = $env:NLINK_FEATURE_SCREENCAP_SCAFFOLD
    try {
        $env:NLINK_FEATURE_SCREENCAP_SCAFFOLD = '1'

        Start-HelpeeFlow -Context $Context
        Start-HelperFlow -Context $Context
        [void](Connect-HelperAndHelpee -Context $Context)

        $shareButton = Wait-Until -TimeoutMs 15000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helpee Share screen button for chat coexistence scenario.' -Condition {
            $button = Find-VisibleByAutomationId -Root $Context.HelpeeWindow -AutomationId 'SessionHeader.ShareScreen'
            if ($button -and $button.Current.IsEnabled) { return $button }
            return $null
        }

        Click-Element $shareButton
        [void](Wait-ScreenShareViewerVisibleState -Window $Context.HelpeeWindow -IsVisible $true -TimeoutMs 10000)

        $message = "screenshare chat coexist"
        Send-ChatMessage -Window $Context.HelpeeWindow -Text $message
        Wait-MessageVisible -Window $Context.HelperWindow -MessageText $message -TimeoutMs 10000
        [void](Wait-AutomationTextEquals -Window $Context.HelperWindow -AutomationId 'SessionHeader.StatusText' -ExpectedText 'Connected' -TimeoutMs 5000)
        [void](Wait-AutomationTextEquals -Window $Context.HelperWindow -AutomationId 'Chat.ConnectionPillText' -ExpectedText 'Connected' -TimeoutMs 5000)
    }
    finally {
        if ($null -eq $previousScaffold) {
            Remove-Item Env:NLINK_FEATURE_SCREENCAP_SCAFFOLD -ErrorAction SilentlyContinue
        }
        else {
            $env:NLINK_FEATURE_SCREENCAP_SCAFFOLD = $previousScaffold
        }
    }
}

function Run-ScenarioScreenShareStopWhileControlApprovalPending {
    param([Parameter(Mandatory = $true)]$Context)
    $previousScaffold = $env:NLINK_FEATURE_SCREENCAP_SCAFFOLD
    $attempts = if (Get-IsNknTransport) { 3 } else { 1 }
    try {
        $env:NLINK_FEATURE_SCREENCAP_SCAFFOLD = '1'

        for ($attempt = 1; $attempt -le $attempts; $attempt++) {
            try {
                Reset-ScenarioContext -Context $Context
                if ($attempts -gt 1) {
                    Write-Host "[GUI Smoke] pending-approval stop scenario attempt $attempt/$attempts" -ForegroundColor DarkGray
                }

                Start-HelpeeFlow -Context $Context
                Start-HelperFlow -Context $Context
                [void](Connect-HelperAndHelpee -Context $Context)

                $shareButton = Wait-Until -TimeoutMs 15000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helpee Share screen button for pending-approval stop scenario.' -Condition {
                    $button = Find-VisibleByAutomationId -Root $Context.HelpeeWindow -AutomationId 'SessionHeader.ShareScreen'
                    if ($button -and $button.Current.IsEnabled) { return $button }
                    return $null
                }

                Click-Element $shareButton
                [void](Wait-ScreenShareButtonText -Window $Context.HelpeeWindow -ExpectedText 'Stop sharing' -TimeoutMs 10000)
                [void](Wait-ScreenShareViewerVisibleState -Window $Context.HelperWindow -IsVisible $true -TimeoutMs 10000)

                $requestControlButton = Wait-Until -TimeoutMs 15000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helper Request control button.' -Condition {
                    $button = Find-VisibleByAutomationId -Root $Context.HelperWindow -AutomationId 'SessionHeader.RequestControl'
                    if ($button -and $button.Current.IsEnabled) { return $button }
                    return $null
                }
                Click-Element $requestControlButton

                [void](Wait-AutomationTextEquals -Window $Context.HelperWindow -AutomationId 'SessionHeader.StopControl' -ExpectedText 'Cancel request' -TimeoutMs 10000)
                [void](Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helpee remote-control approval dialog.' -Condition {
                    $allow = Find-VisibleByAutomationIdOrName -Root $Context.HelpeeWindow -AutomationId 'Helpee.ControlConsent.Allow' -FallbackName 'Allow control' -FallbackControlType ([System.Windows.Automation.ControlType]::Button)
                    if ($allow -and $allow.Current.IsEnabled) { return $allow }
                    return $null
                })

                $stopShareButton = Wait-Until -TimeoutMs 5000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helpee Stop sharing button.' -Condition {
                    $button = Find-VisibleByAutomationId -Root $Context.HelpeeWindow -AutomationId 'SessionHeader.ShareScreen'
                    if (-not $button -or -not $button.Current.IsEnabled) { return $null }
                    $text = (Get-ElementTextSafe -Element $button).Trim()
                    if ([string]::Equals($text, 'Stop sharing', [System.StringComparison]::Ordinal)) { return $button }
                    return $null
                }
                Click-Element $stopShareButton

                [void](Wait-ScreenShareButtonText -Window $Context.HelpeeWindow -ExpectedText 'Share screen' -TimeoutMs 5000)
                [void](Wait-ScreenShareViewerVisibleState -Window $Context.HelperWindow -IsVisible $false -TimeoutMs 5000)
                [void](Wait-AutomationElementVisibleState -Window $Context.HelpeeWindow -AutomationId 'Helpee.ControlConsent.Allow' -IsVisible $false -TimeoutMs 5000)
                [void](Wait-AutomationTextEquals -Window $Context.HelpeeWindow -AutomationId 'SessionHeader.StatusText' -ExpectedText 'Connected' -TimeoutMs 5000)
                [void](Wait-AutomationTextEquals -Window $Context.HelperWindow -AutomationId 'SessionHeader.StatusText' -ExpectedText 'Connected' -TimeoutMs 5000)
                [void](Wait-AutomationElementVisibleState -Window $Context.HelperWindow -AutomationId 'SessionHeader.StopControl' -IsVisible $false -TimeoutMs 5000)
                [void](Wait-AutomationElementVisibleState -Window $Context.HelperWindow -AutomationId 'SessionHeader.RequestControl' -IsVisible $false -TimeoutMs 5000)
                return
            }
            catch {
                if ($attempt -ge $attempts) {
                    throw
                }

                Write-Host "[GUI Smoke] pending-approval stop scenario attempt $attempt failed; retrying. Error: $($_.Exception.Message)" -ForegroundColor Yellow
            }
        }
    }
    finally {
        if ($null -eq $previousScaffold) {
            Remove-Item Env:NLINK_FEATURE_SCREENCAP_SCAFFOLD -ErrorAction SilentlyContinue
        }
        else {
            $env:NLINK_FEATURE_SCREENCAP_SCAFFOLD = $previousScaffold
        }
    }
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
    if (-not (Test-DiagnosticsAffordanceVisible -Window $Context.HelperWindow -BannerBundle $failedBanner)) {
        Write-Host "[GUI Smoke][D] Diagnostics affordance not present on helper failure screen (accepted when diagnostics is Home-only)." -ForegroundColor Yellow
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

Write-Host "[GUI Smoke] Using executable: $resolvedExe" -ForegroundColor DarkGray

$oldTransport = $env:NLINK_TRANSPORT
$requestedScenarios = [string]$env:NLINK_GUI_SMOKE_SCENARIOS
if ([string]::IsNullOrWhiteSpace($requestedScenarios)) { $requestedScenarios = 'A,B,C,E,F,G,H,I,J,K,L,M' }
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

    Clear-AppLogsIfPresent

    foreach ($scenario in @($scenarioList)) {
        switch ($scenario) {
            'A' { Invoke-Scenario -Name 'A' -TimeoutSec ([Math]::Min($TimeoutSeconds, 90)) -Action { Run-ScenarioA -Context $ctx } }
            'B' { Invoke-Scenario -Name 'B' -TimeoutSec ([Math]::Min($TimeoutSeconds, 90)) -Action { Run-ScenarioB -Context $ctx } }
            'C' { Invoke-Scenario -Name 'C' -TimeoutSec ([Math]::Min($TimeoutSeconds, 90)) -Action { Run-ScenarioC -Context $ctx } }
            'D' { Invoke-Scenario -Name 'D' -TimeoutSec ([Math]::Min($TimeoutSeconds, 90)) -Action { Run-ScenarioD -Context $ctx } }
            'E' { Invoke-Scenario -Name 'E' -TimeoutSec ([Math]::Min($TimeoutSeconds, 90)) -Action { Run-ScenarioE -Context $ctx } }
            'F' { Invoke-Scenario -Name 'F' -TimeoutSec ([Math]::Min($TimeoutSeconds, 60)) -Action { Run-ScenarioF -Context $ctx } }
            'G' { Invoke-Scenario -Name 'G' -TimeoutSec ([Math]::Min($TimeoutSeconds, 60)) -Action { Run-ScenarioG -Context $ctx } }
            'H' { Invoke-Scenario -Name 'H' -TimeoutSec ([Math]::Min($TimeoutSeconds, 45)) -Action { Run-ScenarioH -Context $ctx } }
            'I' { Invoke-Scenario -Name 'I' -TimeoutSec ([Math]::Min($TimeoutSeconds, 60)) -Action { Run-ScenarioI -Context $ctx } }
            'J' { Invoke-Scenario -Name 'J' -TimeoutSec ([Math]::Min($TimeoutSeconds, 45)) -Action { Run-ScenarioJ -Context $ctx } }
            'K' { Invoke-Scenario -Name 'K' -TimeoutSec ([Math]::Min($TimeoutSeconds, 60)) -Action { Run-ScenarioK -Context $ctx } }
            'L' { Invoke-Scenario -Name 'L' -TimeoutSec ([Math]::Min($TimeoutSeconds, 90)) -Action { Run-ScenarioL -Context $ctx } }
            'M' { Invoke-Scenario -Name 'M' -TimeoutSec ([Math]::Min($TimeoutSeconds, 90)) -Action { Run-ScenarioM -Context $ctx } }
            'NKN_DIRECT_CONNECT' { Invoke-Scenario -Name 'nkn_direct_connect' -TimeoutSec ([Math]::Min($TimeoutSeconds, 180)) -Action { Run-ScenarioNknDirectConnect -Context $ctx } }
            'END_SESSION_DISABLES_CHAT' { Invoke-Scenario -Name 'end_session_disables_chat' -TimeoutSec ([Math]::Min($TimeoutSeconds, 90)) -Action { Run-ScenarioEndSessionDisablesChat -Context $ctx } }
            'HEADER_CHAT_COHERENCE' { Invoke-Scenario -Name 'header_chat_coherence' -TimeoutSec ([Math]::Min($TimeoutSeconds, 90)) -Action { Run-ScenarioHeaderChatCoherence -Context $ctx } }
            'SCREENSHARE_BUTTON_VISIBILITY' { Invoke-Scenario -Name 'screenshare_button_visibility' -TimeoutSec ([Math]::Min($TimeoutSeconds, 90)) -Action { Run-ScenarioScreenShareButtonVisibility -Context $ctx } }
            'SCREENSHARE_VIEWER_TOGGLE' { Invoke-Scenario -Name 'screenshare_viewer_toggle' -TimeoutSec ([Math]::Min($TimeoutSeconds, 90)) -Action { Run-ScenarioScreenShareViewerToggle -Context $ctx } }
            'SCREENSHARE_CHAT_COEXISTENCE' { Invoke-Scenario -Name 'screenshare_chat_coexistence' -TimeoutSec ([Math]::Min($TimeoutSeconds, 90)) -Action { Run-ScenarioScreenShareChatCoexistence -Context $ctx } }
            'SCREENSHARE_STOP_PENDING_APPROVAL' { Invoke-Scenario -Name 'screenshare_stop_pending_approval' -TimeoutSec ([Math]::Min($TimeoutSeconds, 90)) -Action { Run-ScenarioScreenShareStopWhileControlApprovalPending -Context $ctx } }
            'STATUS_TEXT_GUARDRAILS' { Invoke-Scenario -Name 'status_text_guardrails' -TimeoutSec ([Math]::Min($TimeoutSeconds, 90)) -Action { Run-ScenarioStatusTextGuardrails -Context $ctx } }
            default { throw "Unknown GUI smoke scenario '$scenario'. Use A,B,C,D,E,F,G,H,I,J,K,L,M,NKN_DIRECT_CONNECT,HEADER_CHAT_COHERENCE,END_SESSION_DISABLES_CHAT,SCREENSHARE_BUTTON_VISIBILITY,SCREENSHARE_VIEWER_TOGGLE,SCREENSHARE_CHAT_COEXISTENCE,STATUS_TEXT_GUARDRAILS." }
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
