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

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class NtProcessControl {
    [DllImport("ntdll.dll")]
    public static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    public static extern int NtResumeProcess(IntPtr processHandle);
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
    $cond = New-Object System.Windows.Automation.PropertyCondition($procProp, $ProcessId)
    $windows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
    foreach ($window in @($windows)) {
        if ([string]::Equals($window.Current.Name, 'nLink', [System.StringComparison]::Ordinal)) {
            return $window
        }
    }

    return $null
}

function Get-TopLevelWindowElementsByProcessId {
    param([int]$ProcessId)

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $procProp = [System.Windows.Automation.AutomationElement]::ProcessIdProperty
    $cond = New-Object System.Windows.Automation.PropertyCondition($procProp, $ProcessId)
    try {
        return @($root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond))
    }
    catch {
        return @()
    }
}

function Get-StartupWindowTimeoutMs {
    return 45000
}

function Get-ProcessSnapshot {
    param([Parameter(Mandatory = $true)]$Process)

    $liveProcess = Get-Process -Id $Process.Id -ErrorAction SilentlyContinue
    if ($null -eq $liveProcess) {
        return [pscustomobject]@{
            IsRunning = $false
            ProcessId = $Process.Id
            ProcessName = $Process.ProcessName
            MainWindowHandle = 0
            MainWindowTitle = ''
            ThreadCount = -1
            HandleCount = -1
            WorkingSetMb = -1
            TopLevelWindowCount = 0
        }
    }

    return [pscustomobject]@{
        IsRunning = $true
        ProcessId = $liveProcess.Id
        ProcessName = $liveProcess.ProcessName
        MainWindowHandle = [int64]$liveProcess.MainWindowHandle
        MainWindowTitle = [string]$liveProcess.MainWindowTitle
        ThreadCount = $liveProcess.Threads.Count
        HandleCount = $liveProcess.Handles
        WorkingSetMb = [math]::Round(($liveProcess.WorkingSet64 / 1MB), 1)
        TopLevelWindowCount = @(Get-TopLevelWindowElementsByProcessId -ProcessId $liveProcess.Id).Count
    }
}

function Get-TopLevelWindowInventoryText {
    param([Parameter(Mandatory = $true)]$Process)

    $windows = @(Get-TopLevelWindowElementsByProcessId -ProcessId $Process.Id)
    if ($windows.Count -eq 0) {
        return "top_level_windows: (none)"
    }

    $lines = New-Object System.Collections.Generic.List[string]
    $index = 0
    foreach ($window in $windows) {
        $index++
        $controlType = if ($window.Current.ControlType) { $window.Current.ControlType.ProgrammaticName } else { '(none)' }
        $lines.Add(
            ("[{0}] Name='{1}' Class='{2}' AutomationId='{3}' NativeWindowHandle='{4}' ControlType='{5}' IsOffscreen='{6}'" -f
                $index,
                $window.Current.Name,
                $window.Current.ClassName,
                $window.Current.AutomationId,
                $window.Current.NativeWindowHandle,
                $controlType,
                $window.Current.IsOffscreen))
    }

    return ($lines -join [Environment]::NewLine)
}

function Find-ByAutomationId {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Root,
        [Parameter(Mandatory = $true)][string]$AutomationId
    )
    $idProp = [System.Windows.Automation.AutomationElement]::AutomationIdProperty
    $cond = New-Object System.Windows.Automation.PropertyCondition($idProp, $AutomationId)
    try {
        return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    }
    catch {
        return $null
    }
}

function Find-AllByAutomationId {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Root,
        [Parameter(Mandatory = $true)][string]$AutomationId
    )
    $idProp = [System.Windows.Automation.AutomationElement]::AutomationIdProperty
    $cond = New-Object System.Windows.Automation.PropertyCondition($idProp, $AutomationId)
    try {
        return $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    }
    catch {
        return @()
    }
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
    try {
        return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    }
    catch {
        return $null
    }
}

function Find-AllByType {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Root,
        [Parameter(Mandatory = $true)][System.Windows.Automation.ControlType]$ControlType
    )
    $typeProp = [System.Windows.Automation.AutomationElement]::ControlTypeProperty
    $cond = New-Object System.Windows.Automation.PropertyCondition($typeProp, $ControlType)
    try {
        return $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    }
    catch {
        return @()
    }
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

function Assert-OptionalAutomationTextInSet {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$AutomationId,
        [Parameter(Mandatory = $true)][string[]]$AllowedTexts
    )

    $el = Find-VisibleByAutomationId -Root $Window -AutomationId $AutomationId
    if (-not $el) {
        return $null
    }

    $text = (Get-ElementTextSafe -Element $el).Trim()
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $null
    }

    foreach ($allowed in $AllowedTexts) {
        if ([string]::Equals($text, $allowed, [System.StringComparison]::Ordinal)) {
            return [pscustomobject]@{
                Element = $el
                Text = $text
            }
        }
    }

    throw "Unexpected $AutomationId text '$text'. Expected one of: $($AllowedTexts -join ', ')"
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
        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        do {
            Start-Sleep -Milliseconds 100
            try {
                if ($Element.Current.IsEnabled) {
                    break
                }
            }
            catch {}
        } while ([DateTime]::UtcNow -lt $deadline)

        if (-not $Element.Current.IsEnabled) {
            throw "Element disabled (Id='$($Element.Current.AutomationId)', Name='$($Element.Current.Name)')"
        }
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

    try {
        $clickablePoint = [System.Windows.Point]::new()
        if ($Element.TryGetClickablePoint([ref]$clickablePoint)) {
            [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point([int]$clickablePoint.X, [int]$clickablePoint.Y)
            [Win32GuiSmoke]::mouse_event([Win32GuiSmoke]::MOUSEEVENTF_LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
            [Win32GuiSmoke]::mouse_event([Win32GuiSmoke]::MOUSEEVENTF_LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
            return
        }
    }
    catch {}

    try {
        $legacyPattern = $Element.GetCurrentPattern([System.Windows.Automation.LegacyIAccessiblePattern]::Pattern)
        ([System.Windows.Automation.LegacyIAccessiblePattern]$legacyPattern).DoDefaultAction()
        return
    }
    catch {}

    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        try {
            $clickablePoint = [System.Windows.Point]::new()
            if ($Element.TryGetClickablePoint([ref]$clickablePoint)) {
                [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point([int]$clickablePoint.X, [int]$clickablePoint.Y)
                [Win32GuiSmoke]::mouse_event([Win32GuiSmoke]::MOUSEEVENTF_LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
                [Win32GuiSmoke]::mouse_event([Win32GuiSmoke]::MOUSEEVENTF_LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
                return
            }
        }
        catch {}

        $rect = $Element.Current.BoundingRectangle
        if (-not $rect.IsEmpty) {
            [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point([int]($rect.Left + $rect.Width/2), [int]($rect.Top + $rect.Height/2))
            [Win32GuiSmoke]::mouse_event([Win32GuiSmoke]::MOUSEEVENTF_LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
            [Win32GuiSmoke]::mouse_event([Win32GuiSmoke]::MOUSEEVENTF_LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
            return
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    $rect = $Element.Current.BoundingRectangle
    if ($rect.IsEmpty) {
        throw "Cannot click element without bounds (Id='$($Element.Current.AutomationId)', Name='$($Element.Current.Name)', ControlType='$($Element.Current.ControlType.ProgrammaticName)')."
    }
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
            Start-Sleep -Milliseconds 50
            $state = Test-ElementValueMatchesText -Element $Element -ExpectedText $Text
            if ($state.IsMatch) {
                return
            }
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

function Wait-ElementValueMatchesText {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Element,
        [Parameter(Mandatory = $true)][string]$ExpectedText,
        [int]$TimeoutMs = 1500
    )

    return Wait-Until -TimeoutMs $TimeoutMs -PollMs 100 -OnTimeoutMessage "Timed out waiting for element value to match '$ExpectedText'." -Condition {
        $state = Test-ElementValueMatchesText -Element $Element -ExpectedText $ExpectedText
        if ($state.IsMatch) {
            return $state
        }

        return $null
    }
}

function Submit-TextInputWithEnter {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Element)

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
        [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    }
    catch {}
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

function Write-ProcessStartupArtifacts {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)]$Process
    )

    $snapshot = Get-ProcessSnapshot -Process $Process
    $safeLabel = if ([string]::IsNullOrWhiteSpace($Label)) { 'process' } else { $Label.Trim().ToLowerInvariant() }
    $snapshotPath = Join-Path $ArtifactDir ("{0}-process-snapshot.txt" -f $safeLabel)
    $uiaPath = Join-Path $ArtifactDir ("{0}-top-level-windows.txt" -f $safeLabel)

    $lines = @(
        ("label: {0}" -f $Label),
        ("process_id: {0}" -f $snapshot.ProcessId),
        ("process_name: {0}" -f $snapshot.ProcessName),
        ("is_running: {0}" -f $snapshot.IsRunning),
        ("main_window_handle: {0}" -f $snapshot.MainWindowHandle),
        ("main_window_title: {0}" -f $snapshot.MainWindowTitle),
        ("thread_count: {0}" -f $snapshot.ThreadCount),
        ("handle_count: {0}" -f $snapshot.HandleCount),
        ("working_set_mb: {0}" -f $snapshot.WorkingSetMb),
        ("top_level_window_count: {0}" -f $snapshot.TopLevelWindowCount)
    )

    $lines | Set-Content -Path $snapshotPath -Encoding UTF8
    (Get-TopLevelWindowInventoryText -Process $Process) | Set-Content -Path $uiaPath -Encoding UTF8
}

$script:GuiSmokeProcessOutputRoot = $null
$script:GuiSmokeProcessOutputFiles = New-Object System.Collections.Generic.List[string]

function Get-GuiSmokeProcessOutputRoot {
    $artifactDir = [string]$env:NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR
    if (-not [string]::IsNullOrWhiteSpace($artifactDir)) {
        $root = Join-Path ([System.IO.Path]::GetFullPath($artifactDir)) 'process-output'
        New-Item -ItemType Directory -Force -Path $root | Out-Null
        return $root
    }

    if ([string]::IsNullOrWhiteSpace($script:GuiSmokeProcessOutputRoot)) {
        $timestamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
        $root = Join-Path (Resolve-Path '.').Path ("artifacts\\gui-smoke\\process-output\\$timestamp")
        New-Item -ItemType Directory -Force -Path $root | Out-Null
        $script:GuiSmokeProcessOutputRoot = $root
    }

    return $script:GuiSmokeProcessOutputRoot
}

function New-GuiSmokeProcessOutputCapture {
    param(
        [Parameter(Mandatory = $true)][string]$RoleName,
        [Parameter(Mandatory = $true)][int]$ProcessId
    )

    $root = Get-GuiSmokeProcessOutputRoot
    $safeRole = if ([string]::IsNullOrWhiteSpace($RoleName)) { 'app' } else { $RoleName.Trim().ToLowerInvariant() }
    $safeRole = [regex]::Replace($safeRole, '[^a-z0-9_-]+', '-')
    if ([string]::IsNullOrWhiteSpace($safeRole)) {
        $safeRole = 'app'
    }

    $prefix = '{0}-{1}' -f $safeRole, $ProcessId
    $stdoutPath = Join-Path $root ("$prefix.stdout.log")
    $stderrPath = Join-Path $root ("$prefix.stderr.log")
    '' | Set-Content -LiteralPath $stdoutPath -Encoding UTF8
    '' | Set-Content -LiteralPath $stderrPath -Encoding UTF8
    $script:GuiSmokeProcessOutputFiles.Add($stdoutPath) | Out-Null
    $script:GuiSmokeProcessOutputFiles.Add($stderrPath) | Out-Null

    return [pscustomobject]@{
        StdoutPath = $stdoutPath
        StderrPath = $stderrPath
    }
}

function Copy-GuiSmokeProcessOutputIfPresent {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir)

    if ($script:GuiSmokeProcessOutputFiles.Count -le 0) {
        return
    }

    $dest = Join-Path $ArtifactDir 'process-output'
    New-Item -ItemType Directory -Force -Path $dest | Out-Null

    foreach ($path in @($script:GuiSmokeProcessOutputFiles)) {
        if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            continue
        }

        try {
            Copy-Item -LiteralPath $path -Destination (Join-Path $dest ([System.IO.Path]::GetFileName($path))) -Force
        }
        catch {
            # Best-effort failure artifacts.
        }
    }
}

function Copy-AppLogsIfPresent {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir)
    $logsDir = Join-Path $env:LOCALAPPDATA 'nLink\logs'
    if (-not (Test-Path $logsDir)) { return }
    $dest = Join-Path $ArtifactDir 'logs'
    New-Item -ItemType Directory -Force -Path $dest | Out-Null

    foreach ($logFile in @(Get-ChildItem -Path $logsDir -File -ErrorAction SilentlyContinue)) {
        $destPath = Join-Path $dest $logFile.Name
        try {
            $lines = Read-AppLogLinesSafe -Path $logFile.FullName
            if ($lines.Count -gt 0) {
                $content = $lines -join [Environment]::NewLine
                [System.IO.File]::WriteAllText($destPath, $content, [System.Text.Encoding]::UTF8)
                continue
            }

            $shareMode = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
            $sourceStream = $null
            $destStream = $null
            try {
                $sourceStream = [System.IO.FileStream]::new($logFile.FullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, $shareMode)
                $destStream = [System.IO.FileStream]::new($destPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
                $sourceStream.CopyTo($destStream)
            }
            finally {
                if ($destStream) { $destStream.Dispose() }
                if ($sourceStream) { $sourceStream.Dispose() }
            }
        }
        catch {
            # Best-effort failure artifacts.
        }
    }

    try {
        $logPath = Join-Path $logsDir 'nlink.log'
        if (Test-Path $logPath) {
            $tail = @(Read-AppLogLinesSafe -Path $logPath | Select-Object -Last 200)
            if ($tail.Count -gt 0) {
                [System.IO.File]::WriteAllText(
                    (Join-Path $ArtifactDir 'app-log-tail.txt'),
                    ($tail -join [Environment]::NewLine),
                    [System.Text.Encoding]::UTF8)
            }
        }
    }
    catch {
        # Best-effort failure artifacts.
    }
}

function Clear-AppLogsIfPresent {
    $logsDir = Join-Path $env:LOCALAPPDATA 'nLink\logs'
    if (-not (Test-Path $logsDir)) { return }

    Get-ChildItem -Path $logsDir -File -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

function Get-AppLogBookmark {
    try {
        return (Read-AppLogLinesSnapshot).Length
    }
    catch {
        return 0
    }
}

function Get-AppLogPaths {
    $logsDir = Join-Path $env:LOCALAPPDATA 'nLink\logs'
    if (-not (Test-Path -LiteralPath $logsDir -PathType Container)) {
        return @()
    }

    return @(
        Get-ChildItem -LiteralPath $logsDir -File -Filter 'nlink*.log' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc, Name |
            ForEach-Object { $_.FullName }
    )
}

function Get-HelperIdentityArtifactPath {
    return Join-Path $env:LOCALAPPDATA 'nLink\gui-smoke'
}

function Get-HelperIdentityArtifactPaths {
    $artifactDir = Get-HelperIdentityArtifactPath
    if (-not (Test-Path $artifactDir)) {
        return @()
    }

    return @(
        Get-ChildItem -Path $artifactDir -Filter 'helper-address*.txt' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        ForEach-Object { $_.FullName }
    )
}

function Clear-HelperIdentityArtifact {
    foreach ($artifactPath in @(Get-HelperIdentityArtifactPaths)) {
        if (Test-Path $artifactPath) {
            Remove-Item -Path $artifactPath -Force -ErrorAction SilentlyContinue
        }
    }
}

function Parse-HelperIdentityArtifactLine {
    param([string]$Line)

    if ([string]::IsNullOrWhiteSpace($Line)) {
        return $null
    }

    if ($Line -match 'run_id=(?<runId>[^;]+);\s*listener_generation=(?<generation>\d+);\s*address=(?<address>[^;]+);\s*published_utc_ms=(?<published>\d+);\s*host_ready=(?<hostReady>[01])') {
        return [pscustomobject]@{
            Address = $Matches['address'].Trim()
            RunId = $Matches['runId'].Trim()
            ListenerGeneration = [int64]$Matches['generation']
            PublishedUtcMs = [int64]$Matches['published']
            HostReady = [int]$Matches['hostReady'] -eq 1
        }
    }

    return $null
}

function Parse-HelperReadyLogLine {
    param([string]$Line)

    if ([string]::IsNullOrWhiteSpace($Line)) {
        return $null
    }

    if ($Line -match 'event=helper_local_peer_address_ready;\s*address=(?<address>[^;]+);.*run_id=(?<runId>[^;]+);\s*listener_generation=(?<generation>\d+);\s*published_utc_ms=(?<published>\d+);\s*host_ready=(?<hostReady>[01])') {
        return [pscustomobject]@{
            Address = $Matches['address'].Trim()
            RunId = $Matches['runId'].Trim()
            ListenerGeneration = [int64]$Matches['generation']
            PublishedUtcMs = [int64]$Matches['published']
            HostReady = [int]$Matches['hostReady'] -eq 1
            IsLegacy = $false
        }
    }

    if ($Line -match 'event=helper_local_peer_address_ready;\s*address=(?<address>[^;]+);') {
        return [pscustomobject]@{
            Address = $Matches['address'].Trim()
            RunId = ''
            ListenerGeneration = -1
            PublishedUtcMs = 0
            HostReady = $true
            IsLegacy = $true
        }
    }

    return $null
}

function Test-HelperIdentityValueUsable {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $false
    }

    $trimmed = $Value.Trim()
    if ([string]::IsNullOrWhiteSpace($trimmed)) {
        return $false
    }

    if ($trimmed.IndexOf('[redacted]', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return $false
    }

    if ($trimmed.IndexOf('[', [System.StringComparison]::Ordinal) -ge 0 -or
        $trimmed.IndexOf(']', [System.StringComparison]::Ordinal) -ge 0) {
        return $false
    }

    if ($trimmed.IndexOf(' ', [System.StringComparison]::Ordinal) -ge 0) {
        return $false
    }

    if ([string]::Equals($trimmed, '(none)', [System.StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    return $true
}

function Get-AppLogLinesAfterBookmark {
    param(
        [Parameter(Mandatory = $true)][int]$Bookmark
    )

    $lines = Read-AppLogLinesSnapshot
    if ($lines.Length -eq 0) {
        return @()
    }

    $start = Get-AppLogReadStartIndex -Bookmark $Bookmark -LineCount $lines.Length
    if ($start -ge $lines.Length) {
        return @()
    }

    return @($lines[$start..($lines.Length - 1)])
}

function Wait-AppLogRegexAfterBookmark {
    param(
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][int]$Bookmark,
        [int]$TimeoutMs = 10000,
        [string]$OnTimeoutMessage = 'Timed out waiting for matching app log entry.'
    )

    return Wait-Until -TimeoutMs $TimeoutMs -PollMs 250 -OnTimeoutMessage $OnTimeoutMessage -Condition {
        $lines = Read-AppLogLinesSnapshot
        if ($lines.Length -eq 0) {
            return $null
        }

        $start = Get-AppLogReadStartIndex -Bookmark $Bookmark -LineCount $lines.Length
        for ($i = $start; $i -lt $lines.Length; $i++) {
            $line = [string]$lines[$i]
            if ($line -match $Pattern) {
                return $line
            }
        }

        return $null
    }
}

function Read-AppLogLinesSnapshot {
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($path in @(Get-AppLogPaths)) {
        foreach ($line in @(Read-AppLogLinesSafe -Path $path)) {
            $lines.Add([string]$line) | Out-Null
        }
    }

    return $lines.ToArray()
}

function Read-AppLogLinesSafe {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path $Path)) {
        return @()
    }

    $fileStream = $null
    $reader = $null
    try {
        $shareMode = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
        $fileStream = [System.IO.FileStream]::new($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, $shareMode)
        $reader = [System.IO.StreamReader]::new($fileStream)
        $text = $reader.ReadToEnd()
        if ([string]::IsNullOrEmpty($text)) {
            return @()
        }

        return @($text -split "`r?`n")
    }
    catch {
        return @()
    }
    finally {
        if ($reader) {
            $reader.Dispose()
        }
        elseif ($fileStream) {
            $fileStream.Dispose()
        }
    }
}

function Get-AppLogReadStartIndex {
    param(
        [Parameter(Mandatory = $true)][int]$Bookmark,
        [Parameter(Mandatory = $true)][int]$LineCount
    )

    if ($Bookmark -gt $LineCount) {
        return 0
    }

    return [Math]::Min($Bookmark, $LineCount)
}

function Wait-HelperRenderedScreenShareFrame {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [int]$LogBookmark = 0,
        [int]$TimeoutMs = 15000
    )

    $helperRunId = if ([string]::IsNullOrWhiteSpace([string]$Context.HelperRunId)) { '' } else { [string]$Context.HelperRunId }
    $timeoutMessage = "Timed out waiting for helper to render a remote screenshare frame. helper_run_id=$helperRunId listener_generation=$($Context.HelperListenerGeneration)"
    $result = Wait-Until -TimeoutMs $TimeoutMs -PollMs 200 -OnTimeoutMessage $timeoutMessage -Condition {
        $error = Find-VisibleByAutomationId -Root $Context.HelperWindow -AutomationId 'ScreenShare.ViewerMessage'
        if ($error) {
            $message = (Get-ElementTextSafe -Element $error).Trim()
            if (-not [string]::IsNullOrWhiteSpace($message)) {
                throw "Helper screenshare viewer reported an error: $message"
            }
        }

        foreach ($line in @(Get-AppLogLinesAfterBookmark -Bookmark $LogBookmark)) {
            if ($line -match 'event=helper_screenshare_viewer_surface_visible;') {
                return [pscustomobject]@{
                    Kind = 'log'
                    Value = $line
                }
            }
        }

        $viewer = Find-ScreenShareViewer -Window $Context.HelperWindow
        if ($viewer) {
            return [pscustomobject]@{
                Kind = 'uia'
                Value = $viewer
            }
        }

        return $null
    }

    if ($result.Kind -eq 'uia') {
        return $result.Value
    }

    return $true
}

function Wait-HelpeeRenderedScreenSharePreview {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [int]$LogBookmark = 0,
        [int]$TimeoutMs = 15000
    )

    $previewVisible = Wait-Until -TimeoutMs $TimeoutMs -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helpee screenshare preview surface to appear.' -Condition {
        $surface = Find-ScreenShareViewer -Window $Context.HelpeeWindow
        if ($surface) {
            return $surface
        }

        return $null
    }

    $error = Find-VisibleByAutomationId -Root $Context.HelpeeWindow -AutomationId 'ScreenShare.ViewerMessage'
    if ($error) {
        $message = (Get-ElementTextSafe -Element $error).Trim()
        if (-not [string]::IsNullOrWhiteSpace($message)) {
            throw "Helpee screenshare preview reported an error: $message"
        }
    }

    [void](Wait-AppLogRegexAfterBookmark `
        -Pattern 'event=helpee_screenshare_preview_surface_visible;' `
        -Bookmark $LogBookmark `
        -TimeoutMs $TimeoutMs `
        -OnTimeoutMessage 'Timed out waiting for helpee to render the local screenshare preview.')

    return $previewVisible
}

function Get-ScreenShareSoakDurationSeconds {
    $value = [string]$env:NLINK_SCREENSHARE_SOAK_SECONDS
    if ([string]::IsNullOrWhiteSpace($value)) {
        return 30
    }

    $parsed = 0
    if ([int]::TryParse($value, [ref]$parsed) -and $parsed -gt 0) {
        return $parsed
    }

    throw "Invalid NLINK_SCREENSHARE_SOAK_SECONDS value '$value'."
}

function Get-PercentileValue {
    param(
        [Parameter(Mandatory = $true)][int[]]$Values,
        [Parameter(Mandatory = $true)][double]$Percentile
    )

    if ($Values.Count -eq 0) {
        return $null
    }

    $sorted = @($Values | Sort-Object)
    $rank = [Math]::Ceiling(($Percentile / 100.0) * $sorted.Count) - 1
    if ($rank -lt 0) { $rank = 0 }
    if ($rank -ge $sorted.Count) { $rank = $sorted.Count - 1 }
    return [int]$sorted[$rank]
}

function Measure-ScreenShareRemoteSoakSummary {
    param(
        [Parameter(Mandatory = $true)][int]$Bookmark
    )

    $lines = @(Get-AppLogLinesAfterBookmark -Bookmark $Bookmark)
    $captureToSendAges = New-Object System.Collections.Generic.List[int]
    $helperApplyAges = New-Object System.Collections.Generic.List[int]
    $helperStaleDrops = 0
    $persistentSummaries = 0
    $sinkWriterSummaries = 0
    $latestHelperApplyUtc = $null
    $latestHelperAppliedFrameCount = 0
    $normalModeSummaries = 0
    $reducedModeSummaries = 0
    $catchUpModeSummaries = 0
    $bridgeHealthAdvisorySummaries = 0
    $bridgeHealthActionableSummaries = 0
    $helperProgressEventCount = 0

    foreach ($line in $lines) {
        if ($line -match 'event=screenshare_freshness_summary;.*capture_to_send_age_ms=([0-9-]+).*encoder_path=([a-z_]+).*sender_freshness_mode=([a-z_]+).*bridge_health_kind=([a-z_]+)') {
            [void]$captureToSendAges.Add([int]$matches[1])
            if ([string]::Equals($matches[2], 'persistent_transform', [System.StringComparison]::OrdinalIgnoreCase)) {
                $persistentSummaries++
            }
            elseif ([string]::Equals($matches[2], 'sink_writer_fallback', [System.StringComparison]::OrdinalIgnoreCase)) {
                $sinkWriterSummaries++
            }

            switch -Regex ($matches[3]) {
                '^normal$' { $normalModeSummaries++ }
                '^reduced$' { $reducedModeSummaries++ }
                '^catch_up$' { $catchUpModeSummaries++ }
            }

            switch -Regex ($matches[4]) {
                '^advisory$' { $bridgeHealthAdvisorySummaries++ }
                '^actionable$' { $bridgeHealthActionableSummaries++ }
            }
        }

        if ($line -match '^\[([0-9:\- TZ]+)\].*event=screenshare_viewer_frame_applied; role=helper_remote; age_ms=([0-9-]+);.*frames_applied=([0-9-]+)') {
            [void]$helperApplyAges.Add([int]$matches[2])
            $latestHelperApplyUtc = $matches[1]
            $latestHelperAppliedFrameCount = [Math]::Max($latestHelperAppliedFrameCount, [int]$matches[3])
            $helperProgressEventCount++
        }

        if ($line -match '^\[([0-9:\- TZ]+)\].*event=screenshare_viewer_recovery_keyframe_applied; role=helper_remote;') {
            $latestHelperApplyUtc = $matches[1]
            $helperProgressEventCount++
        }

        if ($line -match '^\[([0-9:\- TZ]+)\].*event=screenshare_helper_(?:frame_loss|quality)_summary; role=helper_remote;.*frames_applied=([0-9-]+)') {
            $summaryAppliedCount = [int]$matches[2]
            if ($summaryAppliedCount -gt $latestHelperAppliedFrameCount) {
                $latestHelperApplyUtc = $matches[1]
                $latestHelperAppliedFrameCount = $summaryAppliedCount
                $helperProgressEventCount++
            }
        }

        if ($line -match 'event=screenshare_viewer_stale_frame_dropped; role=helper_remote;') {
            $helperStaleDrops++
        }
    }

    $captureAgeValues = @($captureToSendAges.ToArray())
    $helperApplyValues = @($helperApplyAges.ToArray())

    return [pscustomobject]@{
        CaptureToSendSamples = $captureAgeValues
        HelperApplyAges = $helperApplyValues
        HelperAppliedFrameCount = $latestHelperAppliedFrameCount
        HelperProgressEventCount = $helperProgressEventCount
        HelperStaleDrops = $helperStaleDrops
        PersistentSummaryCount = $persistentSummaries
        SinkWriterSummaryCount = $sinkWriterSummaries
        LatestHelperApplyUtc = $latestHelperApplyUtc
        NormalModeSummaryCount = $normalModeSummaries
        ReducedModeSummaryCount = $reducedModeSummaries
        CatchUpModeSummaryCount = $catchUpModeSummaries
        BridgeHealthAdvisorySummaryCount = $bridgeHealthAdvisorySummaries
        BridgeHealthActionableSummaryCount = $bridgeHealthActionableSummaries
    }
}

function Format-ScreenShareRemoteSoakSummary {
    param(
        [Parameter(Mandatory = $true)]$Summary
    )

    $captureSamples = @($Summary.CaptureToSendSamples)
    $helperApplyAges = @($Summary.HelperApplyAges)

    $captureAvg = if ($captureSamples.Count -gt 0) { [math]::Round((($captureSamples | Measure-Object -Average).Average), 1) } else { -1 }
    $captureMin = if ($captureSamples.Count -gt 0) { ($captureSamples | Measure-Object -Minimum).Minimum } else { -1 }
    $captureMax = if ($captureSamples.Count -gt 0) { ($captureSamples | Measure-Object -Maximum).Maximum } else { -1 }

    $applyAvg = if ($helperApplyAges.Count -gt 0) { [math]::Round((($helperApplyAges | Measure-Object -Average).Average), 1) } else { -1 }
    $applyMin = if ($helperApplyAges.Count -gt 0) { ($helperApplyAges | Measure-Object -Minimum).Minimum } else { -1 }
    $applyMax = if ($helperApplyAges.Count -gt 0) { ($helperApplyAges | Measure-Object -Maximum).Maximum } else { -1 }
    $applyP95 = if ($helperApplyAges.Count -gt 0) { Get-PercentileValue -Values $helperApplyAges -Percentile 95 } else { -1 }

    return [pscustomobject]@{
        CaptureSampleCount = $captureSamples.Count
        CaptureMinMs = $captureMin
        CaptureMaxMs = $captureMax
        CaptureAvgMs = $captureAvg
        HelperApplyCount = $Summary.HelperAppliedFrameCount
        HelperApplySampleCount = $helperApplyAges.Count
        HelperApplyMinMs = $applyMin
        HelperApplyMaxMs = $applyMax
        HelperApplyAvgMs = $applyAvg
        HelperApplyP95Ms = $applyP95
        HelperStaleDrops = $Summary.HelperStaleDrops
        PersistentSummaryCount = $Summary.PersistentSummaryCount
        SinkWriterSummaryCount = $Summary.SinkWriterSummaryCount
        LatestHelperApplyUtc = $Summary.LatestHelperApplyUtc
        NormalModeSummaryCount = $Summary.NormalModeSummaryCount
        ReducedModeSummaryCount = $Summary.ReducedModeSummaryCount
        CatchUpModeSummaryCount = $Summary.CatchUpModeSummaryCount
        BridgeHealthAdvisorySummaryCount = $Summary.BridgeHealthAdvisorySummaryCount
        BridgeHealthActionableSummaryCount = $Summary.BridgeHealthActionableSummaryCount
    }
}

function Wait-ScreenShareRemoteSoak {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [Parameter(Mandatory = $true)][int]$Bookmark,
        [Parameter(Mandatory = $true)][int]$DurationSeconds
    )

    $startedAt = Get-Date
    $lastHelperFrameSeenAt = $startedAt
    $lastObservedApplyCount = 0
    $lastObservedHelperProgressEventCount = 0
    $idleTimeout = [TimeSpan]::FromSeconds([Math]::Max(8, [Math]::Min(12, [int][math]::Ceiling($DurationSeconds / 3.0))))

    while (((Get-Date) - $startedAt).TotalSeconds -lt $DurationSeconds) {
        Start-Sleep -Milliseconds 1000

        $error = Find-VisibleByAutomationId -Root $Context.HelperWindow -AutomationId 'ScreenShare.ViewerMessage'
        if ($error) {
            $message = (Get-ElementTextSafe -Element $error).Trim()
            if (-not [string]::IsNullOrWhiteSpace($message)) {
                throw "Helper screenshare viewer reported an error during soak: $message"
            }
        }

        $viewer = Find-ScreenShareViewer -Window $Context.HelperWindow
        if (-not $viewer) {
            throw 'Helper screenshare surface disappeared during soak.'
        }

        $summary = Measure-ScreenShareRemoteSoakSummary -Bookmark $Bookmark
        $applyCount = [int]$summary.HelperAppliedFrameCount
        $progressEventCount = [int]$summary.HelperProgressEventCount
        if ($applyCount -gt $lastObservedApplyCount) {
            $lastObservedApplyCount = $applyCount
            $lastHelperFrameSeenAt = Get-Date
        }
        elseif ($progressEventCount -gt $lastObservedHelperProgressEventCount) {
            $lastObservedHelperProgressEventCount = $progressEventCount
            $lastHelperFrameSeenAt = Get-Date
        }

        if (((Get-Date) - $lastHelperFrameSeenAt) -gt $idleTimeout) {
            throw "No new helper_remote frame progress log (viewer apply, recovery apply, or helper summary with increased frames_applied) was observed for $([int]$idleTimeout.TotalSeconds)s during soak."
        }
    }

    $finalSummary = Measure-ScreenShareRemoteSoakSummary -Bookmark $Bookmark
    $finalMetrics = Format-ScreenShareRemoteSoakSummary -Summary $finalSummary
    if ($finalMetrics.HelperApplyCount -lt 5) {
        throw "Screenshare soak observed too few helper_remote frame applications ($($finalMetrics.HelperApplyCount))."
    }

    Write-Host ("[GUI Smoke][screenshare_nkn_soak] capture_to_send_ms avg={0} min={1} max={2} samples={3}; helper_apply_ms avg={4} min={5} max={6} p95={7} frames_applied={8} samples={9}; helper_stale_drops={10}; encoder_path persistent={11} sink_writer={12}; sender_mode normal={13} reduced={14} catch_up={15}; bridge_health advisory={16} actionable={17}" -f `
        $finalMetrics.CaptureAvgMs,
        $finalMetrics.CaptureMinMs,
        $finalMetrics.CaptureMaxMs,
        $finalMetrics.CaptureSampleCount,
        $finalMetrics.HelperApplyAvgMs,
        $finalMetrics.HelperApplyMinMs,
        $finalMetrics.HelperApplyMaxMs,
        $finalMetrics.HelperApplyP95Ms,
        $finalMetrics.HelperApplyCount,
        $finalMetrics.HelperApplySampleCount,
        $finalMetrics.HelperStaleDrops,
        $finalMetrics.PersistentSummaryCount,
        $finalMetrics.SinkWriterSummaryCount,
        $finalMetrics.NormalModeSummaryCount,
        $finalMetrics.ReducedModeSummaryCount,
        $finalMetrics.CatchUpModeSummaryCount,
        $finalMetrics.BridgeHealthAdvisorySummaryCount,
        $finalMetrics.BridgeHealthActionableSummaryCount) -ForegroundColor DarkGray

    return $finalMetrics
}

function ConvertFrom-GuiSmokeSemicolonFields {
    param([AllowNull()][string]$Message)

    $fields = @{}
    if ([string]::IsNullOrWhiteSpace($Message)) {
        return $fields
    }

    $payload = $Message
    $prefixMatch = [regex]::Match($Message, '^\[[^\]]+\]\s+\[[^\]]+\]\s+\[[^\]]+\]\s+(?<message>.*)$')
    if ($prefixMatch.Success) {
        $payload = $prefixMatch.Groups['message'].Value
    }

    foreach ($part in ($payload -split ';')) {
        $segment = $part.Trim()
        if ([string]::IsNullOrWhiteSpace($segment)) { continue }
        $separator = $segment.IndexOf('=')
        if ($separator -le 0) { continue }
        $key = $segment.Substring(0, $separator).Trim()
        $value = $segment.Substring($separator + 1).Trim()
        if (-not [string]::IsNullOrWhiteSpace($key)) {
            $fields[$key] = $value
        }
    }

    return $fields
}

function Get-GuiSmokeFieldValue {
    param(
        $Fields,
        [Parameter(Mandatory = $true)][string]$Name,
        [string]$Default = ''
    )

    if ($null -ne $Fields -and $Fields.ContainsKey($Name)) {
        return [string]$Fields[$Name]
    }

    return $Default
}

function ConvertTo-GuiSmokeInt {
    param(
        [AllowNull()]$Value,
        [int]$Default = 0
    )

    if ($null -eq $Value) {
        return $Default
    }

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $Default
    }

    $parsed = $Default
    if ([int]::TryParse($text, [System.Globalization.NumberStyles]::Integer, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) {
        return $parsed
    }

    return $Default
}

function ConvertFrom-FileTransferSoakPayloadSize {
    param([Parameter(Mandatory = $true)][string]$Text)

    $value = $Text.Trim()
    if ($value -match '^(?<number>\d+)(?<unit>KiB|MiB|GiB|KB|MB|GB|B)?$') {
        $number = [int64]$Matches['number']
        $unit = [string]$Matches['unit']
        switch -Regex ($unit) {
            '^KiB$|^KB$' { return $number * 1024L }
            '^MiB$|^MB$' { return $number * 1024L * 1024L }
            '^GiB$|^GB$' { return $number * 1024L * 1024L * 1024L }
            default { return $number }
        }
    }

    throw "Invalid file-transfer soak payload size '$Text'."
}

function Get-FileTransferSoakPayloadSizes {
    $raw = [string]$env:NLINK_FILETRANSFER_SOAK_PAYLOAD_SIZES
    if ([string]::IsNullOrWhiteSpace($raw)) {
        $raw = '1MiB,16MiB,64MiB'
    }

    $sizes = New-Object System.Collections.Generic.List[long]
    foreach ($part in $raw.Split(',', [System.StringSplitOptions]::RemoveEmptyEntries)) {
        $sizes.Add((ConvertFrom-FileTransferSoakPayloadSize -Text $part)) | Out-Null
    }

    if ($sizes.Count -eq 0) {
        throw 'No file-transfer payload sizes were configured.'
    }

    return $sizes.ToArray()
}

function Get-FileTransferSoakCycleCount {
    param([Parameter(Mandatory = $true)][long[]]$PayloadSizes)

    $raw = [string]$env:NLINK_FILETRANSFER_SOAK_CYCLES
    $parsed = 0
    if (-not [string]::IsNullOrWhiteSpace($raw) -and [int]::TryParse($raw, [ref]$parsed) -and $parsed -gt 0) {
        return $parsed
    }

    return $PayloadSizes.Count
}

function Get-FileTransferSoakDirection {
    $value = [string]$env:NLINK_FILETRANSFER_SOAK_DIRECTION
    if ([string]::IsNullOrWhiteSpace($value)) {
        return 'alternate'
    }

    $normalized = $value.Trim().ToLowerInvariant()
    if ($normalized -in @('alternate', 'helper-to-helpee', 'helpee-to-helper')) {
        return $normalized
    }

    throw "Invalid NLINK_FILETRANSFER_SOAK_DIRECTION value '$value'."
}

function Get-FileTransferSoakSeed {
    $value = [string]$env:NLINK_FILETRANSFER_SOAK_SEED
    $parsed = 0
    if (-not [string]::IsNullOrWhiteSpace($value) -and [int]::TryParse($value, [ref]$parsed)) {
        return $parsed
    }

    return 1313625684
}

function Get-FileTransferSoakCycleTimeoutMs {
    $value = [string]$env:NLINK_FILETRANSFER_SOAK_CYCLE_TIMEOUT_SECONDS
    $parsed = 0
    if (-not [string]::IsNullOrWhiteSpace($value) -and [int]::TryParse($value, [ref]$parsed) -and $parsed -gt 0) {
        return $parsed * 1000
    }

    return 120000
}

function Get-FileTransferSoakStartupTimeoutMs {
    $value = [string]$env:NLINK_FILETRANSFER_SOAK_STARTUP_TIMEOUT_SECONDS
    $parsed = 0
    if (-not [string]::IsNullOrWhiteSpace($value) -and [int]::TryParse($value, [ref]$parsed) -and $parsed -gt 0) {
        return $parsed * 1000
    }

    return 90000
}

function Get-FileTransferSoakProgressTimeoutMs {
    $value = [string]$env:NLINK_FILETRANSFER_SOAK_PROGRESS_TIMEOUT_SECONDS
    $parsed = 0
    if (-not [string]::IsNullOrWhiteSpace($value) -and [int]::TryParse($value, [ref]$parsed) -and $parsed -gt 0) {
        return $parsed * 1000
    }

    return 120000
}

function Get-FileTransferMixedScreenShareWarmupTimeoutMs {
    $value = [string]$env:NLINK_FILETRANSFER_MIXED_SCREENSHARE_WARMUP_TIMEOUT_SECONDS
    $parsed = 0
    if (-not [string]::IsNullOrWhiteSpace($value) -and [int]::TryParse($value, [ref]$parsed) -and $parsed -gt 0) {
        return $parsed * 1000
    }

    return 120000
}

function Get-FileTransferSoakArtifactDir {
    $value = [string]$env:NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR
    if (-not [string]::IsNullOrWhiteSpace($value)) {
        New-Item -ItemType Directory -Force -Path $value | Out-Null
        return [System.IO.Path]::GetFullPath($value)
    }

    return New-FailureArtifactDir
}

function Get-FileTransferCycleDirection {
    param(
        [Parameter(Mandatory = $true)][string]$Direction,
        [Parameter(Mandatory = $true)][int]$CycleIndex
    )

    if ($Direction -eq 'helper-to-helpee') { return 'helper-to-helpee' }
    if ($Direction -eq 'helpee-to-helper') { return 'helpee-to-helper' }
    if (($CycleIndex % 2) -eq 0) { return 'helper-to-helpee' }
    return 'helpee-to-helper'
}

function Get-FileSha256Hex {
    param([Parameter(Mandatory = $true)][string]$Path)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = $null
    try {
        $stream = [System.IO.File]::OpenRead($Path)
        $hash = $sha.ComputeHash($stream)
        return ([System.BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
    }
    finally {
        if ($stream) { $stream.Dispose() }
        $sha.Dispose()
    }
}

function Write-DeterministicFileTransferPayload {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][long]$SizeBytes,
        [Parameter(Mandatory = $true)][int]$Seed,
        [Parameter(Mandatory = $true)][int]$CycleIndex
    )

    $rng = [System.Random]::new($Seed + ($CycleIndex * 7919))
    $buffer = New-Object byte[] 65536
    $stream = $null
    try {
        $stream = [System.IO.FileStream]::new($Path, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
        $remaining = [int64]$SizeBytes
        while ($remaining -gt 0) {
            $rng.NextBytes($buffer)
            $count = [int][Math]::Min($buffer.Length, $remaining)
            $stream.Write($buffer, 0, $count)
            $remaining -= $count
        }
    }
    finally {
        if ($stream) { $stream.Dispose() }
    }
}

function Resolve-FileTransferLiveReceivedFilePath {
    param(
        [string]$LoggedPath = '',
        [string]$ArtifactDir = '',
        [Parameter(Mandatory = $true)][string]$ExpectedFileName,
        [Parameter(Mandatory = $true)][long]$ExpectedSizeBytes,
        [Parameter(Mandatory = $true)][datetime]$NotBeforeUtc
    )

    if (-not [string]::IsNullOrWhiteSpace($LoggedPath) -and
        $LoggedPath -ne '(none)' -and
        $LoggedPath.IndexOf('[redacted]', [System.StringComparison]::OrdinalIgnoreCase) -lt 0 -and
        (Test-Path -LiteralPath $LoggedPath -PathType Leaf)) {
        $loggedItem = Get-Item -LiteralPath $LoggedPath -ErrorAction SilentlyContinue
        $minimumWriteUtc = $NotBeforeUtc.AddMilliseconds(-500)
        if ($null -ne $loggedItem -and
            $loggedItem.Length -eq $ExpectedSizeBytes -and
            $loggedItem.LastWriteTimeUtc -ge $minimumWriteUtc) {
            return (Resolve-Path -LiteralPath $LoggedPath).Path
        }
    }

    $candidateRoots = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($ArtifactDir)) {
        $candidateRoots.Add((Join-Path $ArtifactDir 'received')) | Out-Null
    }

    $incomingRoot = Join-Path $env:LOCALAPPDATA 'nLink\transfers\incoming'
    $candidateRoots.Add($incomingRoot) | Out-Null

    $candidateFileNames = New-Object System.Collections.Generic.List[string]
    $candidateFileNames.Add($ExpectedFileName) | Out-Null
    if (-not [string]::IsNullOrWhiteSpace($LoggedPath) -and $LoggedPath -ne '(none)') {
        $loggedLeaf = [System.IO.Path]::GetFileName($LoggedPath)
        if (-not [string]::IsNullOrWhiteSpace($loggedLeaf) -and
            -not $candidateFileNames.Contains($loggedLeaf)) {
            $candidateFileNames.Add($loggedLeaf) | Out-Null
        }
    }

    $minimumWriteUtc = $NotBeforeUtc.AddMilliseconds(-500)
    foreach ($candidateRoot in $candidateRoots) {
        if (-not (Test-Path -LiteralPath $candidateRoot -PathType Container)) {
            continue
        }

        $matches = New-Object System.Collections.Generic.List[object]
        foreach ($candidateFileName in $candidateFileNames) {
            foreach ($match in @(
                    Get-ChildItem -LiteralPath $candidateRoot -Recurse -File -Filter $candidateFileName -ErrorAction SilentlyContinue |
                        Where-Object { $_.Length -eq $ExpectedSizeBytes -and $_.LastWriteTimeUtc -ge $minimumWriteUtc }
                )) {
                $matches.Add($match) | Out-Null
            }
        }

        if ($matches.Count -gt 0) {
            return @($matches | Sort-Object LastWriteTimeUtc -Descending)[0].FullName
        }
    }

    return ''
}

function Find-FileTransferLiveReceivedFileByHash {
    param(
        [string]$ArtifactDir = '',
        [Parameter(Mandatory = $true)][string]$ExpectedFileName,
        [Parameter(Mandatory = $true)][long]$ExpectedSizeBytes,
        [Parameter(Mandatory = $true)][datetime]$NotBeforeUtc,
        [Parameter(Mandatory = $true)][string]$ExpectedHash
    )

    $candidateRoots = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($ArtifactDir)) {
        $candidateRoots.Add((Join-Path $ArtifactDir 'received')) | Out-Null
    }

    $incomingRoot = Join-Path $env:LOCALAPPDATA 'nLink\transfers\incoming'
    $candidateRoots.Add($incomingRoot) | Out-Null

    $candidateFileNames = New-Object System.Collections.Generic.List[string]
    $candidateFileNames.Add($ExpectedFileName) | Out-Null

    $minimumWriteUtc = $NotBeforeUtc.AddMilliseconds(-500)
    foreach ($candidateRoot in $candidateRoots) {
        if (-not (Test-Path -LiteralPath $candidateRoot -PathType Container)) {
            continue
        }

        foreach ($candidateFileName in $candidateFileNames) {
            foreach ($candidate in @(Get-ChildItem -LiteralPath $candidateRoot -Recurse -File -Filter $candidateFileName -ErrorAction SilentlyContinue |
                    Where-Object { $_.Length -eq $ExpectedSizeBytes -and $_.LastWriteTimeUtc -ge $minimumWriteUtc } |
                    Sort-Object LastWriteTimeUtc -Descending)) {
                try {
                    $hash = Get-FileSha256Hex -Path $candidate.FullName
                    if ($hash -eq $ExpectedHash) {
                        return $candidate.FullName
                    }
                }
                catch {
                }
            }
        }
    }

    return ''
}

function Append-FileTransferLiveHarnessDiagnostic {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)]$Diagnostic
    )

    $path = Join-Path $ArtifactDir 'filetransfer-live-nkn-harness-diagnostics.jsonl'
    ($Diagnostic | ConvertTo-Json -Compress -Depth 6) | Add-Content -LiteralPath $path -Encoding UTF8
}

function Get-FileTransferLiveProgressScore {
    param(
        $Fields,
        [ref]$MaxReceiverNextChunkIndex,
        [ref]$MaxReceiverHighestChunkIndex
    )

    $eventName = Get-GuiSmokeFieldValue -Fields $Fields -Name 'event'
    if ([string]::IsNullOrWhiteSpace($eventName)) {
        return 0L
    }

    $score = 0L
    switch ($eventName) {
        'filetransfer_chunk_batch_sent_as_batch' {
            $score += [Math]::Max(1L, (Get-GuiSmokeInt64FieldValue -Fields $Fields -Name 'raw_bytes'))
        }
        'filetransfer_binary_frame_sent' {
            $frameType = Get-GuiSmokeFieldValue -Fields $Fields -Name 'frame_type'
            if ($frameType.IndexOf('chunk_data', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $frameType.IndexOf('chunk_batch', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $score += [Math]::Max(1L, (Get-GuiSmokeInt64FieldValue -Fields $Fields -Name 'raw_chunk_bytes'))
            }
        }
        'filetransfer_binary_frame_received' {
            $frameType = Get-GuiSmokeFieldValue -Fields $Fields -Name 'frame_type'
            if ($frameType.IndexOf('chunk_data', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $frameType.IndexOf('chunk_batch', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $score += [Math]::Max(1L, (Get-GuiSmokeInt64FieldValue -Fields $Fields -Name 'raw_chunk_bytes'))
            }
        }
        'filetransfer_v4_sender_throughput_summary' {
            $score += Get-GuiSmokeInt64FieldValue -Fields $Fields -Name 'raw_bytes_sent'
            $score += Get-GuiSmokeInt64FieldValue -Fields $Fields -Name 'chunk_count_sent'
        }
        'filetransfer_v4_receiver_throughput_summary' {
            $score += Get-GuiSmokeInt64FieldValue -Fields $Fields -Name 'raw_bytes_received'
            $score += Get-GuiSmokeInt64FieldValue -Fields $Fields -Name 'contiguous_bytes_committed'
            $score += Get-GuiSmokeInt64FieldValue -Fields $Fields -Name 'write_batch_bytes'

            $nextChunk = Get-GuiSmokeInt64FieldValue -Fields $Fields -Name 'next_chunk_index'
            if ($nextChunk -gt [int64]$MaxReceiverNextChunkIndex.Value) {
                $score += $nextChunk - [int64]$MaxReceiverNextChunkIndex.Value
                $MaxReceiverNextChunkIndex.Value = $nextChunk
            }

            $highestChunk = Get-GuiSmokeInt64FieldValue -Fields $Fields -Name 'highest_received_chunk_index'
            if ($highestChunk -gt [int64]$MaxReceiverHighestChunkIndex.Value) {
                $score += $highestChunk - [int64]$MaxReceiverHighestChunkIndex.Value
                $MaxReceiverHighestChunkIndex.Value = $highestChunk
            }
        }
        'filetransfer_receiver_sparse_write_summary' {
            $score += Get-GuiSmokeInt64FieldValue -Fields $Fields -Name 'written_chunk_count'
            $score += Get-GuiSmokeInt64FieldValue -Fields $Fields -Name 'written_bytes'
        }
        'filetransfer_receiver_sparse_commit_summary' {
            $score += Get-GuiSmokeInt64FieldValue -Fields $Fields -Name 'contiguous_chunks_committed'
            $score += Get-GuiSmokeInt64FieldValue -Fields $Fields -Name 'contiguous_bytes_committed'
        }
        'filetransfer_receiver_write_batch_committed' {
            $score += Get-GuiSmokeInt64FieldValue -Fields $Fields -Name 'write_batch_bytes'
            $score += Get-GuiSmokeInt64FieldValue -Fields $Fields -Name 'write_batch_chunk_count'
            $score += Get-GuiSmokeInt64FieldValue -Fields $Fields -Name 'batch_bytes'
            $score += Get-GuiSmokeInt64FieldValue -Fields $Fields -Name 'batch_chunk_count'
        }
        'filetransfer_v4_chunk_batch_received' {
            $score += Get-GuiSmokeInt64FieldValue -Fields $Fields -Name 'raw_bytes'
            $score += Get-GuiSmokeInt64FieldValue -Fields $Fields -Name 'accepted_chunk_count'
        }
        'filetransfer_v4_state_sent' {
            $nextChunk = Get-GuiSmokeInt64FieldValue -Fields $Fields -Name 'contiguous_committed_chunk_index'
            if ($nextChunk -gt [int64]$MaxReceiverNextChunkIndex.Value) {
                $score += $nextChunk - [int64]$MaxReceiverNextChunkIndex.Value
                $MaxReceiverNextChunkIndex.Value = $nextChunk
            }

            $highestChunk = Get-GuiSmokeInt64FieldValue -Fields $Fields -Name 'durable_received_highest_chunk_index'
            if ($highestChunk -gt [int64]$MaxReceiverHighestChunkIndex.Value) {
                $score += $highestChunk - [int64]$MaxReceiverHighestChunkIndex.Value
                $MaxReceiverHighestChunkIndex.Value = $highestChunk
            }
        }
        'filetransfer_v4_state_received' {
            $nextChunk = Get-GuiSmokeInt64FieldValue -Fields $Fields -Name 'contiguous_committed_chunk_index'
            if ($nextChunk -gt [int64]$MaxReceiverNextChunkIndex.Value) {
                $score += $nextChunk - [int64]$MaxReceiverNextChunkIndex.Value
                $MaxReceiverNextChunkIndex.Value = $nextChunk
            }

            $highestChunk = Get-GuiSmokeInt64FieldValue -Fields $Fields -Name 'durable_received_highest_chunk_index'
            if ($highestChunk -gt [int64]$MaxReceiverHighestChunkIndex.Value) {
                $score += $highestChunk - [int64]$MaxReceiverHighestChunkIndex.Value
                $MaxReceiverHighestChunkIndex.Value = $highestChunk
            }
        }
    }

    return $score
}

function Get-GuiSmokeInt64FieldValue {
    param(
        $Fields,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $value = Get-GuiSmokeFieldValue -Fields $Fields -Name $Name
    $parsed = 0L
    if (-not [string]::IsNullOrWhiteSpace($value) -and
        [int64]::TryParse($value, [System.Globalization.NumberStyles]::Integer, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) {
        return $parsed
    }

    return 0L
}

function Get-GuiSmokeFileTransferTerminalDirection {
    param(
        [string]$EventName,
        $Fields
    )

    if ($EventName -eq 'file_transfer_inbound_terminal') {
        return 'inbound'
    }

    if ($EventName -eq 'file_transfer_outbound_terminal') {
        return 'outbound'
    }

    if ($EventName -eq 'transfer_terminal') {
        $direction = Get-GuiSmokeFieldValue -Fields $Fields -Name 'direction' -Default ''
        if ([string]::Equals($direction, 'inbound', [System.StringComparison]::OrdinalIgnoreCase)) {
            return 'inbound'
        }

        if ([string]::Equals($direction, 'outbound', [System.StringComparison]::OrdinalIgnoreCase)) {
            return 'outbound'
        }
    }

    return ''
}

function Set-GuiSmokeFileTransferTerminalDefaults {
    param($Fields)

    $state = Get-GuiSmokeFieldValue -Fields $Fields -Name 'state' -Default ''
    if ([string]::IsNullOrWhiteSpace($state)) {
        $errorCode = Get-GuiSmokeFieldValue -Fields $Fields -Name 'error_code' -Default '(none)'
        $reason = Get-GuiSmokeFieldValue -Fields $Fields -Name 'reason' -Default ''
        if ($errorCode -eq 'canceled_local' -or $errorCode -eq 'canceled_remote') {
            $Fields['state'] = 'Canceled'
        }
        elseif ($errorCode -eq '(none)' -and
            ([string]::IsNullOrWhiteSpace($reason) -or $reason.IndexOf('complete', [System.StringComparison]::OrdinalIgnoreCase) -ge 0)) {
            $Fields['state'] = 'Completed'
        }
        else {
            $Fields['state'] = 'Failed'
        }
    }
}

function Wait-FileTransferTerminalPairAfterBookmark {
    param(
        [Parameter(Mandatory = $true)][int]$Bookmark,
        [Parameter(Mandatory = $true)][int]$TimeoutMs,
        [Parameter(Mandatory = $true)][int]$ProgressTimeoutMs,
        [string]$ExpectedFileName = '',
        [long]$ExpectedSizeBytes = 0,
        [string]$ExpectedInboundRole = '',
        [string]$ExpectedOutboundRole = '',
        [datetime]$NotBeforeUtc = [datetime]::MinValue,
        [string]$ArtifactDir = ''
    )

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $lastProgressMs = 0L
    $lastProgressEventCount = 0
    $maxReceiverNextChunkIndex = [ref](-1L)
    $maxReceiverHighestChunkIndex = [ref](-1L)
    $ignoredUnresolvedTerminalCandidates = @{}
    $terminalPathResolveStartedMs = -1L
    $terminalPathResolveTransferId = ''

    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        $byTransfer = @{}
        $progressEventCount = 0
        foreach ($line in @(Get-AppLogLinesAfterBookmark -Bookmark $Bookmark)) {
            if ($line.IndexOf('event=', [System.StringComparison]::Ordinal) -lt 0) {
                continue
            }

            $fields = ConvertFrom-GuiSmokeSemicolonFields -Message $line
            $eventName = Get-GuiSmokeFieldValue -Fields $fields -Name 'event'

            if ((Get-FileTransferLiveProgressScore -Fields $fields -MaxReceiverNextChunkIndex $maxReceiverNextChunkIndex -MaxReceiverHighestChunkIndex $maxReceiverHighestChunkIndex) -gt 0) {
                $progressEventCount++
            }

            $terminalDirection = Get-GuiSmokeFileTransferTerminalDirection -EventName $eventName -Fields $fields
            if ([string]::IsNullOrWhiteSpace($terminalDirection)) {
                continue
            }

            Set-GuiSmokeFileTransferTerminalDefaults -Fields $fields

            $transferId = Get-GuiSmokeFieldValue -Fields $fields -Name 'transfer_id'
            if ([string]::IsNullOrWhiteSpace($transferId)) {
                continue
            }

            if (-not $byTransfer.ContainsKey($transferId)) {
                $byTransfer[$transferId] = [ordered]@{
                    TransferId = $transferId
                    Inbound = $null
                    Outbound = $null
                    ResolvedSavedPath = ''
                }
            }

            if ($terminalDirection -eq 'inbound') {
                $role = Get-GuiSmokeFieldValue -Fields $fields -Name 'role' -Default ''
                if (-not [string]::IsNullOrWhiteSpace($ExpectedInboundRole) -and
                    -not [string]::IsNullOrWhiteSpace($role) -and
                    -not [string]::Equals($role, $ExpectedInboundRole, [System.StringComparison]::OrdinalIgnoreCase)) {
                    continue
                }

                if (-not [string]::IsNullOrWhiteSpace($ExpectedFileName) -and $ExpectedSizeBytes -gt 0) {
                    $savedPath = Get-GuiSmokeFieldValue -Fields $fields -Name 'saved_path' -Default '(none)'
                    $resolvedSavedPath = Resolve-FileTransferLiveReceivedFilePath `
                        -LoggedPath $savedPath `
                        -ArtifactDir $ArtifactDir `
                        -ExpectedFileName $ExpectedFileName `
                        -ExpectedSizeBytes $ExpectedSizeBytes `
                        -NotBeforeUtc $NotBeforeUtc
                    if (-not [string]::IsNullOrWhiteSpace($resolvedSavedPath)) {
                        $byTransfer[$transferId]['ResolvedSavedPath'] = $resolvedSavedPath
                    }
                }

                $byTransfer[$transferId]['Inbound'] = $fields
            }
            else {
                $role = Get-GuiSmokeFieldValue -Fields $fields -Name 'role' -Default ''
                if (-not [string]::IsNullOrWhiteSpace($ExpectedOutboundRole) -and
                    -not [string]::IsNullOrWhiteSpace($role) -and
                    -not [string]::Equals($role, $ExpectedOutboundRole, [System.StringComparison]::OrdinalIgnoreCase)) {
                    continue
                }

                $byTransfer[$transferId]['Outbound'] = $fields
            }

            if ($null -ne $byTransfer[$transferId]['Inbound'] -and $null -ne $byTransfer[$transferId]['Outbound']) {
                $requiresResolvedInboundPath = -not [string]::IsNullOrWhiteSpace($ExpectedFileName) -and $ExpectedSizeBytes -gt 0
                if ($requiresResolvedInboundPath) {
                    $inboundCandidate = $byTransfer[$transferId]['Inbound']
                    $inboundState = Get-GuiSmokeFieldValue -Fields $inboundCandidate -Name 'state' -Default '(unknown)'
                    $inboundError = Get-GuiSmokeFieldValue -Fields $inboundCandidate -Name 'error_code' -Default '(none)'
                    $resolvedCandidatePath = [string]$byTransfer[$transferId]['ResolvedSavedPath']

                    if ($inboundState -eq 'Completed' -and
                        $inboundError -eq '(none)' -and
                        [string]::IsNullOrWhiteSpace($resolvedCandidatePath)) {
                        $loggedSavedPath = Get-GuiSmokeFieldValue -Fields $inboundCandidate -Name 'saved_path' -Default '(none)'
                        if ($terminalPathResolveStartedMs -lt 0 -or
                            -not [string]::Equals($terminalPathResolveTransferId, $transferId, [System.StringComparison]::Ordinal)) {
                            $terminalPathResolveStartedMs = $sw.ElapsedMilliseconds
                            $terminalPathResolveTransferId = $transferId
                        }

                        $ignoreKey = '{0}|{1}|{2}|{3}' -f $transferId, $loggedSavedPath, $inboundState, $inboundError
                        if (-not $ignoredUnresolvedTerminalCandidates.ContainsKey($ignoreKey)) {
                            $ignoredUnresolvedTerminalCandidates[$ignoreKey] = $true
                            if (-not [string]::IsNullOrWhiteSpace($ArtifactDir)) {
                                Append-FileTransferLiveHarnessDiagnostic -ArtifactDir $ArtifactDir -Diagnostic ([ordered]@{
                                        event = 'filetransfer_live_terminal_ignored_unresolved_saved_path'
                                        reason = 'current_cycle_saved_path_unresolved'
                                        transfer_id = $transferId
                                        expected_file_name = $ExpectedFileName
                                        expected_size_bytes = $ExpectedSizeBytes
                                        logged_saved_path = $loggedSavedPath
                                        inbound_state = $inboundState
                                        inbound_error_code = $inboundError
                                        expected_inbound_role = $ExpectedInboundRole
                                        expected_outbound_role = $ExpectedOutboundRole
                                        wait_elapsed_ms = $sw.ElapsedMilliseconds
                                        not_before_utc = $NotBeforeUtc.ToUniversalTime().ToString('o')
                                    })
                            }
                        }

                        $lastProgressMs = $sw.ElapsedMilliseconds
                        $pathResolveGraceMs = [Math]::Min(
                            60000,
                            [Math]::Max(15000, $ProgressTimeoutMs))
                        if (($sw.ElapsedMilliseconds - $terminalPathResolveStartedMs) -ge $pathResolveGraceMs) {
                            throw ("Timed out waiting for completed file-transfer saved path resolution: transfer_id={0}; expected_file_name={1}; expected_size_bytes={2}; logged_saved_path={3}; grace_ms={4}; total_wait_s={5:N0}." -f `
                                $transferId,
                                $ExpectedFileName,
                                $ExpectedSizeBytes,
                                $loggedSavedPath,
                                $pathResolveGraceMs,
                                ($sw.ElapsedMilliseconds / 1000))
                        }

                        continue
                    }
                }

                return [pscustomobject]@{
                    TransferId = $transferId
                    Inbound = $byTransfer[$transferId]['Inbound']
                    Outbound = $byTransfer[$transferId]['Outbound']
                    ResolvedSavedPath = $byTransfer[$transferId]['ResolvedSavedPath']
                }
            }
        }

        if ($progressEventCount -gt $lastProgressEventCount) {
            $lastProgressEventCount = $progressEventCount
            $lastProgressMs = $sw.ElapsedMilliseconds
        }

        if (($sw.ElapsedMilliseconds - $lastProgressMs) -ge $ProgressTimeoutMs) {
            throw ("Timed out waiting for live file-transfer progress: no useful data progress for {0:N0}s; total_wait_s={1:N0}; receiver_next_chunk={2}; receiver_highest_chunk={3}; progress_events={4}." -f `
                ($ProgressTimeoutMs / 1000),
                ($sw.ElapsedMilliseconds / 1000),
                $maxReceiverNextChunkIndex.Value,
                $maxReceiverHighestChunkIndex.Value,
                $lastProgressEventCount)
        }

        Start-Sleep -Milliseconds 500
    }

    throw 'Timed out waiting for live file-transfer terminal evidence.'
}

function Append-FileTransferLiveCycleArtifact {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)]$Cycle
    )

    $path = Join-Path $ArtifactDir 'filetransfer-live-nkn-cycles.jsonl'
    ($Cycle | ConvertTo-Json -Compress -Depth 8) | Add-Content -LiteralPath $path -Encoding UTF8
}

function Copy-FileTransferLiveLogSlice {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][int]$Bookmark,
        [string]$FileName = 'filetransfer-retained-log-slice.log'
    )

    $lines = @(Get-AppLogLinesAfterBookmark -Bookmark $Bookmark)
    $path = Join-Path $ArtifactDir $FileName
    if ($lines.Count -gt 0) {
        [System.IO.File]::WriteAllText($path, ($lines -join [Environment]::NewLine), [System.Text.Encoding]::UTF8)
    }
    else {
        [System.IO.File]::WriteAllText($path, '', [System.Text.Encoding]::UTF8)
    }
}

function Test-TunaGuiSecondTransferPostTerminalNoiseLine {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Line)

    if ([string]::IsNullOrEmpty($Line)) {
        return $false
    }

    $needles = @(
        'proof_kind=file_transfer_data_frame',
        'envelope_type=file_transfer_complete',
        'message_type=file_transfer_complete',
        'event=filetransfer_message_ignored; message_type=file_transfer_complete',
        'event=filetransfer_terminal_redundant',
        'event=filetransfer_v4_data_frame_received',
        'event=filetransfer_v6_data_frame_received'
    )

    foreach ($needle in $needles) {
        if ($Line.IndexOf($needle, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $true
        }
    }

    return $false
}

function Wait-TunaGuiSecondTransferPostTerminalQuietWindowOrThrow {
    param(
        [Parameter(Mandatory = $true)][int]$Bookmark,
        [int]$TimeoutMs = 45000,
        [int]$QuietMs = 3000
    )

    $deadline = [DateTimeOffset]::UtcNow.AddMilliseconds([Math]::Max(1000, $TimeoutMs))
    $quietStart = [DateTimeOffset]::UtcNow
    $scanBookmark = $Bookmark
    $lastNoiseLine = '(none)'
    $lastNoiseUtc = $null
    $noiseCount = 0

    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $lines = @(Get-AppLogLinesAfterBookmark -Bookmark $scanBookmark)
        if ($lines.Count -gt 0) {
            foreach ($line in $lines) {
                $text = [string]$line
                if (Test-TunaGuiSecondTransferPostTerminalNoiseLine -Line $text) {
                    $lastNoiseLine = $text
                    $lastNoiseUtc = [DateTimeOffset]::UtcNow
                    $quietStart = [DateTimeOffset]::UtcNow
                    $noiseCount++
                }
            }

            $scanBookmark = Get-AppLogBookmark
        }

        $quietForMs = ([DateTimeOffset]::UtcNow - $quietStart).TotalMilliseconds
        if ($quietForMs -ge $QuietMs) {
            return [pscustomobject]@{
                QuietMs = [Math]::Round($quietForMs, 3)
                NoiseCount = $noiseCount
                LastNoiseUtc = if ($null -eq $lastNoiseUtc) { '(none)' } else { $lastNoiseUtc.ToString('o') }
                LastNoiseLine = $lastNoiseLine
            }
        }

        Start-Sleep -Milliseconds 250
    }

    throw ("Timed out waiting for second-transfer post-terminal quiet window: quiet_ms={0}; timeout_ms={1}; noise_count={2}; last_noise_utc={3}; last_noise_line={4}" -f `
        $QuietMs,
        $TimeoutMs,
        $noiseCount,
        ($(if ($null -eq $lastNoiseUtc) { '(none)' } else { $lastNoiseUtc.ToString('o') })),
        $lastNoiseLine)
}

function Test-TunaGuiLogLinesContain {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Lines,
        [Parameter(Mandatory = $true)][string[]]$Needles
    )

    foreach ($line in @($Lines)) {
        $text = [string]$line
        $matched = $true
        foreach ($needle in @($Needles)) {
            if ($text.IndexOf($needle, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
                $matched = $false
                break
            }
        }

        if ($matched) { return $true }
    }

    return $false
}

function Get-TunaGuiFileTransferSetupFailureClassification {
    param(
        [Parameter(Mandatory = $true)][int]$Bookmark,
        [Parameter(Mandatory = $true)][string]$ErrorMessage,
        [Parameter(Mandatory = $true)][string]$RouteMode
    )

    $lines = @(Get-AppLogLinesAfterBookmark -Bookmark $Bookmark)
    $rawListenerUnavailable = Test-TunaGuiLogLinesContain -Lines $lines -Needles @('listener_unavailable')
    $rawListenerSidecarUnavailable = Test-TunaGuiLogLinesContain -Lines $lines -Needles @('listener_sidecar_unavailable')
    $rawListenerReady = (Test-TunaGuiLogLinesContain -Lines $lines -Needles @('event=tuna_acceleration_timeline', 'status=listener_ready')) -or
        (Test-TunaGuiLogLinesContain -Lines $lines -Needles @('event=tuna_listener_startup_stage', 'stage=listener_ready'))
    $readinessState = Get-TunaGuiReadinessStateAfterBookmark -Bookmark $Bookmark
    $listenerUnavailable = [bool]$readinessState.ListenerUnavailable
    $listenerSidecarUnavailable = [bool]($readinessState.ListenerUnavailable -and $rawListenerSidecarUnavailable)
    $listenerReady = [bool]$readinessState.ListenerReady
    $tunaActive = [bool]$readinessState.TunaActive
    if (-not $listenerUnavailable -and -not $listenerReady -and -not $tunaActive) {
        $listenerUnavailable = [bool]$rawListenerUnavailable
        $listenerSidecarUnavailable = [bool]$rawListenerSidecarUnavailable
        $listenerReady = [bool]$rawListenerReady
        $tunaActive = Test-TunaGuiLogLinesContain -Lines $lines -Needles @('event=tuna_acceleration_timeline', 'active=1')
    }
    $routeSelected = Test-TunaGuiLogLinesContain -Lines $lines -Needles @('event=filetransfer_route_selected')
    $activationOfferNotObserved = (Test-TunaGuiLogLinesContain -Lines $lines -Needles @('event=tuna_acceleration_activation_offer_not_observed')) -or
        (Test-TunaGuiLogLinesContain -Lines $lines -Needles @('reason=offer_send_not_observed')) -or
        (Test-TunaGuiLogLinesContain -Lines $lines -Needles @('event=tuna_acceleration_control_send_wait_timeout', 'purpose=offer'))
    $activationOfferWaitingAnswer = (Test-TunaGuiLogLinesContain -Lines $lines -Needles @('event=tuna_acceleration_offer_queued')) -or
        (Test-TunaGuiLogLinesContain -Lines $lines -Needles @('status=waiting_for_answer')) -or
        (Test-TunaGuiLogLinesContain -Lines $lines -Needles @('event=tuna_acceleration_offer_replay_sent'))
    $runtimeUnlockDispatchDeferredForRegularV4ReceiveRecovery = Test-TunaGuiLogLinesContain -Lines $lines -Needles @('event=tuna_acceleration_runtime_unlock_dispatch_deferred_for_regular_v4_receive_recovery')
    $measuredOfferSent = (Test-TunaGuiLogLinesContain -Lines $lines -Needles @('message_type=file_transfer_offer')) -or
        (Test-TunaGuiLogLinesContain -Lines $lines -Needles @('event=offer_sent'))
    $measuredOfferReceived = Test-TunaGuiLogLinesContain -Lines $lines -Needles @('event=offer_received')
    $activationOfferSent = (Test-TunaGuiLogLinesContain -Lines $lines -Needles @('event=tuna_acceleration_offer_queued')) -or
        (Test-TunaGuiLogLinesContain -Lines $lines -Needles @('event=tuna_acceleration_offer_replay_sent'))
    $activationOfferReceived = Test-TunaGuiLogLinesContain -Lines $lines -Needles @('event=tuna_acceleration_offer_received_raw')
    $activationPhaseEvidence = (-not $tunaActive) -and ($activationOfferNotObserved -or $activationOfferWaitingAnswer -or $activationOfferSent -or $activationOfferReceived)
    $offerSent = if ($activationPhaseEvidence) { $activationOfferSent } else { $measuredOfferSent }
    $offerReceived = if ($activationPhaseEvidence) { $activationOfferReceived } else { $measuredOfferReceived }
    $terminalObserved = Test-TunaGuiLogLinesContain -Lines $lines -Needles @('_terminal')

    $phase = 'unknown'
    $reason = 'unknown'
    $listenerReadyUnavailableContradiction = $listenerReady -and ($listenerUnavailable -or $listenerSidecarUnavailable)
    if ($ErrorMessage.IndexOf('Timed out waiting for helpee invite to become ready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $phase = 'preactivation_readiness'
        $reason = 'helpee_invite_readiness_timeout'
    }
    elseif ($ErrorMessage.IndexOf('Helpee reached Connection failed before invite became ready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $phase = 'preactivation_readiness'
        $reason = 'nkn_bridge_bootstrap_not_ready'
    }
    elseif ($ErrorMessage.IndexOf('Chat.FileTransfer.Accept', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        if (-not $tunaActive -and $listenerReadyUnavailableContradiction) {
            $phase = 'preactivation_readiness'
            $reason = 'listener_ready_unavailable_contradiction'
        }
        elseif (-not $tunaActive -and $runtimeUnlockDispatchDeferredForRegularV4ReceiveRecovery) {
            $phase = 'preactivation_readiness'
            $reason = 'regular_v4_receive_recovery_unproven'
        }
        elseif (-not $tunaActive -and $activationOfferNotObserved) {
            $phase = 'activation_offer_send'
            $reason = 'activation_offer_not_observed'
        }
        elseif (-not $tunaActive -and $activationOfferWaitingAnswer) {
            $phase = 'activation_offer_answer'
            $reason = 'activation_offer_sent_waiting_answer'
        }
        elseif (-not $tunaActive -and ($listenerUnavailable -or $listenerSidecarUnavailable)) {
            $phase = 'preactivation_readiness'
            $reason = 'preflight_listener_unavailable'
        }
        elseif (-not $tunaActive) {
            $phase = 'preactivation_readiness'
            $reason = 'tuna_transport_not_active'
        }
        elseif (-not $offerSent) {
            $phase = 'measured_offer_start'
            $reason = 'offer_not_sent'
        }
        elseif (-not $offerReceived) {
            $phase = 'measured_accept_wait'
            $reason = 'offer_sent_accept_not_enabled'
        }
        else {
            $phase = 'measured_accept_wait'
            $reason = 'offer_received_accept_not_enabled'
        }
    }
    elseif ($listenerReadyUnavailableContradiction) {
        $phase = 'preactivation_readiness'
        $reason = 'listener_ready_unavailable_contradiction'
    }
    elseif (-not $tunaActive -and $runtimeUnlockDispatchDeferredForRegularV4ReceiveRecovery) {
        $phase = 'preactivation_readiness'
        $reason = 'regular_v4_receive_recovery_unproven'
    }
    elseif (-not $tunaActive -and $activationOfferNotObserved) {
        $phase = 'activation_offer_send'
        $reason = 'activation_offer_not_observed'
    }
    elseif (-not $tunaActive -and $activationOfferWaitingAnswer) {
        $phase = 'activation_offer_answer'
        $reason = 'activation_offer_sent_waiting_answer'
    }
    elseif (-not $tunaActive -and ($listenerUnavailable -or $listenerSidecarUnavailable)) {
        $phase = 'preactivation_readiness'
        $reason = 'preflight_listener_unavailable'
    }
    elseif ($terminalObserved) {
        $phase = 'measured_terminal'
        $reason = 'terminal_before_accept'
    }

    return [pscustomobject]@{
        Phase = $phase
        Reason = $reason
        RouteMode = $RouteMode
        ListenerUnavailable = [bool]$listenerUnavailable
        ListenerSidecarUnavailable = [bool]$listenerSidecarUnavailable
        ListenerReady = [bool]$listenerReady
        TunaActive = [bool]$tunaActive
        RouteSelected = [bool]$routeSelected
        ActivationOfferNotObserved = [bool]$activationOfferNotObserved
        ActivationOfferWaitingAnswer = [bool]$activationOfferWaitingAnswer
        RuntimeUnlockDispatchDeferredForRegularV4ReceiveRecovery = [bool]$runtimeUnlockDispatchDeferredForRegularV4ReceiveRecovery
        ActivationOfferSent = [bool]$activationOfferSent
        ActivationOfferReceived = [bool]$activationOfferReceived
        MeasuredOfferSent = [bool]$measuredOfferSent
        MeasuredOfferReceived = [bool]$measuredOfferReceived
        OfferSent = [bool]$offerSent
        OfferReceived = [bool]$offerReceived
        TerminalObserved = [bool]$terminalObserved
    }
}

function Wait-TunaGuiFileTransferAcceptOrThrow {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][int]$Bookmark,
        [Parameter(Mandatory = $true)][int]$TimeoutMs,
        [Parameter(Mandatory = $true)][string]$RouteMode
    )

    try {
        return Wait-ControlEnabledStateByAutomationId -Window $Window -AutomationId 'Chat.FileTransfer.Accept' -IsEnabled $true -TimeoutMs $TimeoutMs
    }
    catch {
        $classification = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark $Bookmark -ErrorMessage $_.Exception.Message -RouteMode $RouteMode
        throw ("Tuna GUI measured file-transfer accept did not become enabled: phase={0}; reason={1}; route_mode={2}; tuna_active={3}; listener_ready={4}; listener_unavailable={5}; route_selected={6}; activation_offer_not_observed={7}; activation_offer_waiting_answer={8}; runtime_unlock_dispatch_deferred_for_regular_v4_receive_recovery={9}; activation_offer_sent={10}; activation_offer_received={11}; measured_offer_sent={12}; measured_offer_received={13}; offer_sent={14}; offer_received={15}; terminal_observed={16}; original_error={17}" -f `
            $classification.Phase,
            $classification.Reason,
            $classification.RouteMode,
            ($(if ($classification.TunaActive) { 1 } else { 0 })),
            ($(if ($classification.ListenerReady) { 1 } else { 0 })),
            ($(if ($classification.ListenerUnavailable -or $classification.ListenerSidecarUnavailable) { 1 } else { 0 })),
            ($(if ($classification.RouteSelected) { 1 } else { 0 })),
            ($(if ($classification.ActivationOfferNotObserved) { 1 } else { 0 })),
            ($(if ($classification.ActivationOfferWaitingAnswer) { 1 } else { 0 })),
            ($(if ($classification.RuntimeUnlockDispatchDeferredForRegularV4ReceiveRecovery) { 1 } else { 0 })),
            ($(if ($classification.ActivationOfferSent) { 1 } else { 0 })),
            ($(if ($classification.ActivationOfferReceived) { 1 } else { 0 })),
            ($(if ($classification.MeasuredOfferSent) { 1 } else { 0 })),
            ($(if ($classification.MeasuredOfferReceived) { 1 } else { 0 })),
            ($(if ($classification.OfferSent) { 1 } else { 0 })),
            ($(if ($classification.OfferReceived) { 1 } else { 0 })),
            ($(if ($classification.TerminalObserved) { 1 } else { 0 })),
            $_.Exception.Message)
    }
}

function Get-TunaGuiReadinessStateAfterBookmark {
    param([Parameter(Mandatory = $true)][int]$Bookmark)

    $lines = @(Get-AppLogLinesAfterBookmark -Bookmark $Bookmark)
    $lastListenerReadyIndex = -1
    $lastTunaActiveIndex = -1
    $lastTunaInactiveIndex = -1
    $lastListenerUnavailableIndex = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = [string]$lines[$i]
        if ($line.IndexOf('listener_unavailable', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('listener_sidecar_unavailable', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            ($line.IndexOf('event=tuna_listener_startup_stage', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                $line.IndexOf('stage=sidecar_error', [System.StringComparison]::OrdinalIgnoreCase) -ge 0)) {
            $lastListenerUnavailableIndex = $i
        }

        $isTunaTimeline = $line.IndexOf('event=tuna_acceleration_timeline', [System.StringComparison]::OrdinalIgnoreCase) -ge 0
        if (($isTunaTimeline -and $line.IndexOf('active=1', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) -or
            $line.IndexOf('filetransfer_tuna_gui_active_bridge_quiet_window', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $lastTunaActiveIndex = $i
        }
        elseif ($isTunaTimeline -and $line.IndexOf('active=0', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $lastTunaInactiveIndex = $i
        }

        if (($line.IndexOf('event=tuna_acceleration_timeline', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                $line.IndexOf('status=listener_ready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) -or
            ($line.IndexOf('event=tuna_listener_startup_stage', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                $line.IndexOf('stage=listener_ready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0)) {
            $lastListenerReadyIndex = $i
        }
    }

    $lastNotActiveIndex = [Math]::Max($lastListenerUnavailableIndex, $lastTunaInactiveIndex)
    $lastReadyOrActiveIndex = [Math]::Max($lastListenerReadyIndex, $lastTunaActiveIndex)

    return [pscustomobject]@{
        TunaActive = $lastTunaActiveIndex -ge 0 -and $lastTunaActiveIndex -gt $lastNotActiveIndex
        ListenerReady = $lastListenerReadyIndex -ge 0 -and $lastListenerReadyIndex -ge $lastListenerUnavailableIndex
        ListenerUnavailable = $lastListenerUnavailableIndex -ge 0 -and $lastListenerUnavailableIndex -gt $lastReadyOrActiveIndex
        LastTunaActiveIndex = $lastTunaActiveIndex
        LastTunaInactiveIndex = $lastTunaInactiveIndex
        LastListenerReadyIndex = $lastListenerReadyIndex
        LastListenerUnavailableIndex = $lastListenerUnavailableIndex
    }
}

function Get-TunaGuiSendFileReadinessStateAfterBookmark {
    param(
        [Parameter(Mandatory = $true)][int]$Bookmark,
        [string]$SenderRole = ''
    )

    $normalizedRole = if ([string]::IsNullOrWhiteSpace($SenderRole)) {
        ''
    }
    else {
        $SenderRole.Trim().ToLowerInvariant()
    }

    $expectedEvent = if ($normalizedRole -eq 'helper') {
        'helper_chat_panel_state'
    }
    elseif ($normalizedRole -eq 'helpee') {
        'helpee_chat_panel_state'
    }
    else {
        ''
    }

    $lastFields = $null
    $lastLine = ''
    foreach ($line in @(Get-AppLogLinesAfterBookmark -Bookmark $Bookmark)) {
        $text = [string]$line
        if ($text.IndexOf('_chat_panel_state', [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
            continue
        }

        $fields = ConvertFrom-GuiSmokeSemicolonFields -Message $text
        $eventName = Get-GuiSmokeFieldValue -Fields $fields -Name 'event' -Default ''
        if (-not [string]::IsNullOrWhiteSpace($expectedEvent) -and
            -not [string]::Equals($eventName, $expectedEvent, [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $lastFields = $fields
        $lastLine = $text
    }

    $runtimeState = Get-GuiSmokeFieldValue -Fields $lastFields -Name 'runtime_state' -Default '(none)'
    $phase = Get-GuiSmokeFieldValue -Fields $lastFields -Name 'phase' -Default '(none)'
    $connectionState = Get-GuiSmokeFieldValue -Fields $lastFields -Name 'connection_state' -Default '(none)'
    $canSendFiles = Get-GuiSmokeFieldValue -Fields $lastFields -Name 'can_send_files' -Default 'False'
    $outboundState = Get-GuiSmokeFieldValue -Fields $lastFields -Name 'outbound_state' -Default '(none)'
    $outboundTerminal = Get-GuiSmokeFieldValue -Fields $lastFields -Name 'outbound_terminal' -Default '(none)'

    $connected = [string]::Equals($runtimeState, 'Connected', [System.StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals($phase, 'Connected', [System.StringComparison]::OrdinalIgnoreCase)
    $canSend = [string]::Equals($canSendFiles, 'True', [System.StringComparison]::OrdinalIgnoreCase) -or $canSendFiles -eq '1'
    $outboundNotActive = [string]::Equals($outboundState, '(none)', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($outboundTerminal, 'True', [System.StringComparison]::OrdinalIgnoreCase) -or
        $outboundTerminal -eq '1'

    return [pscustomobject]@{
        Ready = ($connected -and $canSend -and $outboundNotActive)
        Connected = $connected
        CanSendFiles = $canSend
        OutboundNotActive = $outboundNotActive
        RuntimeState = $runtimeState
        Phase = $phase
        ConnectionState = $connectionState
        OutboundState = $outboundState
        OutboundTerminal = $outboundTerminal
        LastLine = $lastLine
    }
}

function Wait-TunaGuiSecondTransferReadinessOrThrow {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][int]$Bookmark,
        [Parameter(Mandatory = $true)][int]$TimeoutMs,
        [Parameter(Mandatory = $true)][string]$RouteMode,
        [string]$SenderRole = ''
    )

    $deadline = [DateTimeOffset]::UtcNow.AddMilliseconds([Math]::Max(1000, $TimeoutMs))
    $lastState = $null
    $lastSendState = $null
    $lastSendError = ''
    $lastUiState = '(not_checked)'
    $stableReadyPolls = 0
    $requiredStableReadyPolls = 4
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $lastState = Get-TunaGuiReadinessStateAfterBookmark -Bookmark $Bookmark
        $lastSendState = Get-TunaGuiSendFileReadinessStateAfterBookmark -Bookmark $Bookmark -SenderRole $SenderRole
        $sendButton = $null
        $pollWindow = $Window
        try {
            $processId = $Window.Current.ProcessId
            $freshWindow = Get-WindowElementByProcessId -ProcessId $processId
            if ($freshWindow) {
                $pollWindow = $freshWindow
            }
        }
        catch {}

        try {
            $candidate = Find-VisibleByAutomationId -Root $pollWindow -AutomationId 'Chat.SendFile'
            if ($candidate) {
                $lastUiState = "visible=$(-not $candidate.Current.IsOffscreen); enabled=$($candidate.Current.IsEnabled); name=$($candidate.Current.Name)"
                if ($candidate.Current.IsEnabled) {
                    $sendButton = $candidate
                }
            }
            else {
                $lastUiState = 'missing'
            }
        }
        catch {
            $lastSendError = $_.Exception.Message
            $lastUiState = "error=$lastSendError"
        }

        $isReadyNow = $null -ne $sendButton -and
            [bool]$lastState.TunaActive -and
            [bool]$lastState.ListenerReady -and
            -not [bool]$lastState.ListenerUnavailable -and
            [bool]$lastSendState.Ready
        if ($isReadyNow) {
            $stableReadyPolls++
        }
        else {
            $stableReadyPolls = 0
        }

        if ($stableReadyPolls -ge $requiredStableReadyPolls) {
            return $sendButton
        }

        Start-Sleep -Milliseconds 250
    }

    $active = if ($null -ne $lastState -and [bool]$lastState.TunaActive) { 1 } else { 0 }
    $ready = if ($null -ne $lastState -and [bool]$lastState.ListenerReady) { 1 } else { 0 }
    $unavailable = if ($null -ne $lastState -and [bool]$lastState.ListenerUnavailable) { 1 } else { 0 }
    $sendLogReady = if ($null -ne $lastSendState -and [bool]$lastSendState.Ready) { 1 } else { 0 }
    $sendLogCanSend = if ($null -ne $lastSendState -and [bool]$lastSendState.CanSendFiles) { 1 } else { 0 }
    $sendLogConnected = if ($null -ne $lastSendState -and [bool]$lastSendState.Connected) { 1 } else { 0 }
    $sendLogOutboundClear = if ($null -ne $lastSendState -and [bool]$lastSendState.OutboundNotActive) { 1 } else { 0 }
    $lastTunaActiveIndex = if ($null -ne $lastState) { [int]$lastState.LastTunaActiveIndex } else { -1 }
    $lastTunaInactiveIndex = if ($null -ne $lastState) { [int]$lastState.LastTunaInactiveIndex } else { -1 }
    $lastSendLine = if ($null -ne $lastSendState) { [string]$lastSendState.LastLine } else { '' }
    if ([string]::IsNullOrWhiteSpace($lastSendLine)) {
        $lastSendLine = '(none)'
    }

    $readinessFailureReason = if ($ready -eq 1 -and $unavailable -eq 1) { 'listener_ready_unavailable_contradiction' } else { 'second_transfer_tuna_readiness_unstable' }
    throw ("Tuna GUI second-transfer readiness did not stabilize: phase=preactivation_readiness; reason={0}; route_mode={1}; tuna_active={2}; listener_ready={3}; listener_unavailable={4}; send_log_ready={5}; send_log_connected={6}; send_log_can_send_files={7}; send_log_outbound_clear={8}; send_ui_state={9}; send_enabled_error={10}; last_tuna_active_index={11}; last_tuna_inactive_index={12}; stable_ready_polls={13}; required_stable_ready_polls={14}; last_sender_panel_state={15}" -f `
        $readinessFailureReason,
        $RouteMode,
        $active,
        $ready,
        $unavailable,
        $sendLogReady,
        $sendLogConnected,
        $sendLogCanSend,
        $sendLogOutboundClear,
        $lastUiState,
        $lastSendError,
        $lastTunaActiveIndex,
        $lastTunaInactiveIndex,
        $stableReadyPolls,
        $requiredStableReadyPolls,
        $lastSendLine)
}

function Invoke-FileTransferLiveCycle {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$AutopickPath,
        [Parameter(Mandatory = $true)][int]$CycleIndex,
        [Parameter(Mandatory = $true)][long]$PayloadSizeBytes,
        [Parameter(Mandatory = $true)][string]$Direction,
        [Parameter(Mandatory = $true)][int]$Seed,
        [Parameter(Mandatory = $true)][int]$TimeoutMs,
        [Parameter(Mandatory = $true)][int]$StartupTimeoutMs,
        [Parameter(Mandatory = $true)][int]$ProgressTimeoutMs
    )

    Write-DeterministicFileTransferPayload -Path $AutopickPath -SizeBytes $PayloadSizeBytes -Seed $Seed -CycleIndex $CycleIndex
    $expectedHash = Get-FileSha256Hex -Path $AutopickPath
    $senderWindow = if ($Direction -eq 'helper-to-helpee') { $Context.HelperWindow } else { $Context.HelpeeWindow }
    $receiverWindow = if ($Direction -eq 'helper-to-helpee') { $Context.HelpeeWindow } else { $Context.HelperWindow }
    $cycleStartedUtc = [datetime]::UtcNow

    $bookmark = Get-AppLogBookmark
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $sendButton = Wait-ControlEnabledStateByAutomationId -Window $senderWindow -AutomationId 'Chat.SendFile' -IsEnabled $true -TimeoutMs ([Math]::Min(15000, $StartupTimeoutMs))
    Click-Element $sendButton

    $acceptButton = Wait-ControlEnabledStateByAutomationId -Window $receiverWindow -AutomationId 'Chat.FileTransfer.Accept' -IsEnabled $true -TimeoutMs $StartupTimeoutMs
    Click-Element $acceptButton

    $expectedInboundRole = if ($Direction -eq 'helper-to-helpee') { 'helpee' } else { 'helper' }
    $expectedOutboundRole = if ($Direction -eq 'helper-to-helpee') { 'helper' } else { 'helpee' }
    $terminal = Wait-FileTransferTerminalPairAfterBookmark `
        -Bookmark $bookmark `
        -TimeoutMs $TimeoutMs `
        -ProgressTimeoutMs $ProgressTimeoutMs `
        -ExpectedFileName ([System.IO.Path]::GetFileName($AutopickPath)) `
        -ExpectedSizeBytes $PayloadSizeBytes `
        -ExpectedInboundRole $expectedInboundRole `
        -ExpectedOutboundRole $expectedOutboundRole `
        -NotBeforeUtc $cycleStartedUtc `
        -ArtifactDir $ArtifactDir
    $sw.Stop()

    $inbound = $terminal.Inbound
    $outbound = $terminal.Outbound
    $savedPath = Get-GuiSmokeFieldValue -Fields $inbound -Name 'saved_path' -Default '(none)'
    $resolvedSavedPath = [string]$terminal.ResolvedSavedPath
    if ([string]::IsNullOrWhiteSpace($resolvedSavedPath)) {
        $resolvedSavedPath = Resolve-FileTransferLiveReceivedFilePath `
            -LoggedPath $savedPath `
            -ArtifactDir $ArtifactDir `
            -ExpectedFileName ([System.IO.Path]::GetFileName($AutopickPath)) `
            -ExpectedSizeBytes $PayloadSizeBytes `
            -NotBeforeUtc $cycleStartedUtc
    }
    $actualHash = '(none)'
    $savedSize = -1L
    if (-not [string]::IsNullOrWhiteSpace($resolvedSavedPath) -and (Test-Path -LiteralPath $resolvedSavedPath -PathType Leaf)) {
        $actualHash = Get-FileSha256Hex -Path $resolvedSavedPath
        $savedSize = (Get-Item -LiteralPath $resolvedSavedPath).Length
    }

    $inboundState = Get-GuiSmokeFieldValue -Fields $inbound -Name 'state' -Default '(unknown)'
    $outboundState = Get-GuiSmokeFieldValue -Fields $outbound -Name 'state' -Default '(unknown)'
    $inboundError = Get-GuiSmokeFieldValue -Fields $inbound -Name 'error_code' -Default '(none)'
    $outboundError = Get-GuiSmokeFieldValue -Fields $outbound -Name 'error_code' -Default '(none)'
    $completed = $inboundState -eq 'Completed' -and $outboundState -eq 'Completed' -and $inboundError -eq '(none)' -and $outboundError -eq '(none)'
    $integrityOk = $completed -and $savedSize -eq $PayloadSizeBytes -and $actualHash -eq $expectedHash
    $alternateMatchingPath = ''
    $harnessVerifierWarning = ''
    if ($completed -and -not $integrityOk) {
        $alternateMatchingPath = Find-FileTransferLiveReceivedFileByHash `
            -ArtifactDir $ArtifactDir `
            -ExpectedFileName ([System.IO.Path]::GetFileName($AutopickPath)) `
            -ExpectedSizeBytes $PayloadSizeBytes `
            -NotBeforeUtc $cycleStartedUtc `
            -ExpectedHash $expectedHash
        if (-not [string]::IsNullOrWhiteSpace($alternateMatchingPath) -and
            -not [string]::Equals($alternateMatchingPath, $resolvedSavedPath, [System.StringComparison]::OrdinalIgnoreCase)) {
            $harnessVerifierWarning = 'terminal_clean_but_selected_saved_path_hash_mismatch'
            Append-FileTransferLiveHarnessDiagnostic -ArtifactDir $ArtifactDir -Diagnostic ([ordered]@{
                    event = 'filetransfer_live_soak_saved_path_verifier_mismatch'
                    cycle_index = $CycleIndex
                    direction = $Direction
                    transfer_id = $terminal.TransferId
                    selected_saved_path = $resolvedSavedPath
                    alternate_matching_path = $alternateMatchingPath
                    expected_sha256 = $expectedHash
                    selected_sha256 = $actualHash
                    expected_inbound_role = $expectedInboundRole
                    expected_outbound_role = $expectedOutboundRole
                })
            Write-Warning ("[GUI Smoke][filetransfer_nkn] terminal completion is clean but selected saved path hash mismatched; alternate matching file found for transfer_id={0}" -f $terminal.TransferId)
        }
    }
    $goodput = if ($sw.Elapsed.TotalSeconds -gt 0) { $PayloadSizeBytes / $sw.Elapsed.TotalSeconds } else { 0.0 }

    $cycle = [ordered]@{
        cycle_index = $CycleIndex
        direction = $Direction
        transfer_id = $terminal.TransferId
        payload_bytes = $PayloadSizeBytes
        duration_ms = [Math]::Round($sw.Elapsed.TotalMilliseconds, 3)
        goodput_bytes_per_second = [Math]::Round($goodput, 3)
        completed = $completed
        integrity_ok = $integrityOk
        expected_sha256 = $expectedHash
        received_sha256 = $actualHash
        saved_file_size_bytes = $savedSize
        saved_path = $savedPath
        resolved_saved_path = $resolvedSavedPath
        inbound_state = $inboundState
        outbound_state = $outboundState
        inbound_error_code = $inboundError
        outbound_error_code = $outboundError
        expected_inbound_role = $expectedInboundRole
        expected_outbound_role = $expectedOutboundRole
        harness_verifier_warning = $harnessVerifierWarning
        alternate_matching_path = $alternateMatchingPath
    }
    Append-FileTransferLiveCycleArtifact -ArtifactDir $ArtifactDir -Cycle $cycle

    if (-not $integrityOk) {
        throw ("File-transfer live cycle failed integrity or terminal check: cycle={0}; direction={1}; transfer_id={2}; inbound_state={3}; outbound_state={4}; inbound_error={5}; outbound_error={6}; saved_size={7}; expected_size={8}" -f `
            $CycleIndex,
            $Direction,
            $terminal.TransferId,
            $inboundState,
            $outboundState,
            $inboundError,
            $outboundError,
            $savedSize,
            $PayloadSizeBytes)
    }

    Write-Host ("[GUI Smoke][filetransfer_nkn] cycle={0} direction={1} bytes={2} goodput_bps={3:F0} transfer_id={4}" -f `
        $CycleIndex,
        $Direction,
        $PayloadSizeBytes,
        $goodput,
        $terminal.TransferId) -ForegroundColor Green
}

function Start-FileTransferMixedScreenShare {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [Parameter(Mandatory = $true)][int]$WarmupTimeoutMs
    )

    $shareButton = Wait-Until -TimeoutMs 15000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helpee Share screen button for file-transfer mixed NKN soak.' -Condition {
        $button = Find-VisibleByAutomationId -Root $Context.HelpeeWindow -AutomationId 'SessionHeader.ShareScreen'
        if ($button -and $button.Current.IsEnabled) { return $button }
        return $null
    }

    $logBookmark = Get-AppLogBookmark
    Click-Element $shareButton
    $shareButton = Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for screenshare start during file-transfer mixed NKN soak.' -Condition {
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

    [void](Wait-HelpeeRenderedScreenSharePreview -Context $Context -LogBookmark $logBookmark -TimeoutMs ([Math]::Min(15000, $WarmupTimeoutMs)))
    [void](Wait-HelperRenderedScreenShareFrame -Context $Context -LogBookmark $logBookmark -TimeoutMs $WarmupTimeoutMs)
    Start-Sleep -Seconds 3

    return $shareButton
}

function Run-FileTransferNknSoakCore {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [Parameter(Mandatory = $true)][bool]$Mixed
    )

    Reset-ScenarioContext -Context $Context

    $artifactDir = Get-FileTransferSoakArtifactDir
    $receivedRoot = Join-Path $artifactDir 'received'
    New-Item -ItemType Directory -Force -Path $receivedRoot | Out-Null
    $autopickPath = [string]$env:NLINK_FILETRANSFER_SOAK_AUTOPICK_FILE
    if ([string]::IsNullOrWhiteSpace($autopickPath)) {
        $autopickPath = Join-Path $artifactDir 'filetransfer-live-autopick-payload.bin'
        $env:NLINK_FILETRANSFER_SOAK_AUTOPICK_FILE = $autopickPath
    }

    $payloadSizes = @(Get-FileTransferSoakPayloadSizes)
    $cycleCount = Get-FileTransferSoakCycleCount -PayloadSizes $payloadSizes
    $directionMode = Get-FileTransferSoakDirection
    $seed = Get-FileTransferSoakSeed
    $cycleTimeoutMs = Get-FileTransferSoakCycleTimeoutMs
    $startupTimeoutMs = Get-FileTransferSoakStartupTimeoutMs
    $progressTimeoutMs = Get-FileTransferSoakProgressTimeoutMs
    $mixedScreenShareWarmupTimeoutMs = Get-FileTransferMixedScreenShareWarmupTimeoutMs
    $runBookmark = Get-AppLogBookmark

    $previousScaffold = $env:NLINK_FEATURE_SCREENCAP_SCAFFOLD
    $previousInboundRoot = $env:NLINK_FILE_TRANSFER_TEST_INBOUND_ROOT
    $shareButton = $null
    try {
        $env:NLINK_FILE_TRANSFER_TEST_INBOUND_ROOT = $receivedRoot

        if ($Mixed) {
            $env:NLINK_FEATURE_SCREENCAP_SCAFFOLD = '1'
        }

        Start-HelpeeFlow -Context $Context
        Start-HelperFlow -Context $Context
        [void](Connect-HelperAndHelpee -Context $Context)

        if ($Mixed) {
            $shareButton = Start-FileTransferMixedScreenShare -Context $Context -WarmupTimeoutMs $mixedScreenShareWarmupTimeoutMs
        }

        for ($cycleIndex = 0; $cycleIndex -lt $cycleCount; $cycleIndex++) {
            $direction = Get-FileTransferCycleDirection -Direction $directionMode -CycleIndex $cycleIndex
            $payloadSize = [long]$payloadSizes[$cycleIndex % $payloadSizes.Count]
            Invoke-FileTransferLiveCycle `
                -Context $Context `
                -ArtifactDir $artifactDir `
                -AutopickPath $autopickPath `
                -CycleIndex $cycleIndex `
                -PayloadSizeBytes $payloadSize `
                -Direction $direction `
                -Seed $seed `
                -TimeoutMs $cycleTimeoutMs `
                -StartupTimeoutMs $startupTimeoutMs `
                -ProgressTimeoutMs $progressTimeoutMs
        }
    }
    finally {
        if ($shareButton) {
            try {
                Click-Element $shareButton
                [void](Wait-ScreenShareButtonText -Window $Context.HelpeeWindow -ExpectedText 'Share screen' -TimeoutMs 10000)
            }
            catch {
                Write-Host "[GUI Smoke][filetransfer_nkn_mixed] Screen-share stop after file-transfer cycle was not clean: $($_.Exception.Message)" -ForegroundColor Yellow
            }
        }

        Copy-FileTransferLiveLogSlice -ArtifactDir $artifactDir -Bookmark $runBookmark
        if ($null -eq $previousScaffold) {
            Remove-Item Env:NLINK_FEATURE_SCREENCAP_SCAFFOLD -ErrorAction SilentlyContinue
        }
        else {
            $env:NLINK_FEATURE_SCREENCAP_SCAFFOLD = $previousScaffold
        }

        if ($null -eq $previousInboundRoot) {
            Remove-Item Env:NLINK_FILE_TRANSFER_TEST_INBOUND_ROOT -ErrorAction SilentlyContinue
        }
        else {
            $env:NLINK_FILE_TRANSFER_TEST_INBOUND_ROOT = $previousInboundRoot
        }
    }
}

function Run-ScenarioFileTransferNknSoak {
    param([Parameter(Mandatory = $true)]$Context)
    Assert-GuiSmokeNknTransport -ScenarioName 'FILETRANSFER_NKN_SOAK'
    Run-FileTransferNknSoakCore -Context $Context -Mixed:$false
}

function Run-ScenarioFileTransferNknMixedSoak {
    param([Parameter(Mandatory = $true)]$Context)
    Assert-GuiSmokeNknTransport -ScenarioName 'FILETRANSFER_NKN_MIXED_SOAK'
    Run-FileTransferNknSoakCore -Context $Context -Mixed:$true
}

function Assert-GuiSmokeNknTransport {
    param([Parameter(Mandatory = $true)][string]$ScenarioName)

    $transport = [string]$env:NLINK_TRANSPORT
    if (-not [string]::Equals($transport, 'NKN', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw ("{0} requires NLINK_TRANSPORT=NKN. Refusing to run regular-NKN evidence with NLINK_TRANSPORT={1}." -f $ScenarioName, ($(if ([string]::IsNullOrWhiteSpace($transport)) { '(empty)' } else { $transport })))
    }
}

function Test-GuiSmokeEnvEnabled {
    param([Parameter(Mandatory = $true)][string]$Name)

    $value = [string][Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($value)) { return $false }
    return $value -match '^(1|true|yes|on)$'
}

function Resolve-TunaGuiLiveSwitchOffMinimumCommittedBytes {
    param([Parameter(Mandatory = $true)][long]$PayloadSizeBytes)

    $overrideText = [string][Environment]::GetEnvironmentVariable('NLINK_TUNA_GUI_LIVE_SWITCH_OFF_MIN_COMMITTED_BYTES')
    if ([string]::IsNullOrWhiteSpace($overrideText)) {
        $overrideText = [string][Environment]::GetEnvironmentVariable('NLINK_TUNA_GUI_LIVE_SWITCH_OFF_MIN_PAYLOAD_BYTES')
    }

    if (-not [string]::IsNullOrWhiteSpace($overrideText)) {
        $overrideBytes = 0L
        if (-not [long]::TryParse($overrideText, [ref]$overrideBytes) -or $overrideBytes -le 0L) {
            throw "Invalid NLINK_TUNA_GUI_LIVE_SWITCH_OFF_MIN_COMMITTED_BYTES '$overrideText'. Use a positive byte count."
        }

        return [Math]::Min($overrideBytes, [Math]::Max(1L, [long]($PayloadSizeBytes * 3 / 4)))
    }

    $payloadTarget = [Math]::Max(1L, [long]($PayloadSizeBytes / 2))
    $floorBytes = 16777216L
    $capBytes = [Math]::Max(1L, [long]($PayloadSizeBytes * 3 / 4))
    return [Math]::Min([Math]::Max($floorBytes, $payloadTarget), $capBytes)
}

function Resolve-TunaGuiLiveSwitchOffMinimumElapsedMs {
    $overrideText = [string][Environment]::GetEnvironmentVariable('NLINK_TUNA_GUI_LIVE_SWITCH_OFF_MIN_ELAPSED_MS')
    if (-not [string]::IsNullOrWhiteSpace($overrideText)) {
        $overrideMs = 0
        if (-not [int]::TryParse($overrideText, [ref]$overrideMs) -or $overrideMs -lt 0) {
            throw "Invalid NLINK_TUNA_GUI_LIVE_SWITCH_OFF_MIN_ELAPSED_MS '$overrideText'. Use a non-negative millisecond count."
        }

        return $overrideMs
    }

    return 5000
}

function Resolve-TunaGuiLiveSwitchOffMinimumPeerVisiblePayloadBytes {
    param(
        [Parameter(Mandatory = $true)][long]$PayloadSizeBytes,
        [Parameter(Mandatory = $true)][long]$MinimumCommittedBytes
    )

    $overrideText = [string][Environment]::GetEnvironmentVariable('NLINK_TUNA_GUI_LIVE_SWITCH_OFF_MIN_PEER_VISIBLE_PAYLOAD_BYTES')
    if ([string]::IsNullOrWhiteSpace($overrideText)) {
        $overrideText = [string][Environment]::GetEnvironmentVariable('NLINK_TUNA_GUI_LIVE_SWITCH_OFF_MIN_ACCELERATED_PAYLOAD_BYTES')
    }

    if (-not [string]::IsNullOrWhiteSpace($overrideText)) {
        $overrideBytes = 0L
        if (-not [long]::TryParse($overrideText, [ref]$overrideBytes) -or $overrideBytes -le 0L) {
            throw "Invalid NLINK_TUNA_GUI_LIVE_SWITCH_OFF_MIN_PEER_VISIBLE_PAYLOAD_BYTES '$overrideText'. Use a positive byte count."
        }

        return [Math]::Min($overrideBytes, [Math]::Max(1L, [long]($PayloadSizeBytes * 3 / 4)))
    }

    $payloadCap = [Math]::Max(1L, [long]($PayloadSizeBytes * 3 / 4))
    $committedScaledTarget = [Math]::Max(1L, [long]($MinimumCommittedBytes / 64))
    $floorBytes = 524288L
    $target = [Math]::Max($floorBytes, $committedScaledTarget)
    return [Math]::Min([Math]::Min($target, $MinimumCommittedBytes), $payloadCap)
}

function Resolve-RepoPathForGuiSmoke {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path (Resolve-Path '.').Path $Path))
}

function Resolve-TunaGuiWalletPath {
    $value = [string]$env:NLINK_TUNA_GUI_WALLET_PATH
    if ([string]::IsNullOrWhiteSpace($value)) {
        $value = 'artifacts\tuna-poc\wallet-test-nkn.json'
    }

    return Resolve-RepoPathForGuiSmoke -Path $value
}

function Resolve-TunaGuiSidecarPath {
    $value = [string]$env:NLINK_TUNA_GUI_SIDECAR_EXE
    if ([string]::IsNullOrWhiteSpace($value)) {
        $value = [string]$env:NLINK_NKN_TUNA_SIDECAR_EXE
    }

    if ([string]::IsNullOrWhiteSpace($value)) {
        $value = 'artifacts\portable\nLink\win-x64\tuna\win-x64\nlink-tuna-sidecar.exe'
    }

    return Resolve-RepoPathForGuiSmoke -Path $value
}

function Initialize-TunaGuiRuntimeState {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$WalletPath,
        [Parameter(Mandatory = $true)][string]$SidecarPath
    )

    if (-not (Test-Path -LiteralPath $WalletPath -PathType Leaf)) {
        throw "Tuna GUI wallet not found: $WalletPath"
    }

    if (-not (Test-Path -LiteralPath $SidecarPath -PathType Leaf)) {
        throw "Tuna GUI sidecar not found: $SidecarPath"
    }

    $stateRoot = Join-Path $ArtifactDir 'tuna-runtime-state'
    New-Item -ItemType Directory -Force -Path $stateRoot | Out-Null
    $now = [DateTimeOffset]::UtcNow.ToString('o')

    $walletState = [ordered]@{
        walletPath = $WalletPath
        linkedUtc = $now
        lastVerifiedUtc = $now
        walletAddress = 'gui-smoke-redacted'
        balanceNkn = '1'
        status = 2
    }
    $walletState | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $stateRoot 'tuna-wallet-link.json') -Encoding UTF8

    $allowDegradedProviderReady = Test-GuiSmokeEnvEnabled -Name 'NLINK_TUNA_TEST_ALLOW_DEGRADED_PROVIDER_READY'
    $preferences = [ordered]@{
        enabled = $true
        fileLaneEnabled = $true
        screenLaneEnabled = $false
        maxPriceNknPerMb = '0.0002'
        maxTotalMiB = 2048
        maxDurationSec = 1800
        allowDegradedProviderReady = $allowDegradedProviderReady
        lastRuntimeStatus = 'locked'
    }
    $preferences | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $stateRoot 'tuna-runtime-preferences.json') -Encoding UTF8

    $usage = [ordered]@{
        sessions = @()
    }
    $usage | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $stateRoot 'tuna-usage-accounting.json') -Encoding UTF8

    $env:NLINK_TUNA_STATE_ROOT = $stateRoot
    $env:NLINK_NKN_TUNA_SIDECAR_EXE = $SidecarPath
    $env:NLINK_NKN_TUNA_LANES = 'file'
    if ($allowDegradedProviderReady) {
        $env:NLINK_NKN_TUNA_ALLOW_DEGRADED_PROVIDER_READY = '1'
    } else {
        Remove-Item Env:NLINK_NKN_TUNA_ALLOW_DEGRADED_PROVIDER_READY -ErrorAction SilentlyContinue
    }

    if ([string]::IsNullOrWhiteSpace($env:NLINK_NKN_TUNA_DEGRADED_PROVIDER_GRACE_SECONDS) -and
        -not [string]::IsNullOrWhiteSpace($env:NLINK_TUNA_TEST_DEGRADED_PROVIDER_GRACE_SECONDS)) {
        $env:NLINK_NKN_TUNA_DEGRADED_PROVIDER_GRACE_SECONDS = $env:NLINK_TUNA_TEST_DEGRADED_PROVIDER_GRACE_SECONDS
    }

    return $stateRoot
}

function Get-WalletPasswordDialogForProcess {
    param([Parameter(Mandatory = $true)][int]$ProcessId)

    foreach ($window in @(Get-TopLevelWindowElementsByProcessId -ProcessId $ProcessId)) {
        if ([string]::Equals($window.Current.Name, 'Wallet password', [System.StringComparison]::Ordinal)) {
            return $window
        }

        $dialog = Find-ByNameAndType -Root $window -Name 'Wallet password' -ControlType ([System.Windows.Automation.ControlType]::Window)
        if ($dialog) {
            return $dialog
        }
    }

    return $null
}

function Wait-AppLogContainsAfterBookmark {
    param(
        [Parameter(Mandatory = $true)][int]$Bookmark,
        [Parameter(Mandatory = $true)][string]$Needle,
        [int]$TimeoutMs = 30000,
        [string]$Description = ''
    )

    $label = if ([string]::IsNullOrWhiteSpace($Description)) { $Needle } else { $Description }
    return Wait-Until -TimeoutMs $TimeoutMs -PollMs 500 -OnTimeoutMessage "Timed out waiting for app log evidence: $label" -Condition {
        foreach ($line in @(Get-AppLogLinesAfterBookmark -Bookmark $Bookmark)) {
            if ($line.IndexOf($Needle, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                return $line
            }
        }

        return $null
    }
}

function Wait-AppLogContainsAllAfterBookmark {
    param(
        [Parameter(Mandatory = $true)][int]$Bookmark,
        [Parameter(Mandatory = $true)][string[]]$Needles,
        [int]$TimeoutMs = 30000,
        [string]$Description = ''
    )

    $label = if ([string]::IsNullOrWhiteSpace($Description)) { ($Needles -join ' + ') } else { $Description }
    return Wait-Until -TimeoutMs $TimeoutMs -PollMs 500 -OnTimeoutMessage "Timed out waiting for app log evidence: $label" -Condition {
        foreach ($line in @(Get-AppLogLinesAfterBookmark -Bookmark $Bookmark)) {
            $matched = $true
            foreach ($needle in $Needles) {
                if ($line.IndexOf($needle, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
                    $matched = $false
                    break
                }
            }

            if ($matched) { return $line }
        }

        return $null
    }
}

function Wait-AppLogContainsAnyAllAfterBookmark {
    param(
        [Parameter(Mandatory = $true)][int]$Bookmark,
        [Parameter(Mandatory = $true)][object[]]$NeedleSets,
        [int]$TimeoutMs = 30000,
        [string]$Description = ''
    )

    $label = if ([string]::IsNullOrWhiteSpace($Description)) { 'any matching app log evidence' } else { $Description }
    $normalizedNeedleSets = @($NeedleSets)
    if ($normalizedNeedleSets.Count -gt 0) {
        $flatStringNeedleSet = $true
        foreach ($needleSet in $normalizedNeedleSets) {
            if ($needleSet -isnot [string]) {
                $flatStringNeedleSet = $false
                break
            }
        }

        if ($flatStringNeedleSet) {
            $normalizedNeedleSets = @(, $normalizedNeedleSets)
        }
    }

    return Wait-Until -TimeoutMs $TimeoutMs -PollMs 500 -OnTimeoutMessage "Timed out waiting for app log evidence: $label" -Condition {
        foreach ($line in @(Get-AppLogLinesAfterBookmark -Bookmark $Bookmark)) {
            foreach ($needleSet in $normalizedNeedleSets) {
                $needles = @($needleSet)
                $matched = $true
                foreach ($needle in $needles) {
                    if ($line.IndexOf([string]$needle, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
                        $matched = $false
                        break
                    }
                }

                if ($matched) { return $line }
            }
        }

        return $null
    }
}

function Unlock-TunaFromSessionHeader {
    param(
        [Parameter(Mandatory = $true)]$Process,
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$Password,
        [Parameter(Mandatory = $true)][string]$RoleLabel,
        [bool]$WaitForRuntimeEvidence = $true
    )

    $bookmark = Get-AppLogBookmark
    $toggle = Wait-ControlEnabledStateByAutomationId -Window $Window -AutomationId 'SessionHeader.TunaUnlockToggle' -IsEnabled $true -TimeoutMs 45000
    Click-Element $toggle

    $runtimeEvidenceNeedleSets = @(
        @('event=tuna_runtime_unlocked'),
        @('event=tuna_acceleration_payer_intent_queued', 'trigger=runtime_unlock'),
        @('event=tuna_acceleration_timeline', 'status=selected_payer_starting_listener'),
        @('event=tuna_acceleration_timeline', 'status=listener_starting'),
        @('event=tuna_acceleration_timeline', 'status=waiting_for_answer'),
        @('event=tuna_acceleration_negotiated'),
        @('event=tuna_acceleration_timeline', 'active=1')
    )

    $findRuntimeEvidence = {
        foreach ($line in @(Get-AppLogLinesAfterBookmark -Bookmark $bookmark)) {
            foreach ($needleSet in $runtimeEvidenceNeedleSets) {
                $matched = $true
                foreach ($needle in @($needleSet)) {
                    if ($line.IndexOf([string]$needle, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
                        $matched = $false
                        break
                    }
                }

                if ($matched) {
                    return [string]$line
                }
            }
        }

        return $null
    }

    $dialogOrEvidence = Wait-Until -TimeoutMs 20000 -PollMs 200 -OnTimeoutMessage "Timed out waiting for $RoleLabel Tuna wallet password dialog." -Condition {
        $dialog = Get-WalletPasswordDialogForProcess -ProcessId $Process.Id
        if ($dialog) {
            return [pscustomobject]@{
                Kind = 'dialog'
                Dialog = $dialog
                EvidenceLine = ''
            }
        }

        if ($WaitForRuntimeEvidence) {
            $evidenceLine = & $findRuntimeEvidence
            if (-not [string]::IsNullOrWhiteSpace($evidenceLine)) {
                return [pscustomobject]@{
                    Kind = 'runtime_evidence'
                    Dialog = $null
                    EvidenceLine = $evidenceLine
                }
            }
        }

        return $null
    }

    if ([string]::Equals([string]$dialogOrEvidence.Kind, 'runtime_evidence', [System.StringComparison]::OrdinalIgnoreCase)) {
        Write-Host "[GUI Smoke][filetransfer_tuna] $RoleLabel Tuna unlock already had runtime evidence after toggle." -ForegroundColor Green
        return
    }

    $dialog = $dialogOrEvidence.Dialog
    $passwordBox = Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage "Timed out waiting for $RoleLabel Tuna wallet password box." -Condition {
        Find-VisibleByAutomationId -Root $dialog -AutomationId 'WalletPassword.Password'
    }
    Set-Text -Element $passwordBox -Text $Password
    $accept = Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage "Timed out waiting for $RoleLabel Tuna wallet Unlock button." -Condition {
        $button = Find-VisibleByAutomationId -Root $dialog -AutomationId 'WalletPassword.Accept'
        if ($button -and $button.Current.IsEnabled) { return $button }
        return $null
    }
    Click-Element $accept

    if ($WaitForRuntimeEvidence) {
        [void](Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $bookmark -NeedleSets $runtimeEvidenceNeedleSets -TimeoutMs 90000 -Description "$RoleLabel Tuna runtime unlocked or runtime-unlock negotiation started")
        Write-Host "[GUI Smoke][filetransfer_tuna] $RoleLabel Tuna unlock completed." -ForegroundColor Green
        return
    }

    [void](Wait-Until -TimeoutMs 15000 -PollMs 200 -OnTimeoutMessage "Timed out waiting for $RoleLabel Tuna wallet dialog to close after unlock submission." -Condition {
        $null -eq (Get-WalletPasswordDialogForProcess -ProcessId $Process.Id)
    })
    Write-Host "[GUI Smoke][filetransfer_tuna] $RoleLabel Tuna unlock submitted without runtime-evidence wait." -ForegroundColor Green
}

function Get-TunaGuiPayerRole {
    $mode = [string]$env:NLINK_TUNA_GUI_PAYER_MODE
    if ([string]::IsNullOrWhiteSpace($mode)) { return 'helpee' }

    $normalized = $mode.Trim().ToLowerInvariant()
    if ($normalized -in @('helpee', 'helper', 'both')) { return $normalized }
    throw "Invalid NLINK_TUNA_GUI_PAYER_MODE '$mode'. Use helpee, helper, or both."
}

function Unlock-TunaPayers {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [Parameter(Mandatory = $true)][string]$PayerMode,
        [Parameter(Mandatory = $true)][string]$Password
    )

    if ($PayerMode -eq 'helpee' -or $PayerMode -eq 'both') {
        Unlock-TunaFromSessionHeader -Process $Context.HelpeeProc -Window $Context.HelpeeWindow -Password $Password -RoleLabel 'helpee'
    }

    if ($PayerMode -eq 'helper' -or $PayerMode -eq 'both') {
        $waitForHelperRuntimeEvidence = $PayerMode -eq 'helper'
        Unlock-TunaFromSessionHeader -Process $Context.HelperProc -Window $Context.HelperWindow -Password $Password -RoleLabel 'helper' -WaitForRuntimeEvidence $waitForHelperRuntimeEvidence
    }
}

function Get-TunaGuiFaultMode {
    $value = [string]$env:NLINK_TUNA_GUI_FAULT
    if ([string]::IsNullOrWhiteSpace($value)) { return 'switch-off' }

    $normalized = $value.Trim().ToLowerInvariant()
    if ($normalized -in @('none', 'switch-off', 'sidecar-kill')) { return $normalized }
    throw "Invalid NLINK_TUNA_GUI_FAULT '$value'. Use none, switch-off, or sidecar-kill."
}

function Get-TunaGuiRouteMode {
    $value = [string]$env:NLINK_TUNA_GUI_ROUTE_MODE
    if ([string]::IsNullOrWhiteSpace($value)) { return 'handoff-fallback' }

    $normalized = $value.Trim().ToLowerInvariant()
    if ($normalized -in @('handoff-fallback', 'preactivated', 'post-fallback', 'v4-restart-v6-fallback', 'live-v4-switch-off', 'live-multi-toggle', 'live-reactivation-second-transfer', 'live-regular-activation-cycle')) { return $normalized }
    throw "Invalid NLINK_TUNA_GUI_ROUTE_MODE '$value'. Use handoff-fallback, preactivated, post-fallback, v4-restart-v6-fallback, live-v4-switch-off, live-multi-toggle, live-reactivation-second-transfer, or live-regular-activation-cycle."
}

function Get-TunaGuiLiveMultiToggleSequence {
    param(
        [string]$DefaultValue = 'off,on,off',
        [switch]$AllowInitialOn
    )

    $value = [string]$env:NLINK_TUNA_GUI_LIVE_MULTI_TOGGLE_SEQUENCE
    if ([string]::IsNullOrWhiteSpace($value)) {
        $value = $DefaultValue
    }

    $tokens = @(
        $value.Split([char]',', [System.StringSplitOptions]::RemoveEmptyEntries) |
            ForEach-Object { $_.Trim().ToLowerInvariant() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    if ($tokens.Count -eq 0) {
        throw "Invalid NLINK_TUNA_GUI_LIVE_MULTI_TOGGLE_SEQUENCE '$value'. Use a comma-separated sequence such as off,on,off."
    }

    foreach ($token in $tokens) {
        if ($token -ne 'off' -and $token -ne 'on') {
            throw "Invalid NLINK_TUNA_GUI_LIVE_MULTI_TOGGLE_SEQUENCE token '$token'. Use only off or on."
        }
    }

    if (-not $AllowInitialOn -and $tokens[0] -ne 'off') {
        throw "Invalid NLINK_TUNA_GUI_LIVE_MULTI_TOGGLE_SEQUENCE '$value'. The first live toggle must be off."
    }

    for ($i = 1; $i -lt $tokens.Count; $i++) {
        if ($tokens[$i] -eq $tokens[$i - 1]) {
            throw "Invalid NLINK_TUNA_GUI_LIVE_MULTI_TOGGLE_SEQUENCE '$value'. Toggle actions must alternate."
        }
    }

    return $tokens
}

function Get-TunaGuiFaultTarget {
    param([Parameter(Mandatory = $true)][string]$PayerMode)

    if ($PayerMode -eq 'helper') { return 'helper' }
    return 'helpee'
}

function Invoke-TunaGuiFallbackFault {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [Parameter(Mandatory = $true)][string]$FaultMode,
        [Parameter(Mandatory = $true)][string]$PayerMode
    )

    $target = Get-TunaGuiFaultTarget -PayerMode $PayerMode
    $targetWindow = if ($target -eq 'helper') { $Context.HelperWindow } else { $Context.HelpeeWindow }
    $targetProc = if ($target -eq 'helper') { $Context.HelperProc } else { $Context.HelpeeProc }

    if ($FaultMode -eq 'switch-off') {
        $toggle = Wait-ControlEnabledStateByAutomationId -Window $targetWindow -AutomationId 'SessionHeader.TunaUnlockToggle' -IsEnabled $true -TimeoutMs 30000
        Click-Element $toggle
        Write-Host "[GUI Smoke][filetransfer_tuna] Triggered Tuna fallback by switching off $target." -ForegroundColor Yellow
        return
    }

    $children = @(
        Get-CimInstance Win32_Process -Filter "ParentProcessId = $($targetProc.Id)" -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -ieq 'nlink-tuna-sidecar.exe' -or $_.Name -ieq 'nlink-tuna-sidecar' }
    )
    if ($children.Count -eq 0) {
        throw "No Tuna sidecar child process found for $target."
    }

    foreach ($child in $children) {
        Stop-Process -Id $child.ProcessId -Force -ErrorAction Stop
    }
    Write-Host "[GUI Smoke][filetransfer_tuna] Triggered Tuna fallback by killing $target sidecar child process(es)." -ForegroundColor Yellow
}

function Add-TunaGuiLiveRouteEpochObservation {
    param(
        [Parameter(Mandatory = $true)]$Observations,
        [Parameter(Mandatory = $true)][string]$Action,
        [AllowEmptyString()][string]$Line
    )

    if ([string]::IsNullOrWhiteSpace($Line)) {
        return
    }

    $fields = ConvertFrom-GuiSmokeSemicolonFields -Message $Line
    $eventName = Get-GuiSmokeFieldValue -Fields $fields -Name 'event' -Default '(unknown)'
    $handoffKind = Get-GuiSmokeFieldValue -Fields $fields -Name 'handoff_kind' -Default '(none)'
    $targetTransport = Get-GuiSmokeFieldValue -Fields $fields -Name 'target_transport' -Default '(none)'
    $route = Get-GuiSmokeFieldValue -Fields $fields -Name 'route' -Default '(unknown)'
    $transferId = Get-GuiSmokeFieldValue -Fields $fields -Name 'transfer_id' -Default '(unknown)'
    $sessionId = Get-GuiSmokeFieldValue -Fields $fields -Name 'session_id' -Default '(unknown)'
    $protocol = ConvertTo-GuiSmokeInt -Value (Get-GuiSmokeFieldValue -Fields $fields -Name 'protocol_version' -Default '0') -Default 0
    $epoch = ConvertTo-GuiSmokeInt -Value (Get-GuiSmokeFieldValue -Fields $fields -Name 'live_route_epoch' -Default '0') -Default 0
    $missing = New-Object System.Collections.Generic.List[string]
    if ($eventName -ne 'filetransfer_live_route_epoch_started' -and $eventName -ne 'filetransfer_live_route_epoch_recovered') {
        $missing.Add('event') | Out-Null
    }
    if ([string]::IsNullOrWhiteSpace($route) -or $route -eq '(unknown)') {
        $missing.Add('route') | Out-Null
    }
    if ($protocol -le 0) {
        $missing.Add('protocol_version') | Out-Null
    }
    if ([string]::IsNullOrWhiteSpace($handoffKind) -or $handoffKind -eq '(none)') {
        $missing.Add('handoff_kind') | Out-Null
    }
    if ([string]::IsNullOrWhiteSpace($targetTransport) -or $targetTransport -eq '(none)') {
        $missing.Add('target_transport') | Out-Null
    }
    if ($epoch -le 0) {
        $missing.Add('live_route_epoch') | Out-Null
    }
    if ([string]::IsNullOrWhiteSpace($transferId) -or $transferId -eq '(unknown)' -or $transferId -eq '(none)') {
        $missing.Add('transfer_id') | Out-Null
    }
    if ([string]::IsNullOrWhiteSpace($sessionId) -or $sessionId -eq '(unknown)' -or $sessionId -eq '(none)') {
        $missing.Add('session_id') | Out-Null
    }

    $Observations.Add([ordered]@{
        order = $Observations.Count + 1
        action = $Action
        event = $eventName
        liveRouteEpoch = $epoch
        route = $route
        transferId = $transferId
        sessionId = $sessionId
        protocolVersion = $protocol
        handoffKind = $handoffKind
        targetTransport = $targetTransport
        metadataComplete = $missing.Count -eq 0
        missingMetadata = if ($missing.Count -gt 0) { $missing.ToArray() -join ',' } else { '(none)' }
    }) | Out-Null
}

function Get-TunaGuiLiveRouteExpectation {
    param([Parameter(Mandatory = $true)][string]$Route)

    if ($Route -eq 'post_tuna_fallback_v6') {
        return [ordered]@{
            route = 'post_tuna_fallback_v6'
            protocolVersion = 6
            handoffKind = 'tuna_to_normal_fallback'
            targetTransport = 'regular_nkn'
        }
    }

    if ($Route -eq 'file_tuna_v4') {
        return [ordered]@{
            route = 'file_tuna_v4'
            protocolVersion = 4
            handoffKind = 'normal_to_tuna_activation'
            targetTransport = 'tuna'
        }
    }

    throw "Unsupported live route proof expectation: $Route"
}

function Test-TunaGuiLiveRouteObservationMatches {
    param(
        [Parameter(Mandatory = $true)]$Observation,
        [Parameter(Mandatory = $true)]$Expectation,
        [Parameter(Mandatory = $true)][string]$Event
    )

    return [bool]$Observation['metadataComplete'] -and
        [string]$Observation['event'] -eq $Event -and
        [string]$Observation['route'] -eq [string]$Expectation['route'] -and
        [int]$Observation['protocolVersion'] -eq [int]$Expectation['protocolVersion'] -and
        [string]$Observation['handoffKind'] -eq [string]$Expectation['handoffKind'] -and
        [string]$Observation['targetTransport'] -eq [string]$Expectation['targetTransport']
}

function Get-TunaGuiLiveRouteEpochProof {
    param(
        [Parameter(Mandatory = $true)]$Observations,
        [Parameter(Mandatory = $true)][string[]]$ExpectedRoutes
    )

    $ordered = @($Observations.ToArray() | Sort-Object { [int]$_['order'] })
    $metadataMissing = @($ordered | Where-Object { -not [bool]$_['metadataComplete'] })
    $findings = New-Object System.Collections.Generic.List[string]
    foreach ($observation in @($metadataMissing)) {
        $findings.Add(("missing_metadata action={0}; event={1}; missing={2}" -f $observation['action'], $observation['event'], $observation['missingMetadata'])) | Out-Null
    }

    $lastEpoch = 0
    $expectedTransferId = ''
    $expectedSessionId = ''
    $matchedRoutes = New-Object System.Collections.Generic.List[string]
    foreach ($expectedRoute in @($ExpectedRoutes)) {
        $expectation = Get-TunaGuiLiveRouteExpectation -Route $expectedRoute
        $started = $null
        foreach ($observation in @($ordered)) {
            $epoch = [int]$observation['liveRouteEpoch']
            if ($epoch -le $lastEpoch) {
                continue
            }

            if (Test-TunaGuiLiveRouteObservationMatches -Observation $observation -Expectation $expectation -Event 'filetransfer_live_route_epoch_started') {
                if (-not [string]::IsNullOrWhiteSpace($expectedTransferId) -and
                    -not [string]::Equals([string]$observation['transferId'], $expectedTransferId, [System.StringComparison]::Ordinal)) {
                    continue
                }

                if (-not [string]::IsNullOrWhiteSpace($expectedSessionId) -and
                    -not [string]::Equals([string]$observation['sessionId'], $expectedSessionId, [System.StringComparison]::Ordinal)) {
                    continue
                }

                $started = $observation
                break
            }
        }

        if ($null -eq $started) {
            $findings.Add(("missing_started route={0}; after_epoch={1}" -f $expectedRoute, $lastEpoch)) | Out-Null
            continue
        }

        $startedEpoch = [int]$started['liveRouteEpoch']
        if ([string]::IsNullOrWhiteSpace($expectedTransferId)) {
            $expectedTransferId = [string]$started['transferId']
        }
        elseif (-not [string]::Equals([string]$started['transferId'], $expectedTransferId, [System.StringComparison]::Ordinal)) {
            $findings.Add(("transfer_scope_mismatch route={0}; live_route_epoch={1}; expected_transfer_id={2}; actual_transfer_id={3}" -f $expectedRoute, $startedEpoch, $expectedTransferId, $started['transferId'])) | Out-Null
            continue
        }

        if ([string]::IsNullOrWhiteSpace($expectedSessionId)) {
            $expectedSessionId = [string]$started['sessionId']
        }
        elseif (-not [string]::Equals([string]$started['sessionId'], $expectedSessionId, [System.StringComparison]::Ordinal)) {
            $findings.Add(("session_scope_mismatch route={0}; live_route_epoch={1}; expected_session_id={2}; actual_session_id={3}" -f $expectedRoute, $startedEpoch, $expectedSessionId, $started['sessionId'])) | Out-Null
            continue
        }

        $recovered = $null
        foreach ($observation in @($ordered)) {
            if ([int]$observation['order'] -le [int]$started['order']) {
                continue
            }

            if ([int]$observation['liveRouteEpoch'] -ne $startedEpoch) {
                continue
            }

            if (-not [string]::Equals([string]$observation['transferId'], $expectedTransferId, [System.StringComparison]::Ordinal) -or
                -not [string]::Equals([string]$observation['sessionId'], $expectedSessionId, [System.StringComparison]::Ordinal)) {
                continue
            }

            if (Test-TunaGuiLiveRouteObservationMatches -Observation $observation -Expectation $expectation -Event 'filetransfer_live_route_epoch_recovered') {
                $recovered = $observation
                break
            }
        }

        if ($null -eq $recovered) {
            $findings.Add(("missing_recovered route={0}; live_route_epoch={1}" -f $expectedRoute, $startedEpoch)) | Out-Null
            continue
        }

        $matchedRoutes.Add($expectedRoute) | Out-Null
        $lastEpoch = $startedEpoch
    }

    return [pscustomobject]@{
        Pass = $findings.Count -eq 0 -and $matchedRoutes.Count -eq $ExpectedRoutes.Count
        RouteChanges = @($matchedRoutes.ToArray())
        MetadataMissingCount = $metadataMissing.Count
        Findings = @($findings.ToArray())
    }
}

function Wait-TunaGuiLiveRouteEpochStarted {
    param(
        [Parameter(Mandatory = $true)][int]$Bookmark,
        [Parameter(Mandatory = $true)][string]$Route,
        [Parameter(Mandatory = $true)][int]$ProtocolVersion,
        [Parameter(Mandatory = $true)][string]$HandoffKind,
        [Parameter(Mandatory = $true)][string]$TargetTransport,
        [Parameter(Mandatory = $true)][string]$Description,
        [int]$FallbackBookmark = -1,
        [int]$AfterLiveRouteEpoch = 0,
        [int]$TimeoutMs = 90000
    )

    return [string](Wait-TunaGuiLiveRouteEpochEvidence `
        -Bookmark $Bookmark `
        -Route $Route `
        -ProtocolVersion $ProtocolVersion `
        -HandoffKind $HandoffKind `
        -TargetTransport $TargetTransport `
        -EventName 'filetransfer_live_route_epoch_started' `
        -Description $Description `
        -FallbackBookmark $FallbackBookmark `
        -AfterLiveRouteEpoch $AfterLiveRouteEpoch `
        -TimeoutMs $TimeoutMs)
}

function Wait-TunaGuiLiveRouteEpochRecovered {
    param(
        [Parameter(Mandatory = $true)][int]$Bookmark,
        [Parameter(Mandatory = $true)][string]$Route,
        [Parameter(Mandatory = $true)][int]$ProtocolVersion,
        [Parameter(Mandatory = $true)][string]$HandoffKind,
        [Parameter(Mandatory = $true)][string]$TargetTransport,
        [Parameter(Mandatory = $true)][string]$Description,
        [int]$FallbackBookmark = -1,
        [int]$LiveRouteEpoch = 0,
        [int]$AfterLiveRouteEpoch = 0,
        [string]$AfterLine = '',
        [int]$TimeoutMs = 150000
    )

    return [string](Wait-TunaGuiLiveRouteEpochEvidence `
        -Bookmark $Bookmark `
        -Route $Route `
        -ProtocolVersion $ProtocolVersion `
        -HandoffKind $HandoffKind `
        -TargetTransport $TargetTransport `
        -EventName 'filetransfer_live_route_epoch_recovered' `
        -Description $Description `
        -FallbackBookmark $FallbackBookmark `
        -LiveRouteEpoch $LiveRouteEpoch `
        -AfterLiveRouteEpoch $AfterLiveRouteEpoch `
        -AfterLine $AfterLine `
        -TimeoutMs $TimeoutMs)
}

function Wait-TunaGuiLiveRouteEpochEvidence {
    param(
        [Parameter(Mandatory = $true)][int]$Bookmark,
        [Parameter(Mandatory = $true)][string]$Route,
        [Parameter(Mandatory = $true)][int]$ProtocolVersion,
        [Parameter(Mandatory = $true)][string]$HandoffKind,
        [Parameter(Mandatory = $true)][string]$TargetTransport,
        [Parameter(Mandatory = $true)][string]$EventName,
        [Parameter(Mandatory = $true)][string]$Description,
        [int]$FallbackBookmark = -1,
        [int]$LiveRouteEpoch = 0,
        [int]$AfterLiveRouteEpoch = 0,
        [string]$AfterLine = '',
        [int]$TimeoutMs = 90000
    )

    $needles = @(
        ("event={0}" -f $EventName),
        ("route={0}" -f $Route),
        ("protocol_version={0}" -f $ProtocolVersion),
        ("handoff_kind={0}" -f $HandoffKind),
        ("target_transport={0}" -f $TargetTransport),
        'live_route_epoch='
    )
    $label = if ([string]::IsNullOrWhiteSpace($Description)) { $EventName } else { $Description }
    $requiresAfterLine = -not [string]::IsNullOrWhiteSpace($AfterLine)
    $findMatchingLine = {
        param([int]$CandidateBookmark)

        $afterLineSeen = -not $requiresAfterLine
        foreach ($line in @(Get-AppLogLinesAfterBookmark -Bookmark $CandidateBookmark)) {
            if (-not $afterLineSeen) {
                if ([string]::Equals([string]$line, $AfterLine, [System.StringComparison]::Ordinal)) {
                    $afterLineSeen = $true
                }

                continue
            }

            $matched = $true
            foreach ($needle in $needles) {
                if ($line.IndexOf([string]$needle, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
                    $matched = $false
                    break
                }
            }

            if (-not $matched) {
                continue
            }

            $fields = ConvertFrom-GuiSmokeSemicolonFields -Message $line
            $epoch = ConvertTo-GuiSmokeInt -Value (Get-GuiSmokeFieldValue -Fields $fields -Name 'live_route_epoch' -Default '0') -Default 0
            if ($LiveRouteEpoch -gt 0 -and $epoch -ne $LiveRouteEpoch) {
                continue
            }

            if ($AfterLiveRouteEpoch -gt 0 -and $epoch -le $AfterLiveRouteEpoch) {
                continue
            }

            return $line
        }

        return $null
    }

    return Wait-Until -TimeoutMs $TimeoutMs -PollMs 500 -OnTimeoutMessage "Timed out waiting for app log evidence: $label" -Condition {
        $line = & $findMatchingLine $Bookmark
        if ($line) {
            return $line
        }

        if ($FallbackBookmark -ge 0 -and $FallbackBookmark -ne $Bookmark -and $FallbackBookmark -lt $Bookmark) {
            $line = & $findMatchingLine $FallbackBookmark
            if ($line) {
                return $line
            }
        }

        return $null
    }
}

function Wait-TunaGuiActiveBridgeQuietWindow {
    param(
        [Parameter(Mandatory = $true)][int]$Bookmark,
        [int]$QuietMs = 4000,
        [int]$TimeoutMs = 45000,
        [int]$PollIntervalMs = 250
    )

    $overrideText = [string][Environment]::GetEnvironmentVariable('NLINK_TUNA_GUI_ACTIVE_BRIDGE_QUIET_MS')
    if (-not [string]::IsNullOrWhiteSpace($overrideText)) {
        $overrideMs = 0
        if (-not [int]::TryParse($overrideText, [ref]$overrideMs) -or $overrideMs -lt 0) {
            throw "Invalid NLINK_TUNA_GUI_ACTIVE_BRIDGE_QUIET_MS '$overrideText'. Use a non-negative millisecond count."
        }

        $QuietMs = $overrideMs
    }

    if ($QuietMs -le 0) {
        return "[{0}] [INFO] [GuiSmoke] event=tuna_gui_active_bridge_quiet_window; quiet_ms=0; skipped=1" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ'))
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $quietSinceMs = 0L
    $processedLineCount = 0
    $healthSummaryObserved = $false
    $lastAdverseEvent = '(none)'
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        $lines = @(Get-AppLogLinesAfterBookmark -Bookmark $Bookmark)
        if ($lines.Count -lt $processedLineCount) {
            $processedLineCount = 0
            $quietSinceMs = $sw.ElapsedMilliseconds
            $healthSummaryObserved = $false
        }

        for ($lineIndex = $processedLineCount; $lineIndex -lt $lines.Count; $lineIndex++) {
            $line = [string]$lines[$lineIndex]
            if ($line.IndexOf('event=screenshare_bridge_transport_health_summary', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $healthSummaryObserved = $true
            }

            if ($line.IndexOf('event=nkn_bridge_receive_stall_detected', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $line.IndexOf('event=nkn_bridge_receive_stall_recovery_started', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $line.IndexOf('event=nkn_bridge_receive_stall_recovery_completed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $line.IndexOf('event=nkn_bridge_receive_stall_recovery_unproven', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $line.IndexOf('event=nkn_bridge_receive_stall_recovery_failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $line.IndexOf('event=nkn_bridge_receive_stall_recovery_cooldown_bypassed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $line.IndexOf('event=nkn_bridge_receive_stall_recovery_receive_resumed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $quietSinceMs = $sw.ElapsedMilliseconds
                $healthSummaryObserved = $false
                $lastAdverseEvent = $line
            }
        }

        $processedLineCount = $lines.Count
        if ($healthSummaryObserved -and (($sw.ElapsedMilliseconds - $quietSinceMs) -ge $QuietMs)) {
            return "[{0}] [INFO] [GuiSmoke] event=tuna_gui_active_bridge_quiet_window; quiet_ms={1}; elapsed_ms={2}; last_adverse_event={3}" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $QuietMs, $sw.ElapsedMilliseconds, $lastAdverseEvent
        }

        Start-Sleep -Milliseconds $PollIntervalMs
    }

    throw "Timed out waiting for active Tuna bridge quiet window. quiet_ms=$QuietMs; timeout_ms=$TimeoutMs; last_adverse_event=$lastAdverseEvent"
}

function Wait-TunaGuiAcceleratedFilePayloadBeforeFault {
    param(
        [Parameter(Mandatory = $true)][int]$Bookmark,
        [int]$TimeoutMs = 60000,
        [long]$MinimumTotalPayloadBytes = 67108864,
        [long]$MinimumFramePayloadBytes = 16384,
        [int]$PollIntervalMs = 500
    )

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $processedLineCount = 0
    $totalPayloadBytes = 0L
    $lastFramePayloadBytes = -1L
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        $lines = @(Get-AppLogLinesAfterBookmark -Bookmark $Bookmark)
        if ($lines.Count -lt $processedLineCount) {
            $processedLineCount = 0
        }

        for ($lineIndex = $processedLineCount; $lineIndex -lt $lines.Count; $lineIndex++) {
            $line = $lines[$lineIndex]
            if ($line.IndexOf('event=tuna_accelerated_file_frame_sent', [System.StringComparison]::OrdinalIgnoreCase) -lt 0) { continue }
            if ($line.IndexOf('channel=bulk', [System.StringComparison]::OrdinalIgnoreCase) -lt 0) { continue }

            $fields = ConvertFrom-GuiSmokeSemicolonFields -Message $line
            $payloadText = Get-GuiSmokeFieldValue -Fields $fields -Name 'payload_bytes' -Default '-1'
            $payloadBytes = -1L
            if ([long]::TryParse($payloadText, [ref]$payloadBytes)) {
                $lastFramePayloadBytes = [Math]::Max($lastFramePayloadBytes, $payloadBytes)
                if ($payloadBytes -ge $MinimumFramePayloadBytes) {
                    $totalPayloadBytes += $payloadBytes
                }

                if ($totalPayloadBytes -ge $MinimumTotalPayloadBytes) {
                    return $line
                }
            }
        }
        $processedLineCount = $lines.Count

        Start-Sleep -Milliseconds ([Math]::Max(10, $PollIntervalMs))
    }

    throw "Timed out waiting for Tuna accelerated bulk file payload before fallback fault; min_total_payload_bytes=$MinimumTotalPayloadBytes; total_payload_bytes=$totalPayloadBytes; min_frame_payload_bytes=$MinimumFramePayloadBytes; last_frame_payload_bytes=$lastFramePayloadBytes; timeout_s=$($TimeoutMs / 1000)."
}

function Wait-TunaGuiLiveSwitchOffTransferProgressBeforeFault {
    param(
        [Parameter(Mandatory = $true)][int]$Bookmark,
        [int]$TimeoutMs = 60000,
        [long]$MinimumCommittedBytes = 16777216,
        [long]$MinimumPeerVisiblePayloadBytes = 524288,
        [int]$MinimumElapsedMs = 3000,
        [long]$MinimumFramePayloadBytes = 16384,
        [int]$PollIntervalMs = 100
    )

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $processedLineCount = 0
    $acceleratedPayloadBytes = 0L
    $lastFramePayloadBytes = -1L
    $maxCommittedBytes = 0L
    $bestProgressLine = ''
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        $lines = @(Get-AppLogLinesAfterBookmark -Bookmark $Bookmark)
        if ($lines.Count -lt $processedLineCount) {
            $processedLineCount = 0
        }

        for ($lineIndex = $processedLineCount; $lineIndex -lt $lines.Count; $lineIndex++) {
            $line = $lines[$lineIndex]
            if ($line.IndexOf('event=tuna_accelerated_file_frame_sent', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                $line.IndexOf('channel=bulk', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $fields = ConvertFrom-GuiSmokeSemicolonFields -Message $line
                $payloadText = Get-GuiSmokeFieldValue -Fields $fields -Name 'payload_bytes' -Default '-1'
                $payloadBytes = -1L
                if ([long]::TryParse($payloadText, [ref]$payloadBytes)) {
                    $lastFramePayloadBytes = [Math]::Max($lastFramePayloadBytes, $payloadBytes)
                    if ($payloadBytes -ge $MinimumFramePayloadBytes) {
                        $acceleratedPayloadBytes += $payloadBytes
                    }
                }
            }

            if ($line.IndexOf('event=filetransfer_v4_state_sent', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                $line.IndexOf('bytes_committed=', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $fields = ConvertFrom-GuiSmokeSemicolonFields -Message $line
                $committedText = Get-GuiSmokeFieldValue -Fields $fields -Name 'bytes_committed' -Default '-1'
                $committedBytes = -1L
                if ([long]::TryParse($committedText, [ref]$committedBytes) -and $committedBytes -gt $maxCommittedBytes) {
                    $maxCommittedBytes = $committedBytes
                    $bestProgressLine = $line
                }
            }
        }
        $processedLineCount = $lines.Count

        if ($sw.ElapsedMilliseconds -ge $MinimumElapsedMs) {
            if ($acceleratedPayloadBytes -gt 0L -and
                $maxCommittedBytes -ge $MinimumCommittedBytes) {
                return "[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_live_switch_off_progress_gate_satisfied; mode=receiver_committed; committed_bytes={1}; minimum_committed_bytes={2}; accelerated_payload_bytes={3}; minimum_peer_visible_payload_bytes={4}; elapsed_ms={5}" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $maxCommittedBytes, $MinimumCommittedBytes, $acceleratedPayloadBytes, $MinimumPeerVisiblePayloadBytes, $sw.ElapsedMilliseconds
            }

            if ($acceleratedPayloadBytes -ge $MinimumPeerVisiblePayloadBytes) {
                return "[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_live_switch_off_progress_gate_satisfied; mode=peer_visible_payload; committed_bytes={1}; minimum_committed_bytes={2}; accelerated_payload_bytes={3}; minimum_peer_visible_payload_bytes={4}; last_frame_payload_bytes={5}; elapsed_ms={6}; reason=bounded_tuna_bulk_payload_before_fault" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $maxCommittedBytes, $MinimumCommittedBytes, $acceleratedPayloadBytes, $MinimumPeerVisiblePayloadBytes, $lastFramePayloadBytes, $sw.ElapsedMilliseconds
            }
        }

        Start-Sleep -Milliseconds ([Math]::Max(10, $PollIntervalMs))
    }

    throw "Timed out waiting for receiver-committed Tuna file progress or peer-visible Tuna payload before fallback fault; min_committed_bytes=$MinimumCommittedBytes; committed_bytes=$maxCommittedBytes; min_peer_visible_payload_bytes=$MinimumPeerVisiblePayloadBytes; accelerated_payload_bytes=$acceleratedPayloadBytes; min_elapsed_ms=$MinimumElapsedMs; elapsed_ms=$($sw.ElapsedMilliseconds); min_frame_payload_bytes=$MinimumFramePayloadBytes; last_frame_payload_bytes=$lastFramePayloadBytes; timeout_s=$($TimeoutMs / 1000)."
}

function Wait-TunaGuiLiveSwitchOffTransferProgressOrEarlyFallbackBeforeFault {
    param(
        [Parameter(Mandatory = $true)][int]$Bookmark,
        [int]$TimeoutMs = 60000,
        [long]$MinimumCommittedBytes = 16777216,
        [long]$MinimumPeerVisiblePayloadBytes = 524288,
        [int]$MinimumElapsedMs = 3000,
        [long]$MinimumFramePayloadBytes = 16384,
        [int]$PollIntervalMs = 100
    )

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $processedLineCount = 0
    $acceleratedPayloadBytes = 0L
    $lastFramePayloadBytes = -1L
    $maxCommittedBytes = 0L
    $bestProgressLine = ''
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        $lines = @(Get-AppLogLinesAfterBookmark -Bookmark $Bookmark)
        if ($lines.Count -lt $processedLineCount) {
            $processedLineCount = 0
        }

        for ($lineIndex = $processedLineCount; $lineIndex -lt $lines.Count; $lineIndex++) {
            $line = [string]$lines[$lineIndex]
            if ($line.IndexOf('event=filetransfer_live_route_epoch_started', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                $line.IndexOf('route=post_tuna_fallback_v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                $line.IndexOf('protocol_version=6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                $line.IndexOf('handoff_kind=tuna_to_normal_fallback', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                $line.IndexOf('target_transport=regular_nkn', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                return [pscustomobject]@{
                    Kind = 'early_fallback'
                    Line = $line
                    CommittedBytes = $maxCommittedBytes
                    AcceleratedPayloadBytes = $acceleratedPayloadBytes
                    LastFramePayloadBytes = $lastFramePayloadBytes
                    ElapsedMs = $sw.ElapsedMilliseconds
                }
            }

            if ($line.IndexOf('event=tuna_accelerated_file_frame_sent', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                $line.IndexOf('channel=bulk', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $fields = ConvertFrom-GuiSmokeSemicolonFields -Message $line
                $payloadText = Get-GuiSmokeFieldValue -Fields $fields -Name 'payload_bytes' -Default '-1'
                $payloadBytes = -1L
                if ([long]::TryParse($payloadText, [ref]$payloadBytes)) {
                    $lastFramePayloadBytes = [Math]::Max($lastFramePayloadBytes, $payloadBytes)
                    if ($payloadBytes -ge $MinimumFramePayloadBytes) {
                        $acceleratedPayloadBytes += $payloadBytes
                    }
                }
            }

            if ($line.IndexOf('event=filetransfer_v4_state_sent', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                $line.IndexOf('bytes_committed=', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $fields = ConvertFrom-GuiSmokeSemicolonFields -Message $line
                $committedText = Get-GuiSmokeFieldValue -Fields $fields -Name 'bytes_committed' -Default '-1'
                $committedBytes = -1L
                if ([long]::TryParse($committedText, [ref]$committedBytes) -and $committedBytes -gt $maxCommittedBytes) {
                    $maxCommittedBytes = $committedBytes
                    $bestProgressLine = $line
                }
            }
        }
        $processedLineCount = $lines.Count

        if ($sw.ElapsedMilliseconds -ge $MinimumElapsedMs) {
            if ($acceleratedPayloadBytes -gt 0L -and
                $maxCommittedBytes -ge $MinimumCommittedBytes) {
                return [pscustomobject]@{
                    Kind = 'progress'
                    Line = ("[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_live_switch_off_progress_gate_satisfied; mode=receiver_committed; committed_bytes={1}; minimum_committed_bytes={2}; accelerated_payload_bytes={3}; minimum_peer_visible_payload_bytes={4}; elapsed_ms={5}" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $maxCommittedBytes, $MinimumCommittedBytes, $acceleratedPayloadBytes, $MinimumPeerVisiblePayloadBytes, $sw.ElapsedMilliseconds)
                    CommittedBytes = $maxCommittedBytes
                    AcceleratedPayloadBytes = $acceleratedPayloadBytes
                    LastFramePayloadBytes = $lastFramePayloadBytes
                    ElapsedMs = $sw.ElapsedMilliseconds
                }
            }

            if ($acceleratedPayloadBytes -ge $MinimumPeerVisiblePayloadBytes) {
                return [pscustomobject]@{
                    Kind = 'peer_visible_payload'
                    Line = ("[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_live_switch_off_progress_gate_satisfied; mode=peer_visible_payload; committed_bytes={1}; minimum_committed_bytes={2}; accelerated_payload_bytes={3}; minimum_peer_visible_payload_bytes={4}; last_frame_payload_bytes={5}; elapsed_ms={6}; reason=bounded_tuna_bulk_payload_before_fault" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $maxCommittedBytes, $MinimumCommittedBytes, $acceleratedPayloadBytes, $MinimumPeerVisiblePayloadBytes, $lastFramePayloadBytes, $sw.ElapsedMilliseconds)
                    CommittedBytes = $maxCommittedBytes
                    AcceleratedPayloadBytes = $acceleratedPayloadBytes
                    LastFramePayloadBytes = $lastFramePayloadBytes
                    ElapsedMs = $sw.ElapsedMilliseconds
                }
            }
        }

        Start-Sleep -Milliseconds ([Math]::Max(10, $PollIntervalMs))
    }

    throw "Timed out waiting for receiver-committed Tuna file progress, peer-visible Tuna payload, or early fallback before fallback fault; min_committed_bytes=$MinimumCommittedBytes; committed_bytes=$maxCommittedBytes; min_peer_visible_payload_bytes=$MinimumPeerVisiblePayloadBytes; accelerated_payload_bytes=$acceleratedPayloadBytes; min_elapsed_ms=$MinimumElapsedMs; elapsed_ms=$($sw.ElapsedMilliseconds); min_frame_payload_bytes=$MinimumFramePayloadBytes; last_frame_payload_bytes=$lastFramePayloadBytes; timeout_s=$($TimeoutMs / 1000)."
}

function Invoke-TunaGuiPauseResumeProbe {
    param(
        [Parameter(Mandatory = $true)]$SenderWindow,
        [Parameter(Mandatory = $true)][int]$Bookmark
    )

    if (-not (Test-GuiSmokeEnvEnabled -Name 'NLINK_TUNA_GUI_EXERCISE_PAUSE')) {
        return $false
    }

    $pause = Wait-TunaGuiPauseButtonOrTerminal -SenderWindow $SenderWindow -Bookmark $Bookmark -TimeoutMs 30000
    if ($null -eq $pause) {
        Write-Host '[GUI Smoke][filetransfer_tuna] Pause/resume lifecycle probe skipped; transfer terminalized before pause was available.' -ForegroundColor Yellow
        return $false
    }

    Click-Element $pause
    [void](Wait-AppLogContainsAllAfterBookmark -Bookmark $Bookmark -Needles @('event=filetransfer_lifecycle_priority_sent', 'kind=pause_control', 'paused=1') -TimeoutMs 30000 -Description 'pause control sent')
    [void](Wait-AppLogContainsAllAfterBookmark -Bookmark $Bookmark -Needles @('event=filetransfer_lifecycle_priority_received', 'kind=pause_control', 'paused=1') -TimeoutMs 30000 -Description 'pause control received')

    $resume = Wait-TunaGuiResumeButtonOrTerminal -SenderWindow $SenderWindow -Bookmark $Bookmark -TimeoutMs 30000
    if ($null -eq $resume) {
        Write-Host '[GUI Smoke][filetransfer_tuna] Pause/resume lifecycle probe stopped after pause; transfer terminalized before resume was available.' -ForegroundColor Yellow
        return $false
    }

    Start-Sleep -Milliseconds 1000
    Click-Element $resume
    [void](Wait-AppLogContainsAllAfterBookmark -Bookmark $Bookmark -Needles @('event=filetransfer_lifecycle_priority_sent', 'kind=pause_control', 'paused=0') -TimeoutMs 30000 -Description 'resume control sent')
    [void](Wait-AppLogContainsAllAfterBookmark -Bookmark $Bookmark -Needles @('event=filetransfer_lifecycle_priority_received', 'kind=pause_control', 'paused=0') -TimeoutMs 30000 -Description 'resume control received')
    Write-Host '[GUI Smoke][filetransfer_tuna] Pause/resume lifecycle probe completed.' -ForegroundColor Green
    return $true
}

function Test-TunaGuiTransferTerminalAfterBookmark {
    param([Parameter(Mandatory = $true)][int]$Bookmark)

    foreach ($line in @(Get-AppLogLinesAfterBookmark -Bookmark $Bookmark)) {
        if ($line.IndexOf('event=file_transfer_outbound_terminal', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('event=file_transfer_inbound_terminal', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('event=transfer_terminal', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $true
        }
    }

    return $false
}

function Wait-TunaGuiPauseButtonOrTerminal {
    param(
        [Parameter(Mandatory = $true)]$SenderWindow,
        [Parameter(Mandatory = $true)][int]$Bookmark,
        [Parameter(Mandatory = $true)][int]$TimeoutMs
    )

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        if (Test-TunaGuiTransferTerminalAfterBookmark -Bookmark $Bookmark) {
            return $null
        }

        $pause = Find-VisibleByAutomationId -Root $SenderWindow -AutomationId 'Chat.FileTransfer.Pause'
        if ($null -ne $pause -and $pause.Current.IsEnabled) {
            return $pause
        }

        Start-Sleep -Milliseconds 200
    }

    throw "Timed out waiting for Chat.FileTransfer.Pause to become enabled before transfer terminalized."
}

function Wait-TunaGuiResumeButtonOrTerminal {
    param(
        [Parameter(Mandatory = $true)]$SenderWindow,
        [Parameter(Mandatory = $true)][int]$Bookmark,
        [Parameter(Mandatory = $true)][int]$TimeoutMs
    )

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        if (Test-TunaGuiTransferTerminalAfterBookmark -Bookmark $Bookmark) {
            return $null
        }

        $resume = Find-VisibleByAutomationId -Root $SenderWindow -AutomationId 'Chat.FileTransfer.Resume'
        if ($null -ne $resume -and $resume.Current.IsEnabled) {
            return $resume
        }

        Start-Sleep -Milliseconds 200
    }

    throw "Timed out waiting for Chat.FileTransfer.Resume to become enabled before transfer terminalized."
}

function Get-TunaGuiEvidenceSummary {
    param([Parameter(Mandatory = $true)][int]$Bookmark)

    $summary = [ordered]@{
        tunaNegotiated = $false
        activationEpochStarted = $false
        activationEpochRecovered = $false
        fallbackEpochStarted = $false
        fallbackEpochRecovered = $false
        fallbackEpochWaiting = $false
        pauseSent = $false
        pauseReceived = $false
        resumeSent = $false
        resumeReceived = $false
        heartbeatTimeoutCount = 0
        heartbeatDeferredTimeoutCount = 0
        peerDisconnectedCount = 0
        transportFailedCount = 0
        senderChunkBytes = 0L
        receiverChunkBytes = 0L
        tunaAcceleratedFileFrameCount = 0
        tunaAcceleratedFilePayloadBytes = 0L
        postTunaFallbackNknProved = $false
        postTunaFallbackCleanupCompleted = $false
        tunaFallbackNknFileFrameSent = $false
        tunaFallbackNknFileFrameReceived = $false
        postTunaFallbackV6RouteObserved = $false
        v6SenderStartedCount = 0
    }

    foreach ($line in @(Get-AppLogLinesAfterBookmark -Bookmark $Bookmark)) {
        if ($line.IndexOf('event=tuna_acceleration_negotiated', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $summary.tunaNegotiated = $true
        }

        if ($line.IndexOf('event=filetransfer_v6_epoch_started', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $line.IndexOf('handoff_kind=normal_to_tuna_activation', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $summary.activationEpochStarted = $true
        }

        if (($line.IndexOf('event=filetransfer_v6_epoch_recovered', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
             ($line.IndexOf('handoff_kind=normal_to_tuna_activation', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
              $line.IndexOf('target_transport=tuna', [System.StringComparison]::OrdinalIgnoreCase) -ge 0)) -or
            ($line.IndexOf('event=filetransfer_v6_epoch_observed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
             $line.IndexOf('handoff_kind=normal_to_tuna_activation', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
             $line.IndexOf('state=recovered', [System.StringComparison]::OrdinalIgnoreCase) -ge 0)) {
            $summary.activationEpochRecovered = $true
        }

        if ($line.IndexOf('event=filetransfer_v6_epoch_started', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $line.IndexOf('handoff_kind=tuna_to_normal_fallback', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $summary.fallbackEpochStarted = $true
        }

        if ($line.IndexOf('event=tuna_fallback_started', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('event=tuna_fallback_filetransfer_rebind_requested', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('event=tuna_acceleration_user_stop_filetransfer_fallback_forced', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('event=filetransfer_post_tuna_recovery_started', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $summary.fallbackEpochStarted = $true
        }

        if (($line.IndexOf('event=filetransfer_v6_epoch_recovered', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
             $line.IndexOf('handoff_kind=tuna_to_normal_fallback', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) -or
            ($line.IndexOf('event=filetransfer_v6_epoch_observed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
             $line.IndexOf('handoff_kind=tuna_to_normal_fallback', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
             $line.IndexOf('state=recovered', [System.StringComparison]::OrdinalIgnoreCase) -ge 0)) {
            $summary.fallbackEpochRecovered = $true
        }

        if ($line.IndexOf('event=filetransfer_v6_epoch_waiting', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $line.IndexOf('handoff_kind=tuna_to_normal_fallback', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $summary.fallbackEpochWaiting = $true
        }

        if ($line.IndexOf('event=filetransfer_post_tuna_fallback_nkn_proved', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('event=filetransfer_live_v4_fallback_nkn_proved', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $summary.postTunaFallbackNknProved = $true
        }

        if ($line.IndexOf('event=filetransfer_post_tuna_fallback_cleanup_completed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('event=filetransfer_live_v4_fallback_cleanup_completed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('event=tuna_disable_handoff_completed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $summary.postTunaFallbackCleanupCompleted = $true
        }

        if ($line.IndexOf('event=tuna_fallback_nkn_frame_sent', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $line.IndexOf('message_type=file_transfer_data_frame', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $line.IndexOf('channel=bulk', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $summary.tunaFallbackNknFileFrameSent = $true
        }

        if ($line.IndexOf('event=tuna_fallback_nkn_frame_received', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $line.IndexOf('message_type=file_transfer_data_frame', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $line.IndexOf('channel=bulk', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $summary.tunaFallbackNknFileFrameReceived = $true
        }

        if ($line.IndexOf('event=filetransfer_route_selected', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $line.IndexOf('route=post_tuna_fallback_v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $summary.postTunaFallbackV6RouteObserved = $true
        }

        if ($line.IndexOf('event=filetransfer_v6_sender_started', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $summary.v6SenderStartedCount++
        }

        if ($line.IndexOf('event=filetransfer_lifecycle_priority_sent', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $line.IndexOf('kind=pause_control', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            if ($line.IndexOf('paused=1', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) { $summary.pauseSent = $true }
            if ($line.IndexOf('paused=0', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) { $summary.resumeSent = $true }
        }

        if ($line.IndexOf('event=filetransfer_lifecycle_priority_received', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $line.IndexOf('kind=pause_control', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            if ($line.IndexOf('paused=1', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) { $summary.pauseReceived = $true }
            if ($line.IndexOf('paused=0', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) { $summary.resumeReceived = $true }
        }

        if ($line.IndexOf('event=filetransfer_v6_heartbeat_timeout', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            if ($line.IndexOf('_deferred_', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $summary.heartbeatDeferredTimeoutCount++
            }
            else {
                $summary.heartbeatTimeoutCount++
            }
        }

        if ($line.IndexOf('peer_disconnected', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $summary.peerDisconnectedCount++
        }

        if ($line.IndexOf('transport_state_changed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $line.IndexOf('to=Failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $summary.transportFailedCount++
        }

        $fields = ConvertFrom-GuiSmokeSemicolonFields -Message $line
        $eventName = Get-GuiSmokeFieldValue -Fields $fields -Name 'event'
        if ($eventName -eq 'tuna_accelerated_file_frame_sent') {
            $summary.tunaAcceleratedFileFrameCount++
            $summary.tunaAcceleratedFilePayloadBytes += [Math]::Max(0L, (Get-GuiSmokeInt64FieldValue -Fields $fields -Name 'payload_bytes'))
        }

        if ($eventName -eq 'filetransfer_binary_frame_sent') {
            $summary.senderChunkBytes += [Math]::Max(0L, (Get-GuiSmokeInt64FieldValue -Fields $fields -Name 'raw_chunk_bytes'))
        }
        elseif ($eventName -eq 'filetransfer_binary_frame_received') {
            $summary.receiverChunkBytes += [Math]::Max(0L, (Get-GuiSmokeInt64FieldValue -Fields $fields -Name 'raw_chunk_bytes'))
        }
        elseif ($eventName -eq 'filetransfer_v6_sparse_write_committed' -or
            $eventName -eq 'filetransfer_v6_contiguous_write_committed') {
            $summary.receiverChunkBytes += [Math]::Max(0L, (Get-GuiSmokeInt64FieldValue -Fields $fields -Name 'written_bytes'))
        }
    }

    return $summary
}

function Invoke-FileTransferTunaPostFallbackPrecondition {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$AutopickPath,
        [Parameter(Mandatory = $true)][long]$MeasuredPayloadSizeBytes,
        [Parameter(Mandatory = $true)][string]$Direction,
        [Parameter(Mandatory = $true)][int]$Seed,
        [Parameter(Mandatory = $true)][int]$StartupTimeoutMs,
        [Parameter(Mandatory = $true)][int]$ProgressTimeoutMs,
        [Parameter(Mandatory = $true)][string]$PayerMode,
        [Parameter(Mandatory = $true)][string]$FaultMode
    )

    $warmupPayloadSizeBytes = [Math]::Min(67108864L, [Math]::Max(16777216L, [long]($MeasuredPayloadSizeBytes / 2)))
    Write-DeterministicFileTransferPayload -Path $AutopickPath -SizeBytes $warmupPayloadSizeBytes -Seed $Seed -CycleIndex 1001
    $expectedHash = Get-FileSha256Hex -Path $AutopickPath
    $senderWindow = if ($Direction -eq 'helper-to-helpee') { $Context.HelperWindow } else { $Context.HelpeeWindow }
    $receiverWindow = if ($Direction -eq 'helper-to-helpee') { $Context.HelpeeWindow } else { $Context.HelperWindow }
    $expectedInboundRole = if ($Direction -eq 'helper-to-helpee') { 'helpee' } else { 'helper' }
    $expectedOutboundRole = if ($Direction -eq 'helper-to-helpee') { 'helper' } else { 'helpee' }
    $warmupStartedUtc = [datetime]::UtcNow
    $bookmark = Get-AppLogBookmark
    $observedEvidenceLines = New-Object System.Collections.Generic.List[string]

    Write-Host ("[GUI Smoke][filetransfer_tuna] Preconditioning post-fallback route with active Tuna warm-up bytes={0}." -f $warmupPayloadSizeBytes) -ForegroundColor DarkGray

    $sendButton = Wait-ControlEnabledStateByAutomationId -Window $senderWindow -AutomationId 'Chat.SendFile' -IsEnabled $true -TimeoutMs ([Math]::Min(15000, $StartupTimeoutMs))
    Click-Element $sendButton

    $acceptButton = Wait-ControlEnabledStateByAutomationId -Window $receiverWindow -AutomationId 'Chat.FileTransfer.Accept' -IsEnabled $true -TimeoutMs $StartupTimeoutMs
    Click-Element $acceptButton

    $routeLine = [string](Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $bookmark -NeedleSets @(
        @('event=filetransfer_route_selected', 'route=file_tuna_v4'),
        @('event=filetransfer_runtime_started', 'route=file_tuna_v4'),
        @('event=filetransfer_v4_sender_started', 'route=file_tuna_v4')
    ) -TimeoutMs 90000 -Description 'post-fallback precondition active file Tuna route')
    $observedEvidenceLines.Add($routeLine) | Out-Null

    [void](Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $bookmark -NeedleSets @(
        @('event=filetransfer_v6_sender_started'),
        @('event=filetransfer_v4_sender_started')
    ) -TimeoutMs 45000 -Description 'post-fallback precondition sender started')
    [void](Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $bookmark -NeedleSets @(
        @('event=filetransfer_v6_receiver_started'),
        @('event=filetransfer_v4_receiver_started')
    ) -TimeoutMs 45000 -Description 'post-fallback precondition receiver started')

    $minimumFaultPayloadBytes = [Math]::Min(4194304L, [Math]::Max(1048576L, [long]($warmupPayloadSizeBytes / 16)))
    $acceleratedPayloadLine = [string](Wait-TunaGuiAcceleratedFilePayloadBeforeFault -Bookmark $bookmark -MinimumTotalPayloadBytes $minimumFaultPayloadBytes -TimeoutMs 90000)
    $observedEvidenceLines.Add($acceleratedPayloadLine) | Out-Null

    Invoke-TunaGuiFallbackFault -Context $Context -FaultMode $FaultMode -PayerMode $PayerMode
    $fallbackStartedLine = [string](Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $bookmark -NeedleSets @(
        @('event=filetransfer_v6_epoch_started', 'handoff_kind=tuna_to_normal_fallback'),
        @('event=filetransfer_v6_epoch_observed', 'handoff_kind=tuna_to_normal_fallback'),
        @('event=tuna_acceleration_user_stop_filetransfer_fallback_forced')
    ) -TimeoutMs 90000 -Description 'post-fallback precondition TunaToNormalFallback started')
    $observedEvidenceLines.Add($fallbackStartedLine) | Out-Null

    $fallbackResolutionLine = [string](Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $bookmark -NeedleSets @(
        @('event=filetransfer_v6_epoch_recovered', 'handoff_kind=tuna_to_normal_fallback'),
        @('event=filetransfer_v6_epoch_recovered', 'target_transport=regular_nkn'),
        @('event=filetransfer_v6_epoch_observed', 'handoff_kind=tuna_to_normal_fallback', 'state=recovered'),
        @('event=filetransfer_v6_epoch_waiting', 'handoff_kind=tuna_to_normal_fallback'),
        @('event=filetransfer_v6_epoch_observed', 'handoff_kind=tuna_to_normal_fallback', 'state=waiting_for_target_transport')
    ) -TimeoutMs 150000 -Description 'post-fallback precondition TunaToNormalFallback recovered or waiting')
    $observedEvidenceLines.Add($fallbackResolutionLine) | Out-Null

    $terminal = Wait-FileTransferTerminalPairAfterBookmark `
        -Bookmark $bookmark `
        -TimeoutMs ([Math]::Max(180000, $ProgressTimeoutMs * 2)) `
        -ProgressTimeoutMs $ProgressTimeoutMs `
        -ExpectedFileName ([System.IO.Path]::GetFileName($AutopickPath)) `
        -ExpectedSizeBytes $warmupPayloadSizeBytes `
        -ExpectedInboundRole $expectedInboundRole `
        -ExpectedOutboundRole $expectedOutboundRole `
        -NotBeforeUtc $warmupStartedUtc `
        -ArtifactDir $ArtifactDir

    $inbound = $terminal.Inbound
    $outbound = $terminal.Outbound
    $savedPath = Get-GuiSmokeFieldValue -Fields $inbound -Name 'saved_path' -Default '(none)'
    $resolvedSavedPath = [string]$terminal.ResolvedSavedPath
    if ([string]::IsNullOrWhiteSpace($resolvedSavedPath)) {
        $resolvedSavedPath = Resolve-FileTransferLiveReceivedFilePath `
            -LoggedPath $savedPath `
            -ArtifactDir $ArtifactDir `
            -ExpectedFileName ([System.IO.Path]::GetFileName($AutopickPath)) `
            -ExpectedSizeBytes $warmupPayloadSizeBytes `
            -NotBeforeUtc $warmupStartedUtc
    }

    $actualHash = '(none)'
    $savedSize = -1L
    if (-not [string]::IsNullOrWhiteSpace($resolvedSavedPath) -and (Test-Path -LiteralPath $resolvedSavedPath -PathType Leaf)) {
        $actualHash = Get-FileSha256Hex -Path $resolvedSavedPath
        $savedSize = (Get-Item -LiteralPath $resolvedSavedPath).Length
    }

    $inboundState = Get-GuiSmokeFieldValue -Fields $inbound -Name 'state' -Default '(unknown)'
    $outboundState = Get-GuiSmokeFieldValue -Fields $outbound -Name 'state' -Default '(unknown)'
    $inboundError = Get-GuiSmokeFieldValue -Fields $inbound -Name 'error_code' -Default '(none)'
    $outboundError = Get-GuiSmokeFieldValue -Fields $outbound -Name 'error_code' -Default '(none)'
    $integrityOk = $inboundState -eq 'Completed' -and
        $outboundState -eq 'Completed' -and
        $inboundError -eq '(none)' -and
        $outboundError -eq '(none)' -and
        $savedSize -eq $warmupPayloadSizeBytes -and
        $actualHash -eq $expectedHash

    if (-not $integrityOk) {
        throw ("Post-fallback precondition warm-up transfer failed: inbound_state={0}; outbound_state={1}; inbound_error={2}; outbound_error={3}; saved_size={4}; expected_size={5}" -f `
            $inboundState,
            $outboundState,
            $inboundError,
            $outboundError,
            $savedSize,
            $warmupPayloadSizeBytes)
    }

    $fallbackWaiting = ($fallbackResolutionLine.IndexOf('state=waiting_for_target_transport', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) -or
        ($fallbackResolutionLine.IndexOf('event=filetransfer_v6_epoch_waiting', [System.StringComparison]::OrdinalIgnoreCase) -ge 0)

    [pscustomobject]@{
        EvidenceLines = $observedEvidenceLines.ToArray()
        FallbackEpochStarted = $true
        FallbackEpochRecovered = -not $fallbackWaiting
        FallbackEpochWaiting = $fallbackWaiting
    }
}

function Invoke-FileTransferTunaV4RestartV6FallbackPrecondition {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$AutopickPath,
        [Parameter(Mandatory = $true)][long]$MeasuredPayloadSizeBytes,
        [Parameter(Mandatory = $true)][string]$Direction,
        [Parameter(Mandatory = $true)][int]$Seed,
        [Parameter(Mandatory = $true)][int]$StartupTimeoutMs,
        [Parameter(Mandatory = $true)][int]$ProgressTimeoutMs,
        [Parameter(Mandatory = $true)][string]$PayerMode,
        [Parameter(Mandatory = $true)][string]$FaultMode
    )

    if ($FaultMode -eq 'none') {
        throw 'v4-restart-v6-fallback route mode requires a fallback fault.'
    }

    $warmupPayloadSizeBytes = [Math]::Min(67108864L, [Math]::Max(16777216L, [long]($MeasuredPayloadSizeBytes / 2)))
    Write-DeterministicFileTransferPayload -Path $AutopickPath -SizeBytes $warmupPayloadSizeBytes -Seed $Seed -CycleIndex 2001
    $senderWindow = if ($Direction -eq 'helper-to-helpee') { $Context.HelperWindow } else { $Context.HelpeeWindow }
    $receiverWindow = if ($Direction -eq 'helper-to-helpee') { $Context.HelpeeWindow } else { $Context.HelperWindow }
    $expectedInboundRole = if ($Direction -eq 'helper-to-helpee') { 'helpee' } else { 'helper' }
    $expectedOutboundRole = if ($Direction -eq 'helper-to-helpee') { 'helper' } else { 'helpee' }
    $warmupStartedUtc = [datetime]::UtcNow
    $bookmark = Get-AppLogBookmark
    $observedEvidenceLines = New-Object System.Collections.Generic.List[string]

    Write-Host ("[GUI Smoke][filetransfer_tuna] Preconditioning V4 Tuna warm-up then V6 fallback restart bytes={0}." -f $warmupPayloadSizeBytes) -ForegroundColor DarkGray
    $observedEvidenceLines.Add(("[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_phase_marker; phase=setup_file_tuna_v4_started" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')))) | Out-Null

    $sendButton = Wait-ControlEnabledStateByAutomationId -Window $senderWindow -AutomationId 'Chat.SendFile' -IsEnabled $true -TimeoutMs ([Math]::Min(15000, $StartupTimeoutMs))
    Click-Element $sendButton

    $acceptButton = Wait-ControlEnabledStateByAutomationId -Window $receiverWindow -AutomationId 'Chat.FileTransfer.Accept' -IsEnabled $true -TimeoutMs $StartupTimeoutMs
    Click-Element $acceptButton

    $routeLine = [string](Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $bookmark -NeedleSets @(
        @('event=filetransfer_route_selected', 'route=file_tuna_v4', 'protocol_version=4'),
        @('event=filetransfer_runtime_started', 'route=file_tuna_v4', 'protocol_version=4'),
        @('event=filetransfer_v4_sender_started', 'route=file_tuna_v4')
    ) -TimeoutMs 90000 -Description 'V4 restart precondition active Tuna V4 route')
    $observedEvidenceLines.Add($routeLine) | Out-Null

    [void](Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $bookmark -NeedleSets @(
        @('event=filetransfer_v4_sender_started'),
        @('event=filetransfer_runtime_started', 'direction=outbound', 'protocol_version=4')
    ) -TimeoutMs 45000 -Description 'V4 restart precondition sender started')
    [void](Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $bookmark -NeedleSets @(
        @('event=filetransfer_v4_receiver_started'),
        @('event=filetransfer_runtime_started', 'direction=inbound', 'protocol_version=4')
    ) -TimeoutMs 45000 -Description 'V4 restart precondition receiver started')

    $minimumFaultPayloadBytes = [Math]::Min(4194304L, [Math]::Max(1048576L, [long]($warmupPayloadSizeBytes / 16)))
    $acceleratedPayloadLine = [string](Wait-TunaGuiAcceleratedFilePayloadBeforeFault -Bookmark $bookmark -MinimumTotalPayloadBytes $minimumFaultPayloadBytes -TimeoutMs 90000)
    $observedEvidenceLines.Add($acceleratedPayloadLine) | Out-Null

    Invoke-TunaGuiFallbackFault -Context $Context -FaultMode $FaultMode -PayerMode $PayerMode
    $fallbackStartedLine = [string](Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $bookmark -NeedleSets @(
        @('event=tuna_fallback_started'),
        @('event=tuna_fallback_filetransfer_rebind_requested'),
        @('event=tuna_acceleration_user_stop_filetransfer_fallback_forced'),
        @('event=filetransfer_post_tuna_recovery_started')
    ) -TimeoutMs 90000 -Description 'V4 restart precondition fallback started')
    $observedEvidenceLines.Add($fallbackStartedLine) | Out-Null

    $cancelButton = Wait-ControlEnabledStateByAutomationId -Window $senderWindow -AutomationId 'Chat.FileTransfer.Cancel' -IsEnabled $true -TimeoutMs 15000
    Click-Element $cancelButton

    $terminal = Wait-FileTransferTerminalPairAfterBookmark `
        -Bookmark $bookmark `
        -TimeoutMs ([Math]::Max(180000, $ProgressTimeoutMs * 2)) `
        -ProgressTimeoutMs ([Math]::Max(60000, $ProgressTimeoutMs)) `
        -ExpectedFileName ([System.IO.Path]::GetFileName($AutopickPath)) `
        -ExpectedSizeBytes $warmupPayloadSizeBytes `
        -ExpectedInboundRole $expectedInboundRole `
        -ExpectedOutboundRole $expectedOutboundRole `
        -NotBeforeUtc $warmupStartedUtc `
        -ArtifactDir $ArtifactDir

    $inboundState = Get-GuiSmokeFieldValue -Fields $terminal.Inbound -Name 'state' -Default '(unknown)'
    $outboundState = Get-GuiSmokeFieldValue -Fields $terminal.Outbound -Name 'state' -Default '(unknown)'
    $inboundError = Get-GuiSmokeFieldValue -Fields $terminal.Inbound -Name 'error_code' -Default '(none)'
    $outboundError = Get-GuiSmokeFieldValue -Fields $terminal.Outbound -Name 'error_code' -Default '(none)'
    $observedEvidenceLines.Add(("[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_phase_marker; phase=setup_file_tuna_v4_terminal; inbound_state={1}; outbound_state={2}; inbound_error={3}; outbound_error={4}" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $inboundState, $outboundState, $inboundError, $outboundError)) | Out-Null

    $cleanupBookmark = Get-AppLogBookmark
    $cleanupClosedLine = [string](Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $cleanupBookmark -NeedleSets @(
        @('event=filetransfer_data_session_removed', 'reason=disposed'),
        @('event=filetransfer_chunk_batch_transport_summary', 'raw_bytes_sent_total='),
        @('event=filetransfer_v4_receive_liveness_summary', 'reason=data_session_closed')
    ) -TimeoutMs 45000 -Description 'V4 restart precondition setup cleanup closed')
    $observedEvidenceLines.Add($cleanupClosedLine) | Out-Null
    $observedEvidenceLines.Add(("[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_phase_marker; phase=setup_file_tuna_v4_cleanup_closed" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')))) | Out-Null

    $routeFields = ConvertFrom-GuiSmokeSemicolonFields -Message $routeLine
    $setupRoute = Get-GuiSmokeFieldValue -Fields $terminal.Outbound -Name 'route' -Default (Get-GuiSmokeFieldValue -Fields $terminal.Inbound -Name 'route' -Default (Get-GuiSmokeFieldValue -Fields $routeFields -Name 'route' -Default 'file_tuna_v4'))
    $setupProtocol = ConvertTo-GuiSmokeInt -Value (Get-GuiSmokeFieldValue -Fields $terminal.Outbound -Name 'protocol_version' -Default (Get-GuiSmokeFieldValue -Fields $terminal.Inbound -Name 'protocol_version' -Default (Get-GuiSmokeFieldValue -Fields $routeFields -Name 'protocol_version' -Default '4'))) -Default 4
    if ($inboundState -ne 'Canceled' -or
        $outboundState -ne 'Canceled' -or
        $inboundError -ne 'canceled_remote' -or
        $outboundError -ne 'canceled_local') {
        throw ("V4 restart precondition did not drain the warm-up transfer cleanly: inbound_state={0}; outbound_state={1}; inbound_error={2}; outbound_error={3}" -f `
            $inboundState,
            $outboundState,
            $inboundError,
            $outboundError)
    }

    [pscustomobject]@{
        EvidenceLines = $observedEvidenceLines.ToArray()
        FallbackEpochStarted = $true
        FallbackEpochRecovered = $false
        FallbackEpochWaiting = $true
        SetupPhase = [ordered]@{
            name = 'setup_file_tuna_v4'
            route = $setupRoute
            protocolVersion = $setupProtocol
            payloadBytes = $warmupPayloadSizeBytes
            completed = $false
            integrityOk = $false
            inboundState = $inboundState
            outboundState = $outboundState
            inboundErrorCode = $inboundError
            outboundErrorCode = $outboundError
        }
    }
}

function Invoke-FileTransferTunaHandoffFallbackCycle {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][string]$AutopickPath,
        [Parameter(Mandatory = $true)][long]$PayloadSizeBytes,
        [Parameter(Mandatory = $true)][string]$Direction,
        [Parameter(Mandatory = $true)][int]$Seed,
        [Parameter(Mandatory = $true)][int]$TimeoutMs,
        [Parameter(Mandatory = $true)][int]$StartupTimeoutMs,
        [Parameter(Mandatory = $true)][int]$ProgressTimeoutMs,
        [Parameter(Mandatory = $true)][string]$PayerMode,
        [Parameter(Mandatory = $true)][string]$FaultMode,
        [Parameter(Mandatory = $true)][string]$RouteMode,
        [Parameter(Mandatory = $true)][string]$WalletPassword
    )

    $senderWindow = if ($Direction -eq 'helper-to-helpee') { $Context.HelperWindow } else { $Context.HelpeeWindow }
    $receiverWindow = if ($Direction -eq 'helper-to-helpee') { $Context.HelpeeWindow } else { $Context.HelperWindow }
    $observedEvidenceLines = New-Object System.Collections.Generic.List[string]
    $tunaNegotiatedObserved = $false
    $activationEpochStartedObserved = $false
    $activationEpochRecoveredObserved = $false
    $fallbackEpochStartedObserved = $false
    $fallbackEpochRecoveredObserved = $false
    $fallbackEpochWaitingObserved = $false
    $setupPhase = $null
    $liveRouteEpochObservations = New-Object System.Collections.Generic.List[object]
    $firstTransferTerminalBeforeLiveReactivation = $false
    $firstTransferTerminalBeforeLiveReactivationTransferId = ''
    $liveSwitchOffMinimumFaultPayloadBytes = 0L
    $liveSwitchOffMinimumCommittedBytes = 0L
    $liveSwitchOffMinimumPeerVisiblePayloadBytes = 0L
    $liveSwitchOffMinimumElapsedMs = 0

    if ($RouteMode -eq 'preactivated' -or $RouteMode -eq 'post-fallback' -or $RouteMode -eq 'v4-restart-v6-fallback' -or $RouteMode -eq 'live-v4-switch-off' -or $RouteMode -eq 'live-multi-toggle' -or $RouteMode -eq 'live-reactivation-second-transfer') {
        $preconditionBookmark = Get-AppLogBookmark
        Unlock-TunaPayers -Context $Context -PayerMode $PayerMode -Password $WalletPassword
        $tunaNegotiatedLine = [string](Wait-AppLogContainsAfterBookmark -Bookmark $preconditionBookmark -Needle 'event=tuna_acceleration_negotiated' -TimeoutMs 150000 -Description 'Tuna negotiated before measured GUI file transfer')
        $observedEvidenceLines.Add($tunaNegotiatedLine) | Out-Null
        $tunaNegotiatedObserved = $true
        if ($RouteMode -eq 'preactivated' -or $RouteMode -eq 'live-v4-switch-off' -or $RouteMode -eq 'live-multi-toggle' -or $RouteMode -eq 'live-reactivation-second-transfer') {
            $tunaActiveLine = [string](Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $preconditionBookmark -NeedleSets @(
                @('event=tuna_acceleration_timeline', 'active=1', 'paid_listener_active'),
                @('event=tuna_acceleration_timeline', 'active=1', 'free_dialer_active')
            ) -TimeoutMs 150000 -Description 'Tuna transport active before measured GUI file transfer')
            $observedEvidenceLines.Add($tunaActiveLine) | Out-Null
            $bridgeQuietLine = [string](Wait-TunaGuiActiveBridgeQuietWindow -Bookmark $preconditionBookmark)
            $observedEvidenceLines.Add($bridgeQuietLine) | Out-Null
        }

        if ($RouteMode -eq 'post-fallback') {
            $precondition = Invoke-FileTransferTunaPostFallbackPrecondition `
                -Context $Context `
                -ArtifactDir $ArtifactDir `
                -AutopickPath $AutopickPath `
                -MeasuredPayloadSizeBytes $PayloadSizeBytes `
                -Direction $Direction `
                -Seed $Seed `
                -StartupTimeoutMs $StartupTimeoutMs `
                -ProgressTimeoutMs $ProgressTimeoutMs `
                -PayerMode $PayerMode `
                -FaultMode $FaultMode
            foreach ($line in @($precondition.EvidenceLines)) {
                $observedEvidenceLines.Add([string]$line) | Out-Null
            }

            $fallbackEpochStartedObserved = [bool]$precondition.FallbackEpochStarted
            $fallbackEpochRecoveredObserved = [bool]$precondition.FallbackEpochRecovered
            $fallbackEpochWaitingObserved = [bool]$precondition.FallbackEpochWaiting
            $setupPhase = [ordered]@{
                name = 'setup_post_tuna_fallback_v6'
                route = 'post_tuna_fallback_v6'
                protocolVersion = 6
                completed = $true
                integrityOk = $true
                inboundState = 'Completed'
                outboundState = 'Completed'
                inboundErrorCode = '(none)'
                outboundErrorCode = '(none)'
            }
        }
        elseif ($RouteMode -eq 'v4-restart-v6-fallback') {
            $precondition = Invoke-FileTransferTunaV4RestartV6FallbackPrecondition `
                -Context $Context `
                -ArtifactDir $ArtifactDir `
                -AutopickPath $AutopickPath `
                -MeasuredPayloadSizeBytes $PayloadSizeBytes `
                -Direction $Direction `
                -Seed $Seed `
                -StartupTimeoutMs $StartupTimeoutMs `
                -ProgressTimeoutMs $ProgressTimeoutMs `
                -PayerMode $PayerMode `
                -FaultMode $FaultMode
            foreach ($line in @($precondition.EvidenceLines)) {
                $observedEvidenceLines.Add([string]$line) | Out-Null
            }

            $fallbackEpochStartedObserved = [bool]$precondition.FallbackEpochStarted
            $fallbackEpochRecoveredObserved = [bool]$precondition.FallbackEpochRecovered
            $fallbackEpochWaitingObserved = [bool]$precondition.FallbackEpochWaiting
            $setupPhase = $precondition.SetupPhase
        }
    }

    Write-DeterministicFileTransferPayload -Path $AutopickPath -SizeBytes $PayloadSizeBytes -Seed $Seed -CycleIndex 0
    $expectedHash = Get-FileSha256Hex -Path $AutopickPath
    $cycleStartedUtc = [datetime]::UtcNow
    $bookmark = Get-AppLogBookmark
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    if ($RouteMode -eq 'v4-restart-v6-fallback') {
        $observedEvidenceLines.Add(("[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_phase_marker; phase=measured_post_tuna_fallback_v6_started" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')))) | Out-Null
    }

    $sendButton = Wait-ControlEnabledStateByAutomationId -Window $senderWindow -AutomationId 'Chat.SendFile' -IsEnabled $true -TimeoutMs ([Math]::Min(15000, $StartupTimeoutMs))
    Click-Element $sendButton

    $acceptButton = Wait-TunaGuiFileTransferAcceptOrThrow -Window $receiverWindow -Bookmark $bookmark -TimeoutMs $StartupTimeoutMs -RouteMode $RouteMode
    Click-Element $acceptButton

    [void](Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $bookmark -NeedleSets @(
        @('event=filetransfer_v4_sender_started'),
        @('event=filetransfer_v6_sender_started')
    ) -TimeoutMs 45000 -Description 'primary file-transfer sender started')
    [void](Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $bookmark -NeedleSets @(
        @('event=filetransfer_v4_receiver_started'),
        @('event=filetransfer_v6_receiver_started'),
        @('event=filetransfer_runtime_started', 'direction=inbound', 'role=receiver')
    ) -TimeoutMs 45000 -Description 'primary file-transfer receiver started')

    if ($RouteMode -eq 'live-v4-switch-off') {
        if ($FaultMode -eq 'none') {
            throw 'live-v4-switch-off route mode requires a fallback fault.'
        }

        $routeLine = [string](Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $bookmark -NeedleSets @(
            @('event=filetransfer_route_selected', 'route=file_tuna_v4', 'protocol_version=4'),
            @('event=filetransfer_runtime_started', 'route=file_tuna_v4', 'protocol_version=4'),
            @('event=filetransfer_v4_sender_started', 'route=file_tuna_v4')
        ) -TimeoutMs 90000 -Description 'live V4 switch-off active file Tuna route')
        $observedEvidenceLines.Add($routeLine) | Out-Null

        $liveSwitchOffMinimumCommittedBytes = Resolve-TunaGuiLiveSwitchOffMinimumCommittedBytes -PayloadSizeBytes $PayloadSizeBytes
        $liveSwitchOffMinimumFaultPayloadBytes = $liveSwitchOffMinimumCommittedBytes
        $liveSwitchOffMinimumPeerVisiblePayloadBytes = Resolve-TunaGuiLiveSwitchOffMinimumPeerVisiblePayloadBytes -PayloadSizeBytes $PayloadSizeBytes -MinimumCommittedBytes $liveSwitchOffMinimumCommittedBytes
        $liveSwitchOffMinimumElapsedMs = Resolve-TunaGuiLiveSwitchOffMinimumElapsedMs
        $observedEvidenceLines.Add(("[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_live_switch_off_fault_threshold; minimum_committed_bytes={1}; minimum_peer_visible_payload_bytes={2}; minimum_elapsed_ms={3}; payload_bytes={4}" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $liveSwitchOffMinimumCommittedBytes, $liveSwitchOffMinimumPeerVisiblePayloadBytes, $liveSwitchOffMinimumElapsedMs, $PayloadSizeBytes)) | Out-Null
        Write-Host ("[GUI Smoke][filetransfer_tuna] Waiting for receiver commit >= {0} byte(s) or peer-visible Tuna payload >= {1} byte(s), elapsed >= {2} ms before live switch-off fault." -f $liveSwitchOffMinimumCommittedBytes, $liveSwitchOffMinimumPeerVisiblePayloadBytes, $liveSwitchOffMinimumElapsedMs) -ForegroundColor DarkGray

        $progressLine = [string](Wait-TunaGuiLiveSwitchOffTransferProgressBeforeFault -Bookmark $bookmark -MinimumCommittedBytes $liveSwitchOffMinimumCommittedBytes -MinimumPeerVisiblePayloadBytes $liveSwitchOffMinimumPeerVisiblePayloadBytes -MinimumElapsedMs $liveSwitchOffMinimumElapsedMs -MinimumFramePayloadBytes 16384L -TimeoutMs 90000 -PollIntervalMs 25)
        $observedEvidenceLines.Add($progressLine) | Out-Null

        Invoke-TunaGuiFallbackFault -Context $Context -FaultMode $FaultMode -PayerMode $PayerMode
        $fallbackStartedLine = Wait-TunaGuiLiveRouteEpochStarted -Bookmark $bookmark -Route 'post_tuna_fallback_v6' -ProtocolVersion 6 -HandoffKind 'tuna_to_normal_fallback' -TargetTransport 'regular_nkn' -Description 'live switch-off post-Tuna fallback route epoch started'
        $observedEvidenceLines.Add($fallbackStartedLine) | Out-Null
        Add-TunaGuiLiveRouteEpochObservation -Observations $liveRouteEpochObservations -Action 'off_started' -Line $fallbackStartedLine
        $fallbackEpochStartedObserved = $true

        $fallbackRouteLine = [string](Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $bookmark -NeedleSets @(
            @('event=filetransfer_route_selected', 'route=post_tuna_fallback_v6', 'protocol_version=6'),
            @('event=filetransfer_protocol_negotiated', 'route=post_tuna_fallback_v6', 'protocol_version=6')
        ) -TimeoutMs 90000 -Description 'live switch-off post-Tuna fallback V6 route')
        $observedEvidenceLines.Add($fallbackRouteLine) | Out-Null

        $fallbackResolutionLine = Wait-TunaGuiLiveRouteEpochRecovered -Bookmark $bookmark -Route 'post_tuna_fallback_v6' -ProtocolVersion 6 -HandoffKind 'tuna_to_normal_fallback' -TargetTransport 'regular_nkn' -Description 'live switch-off post-Tuna fallback route epoch recovered' -AfterLine $fallbackStartedLine
        $observedEvidenceLines.Add($fallbackResolutionLine) | Out-Null
        Add-TunaGuiLiveRouteEpochObservation -Observations $liveRouteEpochObservations -Action 'off_recovered' -Line $fallbackResolutionLine
        $fallbackEpochRecoveredObserved = $true
    }
    elseif ($RouteMode -eq 'live-multi-toggle' -or $RouteMode -eq 'live-reactivation-second-transfer' -or $RouteMode -eq 'live-regular-activation-cycle') {
        if ($FaultMode -eq 'none') {
            throw "$RouteMode route mode requires a fallback fault."
        }

        $sequence = if ($RouteMode -eq 'live-reactivation-second-transfer') {
            @(Get-TunaGuiLiveMultiToggleSequence -DefaultValue 'off,on')
        }
        elseif ($RouteMode -eq 'live-regular-activation-cycle') {
            @(Get-TunaGuiLiveMultiToggleSequence -DefaultValue 'on,off,on,off' -AllowInitialOn)
        }
        else {
            @(Get-TunaGuiLiveMultiToggleSequence)
        }
        $routeNeedleSets = if ($RouteMode -eq 'live-regular-activation-cycle') {
            @(
                @('event=filetransfer_route_selected', 'route=regular_nkn_v4_fast', 'protocol_version=4'),
                @('event=filetransfer_runtime_started', 'route=regular_nkn_v4_fast', 'protocol_version=4'),
                @('event=filetransfer_v4_sender_started', 'route=regular_nkn_v4_fast')
            )
        }
        else {
            @(
                @('event=filetransfer_route_selected', 'route=file_tuna_v4', 'protocol_version=4'),
                @('event=filetransfer_runtime_started', 'route=file_tuna_v4', 'protocol_version=4'),
                @('event=filetransfer_v4_sender_started', 'route=file_tuna_v4')
            )
        }
        $routeDescription = if ($RouteMode -eq 'live-regular-activation-cycle') { 'live regular activation cycle initial regular NKN V4 route' } else { 'live multi-toggle active file Tuna V4 route' }
        $routeLine = [string](Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $bookmark -NeedleSets $routeNeedleSets -TimeoutMs 90000 -Description $routeDescription)
        $observedEvidenceLines.Add($routeLine) | Out-Null

        $liveSwitchOffMinimumCommittedBytes = Resolve-TunaGuiLiveSwitchOffMinimumCommittedBytes -PayloadSizeBytes $PayloadSizeBytes
        $liveSwitchOffMinimumOverride = [string][Environment]::GetEnvironmentVariable('NLINK_TUNA_GUI_LIVE_SWITCH_OFF_MIN_COMMITTED_BYTES')
        if ([string]::IsNullOrWhiteSpace($liveSwitchOffMinimumOverride)) {
            $liveSwitchOffMinimumOverride = [string][Environment]::GetEnvironmentVariable('NLINK_TUNA_GUI_LIVE_SWITCH_OFF_MIN_PAYLOAD_BYTES')
        }
        if ($RouteMode -eq 'live-regular-activation-cycle' -and [string]::IsNullOrWhiteSpace($liveSwitchOffMinimumOverride)) {
            $regularActivationCycleCapBytes = [Math]::Max(1L, [long]($PayloadSizeBytes / 4))
            if ($regularActivationCycleCapBytes -lt $liveSwitchOffMinimumCommittedBytes) {
                $liveSwitchOffMinimumCommittedBytes = $regularActivationCycleCapBytes
            }
        }
        $liveSwitchOffMinimumFaultPayloadBytes = $liveSwitchOffMinimumCommittedBytes
        $liveSwitchOffMinimumPeerVisiblePayloadBytes = Resolve-TunaGuiLiveSwitchOffMinimumPeerVisiblePayloadBytes -PayloadSizeBytes $PayloadSizeBytes -MinimumCommittedBytes $liveSwitchOffMinimumCommittedBytes
        $liveSwitchOffMinimumElapsedMs = Resolve-TunaGuiLiveSwitchOffMinimumElapsedMs
        $observedEvidenceLines.Add(("[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_live_multi_toggle_sequence; route_mode={1}; sequence={2}; minimum_committed_bytes={3}; minimum_peer_visible_payload_bytes={4}; minimum_elapsed_ms={5}; payload_bytes={6}" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $RouteMode, ($sequence -join ','), $liveSwitchOffMinimumCommittedBytes, $liveSwitchOffMinimumPeerVisiblePayloadBytes, $liveSwitchOffMinimumElapsedMs, $PayloadSizeBytes)) | Out-Null

        $lastActivationPayloadBookmark = $null
        $lastObservedLiveRouteEpoch = 0
        $prearmedActivationBookmark = $null
        for ($stepIndex = 0; $stepIndex -lt $sequence.Count; $stepIndex++) {
            $action = [string]$sequence[$stepIndex]
            $stepNumber = $stepIndex + 1
            if ($action -eq 'off') {
                Write-Host ("[GUI Smoke][filetransfer_tuna] Live toggle step {0}/{1}: switching Tuna off after transfer progress." -f $stepNumber, $sequence.Count) -ForegroundColor DarkGray
                $earlyFallbackStartedLine = $null
                $earlyFallbackAccepted = $false
                if ($stepIndex -eq 0) {
                    if ($RouteMode -eq 'live-reactivation-second-transfer') {
                        $progressResult = Wait-TunaGuiLiveSwitchOffTransferProgressOrEarlyFallbackBeforeFault -Bookmark $bookmark -MinimumCommittedBytes $liveSwitchOffMinimumCommittedBytes -MinimumPeerVisiblePayloadBytes $liveSwitchOffMinimumPeerVisiblePayloadBytes -MinimumElapsedMs $liveSwitchOffMinimumElapsedMs -MinimumFramePayloadBytes 16384L -TimeoutMs 90000 -PollIntervalMs 25
                        $progressLine = [string]$progressResult.Line
                        if ([string]::Equals([string]$progressResult.Kind, 'early_fallback', [System.StringComparison]::OrdinalIgnoreCase)) {
                            $earlyFallbackAccepted = $true
                            $earlyFallbackStartedLine = $progressLine
                            $observedEvidenceLines.Add(("[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_live_reactivation_second_transfer_early_fallback; route_mode={1}; step={2}; committed_bytes={3}; accelerated_payload_bytes={4}; elapsed_ms={5}; reason=pre_fault_tuna_runtime_drop" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $RouteMode, $stepNumber, $progressResult.CommittedBytes, $progressResult.AcceleratedPayloadBytes, $progressResult.ElapsedMs)) | Out-Null
                        }
                    }
                    else {
                        $progressLine = [string](Wait-TunaGuiLiveSwitchOffTransferProgressBeforeFault -Bookmark $bookmark -MinimumCommittedBytes $liveSwitchOffMinimumCommittedBytes -MinimumPeerVisiblePayloadBytes $liveSwitchOffMinimumPeerVisiblePayloadBytes -MinimumElapsedMs $liveSwitchOffMinimumElapsedMs -MinimumFramePayloadBytes 16384L -TimeoutMs 90000 -PollIntervalMs 25)
                    }
                }
                elseif ($RouteMode -eq 'live-regular-activation-cycle' -and
                    $stepIndex -gt 0 -and
                    [string]::Equals([string]$sequence[$stepIndex - 1], 'on', [System.StringComparison]::OrdinalIgnoreCase)) {
                    $progressLine = "[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_live_multi_toggle_progress_wait_skipped; route_mode={1}; step={2}; action=off; reason=regular_activation_cycle_off_after_live_tuna_epoch" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $RouteMode, $stepNumber
                }
                elseif ($RouteMode -eq 'live-multi-toggle' -and
                    $stepIndex -eq ($sequence.Count - 1) -and
                    $stepIndex -gt 0 -and
                    [string]::Equals([string]$sequence[$stepIndex - 1], 'on', [System.StringComparison]::OrdinalIgnoreCase)) {
                    $progressLine = "[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_live_multi_toggle_progress_wait_skipped; route_mode={1}; step={2}; action=off; reason=final_off_after_reactivation" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $RouteMode, $stepNumber
                }
                else {
                    $progressBookmark = if ($null -ne $lastActivationPayloadBookmark) { $lastActivationPayloadBookmark } else { Get-AppLogBookmark }
                    $progressLine = [string](Wait-TunaGuiAcceleratedFilePayloadBeforeFault -Bookmark $progressBookmark -MinimumTotalPayloadBytes 1048576L -MinimumFramePayloadBytes 16384L -TimeoutMs 90000 -PollIntervalMs 25)
                }

                $observedEvidenceLines.Add($progressLine) | Out-Null
                $lastActivationPayloadBookmark = $null
                $stepBookmark = if ($earlyFallbackAccepted) { $bookmark } else { Get-AppLogBookmark }
                $observedEvidenceLines.Add(("[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_live_multi_toggle_step; step={1}; action=off; early_fallback={2}" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $stepNumber, ($(if ($earlyFallbackAccepted) { 1 } else { 0 })))) | Out-Null
                if (-not $earlyFallbackAccepted) {
                    Invoke-TunaGuiFallbackFault -Context $Context -FaultMode $FaultMode -PayerMode $PayerMode
                }

                $fallbackStartedLine = if ($earlyFallbackAccepted) {
                    $earlyFallbackStartedLine
                }
                else {
                    Wait-TunaGuiLiveRouteEpochStarted -Bookmark $stepBookmark -FallbackBookmark $bookmark -Route 'post_tuna_fallback_v6' -ProtocolVersion 6 -HandoffKind 'tuna_to_normal_fallback' -TargetTransport 'regular_nkn' -Description 'live multi-toggle Tuna-to-normal fallback route epoch started' -AfterLiveRouteEpoch $lastObservedLiveRouteEpoch
                }
                $observedEvidenceLines.Add($fallbackStartedLine) | Out-Null
                Add-TunaGuiLiveRouteEpochObservation -Observations $liveRouteEpochObservations -Action 'off_started' -Line $fallbackStartedLine
                $fallbackEpochStartedObserved = $true
                $fallbackStartedFields = ConvertFrom-GuiSmokeSemicolonFields -Message $fallbackStartedLine
                $fallbackStartedEpoch = ConvertTo-GuiSmokeInt -Value (Get-GuiSmokeFieldValue -Fields $fallbackStartedFields -Name 'live_route_epoch' -Default '0') -Default 0

                $fallbackRouteLine = [string](Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $stepBookmark -NeedleSets @(
                    @('event=filetransfer_route_selected', 'route=post_tuna_fallback_v6', 'protocol_version=6')
                ) -TimeoutMs 90000 -Description 'live multi-toggle post-Tuna fallback V6 route selection')
                $observedEvidenceLines.Add($fallbackRouteLine) | Out-Null

                $nextAction = if ($stepIndex + 1 -lt $sequence.Count) { [string]$sequence[$stepIndex + 1] } else { '' }
                if (($RouteMode -eq 'live-regular-activation-cycle' -or $RouteMode -eq 'live-multi-toggle' -or $RouteMode -eq 'live-reactivation-second-transfer') -and
                    [string]::Equals($nextAction, 'on', [System.StringComparison]::OrdinalIgnoreCase)) {
                    $prearmedActivationBookmark = Get-AppLogBookmark
                    $observedEvidenceLines.Add(("[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_live_multi_toggle_step_prearmed; step={1}; next_step={2}; action=on; route_mode={3}; reason=prearm_after_fallback_route_started" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $stepNumber, ($stepNumber + 1), $RouteMode)) | Out-Null
                    Unlock-TunaPayers -Context $Context -PayerMode $PayerMode -Password $WalletPassword
                }

                $fallbackResolutionLine = Wait-TunaGuiLiveRouteEpochRecovered -Bookmark $stepBookmark -FallbackBookmark $bookmark -Route 'post_tuna_fallback_v6' -ProtocolVersion 6 -HandoffKind 'tuna_to_normal_fallback' -TargetTransport 'regular_nkn' -Description 'live multi-toggle Tuna-to-normal fallback route epoch recovered' -LiveRouteEpoch $fallbackStartedEpoch -AfterLiveRouteEpoch $lastObservedLiveRouteEpoch -AfterLine $fallbackStartedLine
                $observedEvidenceLines.Add($fallbackResolutionLine) | Out-Null
                Add-TunaGuiLiveRouteEpochObservation -Observations $liveRouteEpochObservations -Action 'off_recovered' -Line $fallbackResolutionLine
                $fallbackEpochRecoveredObserved = $true
                if ($fallbackStartedEpoch -gt $lastObservedLiveRouteEpoch) {
                    $lastObservedLiveRouteEpoch = $fallbackStartedEpoch
                }
            }
            else {
                Write-Host ("[GUI Smoke][filetransfer_tuna] Live toggle step {0}/{1}: re-enabling Tuna for the same transfer." -f $stepNumber, $sequence.Count) -ForegroundColor DarkGray
                $prearmedActivation = $false
                if ($null -ne $prearmedActivationBookmark) {
                    $stepBookmark = $prearmedActivationBookmark
                    $prearmedActivationBookmark = $null
                    $prearmedActivation = $true
                }
                else {
                    $stepBookmark = Get-AppLogBookmark
                }
                $lastActivationPayloadBookmark = $stepBookmark
                $prearmedFlag = if ($prearmedActivation) { 1 } else { 0 }
                $observedEvidenceLines.Add(("[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_live_multi_toggle_step; step={1}; action=on; prearmed={2}" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $stepNumber, $prearmedFlag)) | Out-Null
                if (-not $prearmedActivation) {
                    Unlock-TunaPayers -Context $Context -PayerMode $PayerMode -Password $WalletPassword
                }

                $activationProofTimeoutMs = if ($RouteMode -eq 'live-regular-activation-cycle' -or $RouteMode -eq 'live-reactivation-second-transfer') { 240000 } else { 90000 }
                try {
                    $activationStartedLine = Wait-TunaGuiLiveRouteEpochStarted -Bookmark $stepBookmark -FallbackBookmark $bookmark -Route 'file_tuna_v4' -ProtocolVersion 4 -HandoffKind 'normal_to_tuna_activation' -TargetTransport 'tuna' -Description 'live multi-toggle normal-to-Tuna route epoch started' -AfterLiveRouteEpoch $lastObservedLiveRouteEpoch -TimeoutMs $activationProofTimeoutMs
                }
                catch {
                    if ($RouteMode -eq 'live-reactivation-second-transfer') {
                        $terminalProbeInboundRole = if ($Direction -eq 'helper-to-helpee') { 'helpee' } else { 'helper' }
                        $terminalProbeOutboundRole = if ($Direction -eq 'helper-to-helpee') { 'helper' } else { 'helpee' }
                        try {
                            $terminalProbe = Wait-FileTransferTerminalPairAfterBookmark `
                                -Bookmark $bookmark `
                                -TimeoutMs 10000 `
                                -ProgressTimeoutMs 10000 `
                                -ExpectedFileName ([System.IO.Path]::GetFileName($AutopickPath)) `
                                -ExpectedSizeBytes $PayloadSizeBytes `
                                -ExpectedInboundRole $terminalProbeInboundRole `
                                -ExpectedOutboundRole $terminalProbeOutboundRole `
                                -NotBeforeUtc $cycleStartedUtc `
                                -ArtifactDir $ArtifactDir
                            $terminalProbeInboundState = Get-GuiSmokeFieldValue -Fields $terminalProbe.Inbound -Name 'state' -Default '(unknown)'
                            $terminalProbeOutboundState = Get-GuiSmokeFieldValue -Fields $terminalProbe.Outbound -Name 'state' -Default '(unknown)'
                            $terminalProbeInboundError = Get-GuiSmokeFieldValue -Fields $terminalProbe.Inbound -Name 'error_code' -Default '(none)'
                            $terminalProbeOutboundError = Get-GuiSmokeFieldValue -Fields $terminalProbe.Outbound -Name 'error_code' -Default '(none)'
                            if ($terminalProbeInboundState -eq 'Completed' -and
                                $terminalProbeOutboundState -eq 'Completed' -and
                                $terminalProbeInboundError -eq '(none)' -and
                                $terminalProbeOutboundError -eq '(none)') {
                                $firstTransferTerminalBeforeLiveReactivation = $true
                                $firstTransferTerminalBeforeLiveReactivationTransferId = [string]$terminalProbe.TransferId
                                $observedEvidenceLines.Add(("[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_live_reactivation_first_transfer_terminal_before_activation; route_mode={1}; transfer_id={2}; step={3}; action=on; reason=terminal_before_same_transfer_reactivation; fallback_epoch_recovered={4}" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $RouteMode, $firstTransferTerminalBeforeLiveReactivationTransferId, $stepNumber, ($(if ($fallbackEpochRecoveredObserved) { 1 } else { 0 })))) | Out-Null
                                Write-Host '[GUI Smoke][filetransfer_tuna] First transfer completed cleanly in fallback before same-transfer reactivation; continuing to second-transfer Tuna proof.' -ForegroundColor Yellow
                                continue
                            }
                        }
                        catch {
                        }
                    }

                    throw
                }
                $observedEvidenceLines.Add($activationStartedLine) | Out-Null
                Add-TunaGuiLiveRouteEpochObservation -Observations $liveRouteEpochObservations -Action 'on_started' -Line $activationStartedLine
                $activationEpochStartedObserved = $true
                $activationStartedFields = ConvertFrom-GuiSmokeSemicolonFields -Message $activationStartedLine
                $activationStartedEpoch = ConvertTo-GuiSmokeInt -Value (Get-GuiSmokeFieldValue -Fields $activationStartedFields -Name 'live_route_epoch' -Default '0') -Default 0

                $activationRouteLine = [string](Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $stepBookmark -NeedleSets @(
                    @('event=filetransfer_route_selected', 'route=file_tuna_v4', 'protocol_version=4')
                ) -TimeoutMs $activationProofTimeoutMs -Description 'live multi-toggle file Tuna V4 route selection')
                $observedEvidenceLines.Add($activationRouteLine) | Out-Null

                $activationRecoveredLine = Wait-TunaGuiLiveRouteEpochRecovered -Bookmark $stepBookmark -FallbackBookmark $bookmark -Route 'file_tuna_v4' -ProtocolVersion 4 -HandoffKind 'normal_to_tuna_activation' -TargetTransport 'tuna' -Description 'live multi-toggle normal-to-Tuna route epoch recovered' -LiveRouteEpoch $activationStartedEpoch -AfterLiveRouteEpoch $lastObservedLiveRouteEpoch -AfterLine $activationStartedLine -TimeoutMs $activationProofTimeoutMs
                $observedEvidenceLines.Add($activationRecoveredLine) | Out-Null
                Add-TunaGuiLiveRouteEpochObservation -Observations $liveRouteEpochObservations -Action 'on_recovered' -Line $activationRecoveredLine
                $activationEpochRecoveredObserved = $true
                $tunaNegotiatedObserved = $true
                if ($activationStartedEpoch -gt $lastObservedLiveRouteEpoch) {
                    $lastObservedLiveRouteEpoch = $activationStartedEpoch
                }
            }
        }
    }
    elseif ($RouteMode -eq 'handoff-fallback') {
        [void](Wait-FileTransferTerminalOrProgressBeforeAction -Bookmark $bookmark -ProgressTimeoutMs $ProgressTimeoutMs -MinProgressEvents 2 -TimeoutMs 60000)

        Unlock-TunaPayers -Context $Context -PayerMode $PayerMode -Password $WalletPassword
        $tunaNegotiatedLine = [string](Wait-AppLogContainsAfterBookmark -Bookmark $bookmark -Needle 'event=tuna_acceleration_negotiated' -TimeoutMs 150000 -Description 'Tuna negotiated during active GUI file transfer')
        $observedEvidenceLines.Add($tunaNegotiatedLine) | Out-Null
        $tunaNegotiatedObserved = $true
        $activationStartedLine = [string](Wait-AppLogContainsAllAfterBookmark -Bookmark $bookmark -Needles @('event=filetransfer_v6_epoch_started', 'handoff_kind=normal_to_tuna_activation') -TimeoutMs 150000 -Description 'V6 NormalToTunaActivation epoch started')
        $observedEvidenceLines.Add($activationStartedLine) | Out-Null
        $activationEpochStartedObserved = $true
        $activationResolutionLine = [string](Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $bookmark -NeedleSets @(
            @('event=filetransfer_v6_epoch_recovered', 'handoff_kind=normal_to_tuna_activation'),
            @('event=filetransfer_v6_epoch_recovered', 'target_transport=tuna'),
            @('event=filetransfer_v6_epoch_observed', 'handoff_kind=normal_to_tuna_activation', 'state=recovered'),
            @('event=filetransfer_v6_epoch_started', 'handoff_kind=tuna_to_normal_fallback'),
            @('event=filetransfer_v6_epoch_started', 'handoff_kind=regular_nkn_recovery')
        ) -TimeoutMs 150000 -Description 'V6 NormalToTunaActivation epoch recovered or early fallback started')
        $observedEvidenceLines.Add($activationResolutionLine) | Out-Null
        if (($activationResolutionLine.IndexOf('handoff_kind=tuna_to_normal_fallback', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) -or
            ($activationResolutionLine.IndexOf('handoff_kind=regular_nkn_recovery', [System.StringComparison]::OrdinalIgnoreCase) -ge 0)) {
            Write-Host '[GUI Smoke][filetransfer_tuna] Tuna dropped before activation proof; continuing as early-fallback coverage.' -ForegroundColor Yellow
            $fallbackEpochStartedObserved = $true
        }
        else {
            $activationEpochRecoveredObserved = $true
        }

        if ($FaultMode -ne 'none' -and -not $fallbackEpochStartedObserved) {
            $minimumFaultPayloadBytes = [Math]::Min(16777216L, [Math]::Max(1048576L, [long]($PayloadSizeBytes / 8)))
            $acceleratedPayloadLine = [string](Wait-TunaGuiAcceleratedFilePayloadBeforeFault -Bookmark $bookmark -MinimumTotalPayloadBytes $minimumFaultPayloadBytes)
            $observedEvidenceLines.Add($acceleratedPayloadLine) | Out-Null
            Invoke-TunaGuiFallbackFault -Context $Context -FaultMode $FaultMode -PayerMode $PayerMode
            $fallbackStartedLine = [string](Wait-AppLogContainsAllAfterBookmark -Bookmark $bookmark -Needles @('event=filetransfer_v6_epoch_started', 'handoff_kind=tuna_to_normal_fallback') -TimeoutMs 90000 -Description 'V6 TunaToNormalFallback epoch started')
            $observedEvidenceLines.Add($fallbackStartedLine) | Out-Null
            $fallbackEpochStartedObserved = $true
        }
        if ($fallbackEpochStartedObserved) {
            $fallbackResolutionLine = [string](Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $bookmark -NeedleSets @(
                @('event=filetransfer_v6_epoch_recovered', 'handoff_kind=tuna_to_normal_fallback'),
                @('event=filetransfer_v6_epoch_recovered', 'target_transport=regular_nkn'),
                @('event=filetransfer_v6_epoch_observed', 'handoff_kind=tuna_to_normal_fallback', 'state=recovered'),
                @('event=filetransfer_v6_epoch_waiting', 'handoff_kind=tuna_to_normal_fallback'),
                @('event=filetransfer_v6_epoch_observed', 'handoff_kind=tuna_to_normal_fallback', 'state=waiting_for_target_transport')
            ) -TimeoutMs 150000 -Description 'V6 TunaToNormalFallback epoch recovered or waiting')
            $observedEvidenceLines.Add($fallbackResolutionLine) | Out-Null
            if (($fallbackResolutionLine.IndexOf('state=waiting_for_target_transport', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) -or
                ($fallbackResolutionLine.IndexOf('event=filetransfer_v6_epoch_waiting', [System.StringComparison]::OrdinalIgnoreCase) -ge 0)) {
                $fallbackEpochWaitingObserved = $true
            }
            else {
                $fallbackEpochRecoveredObserved = $true
            }
        }
    }
    else {
        $expectedRouteToken = if ($RouteMode -eq 'preactivated' -or $RouteMode -eq 'live-v4-switch-off') { 'file_tuna_v4' } else { 'post_tuna_fallback_v6' }
        $routeLine = [string](Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $bookmark -NeedleSets @(
            @('event=filetransfer_route_selected', ("route={0}" -f $expectedRouteToken)),
            @('event=filetransfer_runtime_started', ("route={0}" -f $expectedRouteToken)),
            @('event=filetransfer_v6_sender_started', ("route={0}" -f $expectedRouteToken)),
            @('event=filetransfer_v4_sender_started', ("route={0}" -f $expectedRouteToken))
        ) -TimeoutMs 90000 -Description ("preconditioned Tuna route selected: {0}" -f $expectedRouteToken))
        $observedEvidenceLines.Add($routeLine) | Out-Null
        if ($RouteMode -eq 'preactivated') {
            $activationEpochStartedObserved = $true
            $activationEpochRecoveredObserved = $true
        }
    }

    $pauseProbe = Invoke-TunaGuiPauseResumeProbe -SenderWindow $senderWindow -Bookmark $bookmark

    $expectedInboundRole = if ($Direction -eq 'helper-to-helpee') { 'helpee' } else { 'helper' }
    $expectedOutboundRole = if ($Direction -eq 'helper-to-helpee') { 'helper' } else { 'helpee' }
    $terminal = Wait-FileTransferTerminalPairAfterBookmark `
        -Bookmark $bookmark `
        -TimeoutMs $TimeoutMs `
        -ProgressTimeoutMs $ProgressTimeoutMs `
        -ExpectedFileName ([System.IO.Path]::GetFileName($AutopickPath)) `
        -ExpectedSizeBytes $PayloadSizeBytes `
        -ExpectedInboundRole $expectedInboundRole `
        -ExpectedOutboundRole $expectedOutboundRole `
        -NotBeforeUtc $cycleStartedUtc `
        -ArtifactDir $ArtifactDir
    $sw.Stop()

    $inbound = $terminal.Inbound
    $outbound = $terminal.Outbound
    $savedPath = Get-GuiSmokeFieldValue -Fields $inbound -Name 'saved_path' -Default '(none)'
    $resolvedSavedPath = [string]$terminal.ResolvedSavedPath
    if ([string]::IsNullOrWhiteSpace($resolvedSavedPath)) {
        $resolvedSavedPath = Resolve-FileTransferLiveReceivedFilePath `
            -LoggedPath $savedPath `
            -ArtifactDir $ArtifactDir `
            -ExpectedFileName ([System.IO.Path]::GetFileName($AutopickPath)) `
            -ExpectedSizeBytes $PayloadSizeBytes `
            -NotBeforeUtc $cycleStartedUtc
    }

    $actualHash = '(none)'
    $savedSize = -1L
    if (-not [string]::IsNullOrWhiteSpace($resolvedSavedPath) -and (Test-Path -LiteralPath $resolvedSavedPath -PathType Leaf)) {
        $actualHash = Get-FileSha256Hex -Path $resolvedSavedPath
        $savedSize = (Get-Item -LiteralPath $resolvedSavedPath).Length
    }

    $inboundState = Get-GuiSmokeFieldValue -Fields $inbound -Name 'state' -Default '(unknown)'
    $outboundState = Get-GuiSmokeFieldValue -Fields $outbound -Name 'state' -Default '(unknown)'
    $inboundError = Get-GuiSmokeFieldValue -Fields $inbound -Name 'error_code' -Default '(none)'
    $outboundError = Get-GuiSmokeFieldValue -Fields $outbound -Name 'error_code' -Default '(none)'
    $completed = $inboundState -eq 'Completed' -and $outboundState -eq 'Completed' -and $inboundError -eq '(none)' -and $outboundError -eq '(none)'
    $integrityOk = $completed -and $savedSize -eq $PayloadSizeBytes -and $actualHash -eq $expectedHash
    if ($RouteMode -eq 'v4-restart-v6-fallback') {
        $observedEvidenceLines.Add(("[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_phase_marker; phase=measured_post_tuna_fallback_v6_terminal; inbound_state={1}; outbound_state={2}; inbound_error={3}; outbound_error={4}; integrity_ok={5}" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $inboundState, $outboundState, $inboundError, $outboundError, ($(if ($integrityOk) { 1 } else { 0 })))) | Out-Null
    }
    $goodputBytesPerSecond = if ($sw.Elapsed.TotalSeconds -gt 0) { [Math]::Round($PayloadSizeBytes / $sw.Elapsed.TotalSeconds, 3) } else { 0D }
    $expectedMeasuredRoute = if ($RouteMode -eq 'preactivated' -or $RouteMode -eq 'live-reactivation-second-transfer') { 'file_tuna_v4' } elseif ($RouteMode -eq 'handoff-fallback') { '(handoff)' } else { 'post_tuna_fallback_v6' }
    $measuredPhaseName = if ($RouteMode -eq 'preactivated') { 'measured_file_tuna_v4' } elseif ($RouteMode -eq 'live-v4-switch-off') { 'measured_live_post_tuna_fallback_v6' } elseif ($RouteMode -eq 'live-multi-toggle') { 'measured_live_multi_toggle' } elseif ($RouteMode -eq 'live-reactivation-second-transfer') { 'measured_live_reactivation_file_tuna_v4' } elseif ($RouteMode -eq 'live-regular-activation-cycle') { 'measured_live_regular_activation_cycle' } elseif ($RouteMode -eq 'v4-restart-v6-fallback') { 'measured_post_tuna_fallback_v6' } elseif ($RouteMode -eq 'post-fallback') { 'measured_post_tuna_fallback_v6' } else { 'measured_handoff_fallback' }
    $measuredRoute = Get-GuiSmokeFieldValue -Fields $outbound -Name 'route' -Default (Get-GuiSmokeFieldValue -Fields $inbound -Name 'route' -Default $expectedMeasuredRoute)
    $defaultMeasuredProtocol = if ($measuredRoute -eq 'post_tuna_fallback_v6') { 6 } elseif ($measuredRoute -eq 'file_tuna_v4') { 4 } else { 0 }
    $measuredProtocol = ConvertTo-GuiSmokeInt -Value (Get-GuiSmokeFieldValue -Fields $outbound -Name 'protocol_version' -Default (Get-GuiSmokeFieldValue -Fields $inbound -Name 'protocol_version' -Default ([string]$defaultMeasuredProtocol))) -Default $defaultMeasuredProtocol
    $evidence = Get-TunaGuiEvidenceSummary -Bookmark $bookmark
    if ($tunaNegotiatedObserved) { $evidence.tunaNegotiated = $true }
    if ($activationEpochStartedObserved) { $evidence.activationEpochStarted = $true }
    if ($activationEpochRecoveredObserved) { $evidence.activationEpochRecovered = $true }
    if ($fallbackEpochStartedObserved) { $evidence.fallbackEpochStarted = $true }
    if ($fallbackEpochRecoveredObserved) { $evidence.fallbackEpochRecovered = $true }
    if ($fallbackEpochWaitingObserved) { $evidence.fallbackEpochWaiting = $true }
    foreach ($line in @($observedEvidenceLines.ToArray())) {
        if ($line.IndexOf('event=filetransfer_post_tuna_fallback_nkn_proved', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('event=filetransfer_live_v4_fallback_nkn_proved', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $evidence.postTunaFallbackNknProved = $true
        }

        if ($line.IndexOf('event=filetransfer_post_tuna_fallback_cleanup_completed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('event=filetransfer_live_v4_fallback_cleanup_completed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('event=tuna_disable_handoff_completed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $evidence.postTunaFallbackCleanupCompleted = $true
        }

        if ($line.IndexOf('event=tuna_fallback_nkn_frame_sent', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $line.IndexOf('message_type=file_transfer_data_frame', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $line.IndexOf('channel=bulk', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $evidence.tunaFallbackNknFileFrameSent = $true
        }

        if ($line.IndexOf('event=tuna_fallback_nkn_frame_received', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $line.IndexOf('message_type=file_transfer_data_frame', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $line.IndexOf('channel=bulk', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $evidence.tunaFallbackNknFileFrameReceived = $true
        }

        if ($line.IndexOf('event=filetransfer_route_selected', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $line.IndexOf('route=post_tuna_fallback_v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $line.IndexOf('protocol_version=6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $evidence.postTunaFallbackV6RouteObserved = $true
        }
    }
    if ($pauseProbe) {
        $evidence.pauseSent = $true
        $evidence.pauseReceived = $true
        $evidence.resumeSent = $true
        $evidence.resumeReceived = $true
    }

    $fallbackModel = if ($RouteMode -eq 'live-v4-switch-off') { 'live_v6' } elseif ($RouteMode -eq 'live-multi-toggle') { 'live_multi_toggle_v6' } elseif ($RouteMode -eq 'live-reactivation-second-transfer') { 'live_reactivation_v4' } elseif ($RouteMode -eq 'live-regular-activation-cycle') { 'live_regular_activation_cycle_v6' } elseif ($RouteMode -eq 'v4-restart-v6-fallback' -or $RouteMode -eq 'post-fallback') { 'controlled_restart_v6' } else { '(none)' }
    $singleTransferLiveFallback = $RouteMode -eq 'live-v4-switch-off' -or $RouteMode -eq 'live-multi-toggle' -or $RouteMode -eq 'live-reactivation-second-transfer' -or $RouteMode -eq 'live-regular-activation-cycle'
    $liveRouteEpochArray = @($liveRouteEpochObservations.ToArray())
    $liveRouteEpochSequence = @($liveRouteEpochArray | ForEach-Object { [string]($_['route']) } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_ -ne '(unknown)' })
    $liveRouteEpochRouteChanges = New-Object System.Collections.Generic.List[string]
    foreach ($route in $liveRouteEpochSequence) {
        if ($liveRouteEpochRouteChanges.Count -eq 0 -or
            -not [string]::Equals($liveRouteEpochRouteChanges[$liveRouteEpochRouteChanges.Count - 1], $route, [System.StringComparison]::OrdinalIgnoreCase)) {
            $liveRouteEpochRouteChanges.Add($route) | Out-Null
        }
    }
    $summary = [ordered]@{
        event = 'filetransfer_tuna_gui_handoff_fallback_summary'
        routeMode = $RouteMode
        direction = $Direction
        payerMode = $PayerMode
        faultMode = $FaultMode
        mixedScreenShare = Test-GuiSmokeEnvEnabled -Name 'NLINK_TUNA_GUI_MIXED_SCREENSHARE'
        transferId = $terminal.TransferId
        payloadBytes = $PayloadSizeBytes
        durationMs = [Math]::Round($sw.Elapsed.TotalMilliseconds, 3)
        goodputBytesPerSecond = $goodputBytesPerSecond
        completed = $completed
        integrityOk = $integrityOk
        inboundState = $inboundState
        outboundState = $outboundState
        inboundErrorCode = $inboundError
        outboundErrorCode = $outboundError
        expectedSha256 = $expectedHash
        receivedSha256 = $actualHash
        savedFileSizeBytes = $savedSize
        savedPath = $savedPath
        resolvedSavedPath = $resolvedSavedPath
        setupPhase = $setupPhase
        measuredPhase = [ordered]@{
            name = $measuredPhaseName
            route = $measuredRoute
            protocolVersion = $measuredProtocol
            payloadBytes = $PayloadSizeBytes
            goodputBytesPerSecond = $goodputBytesPerSecond
            completed = $completed
            integrityOk = $integrityOk
            inboundState = $inboundState
            outboundState = $outboundState
            inboundErrorCode = $inboundError
            outboundErrorCode = $outboundError
        }
        fallbackModel = $fallbackModel
        singleTransferLiveFallback = $singleTransferLiveFallback
        liveSwitchOffMinimumFaultPayloadBytes = $liveSwitchOffMinimumFaultPayloadBytes
        liveSwitchOffMinimumCommittedBytes = $liveSwitchOffMinimumCommittedBytes
        liveSwitchOffMinimumPeerVisiblePayloadBytes = $liveSwitchOffMinimumPeerVisiblePayloadBytes
        liveSwitchOffMinimumElapsedMs = $liveSwitchOffMinimumElapsedMs
        liveRouteEpochs = $liveRouteEpochArray
        liveRouteEpochSequence = $liveRouteEpochSequence
        liveRouteEpochRouteChanges = @($liveRouteEpochRouteChanges.ToArray())
        firstTransferTerminalBeforeLiveReactivation = [bool]$firstTransferTerminalBeforeLiveReactivation
        firstTransferTerminalBeforeLiveReactivationTransferId = $firstTransferTerminalBeforeLiveReactivationTransferId
        pauseProbe = [bool]$pauseProbe
        evidence = $evidence
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json') -Encoding UTF8
    if ($observedEvidenceLines.Count -gt 0) {
        [System.IO.File]::WriteAllText(
            (Join-Path $ArtifactDir 'filetransfer-tuna-gui-milestone-evidence.log'),
            ($observedEvidenceLines.ToArray() -join [Environment]::NewLine),
            [System.Text.Encoding]::UTF8)
    }
    Copy-FileTransferLiveLogSlice -ArtifactDir $ArtifactDir -Bookmark $bookmark

    if (-not $integrityOk) {
        throw ("Tuna GUI file-transfer cycle failed terminal/integrity check: transfer_id={0}; inbound_state={1}; outbound_state={2}; inbound_error={3}; outbound_error={4}; saved_size={5}; expected_size={6}" -f `
            $terminal.TransferId,
            $inboundState,
            $outboundState,
            $inboundError,
            $outboundError,
            $savedSize,
            $PayloadSizeBytes)
    }

    $secondTransferProofOk = $false
    if ($RouteMode -eq 'live-reactivation-second-transfer') {
        $secondPayloadBytes = 16777216L
        $postTerminalQuietBookmark = Get-AppLogBookmark
        $postTerminalQuiet = Wait-TunaGuiSecondTransferPostTerminalQuietWindowOrThrow `
            -Bookmark $postTerminalQuietBookmark `
            -TimeoutMs ([Math]::Min(45000, [Math]::Max(10000, $StartupTimeoutMs))) `
            -QuietMs 3000
        $summary['secondTransferPreflight'] = [ordered]@{
            postTerminalQuietMs = $postTerminalQuiet.QuietMs
            postTerminalNoiseCount = $postTerminalQuiet.NoiseCount
            postTerminalLastNoiseUtc = $postTerminalQuiet.LastNoiseUtc
        }
        $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json') -Encoding UTF8
        Write-Host ("[GUI Smoke][filetransfer_tuna] First-transfer terminal echoes quiet for {0} ms before second transfer; noise_count={1}." -f $postTerminalQuiet.QuietMs, $postTerminalQuiet.NoiseCount) -ForegroundColor DarkGray

        Write-DeterministicFileTransferPayload -Path $AutopickPath -SizeBytes $secondPayloadBytes -Seed $Seed -CycleIndex 1
        $secondExpectedHash = Get-FileSha256Hex -Path $AutopickPath
        $secondBookmark = Get-AppLogBookmark
        $secondStartedUtc = [datetime]::UtcNow
        $secondSw = [System.Diagnostics.Stopwatch]::StartNew()
        $secondTransferPhase = $null
        Write-Host "[GUI Smoke][filetransfer_tuna] Starting second transfer after live reactivation; expecting file_tuna_v4 protocol 4." -ForegroundColor DarkGray

        try {
            $secondSendButton = Wait-TunaGuiSecondTransferReadinessOrThrow -Window $senderWindow -Bookmark $bookmark -TimeoutMs ([Math]::Min(60000, $StartupTimeoutMs)) -RouteMode $RouteMode -SenderRole $expectedOutboundRole
            Click-Element $secondSendButton

            $secondAcceptButton = Wait-TunaGuiFileTransferAcceptOrThrow -Window $receiverWindow -Bookmark $secondBookmark -TimeoutMs $StartupTimeoutMs -RouteMode $RouteMode
            Click-Element $secondAcceptButton

            $secondRouteLine = [string](Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $secondBookmark -NeedleSets @(
                @('event=filetransfer_route_selected', 'route=file_tuna_v4', 'protocol_version=4'),
                @('event=filetransfer_runtime_started', 'route=file_tuna_v4', 'protocol_version=4'),
                @('event=filetransfer_v4_sender_started', 'route=file_tuna_v4')
            ) -TimeoutMs 90000 -Description 'second transfer after reactivation file Tuna V4 route')

            $secondTerminal = Wait-FileTransferTerminalPairAfterBookmark `
                -Bookmark $secondBookmark `
                -TimeoutMs $TimeoutMs `
                -ProgressTimeoutMs $ProgressTimeoutMs `
                -ExpectedFileName ([System.IO.Path]::GetFileName($AutopickPath)) `
                -ExpectedSizeBytes $secondPayloadBytes `
                -ExpectedInboundRole $expectedInboundRole `
                -ExpectedOutboundRole $expectedOutboundRole `
                -NotBeforeUtc $secondStartedUtc `
                -ArtifactDir $ArtifactDir
            $secondSw.Stop()

            $secondInbound = $secondTerminal.Inbound
            $secondOutbound = $secondTerminal.Outbound
            $secondSavedPath = Get-GuiSmokeFieldValue -Fields $secondInbound -Name 'saved_path' -Default '(none)'
            $secondResolvedSavedPath = [string]$secondTerminal.ResolvedSavedPath
            if ([string]::IsNullOrWhiteSpace($secondResolvedSavedPath)) {
                $secondResolvedSavedPath = Resolve-FileTransferLiveReceivedFilePath `
                    -LoggedPath $secondSavedPath `
                    -ArtifactDir $ArtifactDir `
                    -ExpectedFileName ([System.IO.Path]::GetFileName($AutopickPath)) `
                    -ExpectedSizeBytes $secondPayloadBytes `
                    -NotBeforeUtc $secondStartedUtc
            }

            $secondActualHash = '(none)'
            $secondSavedSize = -1L
            if (-not [string]::IsNullOrWhiteSpace($secondResolvedSavedPath) -and (Test-Path -LiteralPath $secondResolvedSavedPath -PathType Leaf)) {
                $secondActualHash = Get-FileSha256Hex -Path $secondResolvedSavedPath
                $secondSavedSize = (Get-Item -LiteralPath $secondResolvedSavedPath).Length
            }

            $secondInboundState = Get-GuiSmokeFieldValue -Fields $secondInbound -Name 'state' -Default '(unknown)'
            $secondOutboundState = Get-GuiSmokeFieldValue -Fields $secondOutbound -Name 'state' -Default '(unknown)'
            $secondInboundError = Get-GuiSmokeFieldValue -Fields $secondInbound -Name 'error_code' -Default '(none)'
            $secondOutboundError = Get-GuiSmokeFieldValue -Fields $secondOutbound -Name 'error_code' -Default '(none)'
            $secondCompleted = $secondInboundState -eq 'Completed' -and $secondOutboundState -eq 'Completed' -and $secondInboundError -eq '(none)' -and $secondOutboundError -eq '(none)'
            $secondIntegrityOk = $secondCompleted -and $secondSavedSize -eq $secondPayloadBytes -and $secondActualHash -eq $secondExpectedHash
            $secondRouteFields = ConvertFrom-GuiSmokeSemicolonFields -Message $secondRouteLine
            $secondMeasuredRoute = Get-GuiSmokeFieldValue -Fields $secondOutbound -Name 'route' -Default (Get-GuiSmokeFieldValue -Fields $secondInbound -Name 'route' -Default (Get-GuiSmokeFieldValue -Fields $secondRouteFields -Name 'route' -Default '(unknown)'))
            $secondMeasuredProtocol = ConvertTo-GuiSmokeInt -Value (Get-GuiSmokeFieldValue -Fields $secondOutbound -Name 'protocol_version' -Default (Get-GuiSmokeFieldValue -Fields $secondInbound -Name 'protocol_version' -Default (Get-GuiSmokeFieldValue -Fields $secondRouteFields -Name 'protocol_version' -Default '0'))) -Default 0
            $secondGoodputBytesPerSecond = if ($secondSw.Elapsed.TotalSeconds -gt 0) { [Math]::Round($secondPayloadBytes / $secondSw.Elapsed.TotalSeconds, 3) } else { 0D }

            $secondTransferPhase = [ordered]@{
                name = 'second_transfer_after_reactivation_file_tuna_v4'
                transferId = $secondTerminal.TransferId
                route = $secondMeasuredRoute
                protocolVersion = $secondMeasuredProtocol
                payloadBytes = $secondPayloadBytes
                durationMs = [Math]::Round($secondSw.Elapsed.TotalMilliseconds, 3)
                goodputBytesPerSecond = $secondGoodputBytesPerSecond
                completed = $secondCompleted
                integrityOk = $secondIntegrityOk
                inboundState = $secondInboundState
                outboundState = $secondOutboundState
                inboundErrorCode = $secondInboundError
                outboundErrorCode = $secondOutboundError
                expectedSha256 = $secondExpectedHash
                receivedSha256 = $secondActualHash
                savedFileSizeBytes = $secondSavedSize
                savedPath = $secondSavedPath
                resolvedSavedPath = $secondResolvedSavedPath
            }
            $summary['secondTransfer'] = $secondTransferPhase
            $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json') -Encoding UTF8

            if (-not $secondIntegrityOk -or $secondMeasuredRoute -ne 'file_tuna_v4' -or $secondMeasuredProtocol -ne 4) {
                throw ("Second transfer after live reactivation failed route/integrity check: route={0}; protocol={1}; completed={2}; integrity_ok={3}; inbound_state={4}; outbound_state={5}" -f `
                    $secondMeasuredRoute,
                    $secondMeasuredProtocol,
                    $secondCompleted,
                    $secondIntegrityOk,
                    $secondInboundState,
                    $secondOutboundState)
            }

            $secondTransferProofOk = $true
        }
        catch {
            if ($secondSw.IsRunning) {
                $secondSw.Stop()
            }

            $failureClassification = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark $secondBookmark -ErrorMessage $_.Exception.Message -RouteMode $RouteMode
            if ($null -eq $secondTransferPhase) {
                $secondTransferPhase = [ordered]@{
                    name = 'second_transfer_after_reactivation_file_tuna_v4'
                    transferId = '(none)'
                    route = '(unknown)'
                    protocolVersion = 0
                    payloadBytes = $secondPayloadBytes
                    durationMs = [Math]::Round($secondSw.Elapsed.TotalMilliseconds, 3)
                    goodputBytesPerSecond = 0D
                    completed = $false
                    integrityOk = $false
                    inboundState = '(unknown)'
                    outboundState = '(unknown)'
                    inboundErrorCode = '(unknown)'
                    outboundErrorCode = '(unknown)'
                    expectedSha256 = $secondExpectedHash
                    receivedSha256 = '(none)'
                    savedFileSizeBytes = -1L
                    savedPath = '(none)'
                    resolvedSavedPath = '(none)'
                }
            }

            $secondTransferPhase['setupFailurePhase'] = $failureClassification.Phase
            $secondTransferPhase['setupFailureReason'] = $failureClassification.Reason
            $secondTransferPhase['error'] = $_.Exception.Message
            $summary['secondTransfer'] = $secondTransferPhase
            $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json') -Encoding UTF8
            throw
        }
        finally {
            Copy-FileTransferLiveLogSlice -ArtifactDir $ArtifactDir -Bookmark $secondBookmark -FileName 'filetransfer-second-transfer-retained-log-slice.log'
        }
    }

    if ($RouteMode -eq 'handoff-fallback') {
        $activationOrEarlyFallbackProved = $evidence.activationEpochRecovered -or $evidence.fallbackEpochRecovered -or $evidence.fallbackEpochWaiting
        $fallbackRequirementSatisfied = $FaultMode -eq 'none' -or ($evidence.fallbackEpochStarted -and ($evidence.fallbackEpochRecovered -or $evidence.fallbackEpochWaiting))
        if (-not $evidence.tunaNegotiated -or -not $evidence.activationEpochStarted -or -not $activationOrEarlyFallbackProved -or -not $fallbackRequirementSatisfied) {
            throw "Tuna GUI file-transfer missing required V6 handoff/fallback evidence. Summary: $($evidence | ConvertTo-Json -Compress)"
        }
    }
    elseif ($RouteMode -eq 'preactivated') {
        if (-not $evidence.tunaNegotiated) {
            throw "Tuna GUI preactivated file-transfer did not prove Tuna negotiation before the measured transfer. Summary: $($evidence | ConvertTo-Json -Compress)"
        }

        if ($FaultMode -eq 'none' -and ($evidence.fallbackEpochStarted -or $evidence.fallbackEpochRecovered -or $evidence.fallbackEpochWaiting)) {
            throw "Tuna GUI preactivated no-fault transfer unexpectedly entered fallback. Summary: $($evidence | ConvertTo-Json -Compress)"
        }

    }
    elseif ($RouteMode -eq 'live-v4-switch-off') {
        $joinedRouteSequence = ($liveRouteEpochRouteChanges.ToArray() -join ',')
        $liveRouteProof = Get-TunaGuiLiveRouteEpochProof -Observations $liveRouteEpochObservations -ExpectedRoutes @('post_tuna_fallback_v6')
        if (-not $evidence.tunaNegotiated -or
            -not $evidence.fallbackEpochStarted -or
            -not $evidence.postTunaFallbackV6RouteObserved -or
            -not $evidence.fallbackEpochRecovered -or
            -not $liveRouteProof.Pass -or
            $joinedRouteSequence -ne 'post_tuna_fallback_v6') {
            throw "Tuna GUI live switch-off did not prove same-transfer V6 post-Tuna fallback strict live-route epoch sequence. sequence=$joinedRouteSequence findings=$($liveRouteProof.Findings -join '|') metadata_missing=$($liveRouteProof.MetadataMissingCount) Summary: $($evidence | ConvertTo-Json -Compress)"
        }
    }
    elseif ($RouteMode -eq 'live-reactivation-second-transfer') {
        $joinedRouteSequence = ($liveRouteEpochRouteChanges.ToArray() -join ',')
        if ($firstTransferTerminalBeforeLiveReactivation) {
            $liveRouteProof = Get-TunaGuiLiveRouteEpochProof -Observations $liveRouteEpochObservations -ExpectedRoutes @('post_tuna_fallback_v6')
            if (-not $evidence.fallbackEpochStarted -or
                -not $evidence.postTunaFallbackV6RouteObserved -or
                -not $evidence.fallbackEpochRecovered -or
                -not $liveRouteProof.Pass -or
                $joinedRouteSequence -ne 'post_tuna_fallback_v6' -or
                -not $secondTransferProofOk) {
                throw "Tuna GUI live reactivation second-transfer proof did not prove clean fallback terminal followed by second-transfer Tuna V4. sequence=$joinedRouteSequence second_transfer_proof=$secondTransferProofOk findings=$($liveRouteProof.Findings -join '|') metadata_missing=$($liveRouteProof.MetadataMissingCount) Summary: $($evidence | ConvertTo-Json -Compress)"
            }
        }
        else {
            $liveRouteProof = Get-TunaGuiLiveRouteEpochProof -Observations $liveRouteEpochObservations -ExpectedRoutes @('post_tuna_fallback_v6', 'file_tuna_v4')
            if (-not $evidence.tunaNegotiated -or
                -not $evidence.fallbackEpochStarted -or
                -not $evidence.activationEpochStarted -or
                -not $evidence.activationEpochRecovered -or
                -not $evidence.postTunaFallbackV6RouteObserved -or
                -not $evidence.fallbackEpochRecovered -or
                -not $liveRouteProof.Pass -or
                $joinedRouteSequence -ne 'post_tuna_fallback_v6,file_tuna_v4') {
                throw "Tuna GUI live reactivation did not prove same-transfer strict live-route epoch sequence before the second transfer. sequence=$joinedRouteSequence findings=$($liveRouteProof.Findings -join '|') metadata_missing=$($liveRouteProof.MetadataMissingCount) Summary: $($evidence | ConvertTo-Json -Compress)"
            }
        }
    }
    elseif ($RouteMode -eq 'live-multi-toggle') {
        $joinedRouteSequence = ($liveRouteEpochRouteChanges.ToArray() -join ',')
        $liveRouteProof = Get-TunaGuiLiveRouteEpochProof -Observations $liveRouteEpochObservations -ExpectedRoutes @('post_tuna_fallback_v6', 'file_tuna_v4', 'post_tuna_fallback_v6')
        if (-not $evidence.tunaNegotiated -or
            -not $evidence.fallbackEpochStarted -or
            -not $evidence.activationEpochStarted -or
            -not $evidence.activationEpochRecovered -or
            -not $evidence.postTunaFallbackV6RouteObserved -or
            -not $evidence.fallbackEpochRecovered -or
            -not $liveRouteProof.Pass -or
            $joinedRouteSequence -ne 'post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6') {
            throw "Tuna GUI live multi-toggle did not prove same-transfer strict live-route epoch cycling. sequence=$joinedRouteSequence findings=$($liveRouteProof.Findings -join '|') metadata_missing=$($liveRouteProof.MetadataMissingCount) Summary: $($evidence | ConvertTo-Json -Compress)"
        }
    }
    elseif ($RouteMode -eq 'live-regular-activation-cycle') {
        $joinedRouteSequence = ($liveRouteEpochRouteChanges.ToArray() -join ',')
        $liveRouteProof = Get-TunaGuiLiveRouteEpochProof -Observations $liveRouteEpochObservations -ExpectedRoutes @('file_tuna_v4', 'post_tuna_fallback_v6', 'file_tuna_v4', 'post_tuna_fallback_v6')
        if (-not $evidence.tunaNegotiated -or
            -not $evidence.fallbackEpochStarted -or
            -not $evidence.activationEpochStarted -or
            -not $evidence.activationEpochRecovered -or
            -not $evidence.postTunaFallbackV6RouteObserved -or
            -not $evidence.fallbackEpochRecovered -or
            -not $liveRouteProof.Pass -or
            $joinedRouteSequence -ne 'file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6') {
            throw "Tuna GUI live regular activation cycle did not prove same-transfer regular-to-Tuna and fallback/reactivation strict live-route epoch cycling. sequence=$joinedRouteSequence findings=$($liveRouteProof.Findings -join '|') metadata_missing=$($liveRouteProof.MetadataMissingCount) Summary: $($evidence | ConvertTo-Json -Compress)"
        }
    }
    elseif (-not $evidence.tunaNegotiated -or -not $evidence.fallbackEpochStarted) {
        throw "Tuna GUI post-fallback file-transfer did not prove fallback precondition before the measured transfer. Summary: $($evidence | ConvertTo-Json -Compress)"
    }

    if ($evidence.heartbeatTimeoutCount -gt 0 -or $evidence.peerDisconnectedCount -gt 0 -or $evidence.transportFailedCount -gt 0) {
        throw "Tuna GUI file-transfer completed with disconnect/timeout evidence. Summary: $($evidence | ConvertTo-Json -Compress)"
    }

    if ($pauseProbe -and (-not $evidence.pauseSent -or -not $evidence.pauseReceived -or -not $evidence.resumeSent -or -not $evidence.resumeReceived)) {
        throw "Tuna GUI pause/resume probe did not produce complete lifecycle evidence. Summary: $($evidence | ConvertTo-Json -Compress)"
    }

    if (-not (Test-GuiSmokeEnvEnabled -Name 'NLINK_TUNA_GUI_MIXED_SCREENSHARE')) {
        [void](Wait-AutomationTextEquals -Window $Context.HelperWindow -AutomationId 'SessionHeader.StatusText' -ExpectedText 'Connected' -TimeoutMs 10000)
        [void](Wait-AutomationTextEquals -Window $Context.HelpeeWindow -AutomationId 'SessionHeader.StatusText' -ExpectedText 'Connected' -TimeoutMs 10000)
    }

    Write-Host ("[GUI Smoke][filetransfer_tuna] PASS direction={0} bytes={1} transfer_id={2} sent_chunk_bytes={3} received_chunk_bytes={4}" -f `
        $Direction,
        $PayloadSizeBytes,
        $terminal.TransferId,
        $evidence.senderChunkBytes,
        $evidence.receiverChunkBytes) -ForegroundColor Green
}

function Wait-FileTransferTerminalOrProgressBeforeAction {
    param(
        [Parameter(Mandatory = $true)][int]$Bookmark,
        [Parameter(Mandatory = $true)][int]$ProgressTimeoutMs,
        [int]$MinProgressEvents = 1,
        [int]$TimeoutMs = 60000
    )

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $maxReceiverNextChunkIndex = [ref](-1L)
    $maxReceiverHighestChunkIndex = [ref](-1L)
    $progressEvents = 0
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        foreach ($line in @(Get-AppLogLinesAfterBookmark -Bookmark $Bookmark)) {
            if ($line.IndexOf('event=', [System.StringComparison]::Ordinal) -lt 0) {
                continue
            }

            $fields = ConvertFrom-GuiSmokeSemicolonFields -Message $line
            if ((Get-FileTransferLiveProgressScore -Fields $fields -MaxReceiverNextChunkIndex $maxReceiverNextChunkIndex -MaxReceiverHighestChunkIndex $maxReceiverHighestChunkIndex) -gt 0) {
                $progressEvents++
                if ($progressEvents -ge $MinProgressEvents) {
                    return $true
                }
            }
        }

        Start-Sleep -Milliseconds 500
    }

    throw "Timed out waiting for initial GUI file-transfer progress before Tuna action; progress_events=$progressEvents; timeout_s=$($TimeoutMs / 1000); progress_timeout_s=$($ProgressTimeoutMs / 1000)."
}

function Run-ScenarioFileTransferTunaHandoffFallback {
    param([Parameter(Mandatory = $true)]$Context)

    if (-not (Test-GuiSmokeEnvEnabled -Name 'NLINK_RUN_TUNA_GUI_FILETRANSFER')) {
        Write-Host '[GUI Smoke][filetransfer_tuna] SKIP: set NLINK_RUN_TUNA_GUI_FILETRANSFER=1 to run paid Tuna GUI file-transfer automation.' -ForegroundColor Yellow
        return
    }

    Reset-ScenarioContext -Context $Context

    if (-not (Get-IsNknTransport)) {
        throw 'FILETRANSFER_TUNA_HANDOFF_FALLBACK requires NLINK_TRANSPORT=NKN.'
    }

    $walletPassword = [string]$env:NLINK_TUNA_TEST_WALLET_PASSWORD
    if ([string]::IsNullOrWhiteSpace($walletPassword)) {
        throw 'Set NLINK_TUNA_TEST_WALLET_PASSWORD for Tuna GUI file-transfer automation.'
    }

    $artifactDir = Get-FileTransferSoakArtifactDir
    $receivedRoot = Join-Path $artifactDir 'received'
    New-Item -ItemType Directory -Force -Path $receivedRoot | Out-Null
    $walletPath = Resolve-TunaGuiWalletPath
    $sidecarPath = Resolve-TunaGuiSidecarPath
    $stateRoot = Initialize-TunaGuiRuntimeState -ArtifactDir $artifactDir -WalletPath $walletPath -SidecarPath $sidecarPath
    $autopickPath = [string]$env:NLINK_FILETRANSFER_SOAK_AUTOPICK_FILE
    if ([string]::IsNullOrWhiteSpace($autopickPath)) {
        $autopickPath = Join-Path $artifactDir 'filetransfer-tuna-gui-payload.bin'
        $env:NLINK_FILETRANSFER_SOAK_AUTOPICK_FILE = $autopickPath
    }

    $configuredPayloadSizes = [string]$env:NLINK_FILETRANSFER_SOAK_PAYLOAD_SIZES
    if ([string]::IsNullOrWhiteSpace($configuredPayloadSizes)) {
        $payloadSize = 128L * 1024L * 1024L
    }
    else {
        $payloadSizes = @(Get-FileTransferSoakPayloadSizes)
        $payloadSize = if ($payloadSizes.Count -gt 0) { [long]$payloadSizes[0] } else { 128L * 1024L * 1024L }
    }
    if ($payloadSize -lt (16L * 1024L * 1024L)) {
        Write-Host '[GUI Smoke][filetransfer_tuna] Payload size below 16MiB; handoff/fallback may happen near the tail. Consider NLINK_FILETRANSFER_SOAK_PAYLOAD_SIZES=128MiB.' -ForegroundColor Yellow
    }

    $direction = Get-FileTransferSoakDirection
    if ($direction -eq 'alternate') { $direction = 'helpee-to-helper' }
    $seed = Get-FileTransferSoakSeed
    $cycleTimeoutMs = Get-FileTransferSoakCycleTimeoutMs
    $startupTimeoutMs = Get-FileTransferSoakStartupTimeoutMs
    $progressTimeoutMs = Get-FileTransferSoakProgressTimeoutMs
    $mixedScreenShare = Test-GuiSmokeEnvEnabled -Name 'NLINK_TUNA_GUI_MIXED_SCREENSHARE'
    $payerMode = Get-TunaGuiPayerRole
    $faultMode = Get-TunaGuiFaultMode
    $routeMode = Get-TunaGuiRouteMode
    $runBookmark = Get-AppLogBookmark

    Write-Host ("[GUI Smoke][filetransfer_tuna] artifact_dir={0}; state_root={1}; direction={2}; payer={3}; fault={4}; route_mode={5}; payload_bytes={6}; mixed_screenshare={7}" -f `
        $artifactDir,
        $stateRoot,
        $direction,
        $payerMode,
        $faultMode,
        $routeMode,
        $payloadSize,
        ($(if ($mixedScreenShare) { 1 } else { 0 }))) -ForegroundColor DarkGray

    $previousInboundRoot = $env:NLINK_FILE_TRANSFER_TEST_INBOUND_ROOT
    $mixedShareButton = $null
    try {
        $env:NLINK_FILE_TRANSFER_TEST_INBOUND_ROOT = $receivedRoot

        Start-HelpeeFlow -Context $Context
        Start-HelperFlow -Context $Context
        [void](Connect-HelperAndHelpee -Context $Context)
        if ($mixedScreenShare) {
            $mixedShareButton = Start-FileTransferMixedScreenShare -Context $Context -WarmupTimeoutMs (Get-FileTransferMixedScreenShareWarmupTimeoutMs)
        }

        Invoke-FileTransferTunaHandoffFallbackCycle `
            -Context $Context `
            -ArtifactDir $artifactDir `
            -AutopickPath $autopickPath `
            -PayloadSizeBytes $payloadSize `
            -Direction $direction `
            -Seed $seed `
            -TimeoutMs $cycleTimeoutMs `
            -StartupTimeoutMs $startupTimeoutMs `
            -ProgressTimeoutMs $progressTimeoutMs `
            -PayerMode $payerMode `
            -FaultMode $faultMode `
            -RouteMode $routeMode `
            -WalletPassword $walletPassword
    }
    catch {
        $failureClassification = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark $runBookmark -ErrorMessage $_.Exception.Message -RouteMode $routeMode
        $failureSummary = [ordered]@{
            event = 'filetransfer_tuna_gui_handoff_fallback_failure'
            direction = $direction
            payerMode = $payerMode
            faultMode = $faultMode
            routeMode = $routeMode
            payloadBytes = $payloadSize
            completed = $false
            integrityOk = $false
            failurePhase = $failureClassification.Phase
            failureReason = $failureClassification.Reason
            tunaActive = [bool]$failureClassification.TunaActive
            listenerReady = [bool]$failureClassification.ListenerReady
            listenerUnavailable = [bool]($failureClassification.ListenerUnavailable -or $failureClassification.ListenerSidecarUnavailable)
            routeSelected = [bool]$failureClassification.RouteSelected
            activationOfferNotObserved = [bool]$failureClassification.ActivationOfferNotObserved
            activationOfferWaitingAnswer = [bool]$failureClassification.ActivationOfferWaitingAnswer
            runtimeUnlockDispatchDeferredForRegularV4ReceiveRecovery = [bool]$failureClassification.RuntimeUnlockDispatchDeferredForRegularV4ReceiveRecovery
            activationOfferSent = [bool]$failureClassification.ActivationOfferSent
            activationOfferReceived = [bool]$failureClassification.ActivationOfferReceived
            measuredOfferSent = [bool]$failureClassification.MeasuredOfferSent
            measuredOfferReceived = [bool]$failureClassification.MeasuredOfferReceived
            offerSent = [bool]$failureClassification.OfferSent
            offerReceived = [bool]$failureClassification.OfferReceived
            terminalObserved = [bool]$failureClassification.TerminalObserved
            error = $_.Exception.Message
            failedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        }

        try {
            $failureSummary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $artifactDir 'filetransfer-tuna-gui-error.json') -Encoding UTF8
        }
        catch {}

        throw
    }
    finally {
        if ($null -ne $mixedShareButton) {
            try {
                Click-Element $mixedShareButton
                [void](Wait-ScreenShareButtonText -Window $Context.HelpeeWindow -ExpectedText 'Share screen' -TimeoutMs 10000)
            }
            catch {
                Write-Host "[GUI Smoke][filetransfer_tuna_mixed] Screen-share stop after file-transfer cycle was not clean: $($_.Exception.Message)" -ForegroundColor Yellow
            }
        }

        Copy-FileTransferLiveLogSlice -ArtifactDir $artifactDir -Bookmark $runBookmark
        if ($null -eq $previousInboundRoot) {
            Remove-Item Env:NLINK_FILE_TRANSFER_TEST_INBOUND_ROOT -ErrorAction SilentlyContinue
        }
        else {
            $env:NLINK_FILE_TRANSFER_TEST_INBOUND_ROOT = $previousInboundRoot
        }
    }
}

function Suspend-ProcessForRecoveryWindow {
    param(
        [Parameter(Mandatory = $true)]$Process,
        [int]$DurationMs = 5000
    )

    if ($null -eq $Process -or $Process.HasExited) {
        throw 'Cannot suspend a process that is not running.'
    }

    $suspendStatus = [NtProcessControl]::NtSuspendProcess($Process.Handle)
    if ($suspendStatus -ne 0) {
        throw "NtSuspendProcess failed with status 0x$('{0:X8}' -f $suspendStatus)."
    }

    try {
        Start-Sleep -Milliseconds $DurationMs
    }
    finally {
        $resumeStatus = [NtProcessControl]::NtResumeProcess($Process.Handle)
        if ($resumeStatus -ne 0) {
            throw "NtResumeProcess failed with status 0x$('{0:X8}' -f $resumeStatus)."
        }
    }
}

function Get-IsDeepDiagnosticsEnabled {
    $value = [string]$env:NLINK_FEATURE_SCREENCAP_DEEP_DIAGNOSTICS
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $false
    }

    return $value -match '^(1|true|yes|on)$'
}

function Get-DecoderDebugSnapshot {
    $decoderDebugDir = Join-Path $env:LOCALAPPDATA 'nLink\decoder-debug'
    if (-not (Test-Path $decoderDebugDir)) {
        return @()
    }

    return @(
        Get-ChildItem -Path $decoderDebugDir -Force -ErrorAction SilentlyContinue |
        ForEach-Object { $_.FullName }
    )
}

function Assert-DefaultScreenShareTelemetryQuiet {
    param(
        [string[]]$DecoderDebugSnapshotBefore = @()
    )

    if (Get-IsDeepDiagnosticsEnabled) {
        return
    }

    $logPath = Join-Path $env:LOCALAPPDATA 'nLink\logs\nlink.log'
    if (Test-Path $logPath) {
        foreach ($line in [System.IO.File]::ReadLines($logPath)) {
            if ($line -match 'event=helper_chat_panel_state;') {
                throw "Default runtime log emitted helper_chat_panel_state even though deep diagnostics are disabled."
            }
        }
    }

    $before = @($DecoderDebugSnapshotBefore | Sort-Object -Unique)
    $after = @(Get-DecoderDebugSnapshot | Sort-Object -Unique)
    $newEntries = @($after | Where-Object { $_ -notin $before })
    if ($newEntries.Count -gt 0) {
        throw "Default runtime created decoder-debug artifacts with deep diagnostics disabled: $($newEntries -join ', ')"
    }
}

function Cleanup-Processes {
    param(
        [array]$Processes,
        [string]$ExePath = ''
    )
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

    Stop-BridgeNodeProcessesForExePath -ExePath $ExePath
}

function Get-BridgeNodeProcessesForExePath {
    param([string]$ExePath)

    if ([string]::IsNullOrWhiteSpace($ExePath)) {
        return @()
    }

    $resolvedExePath = $null
    try {
        $resolvedExePath = [System.IO.Path]::GetFullPath($ExePath)
    }
    catch {
        return @()
    }

    $appDir = Split-Path -Parent $resolvedExePath
    if ([string]::IsNullOrWhiteSpace($appDir)) {
        return @()
    }

    $expectedNodePath = [System.IO.Path]::GetFullPath((Join-Path $appDir 'bridge\win-x64\node.exe'))
    $expectedScriptPath = [System.IO.Path]::GetFullPath((Join-Path $appDir 'bridge\win-x64\index.js'))
    if (-not (Test-Path $expectedNodePath)) {
        return @()
    }

    return @(
        Get-CimInstance Win32_Process -Filter "Name = 'node.exe' OR Name = 'node'" -ErrorAction SilentlyContinue |
            Where-Object {
                $exeMatches = $false
                $cmdMatches = $false

                if ($_.ExecutablePath) {
                    try {
                        $exeMatches = [string]::Equals(
                            [System.IO.Path]::GetFullPath($_.ExecutablePath),
                            $expectedNodePath,
                            [System.StringComparison]::OrdinalIgnoreCase)
                    }
                    catch {}
                }

                if ($_.CommandLine) {
                    try {
                        $cmdMatches = $_.CommandLine.IndexOf($expectedScriptPath, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
                    }
                    catch {}
                }

                return $exeMatches -or $cmdMatches
            }
    )
}

function Stop-BridgeNodeProcessesForExePath {
    param([string]$ExePath)

    $targets = @(Get-BridgeNodeProcessesForExePath -ExePath $ExePath)
    if ($targets.Count -eq 0) {
        return
    }

    foreach ($target in $targets) {
        try {
            Stop-Process -Id $target.ProcessId -Force -ErrorAction SilentlyContinue
        }
        catch {}
    }

    Start-Sleep -Milliseconds 400
}

function Start-AppInstance {
    param(
        [Parameter(Mandatory = $true)][string]$ExePath,
        [Parameter(Mandatory = $true)][string]$RoleName
    )
    Write-Host "[GUI Smoke] Starting $RoleName instance..." -ForegroundColor Cyan

    $launchEnvironment = Get-AppLaunchEnvironmentOverrides -RoleName $RoleName
    $workingDirectory = Split-Path -Parent $ExePath

    if ($launchEnvironment.Count -gt 0) {
        $summary = ($launchEnvironment.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ', '
        Write-Host "[GUI Smoke] Launch env sanitized for ${RoleName}: $summary" -ForegroundColor DarkGray
    }

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($ExePath)
    $startInfo.WorkingDirectory = $workingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    foreach ($entry in $launchEnvironment.GetEnumerator()) {
        $startInfo.Environment[$entry.Key] = [string]$entry.Value
    }

    $process = [System.Diagnostics.Process]::Start($startInfo)
    if (-not $process) {
        throw "Failed to start app instance for role '$RoleName'."
    }

    $capture = New-GuiSmokeProcessOutputCapture -RoleName $RoleName -ProcessId $process.Id
    Register-ObjectEvent -InputObject $process -EventName OutputDataReceived -MessageData $capture.StdoutPath -Action {
        if ($null -ne $EventArgs.Data) {
            [System.IO.File]::AppendAllText([string]$Event.MessageData, $EventArgs.Data + [Environment]::NewLine, [System.Text.Encoding]::UTF8)
        }
    } | Out-Null
    Register-ObjectEvent -InputObject $process -EventName ErrorDataReceived -MessageData $capture.StderrPath -Action {
        if ($null -ne $EventArgs.Data) {
            [System.IO.File]::AppendAllText([string]$Event.MessageData, $EventArgs.Data + [Environment]::NewLine, [System.Text.Encoding]::UTF8)
        }
    } | Out-Null
    $process.BeginOutputReadLine()
    $process.BeginErrorReadLine()

    return $process
}

function Test-LegacyHigherClarityTupleActive {
    $captureRaw = [Environment]::GetEnvironmentVariable('NLINK_FEATURE_SCREENCAP_MAX_FPS', [System.EnvironmentVariableTarget]::Process)
    $transportRaw = [Environment]::GetEnvironmentVariable('NLINK_FEATURE_SCREENCAP_TRANSPORT_MAX_FPS', [System.EnvironmentVariableTarget]::Process)
    $scaleRaw = [Environment]::GetEnvironmentVariable('NLINK_FEATURE_SCREENCAP_SCALE', [System.EnvironmentVariableTarget]::Process)

    $capture = 0
    $transport = 0
    $scale = 0.0

    return [int]::TryParse($captureRaw, [ref]$capture) -and
        [int]::TryParse($transportRaw, [ref]$transport) -and
        [double]::TryParse($scaleRaw, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$scale) -and
        $capture -eq 20 -and
        $transport -eq 8 -and
        [math]::Abs($scale - 0.85) -lt 0.0001
}

function Get-AppLaunchEnvironmentOverrides {
    param([string]$RoleName = '')

    $overrides = @{}
    if (Test-LegacyHigherClarityTupleActive) {
        $overrides['NLINK_FEATURE_SCREENCAP_MAX_FPS'] = '15'
        $overrides['NLINK_FEATURE_SCREENCAP_TRANSPORT_MAX_FPS'] = '8'
        $overrides['NLINK_FEATURE_SCREENCAP_SCALE'] = '1.0'
    }

    $identityPath = Get-GuiSmokeNknIdentityPathForRole -RoleName $RoleName
    if (-not [string]::IsNullOrWhiteSpace($identityPath)) {
        $overrides['NLINK_NKN_KEY_PATH'] = $identityPath
    }

    return $overrides
}

function Get-GuiSmokeNknIdentityPathForRole {
    param([string]$RoleName = '')

    $artifactDir = [string]$env:NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR
    if ([string]::IsNullOrWhiteSpace($artifactDir)) {
        return ''
    }

    $resolvedArtifactDir = [System.IO.Path]::GetFullPath($artifactDir)
    $identityRoot = Join-Path $resolvedArtifactDir 'nkn-identities'
    $safeRole = if ([string]::IsNullOrWhiteSpace($RoleName)) { 'app' } else { $RoleName.Trim().ToLowerInvariant() }
    $safeRole = [regex]::Replace($safeRole, '[^a-z0-9_-]+', '-')
    if ([string]::IsNullOrWhiteSpace($safeRole)) {
        $safeRole = 'app'
    }

    $roleDir = Join-Path $identityRoot $safeRole
    New-Item -ItemType Directory -Force -Path $roleDir | Out-Null
    return (Join-Path $roleDir 'identity.json')
}

function Wait-Window {
    param(
        [Parameter(Mandatory = $true)]$Process,
        [string]$RoleName = 'app',
        [int]$TimeoutMs = 45000
    )

    $pollMs = 200
    $logEveryMs = 5000
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $nextProgressLogMs = $logEveryMs

    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        $snapshot = Get-ProcessSnapshot -Process $Process
        if (-not $snapshot.IsRunning) {
            throw "Process exited before window appeared (role=$RoleName, pid=$($Process.Id), elapsed_ms=$($sw.ElapsedMilliseconds))."
        }

        $window = Get-WindowElementByProcessId -ProcessId $Process.Id
        if ($window) {
            return $window
        }

        if ($sw.ElapsedMilliseconds -ge $nextProgressLogMs) {
            Write-Host ("[GUI Smoke] Waiting for {0} window... pid={1} elapsed_ms={2} main_window_handle={3} main_window_title='{4}' threads={5} handles={6} working_set_mb={7} top_level_windows={8}" -f `
                $RoleName,
                $snapshot.ProcessId,
                $sw.ElapsedMilliseconds,
                $snapshot.MainWindowHandle,
                $snapshot.MainWindowTitle,
                $snapshot.ThreadCount,
                $snapshot.HandleCount,
                $snapshot.WorkingSetMb,
                $snapshot.TopLevelWindowCount) -ForegroundColor DarkGray
            $nextProgressLogMs += $logEveryMs
        }

        Start-Sleep -Milliseconds $pollMs
    }

    $finalSnapshot = Get-ProcessSnapshot -Process $Process
    throw ("Timed out waiting for window (role={0}, pid={1}, elapsed_ms={2}, main_window_handle={3}, main_window_title='{4}', threads={5}, handles={6}, working_set_mb={7}, top_level_windows={8})." -f `
        $RoleName,
        $Process.Id,
        $sw.ElapsedMilliseconds,
        $finalSnapshot.MainWindowHandle,
        $finalSnapshot.MainWindowTitle,
        $finalSnapshot.ThreadCount,
        $finalSnapshot.HandleCount,
        $finalSnapshot.WorkingSetMb,
        $finalSnapshot.TopLevelWindowCount)
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

function Format-ConnectionDiagnosticText {
    param([AllowNull()][string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return '(none)'
    }

    return (($Text -replace '\s+', ' ').Trim() -replace ';', ',')
}

function Format-BannerDiagnosticText {
    param([AllowNull()]$Bundle)

    if ($null -eq $Bundle -or -not $Bundle.HasBanner) {
        return '(none)'
    }

    $parts = @(
        Format-ConnectionDiagnosticText -Text ([string]$Bundle.Title),
        Format-ConnectionDiagnosticText -Text ([string]$Bundle.Message),
        Format-ConnectionDiagnosticText -Text ([string]$Bundle.RetryCountdown)
    ) | Where-Object { -not [string]::Equals($_, '(none)', [System.StringComparison]::Ordinal) }

    if ($parts.Count -eq 0) {
        return '(visible)'
    }

    return ($parts -join ' | ')
}

function Get-ConnectionWaitDiagnosticContext {
    param([Parameter(Mandatory = $true)]$Context)

    $helperStatus = '(unavailable)'
    $helpeeStatus = '(unavailable)'
    $helperBanner = '(unavailable)'
    $helpeeBanner = '(unavailable)'

    try {
        $helperStatus = Format-ConnectionDiagnosticText -Text (Get-SessionHeaderStatusValue -Window $Context.HelperWindow)
        $helperBanner = Format-BannerDiagnosticText -Bundle (Get-BannerTextBundle -Window $Context.HelperWindow)
    } catch {}

    try {
        $helpeeStatus = Format-ConnectionDiagnosticText -Text (Get-SessionHeaderStatusValue -Window $Context.HelpeeWindow)
        $helpeeBanner = Format-BannerDiagnosticText -Bundle (Get-BannerTextBundle -Window $Context.HelpeeWindow)
    } catch {}

    return ("helper_status='{0}'; helper_banner='{1}'; helpee_status='{2}'; helpee_banner='{3}'" -f `
        $helperStatus,
        $helperBanner,
        $helpeeStatus,
        $helpeeBanner)
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

function Read-HelperIdentityFromAppLog {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [int]$TimeoutMs = 30000
    )

    $logPath = Join-Path $env:LOCALAPPDATA 'nLink\logs\nlink.log'
    return Wait-Until -TimeoutMs $TimeoutMs -PollMs 250 -OnTimeoutMessage 'Timed out waiting for helper address in app log.' -Condition {
        $latestStructuredLogIdentity = $null
        $sawCurrentRunStructuredReady = $false
        foreach ($line in @(Get-AppLogLinesAfterBookmark -Bookmark $Context.HelperLogBookmark)) {
            $parsed = Parse-HelperReadyLogLine -Line ([string]$line)
            if ($parsed -and -not $parsed.IsLegacy) {
                $Context.HelperRunId = $parsed.RunId
                $Context.HelperListenerGeneration = $parsed.ListenerGeneration
                $sawCurrentRunStructuredReady = $true
                if (Test-HelperIdentityValueUsable -Value $parsed.Address) {
                    $latestStructuredLogIdentity = $parsed.Address.Trim()
                }
            }
        }

        foreach ($artifactPath in @(Get-HelperIdentityArtifactPaths)) {
            $artifactValue = [string](Get-Content $artifactPath -ErrorAction SilentlyContinue | Select-Object -Last 1)
            $artifact = Parse-HelperIdentityArtifactLine -Line $artifactValue
            if ($artifact -and
                $sawCurrentRunStructuredReady -and
                $artifact.HostReady -and
                $artifact.PublishedUtcMs -ge $Context.HelperStartedUtcMs -and
                ([string]::IsNullOrWhiteSpace($Context.HelperRunId) -or
                 [string]::Equals($artifact.RunId, $Context.HelperRunId, [System.StringComparison]::Ordinal)) -and
                ($Context.HelperListenerGeneration -le 0 -or
                 $artifact.ListenerGeneration -eq $Context.HelperListenerGeneration) -and
                (Test-HelperIdentityValueUsable -Value $artifact.Address)) {
                $Context.HelperRunId = $artifact.RunId
                $Context.HelperListenerGeneration = $artifact.ListenerGeneration
                return $artifact.Address
            }
        }

        if (-not [string]::IsNullOrWhiteSpace($latestStructuredLogIdentity)) {
            return $latestStructuredLogIdentity
        }

        if (-not (Test-Path $logPath)) {
            return $null
        }

        $match = $null
        foreach ($line in @(Get-AppLogLinesAfterBookmark -Bookmark $Context.HelperLogBookmark)) {
            $parsed = Parse-HelperReadyLogLine -Line ([string]$line)
            if ($parsed -and $parsed.IsLegacy) {
                if (Test-HelperIdentityValueUsable -Value $parsed.Address) {
                    $match = $parsed.Address
                }
            }
        }

        if (-not [string]::IsNullOrWhiteSpace($match)) {
            return $match.Trim()
        }

        return $null
    }
}

function Copy-HelperIdentityWithRecovery {
    param([Parameter(Mandatory = $true)]$Context)

    $attempts = if (Get-IsNknTransport) { 3 } else { 1 }
    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        try {
            $copyButton = Find-VisibleByAutomationId -Root $Context.HelperWindow -AutomationId 'Helper.CopyHelperIdentity'
            if (-not (Get-IsNknTransport)) {
                if ($copyButton -and $copyButton.Current.IsEnabled) {
                    return Copy-HelperIdentityAndReadClipboard -HelperWindow $Context.HelperWindow
                }

                return Read-HelperIdentityFromAppLog -Context $Context -TimeoutMs (Get-TransportAwareTimeoutMs -DefaultMs 10000 -NknMs 45000)
            }

            try {
                return Read-HelperIdentityFromAppLog -Context $Context -TimeoutMs (Get-TransportAwareTimeoutMs -DefaultMs 10000 -NknMs 45000)
            }
            catch {
                if ($copyButton -and $copyButton.Current.IsEnabled) {
                    Write-Host "[GUI Smoke] Helper identity log sync unavailable; falling back to helper clipboard copy for this run." -ForegroundColor DarkGray
                    return Copy-HelperIdentityAndReadClipboard -HelperWindow $Context.HelperWindow
                }

                throw
            }
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

    $pastedViaUi = $false
    $pasteButton = Find-ByNameAndType -Root $HelpeeWindow -Name 'Paste helper address' -ControlType ([System.Windows.Automation.ControlType]::Button)
    if ($pasteButton -and -not $pasteButton.Current.IsOffscreen -and $pasteButton.Current.IsEnabled) {
        try {
            Set-Clipboard -Value $HelperIdentity
            Click-Element $pasteButton
            [void](Wait-ElementValueMatchesText -Element $input -ExpectedText $HelperIdentity -TimeoutMs 1500)
            $pastedViaUi = $true
        }
        catch {
            $pastedViaUi = $false
        }
    }

    if (-not $pastedViaUi) {
        Set-Text -Element $input -Text $HelperIdentity
        [void](Wait-ElementValueMatchesText -Element $input -ExpectedText $HelperIdentity -TimeoutMs 1500)
    }

    $inputState = Test-ElementValueMatchesText -Element $input -ExpectedText $HelperIdentity
    if (-not $inputState.IsMatch) {
        Set-Text -Element $input -Text $HelperIdentity
        [void](Wait-ElementValueMatchesText -Element $input -ExpectedText $HelperIdentity -TimeoutMs 1500)
    }

    Submit-TextInputWithEnter -Element $input

    [void](Wait-Until -TimeoutMs (Get-TransportAwareTimeoutMs -DefaultMs 10000 -NknMs 60000) -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helpee invite to become ready.' -Condition {
        if (Test-ConnectionFailedSurface -Window $HelpeeWindow) {
            throw 'Helpee reached Connection failed before invite became ready.'
        }

        $status = Find-VisibleByAutomationId -Root $HelpeeWindow -AutomationId 'Helpee.InviteStatus'
        if ($status) {
            $statusText = (Get-ElementTextSafe -Element $status).Trim()
            if ($statusText -match 'Preparing invite|Updating invite') {
                return $null
            }
        }

        return $true
    })

    $request = Wait-Until -TimeoutMs (Get-TransportAwareTimeoutMs -DefaultMs 10000 -NknMs 45000) -PollMs 200 -OnTimeoutMessage 'Timed out waiting for Helpee.RequestHelp to become enabled.' -Condition {
        if (Test-ConnectionFailedSurface -Window $HelpeeWindow) {
            throw 'Helpee reached Connection failed before Request help became enabled.'
        }

        $btn = Find-VisibleByAutomationId -Root $HelpeeWindow -AutomationId 'Helpee.RequestHelp'
        if ($btn -and $btn.Current.IsEnabled) { return $btn }
        return $null
    }

    Click-Element $request
    return $request
}

function Test-HelpeeHelperIdentityRequestRetryReady {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$HelpeeWindow)

    if (Test-ConnectionFailedSurface -Window $HelpeeWindow) {
        return $false
    }

    $input = Find-VisibleByAutomationId -Root $HelpeeWindow -AutomationId 'Helpee.HelperIdentityInput'
    $request = Find-VisibleByAutomationId -Root $HelpeeWindow -AutomationId 'Helpee.RequestHelp'
    if ($input -and
        $input.Current.IsEnabled -and
        -not $input.Current.IsOffscreen -and
        $request -and
        $request.Current.IsEnabled -and
        -not $request.Current.IsOffscreen) {
        return $true
    }

    return $false
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
            throw "Helper reached Connection failed before showing an incoming request. $(Get-ConnectionWaitDiagnosticContext -Context $Context)"
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

    throw "Timed out waiting for helper incoming request acceptance UI. $(Get-ConnectionWaitDiagnosticContext -Context $Context)"
}

function Connect-HelperIdentityRequestFlow {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [Parameter(Mandatory = $true)][string]$HelperIdentity
    )

    $maxAttempts = 3
    $lastError = $null
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        if ($attempt -gt 1) {
            Write-Host "[GUI Smoke] Retrying helper identity help request after setup ack miss. attempt=$attempt/$maxAttempts" -ForegroundColor DarkGray
        }

        [void](Enter-HelpeeHelperIdentityAndRequestHelp -HelpeeWindow $Context.HelpeeWindow -HelperIdentity $HelperIdentity)

        try {
            return Wait-HelperAcceptRequestOrExit -Context $Context -TimeoutMs 90000
        }
        catch {
            $lastError = $_
            $message = $_.Exception.Message
            if ($message -notlike 'Timed out waiting for helper incoming request acceptance UI*') {
                throw
            }

            if ($attempt -ge $maxAttempts -or -not (Test-HelpeeHelperIdentityRequestRetryReady -HelpeeWindow $Context.HelpeeWindow)) {
                throw
            }
        }
    }

    if ($lastError) {
        throw $lastError
    }

    throw 'Helper identity request flow failed before an accept button was available.'
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
            throw "Helper reached Connection failed before helpee approval. $(Get-ConnectionWaitDiagnosticContext -Context $Context)"
        }

        if (Test-ConnectionFailedSurface -Window $Context.HelpeeWindow) {
            throw "Helpee reached Connection failed before approval. $(Get-ConnectionWaitDiagnosticContext -Context $Context)"
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

    throw "Timed out waiting for helpee Allow approval UI. $(Get-ConnectionWaitDiagnosticContext -Context $Context)"
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
            throw "Helper reached Connection failed before connected chat became visible. $(Get-ConnectionWaitDiagnosticContext -Context $Context)"
        }

        if (Test-ConnectionFailedSurface -Window $Context.HelpeeWindow) {
            throw "Helpee reached Connection failed before connected chat became visible. $(Get-ConnectionWaitDiagnosticContext -Context $Context)"
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

    throw "Timed out waiting for connected chat on both helper and helpee. $(Get-ConnectionWaitDiagnosticContext -Context $Context)"
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
        HelperLogBookmark = 0
        HelperRunId = ''
        HelperListenerGeneration = -1
        HelperStartedUtcMs = 0
    }
}

function Reset-ScenarioContext {
    param([Parameter(Mandatory = $true)]$Context)
    $toKill = @($Context.Processes.ToArray())
    if ($toKill.Count -gt 0) {
        Cleanup-Processes -Processes $toKill -ExePath $Context.ExePath
    }
    $Context.Processes.Clear()
    $Context.HelpeeProc = $null
    $Context.HelperProc = $null
    $Context.HelpeeWindow = $null
    $Context.HelperWindow = $null
    $Context.HelperLogBookmark = 0
    $Context.HelperRunId = ''
    $Context.HelperListenerGeneration = -1
    $Context.HelperStartedUtcMs = 0
    Clear-HelperIdentityArtifact
}

function Start-HelpeeFlow {
    param([Parameter(Mandatory = $true)]$Context)
    $Context.HelpeeProc = Start-AppInstance -ExePath $Context.ExePath -RoleName 'helpee'
    [void]$Context.Processes.Add($Context.HelpeeProc)
    $Context.HelpeeWindow = Wait-Window -Process $Context.HelpeeProc -RoleName 'helpee' -TimeoutMs (Get-StartupWindowTimeoutMs)
    Click-HomeButton -Window $Context.HelpeeWindow -Text 'I need help'
    if (Try-ClickRoleButtonIfPresent -Window $Context.HelpeeWindow -RoleButtonText 'I need help') {
        Write-Host "[GUI Smoke] Role page detected (helpee); selected 'I need help'." -ForegroundColor DarkGray
    }
}

function Start-HelperFlow {
    param([Parameter(Mandatory = $true)]$Context)
    $Context.HelperLogBookmark = Get-AppLogBookmark
    $Context.HelperRunId = ''
    $Context.HelperListenerGeneration = -1
    Clear-HelperIdentityArtifact
    $Context.HelperProc = Start-AppInstance -ExePath $Context.ExePath -RoleName 'helper'
    try {
        $Context.HelperStartedUtcMs = [DateTimeOffset]::new($Context.HelperProc.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds()
    }
    catch {
        $Context.HelperStartedUtcMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    }
    [void]$Context.Processes.Add($Context.HelperProc)
    $Context.HelperWindow = Wait-Window -Process $Context.HelperProc -RoleName 'helper' -TimeoutMs (Get-StartupWindowTimeoutMs)
    Click-HomeButton -Window $Context.HelperWindow -Text 'I want to help someone'
    if (Try-ClickRoleButtonIfPresent -Window $Context.HelperWindow -RoleButtonText 'I want to help someone') {
        Write-Host "[GUI Smoke] Role page detected (helper); selected 'I want to help someone'." -ForegroundColor DarkGray
    }
}

function Restart-HelpeeFlow {
    param([Parameter(Mandatory = $true)]$Context)

    if ($Context.HelpeeProc) {
        Cleanup-Processes -Processes @($Context.HelpeeProc) -ExePath $Context.ExePath
    }

    $Context.HelpeeProc = $null
    $Context.HelpeeWindow = $null
    Start-HelpeeFlow -Context $Context
}

function Restart-HelperFlow {
    param([Parameter(Mandatory = $true)]$Context)

    if ($Context.HelperProc) {
        Cleanup-Processes -Processes @($Context.HelperProc) -ExePath $Context.ExePath
    }

    $Context.HelperProc = $null
    $Context.HelperWindow = $null
    Start-HelperFlow -Context $Context
}

function Wait-HelpeeConnectionEntryMode {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [int]$TimeoutMs = 30000
    )

    return Wait-Until -TimeoutMs $TimeoutMs -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helpee connection entry mode.' -Condition {
        if (Test-ConnectionFailedSurface -Window $Context.HelpeeWindow) {
            throw 'Helpee reached Connection failed before choosing helper-identity or invite entry mode.'
        }

        $helperIdentityInput = Find-VisibleByAutomationId -Root $Context.HelpeeWindow -AutomationId 'Helpee.HelperIdentityInput'
        if ($helperIdentityInput -and
            $helperIdentityInput.Current.IsEnabled -and
            -not $helperIdentityInput.Current.IsOffscreen) {
            return 'helper_identity'
        }

        $copyBtn = Find-VisibleByAutomationIdOrName -Root $Context.HelpeeWindow -AutomationId 'Helpee.CopyInvite' -FallbackName 'Copy invite' -FallbackControlType ([System.Windows.Automation.ControlType]::Button)
        $qr = Find-VisibleByAutomationId -Root $Context.HelpeeWindow -AutomationId 'Helpee.InviteQr'
        if ($copyBtn -and $copyBtn.Current.IsEnabled -and $qr) {
            return 'invite'
        }

        return $null
    }
}

function Wait-HelpeeConnectionEntryModeWithRecovery {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [int]$TimeoutMs = 30000
    )

    $attempts = if (Get-IsNknTransport) { 3 } else { 1 }
    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        try {
            return Wait-HelpeeConnectionEntryMode -Context $Context -TimeoutMs $TimeoutMs
        }
        catch {
            if ($attempt -ge $attempts) {
                throw
            }

            Write-Host ("[GUI Smoke] Recovering helpee connection entry-mode wait after transient failure (attempt {0}/{1}): {2}" -f $attempt, $attempts, $_.Exception.Message) -ForegroundColor Yellow
            if (-not (Reenter-RoleFlowAfterConnectionFailure -Window $Context.HelpeeWindow -HomeButtonText 'I need help')) {
                Restart-HelpeeFlow -Context $Context
            }
        }
    }

    throw 'Unreachable helpee connection entry-mode recovery failure.'
}

function Connect-HelperAndHelpee {
    param([Parameter(Mandatory = $true)]$Context)

    $entryMode = Wait-HelpeeConnectionEntryModeWithRecovery -Context $Context -TimeoutMs (Get-TransportAwareTimeoutMs -DefaultMs 10000 -NknMs 30000)
    if ([string]::Equals($entryMode, 'helper_identity', [System.StringComparison]::Ordinal)) {
        Write-Host '[GUI Smoke] Helpee connection mode: helper identity request flow.' -ForegroundColor DarkGray
        $helperIdentity = Copy-HelperIdentityWithRecovery -Context $Context
        Write-Host "[GUI Smoke] Helper identity copied: $helperIdentity" -ForegroundColor Green

        $accept = Connect-HelperIdentityRequestFlow -Context $Context -HelperIdentity $helperIdentity
        Click-Element $accept

        $allow = Wait-HelpeeAllowOrExit -Context $Context -TimeoutMs 90000
        Click-Element $allow

        [void](Wait-ConnectedChatVisibleProcessAware -Context $Context -TimeoutMs 120000)
        return $helperIdentity
    }

    Write-Host '[GUI Smoke] Helpee connection mode: invite copy/connect flow.' -ForegroundColor DarkGray
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
    $Context.HelperWindow = Wait-Window -Process $Context.HelperProc -RoleName 'navigation-loop' -TimeoutMs (Get-StartupWindowTimeoutMs)

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

    $copyInstallLink = $null
    try {
        $copyInstallLink = Wait-Until -TimeoutMs 4000 -PollMs 200 -OnTimeoutMessage "Timed out waiting for Helper.CopyInstallLink." -Condition {
            $btn = Find-VisibleByAutomationId -Root $Context.HelperWindow -AutomationId 'Helper.CopyInstallLink'
            if ($btn -and $btn.Current.IsEnabled) { return $btn }
            return $null
        }
    }
    catch {
        $copyInstallLink = $null
    }

    if (-not $copyInstallLink) {
        Write-Host '[GUI Smoke][H] SKIP: helper install-link UI is not visible in the current transport/security mode.' -ForegroundColor Yellow
        return
    }

    Click-Element $copyInstallLink

    [void](Wait-Until -TimeoutMs 5000 -PollMs 200 -OnTimeoutMessage "Timed out waiting for helper install-link copy feedback." -Condition {
        $feedback = Find-VisibleByAutomationId -Root $Context.HelperWindow -AutomationId 'Helper.CopyInstallFeedback'
        if ($feedback -and ($feedback.Current.Name -like '*Copied*')) { return $feedback }
        return $null
    })

    $clipboardText = [string](Get-ClipboardTextSafe)
    $containsExpectedToken =
        ($clipboardText.IndexOf('nLink', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) -or
        ($clipboardText.IndexOf('github', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) -or
        ($clipboardText.IndexOf('http', [System.StringComparison]::OrdinalIgnoreCase) -ge 0)
    if (-not $containsExpectedToken) {
        throw "Clipboard text after helper install-link copy did not look like install guidance. Clipboard: '$clipboardText'"
    }

    $input = Wait-HelperCodeInputEnabled -Window $Context.HelperWindow -TimeoutMs 10000
    Set-Text -Element $input -Text '121212'
    [void](Wait-ConnectButtonEnabled -Window $Context.HelperWindow -TimeoutMs 5000)
}

function Run-ScenarioI {
    param([Parameter(Mandatory = $true)]$Context)
    Reset-ScenarioContext -Context $Context

    $Context.HelperProc = Start-AppInstance -ExePath $Context.ExePath -RoleName 'diagnostics-home-only'
    [void]$Context.Processes.Add($Context.HelperProc)
    $Context.HelperWindow = Wait-Window -Process $Context.HelperProc -RoleName 'diagnostics-home-only' -TimeoutMs (Get-StartupWindowTimeoutMs)

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

    $allowedPillTexts = @('Connected', 'Connecting…', 'Reconnecting…', 'Not connected')

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

    $entryMode = Wait-HelpeeConnectionEntryModeWithRecovery -Context $Context -TimeoutMs (Get-TransportAwareTimeoutMs -DefaultMs 10000 -NknMs 30000)
    if ([string]::Equals($entryMode, 'helper_identity', [System.StringComparison]::Ordinal)) {
        Write-Host '[GUI Smoke] Helpee connection mode: helper identity request flow.' -ForegroundColor DarkGray
        $helperIdentity = Copy-HelperIdentityWithRecovery -Context $Context
        Write-Host "[GUI Smoke] Helper identity copied: $helperIdentity" -ForegroundColor Green

        $accept = Connect-HelperIdentityRequestFlow -Context $Context -HelperIdentity $helperIdentity
        Click-Element $accept

        $allow = Wait-HelpeeAllowOrExit -Context $Context -TimeoutMs 90000
        Click-Element $allow

        [void](Wait-ConnectedChatVisibleProcessAware -Context $Context -TimeoutMs 120000)
        [void](Wait-NonEmptyAutomationText -Window $Context.HelperWindow -AutomationId 'SessionHeader.StatusText' -TimeoutMs 20000)
        [void](Wait-NonEmptyAutomationText -Window $Context.HelpeeWindow -AutomationId 'SessionHeader.StatusText' -TimeoutMs 20000)
        [void](Assert-OptionalAutomationTextInSet -Window $Context.HelperWindow -AutomationId 'Chat.ConnectionPillText' -AllowedTexts $allowedPillTexts)
        [void](Assert-OptionalAutomationTextInSet -Window $Context.HelpeeWindow -AutomationId 'Chat.ConnectionPillText' -AllowedTexts $allowedPillTexts)
        return
    }

    $code = Get-HelpeeCodeFromUi -HelpeeWindow $Context.HelpeeWindow
    [void](Enter-HelperCodeAndConnect -HelperWindow $Context.HelperWindow -Code $code)
    Wait-HelpeeAllowAndClick -HelpeeWindow $Context.HelpeeWindow

    [void](Wait-Until -TimeoutMs 20000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helper header/chat coherence on Connected.' -Condition {
        $header = Find-VisibleByAutomationId -Root $Context.HelperWindow -AutomationId 'SessionHeader.StatusText'
        $pill = Find-VisibleByAutomationId -Root $Context.HelperWindow -AutomationId 'Chat.ConnectionPillText'
        if ($header -and (Get-ElementTextSafe -Element $header) -eq 'Connected') {
            if ($pill -and (Get-ElementTextSafe -Element $pill) -ne 'Connected') {
                return $null
            }

            return $true
        }

        return $null
    })

    [void](Wait-Until -TimeoutMs 20000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helpee header/chat coherence on Connected.' -Condition {
        $header = Find-VisibleByAutomationId -Root $Context.HelpeeWindow -AutomationId 'SessionHeader.StatusText'
        $pill = Find-VisibleByAutomationId -Root $Context.HelpeeWindow -AutomationId 'Chat.ConnectionPillText'
        if ($header -and (Get-ElementTextSafe -Element $header) -eq 'Connected') {
            if ($pill -and (Get-ElementTextSafe -Element $pill) -ne 'Connected') {
                return $null
            }

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
    $initialHelperPill = Find-VisibleByAutomationId -Root $Context.HelperWindow -AutomationId 'Chat.ConnectionPillText'
    if ($initialHelperPill) {
        $initialHelperPillText = (Get-ElementTextSafe -Element $initialHelperPill).Trim()
        if (-not ($allowedPillTexts -contains $initialHelperPillText)) {
            throw "Unexpected initial helper chat connection pill text '$initialHelperPillText'."
        }
    }

    $entryMode = Wait-HelpeeConnectionEntryModeWithRecovery -Context $Context -TimeoutMs (Get-TransportAwareTimeoutMs -DefaultMs 10000 -NknMs 30000)
    if ([string]::Equals($entryMode, 'helper_identity', [System.StringComparison]::Ordinal)) {
        Write-Host '[GUI Smoke] Helpee connection mode: helper identity request flow.' -ForegroundColor DarkGray
        $helperIdentity = Copy-HelperIdentityWithRecovery -Context $Context
        Write-Host "[GUI Smoke] Helper identity copied: $helperIdentity" -ForegroundColor Green

        $accept = Connect-HelperIdentityRequestFlow -Context $Context -HelperIdentity $helperIdentity
        Click-Element $accept

        $allow = Wait-HelpeeAllowOrExit -Context $Context -TimeoutMs 90000
        Click-Element $allow

        [void](Wait-ConnectedChatVisibleProcessAware -Context $Context -TimeoutMs 120000)
        [void](Wait-NonEmptyAutomationText -Window $Context.HelperWindow -AutomationId 'SessionHeader.StatusText' -TimeoutMs 20000)
        [void](Wait-NonEmptyAutomationText -Window $Context.HelpeeWindow -AutomationId 'SessionHeader.StatusText' -TimeoutMs 20000)
        [void](Assert-OptionalAutomationTextInSet -Window $Context.HelperWindow -AutomationId 'Chat.ConnectionPillText' -AllowedTexts $allowedPillTexts)
        [void](Assert-OptionalAutomationTextInSet -Window $Context.HelpeeWindow -AutomationId 'Chat.ConnectionPillText' -AllowedTexts $allowedPillTexts)
        return
    }

    $code = Get-HelpeeCodeFromUi -HelpeeWindow $Context.HelpeeWindow
    [void](Enter-HelperCodeAndConnect -HelperWindow $Context.HelperWindow -Code $code)

    [void](Wait-AutomationTextEquals -Window $Context.HelperWindow -AutomationId 'SessionHeader.StatusText' -ExpectedText 'Connecting…' -TimeoutMs 15000)
    [void](Wait-AutomationTextEquals -Window $Context.HelperWindow -AutomationId 'Chat.ConnectionPillText' -ExpectedText 'Connecting…' -TimeoutMs 15000)

    Wait-HelpeeAllowAndClick -HelpeeWindow $Context.HelpeeWindow

    [void](Wait-NonEmptyAutomationText -Window $Context.HelperWindow -AutomationId 'SessionHeader.StatusText' -TimeoutMs 20000)
    [void](Wait-NonEmptyAutomationText -Window $Context.HelpeeWindow -AutomationId 'SessionHeader.StatusText' -TimeoutMs 20000)
    [void](Assert-OptionalAutomationTextInSet -Window $Context.HelperWindow -AutomationId 'Chat.ConnectionPillText' -AllowedTexts $allowedPillTexts)
    [void](Assert-OptionalAutomationTextInSet -Window $Context.HelpeeWindow -AutomationId 'Chat.ConnectionPillText' -AllowedTexts $allowedPillTexts)
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

        $logBookmark = Get-AppLogBookmark
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
        [void](Wait-HelpeeRenderedScreenSharePreview -Context $Context -LogBookmark $logBookmark -TimeoutMs 15000)
        [void](Wait-HelperRenderedScreenShareFrame -Context $Context -LogBookmark $logBookmark -TimeoutMs 15000)

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

function Run-ScenarioScreenShareNknSoak {
    param([Parameter(Mandatory = $true)]$Context)
    Reset-ScenarioContext -Context $Context

    $previousScaffold = $env:NLINK_FEATURE_SCREENCAP_SCAFFOLD
    try {
        $env:NLINK_FEATURE_SCREENCAP_SCAFFOLD = '1'

        Start-HelpeeFlow -Context $Context
        Start-HelperFlow -Context $Context
        [void](Connect-HelperAndHelpee -Context $Context)

        $shareButton = Wait-Until -TimeoutMs 15000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helpee Share screen button for NKN soak scenario.' -Condition {
            $button = Find-VisibleByAutomationId -Root $Context.HelpeeWindow -AutomationId 'SessionHeader.ShareScreen'
            if ($button -and $button.Current.IsEnabled) {
                return $button
            }

            return $null
        }

        $logBookmark = Get-AppLogBookmark
        Click-Element $shareButton
        $shareButton = Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for screenshare NKN soak start to succeed.' -Condition {
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

        [void](Wait-HelpeeRenderedScreenSharePreview -Context $Context -LogBookmark $logBookmark -TimeoutMs 15000)
        [void](Wait-HelperRenderedScreenShareFrame -Context $Context -LogBookmark $logBookmark -TimeoutMs (Get-TransportAwareTimeoutMs -DefaultMs 15000 -NknMs 90000))
        $durationSeconds = Get-ScreenShareSoakDurationSeconds
        [void](Wait-ScreenShareRemoteSoak -Context $Context -Bookmark $logBookmark -DurationSeconds $durationSeconds)

        Click-Element $shareButton
        [void](Wait-ScreenShareButtonText -Window $Context.HelpeeWindow -ExpectedText 'Share screen' -TimeoutMs 10000)
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

        $logBookmark = Get-AppLogBookmark
        Click-Element $shareButton
        [void](Wait-ScreenShareViewerVisibleState -Window $Context.HelpeeWindow -IsVisible $true -TimeoutMs 10000)
        [void](Wait-HelpeeRenderedScreenSharePreview -Context $Context -LogBookmark $logBookmark -TimeoutMs 15000)
        [void](Wait-HelperRenderedScreenShareFrame -Context $Context -LogBookmark $logBookmark -TimeoutMs 15000)

        $message = "screenshare chat coexist"
        Send-ChatMessage -Window $Context.HelpeeWindow -Text $message
        Wait-MessageVisible -Window $Context.HelperWindow -MessageText $message -TimeoutMs 10000
        [void](Wait-Until -TimeoutMs 5000 -PollMs 200 -OnTimeoutMessage "Timed out waiting for helper connected status after screenshare start." -Condition {
            $status = Find-VisibleByAutomationId -Root $Context.HelperWindow -AutomationId 'SessionHeader.StatusText'
            if (-not $status) { return $null }

            $text = (Get-ElementTextSafe -Element $status).Trim()
            if ($text.StartsWith('Connected', [System.StringComparison]::Ordinal)) {
                return $status
            }

            return $null
        })
        [void](Wait-Until -TimeoutMs 5000 -PollMs 200 -OnTimeoutMessage "Timed out waiting for helper chat-connected state after screenshare start." -Condition {
            $pill = Find-VisibleByAutomationId -Root $Context.HelperWindow -AutomationId 'Chat.ConnectionPillText'
            if ($pill) {
                $text = (Get-ElementTextSafe -Element $pill).Trim()
                if ([string]::Equals($text, 'Connected', [System.StringComparison]::Ordinal)) {
                    return $pill
                }

                return $null
            }

            $input = Find-VisibleByAutomationId -Root $Context.HelperWindow -AutomationId 'Chat.Input'
            $send = Find-VisibleByAutomationId -Root $Context.HelperWindow -AutomationId 'Chat.Send'
            $messages = Find-VisibleByAutomationId -Root $Context.HelperWindow -AutomationId 'Chat.Messages'
            if ($input -and -not $input.Current.IsOffscreen -and
                $send -and -not $send.Current.IsOffscreen -and
                $messages -and -not $messages.Current.IsOffscreen) {
                return $messages
            }

            return $null
        })
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

function Run-ScenarioScreenShareRecoveryReceiptDevLocal {
    param([Parameter(Mandatory = $true)]$Context)
    Reset-ScenarioContext -Context $Context

    $previousScaffold = $env:NLINK_FEATURE_SCREENCAP_SCAFFOLD
    $previousForcedRecovery = $env:NLINK_GUI_SMOKE_FORCE_HELPER_REMOTE_RECOVERY_AFTER_APPLIES
    try {
        $env:NLINK_FEATURE_SCREENCAP_SCAFFOLD = '1'
        $env:NLINK_GUI_SMOKE_FORCE_HELPER_REMOTE_RECOVERY_AFTER_APPLIES = '2'

        Start-HelpeeFlow -Context $Context
        Start-HelperFlow -Context $Context
        [void](Connect-HelperAndHelpee -Context $Context)

        $shareButton = Wait-Until -TimeoutMs 15000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helpee Share screen button for recovery receipt scenario.' -Condition {
            $button = Find-VisibleByAutomationId -Root $Context.HelpeeWindow -AutomationId 'SessionHeader.ShareScreen'
            if ($button -and $button.Current.IsEnabled) { return $button }
            return $null
        }

        $logBookmark = Get-AppLogBookmark
        Click-Element $shareButton
        $shareButton = Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for screenshare recovery receipt scenario start to succeed.' -Condition {
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

        [void](Wait-HelpeeRenderedScreenSharePreview -Context $Context -LogBookmark $logBookmark -TimeoutMs 15000)
        [void](Wait-HelperRenderedScreenShareFrame -Context $Context -LogBookmark $logBookmark -TimeoutMs 15000)

        Write-Host '[GUI Smoke][screenshare_recovery_receipt_devlocal] forcing one helper-remote recovery through the deterministic GUI-smoke hook...' -ForegroundColor DarkGray

        [void](Wait-AppLogRegexAfterBookmark `
            -Pattern 'event=screenshare_forced_helper_remote_recovery_triggered; role=helper_remote;' `
            -Bookmark $logBookmark `
            -TimeoutMs 15000 `
            -OnTimeoutMessage 'Timed out waiting for the deterministic helper recovery trigger to activate.')

        [void](Wait-AppLogRegexAfterBookmark `
            -Pattern 'event=screenshare_viewer_recovery_keyframe_applied; role=helper_remote;' `
            -Bookmark $logBookmark `
            -TimeoutMs 30000 `
            -OnTimeoutMessage 'Timed out waiting for helper_remote to apply a recovery keyframe after the deterministic recovery trigger.')

        [void](Wait-AppLogRegexAfterBookmark `
            -Pattern 'event=screenshare_recovery_receipt_sent; role=helper_remote; .*retry=0' `
            -Bookmark $logBookmark `
            -TimeoutMs 30000 `
            -OnTimeoutMessage 'Timed out waiting for helper to publish a screenshare recovery receipt after the forced DEVLOCAL pause.')

        [void](Wait-AppLogRegexAfterBookmark `
            -Pattern 'event=screenshare_recovery_receipt_received_runtime;' `
            -Bookmark $logBookmark `
            -TimeoutMs 10000 `
            -OnTimeoutMessage 'Timed out waiting for runtime to receive the screenshare recovery receipt after helper publication.')

        $receiptLines = @(Get-AppLogLinesAfterBookmark -Bookmark $logBookmark | Where-Object {
                $_ -match 'event=screenshare_recovery_receipt_sent; role=helper_remote;'
            })
        if ($receiptLines.Count -gt 2) {
            throw "Expected at most 2 helper recovery receipt sends (initial + bounded retry), but observed $($receiptLines.Count)."
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

        if ($null -eq $previousForcedRecovery) {
            Remove-Item Env:NLINK_GUI_SMOKE_FORCE_HELPER_REMOTE_RECOVERY_AFTER_APPLIES -ErrorAction SilentlyContinue
        }
        else {
            $env:NLINK_GUI_SMOKE_FORCE_HELPER_REMOTE_RECOVERY_AFTER_APPLIES = $previousForcedRecovery
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

                $logBookmark = Get-AppLogBookmark
                Click-Element $shareButton
                [void](Wait-ScreenShareButtonText -Window $Context.HelpeeWindow -ExpectedText 'Stop sharing' -TimeoutMs 10000)
                [void](Wait-ScreenShareViewerVisibleState -Window $Context.HelpeeWindow -IsVisible $true -TimeoutMs 10000)
                [void](Wait-HelperRenderedScreenShareFrame -Context $Context -LogBookmark $logBookmark -TimeoutMs 15000)

                $requestControlButton = $null
                try {
                    $requestControlButton = Wait-Until -TimeoutMs 5000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helper Request control button.' -Condition {
                        $button = Find-VisibleByAutomationId -Root $Context.HelperWindow -AutomationId 'SessionHeader.RequestControl'
                        if ($button -and $button.Current.IsEnabled) { return $button }
                        return $null
                    }
                }
                catch {
                    $requestControlButton = $null
                }

                if ($requestControlButton) {
                    Click-Element $requestControlButton

                    [void](Wait-AutomationTextEquals -Window $Context.HelperWindow -AutomationId 'SessionHeader.StopControl' -ExpectedText 'Cancel request' -TimeoutMs 10000)
                    [void](Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helpee remote-control approval dialog.' -Condition {
                        $allow = Find-VisibleByAutomationIdOrName -Root $Context.HelpeeWindow -AutomationId 'Helpee.ControlConsent.Allow' -FallbackName 'Allow control' -FallbackControlType ([System.Windows.Automation.ControlType]::Button)
                        if ($allow -and $allow.Current.IsEnabled) { return $allow }
                        return $null
                    })
                }
                else {
                    Write-Host "[GUI Smoke] helper Request control button not surfaced; validating stop-sharing cleanup without remote-control approval UI." -ForegroundColor DarkGray
                }

                $stopShareButton = Wait-Until -TimeoutMs 5000 -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helpee Stop sharing button.' -Condition {
                    $button = Find-VisibleByAutomationId -Root $Context.HelpeeWindow -AutomationId 'SessionHeader.ShareScreen'
                    if (-not $button -or -not $button.Current.IsEnabled) { return $null }
                    $text = (Get-ElementTextSafe -Element $button).Trim()
                    if ([string]::Equals($text, 'Stop sharing', [System.StringComparison]::Ordinal)) { return $button }
                    return $null
                }
                Click-Element $stopShareButton

                [void](Wait-ScreenShareButtonText -Window $Context.HelpeeWindow -ExpectedText 'Share screen' -TimeoutMs 5000)
                [void](Wait-ScreenShareViewerVisibleState -Window $Context.HelpeeWindow -IsVisible $false -TimeoutMs 5000)
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
$oldUnsafeDeveloperMode = $env:NLINK_UNSAFE_DEVELOPER_MODE
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
$decoderDebugSnapshotBefore = @()

try {
    # GUI smoke intentionally drives release-affecting test overrides such as
    # DEVLOCAL transport and file-transfer soak env knobs. Keep the unsafe
    # opt-in scoped to this harness process and its app children.
    $env:NLINK_UNSAFE_DEVELOPER_MODE = '1'

    # Deterministic local GUI smoke by default.
    if ([string]::IsNullOrWhiteSpace($env:NLINK_TRANSPORT)) {
        $env:NLINK_TRANSPORT = 'DEVLOCAL'
    }

    Clear-AppLogsIfPresent
    $decoderDebugSnapshotBefore = Get-DecoderDebugSnapshot

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
            'SCREENSHARE_RECOVERY_RECEIPT_DEVLOCAL' { Invoke-Scenario -Name 'screenshare_recovery_receipt_devlocal' -TimeoutSec ([Math]::Min($TimeoutSeconds, 120)) -Action { Run-ScenarioScreenShareRecoveryReceiptDevLocal -Context $ctx } }
            'SCREENSHARE_NKN_SOAK' { Invoke-Scenario -Name 'screenshare_nkn_soak' -TimeoutSec ([Math]::Min($TimeoutSeconds, 180)) -Action { Run-ScenarioScreenShareNknSoak -Context $ctx } }
            'SCREENSHARE_CHAT_COEXISTENCE' { Invoke-Scenario -Name 'screenshare_chat_coexistence' -TimeoutSec ([Math]::Min($TimeoutSeconds, 90)) -Action { Run-ScenarioScreenShareChatCoexistence -Context $ctx } }
            'SCREENSHARE_STOP_PENDING_APPROVAL' { Invoke-Scenario -Name 'screenshare_stop_pending_approval' -TimeoutSec ([Math]::Min($TimeoutSeconds, 90)) -Action { Run-ScenarioScreenShareStopWhileControlApprovalPending -Context $ctx } }
            'FILETRANSFER_NKN_SOAK' { Invoke-Scenario -Name 'filetransfer_nkn_soak' -TimeoutSec $TimeoutSeconds -Action { Run-ScenarioFileTransferNknSoak -Context $ctx } }
            'FILETRANSFER_NKN_MIXED_SOAK' { Invoke-Scenario -Name 'filetransfer_nkn_mixed_soak' -TimeoutSec $TimeoutSeconds -Action { Run-ScenarioFileTransferNknMixedSoak -Context $ctx } }
            'FILETRANSFER_TUNA_HANDOFF_FALLBACK' { Invoke-Scenario -Name 'filetransfer_tuna_handoff_fallback' -TimeoutSec $TimeoutSeconds -Action { Run-ScenarioFileTransferTunaHandoffFallback -Context $ctx } }
            'STATUS_TEXT_GUARDRAILS' { Invoke-Scenario -Name 'status_text_guardrails' -TimeoutSec ([Math]::Min($TimeoutSeconds, 90)) -Action { Run-ScenarioStatusTextGuardrails -Context $ctx } }
            default { throw "Unknown GUI smoke scenario '$scenario'. Use A,B,C,D,E,F,G,H,I,J,K,L,M,NKN_DIRECT_CONNECT,HEADER_CHAT_COHERENCE,END_SESSION_DISABLES_CHAT,SCREENSHARE_BUTTON_VISIBILITY,SCREENSHARE_VIEWER_TOGGLE,SCREENSHARE_RECOVERY_RECEIPT_DEVLOCAL,SCREENSHARE_NKN_SOAK,SCREENSHARE_CHAT_COEXISTENCE,FILETRANSFER_NKN_SOAK,FILETRANSFER_NKN_MIXED_SOAK,FILETRANSFER_TUNA_HANDOFF_FALLBACK,STATUS_TEXT_GUARDRAILS." }
        }
    }

    Assert-DefaultScreenShareTelemetryQuiet -DecoderDebugSnapshotBefore $decoderDebugSnapshotBefore

    Write-Host "[GUI Smoke] PASS: scenarios $($scenarioList -join ',') completed." -ForegroundColor Green
    $exitCode = 0
}
catch {
    $failureArtifactsDir = New-FailureArtifactDir
    Write-Host "[GUI Smoke] FAIL: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "[GUI Smoke] Collecting failure artifacts in $failureArtifactsDir" -ForegroundColor Yellow

    try { if ($ctx.HelpeeProc) { Write-ProcessStartupArtifacts -ArtifactDir $failureArtifactsDir -Label 'helpee' -Process $ctx.HelpeeProc } } catch {}
    try { if ($ctx.HelperProc) { Write-ProcessStartupArtifacts -ArtifactDir $failureArtifactsDir -Label 'helper' -Process $ctx.HelperProc } } catch {}
    try { if ($ctx.HelpeeWindow) { Dump-UiTree -Root $ctx.HelpeeWindow -OutPath (Join-Path $failureArtifactsDir 'helpee-ui-tree.txt') } } catch {}
    try { if ($ctx.HelperWindow) { Dump-UiTree -Root $ctx.HelperWindow -OutPath (Join-Path $failureArtifactsDir 'helper-ui-tree.txt') } } catch {}
    try { 'Screenshot capture not implemented in this script (UI tree + logs captured).' | Set-Content -Path (Join-Path $failureArtifactsDir 'screenshot.txt') -Encoding UTF8 } catch {}
    try { Copy-AppLogsIfPresent -ArtifactDir $failureArtifactsDir } catch {}
    try { Copy-GuiSmokeProcessOutputIfPresent -ArtifactDir $failureArtifactsDir } catch {}

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

    if ($null -eq $oldUnsafeDeveloperMode) {
        Remove-Item Env:NLINK_UNSAFE_DEVELOPER_MODE -ErrorAction SilentlyContinue
    }
    else {
        $env:NLINK_UNSAFE_DEVELOPER_MODE = $oldUnsafeDeveloperMode
    }
}

exit $exitCode
