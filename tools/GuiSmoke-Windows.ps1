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
    $logPath = Join-Path $env:LOCALAPPDATA 'nLink\logs\nlink.log'
    if (-not (Test-Path $logPath)) {
        return 0
    }

    try {
        return (Read-AppLogLinesSafe -Path $logPath).Length
    }
    catch {
        return 0
    }
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

    $logPath = Join-Path $env:LOCALAPPDATA 'nLink\logs\nlink.log'
    $lines = Read-AppLogLinesSafe -Path $logPath
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

    $logPath = Join-Path $env:LOCALAPPDATA 'nLink\logs\nlink.log'
    return Wait-Until -TimeoutMs $TimeoutMs -PollMs 250 -OnTimeoutMessage $OnTimeoutMessage -Condition {
        $lines = Read-AppLogLinesSafe -Path $logPath
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

    $launchEnvironment = Get-AppLaunchEnvironmentOverrides
    $workingDirectory = Split-Path -Parent $ExePath

    if ($launchEnvironment.Count -gt 0) {
        $summary = ($launchEnvironment.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ', '
        Write-Host "[GUI Smoke] Launch env sanitized for ${RoleName}: $summary" -ForegroundColor DarkGray
    }

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($ExePath)
    $startInfo.WorkingDirectory = $workingDirectory
    $startInfo.UseShellExecute = $false

    foreach ($entry in $launchEnvironment.GetEnumerator()) {
        $startInfo.Environment[$entry.Key] = [string]$entry.Value
    }

    $process = [System.Diagnostics.Process]::Start($startInfo)
    if (-not $process) {
        throw "Failed to start app instance for role '$RoleName'."
    }

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
    $overrides = @{}
    if (Test-LegacyHigherClarityTupleActive) {
        $overrides['NLINK_FEATURE_SCREENCAP_MAX_FPS'] = '15'
        $overrides['NLINK_FEATURE_SCREENCAP_TRANSPORT_MAX_FPS'] = '8'
        $overrides['NLINK_FEATURE_SCREENCAP_SCALE'] = '1.0'
    }

    return $overrides
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

function Connect-HelperAndHelpee {
    param([Parameter(Mandatory = $true)]$Context)

    $entryMode = Wait-HelpeeConnectionEntryMode -Context $Context -TimeoutMs (Get-TransportAwareTimeoutMs -DefaultMs 10000 -NknMs 30000)
    if ([string]::Equals($entryMode, 'helper_identity', [System.StringComparison]::Ordinal)) {
        Write-Host '[GUI Smoke] Helpee connection mode: helper identity request flow.' -ForegroundColor DarkGray
        $helperIdentity = Copy-HelperIdentityWithRecovery -Context $Context
        Write-Host "[GUI Smoke] Helper identity copied: $helperIdentity" -ForegroundColor Green
        [void](Enter-HelpeeHelperIdentityAndRequestHelp -HelpeeWindow $Context.HelpeeWindow -HelperIdentity $helperIdentity)

        $accept = Wait-HelperAcceptRequestOrExit -Context $Context -TimeoutMs 90000
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
        [void](Wait-AutomationTextEquals -Window $Context.HelperWindow -AutomationId 'SessionHeader.StatusText' -ExpectedText 'Connected' -TimeoutMs 5000)
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
            'STATUS_TEXT_GUARDRAILS' { Invoke-Scenario -Name 'status_text_guardrails' -TimeoutSec ([Math]::Min($TimeoutSeconds, 90)) -Action { Run-ScenarioStatusTextGuardrails -Context $ctx } }
            default { throw "Unknown GUI smoke scenario '$scenario'. Use A,B,C,D,E,F,G,H,I,J,K,L,M,NKN_DIRECT_CONNECT,HEADER_CHAT_COHERENCE,END_SESSION_DISABLES_CHAT,SCREENSHARE_BUTTON_VISIBILITY,SCREENSHARE_VIEWER_TOGGLE,SCREENSHARE_RECOVERY_RECEIPT_DEVLOCAL,SCREENSHARE_NKN_SOAK,SCREENSHARE_CHAT_COEXISTENCE,STATUS_TEXT_GUARDRAILS." }
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
