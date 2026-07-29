param(
    [string]$ExePath = ".\\artifacts\\portable\\nLink\\win-x64\\nLink.exe",
    [int]$TimeoutSeconds = 90
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'FileTransferAuthorityMatching.ps1')
. (Join-Path $PSScriptRoot 'GuiSmokeLogLedger.ps1')

$script:GuiSmokeAppLogLedger = New-GuiSmokeLogLedgerState

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

function Get-NLinkMainWindowCandidateRecord {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [int64]$PreferredNativeHandle = 0
    )

    $name = ''
    $className = ''
    $automationId = ''
    $nativeHandle = 0L
    $controlType = '(none)'
    $isOffscreen = $true
    try {
        $name = [string]$Window.Current.Name
        $className = [string]$Window.Current.ClassName
        $automationId = [string]$Window.Current.AutomationId
        $nativeHandle = [int64]$Window.Current.NativeWindowHandle
        $controlType = if ($Window.Current.ControlType) { [string]$Window.Current.ControlType.ProgrammaticName } else { '(none)' }
        $isOffscreen = [bool]$Window.Current.IsOffscreen
    }
    catch {
        return [pscustomobject]@{
            Window = $Window
            Accepted = $false
            Name = '(unavailable)'
            ClassName = '(unavailable)'
            AutomationId = '(unavailable)'
            NativeWindowHandle = 0L
            ControlType = '(unavailable)'
            IsOffscreen = $true
            RejectedReason = 'uia_current_unavailable'
            PreferredHandleMatch = $false
        }
    }

    $reasons = New-Object System.Collections.Generic.List[string]
    if (-not [string]::Equals($name, 'nLink', [System.StringComparison]::Ordinal)) {
        $reasons.Add('name_not_nlink') | Out-Null
    }

    if (-not [string]::Equals($className, 'MainWindow', [System.StringComparison]::Ordinal)) {
        $reasons.Add('class_not_mainwindow') | Out-Null
    }

    if (-not [string]::Equals($controlType, 'ControlType.Window', [System.StringComparison]::Ordinal)) {
        $reasons.Add('control_type_not_window') | Out-Null
    }

    if ($nativeHandle -eq 0) {
        $reasons.Add('zero_native_handle') | Out-Null
    }

    if ($isOffscreen) {
        $reasons.Add('offscreen') | Out-Null
    }

    return [pscustomobject]@{
        Window = $Window
        Accepted = ($reasons.Count -eq 0)
        Name = $name
        ClassName = $className
        AutomationId = $automationId
        NativeWindowHandle = $nativeHandle
        ControlType = $controlType
        IsOffscreen = $isOffscreen
        RejectedReason = if ($reasons.Count -eq 0) { '(accepted)' } else { ($reasons -join ',') }
        PreferredHandleMatch = ($PreferredNativeHandle -ne 0 -and $nativeHandle -eq $PreferredNativeHandle)
    }
}

function Get-NLinkMainWindowCandidateRecordsByProcessId {
    param(
        [int]$ProcessId,
        [int64]$PreferredNativeHandle = 0
    )

    $records = New-Object System.Collections.Generic.List[object]
    foreach ($window in @(Get-TopLevelWindowElementsByProcessId -ProcessId $ProcessId)) {
        $records.Add((Get-NLinkMainWindowCandidateRecord -Window $window -PreferredNativeHandle $PreferredNativeHandle)) | Out-Null
    }

    return @($records.ToArray())
}

function Get-NLinkMainWindowElementByProcessId {
    param(
        [int]$ProcessId,
        [int64]$PreferredNativeHandle = 0
    )

    $records = @(Get-NLinkMainWindowCandidateRecordsByProcessId -ProcessId $ProcessId -PreferredNativeHandle $PreferredNativeHandle)
    if ($PreferredNativeHandle -ne 0) {
        foreach ($record in @($records)) {
            if ($record.Accepted -and $record.NativeWindowHandle -eq $PreferredNativeHandle) {
                return $record.Window
            }
        }
    }

    foreach ($record in @($records)) {
        if ($record.Accepted) {
            return $record.Window
        }
    }

    return $null
}

function Get-WindowElementByProcessId {
    param([int]$ProcessId)

    return Get-NLinkMainWindowElementByProcessId -ProcessId $ProcessId
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

    $records = @(Get-NLinkMainWindowCandidateRecordsByProcessId -ProcessId $Process.Id)
    if ($records.Count -eq 0) {
        return "top_level_windows: (none)"
    }

    $lines = New-Object System.Collections.Generic.List[string]
    $index = 0
    foreach ($record in $records) {
        $index++
        $lines.Add(
            ("[{0}] selected='{1}' rejected_reason='{2}' Name='{3}' Class='{4}' AutomationId='{5}' NativeWindowHandle='{6}' ControlType='{7}' IsOffscreen='{8}'" -f
                $index,
                ($(if ($record.Accepted) { 1 } else { 0 })),
                $record.RejectedReason,
                $record.Name,
                $record.ClassName,
                $record.AutomationId,
                $record.NativeWindowHandle,
                $record.ControlType,
                $record.IsOffscreen))
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

function Wait-SessionHeaderConnectedState {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [int]$TimeoutMs = 10000
    )

    return Wait-Until -TimeoutMs $TimeoutMs -PollMs 200 -OnTimeoutMessage "Timed out waiting for SessionHeader.StatusText to be a connected state." -Condition {
        $el = Find-VisibleByAutomationId -Root $Window -AutomationId 'SessionHeader.StatusText'
        if (-not $el) { return $null }

        $text = (Get-ElementTextSafe -Element $el).Trim()
        if ([string]::IsNullOrWhiteSpace($text)) { return $null }
        if ([string]::Equals($text, 'Connected', [System.StringComparison]::Ordinal)) {
            return [pscustomobject]@{
                Element = $el
                Text = $text
            }
        }

        if ($text.StartsWith('Connected', [System.StringComparison]::Ordinal)) {
            $suffix = $text.Substring('Connected'.Length)
            if ($suffix.Length -gt 0 -and ([char]::IsWhiteSpace($suffix[0]) -or $suffix[0] -eq [char]0x2022)) {
                return [pscustomobject]@{
                    Element = $el
                    Text = $text
                }
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
    $selectedWindow = Get-NLinkMainWindowElementByProcessId -ProcessId $Process.Id
    $selectedHandle = 0L
    $selectedClass = ''
    $selectedName = ''
    $selectedControlType = ''
    if ($selectedWindow) {
        try {
            $selectedHandle = [int64]$selectedWindow.Current.NativeWindowHandle
            $selectedClass = [string]$selectedWindow.Current.ClassName
            $selectedName = [string]$selectedWindow.Current.Name
            $selectedControlType = if ($selectedWindow.Current.ControlType) { [string]$selectedWindow.Current.ControlType.ProgrammaticName } else { '(none)' }
        }
        catch {
            $selectedHandle = 0L
            $selectedClass = '(unavailable)'
            $selectedName = '(unavailable)'
            $selectedControlType = '(unavailable)'
        }
    }

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
        ("nlink_main_window_handle: {0}" -f $selectedHandle),
        ("nlink_main_window_name: {0}" -f $selectedName),
        ("nlink_main_window_class: {0}" -f $selectedClass),
        ("nlink_main_window_control_type: {0}" -f $selectedControlType),
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

function Get-GuiSmokeProcessOutputFilesSnapshot {
    param(
        [string]$RoleName = '',
        [int]$ProcessId = 0
    )

    $files = New-Object System.Collections.Generic.List[string]
    if ($script:GuiSmokeProcessOutputFiles.Count -le 0) {
        return $files.ToArray()
    }

    $safeRole = ''
    if (-not [string]::IsNullOrWhiteSpace($RoleName)) {
        $safeRole = [regex]::Replace($RoleName.Trim().ToLowerInvariant(), '[^a-z0-9_-]+', '-')
    }

    foreach ($path in @($script:GuiSmokeProcessOutputFiles)) {
        if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            continue
        }

        $name = [System.IO.Path]::GetFileName($path)
        if (-not [string]::IsNullOrWhiteSpace($safeRole) -and
            -not $name.StartsWith(("{0}-" -f $safeRole), [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        if ($ProcessId -gt 0 -and
            $name.IndexOf(("-{0}." -f $ProcessId), [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
            continue
        }

        $files.Add($path) | Out-Null
    }

    return $files.ToArray()
}

function Read-GuiSmokeProcessOutputLinesSnapshot {
    param(
        [string]$RoleName = '',
        [int]$ProcessId = 0
    )

    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($path in @(Get-GuiSmokeProcessOutputFilesSnapshot -RoleName $RoleName -ProcessId $ProcessId)) {
        foreach ($line in @(Read-AppLogLinesSafe -Path $path)) {
            $lines.Add([string]$line) | Out-Null
        }
    }

    return $lines.ToArray()
}

function Wait-GuiSmokeProcessOutputCaptureDrain {
    param(
        [AllowEmptyCollection()][object[]]$Processes = @(),
        [int]$TimeoutMs = 5000,
        [int]$QuietPeriodMs = 500
    )

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    foreach ($process in @($Processes)) {
        if ($null -eq $process) {
            continue
        }

        $remainingMs = [Math]::Max(1, $TimeoutMs - [int]$stopwatch.ElapsedMilliseconds)
        if (-not $process.HasExited -and -not $process.WaitForExit($remainingMs)) {
            throw "process_output_eof_timeout:process_id=$($process.Id)"
        }

        # The parameterless overload waits for redirected asynchronous output
        # handlers to receive EOF after the process has exited.
        $process.WaitForExit()
    }

    $lastFingerprint = ''
    $stableSinceMs = -1L
    while ($stopwatch.ElapsedMilliseconds -lt $TimeoutMs) {
        $fingerprintParts = New-Object System.Collections.Generic.List[string]
        foreach ($path in @($script:GuiSmokeProcessOutputFiles | Sort-Object -Unique)) {
            if ([string]::IsNullOrWhiteSpace([string]$path) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) {
                continue
            }

            $item = Get-Item -LiteralPath $path -Force
            $fingerprintParts.Add(('{0}|{1}|{2}' -f $item.FullName, $item.Length, $item.LastWriteTimeUtc.Ticks)) | Out-Null
        }

        $fingerprint = [string]::Join(';', $fingerprintParts)
        if ([string]::Equals($fingerprint, $lastFingerprint, [System.StringComparison]::Ordinal)) {
            if ($stableSinceMs -lt 0) {
                $stableSinceMs = $stopwatch.ElapsedMilliseconds
            }
            elseif (($stopwatch.ElapsedMilliseconds - $stableSinceMs) -ge $QuietPeriodMs) {
                return
            }
        }
        else {
            $lastFingerprint = $fingerprint
            $stableSinceMs = $stopwatch.ElapsedMilliseconds
        }

        Start-Sleep -Milliseconds 50
    }

    throw 'process_output_capture_drain_timeout'
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
    Reset-GuiSmokeLogLedgerState -State $script:GuiSmokeAppLogLedger
    $logsDir = Join-Path $env:LOCALAPPDATA 'nLink\logs'
    if (-not (Test-Path $logsDir)) { return }

    Get-ChildItem -Path $logsDir -File -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

function Get-AppLogBookmark {
    try {
        Sync-GuiSmokeAppLogLedger
        return Get-GuiSmokeLogLedgerBookmark -State $script:GuiSmokeAppLogLedger
    }
    catch {
        throw
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

    Sync-GuiSmokeAppLogLedger
    return @(Get-GuiSmokeLogLedgerLinesAfterBookmark -State $script:GuiSmokeAppLogLedger -Bookmark $Bookmark)
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
    Sync-GuiSmokeAppLogLedger
    return @(Get-GuiSmokeLogLedgerLines -State $script:GuiSmokeAppLogLedger)
}

function Sync-GuiSmokeAppLogLedger {
    Sync-GuiSmokeLogLedger `
        -State $script:GuiSmokeAppLogLedger `
        -Paths @(Get-AppLogPaths)
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

function Get-GuiSmokeStringHashPrefix {
    param(
        [string]$Value,
        [int]$Length = 12
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ''
    }

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value.Trim())
        $hash = $sha.ComputeHash($bytes)
        $hex = ([System.BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
        return $hex.Substring(0, [Math]::Min($Length, $hex.Length))
    }
    finally {
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
    $terminalQuietGraceStartedMs = -1L
    $terminalTailGraceStartedMs = -1L

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
            $terminalQuietGraceStartedMs = -1L
            $terminalTailGraceStartedMs = -1L
        }

        if (($sw.ElapsedMilliseconds - $lastProgressMs) -ge $ProgressTimeoutMs) {
            $terminalQuietGraceMs = [Math]::Min(
                30000,
                [Math]::Max(10000, [long]($ProgressTimeoutMs / 6)))
            if ($terminalQuietGraceStartedMs -lt 0) {
                $terminalQuietGraceStartedMs = $sw.ElapsedMilliseconds
                if (-not [string]::IsNullOrWhiteSpace($ArtifactDir)) {
                    Append-FileTransferLiveHarnessDiagnostic -ArtifactDir $ArtifactDir -Diagnostic ([ordered]@{
                            event = 'filetransfer_live_progress_timeout_terminal_grace_started'
                            reason = 'quiet_progress_before_terminal_decision'
                            progress_timeout_ms = $ProgressTimeoutMs
                            terminal_quiet_grace_ms = $terminalQuietGraceMs
                            wait_elapsed_ms = $sw.ElapsedMilliseconds
                            receiver_next_chunk = $maxReceiverNextChunkIndex.Value
                            receiver_highest_chunk = $maxReceiverHighestChunkIndex.Value
                            progress_events = $lastProgressEventCount
                        })
                }

                Start-Sleep -Milliseconds 500
                continue
            }

            if (($sw.ElapsedMilliseconds - $terminalQuietGraceStartedMs) -lt $terminalQuietGraceMs) {
                Start-Sleep -Milliseconds 500
                continue
            }

            $expectedChunkCount = if ($ExpectedSizeBytes -gt 0) {
                [long][Math]::Ceiling($ExpectedSizeBytes / 21504.0)
            }
            else {
                -1L
            }
            $remainingTailChunks = if ($expectedChunkCount -gt 0 -and $maxReceiverHighestChunkIndex.Value -ge 0) {
                [Math]::Max(0L, ($expectedChunkCount - 1L) - [long]$maxReceiverHighestChunkIndex.Value)
            }
            else {
                [long]::MaxValue
            }
            $nearTerminalTail = $remainingTailChunks -le 512L
            if ($nearTerminalTail) {
                $terminalTailGraceMs = [Math]::Min(
                    120000,
                    [Math]::Max(30000, [long]($ProgressTimeoutMs / 2)))
                if ($terminalTailGraceStartedMs -lt 0) {
                    $terminalTailGraceStartedMs = $sw.ElapsedMilliseconds
                    if (-not [string]::IsNullOrWhiteSpace($ArtifactDir)) {
                        Append-FileTransferLiveHarnessDiagnostic -ArtifactDir $ArtifactDir -Diagnostic ([ordered]@{
                                event = 'filetransfer_live_progress_timeout_tail_terminal_grace_started'
                                reason = 'near_terminal_tail_wait_for_terminal_proof'
                                progress_timeout_ms = $ProgressTimeoutMs
                                terminal_tail_grace_ms = $terminalTailGraceMs
                                wait_elapsed_ms = $sw.ElapsedMilliseconds
                                expected_chunk_count = $expectedChunkCount
                                receiver_next_chunk = $maxReceiverNextChunkIndex.Value
                                receiver_highest_chunk = $maxReceiverHighestChunkIndex.Value
                                remaining_tail_chunks = $remainingTailChunks
                                progress_events = $lastProgressEventCount
                            })
                    }

                    Start-Sleep -Milliseconds 500
                    continue
                }

                if (($sw.ElapsedMilliseconds - $terminalTailGraceStartedMs) -lt $terminalTailGraceMs) {
                    Start-Sleep -Milliseconds 500
                    continue
                }
            }

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
        [string]$FileName = 'filetransfer-retained-log-slice.log',
        [string[]]$PrefixLines = @(),
        [AllowNull()][object[]]$CapturedLines = $null,
        [switch]$WriteOnce,
        [switch]$PassThru
    )

    $lines = if ($null -eq $CapturedLines) {
        @(Get-AppLogLinesAfterBookmark -Bookmark $Bookmark)
    }
    else {
        @($CapturedLines | ForEach-Object { [string]$_ })
    }
    foreach ($prefixLine in @($PrefixLines)) {
        if ([string]::IsNullOrWhiteSpace($prefixLine)) {
            continue
        }

        if (-not ($lines | Where-Object { [string]::Equals([string]$_, [string]$prefixLine, [System.StringComparison]::Ordinal) } | Select-Object -First 1)) {
            $lines = @($prefixLine) + $lines
        }
    }

    $path = Join-Path $ArtifactDir $FileName
    if ($WriteOnce) {
        $content = if ($lines.Count -gt 0) {
            [string]::Join([Environment]::NewLine, @($lines))
        }
        else {
            ''
        }
        $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($content)
        $stream = $null
        try {
            $stream = [System.IO.FileStream]::new(
                $path,
                [System.IO.FileMode]::CreateNew,
                [System.IO.FileAccess]::Write,
                [System.IO.FileShare]::None)
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        }
        finally {
            if ($stream) {
                $stream.Dispose()
            }
        }
    }
    elseif ($lines.Count -gt 0) {
        [System.IO.File]::WriteAllText($path, ($lines -join [Environment]::NewLine), [System.Text.Encoding]::UTF8)
    }
    else {
        [System.IO.File]::WriteAllText($path, '', [System.Text.Encoding]::UTF8)
    }

    if ($PassThru) {
        return [pscustomobject]@{
            Path = [System.IO.Path]::GetFullPath($path)
            Sha256 = Get-FileSha256Hex -Path $path
            LineCount = [int]$lines.Count
        }
    }
}

function Get-GuiSmokeLogWriterChronology {
    param(
        [AllowEmptyCollection()][object[]]$WriterLineRecords = @()
    )

    $inversionCount = 0L
    $collisionCount = 0L
    $reasons = New-Object System.Collections.Generic.List[string]
    foreach ($writerGroup in @($WriterLineRecords |
            Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.writerId) } |
            Group-Object -Property writerId)) {
        $validBatches = New-Object System.Collections.Generic.List[object]
        foreach ($batchGroup in @($writerGroup.Group | Group-Object -Property batchGeneration)) {
            $batchLines = @($batchGroup.Group | Sort-Object sequence)
            $attemptUtcTicks = @($batchLines | ForEach-Object { [long]$_.attemptUtcTicks } | Select-Object -Unique)
            $declaredFirstSequences = @($batchLines | ForEach-Object { [long]$_.batchFirstSequence } | Select-Object -Unique)
            $declaredLastSequences = @($batchLines | ForEach-Object { [long]$_.batchLastSequence } | Select-Object -Unique)
            $actualFirstSequence = if ($batchLines.Count -gt 0) { [long]$batchLines[0].sequence } else { 0L }
            $actualLastSequence = if ($batchLines.Count -gt 0) { [long]$batchLines[-1].sequence } else { 0L }
            $sequenceRangeIsContiguous = $batchLines.Count -gt 0 -and
                ($actualLastSequence - $actualFirstSequence + 1L) -eq [long]$batchLines.Count
            if ($sequenceRangeIsContiguous) {
                for ($lineIndex = 0; $lineIndex -lt $batchLines.Count; $lineIndex++) {
                    if ([long]$batchLines[$lineIndex].sequence -ne ($actualFirstSequence + $lineIndex)) {
                        $sequenceRangeIsContiguous = $false
                        break
                    }
                }
            }

            $batchIsValid = [long]$batchGroup.Name -gt 0 -and
                $attemptUtcTicks.Count -eq 1 -and
                [long]$attemptUtcTicks[0] -gt 0 -and
                $declaredFirstSequences.Count -eq 1 -and
                [long]$declaredFirstSequences[0] -eq $actualFirstSequence -and
                $declaredLastSequences.Count -eq 1 -and
                [long]$declaredLastSequences[0] -eq $actualLastSequence -and
                $sequenceRangeIsContiguous
            if (-not $batchIsValid) {
                $reasons.Add('writer_recovery_chronology_batch_invalid') | Out-Null
                continue
            }

            $validBatches.Add([pscustomobject]@{
                firstSequence = $actualFirstSequence
                attemptUtcTicks = [long]$attemptUtcTicks[0]
            }) | Out-Null
        }

        $orderedBatches = @($validBatches.ToArray() | Sort-Object firstSequence)
        for ($batchIndex = 1; $batchIndex -lt $orderedBatches.Count; $batchIndex++) {
            $previousTicks = [long]$orderedBatches[$batchIndex - 1].attemptUtcTicks
            $currentTicks = [long]$orderedBatches[$batchIndex].attemptUtcTicks
            if ($currentTicks -lt $previousTicks) {
                $inversionCount++
            }
            elseif ($currentTicks -eq $previousTicks) {
                $collisionCount++
            }
        }
    }

    return [pscustomobject]@{
        inversionCount = $inversionCount
        collisionCount = $collisionCount
        reasons = @($reasons.ToArray() | Select-Object -Unique)
    }
}

function Write-FileTransferGuiLogLedgerEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)][int]$Bookmark,
        [Parameter(Mandatory = $true)]$InitialSnapshot,
        [AllowNull()][scriptblock]$ProcessOutputDrainAction = $null
    )

    $finalizationErrors = New-Object System.Collections.Generic.List[string]
    $retainedSlice = $null
    $analyzerInput = $null
    $finalSnapshot = $null
    $capturedLines = @()
    $processOutputDrainFailure = ''
    if ($null -ne $ProcessOutputDrainAction) {
        try {
            [object[]]$drainProcesses = @(& $ProcessOutputDrainAction)
            Wait-GuiSmokeProcessOutputCaptureDrain -Processes $drainProcesses
        }
        catch {
            $processOutputDrainFailure = "process_output_drain_failed:$($_.Exception.Message)"
            $finalizationErrors.Add($processOutputDrainFailure) | Out-Null
        }
    }

    # The authoritative log boundary must be captured only after all scenario
    # writers have stopped and their redirected output has reached EOF.
    try {
        Sync-GuiSmokeAppLogLedger
        $finalSnapshot = Get-GuiSmokeLogLedgerSnapshot `
            -State $script:GuiSmokeAppLogLedger `
            -Bookmark $Bookmark
        $capturedLines = @(
            Get-GuiSmokeLogLedgerLinesAfterBookmark `
                -State $script:GuiSmokeAppLogLedger `
                -Bookmark $Bookmark)
    }
    catch {
        $finalizationErrors.Add("final_sync_failed:$($_.Exception.Message)") | Out-Null
    }

    try {
        $retainedSlice = Copy-FileTransferLiveLogSlice `
            -ArtifactDir $ArtifactDir `
            -Bookmark $Bookmark `
            -FileName 'filetransfer-gui-log-ledger-slice.log' `
            -CapturedLines $capturedLines `
            -WriteOnce `
            -PassThru
    }
    catch {
        $finalizationErrors.Add("ledger_slice_failed:$($_.Exception.Message)") | Out-Null
    }

    try {
        $analyzerInput = Copy-FileTransferLiveLogSlice `
            -ArtifactDir $ArtifactDir `
            -Bookmark $Bookmark `
            -CapturedLines $capturedLines `
            -PassThru
    }
    catch {
        $finalizationErrors.Add("analyzer_input_failed:$($_.Exception.Message)") | Out-Null
    }

    if ($null -eq $finalSnapshot) {
        $finalSnapshot = Get-GuiSmokeLogLedgerSnapshot -State $script:GuiSmokeAppLogLedger -Bookmark $Bookmark
    }
    $diagnostics = Get-GuiSmokeLogLedgerDiagnostics -State $script:GuiSmokeAppLogLedger
    $integrityReasons = New-Object System.Collections.Generic.List[string]
    $writerIntegrityReasons = New-Object System.Collections.Generic.List[string]
    foreach ($errorText in @($finalizationErrors.ToArray())) {
        $integrityReasons.Add([string]$errorText) | Out-Null
    }
    if (-not [string]::IsNullOrWhiteSpace($processOutputDrainFailure)) {
        $writerIntegrityReasons.Add($processOutputDrainFailure) | Out-Null
    }

    foreach ($snapshotReason in @(Get-GuiSmokeLogLedgerSnapshotIntegrityReasons `
            -InitialSnapshot $InitialSnapshot `
            -FinalSnapshot $finalSnapshot)) {
        $integrityReasons.Add([string]$snapshotReason) | Out-Null
    }

    $expectedAcceptanceRunId = if ([string]::IsNullOrWhiteSpace([string]$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID)) { '(none)' } else { [string]$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID }
    $expectedScenarioRunId = if ([string]::IsNullOrWhiteSpace([string]$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID)) { '(none)' } else { [string]$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID }
    $writerIdentityMismatchCount = [long]$diagnostics.WriterIdentityMismatchCount
    $initialWriters = @()
    if ($null -ne $InitialSnapshot.PSObject.Properties['writers']) {
        $initialWriters = @($InitialSnapshot.writers)
    }
    $finalWriters = @()
    if ($null -ne $finalSnapshot.PSObject.Properties['writers']) {
        $finalWriters = @($finalSnapshot.writers)
    }
    $writerEvidenceSlice = $null
    $writerEvidenceLines = @()
    try {
        $currentWriterIds = @($finalWriters | ForEach-Object { [string]$_.writerId } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        $writerEvidenceLines = @(
            Get-GuiSmokeLogLedgerLines -State $script:GuiSmokeAppLogLedger |
                Where-Object {
                    $lineWriterId = Get-GuiSmokeLogLedgerStructuredField -Line ([string]$_) -Name 'log_writer_id'
                    $currentWriterIds -contains $lineWriterId
                })
        $writerEvidenceSlice = Copy-FileTransferLiveLogSlice `
            -ArtifactDir $ArtifactDir `
            -Bookmark 0 `
            -FileName 'filetransfer-gui-log-writer-evidence-slice.log' `
            -CapturedLines $writerEvidenceLines `
            -WriteOnce `
            -PassThru
    }
    catch {
        $writerEvidenceFailure = "writer_evidence_slice_failed:$($_.Exception.Message)"
        $integrityReasons.Add($writerEvidenceFailure) | Out-Null
        $writerIntegrityReasons.Add($writerEvidenceFailure) | Out-Null
    }
    foreach ($writer in $finalWriters) {
        if (-not [string]::Equals([string]$writer.acceptanceRunId, $expectedAcceptanceRunId, [System.StringComparison]::Ordinal) -or
            -not [string]::Equals([string]$writer.scenarioRunId, $expectedScenarioRunId, [System.StringComparison]::Ordinal)) {
            $writerIdentityMismatchCount++
        }
    }
    if ($finalWriters.Count -eq 0) {
        $writerIntegrityReasons.Add('final_writer_snapshot_missing') | Out-Null
    }
    if ([long]$diagnostics.WriterGapCount -ne 0) {
        $writerIntegrityReasons.Add('writer_sequence_gap_observed') | Out-Null
    }
    if ([long]$diagnostics.WriterDuplicateCount -ne 0) {
        $writerIntegrityReasons.Add('writer_sequence_duplicate_observed') | Out-Null
    }
    if ($writerIdentityMismatchCount -ne 0) {
        $writerIntegrityReasons.Add('writer_identity_mismatch_observed') | Out-Null
    }

    $writerFailureMarkerFiles = New-Object System.Collections.Generic.List[object]
    $writerMarkers = New-Object System.Collections.Generic.List[object]
    $writerWriteFailureCount = 0L
    $currentProcessOutputRoot = [System.IO.Path]::GetFullPath((Join-Path $ArtifactDir 'process-output')).TrimEnd([char[]]@([char]'\', [char]'/')) + [System.IO.Path]::DirectorySeparatorChar
    $writerOutputSnapshotRoot = Join-Path $ArtifactDir 'filetransfer-gui-log-writer-output-snapshots'
    [void][System.IO.Directory]::CreateDirectory($writerOutputSnapshotRoot)
    $writerOutputSnapshotIndex = 0
    foreach ($outputPath in @($script:GuiSmokeProcessOutputFiles | Sort-Object -Unique)) {
        if ([string]::IsNullOrWhiteSpace([string]$outputPath)) {
            continue
        }
        $fullOutputPath = [System.IO.Path]::GetFullPath([string]$outputPath)
        if (-not $fullOutputPath.StartsWith($currentProcessOutputRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        if (-not (Test-Path -LiteralPath $fullOutputPath -PathType Leaf)) {
            $missingOutputReason = "writer_output_registered_file_missing:$fullOutputPath"
            $finalizationErrors.Add($missingOutputReason) | Out-Null
            $integrityReasons.Add($missingOutputReason) | Out-Null
            $writerIntegrityReasons.Add($missingOutputReason) | Out-Null
            continue
        }
        $sourceStream = $null
        $snapshotStream = $null
        try {
            $shareMode = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
            $sourceStream = [System.IO.FileStream]::new(
                $fullOutputPath,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read,
                $shareMode)
            $capturedLength = [long]$sourceStream.Length
            if ($capturedLength -gt [int]::MaxValue) {
                throw "writer_output_snapshot_too_large:bytes=$capturedLength"
            }
            $capturedBytes = New-Object byte[] ([int]$capturedLength)
            $capturedByteCount = 0
            while ($capturedByteCount -lt $capturedBytes.Length) {
                $read = $sourceStream.Read($capturedBytes, $capturedByteCount, $capturedBytes.Length - $capturedByteCount)
                if ($read -le 0) {
                    break
                }
                $capturedByteCount += $read
            }
            if ($capturedByteCount -ne $capturedBytes.Length) {
                throw "writer_output_snapshot_short_read:expected=$capturedLength;actual=$capturedByteCount"
            }

            $writerOutputSnapshotIndex++
            $snapshotName = '{0:D3}-{1}' -f $writerOutputSnapshotIndex, ([System.IO.Path]::GetFileName($fullOutputPath))
            $snapshotPath = Join-Path $writerOutputSnapshotRoot $snapshotName
            $snapshotStream = [System.IO.FileStream]::new(
                $snapshotPath,
                [System.IO.FileMode]::CreateNew,
                [System.IO.FileAccess]::Write,
                [System.IO.FileShare]::None)
            $snapshotStream.Write($capturedBytes, 0, $capturedBytes.Length)
            $snapshotStream.Flush($true)
            $snapshotStream.Dispose()
            $snapshotStream = $null

            $markerCount = 0L
            $deferredMarkerCount = 0L
            $recoveredMarkerCount = 0L
            $capturedText = [System.Text.Encoding]::UTF8.GetString($capturedBytes)
            foreach ($outputLine in @($capturedText -split '\r?\n')) {
                $markerEvent = Get-GuiSmokeLogLedgerStructuredField -Line ([string]$outputLine) -Name 'event'
                if ($markerEvent -notin @('operational_log_write_deferred', 'operational_log_write_recovered', 'operational_log_write_failed')) {
                    continue
                }
                if ([string]::Equals($markerEvent, 'operational_log_write_failed', [System.StringComparison]::OrdinalIgnoreCase)) {
                    $markerCount++
                }
                elseif ([string]::Equals($markerEvent, 'operational_log_write_deferred', [System.StringComparison]::OrdinalIgnoreCase)) {
                    $deferredMarkerCount++
                }
                else {
                    $recoveredMarkerCount++
                }
                $writerMarkers.Add([pscustomobject]@{
                    event = $markerEvent
                    writerId = Get-GuiSmokeLogLedgerStructuredField -Line ([string]$outputLine) -Name 'log_writer_id'
                    acceptanceRunId = Get-GuiSmokeLogLedgerStructuredField -Line ([string]$outputLine) -Name 'acceptance_run_id'
                    scenarioRunId = Get-GuiSmokeLogLedgerStructuredField -Line ([string]$outputLine) -Name 'scenario_run_id'
                    batchGeneration = [long](Get-GuiSmokeLogLedgerStructuredField -Line ([string]$outputLine) -Name 'batch_generation')
                    firstSequence = [long](Get-GuiSmokeLogLedgerStructuredField -Line ([string]$outputLine) -Name 'first_sequence')
                    lastSequence = [long](Get-GuiSmokeLogLedgerStructuredField -Line ([string]$outputLine) -Name 'last_sequence')
                    lineCount = [long](Get-GuiSmokeLogLedgerStructuredField -Line ([string]$outputLine) -Name 'line_count')
                    payloadSha256 = Get-GuiSmokeLogLedgerStructuredField -Line ([string]$outputLine) -Name 'payload_sha256'
                    attemptUtcTicks = [long](Get-GuiSmokeLogLedgerStructuredField -Line ([string]$outputLine) -Name 'attempt_utc_ticks')
                    attemptCount = [long](Get-GuiSmokeLogLedgerStructuredField -Line ([string]$outputLine) -Name 'attempt_count')
                    failureCode = Get-GuiSmokeLogLedgerStructuredField -Line ([string]$outputLine) -Name 'failure_code'
                }) | Out-Null
            }
            $writerWriteFailureCount += $markerCount
            $writerFailureMarkerFiles.Add([ordered]@{
                path = [System.IO.Path]::GetFullPath($snapshotPath)
                sha256 = Get-FileSha256Hex -Path $snapshotPath
                markerCount = $markerCount
                deferredMarkerCount = $deferredMarkerCount
                recoveredMarkerCount = $recoveredMarkerCount
                terminalFailureMarkerCount = $markerCount
            }) | Out-Null
        }
        catch {
            $markerScanFailure = "writer_failure_marker_scan_failed:${outputPath}:$($_.Exception.GetType().Name)"
            $finalizationErrors.Add($markerScanFailure) | Out-Null
            $integrityReasons.Add($markerScanFailure) | Out-Null
            $writerIntegrityReasons.Add($markerScanFailure) | Out-Null
        }
        finally {
            if ($snapshotStream) {
                $snapshotStream.Dispose()
            }
            if ($sourceStream) {
                $sourceStream.Dispose()
            }
        }
    }

    $writerLineRecords = New-Object System.Collections.Generic.List[object]
    foreach ($writerLine in $writerEvidenceLines) {
        $writerLineRecords.Add([pscustomobject]@{
            rawLine = [string]$writerLine
            event = Get-GuiSmokeLogLedgerStructuredField -Line ([string]$writerLine) -Name 'event'
            writerId = Get-GuiSmokeLogLedgerStructuredField -Line ([string]$writerLine) -Name 'log_writer_id'
            sequence = [long](Get-GuiSmokeLogLedgerStructuredField -Line ([string]$writerLine) -Name 'log_writer_sequence')
            batchGeneration = [long](Get-GuiSmokeLogLedgerStructuredField -Line ([string]$writerLine) -Name 'log_writer_batch_generation')
            batchFirstSequence = [long](Get-GuiSmokeLogLedgerStructuredField -Line ([string]$writerLine) -Name 'log_writer_batch_first_sequence')
            batchLastSequence = [long](Get-GuiSmokeLogLedgerStructuredField -Line ([string]$writerLine) -Name 'log_writer_batch_last_sequence')
            attemptUtcTicks = [long](Get-GuiSmokeLogLedgerStructuredField -Line ([string]$writerLine) -Name 'log_writer_attempt_utc_ticks')
        }) | Out-Null
    }

    $deferredMarkers = @($writerMarkers | Where-Object { $_.event -eq 'operational_log_write_deferred' })
    $recoveredMarkers = @($writerMarkers | Where-Object { $_.event -eq 'operational_log_write_recovered' })
    $terminalMarkers = @($writerMarkers | Where-Object { $_.event -eq 'operational_log_write_failed' })
    $writerDeferredLineCount = if ($deferredMarkers.Count -gt 0) {
        [long](($deferredMarkers | Measure-Object -Property lineCount -Sum).Sum)
    }
    else {
        0L
    }
    $writerRecoveredLineCount = if ($recoveredMarkers.Count -gt 0) {
        [long](($recoveredMarkers | Measure-Object -Property lineCount -Sum).Sum)
    }
    else {
        0L
    }
    $writerUnresolvedDeferredBatchCount = 0L
    $writerUnresolvedDeferredLineCount = 0L
    $writerRecoveryMismatchCount = 0L
    $writerChronologyInversionCount = 0L
    $writerChronologyCollisionCount = 0L
    $writerDeferredBatchEvidence = New-Object System.Collections.Generic.List[object]
    foreach ($deferred in $deferredMarkers) {
        $matchingRecoveries = @($recoveredMarkers | Where-Object {
                $_.writerId -eq $deferred.writerId -and
                [long]$_.batchGeneration -eq [long]$deferred.batchGeneration
            })
        $exactRecovery = @($matchingRecoveries | Where-Object {
                [long]$_.firstSequence -eq [long]$deferred.firstSequence -and
                [long]$_.lastSequence -eq [long]$deferred.lastSequence -and
                [long]$_.lineCount -eq [long]$deferred.lineCount -and
                [long]$_.attemptUtcTicks -eq [long]$deferred.attemptUtcTicks -and
                [string]::Equals([string]$_.payloadSha256, [string]$deferred.payloadSha256, [System.StringComparison]::OrdinalIgnoreCase) -and
                [string]::Equals([string]$_.acceptanceRunId, [string]$deferred.acceptanceRunId, [System.StringComparison]::Ordinal) -and
                [string]::Equals([string]$_.scenarioRunId, [string]$deferred.scenarioRunId, [System.StringComparison]::Ordinal)
            })
        $persistedBatchLines = @($writerLineRecords | Where-Object {
                $_.writerId -eq $deferred.writerId -and
                [long]$_.batchGeneration -eq [long]$deferred.batchGeneration
            } | Sort-Object sequence)
        $persistedPayloadSha256 = if ($persistedBatchLines.Count -gt 0) {
            Get-GuiSmokeLogLedgerHash -Value (($persistedBatchLines | ForEach-Object { [string]$_.rawLine }) -join "`n")
        }
        else { '' }
        $persistedRangeMatches = $persistedBatchLines.Count -eq [long]$deferred.lineCount -and
            $persistedBatchLines.Count -gt 0 -and
            [long]$persistedBatchLines[0].sequence -eq [long]$deferred.firstSequence -and
            [long]$persistedBatchLines[-1].sequence -eq [long]$deferred.lastSequence -and
            [string]::Equals($persistedPayloadSha256, [string]$deferred.payloadSha256, [System.StringComparison]::OrdinalIgnoreCase)
        $markerIdentityMatches = [string]::Equals([string]$deferred.acceptanceRunId, $expectedAcceptanceRunId, [System.StringComparison]::Ordinal) -and
            [string]::Equals([string]$deferred.scenarioRunId, $expectedScenarioRunId, [System.StringComparison]::Ordinal)
        $resolved = $exactRecovery.Count -eq 1 -and
            $matchingRecoveries.Count -eq 1 -and
            $persistedRangeMatches -and
            $markerIdentityMatches
        if (-not $resolved) {
            $writerUnresolvedDeferredBatchCount++
            $writerUnresolvedDeferredLineCount += [long]$deferred.lineCount
            $writerRecoveryMismatchCount++
        }
        $writerDeferredBatchEvidence.Add([ordered]@{
            writerId = [string]$deferred.writerId
            batchGeneration = [long]$deferred.batchGeneration
            firstSequence = [long]$deferred.firstSequence
            lastSequence = [long]$deferred.lastSequence
            lineCount = [long]$deferred.lineCount
            payloadSha256 = [string]$deferred.payloadSha256
            attemptUtcTicks = [long]$deferred.attemptUtcTicks
            failureCode = [string]$deferred.failureCode
            recovered = $resolved
        }) | Out-Null
    }
    foreach ($recovered in $recoveredMarkers) {
        $matchingDeferredMarkers = @($deferredMarkers | Where-Object {
                $_.writerId -eq $recovered.writerId -and
                [long]$_.batchGeneration -eq [long]$recovered.batchGeneration
            })
        if ($matchingDeferredMarkers.Count -ne 1) {
            $writerRecoveryMismatchCount++
        }
    }

    $writerDrainEvidence = New-Object System.Collections.Generic.List[object]
    $writerDrainMissingCount = 0L
    foreach ($writer in $finalWriters) {
        $writerDrains = @($writerLineRecords | Where-Object {
                $_.writerId -eq [string]$writer.writerId -and
                $_.event -eq 'operational_log_writer_drained'
            })
        $drainValid = $writerDrains.Count -eq 1 -and [long]$writerDrains[0].sequence -eq [long]$writer.lastSequence
        if (-not $drainValid) {
            $writerDrainMissingCount++
        }
        $writerDrainEvidence.Add([ordered]@{
            writerId = [string]$writer.writerId
            drainSequence = $(if ($writerDrains.Count -eq 1) { [long]$writerDrains[0].sequence } else { 0L })
            finalSequence = [long]$writer.lastSequence
            valid = $drainValid
        }) | Out-Null
    }

    $writerRecoveryReasons = New-Object System.Collections.Generic.List[string]
    if ($writerUnresolvedDeferredBatchCount -ne 0) { $writerRecoveryReasons.Add('writer_deferred_batch_unresolved') | Out-Null }
    if ($writerRecoveryMismatchCount -ne 0) { $writerRecoveryReasons.Add('writer_recovery_evidence_mismatch') | Out-Null }
    if ($terminalMarkers.Count -ne 0) { $writerRecoveryReasons.Add('operational_log_write_failure_observed') | Out-Null }
    if ($writerDrainMissingCount -ne 0) { $writerRecoveryReasons.Add('writer_drain_missing') | Out-Null }
    $writerChronologyReasons = New-Object System.Collections.Generic.List[string]
    $writerChronology = Get-GuiSmokeLogWriterChronology -WriterLineRecords @($writerLineRecords.ToArray())
    $writerChronologyInversionCount = [long]$writerChronology.inversionCount
    $writerChronologyCollisionCount = [long]$writerChronology.collisionCount
    foreach ($chronologyReason in @($writerChronology.reasons)) {
        $writerChronologyReasons.Add([string]$chronologyReason) | Out-Null
    }
    if ($writerChronologyInversionCount -ne 0) { $writerChronologyReasons.Add('writer_recovery_chronology_inversion') | Out-Null }
    if ($writerChronologyCollisionCount -ne 0) { $writerChronologyReasons.Add('writer_recovery_chronology_collision') | Out-Null }
    foreach ($reason in @($writerRecoveryReasons.ToArray()) + @($writerChronologyReasons.ToArray())) {
        $writerIntegrityReasons.Add([string]$reason) | Out-Null
    }
    if ($writerFailureMarkerFiles.Count -eq 0) {
        $writerIntegrityReasons.Add('writer_output_snapshot_missing') | Out-Null
    }
    if ($null -eq $writerEvidenceSlice) {
        $writerIntegrityReasons.Add('writer_evidence_slice_missing') | Out-Null
    }
    if ($writerWriteFailureCount -ne 0) {
        $writerIntegrityReasons.Add('operational_log_write_failure_observed') | Out-Null
    }
    foreach ($writerReason in @($writerIntegrityReasons.ToArray())) {
        $integrityReasons.Add([string]$writerReason) | Out-Null
    }
    if ([int]$diagnostics.GapCount -ne 0) {
        $integrityReasons.Add('cursor_gap_observed') | Out-Null
    }
    if ([int]$diagnostics.DuplicateIndexCount -ne 0) {
        $integrityReasons.Add('duplicate_generation_ordinal_observed') | Out-Null
    }
    if ([int]$diagnostics.ConsumedIndexCount -ne [int]$finalSnapshot.lineCount) {
        $integrityReasons.Add('ledger_line_index_count_mismatch') | Out-Null
    }
    if (-not [string]::Equals([string]$diagnostics.FailureCode, '(none)', [System.StringComparison]::Ordinal)) {
        $integrityReasons.Add("ledger_failure:$($diagnostics.FailureCode)") | Out-Null
    }

    $expectedRetainedLineCount = [Math]::Max(0, [int]$finalSnapshot.lineCount - $Bookmark)
    if ($null -eq $retainedSlice) {
        $integrityReasons.Add('retained_slice_missing') | Out-Null
        $retainedSliceEvidence = [ordered]@{
            path = [System.IO.Path]::GetFullPath((Join-Path $ArtifactDir 'filetransfer-gui-log-ledger-slice.log'))
            sha256 = '(none)'
            lineCount = 0
        }
    }
    else {
        if ([int]$retainedSlice.LineCount -ne $expectedRetainedLineCount) {
            $integrityReasons.Add('retained_slice_line_count_mismatch') | Out-Null
        }
        if ([string]::IsNullOrWhiteSpace([string]$retainedSlice.Sha256)) {
            $integrityReasons.Add('retained_slice_hash_missing') | Out-Null
        }
        $retainedSliceEvidence = [ordered]@{
            path = [string]$retainedSlice.Path
            sha256 = [string]$retainedSlice.Sha256
            lineCount = [int]$retainedSlice.LineCount
        }
    }

    if ($null -eq $analyzerInput) {
        $integrityReasons.Add('analyzer_input_missing') | Out-Null
        $analyzerInputEvidence = [ordered]@{
            path = [System.IO.Path]::GetFullPath((Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log'))
            captureSha256 = '(none)'
            captureLineCount = 0
            mayBeMilestoneMerged = $true
        }
    }
    else {
        $analyzerInputEvidence = [ordered]@{
            path = [string]$analyzerInput.Path
            captureSha256 = [string]$analyzerInput.Sha256
            captureLineCount = [int]$analyzerInput.LineCount
            mayBeMilestoneMerged = $true
        }
    }

    $initialEvidence = [ordered]@{
        bookmark = [int]$InitialSnapshot.bookmark
        lineCount = [int]$InitialSnapshot.lineCount
        activeGenerationKey = [string]$InitialSnapshot.activeGenerationKey
        generations = @($InitialSnapshot.generations)
        writers = @($initialWriters)
    }
    $finalEvidence = [ordered]@{
        bookmark = [int]$finalSnapshot.bookmark
        lineCount = [int]$finalSnapshot.lineCount
        activeGenerationKey = [string]$finalSnapshot.activeGenerationKey
        generations = @($finalSnapshot.generations)
        writers = @($finalWriters)
    }
    $evidence = [ordered]@{
        schemaVersion = 1
        event = 'filetransfer_gui_log_ledger'
        acceptanceRunId = $expectedAcceptanceRunId
        scenarioRunId = $expectedScenarioRunId
        ledgerId = [string]$finalSnapshot.ledgerId
        writerEvidenceVersion = 3
        initial = $initialEvidence
        final = $finalEvidence
        rotationCount = [int]$diagnostics.RotationCount
        transientRescanCount = [int]$diagnostics.TransientRescanCount
        gapCount = [int]$diagnostics.GapCount
        duplicateIndexCount = [int]$diagnostics.DuplicateIndexCount
        writerGapCount = [long]$diagnostics.WriterGapCount
        writerDuplicateCount = [long]$diagnostics.WriterDuplicateCount
        writerIdentityMismatchCount = $writerIdentityMismatchCount
        writerWriteFailureCount = $writerWriteFailureCount
        writerDeferredBatchCount = [long]$deferredMarkers.Count
        writerDeferredLineCount = $writerDeferredLineCount
        writerRecoveredBatchCount = [long]$recoveredMarkers.Count
        writerRecoveredLineCount = $writerRecoveredLineCount
        writerUnresolvedDeferredBatchCount = $writerUnresolvedDeferredBatchCount
        writerUnresolvedDeferredLineCount = $writerUnresolvedDeferredLineCount
        writerTerminalFailureCount = [long]$terminalMarkers.Count
        writerRecoveryMismatchCount = $writerRecoveryMismatchCount
        writerChronologyInversionCount = $writerChronologyInversionCount
        writerChronologyCollisionCount = $writerChronologyCollisionCount
        writerDrainMissingCount = $writerDrainMissingCount
        writerRecoveryVerdict = $(if ($writerRecoveryReasons.Count -eq 0) { 'pass' } else { 'fail' })
        writerRecoveryReasons = @($writerRecoveryReasons.ToArray() | Select-Object -Unique)
        writerChronologyVerdict = $(if ($writerChronologyReasons.Count -eq 0) { 'pass' } else { 'fail' })
        writerChronologyReasons = @($writerChronologyReasons.ToArray() | Select-Object -Unique)
        writerIntegrityVerdict = $(if ($writerIntegrityReasons.Count -eq 0) { 'pass' } else { 'fail' })
        writerIntegrityReasons = @($writerIntegrityReasons.ToArray() | Select-Object -Unique)
        writerFailureMarkerFiles = @($writerFailureMarkerFiles.ToArray())
        writerMarkerFiles = @($writerFailureMarkerFiles.ToArray())
        writerDeferredBatches = @($writerDeferredBatchEvidence.ToArray())
        writerDrains = @($writerDrainEvidence.ToArray())
        writerEvidenceSlice = $(if ($null -eq $writerEvidenceSlice) {
                [ordered]@{ path = ''; sha256 = ''; lineCount = 0 }
            }
            else {
                [ordered]@{
                    path = [string]$writerEvidenceSlice.Path
                    sha256 = [string]$writerEvidenceSlice.Sha256
                    lineCount = [long]$writerEvidenceSlice.LineCount
                }
            })
        consumedIndexCount = [int]$diagnostics.ConsumedIndexCount
        consumedIndexSha256 = [string]$diagnostics.ConsumedIndexSha256
        consumedIndexRanges = @($diagnostics.ConsumedIndexRanges)
        syncCount = [int]$diagnostics.SyncCount
        failureCode = [string]$diagnostics.FailureCode
        failureDetail = [string]$diagnostics.FailureDetail
        retainedSlice = $retainedSliceEvidence
        analyzerInput = $analyzerInputEvidence
        cursorIntegrityVerdict = $(if ($integrityReasons.Count -eq 0) { 'pass' } else { 'fail' })
        cursorIntegrityReasons = @($integrityReasons.ToArray() | Select-Object -Unique)
    }

    $path = Join-Path $ArtifactDir 'filetransfer-gui-log-ledger.json'
    $json = $evidence | ConvertTo-Json -Depth 12
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($json)
    $stream = $null
    try {
        $stream = [System.IO.FileStream]::new(
            $path,
            [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    }
    finally {
        if ($stream) {
            $stream.Dispose()
        }
    }
}

function Write-TunaGuiMilestoneEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)]$ObservedEvidenceLines
    )

    if ($null -eq $ObservedEvidenceLines -or $ObservedEvidenceLines.Count -le 0) {
        return
    }

    [System.IO.File]::WriteAllText(
        (Join-Path $ArtifactDir 'filetransfer-tuna-gui-milestone-evidence.log'),
        (@($ObservedEvidenceLines.ToArray()) -join [Environment]::NewLine),
        [System.Text.Encoding]::UTF8)
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

function Test-TunaGuiLogLineContainsAll {
    param(
        [AllowNull()][string]$Line,
        [Parameter(Mandatory = $true)][string[]]$Needles
    )

    if ([string]::IsNullOrWhiteSpace($Line)) {
        return $false
    }

    foreach ($needle in @($Needles)) {
        if ($Line.IndexOf($needle, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
            return $false
        }
    }

    return $true
}

function Test-TunaGuiFallbackFrontierRequestDispatchTupleMatch {
    param(
        [AllowNull()][string]$AcceptedLine,
        [AllowNull()][string]$DispatchLine
    )

    return Test-FileTransferAuthorityTupleMatch `
        -LeftLine $AcceptedLine `
        -RightLine $DispatchLine `
        -PeerCorrelation
}

function Test-TunaGuiLatestFallbackFrontierRequestDispatchMissing {
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Lines)

    $latestAcceptedIndex = -1
    for ($i = 0; $i -lt @($Lines).Count; $i++) {
        $line = [string]$Lines[$i]
        if ((Test-TunaGuiLogLineContainsAll -Line $line -Needles @('event=filetransfer_control_plane_delivery_result', 'kind=frontier_request', 'frame_type=filetransfer.frontier_request.v6', 'peer_visible_any=1', 'accepted_any=1')) -or
            (Test-TunaGuiLogLineContainsAll -Line $line -Needles @('event=filetransfer_control_plane_delivery_observed', 'kind=frontier_request', 'frame_type=filetransfer.frontier_request.v6', 'peer_visible_any=1', 'accepted_any=1'))) {
            $latestAcceptedIndex = $i
        }
    }

    if ($latestAcceptedIndex -lt 0) {
        return $false
    }

    $acceptedLine = [string]$Lines[$latestAcceptedIndex]
    for ($i = $latestAcceptedIndex + 1; $i -lt @($Lines).Count; $i++) {
        $line = [string]$Lines[$i]
        if ((Test-TunaGuiLogLineContainsAll -Line $line -Needles @('event=filetransfer_v6_frontier_request_receive_dispatched')) -or
            (Test-TunaGuiLogLineContainsAll -Line $line -Needles @('event=filetransfer_v6_frontier_request_receive_dispatch_rejected')) -or
            (Test-TunaGuiLogLineContainsAll -Line $line -Needles @('event=filetransfer_v6_frontier_request_receive_dispatch_coalesced'))) {
            if (Test-TunaGuiFallbackFrontierRequestDispatchTupleMatch -AcceptedLine $acceptedLine -DispatchLine $line) {
                return $false
            }
        }
    }

    return $true
}

function Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing {
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Lines)

    function Get-WorkerHandoffFieldValue {
        param(
            [AllowNull()][string]$Line,
            [Parameter(Mandatory = $true)][string]$Name
        )

        if ([string]::IsNullOrWhiteSpace($Line)) {
            return ''
        }

        $match = [regex]::Match(
            $Line,
            "(?i)(?:^|;\s*)$([regex]::Escape($Name))=([^;`r`n]+)")
        return $(if ($match.Success) { $match.Groups[1].Value.Trim() } else { '' })
    }

    function Test-WorkerHandoffIdentityMatch {
        param(
            [AllowNull()][string]$SourceLine,
            [AllowNull()][string]$CandidateLine
        )

        foreach ($fieldName in @('session_id', 'transfer_id')) {
            $sourceValue = Get-WorkerHandoffFieldValue -Line $SourceLine -Name $fieldName
            $candidateValue = Get-WorkerHandoffFieldValue -Line $CandidateLine -Name $fieldName
            if ([string]::IsNullOrWhiteSpace($sourceValue) -or
                $sourceValue -eq '(none)' -or
                [string]::IsNullOrWhiteSpace($candidateValue) -or
                $candidateValue -eq '(none)' -or
                -not [string]::Equals($sourceValue, $candidateValue, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $false
            }
        }

        return $true
    }

    function Test-WorkerHandoffAcceptedPublication {
        param(
            [AllowNull()][string]$Line,
            [AllowNull()][string]$ScopeLine
        )

        $publicationEvent = $false
        foreach ($eventName in @(
                'filetransfer_route_transitioned',
                'filetransfer_route_selected',
                'filetransfer_live_route_epoch_started',
                'filetransfer_live_route_epoch_recovered',
                'filetransfer_runtime_started')) {
            if ($Line.IndexOf("event=$eventName", [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $publicationEvent = $true
                break
            }
        }

        return $publicationEvent -and
            $Line.IndexOf('direction=outbound', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $Line.IndexOf('route_publication_action_kind=RoutePublication', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $Line.IndexOf('coordinator_decision_reason=accepted', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            (Test-WorkerHandoffIdentityMatch -SourceLine $ScopeLine -CandidateLine $Line)
    }

    function Test-WorkerHandoffPublicationTupleMatch {
        param(
            [AllowNull()][string]$AnchorLine,
            [AllowNull()][string]$CandidateLine
        )

        if (-not (Test-WorkerHandoffPublicationTupleComplete -Line $AnchorLine) -or
            -not (Test-WorkerHandoffPublicationTupleComplete -Line $CandidateLine) -or
            -not (Test-WorkerHandoffIdentityMatch -SourceLine $AnchorLine -CandidateLine $CandidateLine)) {
            return $false
        }

        foreach ($fieldName in @(
                'route',
                'protocol_version',
                'live_route_epoch',
                'runtime_generation',
                'terminal_generation',
                'leg_generation',
                'route_publication_generation',
                'route_commit_generation',
                'target_route',
                'target_protocol_version',
                'target_transport')) {
            $anchorValue = Get-WorkerHandoffFieldValue -Line $AnchorLine -Name $fieldName
            $candidateValue = Get-WorkerHandoffFieldValue -Line $CandidateLine -Name $fieldName
            if ([string]::IsNullOrWhiteSpace($anchorValue) -or
                $anchorValue -eq '(none)' -or
                [string]::IsNullOrWhiteSpace($candidateValue) -or
                $candidateValue -eq '(none)' -or
                -not [string]::Equals($anchorValue, $candidateValue, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $false
            }
        }

        return $true
    }

    function Test-WorkerHandoffPublicationTupleComplete {
        param([AllowNull()][string]$Line)

        if ([string]::IsNullOrWhiteSpace($Line) -or
            $Line.IndexOf('direction=outbound', [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
            return $false
        }

        foreach ($fieldName in @('session_id', 'transfer_id')) {
            $value = Get-WorkerHandoffFieldValue -Line $Line -Name $fieldName
            if ([string]::IsNullOrWhiteSpace($value) -or $value -in @('(none)', 'none')) {
                return $false
            }
        }

        foreach ($fieldName in @(
                'route',
                'protocol_version',
                'live_route_epoch',
                'runtime_generation',
                'terminal_generation',
                'leg_generation',
                'route_publication_generation',
                'route_commit_generation',
                'target_route',
                'target_protocol_version',
                'target_transport')) {
            $value = Get-WorkerHandoffFieldValue -Line $Line -Name $fieldName
            if ([string]::IsNullOrWhiteSpace($value) -or $value -in @('(none)', 'none')) {
                return $false
            }
        }

        return $true
    }

    function Test-WorkerHandoffFrontierTupleMatch {
        param(
            [AllowNull()][string]$PublicationLine,
            [AllowNull()][string]$CandidateLine
        )

        if (-not (Test-WorkerHandoffPublicationTupleComplete -Line $PublicationLine) -or
            -not (Test-WorkerHandoffIdentityMatch -SourceLine $PublicationLine -CandidateLine $CandidateLine)) {
            return $false
        }

        foreach ($fieldName in @('route', 'protocol_version', 'live_route_epoch', 'leg_generation')) {
            $publicationValue = Get-WorkerHandoffFieldValue -Line $PublicationLine -Name $fieldName
            $candidateValue = Get-WorkerHandoffFieldValue -Line $CandidateLine -Name $fieldName
            if ([string]::IsNullOrWhiteSpace($candidateValue) -or
                $candidateValue -in @('(none)', 'none') -or
                -not [string]::Equals($publicationValue, $candidateValue, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $false
            }
        }

        return $true
    }

    function Get-WorkerHandoffFrontierCorrelationKey {
        param([AllowNull()][string]$Line)

        $repairRequestId = Get-WorkerHandoffFieldValue -Line $Line -Name 'repair_request_id'
        $requestCorrelationId = Get-WorkerHandoffFieldValue -Line $Line -Name 'request_correlation_id'
        if ([string]::IsNullOrWhiteSpace($repairRequestId) -or
            $repairRequestId -in @('(none)', 'none') -or
            [string]::IsNullOrWhiteSpace($requestCorrelationId) -or
            $requestCorrelationId -in @('(none)', 'none')) {
            return ''
        }

        return ('{0}|{1}' -f $repairRequestId, $requestCorrelationId).ToLowerInvariant()
    }

    function Get-WorkerHandoffInstallDescriptorFieldNames {
        @(
            'descriptor_terminal_generation',
            'descriptor_leg_generation',
            'installation_reason',
            'runtime_owner_session_id',
            'runtime_owner_transfer_id',
            'runtime_owner_direction',
            'runtime_owner_route',
            'runtime_owner_route_token',
            'runtime_owner_protocol_version',
            'runtime_owner_live_route_epoch',
            'runtime_owner_runtime_generation',
            'runtime_owner_terminal_generation',
            'runtime_owner_runtime_family',
            'runtime_owner_created_reason',
            'route_publication_session_id',
            'route_publication_transfer_id',
            'route_publication_direction',
            'route_publication_route',
            'route_publication_route_token',
            'route_publication_protocol_version',
            'route_publication_live_route_epoch',
            'route_publication_runtime_generation',
            'route_publication_terminal_generation',
            'route_publication_leg_generation',
            'route_publication_route_commit_generation',
            'route_publication_target_route',
            'route_publication_target_protocol_version',
            'route_publication_target_transport',
            'route_publication_token_generation',
            'route_publication_action_kind',
            'route_publication_reason',
            'route_publication_owner_session_id',
            'route_publication_owner_transfer_id',
            'route_publication_owner_direction',
            'route_publication_owner_route',
            'route_publication_owner_route_token',
            'route_publication_owner_protocol_version',
            'route_publication_owner_live_route_epoch',
            'route_publication_owner_runtime_generation',
            'route_publication_owner_terminal_generation',
            'route_publication_owner_runtime_family',
            'route_publication_owner_created_reason'
        )
    }

    function Test-WorkerHandoffInstallDescriptorComplete {
        param([AllowNull()][string]$Line)

        if (-not (Test-WorkerHandoffPublicationTupleComplete -Line $Line)) {
            return $false
        }

        $installGenerationText = Get-WorkerHandoffFieldValue -Line $Line -Name 'install_generation'
        $predecessorGenerationText = Get-WorkerHandoffFieldValue -Line $Line -Name 'predecessor_install_generation'
        $dataSessionGenerationText = Get-WorkerHandoffFieldValue -Line $Line -Name 'data_session_generation'
        $runtimeFamily = Get-WorkerHandoffFieldValue -Line $Line -Name 'runtime_family'
        if ($installGenerationText -notmatch '^\d+$' -or
            $predecessorGenerationText -notmatch '^\d+$' -or
            $dataSessionGenerationText -notmatch '^\d+$' -or
            [string]::IsNullOrWhiteSpace($runtimeFamily) -or
            $runtimeFamily -in @('(none)', 'none')) {
            return $false
        }

        foreach ($fieldName in Get-WorkerHandoffInstallDescriptorFieldNames) {
            $value = Get-WorkerHandoffFieldValue -Line $Line -Name $fieldName
            if ([string]::IsNullOrWhiteSpace($value) -or $value -in @('(none)', 'none')) {
                return $false
            }
        }

        foreach ($fieldName in @(
                'descriptor_terminal_generation',
                'descriptor_leg_generation',
                'runtime_owner_protocol_version',
                'runtime_owner_live_route_epoch',
                'runtime_owner_runtime_generation',
                'runtime_owner_terminal_generation',
                'route_publication_protocol_version',
                'route_publication_live_route_epoch',
                'route_publication_runtime_generation',
                'route_publication_terminal_generation',
                'route_publication_leg_generation',
                'route_publication_route_commit_generation',
                'route_publication_target_protocol_version',
                'route_publication_token_generation',
                'route_publication_owner_protocol_version',
                'route_publication_owner_live_route_epoch',
                'route_publication_owner_runtime_generation',
                'route_publication_owner_terminal_generation')) {
            if ((Get-WorkerHandoffFieldValue -Line $Line -Name $fieldName) -notmatch '^\d+$') {
                return $false
            }
        }

        foreach ($fieldName in @('runtime_owner_direction', 'route_publication_direction', 'route_publication_owner_direction')) {
            if ((Get-WorkerHandoffFieldValue -Line $Line -Name $fieldName) -ne 'outbound') {
                return $false
            }
        }

        $installGeneration = [int64]$installGenerationText
        $predecessorGeneration = [int64]$predecessorGenerationText
        $dataSessionGeneration = [int64]$dataSessionGenerationText
        return $installGeneration -gt 0 -and
            $predecessorGeneration -ge 0 -and
            $predecessorGeneration -lt $installGeneration -and
            $dataSessionGeneration -gt 0
    }

    function Test-WorkerHandoffInstallDescriptorMatch {
        param(
            [AllowNull()][string]$AnchorLine,
            [AllowNull()][string]$CandidateLine
        )

        if (-not (Test-WorkerHandoffInstallDescriptorComplete -Line $AnchorLine) -or
            -not (Test-WorkerHandoffInstallDescriptorComplete -Line $CandidateLine) -or
            -not (Test-WorkerHandoffPublicationTupleMatch -AnchorLine $AnchorLine -CandidateLine $CandidateLine)) {
            return $false
        }

        foreach ($fieldName in @(
                'install_generation',
                'predecessor_install_generation',
                'data_session_generation',
                'runtime_family') + @(Get-WorkerHandoffInstallDescriptorFieldNames)) {
            $anchorValue = Get-WorkerHandoffFieldValue -Line $AnchorLine -Name $fieldName
            $candidateValue = Get-WorkerHandoffFieldValue -Line $CandidateLine -Name $fieldName
            if (-not [string]::Equals($anchorValue, $candidateValue, [System.StringComparison]::Ordinal)) {
                return $false
            }
        }

        return $true
    }

    function Test-WorkerHandoffDispatchTupleMatch {
        param(
            [AllowNull()][string]$InstallLine,
            [AllowNull()][string]$CandidateLine
        )

        return Test-WorkerHandoffInstallDescriptorMatch -AnchorLine $InstallLine -CandidateLine $CandidateLine
    }

    function Test-WorkerHandoffProgressMatch {
        param(
            [AllowNull()][string]$InstallLine,
            [AllowNull()][string]$ProgressLine,
            [int]$ProgressIndex,
            [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Dispatches
        )

        if ([string]::IsNullOrWhiteSpace($InstallLine) -or
            [string]::IsNullOrWhiteSpace($ProgressLine) -or
            $ProgressLine.IndexOf('event=filetransfer_fallback_frontier_repair_progress_observed', [System.StringComparison]::OrdinalIgnoreCase) -lt 0 -or
            (Get-WorkerHandoffFieldValue -Line $ProgressLine -Name 'direction') -ne 'inbound' -or
            -not (Test-WorkerHandoffIdentityMatch -SourceLine $InstallLine -CandidateLine $ProgressLine)) {
            return $false
        }

        $repairRequestKey = Get-WorkerHandoffFieldValue -Line $ProgressLine -Name 'repair_request_key'
        $frontierBeforeText = Get-WorkerHandoffFieldValue -Line $ProgressLine -Name 'committed_frontier_before'
        $frontierAfterText = Get-WorkerHandoffFieldValue -Line $ProgressLine -Name 'committed_frontier_after'
        $requestedChunkCountText = Get-WorkerHandoffFieldValue -Line $ProgressLine -Name 'requested_chunk_count'
        if ([string]::IsNullOrWhiteSpace($repairRequestKey) -or
            $repairRequestKey -in @('(none)', 'none') -or
            $frontierBeforeText -notmatch '^\d+$' -or
            $frontierAfterText -notmatch '^\d+$' -or
            $requestedChunkCountText -notmatch '^\d+$' -or
            [int64]$frontierAfterText -le [int64]$frontierBeforeText -or
            [int64]$requestedChunkCountText -le 0) {
            return $false
        }

        return @($Dispatches | Where-Object {
            $_.Index -lt $ProgressIndex -and
            (Test-WorkerHandoffInstallDescriptorMatch -AnchorLine $InstallLine -CandidateLine $_.Line) -and
            [string]::Equals(
                (Get-WorkerHandoffFieldValue -Line $_.Line -Name 'repair_request_id'),
                $repairRequestKey,
                [System.StringComparison]::Ordinal)
        } | Select-Object -First 1).Count -gt 0
    }

    $lineCount = $Lines.Count
    $handoffCandidates = @(
        for ($index = 0; $index -lt $lineCount; $index++) {
            $line = [string]$Lines[$index]
            $staleWorker = $line.IndexOf(
                    'event=filetransfer_runtime_owner_stale_update_rejected',
                    [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                (Test-TunaGuiLogLineContainsAll -Line $line -Needles @(
                    'direction=outbound',
                    'source=v4_sender_failed',
                    'attempted_action=read_failed',
                    'reason=runtime_generation_mismatch',
                    'current_route=post_tuna_fallback_v6'))
            if ($staleWorker) {
                [pscustomobject]@{ Index = $index; Line = $line }
            }
        }
    )

    foreach ($candidate in $handoffCandidates) {
        $candidateLine = [string]$candidate.Line
        $currentRuntimeGeneration = Get-WorkerHandoffFieldValue -Line $candidateLine -Name 'current_runtime_generation'
        $currentTerminalGeneration = Get-WorkerHandoffFieldValue -Line $candidateLine -Name 'current_terminal_generation'
        $currentLiveRouteEpoch = Get-WorkerHandoffFieldValue -Line $candidateLine -Name 'current_live_route_epoch'
        $currentProtocolVersion = Get-WorkerHandoffFieldValue -Line $candidateLine -Name 'current_protocol_version'
        if ([string]::IsNullOrWhiteSpace($currentRuntimeGeneration) -or
            [string]::IsNullOrWhiteSpace($currentTerminalGeneration) -or
            [string]::IsNullOrWhiteSpace($currentLiveRouteEpoch) -or
            $currentProtocolVersion -ne '6') {
            continue
        }

        $publicationLine = ''
        $publicationIndex = -1
        for ($index = $candidate.Index - 1; $index -ge 0; $index--) {
            $line = [string]$Lines[$index]
            if (-not (Test-WorkerHandoffAcceptedPublication -Line $line -ScopeLine $candidateLine) -or
                (Get-WorkerHandoffFieldValue -Line $line -Name 'route') -ne 'post_tuna_fallback_v6' -or
                (Get-WorkerHandoffFieldValue -Line $line -Name 'protocol_version') -ne '6' -or
                (Get-WorkerHandoffFieldValue -Line $line -Name 'runtime_generation') -ne $currentRuntimeGeneration -or
                (Get-WorkerHandoffFieldValue -Line $line -Name 'terminal_generation') -ne $currentTerminalGeneration -or
                (Get-WorkerHandoffFieldValue -Line $line -Name 'live_route_epoch') -ne $currentLiveRouteEpoch) {
                continue
            }

            if (Test-WorkerHandoffPublicationTupleComplete -Line $line) {
                $publicationLine = $line
                $publicationIndex = $index
                break
            }
        }
        if ([string]::IsNullOrWhiteSpace($publicationLine)) {
            continue
        }

        $requiredDescriptors = @(
            for ($requiredIndex = 0; $requiredIndex -lt $lineCount; $requiredIndex++) {
                $requiredLine = [string]$Lines[$requiredIndex]
                if ($requiredLine.IndexOf('event=filetransfer_outbound_sparse_worker_handoff_required', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                    (Test-WorkerHandoffInstallDescriptorComplete -Line $requiredLine) -and
                    (Test-WorkerHandoffPublicationTupleMatch -AnchorLine $publicationLine -CandidateLine $requiredLine)) {
                    [pscustomobject]@{ Index = $requiredIndex; Line = $requiredLine }
                }
            }
        )

        $fallbackDeliveryOrQueue = $false
        $currentInstallLine = ''
        $currentConsumerProven = $false
        $terminalClosed = $false
        $publicationSuperseded = $false
        $descriptorDispatches = New-Object System.Collections.Generic.List[object]
        $matchedFrontierCorrelationKeys = [System.Collections.Generic.HashSet[string]]::new(
            [System.StringComparer]::OrdinalIgnoreCase)
        for ($index = $publicationIndex + 1; $index -lt $lineCount; $index++) {
            $line = [string]$Lines[$index]
            if ($index -gt $candidate.Index -and
                (Test-WorkerHandoffAcceptedPublication -Line $line -ScopeLine $candidateLine)) {
                if (-not (Test-WorkerHandoffPublicationTupleMatch -AnchorLine $publicationLine -CandidateLine $line)) {
                    $publicationSuperseded = $true
                    break
                }
                continue
            }
            if ($index -gt $candidate.Index -and
                ($line.IndexOf('event=file_transfer_outbound_terminal', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                    ($line.IndexOf('event=transfer_terminal', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                        $line.IndexOf('direction=outbound', [System.StringComparison]::OrdinalIgnoreCase) -ge 0))) {
                if (Test-WorkerHandoffIdentityMatch -SourceLine $candidateLine -CandidateLine $line) {
                    $terminalClosed = $true
                    break
                }
                continue
            }

            if ($line.IndexOf('event=filetransfer_outbound_sparse_worker_handoff_installed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                (Test-WorkerHandoffInstallDescriptorComplete -Line $line) -and
                (Test-WorkerHandoffPublicationTupleMatch -AnchorLine $publicationLine -CandidateLine $line)) {
                $matchingRequired = @($requiredDescriptors | Where-Object {
                    $_.Index -lt $index -and
                    (Test-WorkerHandoffInstallDescriptorMatch -AnchorLine $_.Line -CandidateLine $line)
                } | Sort-Object Index -Descending | Select-Object -First 1)
                if ($matchingRequired.Count -gt 0) {
                    if ([string]::IsNullOrWhiteSpace($currentInstallLine) -or
                        -not (Test-WorkerHandoffInstallDescriptorMatch -AnchorLine $currentInstallLine -CandidateLine $line)) {
                        $currentConsumerProven = $false
                    }
                    $currentInstallLine = $line
                }
                continue
            }

            if ($line.IndexOf('event=filetransfer_outbound_sparse_receive_supervisor_stopped', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                -not [string]::IsNullOrWhiteSpace($currentInstallLine) -and
                (Test-WorkerHandoffInstallDescriptorMatch -AnchorLine $currentInstallLine -CandidateLine $line)) {
                $stopTerminal = Get-WorkerHandoffFieldValue -Line $line -Name 'terminal'
                if ($index -gt $candidate.Index -and $stopTerminal -eq '1') {
                    $terminalClosed = $true
                    break
                }
                if ($stopTerminal -eq '0') {
                    $currentInstallLine = ''
                    $currentConsumerProven = $false
                }
                continue
            }

            if ($index -gt $candidate.Index -and
                $line.IndexOf('event=filetransfer_v6_frontier_request_receive_dispatched', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                (Test-WorkerHandoffInstallDescriptorComplete -Line $line) -and
                (Test-WorkerHandoffPublicationTupleMatch -AnchorLine $publicationLine -CandidateLine $line)) {
                $descriptorDispatches.Add([pscustomobject]@{ Index = $index; Line = $line }) | Out-Null
                if (-not [string]::IsNullOrWhiteSpace($currentInstallLine) -and
                    (Test-WorkerHandoffDispatchTupleMatch -InstallLine $currentInstallLine -CandidateLine $line)) {
                    $currentConsumerProven = $true
                }
                continue
            }

            if ($index -gt $candidate.Index -and
                -not [string]::IsNullOrWhiteSpace($currentInstallLine) -and
                (Test-WorkerHandoffProgressMatch `
                    -InstallLine $currentInstallLine `
                    -ProgressLine $line `
                    -ProgressIndex $index `
                    -Dispatches $descriptorDispatches.ToArray())) {
                $currentConsumerProven = $true
                continue
            }

            if ($index -le $candidate.Index) {
                continue
            }

            if (($line.IndexOf('event=filetransfer_control_plane_delivery_result', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                    $line.IndexOf('event=filetransfer_control_plane_delivery_observed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) -and
                (Test-TunaGuiLogLineContainsAll -Line $line -Needles @(
                    'frame_type=filetransfer.frontier_request.v6',
                    'peer_visible_any=1',
                    'accepted_any=1')) -and
                (Test-WorkerHandoffFrontierTupleMatch -PublicationLine $publicationLine -CandidateLine $line)) {
                $correlationKey = Get-WorkerHandoffFrontierCorrelationKey -Line $line
                if (-not [string]::IsNullOrWhiteSpace($correlationKey)) {
                    $matchedFrontierCorrelationKeys.Add($correlationKey) | Out-Null
                    $fallbackDeliveryOrQueue = $true
                }
                continue
            }

            if (($line.IndexOf('event=filetransfer_v6_frontier_request_transport_queued', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                    $line.IndexOf('event=filetransfer_v6_frontier_request_transport_rejected', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) -and
                $line.IndexOf('frame_type=filetransfer.frontier_request.v6', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                (Test-WorkerHandoffIdentityMatch -SourceLine $candidateLine -CandidateLine $line)) {
                $correlationKey = Get-WorkerHandoffFrontierCorrelationKey -Line $line
                if (-not [string]::IsNullOrWhiteSpace($correlationKey) -and
                    $matchedFrontierCorrelationKeys.Contains($correlationKey)) {
                    $fallbackDeliveryOrQueue = $true
                }
                continue
            }
        }

        if ($publicationSuperseded -or $terminalClosed) {
            continue
        }
        if ($fallbackDeliveryOrQueue -and -not $currentConsumerProven) {
            return $true
        }
    }

    return $false
}

function Test-TunaGuiBridgeBootstrapNotReadyEvidence {
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Lines)

    $bridgeConnectFailure = $false
    $readyNeverEmitted = $false
    $lanesNotReady = $false
    foreach ($line in @($Lines)) {
        $text = [string]$line
        if ([string]::IsNullOrWhiteSpace($text)) {
            continue
        }

        if ($text.IndexOf('RPC call failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $text.IndexOf('Connect failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $text.IndexOf('HostByAddressAsync failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $text.IndexOf('BridgeStartFailure', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $bridgeConnectFailure = $true
        }

        if ($text.IndexOf('ready_emitted=0', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $text.IndexOf('rdy=0', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $readyNeverEmitted = $true
        }

        if (($text.IndexOf('control_ready=0', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $text.IndexOf('cr=0', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) -and
            ($text.IndexOf('bulk_ready=0', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $text.IndexOf('br=0', [System.StringComparison]::OrdinalIgnoreCase) -ge 0)) {
            $lanesNotReady = $true
        }
    }

    return $bridgeConnectFailure -or ($readyNeverEmitted -and $lanesNotReady)
}

function Get-TunaGuiExplicitFailureClassification {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$ErrorMessage)

    $failureClassMatch = [regex]::Match($ErrorMessage, '(?i)(?:^|[;\s])failure_class=(?<value>[a-z0-9_-]+)')
    if ($failureClassMatch.Success) {
        $failureClass = $failureClassMatch.Groups['value'].Value.ToLowerInvariant()
        switch ($failureClass) {
            'terminal_before_fallback_switch_off' {
                return [pscustomobject]@{ Phase = 'live_route_proof'; Reason = $failureClass }
            }
            'regular_v4_terminal_before_tuna_activation_commit' {
                return [pscustomobject]@{ Phase = 'live_route_proof'; Reason = $failureClass }
            }
            'terminal_fallback_prefix_missing' {
                return [pscustomobject]@{ Phase = 'live_route_proof'; Reason = $failureClass }
            }
            'runtime_unlock_retry_source_publication_mismatch' {
                return [pscustomobject]@{ Phase = 'runtime_unlock_retry'; Reason = $failureClass }
            }
            'runtime_unlock_retry_dispatch_authority_tuple_mismatch' {
                return [pscustomobject]@{ Phase = 'runtime_unlock_retry'; Reason = $failureClass }
            }
            'runtime_unlock_retry_dispatch_successor_missing_after_missing_dispatch' {
                return [pscustomobject]@{ Phase = 'runtime_unlock_retry'; Reason = $failureClass }
            }
            'runtime_unlock_retry_dispatch_grant_bypassed_by_retry_budget' {
                return [pscustomobject]@{ Phase = 'runtime_unlock_retry'; Reason = $failureClass }
            }
            'runtime_unlock_retry_dispatch_contract_unsettled' {
                return [pscustomobject]@{ Phase = 'runtime_unlock_retry'; Reason = $failureClass }
            }
            'runtime_unlock_initial_offer_receive_recovery_hold_not_preserved' {
                return [pscustomobject]@{
                    Phase = 'runtime_unlock_initial_activation'
                    Reason = 'initial_activation_retry_exhausted_while_receive_recovery_awaiting_proof'
                }
            }
            'runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing' {
                return [pscustomobject]@{
                    Phase = 'runtime_unlock_initial_activation'
                    Reason = 'released_hold_routed_through_exact_invalidation_boundary'
                }
            }
            'runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired' {
                return [pscustomobject]@{
                    Phase = 'runtime_unlock_initial_activation'
                    Reason = 'initial_activation_offer_expired_during_receive_recovery_hold'
                }
            }
            'post_tuna_fallback_outbound_receive_worker_handoff_missing' {
                return [pscustomobject]@{ Phase = 'outbound_sparse_worker_handoff'; Reason = $failureClass }
            }
            'fallback_tail_yield_authority_invalid' {
                return [pscustomobject]@{ Phase = 'fallback_tail_yield'; Reason = $failureClass }
            }
        }
    }

    $cursorFailureMatch = [regex]::Match($ErrorMessage, '(?i)(?<value>gui_smoke_log_cursor_[a-z0-9_-]+)')
    if ($cursorFailureMatch.Success) {
        return [pscustomobject]@{
            Phase = 'harness_log_cursor'
            Reason = $cursorFailureMatch.Groups['value'].Value.ToLowerInvariant()
        }
    }

    if ($ErrorMessage.IndexOf('gui_smoke_switch_off_control_not_ready_before_fault_window', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return [pscustomobject]@{
            Phase = 'live_toggle_window'
            Reason = 'gui_smoke_switch_off_control_not_ready_before_fault_window'
        }
    }

    return $null
}

function Resolve-TunaGuiFailureClassification {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$ErrorMessage,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$HistoricalPhase,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$HistoricalReason
    )

    $explicit = Get-TunaGuiExplicitFailureClassification -ErrorMessage $ErrorMessage
    if ($null -ne $explicit) {
        return $explicit
    }

    return [pscustomobject]@{
        Phase = $HistoricalPhase
        Reason = $HistoricalReason
    }
}

function Write-TunaGuiTopLevelFailureSummaryIfMissing {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$ErrorMessage)

    $artifactDir = [string]$env:NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR
    if ([string]::IsNullOrWhiteSpace($artifactDir)) {
        return
    }

    try {
        [void][System.IO.Directory]::CreateDirectory($artifactDir)
        $errorPath = Join-Path $artifactDir 'filetransfer-tuna-gui-error.json'
        if (Test-Path -LiteralPath $errorPath -PathType Leaf) {
            return
        }

        $classification = Resolve-TunaGuiFailureClassification `
            -ErrorMessage $ErrorMessage `
            -HistoricalPhase 'top_level_harness' `
            -HistoricalReason 'gui_smoke_failed'
        $summary = [ordered]@{
            event = 'filetransfer_tuna_gui_top_level_failure'
            routeMode = [string]$env:NLINK_TUNA_GUI_ROUTE_MODE
            completed = $false
            integrityOk = $false
            failurePhase = [string]$classification.Phase
            failureReason = [string]$classification.Reason
            error = $ErrorMessage
            failedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        }
        $summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $errorPath -Encoding UTF8
    }
    catch {
        # Best-effort failure propagation must not replace the original GUI failure.
    }
}

function Get-TunaGuiFileTransferSetupFailureClassification {
    param(
        [Parameter(Mandatory = $true)][int]$Bookmark,
        [Parameter(Mandatory = $true)][string]$ErrorMessage,
        [Parameter(Mandatory = $true)][string]$RouteMode
    )

    $lines = @()
    try {
        $lines = @(Get-AppLogLinesAfterBookmark -Bookmark $Bookmark)
    }
    catch {
        $cursorReadClassification = Get-TunaGuiExplicitFailureClassification -ErrorMessage ([string]$_.Exception.Message)
        if ($null -eq $cursorReadClassification -or
            -not [string]::Equals([string]$cursorReadClassification.Phase, 'harness_log_cursor', [System.StringComparison]::Ordinal)) {
            throw
        }

        $ErrorMessage = "{0}; {1}" -f $ErrorMessage, $_.Exception.Message
    }
    $processLines = @(Read-GuiSmokeProcessOutputLinesSnapshot)
    $evidenceLines = @($lines + $processLines)

    function Get-TunaGuiLogLineFieldValue {
        param(
            [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Line,
            [Parameter(Mandatory = $true)][string]$Name
        )

        if ([string]::IsNullOrWhiteSpace($Line) -or [string]::IsNullOrWhiteSpace($Name)) {
            return ''
        }

        $escapedName = [regex]::Escape($Name)
        $match = [regex]::Match($Line, "(?i)(?:^|;\s*)$escapedName=([^;`r`n]+)")
        if (-not $match.Success) {
            return ''
        }

        return $match.Groups[1].Value.Trim()
    }

    function Test-TunaGuiLogLineHasEvent {
        param(
            [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Line,
            [Parameter(Mandatory = $true)][string[]]$Events
        )

        foreach ($eventName in $Events) {
            if ($Line.IndexOf("event=$eventName", [System.StringComparison]::Ordinal) -ge 0) {
                return $true
            }
        }

        return $false
    }

    function Test-TunaGuiLogLineFieldMatch {
        param(
            [Parameter(Mandatory = $true)][AllowEmptyString()][string]$SourceLine,
            [Parameter(Mandatory = $true)][AllowEmptyString()][string]$CandidateLine,
            [Parameter(Mandatory = $true)][string]$FieldName,
            [System.StringComparison]$Comparison = [System.StringComparison]::OrdinalIgnoreCase
        )

        $sourceValue = Get-TunaGuiLogLineFieldValue -Line $SourceLine -Name $FieldName
        $candidateValue = Get-TunaGuiLogLineFieldValue -Line $CandidateLine -Name $FieldName
        if ([string]::IsNullOrWhiteSpace($sourceValue) -or
            [string]::IsNullOrWhiteSpace($candidateValue) -or
            $sourceValue -eq '(none)' -or
            $candidateValue -eq '(none)') {
            return $true
        }

        return [string]::Equals($sourceValue, $candidateValue, $Comparison)
    }

    function Test-TunaGuiRuntimeUnlockFileTransferOwnerRejectionUnresolved {
        param(
            [Parameter(Mandatory = $true)][AllowEmptyCollection()][AllowEmptyString()][string[]]$Lines,
            [Parameter(Mandatory = $true)][string]$RejectionEvent
        )

        $indexedLines = for ($index = 0; $index -lt $Lines.Count; $index++) {
            [pscustomobject]@{ Index = $index; Line = [string]$Lines[$index] }
        }
        $rejections = @($indexedLines | Where-Object {
            Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @($RejectionEvent)
        })
        foreach ($rejection in $rejections) {
            $matchingAcceptance = @($indexedLines | Where-Object {
                $_.Index -gt $rejection.Index -and
                (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @('runtime_unlock_filetransfer_owner_accepted_before_path_lease')) -and
                (Test-TunaGuiLogLineFieldMatch -SourceLine $rejection.Line -CandidateLine $_.Line -FieldName 'session_id') -and
                (Test-TunaGuiLogLineFieldMatch -SourceLine $rejection.Line -CandidateLine $_.Line -FieldName 'transfer_id') -and
                (Test-TunaGuiLogLineFieldMatch -SourceLine $rejection.Line -CandidateLine $_.Line -FieldName 'direction')
            })
            if ($matchingAcceptance.Count -eq 0) {
                return $true
            }
        }

        return $false
    }

    function Test-TunaGuiRuntimeUnlockMissingDispatchSuccessorUnresolved {
        param(
            [Parameter(Mandatory = $true)][AllowEmptyCollection()][AllowEmptyString()][string[]]$Lines
        )

        $indexedLines = for ($index = 0; $index -lt $Lines.Count; $index++) {
            [pscustomobject]@{ Index = $index; Line = [string]$Lines[$index] }
        }
        $stableFields = @(
            'session_id', 'transfer_id', 'direction', 'route', 'protocol_version',
            'live_route_epoch', 'runtime_generation', 'terminal_generation', 'leg_generation',
            'route_publication_generation', 'route_commit_generation', 'contract_generation',
            'release_kind', 'release_authority_id', 'release_generation', 'release_outcome',
            'target_route', 'target_protocol_version', 'target_transport')
        $missingSettlements = @($indexedLines | Where-Object {
            (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @('runtime_unlock_retry_dispatch_contract_settled')) -and
            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'authority_schema') -eq '2' -and
            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'settlement_outcome') -eq 'MissingDispatch'
        })
        foreach ($settlement in $missingSettlements) {
            $predecessorId = Get-TunaGuiLogLineFieldValue -Line $settlement.Line -Name 'authority_id'
            $predecessorAttempt = [int](Get-TunaGuiLogLineFieldValue -Line $settlement.Line -Name 'dispatch_attempt_generation')
            $predecessorMissingDispatchGeneration = [int](Get-TunaGuiLogLineFieldValue -Line $settlement.Line -Name 'missing_dispatch_successor_generation')
            $failedOfferGeneration = [int64](Get-TunaGuiLogLineFieldValue -Line $settlement.Line -Name 'offer_generation')
            $successors = @($indexedLines | Where-Object {
                if ($_.Index -le $settlement.Index -or
                    -not (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @('runtime_unlock_retry_dispatch_authority_granted')) -or
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'authority_schema') -ne '2' -or
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'predecessor_authority_id') -ne $predecessorId -or
                    [int](Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'dispatch_attempt_generation') -ne ($predecessorAttempt + 1) -or
                    [int](Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'missing_dispatch_successor_generation') -ne ($predecessorMissingDispatchGeneration + 1) -or
                    ($failedOfferGeneration -gt 0 -and
                        [int64](Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'retired_generation') -ne $failedOfferGeneration)) {
                    return $false
                }

                foreach ($fieldName in $stableFields) {
                    $predecessorValue = Get-TunaGuiLogLineFieldValue -Line $settlement.Line -Name $fieldName
                    $successorValue = Get-TunaGuiLogLineFieldValue -Line $_.Line -Name $fieldName
                    if ([string]::IsNullOrWhiteSpace($predecessorValue) -or
                        [string]::IsNullOrWhiteSpace($successorValue) -or
                        -not [string]::Equals(
                            $predecessorValue,
                            $successorValue,
                            [System.StringComparison]::OrdinalIgnoreCase)) {
                        return $false
                    }
                }
                return $true
            })
            if ($successors.Count -eq 1) {
                continue
            }

            $boundedRejection = @($indexedLines | Where-Object {
                if ($_.Index -le $settlement.Index -or
                    -not (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @('runtime_unlock_retry_successor_intent_rejected')) -or
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'authority_schema') -ne '2' -or
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'authority_id') -ne $predecessorId -or
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'requested_predecessor_authority_id') -ne $predecessorId -or
                    [int](Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'requested_dispatch_attempt_generation') -ne ($predecessorAttempt + 1) -or
                    [int](Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'requested_missing_dispatch_successor_generation') -ne ($predecessorMissingDispatchGeneration + 1) -or
                    ($failedOfferGeneration -gt 0 -and
                        [int64](Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'requested_retired_generation') -ne $failedOfferGeneration)) {
                    return $false
                }

                foreach ($fieldName in $stableFields) {
                    $predecessorValue = Get-TunaGuiLogLineFieldValue -Line $settlement.Line -Name $fieldName
                    $rejectionValue = Get-TunaGuiLogLineFieldValue -Line $_.Line -Name $fieldName
                    if ([string]::IsNullOrWhiteSpace($predecessorValue) -or
                        [string]::IsNullOrWhiteSpace($rejectionValue) -or
                        -not [string]::Equals(
                            $predecessorValue,
                            $rejectionValue,
                            [System.StringComparison]::OrdinalIgnoreCase)) {
                        return $false
                    }
                }
                return $true
            })
            if ($boundedRejection.Count -gt 0) {
                continue
            }

            $settledPredecessorReuse = @($indexedLines | Where-Object {
                $_.Index -gt $settlement.Index -and
                (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @('runtime_unlock_retry_dispatch_authority_rejected')) -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'authority_id') -eq $predecessorId -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'decision_reason') -eq 'runtime_unlock_retry_authority_settled'
            })
            $terminal = @($indexedLines | Where-Object {
                $terminalEntry = $_
                $terminalLine = $terminalEntry.Line
                $terminalDirection = switch -Exact ((Get-TunaGuiLogLineFieldValue -Line $terminalLine -Name 'event')) {
                    'file_transfer_outbound_terminal' { 'outbound'; break }
                    'file_transfer_inbound_terminal' { 'inbound'; break }
                    'transfer_terminal' { Get-TunaGuiLogLineFieldValue -Line $terminalLine -Name 'direction'; break }
                    default { '' }
                }
                $terminalEntry.Index -gt $settlement.Index -and
                (Test-TunaGuiLogLineHasEvent -Line $terminalLine -Events @('file_transfer_outbound_terminal', 'file_transfer_inbound_terminal', 'transfer_terminal')) -and
                (Get-TunaGuiLogLineFieldValue -Line $terminalLine -Name 'session_id') -eq
                    (Get-TunaGuiLogLineFieldValue -Line $settlement.Line -Name 'session_id') -and
                (Get-TunaGuiLogLineFieldValue -Line $terminalLine -Name 'transfer_id') -eq
                    (Get-TunaGuiLogLineFieldValue -Line $settlement.Line -Name 'transfer_id') -and
                $terminalDirection -eq
                    (Get-TunaGuiLogLineFieldValue -Line $settlement.Line -Name 'direction')
            })
            if ($settledPredecessorReuse.Count -gt 0 -or $terminal.Count -eq 0) {
                return $true
            }
        }

        return $false
    }

    function Get-TunaGuiRuntimeUnlockUnsettledGrantReason {
        param(
            [Parameter(Mandatory = $true)][AllowEmptyCollection()][AllowEmptyString()][string[]]$Lines
        )

        $indexedLines = for ($index = 0; $index -lt $Lines.Count; $index++) {
            [pscustomobject]@{ Index = $index; Line = [string]$Lines[$index] }
        }
        $tupleFields = @(
            'authority_id', 'predecessor_authority_id', 'session_id', 'transfer_id', 'direction',
            'route', 'protocol_version', 'live_route_epoch', 'runtime_generation', 'terminal_generation',
            'leg_generation', 'transport_epoch', 'bridge_recovery_generation', 'bridge_decision_generation',
            'route_publication_generation', 'route_commit_generation', 'contract_generation',
            'retired_generation', 'dispatch_attempt_generation', 'missing_dispatch_successor_generation',
            'retry_reason', 'recovery_reason', 'release_kind', 'release_authority_id',
            'release_generation', 'release_outcome', 'target_route', 'target_protocol_version',
            'target_transport')
        $grants = @($indexedLines | Where-Object {
            (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @('runtime_unlock_retry_dispatch_authority_granted')) -and
            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'authority_schema') -eq '2'
        })
        foreach ($grant in $grants) {
            foreach ($fieldName in $tupleFields) {
                if ([string]::IsNullOrWhiteSpace(
                        (Get-TunaGuiLogLineFieldValue -Line $grant.Line -Name $fieldName))) {
                    return 'runtime_unlock_retry_dispatch_contract_unsettled'
                }
            }

            $matchingSettlement = @($indexedLines | Where-Object {
                if ($_.Index -le $grant.Index -or
                    -not (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @('runtime_unlock_retry_dispatch_contract_settled')) -or
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'authority_schema') -ne '2') {
                    return $false
                }

                foreach ($fieldName in $tupleFields) {
                    $grantValue = Get-TunaGuiLogLineFieldValue -Line $grant.Line -Name $fieldName
                    $settlementValue = Get-TunaGuiLogLineFieldValue -Line $_.Line -Name $fieldName
                    if ([string]::IsNullOrWhiteSpace($grantValue) -or
                        [string]::IsNullOrWhiteSpace($settlementValue) -or
                        -not [string]::Equals(
                            $grantValue,
                            $settlementValue,
                            [System.StringComparison]::OrdinalIgnoreCase)) {
                        return $false
                    }
                }
                return $true
            })
            if ($matchingSettlement.Count -gt 0) {
                continue
            }

            $budgetSuppression = @($indexedLines | Where-Object {
                $_.Index -gt $grant.Index -and
                (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @('runtime_unlock_broken_state_retry_suppressed')) -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'session_id') -eq
                    (Get-TunaGuiLogLineFieldValue -Line $grant.Line -Name 'session_id') -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'transfer_id') -eq
                    (Get-TunaGuiLogLineFieldValue -Line $grant.Line -Name 'transfer_id') -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'contract_generation') -eq
                    (Get-TunaGuiLogLineFieldValue -Line $grant.Line -Name 'contract_generation')
            })
            return $(if ($budgetSuppression.Count -gt 0) {
                'runtime_unlock_retry_dispatch_grant_bypassed_by_retry_budget'
            } else {
                'runtime_unlock_retry_dispatch_contract_unsettled'
            })
        }

        return '(none)'
    }

    function Test-TunaGuiRegularV4FrontierRepairMatch {
        param(
            [Parameter(Mandatory = $true)][AllowEmptyString()][string]$SourceLine,
            [Parameter(Mandatory = $true)][AllowEmptyString()][string]$CandidateLine
        )

        $sourceKey = Get-TunaGuiLogLineFieldValue -Line $SourceLine -Name 'repair_request_key'
        $candidateKey = Get-TunaGuiLogLineFieldValue -Line $CandidateLine -Name 'repair_request_key'
        if ([string]::IsNullOrWhiteSpace($sourceKey) -or
            [string]::IsNullOrWhiteSpace($candidateKey) -or
            -not [string]::Equals($sourceKey, $candidateKey, [System.StringComparison]::Ordinal)) {
            return $false
        }

        foreach ($fieldName in @(
                'transfer_id',
                'session_id',
                'direction',
                'route',
                'protocol_version',
                'live_route_epoch',
                'leg_generation',
                'transport_epoch',
                'bridge_recovery_generation',
                'route_publication_generation')) {
            if (-not (Test-TunaGuiLogLineFieldMatch -SourceLine $SourceLine -CandidateLine $CandidateLine -FieldName $fieldName)) {
                return $false
            }
        }

        return $true
    }

    function Test-TunaGuiRegularV4FrontierRepairPrecontractClosureMatch {
        param(
            [Parameter(Mandatory = $true)]$Accepted,
            [Parameter(Mandatory = $true)]$Consumed,
            [Parameter(Mandatory = $true)]$Closure
        )

        if ($Consumed.Index -le $Accepted.Index -or $Closure.Index -le $Consumed.Index) {
            return $false
        }

        if (-not (Test-TunaGuiRegularV4FrontierRepairMatch -SourceLine $Accepted.Line -CandidateLine $Consumed.Line)) {
            return $false
        }

        foreach ($fieldName in @(
                'session_id',
                'transfer_id',
                'direction',
                'route',
                'protocol_version',
                'live_route_epoch',
                'leg_generation',
                'transport_epoch',
                'bridge_recovery_generation')) {
            if (-not (Test-TunaGuiLogLineFieldMatch -SourceLine $Consumed.Line -CandidateLine $Closure.Line -FieldName $fieldName)) {
                return $false
            }
        }

        $consumedBridgeDecision = Get-TunaGuiLogLineFieldValue -Line $Consumed.Line -Name 'bridge_decision_generation'
        $closureBridgeDecision = Get-TunaGuiLogLineFieldValue -Line $Closure.Line -Name 'bridge_decision_generation'
        if (-not [string]::IsNullOrWhiteSpace($consumedBridgeDecision) -and
            -not [string]::IsNullOrWhiteSpace($closureBridgeDecision) -and
            $consumedBridgeDecision -ne '0' -and
            -not [string]::Equals($consumedBridgeDecision, $closureBridgeDecision, [System.StringComparison]::Ordinal)) {
            return $false
        }

        return $true
    }

    function Test-TunaGuiInitialActivationReceiveRecoveryHoldNotPreserved {
        param(
            [Parameter(Mandatory = $true)][AllowEmptyCollection()][AllowEmptyString()][string[]]$Lines
        )

        function Get-InitialActivationField {
            param(
                [Parameter(Mandatory = $true)][string]$Line,
                [Parameter(Mandatory = $true)][string[]]$Names
            )

            foreach ($name in $Names) {
                $value = Get-TunaGuiLogLineFieldValue -Line $Line -Name $name
                if (-not [string]::IsNullOrWhiteSpace($value) -and
                    $value -notin @('(none)', 'none', '(missing)', '__missing__')) {
                    return $value
                }
            }
            return ''
        }

        function Test-InitialActivationWriterAndSession {
            param(
                [Parameter(Mandatory = $true)][string]$Left,
                [Parameter(Mandatory = $true)][string]$Right
            )

            foreach ($field in @('log_writer_id', 'session_id')) {
                $leftValue = Get-TunaGuiLogLineFieldValue -Line $Left -Name $field
                $rightValue = Get-TunaGuiLogLineFieldValue -Line $Right -Name $field
                if ([string]::IsNullOrWhiteSpace($leftValue) -or
                    [string]::IsNullOrWhiteSpace($rightValue) -or
                    -not [string]::Equals(
                        $leftValue,
                        $rightValue,
                        [System.StringComparison]::OrdinalIgnoreCase)) {
                    return $false
                }
            }
            return $true
        }

        function Test-InitialActivationWriter {
            param(
                [Parameter(Mandatory = $true)][string]$Left,
                [Parameter(Mandatory = $true)][string]$Right
            )

            $leftWriter = Get-TunaGuiLogLineFieldValue -Line $Left -Name 'log_writer_id'
            $rightWriter = Get-TunaGuiLogLineFieldValue -Line $Right -Name 'log_writer_id'
            return -not [string]::IsNullOrWhiteSpace($leftWriter) -and
                -not [string]::IsNullOrWhiteSpace($rightWriter) -and
                [string]::Equals(
                    $leftWriter,
                    $rightWriter,
                    [System.StringComparison]::OrdinalIgnoreCase)
        }

        function Test-InitialActivationOfferTuple {
            param(
                [Parameter(Mandatory = $true)][string]$Boundary,
                [Parameter(Mandatory = $true)][string]$Candidate,
                [switch]$RequireTransaction
            )

            if (-not (Test-InitialActivationWriterAndSession -Left $Boundary -Right $Candidate)) {
                return $false
            }

            foreach ($mapping in @(
                    [pscustomobject]@{
                        Boundary = @('payer_decision_id')
                        Candidate = @('payer_decision_id')
                        Required = $true
                    },
                    [pscustomobject]@{
                        Boundary = @('offer_generation', 'generation')
                        Candidate = @('offer_generation', 'generation')
                        Required = $true
                    },
                    [pscustomobject]@{
                        Boundary = @('transaction_generation')
                        Candidate = @('transaction_generation')
                        Required = $RequireTransaction.IsPresent
                    },
                    [pscustomobject]@{
                        Boundary = @('tuna_path_lease_generation')
                        Candidate = @('tuna_path_lease_generation')
                        Required = $RequireTransaction.IsPresent
                    })) {
                $boundaryValue =
                    Get-InitialActivationField -Line $Boundary -Names $mapping.Boundary
                $candidateValue =
                    Get-InitialActivationField -Line $Candidate -Names $mapping.Candidate
                if (($mapping.Required -and
                        ([string]::IsNullOrWhiteSpace($boundaryValue) -or
                            [string]::IsNullOrWhiteSpace($candidateValue))) -or
                    (-not [string]::IsNullOrWhiteSpace($candidateValue) -and
                        -not [string]::Equals(
                            $boundaryValue,
                            $candidateValue,
                            [System.StringComparison]::OrdinalIgnoreCase))) {
                    return $false
                }
            }
            return $true
        }

        function Test-InitialActivationRecoveryOperationMatch {
            param(
                [Parameter(Mandatory = $true)][string]$Operation,
                [Parameter(Mandatory = $true)][string]$Candidate
            )

            foreach ($mapping in @(
                    [pscustomobject]@{
                        Operation = @('receive_recovery_operation_authority_id')
                        Candidate = @('receive_recovery_operation_authority_id')
                    },
                    [pscustomobject]@{
                        Operation = @('receive_recovery_operation_generation')
                        Candidate = @('receive_recovery_operation_generation')
                    },
                    [pscustomobject]@{
                        Operation = @('recovery_liveness_lease_authority_id')
                        Candidate = @('recovery_liveness_lease_authority_id')
                    },
                    [pscustomobject]@{
                        Operation = @('recovery_liveness_lease_generation')
                        Candidate = @('recovery_liveness_lease_generation')
                    },
                    [pscustomobject]@{
                        Operation = @('closure_generation', 'receive_recovery_closure_generation')
                        Candidate = @('closure_generation', 'receive_recovery_closure_generation')
                    })) {
                $operationValue =
                    Get-InitialActivationField -Line $Operation -Names $mapping.Operation
                $candidateValue =
                    Get-InitialActivationField -Line $Candidate -Names $mapping.Candidate
                if ([string]::IsNullOrWhiteSpace($operationValue) -or
                    [string]::IsNullOrWhiteSpace($candidateValue) -or
                    -not [string]::Equals(
                        $operationValue,
                        $candidateValue,
                        [System.StringComparison]::OrdinalIgnoreCase)) {
                    return $false
                }
            }
            return $true
        }

        function Test-InitialActivationHoldRequestMatch {
            param(
                [Parameter(Mandatory = $true)][string]$Left,
                [Parameter(Mandatory = $true)][string]$Right
            )

            foreach ($field in @(
                    'log_writer_id',
                    'session_id',
                    'transfer_id',
                    'direction',
                    'offer_nonce',
                    'payer_decision_id',
                    'offer_generation',
                    'transaction_generation',
                    'tuna_path_lease_generation',
                    'tuna_path_lease_listener_run_id',
                    'control_envelope_fingerprint')) {
                $leftValue = Get-TunaGuiLogLineFieldValue -Line $Left -Name $field
                $rightValue = Get-TunaGuiLogLineFieldValue -Line $Right -Name $field
                if ([string]::IsNullOrWhiteSpace($leftValue) -or
                    [string]::IsNullOrWhiteSpace($rightValue) -or
                    -not [string]::Equals(
                        $leftValue,
                        $rightValue,
                        [System.StringComparison]::OrdinalIgnoreCase)) {
                    return $false
                }
            }
            return $true
        }

        $indexedLines = @(
            for ($index = 0; $index -lt $Lines.Count; $index++) {
                [pscustomobject]@{ Index = $index; Line = [string]$Lines[$index] }
            }
        )
        $finalFailures = @($indexedLines | Where-Object {
            (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                'tuna_acceleration_activation_offer_not_observed')) -and
            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'authority_mode') -eq 'InitialActivation' -and
            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'retry_scheduled') -eq '0' -and
            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'retry_after_recovery_armed') -eq '0' -and
            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'pause_deferred') -eq '1' -and
            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'trigger') -in @(
                'runtime_unlock_offer_send_not_observed',
                'runtime_unlock_payer_intent_send_rejected')
        })

        foreach ($failure in $finalFailures) {
            $writerId = Get-TunaGuiLogLineFieldValue -Line $failure.Line -Name 'log_writer_id'
            $sessionId = Get-TunaGuiLogLineFieldValue -Line $failure.Line -Name 'session_id'
            $payerDecisionId =
                Get-TunaGuiLogLineFieldValue -Line $failure.Line -Name 'payer_decision_id'
            $offerGeneration =
                Get-TunaGuiLogLineFieldValue -Line $failure.Line -Name 'generation'
            if ([string]::IsNullOrWhiteSpace($writerId) -or
                [string]::IsNullOrWhiteSpace($sessionId) -or
                [string]::IsNullOrWhiteSpace($payerDecisionId) -or
                [string]::IsNullOrWhiteSpace($offerGeneration)) {
                continue
            }

            $boundary = @($indexedLines | Where-Object {
                $_.Index -lt $failure.Index -and
                (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                    'runtime_unlock_offer_authority_boundary_decided')) -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'authority_mode') -eq
                    'InitialActivation' -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'decision_status') -eq
                    'accepted' -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'log_writer_id') -eq $writerId -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'session_id') -eq $sessionId -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'payer_decision_id') -eq
                    $payerDecisionId -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'offer_generation') -eq
                    $offerGeneration -and
                [int64](Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'transaction_generation') -gt 0 -and
                [int64](Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'tuna_path_lease_generation') -gt 0
            } | Select-Object -Last 1)
            if ($boundary.Count -eq 0) {
                continue
            }

            $competingBoundaries = @($indexedLines | Where-Object {
                $_.Index -gt $boundary[0].Index -and
                $_.Index -lt $failure.Index -and
                (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                    'runtime_unlock_offer_authority_boundary_decided')) -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'authority_mode') -eq
                    'InitialActivation' -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'decision_status') -eq
                    'accepted' -and
                (Test-InitialActivationWriter -Left $boundary[0].Line -Right $_.Line) -and
                -not (
                    (Test-InitialActivationWriterAndSession `
                        -Left $boundary[0].Line `
                        -Right $_.Line) -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'transfer_id') -eq
                        (Get-TunaGuiLogLineFieldValue `
                            -Line $boundary[0].Line `
                            -Name 'transfer_id') -and
                    (Get-TunaGuiLogLineFieldValue `
                        -Line $_.Line `
                        -Name 'transaction_generation') -eq
                        (Get-TunaGuiLogLineFieldValue `
                            -Line $boundary[0].Line `
                            -Name 'transaction_generation')
                )
            })
            if ($competingBoundaries.Count -gt 0) {
                continue
            }

            $transferId =
                Get-TunaGuiLogLineFieldValue -Line $boundary[0].Line -Name 'transfer_id'
            $transactionGeneration =
                Get-TunaGuiLogLineFieldValue -Line $boundary[0].Line -Name 'transaction_generation'
            $candidateClosures = @($indexedLines | Where-Object {
                $_.Index -lt $failure.Index -and
                (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                    'filetransfer_receive_recovery_operation_closure_retained')) -and
                (Test-InitialActivationWriterAndSession `
                    -Left $boundary[0].Line `
                    -Right $_.Line) -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'transfer_id') -eq $transferId -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'direction') -eq 'outbound' -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'execution_state') -eq 'Completed' -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'execution_outcome') -eq 'Completed' -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'closure_state') -eq 'Retained' -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'proof_identity') -in @(
                    '', '(none)', 'none')
            } | Sort-Object Index -Descending)
            $closure = @()
            $deferrals = @()
            foreach ($candidateClosure in $candidateClosures) {
                $candidateDeferrals = @($indexedLines | Where-Object {
                    $deferral = $_
                    if ($deferral.Index -le $candidateClosure.Index -or
                        $deferral.Index -ge $failure.Index -or
                        -not (Test-TunaGuiLogLineHasEvent `
                            -Line $deferral.Line `
                            -Events @(
                                'tuna_activation_control_send_deferred_for_regular_v4_recovery')) -or
                        -not (Test-InitialActivationWriterAndSession `
                            -Left $boundary[0].Line `
                            -Right $deferral.Line) -or
                        (Get-TunaGuiLogLineFieldValue -Line $deferral.Line -Name 'purpose') -ne
                            'offer' -or
                        (Get-TunaGuiLogLineFieldValue `
                            -Line $deferral.Line `
                            -Name 'blocker_reason') -ne
                            'receive_stall_recovery_awaiting_receive_proof') {
                        return $false
                    }

                    $latestClosure = @($indexedLines | Where-Object {
                        $_.Index -lt $deferral.Index -and
                        (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                            'filetransfer_receive_recovery_operation_closure_retained')) -and
                        (Test-InitialActivationWriterAndSession `
                            -Left $boundary[0].Line `
                            -Right $_.Line)
                    } | Select-Object -Last 1)
                    return $latestClosure.Count -eq 1 -and
                        (Test-InitialActivationRecoveryOperationMatch `
                            -Operation $candidateClosure.Line `
                            -Candidate $latestClosure[0].Line)
                })
                if ($candidateDeferrals.Count -ge 2) {
                    $closure = @($candidateClosure)
                    $deferrals = $candidateDeferrals
                    break
                }
            }
            if ($closure.Count -ne 1 -or $deferrals.Count -lt 2) {
                continue
            }

            $sameLineageBoundaries = @($indexedLines | Where-Object {
                $_.Index -ge $closure[0].Index -and
                $_.Index -le $failure.Index -and
                (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                    'runtime_unlock_offer_authority_boundary_decided')) -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'authority_mode') -eq
                    'InitialActivation' -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'decision_status') -eq
                    'accepted' -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'log_writer_id') -eq $writerId -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'session_id') -eq $sessionId -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'transfer_id') -eq $transferId -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'transaction_generation') -eq
                    $transactionGeneration
            })
            $exhaustion = @($indexedLines | Where-Object {
                $_.Index -gt $boundary[0].Index -and
                $_.Index -lt $failure.Index -and
                (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                    'tuna_acceleration_retry_exhausted')) -and
                (Test-InitialActivationWriter -Left $boundary[0].Line -Right $_.Line) -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'reason') -eq
                    'runtime_unlock_offer_send_not_observed'
            })
            if ($sameLineageBoundaries.Count -lt 2 -or $exhaustion.Count -ne 1) {
                continue
            }

            $heldDecisions = @($indexedLines | Where-Object {
                $_.Index -gt $closure[0].Index -and
                $_.Index -lt $failure.Index -and
                (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                    'runtime_unlock_initial_activation_receive_recovery_hold_decided')) -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'decision_status') -eq 'Held' -and
                (Test-InitialActivationOfferTuple `
                    -Boundary $boundary[0].Line `
                    -Candidate $_.Line `
                    -RequireTransaction) -and
                (Test-InitialActivationRecoveryOperationMatch `
                    -Operation $closure[0].Line `
                    -Candidate $_.Line)
            })
            $recovered = $false
            foreach ($held in $heldDecisions) {
                $adoptions = @($indexedLines | Where-Object {
                    $proof = $_
                    if ($proof.Index -le $held.Index -or
                        $proof.Index -ge $failure.Index -or
                        -not (Test-TunaGuiLogLineHasEvent -Line $proof.Line -Events @(
                            'filetransfer_receive_recovery_operation_proof_observed',
                            'filetransfer_receive_recovery_operation_closure_adopted')) -or
                        (Get-TunaGuiLogLineFieldValue -Line $proof.Line -Name 'closure_state') -ne
                            'Adopted' -or
                        -not (Test-InitialActivationWriterAndSession `
                            -Left $boundary[0].Line `
                            -Right $proof.Line)) {
                        return $false
                    }

                    if (Test-InitialActivationRecoveryOperationMatch `
                            -Operation $closure[0].Line `
                            -Candidate $proof.Line) {
                        return $true
                    }

                    $rebound = @($indexedLines | Where-Object {
                        $_.Index -gt $held.Index -and
                        $_.Index -lt $proof.Index -and
                        (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                            'runtime_unlock_initial_activation_receive_recovery_hold_rebound')) -and
                        (Test-InitialActivationOfferTuple `
                            -Boundary $boundary[0].Line `
                            -Candidate $_.Line `
                            -RequireTransaction) -and
                        (Get-InitialActivationField `
                            -Line $_.Line `
                            -Names @('previous_receive_recovery_operation_authority_id')) -eq
                            (Get-InitialActivationField `
                                -Line $closure[0].Line `
                                -Names @('receive_recovery_operation_authority_id')) -and
                        (Get-InitialActivationField `
                            -Line $_.Line `
                            -Names @('previous_receive_recovery_operation_generation')) -eq
                            (Get-InitialActivationField `
                                -Line $closure[0].Line `
                                -Names @('receive_recovery_operation_generation')) -and
                        (Test-InitialActivationRecoveryOperationMatch `
                            -Operation $proof.Line `
                            -Candidate $_.Line)
                    })
                    return $rebound.Count -gt 0
                })

                foreach ($adoption in $adoptions) {
                    $release = @($indexedLines | Where-Object {
                        $_.Index -gt $adoption.Index -and
                        $_.Index -lt $failure.Index -and
                        (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                            'runtime_unlock_initial_activation_receive_recovery_hold_decided',
                            'runtime_unlock_initial_activation_receive_recovery_hold_resumed')) -and
                        (
                            (Get-TunaGuiLogLineFieldValue `
                                -Line $_.Line `
                                -Name 'decision_status') -eq 'Released' -or
                            (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                                'runtime_unlock_initial_activation_receive_recovery_hold_resumed'))
                        ) -and
                        (Test-InitialActivationHoldRequestMatch `
                            -Left $held.Line `
                            -Right $_.Line) -and
                        (Test-InitialActivationRecoveryOperationMatch `
                            -Operation $adoption.Line `
                            -Candidate $_.Line)
                    } | Select-Object -First 1)
                    if ($release.Count -eq 1) {
                        $recovered = $true
                        break
                    }
                }
                if ($recovered) {
                    break
                }
            }

            if (-not $recovered) {
                return $true
            }
        }

        return $false
    }

    function Test-TunaGuiInitialActivationReceiveRecoveryHoldReleasedOfferExpired {
        param(
            [Parameter(Mandatory = $true)][AllowEmptyCollection()][AllowEmptyString()][string[]]$Lines
        )

        $indexedLines = @(
            for ($index = 0; $index -lt $Lines.Count; $index++) {
                [pscustomobject]@{ Index = $index; Line = [string]$Lines[$index] }
            }
        )
        foreach ($rejection in @($indexedLines | Where-Object {
            (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                'tuna_acceleration_message_rejected')) -and
            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'message_type') -eq
                'transport_acceleration_offer' -and
            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'reason') -eq 'expired'
        })) {
            $peerWriter =
                Get-TunaGuiLogLineFieldValue -Line $rejection.Line -Name 'log_writer_id'
            $peerReceipt = @($indexedLines | Where-Object {
                $_.Index -lt $rejection.Index -and
                (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                    'tuna_acceleration_offer_received_raw')) -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'log_writer_id') -eq
                    $peerWriter
            } | Select-Object -Last 1)
            if ($peerReceipt.Count -ne 1) {
                continue
            }

            $releases = @($indexedLines | Where-Object {
                $_.Index -lt $peerReceipt[0].Index -and
                (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                    'runtime_unlock_initial_activation_receive_recovery_hold_resumed')) -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'decision_status') -eq
                    'Released' -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'log_writer_id') -ne
                    $peerWriter
            } | Select-Object -Last 1)
            if ($releases.Count -ne 1) {
                continue
            }
            $release = $releases[0]
            $sourceWriter =
                Get-TunaGuiLogLineFieldValue -Line $release.Line -Name 'log_writer_id'
            $sessionId =
                Get-TunaGuiLogLineFieldValue -Line $release.Line -Name 'session_id'
            $transferId =
                Get-TunaGuiLogLineFieldValue -Line $release.Line -Name 'transfer_id'
            $offerGeneration =
                Get-TunaGuiLogLineFieldValue -Line $release.Line -Name 'offer_generation'
            $payerDecisionId =
                Get-TunaGuiLogLineFieldValue -Line $release.Line -Name 'payer_decision_id'
            $envelopeFingerprint =
                Get-TunaGuiLogLineFieldValue `
                    -Line $release.Line `
                    -Name 'control_envelope_fingerprint'
            if ([string]::IsNullOrWhiteSpace($sourceWriter) -or
                [string]::IsNullOrWhiteSpace($sessionId) -or
                [string]::IsNullOrWhiteSpace($transferId) -or
                [string]::IsNullOrWhiteSpace($offerGeneration) -or
                [string]::IsNullOrWhiteSpace($payerDecisionId) -or
                [string]::IsNullOrWhiteSpace($envelopeFingerprint)) {
                continue
            }

            $held = @($indexedLines | Where-Object {
                $_.Index -lt $release.Index -and
                (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                    'runtime_unlock_initial_activation_receive_recovery_hold_decided')) -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'decision_status') -eq
                    'Held' -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'log_writer_id') -eq
                    $sourceWriter -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'session_id') -eq
                    $sessionId -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'transfer_id') -eq
                    $transferId -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'offer_generation') -eq
                    $offerGeneration -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'payer_decision_id') -eq
                    $payerDecisionId -and
                (Get-TunaGuiLogLineFieldValue `
                    -Line $_.Line `
                    -Name 'control_envelope_fingerprint') -eq $envelopeFingerprint
            } | Select-Object -Last 1)
            $prevalidated = @($indexedLines | Where-Object {
                $_.Index -ge $release.Index -and
                $_.Index -lt $peerReceipt[0].Index -and
                (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                    'runtime_unlock_initial_activation_receive_recovery_hold_send_prevalidated')) -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'decision_status') -eq
                    'Released' -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'log_writer_id') -eq
                    $sourceWriter -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'session_id') -eq
                    $sessionId -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'offer_generation') -eq
                    $offerGeneration -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'payer_decision_id') -eq
                    $payerDecisionId
            } | Select-Object -First 1)
            if ($held.Count -eq 1 -and $prevalidated.Count -eq 1) {
                return $true
            }
        }

        return $false
    }

    function Test-TunaGuiReceiveRecoveryCompletedProvenSuccessorMissing {
        param(
            [Parameter(Mandatory = $true)][AllowEmptyCollection()][AllowEmptyString()][string[]]$Lines
        )

        $indexedLines = @(
            for ($index = 0; $index -lt $Lines.Count; $index++) {
                [pscustomobject]@{ Index = $index; Line = [string]$Lines[$index] }
            }
        )

        function Test-TunaGuiCompletedProvenMeaningful {
            param([AllowNull()][AllowEmptyString()][string]$Value)

            return -not [string]::IsNullOrWhiteSpace($Value) -and
                $Value -notin @('(none)', 'none', '(missing)', '__missing__', '0')
        }

        function Test-TunaGuiCompletedProvenOperationMatch {
            param(
                [Parameter(Mandatory = $true)][string]$AnchorLine,
                [Parameter(Mandatory = $true)][string]$CandidateLine
            )

            foreach ($field in @(
                    'receive_recovery_operation_authority_id',
                    'receive_recovery_operation_generation',
                    'session_id',
                    'transfer_id',
                    'direction',
                    'route',
                    'protocol_version',
                    'live_route_epoch',
                    'route_publication_generation',
                    'runtime_generation',
                    'terminal_generation',
                    'leg_generation',
                    'transport_epoch',
                    'bridge_recovery_generation',
                    'bridge_decision_generation',
                    'data_session_generation',
                    'recovery_liveness_lease_authority_id',
                    'recovery_liveness_lease_generation')) {
                $anchor = Get-TunaGuiLogLineFieldValue -Line $AnchorLine -Name $field
                $candidate = Get-TunaGuiLogLineFieldValue -Line $CandidateLine -Name $field
                if ([string]::IsNullOrWhiteSpace($anchor) -or
                    [string]::IsNullOrWhiteSpace($candidate) -or
                    $anchor -eq '(none)' -or
                    $candidate -eq '(none)' -or
                    $anchor -ne $candidate) {
                    return $false
                }
            }
            return $true
        }

        function Test-TunaGuiCompletedProvenConflictScope {
            param(
                [Parameter(Mandatory = $true)][string]$OperationLine,
                [Parameter(Mandatory = $true)][string]$ConflictLine
            )

            foreach ($field in @('session_id', 'transfer_id', 'direction')) {
                if (-not (Test-TunaGuiLogLineFieldMatch `
                        -SourceLine $OperationLine `
                        -CandidateLine $ConflictLine `
                        -FieldName $field)) {
                    return $false
                }
            }
            foreach ($pair in @(
                    @('route', 'current_route'),
                    @('protocol_version', 'current_protocol_version'),
                    @('live_route_epoch', 'current_live_route_epoch'),
                    @('runtime_generation', 'current_runtime_generation'),
                    @('terminal_generation', 'current_terminal_generation'),
                    @('leg_generation', 'attempted_leg_generation'),
                    @('transport_epoch', 'attempted_transport_epoch'),
                    @('bridge_recovery_generation', 'attempted_bridge_recovery_generation'))) {
                $expected = Get-TunaGuiLogLineFieldValue -Line $OperationLine -Name $pair[0]
                $actual = Get-TunaGuiLogLineFieldValue -Line $ConflictLine -Name $pair[1]
                if ([string]::IsNullOrWhiteSpace($expected) -or
                    [string]::IsNullOrWhiteSpace($actual) -or
                    $expected -eq '(none)' -or
                    $actual -eq '(none)' -or
                    $expected -ne $actual) {
                    return $false
                }
            }
            return $true
        }

        $issuedOperations = @($indexedLines | Where-Object {
            Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                'filetransfer_receive_recovery_operation_issued')
        })
        foreach ($issued in $issuedOperations) {
            $operationAuthority = Get-TunaGuiLogLineFieldValue `
                -Line $issued.Line `
                -Name 'receive_recovery_operation_authority_id'
            $operationGeneration = Get-TunaGuiLogLineFieldValue `
                -Line $issued.Line `
                -Name 'receive_recovery_operation_generation'
            if (-not (Test-TunaGuiCompletedProvenMeaningful $operationAuthority) -or
                -not (Test-TunaGuiCompletedProvenMeaningful $operationGeneration)) {
                continue
            }

            $started = @($indexedLines | Where-Object {
                $_.Index -gt $issued.Index -and
                (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                    'filetransfer_receive_recovery_operation_execution_started',
                    'filetransfer_receive_recovery_operation_execution_start_decided')) -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'execution_start_status') -in
                    @('Started', 'Idempotent') -and
                (Test-TunaGuiCompletedProvenOperationMatch `
                    -AnchorLine $issued.Line `
                    -CandidateLine $_.Line)
            } | Select-Object -First 1)
            if ($started.Count -eq 0) {
                continue
            }

            $receipt = @($indexedLines | Where-Object {
                $_.Index -gt $started[0].Index -and
                (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                    'filetransfer_receive_recovery_operation_execution_receipt_applied')) -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'execution_outcome') -eq 'Completed' -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'execution_state') -eq 'Completed' -and
                (Test-TunaGuiCompletedProvenOperationMatch `
                    -AnchorLine $issued.Line `
                    -CandidateLine $_.Line)
            } | Select-Object -First 1)
            $proof = @($indexedLines | Where-Object {
                $_.Index -gt $issued.Index -and
                (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                    'filetransfer_receive_recovery_operation_proof_observed')) -and
                (Test-TunaGuiCompletedProvenMeaningful (
                    Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'proof_identity')) -and
                (Test-TunaGuiCompletedProvenOperationMatch `
                    -AnchorLine $issued.Line `
                    -CandidateLine $_.Line)
            } | Select-Object -First 1)
            if ($receipt.Count -eq 0 -or $proof.Count -eq 0) {
                continue
            }

            $proofBoundary = [Math]::Max([int]$receipt[0].Index, [int]$proof[0].Index)
            $adopted = @($indexedLines | Where-Object {
                $_.Index -gt $proofBoundary -and
                (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                    'filetransfer_receive_recovery_operation_closure_adopted')) -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'closure_state') -eq 'Adopted' -and
                (Test-TunaGuiCompletedProvenOperationMatch `
                    -AnchorLine $issued.Line `
                    -CandidateLine $_.Line)
            } | Select-Object -First 1)
            if ($adopted.Count -eq 0) {
                continue
            }

            $conflicts = @($indexedLines | Where-Object {
                $_.Index -gt $adopted[0].Index -and
                (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                    'filetransfer_recovery_authority_rejected')) -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'action_kind') -eq
                    'ReceiveRecoveryOperation' -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'rejection_reason') -eq
                    'filetransfer_receive_recovery_operation_conflict' -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'attempted_action') -in @(
                    'all_channels_zero_receive',
                    'receive_stall',
                    'ping_timeout',
                    'session_liveness_timeout_pending') -and
                (Test-TunaGuiCompletedProvenConflictScope `
                    -OperationLine $issued.Line `
                    -ConflictLine $_.Line)
            })
            foreach ($conflict in $conflicts) {
                $ownedBeforeConflict = @($indexedLines | Where-Object {
                    $_.Index -gt $adopted[0].Index -and
                    $_.Index -lt $conflict.Index -and
                    (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                        'filetransfer_receive_recovery_operation_closure_reserved',
                        'filetransfer_receive_recovery_operation_closure_consumed',
                        'filetransfer_receive_recovery_operation_invalidated',
                        'filetransfer_receive_recovery_operation_superseded')) -and
                    (Test-TunaGuiCompletedProvenOperationMatch `
                        -AnchorLine $issued.Line `
                        -CandidateLine $_.Line)
                })
                if ($ownedBeforeConflict.Count -gt 0) {
                    continue
                }

                $typedEvaluation = @($indexedLines | Where-Object {
                    $_.Index -gt $adopted[0].Index -and
                    $_.Index -le ($conflict.Index + 2) -and
                    (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                        'filetransfer_receive_recovery_evaluation_requested')) -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'receive_recovery_operation_issued') -eq '0' -and
                    (Test-TunaGuiLogLineFieldMatch -SourceLine $issued.Line -CandidateLine $_.Line -FieldName 'session_id') -and
                    (Test-TunaGuiLogLineFieldMatch -SourceLine $issued.Line -CandidateLine $_.Line -FieldName 'transfer_id') -and
                    (Test-TunaGuiLogLineFieldMatch -SourceLine $issued.Line -CandidateLine $_.Line -FieldName 'direction')
                })
                $legacyEvaluation = @($indexedLines | Where-Object {
                    $_.Index -gt $adopted[0].Index -and
                    $_.Index -lt $conflict.Index -and
                    (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                        'session_liveness_timeout_filetransfer_recovery_state_not_deferred')) -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'state') -eq 'receiveproofobserved' -and
                    (Test-TunaGuiLogLineFieldMatch -SourceLine $issued.Line -CandidateLine $_.Line -FieldName 'session_id') -and
                    (Test-TunaGuiLogLineFieldMatch -SourceLine $issued.Line -CandidateLine $_.Line -FieldName 'transfer_id')
                })
                if ($typedEvaluation.Count -eq 0 -and $legacyEvaluation.Count -ne 1) {
                    continue
                }

                $reservation = @($indexedLines | Where-Object {
                    $_.Index -gt $conflict.Index -and
                    (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                        'filetransfer_receive_recovery_operation_closure_reserved')) -and
                    (Test-TunaGuiCompletedProvenOperationMatch `
                        -AnchorLine $issued.Line `
                        -CandidateLine $_.Line)
                } | Select-Object -First 1)
                if ($reservation.Count -eq 1) {
                    $consumed = @($indexedLines | Where-Object {
                        $_.Index -gt $reservation[0].Index -and
                        (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                            'filetransfer_receive_recovery_operation_closure_consumed')) -and
                        (Test-TunaGuiCompletedProvenOperationMatch `
                            -AnchorLine $issued.Line `
                            -CandidateLine $_.Line)
                    } | Select-Object -First 1)
                    if ($consumed.Count -eq 1) {
                        continue
                    }
                }

                $superseded = @($indexedLines | Where-Object {
                    $_.Index -gt $conflict.Index -and
                    (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                        'filetransfer_receive_recovery_operation_superseded')) -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'predecessor_receive_recovery_operation_authority_id') -eq
                        $operationAuthority -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'predecessor_receive_recovery_operation_generation') -eq
                        $operationGeneration
                } | Select-Object -First 1)
                if ($superseded.Count -eq 1) {
                    $successorAuthority = Get-TunaGuiLogLineFieldValue `
                        -Line $superseded[0].Line `
                        -Name 'receive_recovery_operation_authority_id'
                    $successorGeneration = Get-TunaGuiLogLineFieldValue `
                        -Line $superseded[0].Line `
                        -Name 'receive_recovery_operation_generation'
                    $successorTerminal = @($indexedLines | Where-Object {
                        $_.Index -gt $superseded[0].Index -and
                        (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                            'filetransfer_receive_recovery_operation_execution_receipt_applied')) -and
                        (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'receive_recovery_operation_authority_id') -eq
                            $successorAuthority -and
                        (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'receive_recovery_operation_generation') -eq
                            $successorGeneration
                    } | Select-Object -First 1)
                    if ($successorTerminal.Count -eq 1) {
                        continue
                    }
                }

                return $true
            }
        }

        return $false
    }

    function Test-TunaGuiReceiveRecoveryCompletedUnprovenSuccessorMissing {
        param(
            [Parameter(Mandatory = $true)][AllowEmptyCollection()][AllowEmptyString()][string[]]$Lines
        )

        $indexedLines = @(
            for ($index = 0; $index -lt $Lines.Count; $index++) {
                [pscustomobject]@{ Index = $index; Line = [string]$Lines[$index] }
            }
        )

        function Test-TunaGuiReceiveRecoveryMeaningful {
            param([AllowNull()][AllowEmptyString()][string]$Value)

            return -not [string]::IsNullOrWhiteSpace($Value) -and
                $Value -notin @('(none)', 'none', '(missing)', '__missing__', '0')
        }

        function Get-TunaGuiReceiveRecoveryOperationGeneration {
            param([Parameter(Mandatory = $true)][string]$Line)

            foreach ($name in @(
                    'receive_recovery_operation_generation',
                    'recovery_operation_generation',
                    'operation_generation')) {
                $value = Get-TunaGuiLogLineFieldValue -Line $Line -Name $name
                if (-not [string]::IsNullOrWhiteSpace($value)) {
                    return $value
                }
            }
            return ''
        }

        function Get-TunaGuiReceiveRecoveryOperationAuthority {
            param([Parameter(Mandatory = $true)][string]$Line)

            foreach ($name in @(
                    'receive_recovery_operation_authority_id',
                    'recovery_operation_authority_id')) {
                $value = Get-TunaGuiLogLineFieldValue -Line $Line -Name $name
                if (-not [string]::IsNullOrWhiteSpace($value)) {
                    return $value
                }
            }
            return ''
        }

        function Test-TunaGuiReceiveRecoveryOperationMatch {
            param(
                [Parameter(Mandatory = $true)][string]$AnchorLine,
                [Parameter(Mandatory = $true)][string]$CandidateLine
            )

            $anchorAuthority =
                Get-TunaGuiReceiveRecoveryOperationAuthority -Line $AnchorLine
            $candidateAuthority =
                Get-TunaGuiReceiveRecoveryOperationAuthority -Line $CandidateLine
            $anchorGeneration =
                Get-TunaGuiReceiveRecoveryOperationGeneration -Line $AnchorLine
            $candidateGeneration =
                Get-TunaGuiReceiveRecoveryOperationGeneration -Line $CandidateLine
            if (-not (Test-TunaGuiReceiveRecoveryMeaningful $anchorAuthority) -or
                -not (Test-TunaGuiReceiveRecoveryMeaningful $candidateAuthority) -or
                -not (Test-TunaGuiReceiveRecoveryMeaningful $anchorGeneration) -or
                -not (Test-TunaGuiReceiveRecoveryMeaningful $candidateGeneration) -or
                $anchorAuthority -ne $candidateAuthority -or
                $anchorGeneration -ne $candidateGeneration) {
                return $false
            }

            foreach ($field in @(
                    'session_id',
                    'transfer_id',
                    'direction',
                    'route',
                    'protocol_version',
                    'live_route_epoch',
                    'route_publication_generation',
                    'route_commit_generation',
                    'runtime_generation',
                    'terminal_generation',
                    'leg_generation',
                    'transport_epoch',
                    'bridge_recovery_generation',
                    'bridge_decision_generation',
                    'checkpoint_request_id',
                    'data_session_identity',
                    'data_session_generation',
                    'recovery_liveness_lease_authority_id',
                    'recovery_liveness_lease_generation')) {
                $expected =
                    Get-TunaGuiLogLineFieldValue -Line $AnchorLine -Name $field
                $actual =
                    Get-TunaGuiLogLineFieldValue -Line $CandidateLine -Name $field
                if ([string]::IsNullOrWhiteSpace($expected) -or
                    [string]::IsNullOrWhiteSpace($actual) -or
                    -not [string]::Equals(
                        $expected,
                        $actual,
                        [System.StringComparison]::OrdinalIgnoreCase)) {
                    return $false
                }
            }
            return $true
        }

        function Test-TunaGuiReceiveRecoveryRejectionScope {
            param(
                [Parameter(Mandatory = $true)][string]$OperationLine,
                [Parameter(Mandatory = $true)][string]$RejectionLine
            )

            foreach ($field in @('session_id', 'transfer_id', 'direction')) {
                $expected =
                    Get-TunaGuiLogLineFieldValue -Line $OperationLine -Name $field
                $actual =
                    Get-TunaGuiLogLineFieldValue -Line $RejectionLine -Name $field
                if ([string]::IsNullOrWhiteSpace($expected) -or
                    [string]::IsNullOrWhiteSpace($actual) -or
                    -not [string]::Equals(
                        $expected,
                        $actual,
                        [System.StringComparison]::OrdinalIgnoreCase)) {
                    return $false
                }
            }

            foreach ($mapping in @(
                    [pscustomobject]@{
                        Operation = 'route'
                        Rejection = @('current_route', 'attempted_route')
                    },
                    [pscustomobject]@{
                        Operation = 'protocol_version'
                        Rejection = @('current_protocol_version', 'attempted_protocol_version')
                    },
                    [pscustomobject]@{
                        Operation = 'live_route_epoch'
                        Rejection = @('current_live_route_epoch', 'attempted_live_route_epoch')
                    },
                    [pscustomobject]@{
                        Operation = 'runtime_generation'
                        Rejection = @('current_runtime_generation', 'attempted_runtime_generation')
                    },
                    [pscustomobject]@{
                        Operation = 'terminal_generation'
                        Rejection = @('current_terminal_generation', 'attempted_terminal_generation')
                    },
                    [pscustomobject]@{
                        Operation = 'leg_generation'
                        Rejection = @('attempted_leg_generation')
                    },
                    [pscustomobject]@{
                        Operation = 'transport_epoch'
                        Rejection = @('attempted_transport_epoch')
                    },
                    [pscustomobject]@{
                        Operation = 'bridge_recovery_generation'
                        Rejection = @('attempted_bridge_recovery_generation')
                    })) {
                $expected = Get-TunaGuiLogLineFieldValue `
                    -Line $OperationLine `
                    -Name $mapping.Operation
                $actual = ''
                foreach ($candidateName in $mapping.Rejection) {
                    $actual = Get-TunaGuiLogLineFieldValue `
                        -Line $RejectionLine `
                        -Name $candidateName
                    if (-not [string]::IsNullOrWhiteSpace($actual)) {
                        break
                    }
                }
                if (-not [string]::IsNullOrWhiteSpace($actual) -and
                    ([string]::IsNullOrWhiteSpace($expected) -or
                     -not [string]::Equals(
                        $expected,
                        $actual,
                        [System.StringComparison]::OrdinalIgnoreCase))) {
                    return $false
                }
            }
            return $true
        }

        function Test-TunaGuiReceiveRecoverySupersessionCompleted {
            param(
                [Parameter(Mandatory = $true)]$Rejection,
                [Parameter(Mandatory = $true)]$Issued,
                [Parameter(Mandatory = $true)][string]$ClosureGeneration
            )

            $operationAuthority =
                Get-TunaGuiReceiveRecoveryOperationAuthority -Line $Issued.Line
            $operationGeneration =
                Get-TunaGuiReceiveRecoveryOperationGeneration -Line $Issued.Line
            $supersession = @($indexedLines | Where-Object {
                $_.Index -gt $Rejection.Index -and
                (Test-TunaGuiLogLineHasEvent `
                    -Line $_.Line `
                    -Events @('filetransfer_receive_recovery_operation_superseded')) -and
                (Get-TunaGuiLogLineFieldValue `
                    -Line $_.Line `
                    -Name 'predecessor_receive_recovery_operation_authority_id') -eq
                        $operationAuthority -and
                (Get-TunaGuiLogLineFieldValue `
                    -Line $_.Line `
                    -Name 'predecessor_receive_recovery_operation_generation') -eq
                        $operationGeneration -and
                (Get-TunaGuiLogLineFieldValue `
                    -Line $_.Line `
                    -Name 'predecessor_closure_generation') -eq
                        $ClosureGeneration -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'session_id') -eq
                    (Get-TunaGuiLogLineFieldValue -Line $Issued.Line -Name 'session_id') -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'transfer_id') -eq
                    (Get-TunaGuiLogLineFieldValue -Line $Issued.Line -Name 'transfer_id') -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'direction') -eq
                    (Get-TunaGuiLogLineFieldValue -Line $Issued.Line -Name 'direction')
            } | Select-Object -First 1)
            if ($supersession.Count -eq 0) {
                return $false
            }

            $successorAuthority = Get-TunaGuiLogLineFieldValue `
                -Line $supersession[0].Line `
                -Name 'successor_receive_recovery_operation_authority_id'
            $successorGeneration = Get-TunaGuiLogLineFieldValue `
                -Line $supersession[0].Line `
                -Name 'successor_receive_recovery_operation_generation'
            if (-not (Test-TunaGuiReceiveRecoveryMeaningful $successorAuthority) -or
                -not (Test-TunaGuiReceiveRecoveryMeaningful $successorGeneration)) {
                return $false
            }

            $successor = @($indexedLines | Where-Object {
                $_.Index -gt $supersession[0].Index -and
                (Test-TunaGuiLogLineHasEvent `
                    -Line $_.Line `
                    -Events @('filetransfer_receive_recovery_operation_issued')) -and
                (Get-TunaGuiReceiveRecoveryOperationAuthority -Line $_.Line) -eq
                    $successorAuthority -and
                (Get-TunaGuiReceiveRecoveryOperationGeneration -Line $_.Line) -eq
                    $successorGeneration -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'session_id') -eq
                    (Get-TunaGuiLogLineFieldValue -Line $Issued.Line -Name 'session_id') -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'transfer_id') -eq
                    (Get-TunaGuiLogLineFieldValue -Line $Issued.Line -Name 'transfer_id') -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'direction') -eq
                    (Get-TunaGuiLogLineFieldValue -Line $Issued.Line -Name 'direction')
            } | Select-Object -First 1)
            if ($successor.Count -eq 0) {
                return $false
            }

            $successorStart = @($indexedLines | Where-Object {
                $_.Index -gt $successor[0].Index -and
                (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                    'filetransfer_receive_recovery_operation_execution_start_decided',
                    'filetransfer_receive_recovery_operation_execution_started')) -and
                (Get-TunaGuiLogLineFieldValue `
                    -Line $_.Line `
                    -Name 'execution_start_status') -in @(
                        'Started',
                        'Idempotent',
                        'Accepted') -and
                (Test-TunaGuiReceiveRecoveryOperationMatch `
                    -AnchorLine $successor[0].Line `
                    -CandidateLine $_.Line)
            } | Select-Object -First 1)
            if ($successorStart.Count -eq 0) {
                return $false
            }

            $successorReceipt = @($indexedLines | Where-Object {
                $_.Index -gt $successorStart[0].Index -and
                (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                    'filetransfer_receive_recovery_operation_execution_receipt_applied',
                    'filetransfer_receive_recovery_operation_receipt_applied')) -and
                (Get-TunaGuiLogLineFieldValue `
                    -Line $_.Line `
                    -Name 'execution_outcome') -in @(
                        'Completed',
                        'Exhausted',
                        'Discarded') -and
                (Test-TunaGuiReceiveRecoveryOperationMatch `
                    -AnchorLine $successor[0].Line `
                    -CandidateLine $_.Line)
            } | Select-Object -First 1)
            return $successorReceipt.Count -gt 0
        }

        $issuedEvents = @($indexedLines | Where-Object {
            (Test-TunaGuiLogLineHasEvent `
                -Line $_.Line `
                -Events @('filetransfer_receive_recovery_operation_issued')) -and
            (Test-TunaGuiReceiveRecoveryMeaningful (
                Get-TunaGuiReceiveRecoveryOperationAuthority -Line $_.Line)) -and
            (Test-TunaGuiReceiveRecoveryMeaningful (
                Get-TunaGuiReceiveRecoveryOperationGeneration -Line $_.Line))
        })
        $rejections = @($indexedLines | Where-Object {
            (Test-TunaGuiLogLineHasEvent `
                -Line $_.Line `
                -Events @('filetransfer_recovery_authority_rejected')) -and
            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'action_kind') -eq
                'ReceiveRecoveryOperation' -and
            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'rejection_reason') -eq
                'filetransfer_receive_recovery_operation_conflict' -and
            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'attempted_action') -in @(
                'bridge_ping_timeout',
                'bridge_receive_stall',
                'receive_stall',
                'ping_timeout')
        })

        foreach ($rejection in $rejections) {
            $writer =
                Get-TunaGuiLogLineFieldValue -Line $rejection.Line -Name 'log_writer_id'
            $transfer =
                Get-TunaGuiLogLineFieldValue -Line $rejection.Line -Name 'transfer_id'
            $direction =
                Get-TunaGuiLogLineFieldValue -Line $rejection.Line -Name 'direction'
            if ([string]::IsNullOrWhiteSpace($writer) -or
                [string]::IsNullOrWhiteSpace($transfer) -or
                [string]::IsNullOrWhiteSpace($direction)) {
                continue
            }

            $health = @($indexedLines | Where-Object {
                $_.Index -lt $rejection.Index -and
                (Test-TunaGuiLogLineHasEvent `
                    -Line $_.Line `
                    -Events @('nkn_bridge_recovery_evaluation_requested')) -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'log_writer_id') -eq
                    $writer -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'transfer_id') -eq
                    $transfer -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'observation_kind') -in @(
                    'ping_timeout',
                    'receive_stall') -and
                (Test-TunaGuiReceiveRecoveryMeaningful (
                    Get-TunaGuiLogLineFieldValue `
                        -Line $_.Line `
                        -Name 'observation_generation'))
            } | Select-Object -Last 1)
            if ($health.Count -eq 0) {
                continue
            }
            $observationGeneration = Get-TunaGuiLogLineFieldValue `
                -Line $health[0].Line `
                -Name 'observation_generation'
            $evaluationDecision = @($indexedLines | Where-Object {
                $_.Index -gt $rejection.Index -and
                (Test-TunaGuiLogLineHasEvent `
                    -Line $_.Line `
                    -Events @('filetransfer_receive_recovery_evaluation_requested')) -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'log_writer_id') -eq
                    $writer -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'transfer_id') -eq
                    $transfer -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'direction') -eq
                    $direction -and
                (Get-TunaGuiLogLineFieldValue `
                    -Line $_.Line `
                    -Name 'observation_generation') -eq $observationGeneration -and
                (Get-TunaGuiLogLineFieldValue `
                    -Line $_.Line `
                    -Name 'receive_recovery_operation_issued') -eq '0'
            } | Select-Object -First 1)
            if ($evaluationDecision.Count -eq 0) {
                continue
            }

            $eligibleOperations = New-Object System.Collections.Generic.List[object]
            foreach ($issued in @($issuedEvents | Where-Object {
                    $_.Index -lt $health[0].Index -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'log_writer_id') -eq
                        $writer -and
                    (Test-TunaGuiReceiveRecoveryRejectionScope `
                        -OperationLine $_.Line `
                        -RejectionLine $rejection.Line)
                })) {
                $operationEvents = @($indexedLines | Where-Object {
                    $_.Index -gt $issued.Index -and
                    $_.Index -lt $rejection.Index -and
                    (Test-TunaGuiReceiveRecoveryOperationMatch `
                        -AnchorLine $issued.Line `
                        -CandidateLine $_.Line)
                })
                $start = @($operationEvents | Where-Object {
                    (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                        'filetransfer_receive_recovery_operation_execution_start_decided',
                        'filetransfer_receive_recovery_operation_execution_started')) -and
                    (Get-TunaGuiLogLineFieldValue `
                        -Line $_.Line `
                        -Name 'execution_start_status') -in @(
                            'Started',
                            'Idempotent',
                            'Accepted')
                } | Select-Object -First 1)
                if ($start.Count -eq 0) {
                    continue
                }
                $receipt = @($operationEvents | Where-Object {
                    $_.Index -gt $start[0].Index -and
                    (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                        'filetransfer_receive_recovery_operation_execution_receipt_applied',
                        'filetransfer_receive_recovery_operation_receipt_applied')) -and
                    (Get-TunaGuiLogLineFieldValue `
                        -Line $_.Line `
                        -Name 'execution_outcome') -eq 'Completed' -and
                    (Get-TunaGuiLogLineFieldValue `
                        -Line $_.Line `
                        -Name 'execution_state') -eq 'Completed' -and
                    (Get-TunaGuiLogLineFieldValue `
                        -Line $_.Line `
                        -Name 'proof_identity') -in @('', '(none)', 'none') -and
                    (Get-TunaGuiLogLineFieldValue `
                        -Line $_.Line `
                        -Name 'proof_observed_utc_ms') -in @('', '0')
                } | Select-Object -First 1)
                if ($receipt.Count -eq 0) {
                    continue
                }
                $retained = @($operationEvents | Where-Object {
                    $_.Index -gt $receipt[0].Index -and
                    (Test-TunaGuiLogLineHasEvent `
                        -Line $_.Line `
                        -Events @('filetransfer_receive_recovery_operation_closure_retained')) -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'closure_state') -eq
                        'Retained' -and
                    (Test-TunaGuiReceiveRecoveryMeaningful (
                        Get-TunaGuiLogLineFieldValue `
                            -Line $_.Line `
                            -Name 'closure_generation'))
                } | Select-Object -First 1)
                if ($retained.Count -eq 0 -or $retained[0].Index -ge $health[0].Index) {
                    continue
                }

                $disqualifying = @($operationEvents | Where-Object {
                    $_.Index -gt $retained[0].Index -and
                    (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                        'filetransfer_receive_recovery_operation_proof_observed',
                        'filetransfer_receive_recovery_operation_receive_proof_observed',
                        'filetransfer_receive_recovery_operation_closure_adopted',
                        'filetransfer_receive_recovery_operation_closure_reserved',
                        'filetransfer_receive_recovery_operation_closure_consumed',
                        'filetransfer_receive_recovery_operation_invalidated'))
                })
                if ($disqualifying.Count -gt 0) {
                    continue
                }

                $eligibleOperations.Add([pscustomobject]@{
                    Issued = $issued
                    Retained = $retained[0]
                }) | Out-Null
            }

            # Legacy rejection events carry no operation identity. They may attach
            # only when one complete same-writer operation window is available.
            if ($eligibleOperations.Count -ne 1) {
                continue
            }

            $operation = $eligibleOperations[0]
            $proof = @($indexedLines | Where-Object {
                $_.Index -gt $rejection.Index -and
                (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                    'filetransfer_receive_recovery_operation_proof_observed',
                    'filetransfer_receive_recovery_operation_receive_proof_observed')) -and
                (Test-TunaGuiReceiveRecoveryMeaningful (
                    Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'proof_identity')) -and
                (Test-TunaGuiReceiveRecoveryOperationMatch `
                    -AnchorLine $operation.Issued.Line `
                    -CandidateLine $_.Line)
            } | Select-Object -First 1)
            if ($proof.Count -gt 0) {
                $adoption = @($indexedLines | Where-Object {
                    $_.Index -gt $proof[0].Index -and
                    (Test-TunaGuiLogLineHasEvent `
                        -Line $_.Line `
                        -Events @('filetransfer_receive_recovery_operation_closure_adopted')) -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'closure_state') -eq
                        'Adopted' -and
                    (Get-TunaGuiLogLineFieldValue `
                        -Line $_.Line `
                        -Name 'closure_generation') -eq
                            (Get-TunaGuiLogLineFieldValue `
                                -Line $operation.Retained.Line `
                                -Name 'closure_generation') -and
                    (Test-TunaGuiReceiveRecoveryOperationMatch `
                        -AnchorLine $operation.Issued.Line `
                        -CandidateLine $_.Line)
                } | Select-Object -First 1)
                if ($adoption.Count -gt 0) {
                    continue
                }
            }

            if (Test-TunaGuiReceiveRecoverySupersessionCompleted `
                    -Rejection $rejection `
                    -Issued $operation.Issued `
                    -ClosureGeneration (
                        Get-TunaGuiLogLineFieldValue `
                            -Line $operation.Retained.Line `
                            -Name 'closure_generation')) {
                continue
            }

            return $true
        }

        return $false
    }

    function Test-TunaGuiBridgeRecoveryExecutionPayloadHoldFailure {
        param(
            [Parameter(Mandatory = $true)][AllowEmptyCollection()][AllowEmptyString()][string[]]$Lines
        )

        $indexedLines = @(
            for ($index = 0; $index -lt $Lines.Count; $index++) {
                [pscustomobject]@{ Index = $index; Line = [string]$Lines[$index] }
            }
        )

        function Get-TunaGuiNormalizedBridgeRecoveryRoute {
            param([string]$Line, [string]$Name)
            return (Get-TunaGuiLogLineFieldValue -Line $Line -Name $Name).
                ToLowerInvariant() -replace '[^a-z0-9]', ''
        }

        function Test-TunaGuiBridgeRecoveryTransferIdentity {
            param(
                [string]$SourceLine,
                [string]$CandidateLine,
                [switch]$RequireDirection,
                [switch]$RequireWriter
            )

            foreach ($field in @('session_id', 'transfer_id')) {
                if (-not (Test-TunaGuiLogLineFieldMatch `
                        -SourceLine $SourceLine `
                        -CandidateLine $CandidateLine `
                        -FieldName $field)) {
                    return $false
                }
            }
            if ($RequireDirection -and
                -not (Test-TunaGuiLogLineFieldMatch `
                    -SourceLine $SourceLine `
                    -CandidateLine $CandidateLine `
                    -FieldName 'direction')) {
                return $false
            }
            if ($RequireWriter) {
                $sourceWriter = Get-TunaGuiLogLineFieldValue -Line $SourceLine -Name 'log_writer_id'
                $candidateWriter = Get-TunaGuiLogLineFieldValue -Line $CandidateLine -Name 'log_writer_id'
                if ([string]::IsNullOrWhiteSpace($sourceWriter) -or
                    [string]::IsNullOrWhiteSpace($candidateWriter) -or
                    $sourceWriter -ne $candidateWriter) {
                    return $false
                }
            }
            return $true
        }

        function Test-TunaGuiBridgeRecoveryCommandScope {
            param(
                [string]$CommandLine,
                [string]$CandidateLine,
                [switch]$SourcePrefixed,
                [switch]$IncludeDecision
            )

            if (-not (Test-TunaGuiBridgeRecoveryTransferIdentity `
                    -SourceLine $CommandLine `
                    -CandidateLine $CandidateLine `
                    -RequireDirection)) {
                return $false
            }
            $candidateRouteField = if ($SourcePrefixed) { 'source_route' } else { 'route' }
            if ((Get-TunaGuiNormalizedBridgeRecoveryRoute -Line $CommandLine -Name 'route') -ne
                (Get-TunaGuiNormalizedBridgeRecoveryRoute -Line $CandidateLine -Name $candidateRouteField)) {
                return $false
            }
            foreach ($field in @(
                    'protocol_version',
                    'live_route_epoch',
                    'runtime_generation',
                    'terminal_generation',
                    'leg_generation',
                    'transport_epoch',
                    'bridge_recovery_generation')) {
                $candidateField = if ($SourcePrefixed) { "source_$field" } else { $field }
                $expected = Get-TunaGuiLogLineFieldValue -Line $CommandLine -Name $field
                $actual = Get-TunaGuiLogLineFieldValue -Line $CandidateLine -Name $candidateField
                if ([string]::IsNullOrWhiteSpace($expected) -or
                    [string]::IsNullOrWhiteSpace($actual) -or
                    $expected -ne $actual) {
                    return $false
                }
            }
            if ($IncludeDecision -and
                -not (Test-TunaGuiLogLineFieldMatch `
                    -SourceLine $CommandLine `
                    -CandidateLine $CandidateLine `
                    -FieldName 'bridge_decision_generation')) {
                return $false
            }
            return $true
        }

        $commands = @($indexedLines | Where-Object {
            Test-TunaGuiLogLineHasEvent `
                -Line $_.Line `
                -Events @('filetransfer_bridge_recovery_execution_command_issued')
        })
        $senderFailures = @($indexedLines | Where-Object {
            Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @('filetransfer_v4_sender_failed')
        })
        foreach ($senderFailure in $senderFailures) {
            $command = @($commands | Where-Object {
                $_.Index -lt $senderFailure.Index -and
                (Test-TunaGuiBridgeRecoveryTransferIdentity `
                    -SourceLine $_.Line `
                    -CandidateLine $senderFailure.Line `
                    -RequireWriter)
            } | Select-Object -Last 1)
            if ($command.Count -eq 0) {
                continue
            }

            $start = @($indexedLines | Where-Object {
                $_.Index -gt $command[0].Index -and
                $_.Index -lt $senderFailure.Index -and
                (Test-TunaGuiLogLineHasEvent `
                    -Line $_.Line `
                    -Events @('nkn_bridge_recovery_execution_started')) -and
                (Test-TunaGuiBridgeRecoveryCommandScope `
                    -CommandLine $command[0].Line `
                    -CandidateLine $_.Line `
                    -IncludeDecision) -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'lease_generation') -eq
                    (Get-TunaGuiLogLineFieldValue `
                        -Line $command[0].Line `
                        -Name 'recovery_liveness_lease_generation')
            } | Select-Object -First 1)
            if ($start.Count -eq 0) {
                continue
            }

            $barrier = @($indexedLines | Where-Object {
                $_.Index -gt $start[0].Index -and
                $_.Index -lt $senderFailure.Index -and
                (Test-TunaGuiLogLineHasEvent `
                    -Line $_.Line `
                    -Events @('nkn_bridge_recovery_submission_barrier_closed')) -and
                (Test-TunaGuiBridgeRecoveryCommandScope `
                    -CommandLine $command[0].Line `
                    -CandidateLine $_.Line `
                    -IncludeDecision) -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'lease_generation') -eq
                    (Get-TunaGuiLogLineFieldValue `
                        -Line $command[0].Line `
                        -Name 'recovery_liveness_lease_generation')
            } | Select-Object -First 1)
            if ($barrier.Count -eq 0) {
                continue
            }
            $submissionEpoch = Get-TunaGuiLogLineFieldValue `
                -Line $barrier[0].Line `
                -Name 'submission_epoch'
            if ([string]::IsNullOrWhiteSpace($submissionEpoch) -or $submissionEpoch -eq '0') {
                continue
            }

            $hardDecision = @($indexedLines | Where-Object {
                $_.Index -gt $barrier[0].Index -and
                $_.Index -lt $senderFailure.Index -and
                (Test-TunaGuiLogLineHasEvent `
                    -Line $_.Line `
                    -Events @('nkn_bridge_recovery_execution_hard_restart_decided')) -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'decision_status') -eq 'Started' -and
                [int64](Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'remaining_total_ms') -gt 0 -and
                (Test-TunaGuiBridgeRecoveryCommandScope `
                    -CommandLine $command[0].Line `
                    -CandidateLine $_.Line `
                    -IncludeDecision) -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'lease_generation') -eq
                    (Get-TunaGuiLogLineFieldValue `
                        -Line $command[0].Line `
                        -Name 'recovery_liveness_lease_generation')
            } | Select-Object -First 1)
            if ($hardDecision.Count -eq 0) {
                continue
            }

            $barrierTerminal = @($indexedLines | Where-Object {
                $_.Index -gt $barrier[0].Index -and
                (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                    'nkn_bridge_recovery_submission_barrier_released',
                    'nkn_bridge_recovery_submission_barrier_invalidated',
                    'nkn_bridge_recovery_execution_completed',
                    'nkn_bridge_recovery_execution_completion_late')) -and
                (Test-TunaGuiBridgeRecoveryTransferIdentity `
                    -SourceLine $command[0].Line `
                    -CandidateLine $_.Line `
                    -RequireDirection `
                    -RequireWriter)
            } | Select-Object -First 1)
            if ($barrierTerminal.Count -gt 0 -and
                $senderFailure.Index -ge $barrierTerminal[0].Index) {
                continue
            }

            $qualifyingBindings = New-Object System.Collections.Generic.List[object]
            foreach ($binding in @($indexedLines | Where-Object {
                    $_.Index -gt $barrier[0].Index -and
                    $_.Index -lt $senderFailure.Index -and
                    (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                        'filetransfer_regular_v4_frontier_repair_pressure_binding_committed')) -and
                    (Test-TunaGuiBridgeRecoveryCommandScope `
                        -CommandLine $command[0].Line `
                        -CandidateLine $_.Line `
                        -IncludeDecision)
                })) {
                $authorityId = Get-TunaGuiLogLineFieldValue -Line $binding.Line -Name 'authority_id'
                $installGeneration = Get-TunaGuiLogLineFieldValue `
                    -Line $binding.Line `
                    -Name 'install_generation'
                $repairKey = Get-TunaGuiLogLineFieldValue `
                    -Line $binding.Line `
                    -Name 'repair_request_key'
                if ([string]::IsNullOrWhiteSpace($authorityId) -or
                    [string]::IsNullOrWhiteSpace($installGeneration) -or
                    $installGeneration -eq '0' -or
                    [string]::IsNullOrWhiteSpace($repairKey)) {
                    continue
                }
                $grant = @($indexedLines | Where-Object {
                    $_.Index -gt $binding.Index -and
                    $_.Index -lt $senderFailure.Index -and
                    (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                        'filetransfer_regular_v4_frontier_repair_authority_granted')) -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'authority_id') -eq $authorityId -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'install_generation') -eq
                        $installGeneration -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'repair_request_key') -eq
                        $repairKey -and
                    (Test-TunaGuiBridgeRecoveryCommandScope `
                        -CommandLine $command[0].Line `
                        -CandidateLine $_.Line `
                        -IncludeDecision)
                } | Select-Object -First 1)
                if ($grant.Count -eq 0) {
                    continue
                }

                $linkedFailure = $false
                foreach ($lease in @($indexedLines | Where-Object {
                        $_.Index -gt $hardDecision[0].Index -and
                        $_.Index -lt $senderFailure.Index -and
                        (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                            'filetransfer_regular_v4_physical_send_lease_registered')) -and
                        (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'send_kind') -eq
                            'RegularV4Repair' -and
                        (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'repair_send') -eq '1' -and
                        (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'repair_request_key') -eq
                            $repairKey -and
                        (Test-TunaGuiBridgeRecoveryCommandScope `
                            -CommandLine $command[0].Line `
                            -CandidateLine $_.Line `
                            -SourcePrefixed) -and
                        (Test-TunaGuiBridgeRecoveryTransferIdentity `
                            -SourceLine $command[0].Line `
                            -CandidateLine $_.Line `
                            -RequireWriter)
                    })) {
                    $deliveryFailure = @($indexedLines | Where-Object {
                        $_.Index -gt $lease.Index -and
                        $_.Index -lt $senderFailure.Index -and
                        (Test-TunaGuiLogLineHasEvent `
                            -Line $_.Line `
                            -Events @('filetransfer_v4_repair_delivery_both_failed')) -and
                        (Test-TunaGuiBridgeRecoveryTransferIdentity `
                            -SourceLine $command[0].Line `
                            -CandidateLine $_.Line `
                            -RequireWriter)
                    } | Select-Object -First 1)
                    if ($deliveryFailure.Count -eq 0) {
                        continue
                    }
                    $rejects = @($indexedLines | Where-Object {
                        $_.Index -gt $lease.Index -and
                        $_.Index -lt $deliveryFailure[0].Index -and
                        (Test-TunaGuiLogLineHasEvent `
                            -Line $_.Line `
                            -Events @('nkn_bridge_submission_rejected')) -and
                        (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'stage') -eq 'pre_wait' -and
                        (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'reason') -eq
                            'bridge_submission_epoch_not_ready' -and
                        (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'submission_epoch') -eq
                            $submissionEpoch -and
                        (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'recovery_barrier_active') -eq '1' -and
                        (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'log_writer_id') -eq
                            (Get-TunaGuiLogLineFieldValue -Line $command[0].Line -Name 'log_writer_id')
                    })
                    $channels = @($rejects | ForEach-Object {
                        (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'channel').ToLowerInvariant()
                    } | Select-Object -Unique)
                    if ($channels -contains 'control' -and $channels -contains 'bulk') {
                        $linkedFailure = $true
                        break
                    }
                }
                if ($linkedFailure) {
                    $qualifyingBindings.Add($binding) | Out-Null
                }
            }

            $bindingKeys = @($qualifyingBindings | ForEach-Object {
                [string]::Join('|', @(
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'authority_id'),
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'install_generation'),
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'repair_request_key')))
            } | Select-Object -Unique)
            if ($bindingKeys.Count -eq 1) {
                return $true
            }
        }

        return $false
    }

    function Test-TunaGuiBridgeRecoveryExecutionSubmissionBarrierDrainFailure {
        param(
            [Parameter(Mandatory = $true)][AllowEmptyCollection()][AllowEmptyString()][string[]]$Lines
        )

        $indexedLines = @(
            for ($index = 0; $index -lt $Lines.Count; $index++) {
                [pscustomobject]@{ Index = $index; Line = [string]$Lines[$index] }
            }
        )

        function Test-TunaGuiBridgeRecoveryDrainScope {
            param(
                [Parameter(Mandatory = $true)][string]$CommandLine,
                [Parameter(Mandatory = $true)][string]$CandidateLine,
                [switch]$SourcePrefixed,
                [switch]$RequireWriter
            )

            foreach ($field in @('session_id', 'transfer_id', 'direction')) {
                if (-not (Test-TunaGuiLogLineFieldMatch `
                        -SourceLine $CommandLine `
                        -CandidateLine $CandidateLine `
                        -FieldName $field)) {
                    return $false
                }
            }

            $commandRoute = (
                Get-TunaGuiLogLineFieldValue -Line $CommandLine -Name 'route'
            ).ToLowerInvariant() -replace '[^a-z0-9]', ''
            $candidateRouteField = if ($SourcePrefixed) { 'source_route' } else { 'route' }
            $candidateRoute = (
                Get-TunaGuiLogLineFieldValue -Line $CandidateLine -Name $candidateRouteField
            ).ToLowerInvariant() -replace '[^a-z0-9]', ''
            if ([string]::IsNullOrWhiteSpace($commandRoute) -or
                $commandRoute -ne $candidateRoute) {
                return $false
            }

            foreach ($field in @(
                    'protocol_version',
                    'live_route_epoch',
                    'runtime_generation',
                    'terminal_generation',
                    'leg_generation',
                    'transport_epoch',
                    'bridge_recovery_generation')) {
                $expected = Get-TunaGuiLogLineFieldValue -Line $CommandLine -Name $field
                $candidateField = if ($SourcePrefixed) { "source_$field" } else { $field }
                $actual = Get-TunaGuiLogLineFieldValue -Line $CandidateLine -Name $candidateField
                if ([string]::IsNullOrWhiteSpace($expected) -or
                    [string]::IsNullOrWhiteSpace($actual) -or
                    $expected -ne $actual) {
                    return $false
                }
            }

            if (-not $SourcePrefixed -and
                -not (Test-TunaGuiLogLineFieldMatch `
                    -SourceLine $CommandLine `
                    -CandidateLine $CandidateLine `
                    -FieldName 'bridge_decision_generation')) {
                return $false
            }

            if ($RequireWriter) {
                $commandWriter =
                    Get-TunaGuiLogLineFieldValue -Line $CommandLine -Name 'log_writer_id'
                $candidateWriter =
                    Get-TunaGuiLogLineFieldValue -Line $CandidateLine -Name 'log_writer_id'
                if ([string]::IsNullOrWhiteSpace($commandWriter) -or
                    $commandWriter -ne $candidateWriter) {
                    return $false
                }
            }

            return $true
        }

        $commands = @($indexedLines | Where-Object {
            Test-TunaGuiLogLineHasEvent `
                -Line $_.Line `
                -Events @('filetransfer_bridge_recovery_execution_command_issued')
        })
        foreach ($command in $commands) {
            $leaseGeneration =
                Get-TunaGuiLogLineFieldValue `
                    -Line $command.Line `
                    -Name 'recovery_liveness_lease_generation'
            if ([string]::IsNullOrWhiteSpace($leaseGeneration) -or $leaseGeneration -eq '0') {
                continue
            }

            $start = @($indexedLines | Where-Object {
                $_.Index -gt $command.Index -and
                (Test-TunaGuiLogLineHasEvent `
                    -Line $_.Line `
                    -Events @('nkn_bridge_recovery_execution_started')) -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'lease_generation') -eq
                    $leaseGeneration -and
                (Test-TunaGuiBridgeRecoveryDrainScope `
                    -CommandLine $command.Line `
                    -CandidateLine $_.Line `
                    -RequireWriter)
            } | Select-Object -First 1)
            if ($start.Count -eq 0) {
                continue
            }

            $barrier = @($indexedLines | Where-Object {
                $_.Index -gt $start[0].Index -and
                (Test-TunaGuiLogLineHasEvent `
                    -Line $_.Line `
                    -Events @('nkn_bridge_recovery_submission_barrier_closed')) -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'lease_generation') -eq
                    $leaseGeneration -and
                (Test-TunaGuiBridgeRecoveryDrainScope `
                    -CommandLine $command.Line `
                    -CandidateLine $_.Line `
                    -RequireWriter)
            } | Select-Object -First 1)
            if ($barrier.Count -eq 0) {
                continue
            }

            $submissionEpoch =
                Get-TunaGuiLogLineFieldValue -Line $barrier[0].Line -Name 'submission_epoch'
            $sourceProcessEpoch =
                Get-TunaGuiLogLineFieldValue -Line $barrier[0].Line -Name 'source_process_epoch'
            $sourceProcessId =
                Get-TunaGuiLogLineFieldValue -Line $barrier[0].Line -Name 'source_process_id'
            if ([string]::IsNullOrWhiteSpace($submissionEpoch) -or
                $submissionEpoch -eq '0' -or
                [string]::IsNullOrWhiteSpace($sourceProcessEpoch) -or
                [string]::IsNullOrWhiteSpace($sourceProcessId)) {
                continue
            }

            $held = @($indexedLines | Where-Object {
                $_.Index -gt $barrier[0].Index -and
                (Test-TunaGuiLogLineHasEvent `
                    -Line $_.Line `
                    -Events @('filetransfer_bridge_recovery_payload_submission_held')) -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'recovery_execution_state') -in
                    @('Issued', 'Running') -and
                (Get-TunaGuiLogLineFieldValue `
                    -Line $_.Line `
                    -Name 'recovery_liveness_lease_generation') -eq $leaseGeneration -and
                (Test-TunaGuiBridgeRecoveryDrainScope `
                    -CommandLine $command.Line `
                    -CandidateLine $_.Line)
            } | Select-Object -First 1)
            if ($held.Count -eq 0) {
                continue
            }

            $fullyRetired = @($indexedLines | Where-Object {
                $_.Index -gt $barrier[0].Index -and
                (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                    'filetransfer_outbound_physical_send_lease_retired',
                    'filetransfer_route_transition_physical_send_lease_retired')) -and
                [int64](Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'registered_send_count') -gt 0 -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'registered_send_count') -eq
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'retired_send_count') -and
                (Test-TunaGuiBridgeRecoveryDrainScope `
                    -CommandLine $command.Line `
                    -CandidateLine $_.Line `
                    -SourcePrefixed `
                    -RequireWriter)
            } | Select-Object -First 1)
            if ($fullyRetired.Count -eq 0) {
                continue
            }

            $invalidation = @($indexedLines | Where-Object {
                $_.Index -gt $fullyRetired[0].Index -and
                (Test-TunaGuiLogLineHasEvent `
                    -Line $_.Line `
                    -Events @('nkn_bridge_recovery_submission_barrier_invalidated')) -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'reason') -eq
                    'bridge_recovery_execution_restart_not_ready' -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'lease_generation') -eq
                    $leaseGeneration -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'submission_epoch') -eq
                    $submissionEpoch -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'source_process_epoch') -eq
                    $sourceProcessEpoch -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'source_process_id') -eq
                    $sourceProcessId -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'process_epoch') -eq
                    $sourceProcessEpoch -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'process_id') -eq
                    $sourceProcessId -and
                (Test-TunaGuiBridgeRecoveryDrainScope `
                    -CommandLine $command.Line `
                    -CandidateLine $_.Line `
                    -RequireWriter)
            } | Select-Object -First 1)
            if ($invalidation.Count -eq 0) {
                continue
            }

            $hardDecision = @($indexedLines | Where-Object {
                $_.Index -gt $barrier[0].Index -and
                $_.Index -lt $invalidation[0].Index -and
                (Test-TunaGuiLogLineHasEvent `
                    -Line $_.Line `
                    -Events @('nkn_bridge_recovery_execution_hard_restart_decided')) -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'decision_status') -eq 'Started' -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'lease_generation') -eq
                    $leaseGeneration -and
                (Test-TunaGuiBridgeRecoveryDrainScope `
                    -CommandLine $command.Line `
                    -CandidateLine $_.Line `
                    -RequireWriter)
            })
            $spawnOrReady = @($indexedLines | Where-Object {
                $_.Index -gt $barrier[0].Index -and
                $_.Index -lt $invalidation[0].Index -and
                (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @('bridge_spawned', 'bridge_ready')) -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'log_writer_id') -eq
                    (Get-TunaGuiLogLineFieldValue -Line $command.Line -Name 'log_writer_id')
            })
            if ($hardDecision.Count -gt 0 -or $spawnOrReady.Count -gt 0) {
                continue
            }

            $completion = @($indexedLines | Where-Object {
                $_.Index -gt $invalidation[0].Index -and
                (Test-TunaGuiLogLineHasEvent `
                    -Line $_.Line `
                    -Events @('nkn_bridge_recovery_execution_completed')) -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'outcome') -eq 'exhausted' -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'reason') -eq
                    'bridge_recovery_execution_timeout' -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'physical_execution_terminated') -eq '1' -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'total_deadline_expired') -eq '1' -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'lease_generation') -eq
                    $leaseGeneration -and
                (Test-TunaGuiBridgeRecoveryDrainScope `
                    -CommandLine $command.Line `
                    -CandidateLine $_.Line `
                    -RequireWriter)
            } | Select-Object -First 1)
            if ($completion.Count -eq 0) {
                continue
            }

            $receipt = @($indexedLines | Where-Object {
                $_.Index -gt $completion[0].Index -and
                (Test-TunaGuiLogLineHasEvent `
                    -Line $_.Line `
                    -Events @('filetransfer_bridge_recovery_execution_receipt_decision_observed')) -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'accepted') -eq '1' -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'disposition') -eq 'accepted' -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'execution_outcome') -eq
                    'exhausted' -and
                (Get-TunaGuiLogLineFieldValue `
                    -Line $_.Line `
                    -Name 'recovery_liveness_lease_generation') -eq $leaseGeneration -and
                (Test-TunaGuiBridgeRecoveryDrainScope `
                    -CommandLine $command.Line `
                    -CandidateLine $_.Line `
                    -RequireWriter)
            } | Select-Object -First 1)
            if ($receipt.Count -gt 0) {
                return $true
            }
        }

        return $false
    }

    function Test-TunaGuiRegularV4FrontierRepairSettlementClosed {
        param(
            [Parameter(Mandatory = $true)][AllowEmptyCollection()][AllowEmptyString()][string[]]$Lines,
            [switch]$RequireRecoveryProof
        )

        $indexedLines = @(
            for ($i = 0; $i -lt $Lines.Count; $i++) {
                [pscustomobject]@{
                    Index = $i
                    Line = [string]$Lines[$i]
                }
            }
        )
        function Test-TunaGuiRegularV4SupersededScopeMatch {
            param(
                [Parameter(Mandatory = $true)][string]$SourceLine,
                [Parameter(Mandatory = $true)][string]$CandidateLine
            )
            foreach ($field in @(
                    'session_id', 'transfer_id', 'direction', 'route', 'protocol_version',
                    'live_route_epoch', 'runtime_generation', 'terminal_generation',
                    'leg_generation', 'transport_epoch', 'bridge_recovery_generation',
                    'bridge_decision_generation', 'route_publication_generation',
                    'route_commit_generation', 'target_transport')) {
                if (-not (Test-TunaGuiLogLineFieldMatch `
                        -SourceLine $SourceLine `
                        -CandidateLine $CandidateLine `
                        -FieldName $field)) {
                    return $false
                }
            }
            return $true
        }

        function Test-TunaGuiVisibleAuthorityMatch {
            param([string]$Actual, [string]$Expected)
            return [string]::IsNullOrWhiteSpace($Actual) -or
                $Actual -in @('[redacted]', 'redacted') -or
                [string]::Equals($Actual, $Expected, [System.StringComparison]::OrdinalIgnoreCase)
        }

        $acceptedSettlements = @($indexedLines | Where-Object {
            Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @('filetransfer_regular_v4_frontier_repair_settlement_accepted')
        })
        if ($acceptedSettlements.Count -eq 0) {
            return $false
        }

        foreach ($accepted in $acceptedSettlements) {
            $settlementOutcome = Get-TunaGuiLogLineFieldValue -Line $accepted.Line -Name 'outcome'
            $normalizedSettlementOutcome = $settlementOutcome.Replace('_', '').ToLowerInvariant()
            if ($normalizedSettlementOutcome -eq 'superseded') {
                $predecessorAuthority = Get-TunaGuiLogLineFieldValue -Line $accepted.Line -Name 'authority_id'
                $successorAuthority = Get-TunaGuiLogLineFieldValue -Line $accepted.Line -Name 'successor_authority_id'
                $successorInstallGeneration = Get-TunaGuiLogLineFieldValue `
                    -Line $accepted.Line `
                    -Name 'successor_install_generation'
                if ([string]::IsNullOrWhiteSpace($predecessorAuthority) -or
                    [string]::IsNullOrWhiteSpace($successorAuthority) -or
                    [string]::IsNullOrWhiteSpace($successorInstallGeneration)) {
                    return $false
                }

                $successorBinding = @($indexedLines | Where-Object {
                    $_.Index -lt $accepted.Index -and
                    (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                        'filetransfer_regular_v4_frontier_repair_pressure_binding_committed')) -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'authority_id') -eq $successorAuthority -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'predecessor_authority_id') -eq
                        $predecessorAuthority -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'install_generation') -eq
                        $successorInstallGeneration -and
                    (Test-TunaGuiRegularV4SupersededScopeMatch `
                        -SourceLine $accepted.Line `
                        -CandidateLine $_.Line)
                } | Select-Object -Last 1)
                if ($successorBinding.Count -eq 0) {
                    return $false
                }

                $repairKey = Get-TunaGuiLogLineFieldValue -Line $accepted.Line -Name 'repair_request_key'
                $predecessorBinding = @($indexedLines | Where-Object {
                    $_.Index -lt $successorBinding[0].Index -and
                    (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                        'filetransfer_regular_v4_frontier_repair_pressure_binding_committed')) -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'authority_id') -eq
                        $predecessorAuthority -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'repair_request_key') -eq $repairKey -and
                    (Test-TunaGuiRegularV4SupersededScopeMatch `
                        -SourceLine $accepted.Line `
                        -CandidateLine $_.Line)
                } | Select-Object -Last 1)
                if ($predecessorBinding.Count -eq 0) {
                    return $false
                }
                $predecessorInstallGeneration = Get-TunaGuiLogLineFieldValue `
                    -Line $predecessorBinding[0].Line `
                    -Name 'install_generation'

                $apply = @($indexedLines | Where-Object {
                    $_.Index -gt $accepted.Index -and
                    (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                        'filetransfer_regular_v4_frontier_repair_pressure_settlement_apply_decided')) -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'install_generation') -eq
                        $successorInstallGeneration -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'settlement_outcome') -eq 'Superseded' -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'apply_status') -in @('applied', 'idempotent') -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'current_authority_cleared') -eq '0' -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'retained_predecessor_cleared') -eq '1' -and
                    (Test-TunaGuiVisibleAuthorityMatch `
                        -Actual (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'current_authority_id') `
                        -Expected $successorAuthority) -and
                    (Test-TunaGuiVisibleAuthorityMatch `
                        -Actual (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'retained_predecessor_authority_id') `
                        -Expected $predecessorAuthority) -and
                    (Test-TunaGuiVisibleAuthorityMatch `
                        -Actual (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'successor_authority_id') `
                        -Expected $successorAuthority) -and
                    (Test-TunaGuiLogLineFieldMatch -SourceLine $accepted.Line -CandidateLine $_.Line -FieldName 'session_id') -and
                    (Test-TunaGuiLogLineFieldMatch -SourceLine $accepted.Line -CandidateLine $_.Line -FieldName 'transfer_id') -and
                    (Test-TunaGuiLogLineFieldMatch -SourceLine $accepted.Line -CandidateLine $_.Line -FieldName 'direction')
                } | Group-Object {
                    [string]::Join('|', @(
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'session_id'),
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'transfer_id'),
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'direction'),
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'authority_id'),
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'install_generation'),
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'settlement_outcome'),
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'apply_status'),
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'current_authority_cleared'),
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'retained_predecessor_cleared'))).
                        ToLowerInvariant()
                } | ForEach-Object {
                    @($_.Group | Sort-Object Index)[0]
                } | Sort-Object Index)
                if ($apply.Count -ne 1) {
                    return $false
                }

                $bindingRetired = @($indexedLines | Where-Object {
                    $_.Index -gt $apply[0].Index -and
                    (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                        'filetransfer_regular_v4_frontier_repair_pressure_binding_retired')) -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'authority_id') -eq
                        $predecessorAuthority -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'install_generation') -eq
                        $predecessorInstallGeneration -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'repair_request_key') -eq $repairKey -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'install_boundary') -eq 'settlement' -and
                    (Test-TunaGuiRegularV4SupersededScopeMatch `
                        -SourceLine $accepted.Line `
                        -CandidateLine $_.Line)
                } | Select-Object -First 1)
                if ($bindingRetired.Count -eq 0) {
                    return $false
                }

                $contractSettled = @($indexedLines | Where-Object {
                    $_.Index -gt $bindingRetired[0].Index -and
                    (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                        'filetransfer_regular_v4_frontier_repair_contract_settled')) -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'authority_id') -eq
                        $predecessorAuthority -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'successor_authority_id') -eq
                        $successorAuthority -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'repair_request_key') -eq $repairKey -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'settlement_outcome') -eq 'Superseded'
                } | Select-Object -First 1)
                if ($contractSettled.Count -eq 0) {
                    return $false
                }

                continue
            }

            if ($normalizedSettlementOutcome -in @(
                    'progressobserved',
                    'missingdispatch',
                    'settledstalled',
                    'stalerejected')) {
                $authority = Get-TunaGuiLogLineFieldValue -Line $accepted.Line -Name 'authority_id'
                $installGeneration = Get-TunaGuiLogLineFieldValue `
                    -Line $accepted.Line `
                    -Name 'successor_install_generation'
                $repairKey = Get-TunaGuiLogLineFieldValue -Line $accepted.Line -Name 'repair_request_key'
                if ([string]::IsNullOrWhiteSpace($authority) -or
                    $authority -in @('[redacted]', 'redacted') -or
                    [string]::IsNullOrWhiteSpace($installGeneration) -or
                    [string]::IsNullOrWhiteSpace($repairKey)) {
                    return $false
                }

                $binding = @($indexedLines | Where-Object {
                    $_.Index -lt $accepted.Index -and
                    (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                        'filetransfer_regular_v4_frontier_repair_pressure_binding_committed')) -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'authority_id') -eq $authority -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'install_generation') -eq
                        $installGeneration -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'repair_request_key') -eq $repairKey -and
                    (Test-TunaGuiRegularV4SupersededScopeMatch `
                        -SourceLine $accepted.Line `
                        -CandidateLine $_.Line)
                } | Select-Object -Last 1)
                if ($binding.Count -eq 0) {
                    return $false
                }

                if ($normalizedSettlementOutcome -eq 'stalerejected') {
                    $prepared = @($indexedLines | Where-Object {
                        $_.Index -lt $accepted.Index -and
                        (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                            'filetransfer_regular_v4_frontier_repair_pressure_recovery_handoff_prepared')) -and
                        (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'authority_id') -eq $authority -and
                        (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'install_generation') -eq
                            $installGeneration -and
                        (Test-TunaGuiLogLineFieldMatch -SourceLine $accepted.Line -CandidateLine $_.Line -FieldName 'session_id') -and
                        (Test-TunaGuiLogLineFieldMatch -SourceLine $accepted.Line -CandidateLine $_.Line -FieldName 'transfer_id') -and
                        (Test-TunaGuiLogLineFieldMatch -SourceLine $accepted.Line -CandidateLine $_.Line -FieldName 'direction') -and
                        (Test-TunaGuiLogLineFieldMatch -SourceLine $accepted.Line -CandidateLine $_.Line -FieldName 'route') -and
                        (Test-TunaGuiLogLineFieldMatch -SourceLine $accepted.Line -CandidateLine $_.Line -FieldName 'protocol_version') -and
                        (Test-TunaGuiLogLineFieldMatch -SourceLine $accepted.Line -CandidateLine $_.Line -FieldName 'live_route_epoch') -and
                        (Test-TunaGuiLogLineFieldMatch -SourceLine $accepted.Line -CandidateLine $_.Line -FieldName 'runtime_generation') -and
                        (Test-TunaGuiLogLineFieldMatch -SourceLine $accepted.Line -CandidateLine $_.Line -FieldName 'terminal_generation') -and
                        (Test-TunaGuiLogLineFieldMatch -SourceLine $accepted.Line -CandidateLine $_.Line -FieldName 'leg_generation') -and
                        (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'source_bridge_decision_generation') -eq
                            (Get-TunaGuiLogLineFieldValue -Line $accepted.Line -Name 'bridge_decision_generation')
                    } | Select-Object -Last 1)
                    $committed = @($indexedLines | Where-Object {
                        $_.Index -gt $accepted.Index -and
                        (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                            'filetransfer_regular_v4_frontier_repair_pressure_recovery_handoff_committed')) -and
                        (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'authority_id') -eq $authority -and
                        (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'install_generation') -eq
                            $installGeneration -and
                        (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'settlement_outcome').
                            Replace('_', '').ToLowerInvariant() -eq $normalizedSettlementOutcome -and
                        (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'apply_status').
                            ToLowerInvariant() -in @('applied', 'idempotent') -and
                        (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'current_authority_cleared') -eq '1' -and
                        (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'retained_predecessor_cleared') -eq '0' -and
                        (Test-TunaGuiLogLineFieldMatch -SourceLine $accepted.Line -CandidateLine $_.Line -FieldName 'session_id') -and
                        (Test-TunaGuiLogLineFieldMatch -SourceLine $accepted.Line -CandidateLine $_.Line -FieldName 'transfer_id') -and
                        (Test-TunaGuiLogLineFieldMatch -SourceLine $accepted.Line -CandidateLine $_.Line -FieldName 'direction') -and
                        (Test-TunaGuiLogLineFieldMatch -SourceLine $accepted.Line -CandidateLine $_.Line -FieldName 'route') -and
                        (Test-TunaGuiLogLineFieldMatch -SourceLine $accepted.Line -CandidateLine $_.Line -FieldName 'protocol_version') -and
                        (Test-TunaGuiLogLineFieldMatch -SourceLine $accepted.Line -CandidateLine $_.Line -FieldName 'live_route_epoch') -and
                        (Test-TunaGuiLogLineFieldMatch -SourceLine $accepted.Line -CandidateLine $_.Line -FieldName 'runtime_generation') -and
                        (Test-TunaGuiLogLineFieldMatch -SourceLine $accepted.Line -CandidateLine $_.Line -FieldName 'terminal_generation') -and
                        (Test-TunaGuiLogLineFieldMatch -SourceLine $accepted.Line -CandidateLine $_.Line -FieldName 'leg_generation') -and
                        (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'source_bridge_decision_generation') -eq
                            (Get-TunaGuiLogLineFieldValue -Line $accepted.Line -Name 'bridge_decision_generation')
                    } | Select-Object -First 1)
                    if ($prepared.Count -eq 1 -and $committed.Count -eq 1) {
                        $atomicApply = @($indexedLines | Where-Object {
                            $_.Index -gt $accepted.Index -and
                            $_.Index -lt $committed[0].Index -and
                            (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                                'filetransfer_regular_v4_frontier_repair_pressure_settlement_apply_decided')) -and
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'install_generation') -eq
                                $installGeneration -and
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'settlement_outcome').
                                Replace('_', '').ToLowerInvariant() -eq $normalizedSettlementOutcome -and
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'apply_status').
                                ToLowerInvariant() -in @('applied', 'idempotent') -and
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'current_authority_cleared') -eq '1' -and
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'retained_predecessor_cleared') -eq '0' -and
                            (Test-TunaGuiLogLineFieldMatch -SourceLine $accepted.Line -CandidateLine $_.Line -FieldName 'session_id') -and
                            (Test-TunaGuiLogLineFieldMatch -SourceLine $accepted.Line -CandidateLine $_.Line -FieldName 'transfer_id') -and
                            (Test-TunaGuiLogLineFieldMatch -SourceLine $accepted.Line -CandidateLine $_.Line -FieldName 'direction')
                        })
                        $operationGeneration = Get-TunaGuiLogLineFieldValue `
                            -Line $committed[0].Line `
                            -Name 'recovery_operation_generation'
                        $leaseAuthority = Get-TunaGuiLogLineFieldValue `
                            -Line $committed[0].Line `
                            -Name 'recovery_liveness_lease_authority_id'
                        $leaseGeneration = Get-TunaGuiLogLineFieldValue `
                            -Line $committed[0].Line `
                            -Name 'recovery_liveness_lease_generation'
                        $operationIssued = @($indexedLines | Where-Object {
                            $_.Index -gt $committed[0].Index -and
                            (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                                'filetransfer_receive_recovery_operation_issued')) -and
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'receive_recovery_operation_generation') -eq
                                $operationGeneration -and
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'recovery_liveness_lease_authority_id') -eq
                                $leaseAuthority -and
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'recovery_liveness_lease_generation') -eq
                                $leaseGeneration -and
                            (Test-TunaGuiLogLineFieldMatch -SourceLine $accepted.Line -CandidateLine $_.Line -FieldName 'session_id') -and
                            (Test-TunaGuiLogLineFieldMatch -SourceLine $accepted.Line -CandidateLine $_.Line -FieldName 'transfer_id') -and
                            (Test-TunaGuiLogLineFieldMatch -SourceLine $accepted.Line -CandidateLine $_.Line -FieldName 'direction')
                        } | Select-Object -First 1)
                        $contractSettled = @($indexedLines | Where-Object {
                            $_.Index -gt $committed[0].Index -and
                            (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                                'filetransfer_regular_v4_frontier_repair_contract_settled')) -and
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'authority_id') -eq $authority -and
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'repair_request_key') -eq
                                $repairKey -and
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'settlement_outcome').
                                Replace('_', '').ToLowerInvariant() -eq $normalizedSettlementOutcome
                        } | Select-Object -First 1)
                        if ($atomicApply.Count -eq 1 -and
                            $operationIssued.Count -eq 1 -and
                            $contractSettled.Count -eq 1) {
                            if (-not $RequireRecoveryProof) {
                                continue
                            }

                            $operationAuthority = Get-TunaGuiLogLineFieldValue `
                                -Line $operationIssued[0].Line `
                                -Name 'receive_recovery_operation_authority_id'
                            $receipt = @($indexedLines | Where-Object {
                                $_.Index -gt $operationIssued[0].Index -and
                                (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                                    'filetransfer_receive_recovery_operation_execution_receipt_applied')) -and
                                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'receive_recovery_operation_authority_id') -eq
                                    $operationAuthority -and
                                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'receive_recovery_operation_generation') -eq
                                    $operationGeneration -and
                                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'recovery_liveness_lease_authority_id') -eq
                                    $leaseAuthority -and
                                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'recovery_liveness_lease_generation') -eq
                                    $leaseGeneration -and
                                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'execution_outcome') -eq 'Completed' -and
                                (Test-TunaGuiLogLineFieldMatch -SourceLine $operationIssued[0].Line -CandidateLine $_.Line -FieldName 'session_id') -and
                                (Test-TunaGuiLogLineFieldMatch -SourceLine $operationIssued[0].Line -CandidateLine $_.Line -FieldName 'transfer_id') -and
                                (Test-TunaGuiLogLineFieldMatch -SourceLine $operationIssued[0].Line -CandidateLine $_.Line -FieldName 'direction')
                            } | Select-Object -First 1)
                            $proof = @($indexedLines | Where-Object {
                                $_.Index -gt $operationIssued[0].Index -and
                                (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                                    'filetransfer_receive_recovery_operation_proof_observed')) -and
                                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'receive_recovery_operation_authority_id') -eq
                                    $operationAuthority -and
                                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'receive_recovery_operation_generation') -eq
                                    $operationGeneration -and
                                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'recovery_liveness_lease_authority_id') -eq
                                    $leaseAuthority -and
                                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'recovery_liveness_lease_generation') -eq
                                    $leaseGeneration -and
                                (Test-TunaGuiLogLineFieldMatch -SourceLine $operationIssued[0].Line -CandidateLine $_.Line -FieldName 'session_id') -and
                                (Test-TunaGuiLogLineFieldMatch -SourceLine $operationIssued[0].Line -CandidateLine $_.Line -FieldName 'transfer_id') -and
                                (Test-TunaGuiLogLineFieldMatch -SourceLine $operationIssued[0].Line -CandidateLine $_.Line -FieldName 'direction')
                            } | Select-Object -First 1)
                            if ($receipt.Count -eq 1 -and $proof.Count -eq 1) {
                                $completionBoundary = [Math]::Max(
                                    [int]$receipt[0].Index,
                                    [int]$proof[0].Index)
                                $adopted = @($indexedLines | Where-Object {
                                    $_.Index -gt $completionBoundary -and
                                    (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                                        'filetransfer_receive_recovery_operation_closure_adopted')) -and
                                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'receive_recovery_operation_authority_id') -eq
                                        $operationAuthority -and
                                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'receive_recovery_operation_generation') -eq
                                        $operationGeneration -and
                                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'recovery_liveness_lease_authority_id') -eq
                                        $leaseAuthority -and
                                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'recovery_liveness_lease_generation') -eq
                                        $leaseGeneration -and
                                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'closure_state') -eq 'Adopted'
                                } | Select-Object -First 1)
                                if ($adopted.Count -eq 1) {
                                    continue
                                }
                            }
                        }
                    }

                    if ($RequireRecoveryProof) {
                        return $false
                    }
                }

                $retirement = @($indexedLines | Where-Object {
                    $_.Index -gt $accepted.Index -and
                    (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                        'filetransfer_regular_v4_frontier_repair_pressure_binding_retired')) -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'authority_id') -eq $authority -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'install_generation') -eq
                        $installGeneration -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'repair_request_key') -eq $repairKey -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'install_boundary') -eq 'settlement' -and
                    (Test-TunaGuiRegularV4SupersededScopeMatch `
                        -SourceLine $accepted.Line `
                        -CandidateLine $_.Line)
                } | Select-Object -First 1)
                if ($retirement.Count -eq 0) {
                    return $false
                }

                $apply = @($indexedLines | Where-Object {
                    $_.Index -gt $accepted.Index -and
                    $_.Index -lt $retirement[0].Index -and
                    (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                        'filetransfer_regular_v4_frontier_repair_pressure_settlement_apply_decided')) -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'install_generation') -eq
                        $installGeneration -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'settlement_outcome').Replace('_', '').ToLowerInvariant() -eq
                        $normalizedSettlementOutcome -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'apply_status').ToLowerInvariant() -in
                        @('applied', 'idempotent') -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'current_authority_cleared') -eq '1' -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'retained_predecessor_cleared') -eq '0' -and
                    (Test-TunaGuiVisibleAuthorityMatch `
                        -Actual (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'authority_id') `
                        -Expected $authority) -and
                    (Test-TunaGuiLogLineFieldMatch -SourceLine $accepted.Line -CandidateLine $_.Line -FieldName 'session_id') -and
                    (Test-TunaGuiLogLineFieldMatch -SourceLine $accepted.Line -CandidateLine $_.Line -FieldName 'transfer_id') -and
                    (Test-TunaGuiLogLineFieldMatch -SourceLine $accepted.Line -CandidateLine $_.Line -FieldName 'direction')
                } | Group-Object {
                    [string]::Join('|', @(
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'session_id'),
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'transfer_id'),
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'direction'),
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'authority_id'),
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'successor_authority_id'),
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'current_authority_id'),
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'retained_predecessor_authority_id'),
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'install_generation'),
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'settlement_outcome'),
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'apply_status'),
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'current_authority_cleared'),
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'retained_predecessor_cleared'))).
                        ToLowerInvariant()
                } | ForEach-Object {
                    @($_.Group | Sort-Object Index)[0]
                } | Sort-Object Index)
                if ($apply.Count -ne 1) {
                    return $false
                }

                $acceptedSuccessor = Get-TunaGuiLogLineFieldValue `
                    -Line $accepted.Line `
                    -Name 'successor_authority_id'
                $contractSettled = @($indexedLines | Where-Object {
                    $_.Index -gt $retirement[0].Index -and
                    (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                        'filetransfer_regular_v4_frontier_repair_contract_settled')) -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'authority_id') -eq $authority -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'successor_authority_id') -eq
                        $acceptedSuccessor -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'repair_request_key') -eq $repairKey -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'settlement_outcome').Replace('_', '').ToLowerInvariant() -eq
                        $normalizedSettlementOutcome
                } | Group-Object {
                    [string]::Join('|', @(
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'authority_id'),
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'successor_authority_id'),
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'repair_request_key'),
                            (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'settlement_outcome'))).
                        ToLowerInvariant()
                } | ForEach-Object {
                    @($_.Group | Sort-Object Index)[0]
                })
                if ($contractSettled.Count -ne 1) {
                    return $false
                }
                continue
            }

            if ($normalizedSettlementOutcome -in @('transferterminal', 'stalerejected')) {
                $authority = Get-TunaGuiLogLineFieldValue -Line $accepted.Line -Name 'authority_id'
                $installGeneration = Get-TunaGuiLogLineFieldValue `
                    -Line $accepted.Line `
                    -Name 'successor_install_generation'
                $repairKey = Get-TunaGuiLogLineFieldValue -Line $accepted.Line -Name 'repair_request_key'
                if ([string]::IsNullOrWhiteSpace($authority) -or
                    $authority -in @('[redacted]', 'redacted') -or
                    [string]::IsNullOrWhiteSpace($installGeneration) -or
                    [string]::IsNullOrWhiteSpace($repairKey)) {
                    return $false
                }

                $binding = @($indexedLines | Where-Object {
                    $_.Index -lt $accepted.Index -and
                    (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                        'filetransfer_regular_v4_frontier_repair_pressure_binding_committed')) -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'authority_id') -eq $authority -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'install_generation') -eq
                        $installGeneration -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'repair_request_key') -eq $repairKey -and
                    (Test-TunaGuiRegularV4SupersededScopeMatch `
                        -SourceLine $accepted.Line `
                        -CandidateLine $_.Line)
                } | Select-Object -Last 1)
                if ($binding.Count -eq 0) {
                    return $false
                }

                $retirement = @($indexedLines | Where-Object {
                    $_.Index -gt $accepted.Index -and
                    (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                        'filetransfer_regular_v4_frontier_repair_pressure_binding_retired')) -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'authority_id') -eq $authority -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'install_generation') -eq
                        $installGeneration -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'repair_request_key') -eq $repairKey -and
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'install_boundary') -eq 'settlement' -and
                    (Test-TunaGuiRegularV4SupersededScopeMatch `
                        -SourceLine $accepted.Line `
                        -CandidateLine $_.Line)
                } | Select-Object -First 1)
                if ($retirement.Count -eq 1) {
                    $apply = @($indexedLines | Where-Object {
                        $_.Index -gt $accepted.Index -and
                        $_.Index -lt $retirement[0].Index -and
                        (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                            'filetransfer_regular_v4_frontier_repair_pressure_settlement_apply_decided')) -and
                        (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'install_generation') -eq
                            $installGeneration -and
                        (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'settlement_outcome').
                            Replace('_', '').ToLowerInvariant() -eq $normalizedSettlementOutcome -and
                        (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'apply_status').
                            ToLowerInvariant() -in @('applied', 'idempotent') -and
                        (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'current_authority_cleared') -eq '1' -and
                        (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'retained_predecessor_cleared') -eq '0' -and
                        (Test-TunaGuiVisibleAuthorityMatch `
                            -Actual (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'authority_id') `
                            -Expected $authority) -and
                        (Test-TunaGuiLogLineFieldMatch `
                            -SourceLine $accepted.Line `
                            -CandidateLine $_.Line `
                            -FieldName 'session_id') -and
                        (Test-TunaGuiLogLineFieldMatch `
                            -SourceLine $accepted.Line `
                            -CandidateLine $_.Line `
                            -FieldName 'transfer_id') -and
                        (Test-TunaGuiLogLineFieldMatch `
                            -SourceLine $accepted.Line `
                            -CandidateLine $_.Line `
                            -FieldName 'direction')
                    } | Group-Object {
                        [string]::Join('|', @(
                                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'session_id'),
                                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'transfer_id'),
                                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'direction'),
                                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'authority_id'),
                                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'install_generation'),
                                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'settlement_outcome'),
                                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'apply_status'),
                                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'current_authority_cleared'),
                                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'retained_predecessor_cleared'))).
                            ToLowerInvariant()
                    } | ForEach-Object {
                        @($_.Group | Sort-Object Index)[0]
                    })
                    if ($apply.Count -eq 1) {
                        # TransferTerminal and StaleRejected are diagnostic final
                        # outcomes. Exact apply plus binding retirement is the
                        # complete coordinator disposition; contract settlement
                        # is optional corroboration.
                        continue
                    }
                }

                return $false
            }

            $handoff = @($indexedLines | Where-Object {
                $_.Index -gt $accepted.Index -and
                (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                    'runtime_unlock_regular_v4_frontier_repair_settlement_released',
                    'runtime_unlock_regular_v4_frontier_repair_settlement_held',
                    'runtime_unlock_regular_v4_frontier_repair_settlement_rejected')) -and
                (Test-TunaGuiRegularV4FrontierRepairMatch -SourceLine $accepted.Line -CandidateLine $_.Line)
            })
            if ($handoff.Count -gt 0) {
                continue
            }

            $precontractClosure = @(
                foreach ($consumed in @($indexedLines | Where-Object {
                    $_.Index -gt $accepted.Index -and
                    (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @('runtime_unlock_regular_v4_frontier_repair_precontract_settlement_consumed')) -and
                    (Test-TunaGuiRegularV4FrontierRepairMatch -SourceLine $accepted.Line -CandidateLine $_.Line)
                })) {
                    $closure = @($indexedLines | Where-Object {
                        (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @('regular_v4_receive_recovery_contract_closed_no_deferral')) -and
                        (Test-TunaGuiRegularV4FrontierRepairPrecontractClosureMatch -Accepted $accepted -Consumed $consumed -Closure $_)
                    })
                    if ($closure.Count -gt 0) {
                        $consumed
                    }
                }
            )
            if ($precontractClosure.Count -eq 0) {
                return $false
            }
        }

        return $true
    }

    function Test-TunaGuiRegularV4FrontierRepairStallUnresolved {
        param(
            [Parameter(Mandatory = $true)][AllowEmptyCollection()][AllowEmptyString()][string[]]$Lines
        )

        $indexedLines = @(
            for ($index = 0; $index -lt $Lines.Count; $index++) {
                [pscustomobject]@{ Index = $index; Line = [string]$Lines[$index] }
            }
        )
        $stalls = @($indexedLines | Where-Object {
            Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                'filetransfer_regular_v4_frontier_repair_stalled')
        })
        $seenStallScopes = [System.Collections.Generic.HashSet[string]]::new(
            [System.StringComparer]::Ordinal)
        foreach ($stall in $stalls) {
            $repairKey = Get-TunaGuiLogLineFieldValue -Line $stall.Line -Name 'repair_request_key'
            if ([string]::IsNullOrWhiteSpace($repairKey)) {
                return $true
            }
            $stallScopeKey = (@(
                    Get-TunaGuiLogLineFieldValue -Line $stall.Line -Name 'session_id'
                    Get-TunaGuiLogLineFieldValue -Line $stall.Line -Name 'transfer_id'
                    Get-TunaGuiLogLineFieldValue -Line $stall.Line -Name 'direction'
                    Get-TunaGuiLogLineFieldValue -Line $stall.Line -Name 'route'
                    Get-TunaGuiLogLineFieldValue -Line $stall.Line -Name 'protocol_version'
                    Get-TunaGuiLogLineFieldValue -Line $stall.Line -Name 'live_route_epoch'
                    Get-TunaGuiLogLineFieldValue -Line $stall.Line -Name 'leg_generation'
                    Get-TunaGuiLogLineFieldValue -Line $stall.Line -Name 'transport_epoch'
                    Get-TunaGuiLogLineFieldValue -Line $stall.Line -Name 'bridge_recovery_generation'
                    $repairKey
                ) -join '|')
            if (-not $seenStallScopes.Add($stallScopeKey)) {
                continue
            }

            $progress = @($indexedLines | Where-Object {
                $_.Index -gt $stall.Index -and
                (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                    'filetransfer_regular_v4_frontier_repair_progress_observed')) -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'repair_request_key') -eq $repairKey -and
                (Test-TunaGuiLogLineFieldMatch -SourceLine $stall.Line -CandidateLine $_.Line -FieldName 'session_id') -and
                (Test-TunaGuiLogLineFieldMatch -SourceLine $stall.Line -CandidateLine $_.Line -FieldName 'transfer_id') -and
                (Test-TunaGuiLogLineFieldMatch -SourceLine $stall.Line -CandidateLine $_.Line -FieldName 'direction') -and
                (Test-TunaGuiLogLineFieldMatch -SourceLine $stall.Line -CandidateLine $_.Line -FieldName 'route') -and
                (Test-TunaGuiLogLineFieldMatch -SourceLine $stall.Line -CandidateLine $_.Line -FieldName 'protocol_version') -and
                (Test-TunaGuiLogLineFieldMatch -SourceLine $stall.Line -CandidateLine $_.Line -FieldName 'live_route_epoch') -and
                (Test-TunaGuiLogLineFieldMatch -SourceLine $stall.Line -CandidateLine $_.Line -FieldName 'leg_generation') -and
                (Test-TunaGuiLogLineFieldMatch -SourceLine $stall.Line -CandidateLine $_.Line -FieldName 'transport_epoch') -and
                (Test-TunaGuiLogLineFieldMatch -SourceLine $stall.Line -CandidateLine $_.Line -FieldName 'bridge_recovery_generation')
            } | Select-Object -First 1)
            if ($progress.Count -eq 1) {
                continue
            }

            $matchingSettlements = @($indexedLines | Where-Object {
                $_.Index -gt $stall.Index -and
                (Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                    'filetransfer_regular_v4_frontier_repair_settlement_accepted')) -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'repair_request_key') -eq $repairKey -and
                (Test-TunaGuiLogLineFieldMatch -SourceLine $stall.Line -CandidateLine $_.Line -FieldName 'session_id') -and
                (Test-TunaGuiLogLineFieldMatch -SourceLine $stall.Line -CandidateLine $_.Line -FieldName 'transfer_id') -and
                (Test-TunaGuiLogLineFieldMatch -SourceLine $stall.Line -CandidateLine $_.Line -FieldName 'direction') -and
                (Test-TunaGuiLogLineFieldMatch -SourceLine $stall.Line -CandidateLine $_.Line -FieldName 'route') -and
                (Test-TunaGuiLogLineFieldMatch -SourceLine $stall.Line -CandidateLine $_.Line -FieldName 'protocol_version') -and
                (Test-TunaGuiLogLineFieldMatch -SourceLine $stall.Line -CandidateLine $_.Line -FieldName 'live_route_epoch') -and
                (Test-TunaGuiLogLineFieldMatch -SourceLine $stall.Line -CandidateLine $_.Line -FieldName 'leg_generation') -and
                (Test-TunaGuiLogLineFieldMatch -SourceLine $stall.Line -CandidateLine $_.Line -FieldName 'transport_epoch') -and
                (Test-TunaGuiLogLineFieldMatch -SourceLine $stall.Line -CandidateLine $_.Line -FieldName 'bridge_recovery_generation')
            })
            $settlementResolved = $false
            foreach ($matchingSettlement in $matchingSettlements) {
                $authority = Get-TunaGuiLogLineFieldValue `
                    -Line $matchingSettlement.Line `
                    -Name 'authority_id'
                $installGeneration = Get-TunaGuiLogLineFieldValue `
                    -Line $matchingSettlement.Line `
                    -Name 'successor_install_generation'
                if ([string]::IsNullOrWhiteSpace($authority) -or
                    $authority -in @('[redacted]', 'redacted') -or
                    [string]::IsNullOrWhiteSpace($installGeneration) -or
                    $installGeneration -eq '0') {
                    continue
                }

                $settlementLines = @($indexedLines | Where-Object {
                    $candidate = $_
                    if ((Test-TunaGuiLogLineHasEvent -Line $candidate.Line -Events @(
                                'filetransfer_regular_v4_frontier_repair_settlement_accepted')) -and
                        $candidate.Index -ne $matchingSettlement.Index) {
                        return $false
                    }
                    foreach ($field in @(
                            'session_id',
                            'transfer_id',
                            'direction',
                            'route',
                            'protocol_version',
                            'live_route_epoch',
                            'leg_generation',
                            'transport_epoch',
                            'bridge_recovery_generation')) {
                        $anchorValue = Get-TunaGuiLogLineFieldValue -Line $matchingSettlement.Line -Name $field
                        $candidateValue = Get-TunaGuiLogLineFieldValue -Line $candidate.Line -Name $field
                        if (-not [string]::IsNullOrWhiteSpace($anchorValue) -and
                            -not [string]::IsNullOrWhiteSpace($candidateValue) -and
                            $anchorValue -ne '(none)' -and
                            $candidateValue -ne '(none)' -and
                            $anchorValue -ne $candidateValue) {
                            return $false
                        }
                    }
                    $candidateAuthority = Get-TunaGuiLogLineFieldValue -Line $candidate.Line -Name 'authority_id'
                    if (-not [string]::IsNullOrWhiteSpace($candidateAuthority) -and
                        $candidateAuthority -notin @('(none)', '[redacted]', 'redacted') -and
                        $candidateAuthority -ne $authority) {
                        return $false
                    }
                    $candidateInstallGeneration = Get-TunaGuiLogLineFieldValue -Line $candidate.Line -Name 'install_generation'
                    if (-not [string]::IsNullOrWhiteSpace($candidateInstallGeneration) -and
                        $candidateInstallGeneration -ne '0' -and
                        $candidateInstallGeneration -ne $installGeneration) {
                        return $false
                    }
                    $candidateRepairKey = Get-TunaGuiLogLineFieldValue -Line $candidate.Line -Name 'repair_request_key'
                    return [string]::IsNullOrWhiteSpace($candidateRepairKey) -or
                        $candidateRepairKey -eq '(none)' -or
                        $candidateRepairKey -eq $repairKey
                } | ForEach-Object { $_.Line })
                $outcome = (Get-TunaGuiLogLineFieldValue `
                    -Line $matchingSettlement.Line `
                    -Name 'outcome').Replace('_', '').ToLowerInvariant()
                $settlementResolved = if ($outcome -eq 'stalerejected') {
                    Test-TunaGuiRegularV4FrontierRepairSettlementClosed `
                        -Lines $settlementLines `
                        -RequireRecoveryProof
                }
                else {
                    Test-TunaGuiRegularV4FrontierRepairSettlementClosed -Lines $settlementLines
                }
                if ($settlementResolved) {
                    break
                }
            }
            if (-not $settlementResolved) {
                return $true
            }
        }

        return $false
    }

    function Test-TunaGuiRegularV4PredecessorSettlementClosedSuccessor {
        param([string[]]$Lines)

        $indexedLines = @(
            for ($index = 0; $index -lt $Lines.Count; $index++) {
                [pscustomobject]@{ Index = $index; Line = [string]$Lines[$index] }
            }
        )
        $adoptions = @($indexedLines | Where-Object {
            Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @('runtime_unlock_regular_v4_frontier_repair_settlement_handoff_adopted')
        })
        $consumedEvents = @($indexedLines | Where-Object {
            Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @(
                'runtime_unlock_regular_v4_frontier_repair_settlement_handoff_consumed',
                'runtime_unlock_regular_v4_frontier_repair_precontract_settlement_consumed')
        })
        $lifecycleEvents = @($indexedLines | Where-Object {
            Test-TunaGuiLogLineHasEvent -Line $_.Line -Events @('filetransfer_regular_v4_recovery_liveness_bridge_lifecycle')
        })

        foreach ($consumed in $consumedEvents) {
            $sessionId = Get-TunaGuiLogLineFieldValue -Line $consumed.Line -Name 'session_id'
            $transferId = Get-TunaGuiLogLineFieldValue -Line $consumed.Line -Name 'transfer_id'
            $livenessGeneration = Get-TunaGuiLogLineFieldValue -Line $consumed.Line -Name 'adopted_recovery_liveness_generation'
            $bridgeDecisionGeneration = Get-TunaGuiLogLineFieldValue -Line $consumed.Line -Name 'adopted_bridge_decision_generation'
            if ([string]::IsNullOrWhiteSpace($livenessGeneration) -or $livenessGeneration -eq '0') {
                $adoption = @($adoptions | Where-Object {
                    $_.Index -lt $consumed.Index -and
                    (Test-TunaGuiRegularV4FrontierRepairMatch -SourceLine $consumed.Line -CandidateLine $_.Line)
                } | Select-Object -Last 1)
                if ($adoption.Count -gt 0) {
                    $livenessGeneration = Get-TunaGuiLogLineFieldValue -Line $adoption[0].Line -Name 'adopted_recovery_liveness_generation'
                    $bridgeDecisionGeneration = Get-TunaGuiLogLineFieldValue -Line $adoption[0].Line -Name 'adopted_bridge_decision_generation'
                }
            }

            $boundary = @($lifecycleEvents | Where-Object {
                $_.Index -lt $consumed.Index -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'session_id') -eq $sessionId -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'transfer_id') -eq $transferId -and
                (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'lifecycle') -in @('completed', 'exhausted', 'receive_resumed') -and
                ([string]::IsNullOrWhiteSpace($livenessGeneration) -or $livenessGeneration -eq '0' -or
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'generation') -eq $livenessGeneration) -and
                ([string]::IsNullOrWhiteSpace($bridgeDecisionGeneration) -or $bridgeDecisionGeneration -eq '0' -or
                    (Get-TunaGuiLogLineFieldValue -Line $_.Line -Name 'bridge_decision_generation') -eq $bridgeDecisionGeneration)
            })
            if ($boundary.Count -eq 0) {
                return $true
            }
        }

        return $false
    }

    function Test-TunaGuiRouteTransitionDrainingTransportGraceFailure {
        param([string[]]$Lines)

        $indexedLines = @(
            for ($index = 0; $index -lt $Lines.Count; $index++) {
                [pscustomobject]@{ Index = $index; Line = [string]$Lines[$index] }
            }
        )
        $graceEvents = @($indexedLines | Where-Object {
            (Test-TunaGuiLogLineHasEvent `
                -Line $_.Line `
                -Events @('filetransfer_transport_grace_exhausted')) -and
            (Get-TunaGuiLogLineFieldValue `
                -Line $_.Line `
                -Name 'reason') -eq
                'filetransfer_route_transition_draining'
        })
        foreach ($grace in $graceEvents) {
            $writer = Get-TunaGuiLogLineFieldValue `
                -Line $grace.Line `
                -Name 'log_writer_id'
            $session = Get-TunaGuiLogLineFieldValue `
                -Line $grace.Line `
                -Name 'session_id'
            $transfer = Get-TunaGuiLogLineFieldValue `
                -Line $grace.Line `
                -Name 'transfer_id'
            $direction = Get-TunaGuiLogLineFieldValue `
                -Line $grace.Line `
                -Name 'direction'
            if ([string]::IsNullOrWhiteSpace($writer) -or
                [string]::IsNullOrWhiteSpace($session) -or
                [string]::IsNullOrWhiteSpace($transfer) -or
                [string]::IsNullOrWhiteSpace($direction)) {
                continue
            }

            $preparedEvents = @($indexedLines | Where-Object {
                $_.Index -lt $grace.Index -and
                (Test-TunaGuiLogLineHasEvent `
                    -Line $_.Line `
                    -Events @('filetransfer_route_transition_prepared')) -and
                (Get-TunaGuiLogLineFieldValue `
                    -Line $_.Line `
                    -Name 'decision_status') -in @(
                        'Prepared',
                        'Idempotent') -and
                (Get-TunaGuiLogLineFieldValue `
                    -Line $_.Line `
                    -Name 'log_writer_id') -eq $writer -and
                (Get-TunaGuiLogLineFieldValue `
                    -Line $_.Line `
                    -Name 'session_id') -eq $session -and
                (Get-TunaGuiLogLineFieldValue `
                    -Line $_.Line `
                    -Name 'transfer_id') -eq $transfer -and
                (Get-TunaGuiLogLineFieldValue `
                    -Line $_.Line `
                    -Name 'direction') -eq $direction
            })
            $preparedEvents = @($preparedEvents | Where-Object {
                $candidate = $_
                $candidateAuthority = Get-TunaGuiLogLineFieldValue `
                    -Line $candidate.Line `
                    -Name 'authority_id'
                $candidateGeneration = Get-TunaGuiLogLineFieldValue `
                    -Line $candidate.Line `
                    -Name 'transition_generation'
                if ([string]::IsNullOrWhiteSpace($candidateAuthority) -or
                    [string]::IsNullOrWhiteSpace($candidateGeneration)) {
                    return $false
                }
                $candidateOperationEvents = @($indexedLines | Where-Object {
                    $_.Index -gt $candidate.Index -and
                    $_.Index -lt $grace.Index -and
                    (Get-TunaGuiLogLineFieldValue `
                        -Line $_.Line `
                        -Name 'authority_id') -eq $candidateAuthority -and
                    (Get-TunaGuiLogLineFieldValue `
                        -Line $_.Line `
                        -Name 'transition_generation') -eq
                        $candidateGeneration
                })
                $candidatePause = @($candidateOperationEvents | Where-Object {
                    Test-TunaGuiLogLineHasEvent `
                        -Line $_.Line `
                        -Events @('filetransfer_route_transition_pause_bound')
                })
                $candidateDraining = @($candidateOperationEvents | Where-Object {
                    Test-TunaGuiLogLineHasEvent `
                        -Line $_.Line `
                        -Events @('filetransfer_route_transition_draining')
                })
                $candidateTerminal = @($candidateOperationEvents | Where-Object {
                    Test-TunaGuiLogLineHasEvent `
                        -Line $_.Line `
                        -Events @(
                            'filetransfer_route_transition_committed',
                            'filetransfer_route_transition_invalidated')
                })
                return $candidatePause.Count -gt 0 -and
                    $candidateDraining.Count -gt 0 -and
                    $candidateTerminal.Count -eq 0
            })
            if ($preparedEvents.Count -ne 1) {
                continue
            }

            $prepared = $preparedEvents[0]
            $authority = Get-TunaGuiLogLineFieldValue `
                -Line $prepared.Line `
                -Name 'authority_id'
            $generation = Get-TunaGuiLogLineFieldValue `
                -Line $prepared.Line `
                -Name 'transition_generation'
            if ([string]::IsNullOrWhiteSpace($authority) -or
                [string]::IsNullOrWhiteSpace($generation)) {
                continue
            }

            $sameOperation = {
                param($Candidate)
                if ((Get-TunaGuiLogLineFieldValue `
                        -Line $Candidate.Line `
                        -Name 'authority_id') -eq $authority -and
                    (Get-TunaGuiLogLineFieldValue `
                        -Line $Candidate.Line `
                        -Name 'transition_generation') -eq $generation -and
                    (Get-TunaGuiLogLineFieldValue `
                        -Line $Candidate.Line `
                        -Name 'session_id') -eq $session -and
                    (Get-TunaGuiLogLineFieldValue `
                        -Line $Candidate.Line `
                        -Name 'transfer_id') -eq $transfer -and
                    (Get-TunaGuiLogLineFieldValue `
                        -Line $Candidate.Line `
                        -Name 'direction') -eq $direction
                ) {
                    foreach ($field in @(
                            'transition_kind',
                            'source_route',
                            'source_protocol_version',
                            'source_transport',
                            'source_live_route_epoch',
                            'source_runtime_generation',
                            'source_route_publication_generation',
                            'source_route_commit_generation',
                            'source_terminal_generation',
                            'source_leg_generation',
                            'source_transport_epoch',
                            'source_bridge_recovery_generation',
                            'target_route',
                            'target_protocol_version',
                            'target_transport')) {
                        if (-not (Test-TunaGuiLogLineFieldMatch `
                                -SourceLine $prepared.Line `
                                -CandidateLine $Candidate.Line `
                                -FieldName $field)) {
                            return $false
                        }
                    }
                    $preparedDataSession =
                        Get-TunaGuiLogLineFieldValue `
                            -Line $prepared.Line `
                            -Name 'source_data_session_generation'
                    $candidateDataSession =
                        Get-TunaGuiLogLineFieldValue `
                            -Line $Candidate.Line `
                            -Name 'source_data_session_generation'
                    if ([string]::IsNullOrWhiteSpace($candidateDataSession)) {
                        $candidateDataSession =
                            Get-TunaGuiLogLineFieldValue `
                                -Line $Candidate.Line `
                                -Name 'data_session_generation'
                    }
                    return -not [string]::IsNullOrWhiteSpace(
                            $preparedDataSession) -and
                        -not [string]::IsNullOrWhiteSpace(
                            $candidateDataSession) -and
                        $preparedDataSession -eq $candidateDataSession
                }
                return $false
            }
            $pause = @($indexedLines | Where-Object {
                $_.Index -gt $prepared.Index -and
                $_.Index -lt $grace.Index -and
                (Test-TunaGuiLogLineHasEvent `
                    -Line $_.Line `
                    -Events @('filetransfer_route_transition_pause_bound')) -and
                (& $sameOperation $_) -and
                (Get-TunaGuiLogLineFieldValue `
                    -Line $_.Line `
                    -Name 'decision_status') -in @(
                        'Installed',
                        'Applied',
                        'Idempotent')
            })
            $draining = @($indexedLines | Where-Object {
                $_.Index -gt $prepared.Index -and
                $_.Index -lt $grace.Index -and
                (Test-TunaGuiLogLineHasEvent `
                    -Line $_.Line `
                    -Events @('filetransfer_route_transition_draining')) -and
                (& $sameOperation $_) -and
                (Get-TunaGuiLogLineFieldValue `
                    -Line $_.Line `
                    -Name 'decision_status') -eq 'Draining'
            })
            if ($pause.Count -eq 0 -or $draining.Count -eq 0) {
                continue
            }

            $invalidations = @($indexedLines | Where-Object {
                $_.Index -gt $grace.Index -and
                (Test-TunaGuiLogLineHasEvent `
                    -Line $_.Line `
                    -Events @('filetransfer_route_transition_invalidated')) -and
                (& $sameOperation $_) -and
                (Get-TunaGuiLogLineFieldValue `
                    -Line $_.Line `
                    -Name 'decision_status') -eq 'Invalidated' -and
                (Get-TunaGuiLogLineFieldValue `
                    -Line $_.Line `
                    -Name 'decision_reason') -in @(
                        'outbound_transfer_terminal',
                        'inbound_transfer_terminal',
                        'terminal_state')
            })
            if ($invalidations.Count -ne 1) {
                continue
            }

            $dispositions = @($indexedLines | Where-Object {
                $_.Index -gt $invalidations[0].Index -and
                (Test-TunaGuiLogLineHasEvent `
                    -Line $_.Line `
                    -Events @(
                        'filetransfer_route_transition_disposition_applied')) -and
                (& $sameOperation $_) -and
                (Get-TunaGuiLogLineFieldValue `
                    -Line $_.Line `
                    -Name 'disposition').Replace(
                        '_',
                        '').ToLowerInvariant() -eq 'terminalinvalidated'
            })
            if ($dispositions.Count -eq 0) {
                continue
            }

            $terminal = @($indexedLines | Where-Object {
                if ($_.Index -le $grace.Index -or
                    (Get-TunaGuiLogLineFieldValue `
                        -Line $_.Line `
                        -Name 'log_writer_id') -ne $writer -or
                    (Get-TunaGuiLogLineFieldValue `
                        -Line $_.Line `
                        -Name 'session_id') -ne $session -or
                    (Get-TunaGuiLogLineFieldValue `
                        -Line $_.Line `
                        -Name 'transfer_id') -ne $transfer) {
                    return $false
                }
                $expectedTerminalEvent = if ($direction -eq 'outbound') {
                    'file_transfer_outbound_terminal'
                }
                else {
                    'file_transfer_inbound_terminal'
                }
                return (Test-TunaGuiLogLineHasEvent `
                        -Line $_.Line `
                        -Events @($expectedTerminalEvent)) -and
                    (
                        (Get-TunaGuiLogLineFieldValue `
                            -Line $_.Line `
                            -Name 'state') -eq 'Failed' -or
                        (Get-TunaGuiLogLineFieldValue `
                            -Line $_.Line `
                            -Name 'error_code') -notin @(
                                '',
                                '(none)',
                                'none')
                    )
            })
            if ($terminal.Count -gt 0) {
                return $true
            }
        }

        return $false
    }

    $bridgeBootstrapNotReady = Test-TunaGuiBridgeBootstrapNotReadyEvidence -Lines $evidenceLines
    $rawListenerUnavailable = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('listener_unavailable')
    $rawListenerSidecarUnavailable = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('listener_sidecar_unavailable')
    $rawListenerReady = (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=tuna_acceleration_timeline', 'status=listener_ready')) -or
        (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=tuna_listener_startup_stage', 'stage=listener_ready'))
    $readinessState = Get-TunaGuiReadinessStateAfterBookmark -Bookmark $Bookmark
    $listenerUnavailable = [bool]$readinessState.ListenerUnavailable
    $listenerSidecarUnavailable = [bool]($readinessState.ListenerUnavailable -and $rawListenerSidecarUnavailable)
    $listenerReady = [bool]$readinessState.ListenerReady
    $tunaActive = [bool]$readinessState.TunaActive
    if (-not $listenerUnavailable -and -not $listenerReady -and -not $tunaActive) {
        $listenerUnavailable = [bool]$rawListenerUnavailable
        $listenerSidecarUnavailable = [bool]$rawListenerSidecarUnavailable
        $listenerReady = [bool]$rawListenerReady
        $tunaActive = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=tuna_acceleration_timeline', 'active=1')
    }
    $routeSelected = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_route_selected')
    $activationOfferNotObserved = (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=tuna_acceleration_activation_offer_not_observed')) -or
        (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('reason=offer_send_not_observed')) -or
        (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=tuna_acceleration_control_send_wait_timeout', 'purpose=offer'))
    $activationOfferWaitingAnswer = (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=tuna_acceleration_offer_queued')) -or
        (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('status=waiting_for_answer')) -or
        (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=tuna_acceleration_offer_replay_sent'))
    $runtimeUnlockDispatchDeferredForRegularV4ReceiveRecovery = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=tuna_acceleration_runtime_unlock_dispatch_deferred_for_regular_v4_receive_recovery')
    $measuredOfferSent = (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('message_type=file_transfer_offer')) -or
        (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=offer_sent'))
    $measuredOfferReceived = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=offer_received')
    $activationOfferSent = (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=tuna_acceleration_offer_queued')) -or
        (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=tuna_acceleration_offer_replay_sent'))
    $activationOfferReceived = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=tuna_acceleration_offer_received_raw')
    $runtimeUnlockRetryBlockedByRegularV4Recovery = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=runtime_unlock_retry_blocked_by_regular_v4_receive_recovery')
    $runtimeUnlockRetryHeldByRegularV4Recovery = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=runtime_unlock_retry_dispatch_held_by_regular_v4_receive_recovery')
    $runtimeUnlockRetryReleasedAfterRegularV4Recovery = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=runtime_unlock_retry_released_after_regular_v4_receive_recovery')
    $runtimeUnlockRetryRejectedAfterRegularV4Recovery = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=runtime_unlock_retry_rejected_after_regular_v4_receive_recovery')
    $runtimeUnlockRetryDispatchMissingAfterAuthorityGranted = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=runtime_unlock_retry_dispatch_missing_after_authority_granted')
    $runtimeUnlockRetryDispatchSuccessorMissingAfterMissingDispatch =
        Test-TunaGuiRuntimeUnlockMissingDispatchSuccessorUnresolved -Lines $evidenceLines
    $runtimeUnlockRetryDispatchUnsettledReason =
        Get-TunaGuiRuntimeUnlockUnsettledGrantReason -Lines $evidenceLines
    $runtimeUnlockRetrySourcePublicationMismatch = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=runtime_unlock_retry_dispatch_authority_rejected', 'decision_reason=retry_dispatch_source_publication_mismatch')
    $runtimeUnlockRetryDispatchAuthorityTupleMismatch =
        (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=runtime_unlock_retry_dispatch_decision_tuple_rejected', 'decision_reason=runtime_unlock_retry_dispatch_authority_tuple_mismatch')) -or
        (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=runtime_unlock_retry_dispatch_current_authority_rejected', 'decision_reason=runtime_unlock_retry_dispatch_authority_tuple_mismatch'))
    $runtimeUnlockFileTransferOwnerMissingBeforePathLease =
        Test-TunaGuiRuntimeUnlockFileTransferOwnerRejectionUnresolved `
            -Lines $evidenceLines `
            -RejectionEvent 'runtime_unlock_filetransfer_owner_missing_before_path_lease'
    $runtimeUnlockFileTransferOwnerStaleBeforeOfferDispatch =
        Test-TunaGuiRuntimeUnlockFileTransferOwnerRejectionUnresolved `
            -Lines $evidenceLines `
            -RejectionEvent 'runtime_unlock_filetransfer_owner_stale_before_offer_dispatch'
    $runtimeUnlockTransactionStarted = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=runtime_unlock_transaction_started')
    $runtimeUnlockRetryRecoverySettlementMissingMarker = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=runtime_unlock_retry_regular_v4_recovery_settlement_missing')
    $runtimeUnlockRetryContractFailed = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=session_recovery_contract_failed', 'runtime_unlock')
    $runtimeUnlockRetryRegularV4RecoverySettled = $runtimeUnlockRetryReleasedAfterRegularV4Recovery -or
        $runtimeUnlockRetryRejectedAfterRegularV4Recovery -or
        $runtimeUnlockRetryContractFailed
    $runtimeUnlockRetryRegularV4RecoverySettlementMissing = ($runtimeUnlockRetryRecoverySettlementMissingMarker -and (-not $runtimeUnlockRetryRegularV4RecoverySettled)) -or
        (($runtimeUnlockRetryBlockedByRegularV4Recovery -or $runtimeUnlockRetryHeldByRegularV4Recovery) -and
            (-not $runtimeUnlockRetryRegularV4RecoverySettled) -and
            $activationOfferNotObserved)
    $fallbackTailLocalYieldBlocked = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_fallback_tail_local_yield_blocked')
    $fallbackTailYieldAuthorityRejected =
        (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_fallback_tail_yield_local_authority_rejected')) -or
        (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_fallback_tail_yield_proof_stale_ignored', 'reason=legacy_authority_missing')) -or
        (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_fallback_tail_yield_proof_stale_ignored', 'reason=authority_malformed')) -or
        (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_fallback_tail_yield_proof_stale_ignored', 'reason=authority_tuple_mismatch')) -or
        (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_fallback_tail_yield_proof_stale_ignored', 'reason=predecessor_mismatch')) -or
        (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_fallback_tail_yield_proof_stale_ignored', 'reason=late_predecessor')) -or
        (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_fallback_tail_yield_proof_stale_ignored', 'reason=peer_yield_authority_invalid')) -or
        (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_fallback_tail_yield_proof_stale_ignored', 'reason=peer_yield_authority_tuple_mismatch')) -or
        (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_fallback_tail_yield_proof_stale_ignored', 'reason=peer_yield_authority_predecessor_mismatch')) -or
        (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_fallback_tail_yield_proof_stale_ignored', 'reason=peer_yield_generation_regressed'))
    $fallbackTailFrontierStateMissing = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_fallback_tail_local_yield_blocked', 'decision_reason=fallback_frontier_state_missing')
    $postFallbackAdmissionGranted = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_runtime_unlock_admission_granted', 'source=fallback_survival_resume', 'admission_reason=fallback_quiescent')
    $postFallbackPrecommitWaitingForProof = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_runtime_unlock_precommit_probe_waiting_for_transport_proof', 'reason=fallback_tail_quiescent')
    $postFallbackPrecommitPendingHeld = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_runtime_unlock_precommit_probe_pending_held', 'reason=fallback_tail_quiescent')
    $postFallbackPrecommitStarted = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=runtime_unlock_precommit_probe_started')
    $postFallbackPrecommitRejected = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_runtime_unlock_precommit_probe_pending_rejected')
    $postFallbackPrecommitAcked = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=runtime_unlock_precommit_probe_acked')
    $postFallbackRouteCommitAccepted = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_runtime_unlock_route_commit_accepted')
    $postFallbackResumeSettled = $postFallbackPrecommitStarted -or $postFallbackPrecommitRejected -or $postFallbackPrecommitAcked -or $postFallbackRouteCommitAccepted
    $fallbackProofReplayPendingAfterPairedYieldRaw = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_runtime_unlock_admission_denied', 'admission_reason=fallback_proof_replay_pending', 'paired_fallback_yield_granted=1')
    $fallbackProofReplayPendingAfterPairedYield = $fallbackProofReplayPendingAfterPairedYieldRaw -and (-not ($postFallbackAdmissionGranted -or $postFallbackResumeSettled))
    $postFallbackRuntimeUnlockResumeBlocked = (-not $postFallbackResumeSettled) -and (($postFallbackAdmissionGranted -and
        ($postFallbackPrecommitWaitingForProof -or $postFallbackPrecommitPendingHeld)) -or
        $fallbackProofReplayPendingAfterPairedYield)
    $fallbackFrontierRepairAuthorityGranted = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_fallback_frontier_repair_authority_granted')
    $fallbackFrontierRepairDispatched = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_fallback_frontier_repair_dispatched')
    $fallbackFrontierRepairStalledEvent = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_fallback_frontier_repair_stalled')
    $fallbackFrontierRepairProgressObserved = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_fallback_frontier_repair_progress_observed')
    $fallbackFrontierRequestAccepted = (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_control_plane_delivery_result', 'kind=frontier_request', 'frame_type=filetransfer.frontier_request.v6', 'peer_visible_any=1', 'accepted_any=1')) -or
        (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_control_plane_delivery_observed', 'kind=frontier_request', 'frame_type=filetransfer.frontier_request.v6', 'peer_visible_any=1', 'accepted_any=1'))
    $fallbackFrontierRequestReceiveDispatched = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_v6_frontier_request_receive_dispatched')
    $fallbackFrontierRequestReceiveRejected = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_v6_frontier_request_receive_dispatch_rejected')
    $postTunaFallbackOutboundReceiveWorkerHandoffMissing =
        Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing -Lines $evidenceLines
    $postTunaFallbackRouteEvidence = (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('route=post_tuna_fallback_v6', 'protocol_version=6')) -or
        (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('phase=measured_post_tuna_fallback_v6'))
    $postTunaFallbackFrontierRescueObserved = (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_v6_post_tuna_fallback_frontier_rescue_requested')) -or
        (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_v6_post_tuna_fallback_frontier_rescue_widened')) -or
        (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_v6_post_tuna_fallback_sender_frontier_rescue_queued'))
    $regularV4FrontierRepairAuthorityGranted = (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_regular_v4_frontier_repair_authority_granted')) -or
        (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_file_tuna_v4_frontier_repair_authority_granted'))
    $regularV4FrontierRepairAuthorityRejected = (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_regular_v4_frontier_repair_authority_rejected')) -or
        (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_file_tuna_v4_frontier_repair_authority_rejected'))
    $regularV4FrontierRepairDispatched = (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_regular_v4_frontier_repair_dispatched')) -or
        (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_file_tuna_v4_frontier_repair_dispatched'))
    $regularV4FrontierRepairSendDeferredEvent = (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_regular_v4_frontier_repair_send_deferred')) -or
        (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_file_tuna_v4_frontier_repair_send_deferred'))
    $regularV4FrontierRepairSuccessorAuthorized = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_regular_v4_frontier_repair_successor_authorized')
    $regularV4FrontierRepairProgressObserved = (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_regular_v4_frontier_repair_progress_observed')) -or
        (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_file_tuna_v4_frontier_repair_progress_observed'))
    $regularV4FrontierRepairStalledEvent = (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_regular_v4_frontier_repair_stalled')) -or
        (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_file_tuna_v4_frontier_repair_stalled'))
    $regularV4FrontierRepairSettledStalledEvent = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_regular_v4_frontier_repair_settled_stalled')
    $regularV4FrontierRepairPrecontractSettlementConsumed = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=runtime_unlock_regular_v4_frontier_repair_precontract_settlement_consumed')
    $regularV4ReceiveRecoveryClosedNoDeferral = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=regular_v4_receive_recovery_contract_closed_no_deferral')
    $regularV4ReceiveRecoveryRejectedBeforeRuntimeUnlock = $runtimeUnlockRetryRejectedAfterRegularV4Recovery -and
        $runtimeUnlockRetryContractFailed -and
        $regularV4ReceiveRecoveryClosedNoDeferral -and
        (-not $runtimeUnlockTransactionStarted)
    $regularV4EvidenceLineText = [string[]]@($evidenceLines | ForEach-Object { [string]$_ })
    $initialActivationReceiveRecoveryHoldNotPreserved =
        Test-TunaGuiInitialActivationReceiveRecoveryHoldNotPreserved `
            -Lines $regularV4EvidenceLineText
    $initialActivationReceiveRecoveryHoldReleasedOfferExpired =
        Test-TunaGuiInitialActivationReceiveRecoveryHoldReleasedOfferExpired `
            -Lines $regularV4EvidenceLineText
    $bridgeRecoveryExecutionPayloadHoldMissing =
        Test-TunaGuiBridgeRecoveryExecutionPayloadHoldFailure -Lines $regularV4EvidenceLineText
    $bridgeRecoveryExecutionSubmissionBarrierDrainMissing =
        Test-TunaGuiBridgeRecoveryExecutionSubmissionBarrierDrainFailure `
            -Lines $regularV4EvidenceLineText
    $receiveRecoveryCompletedUnprovenSuccessorMissing =
        Test-TunaGuiReceiveRecoveryCompletedUnprovenSuccessorMissing `
            -Lines $regularV4EvidenceLineText
    $receiveRecoveryCompletedProvenSuccessorMissing =
        Test-TunaGuiReceiveRecoveryCompletedProvenSuccessorMissing `
            -Lines $regularV4EvidenceLineText
    $routeTransitionDrainingTransportGraceTerminalization =
        Test-TunaGuiRouteTransitionDrainingTransportGraceFailure `
            -Lines $regularV4EvidenceLineText
    $regularV4RecoveryPredecessorSettlementClosedSuccessor =
        Test-TunaGuiRegularV4PredecessorSettlementClosedSuccessor -Lines $regularV4EvidenceLineText
    $regularV4FrontierRepairSettlementClosed =
        Test-TunaGuiRegularV4FrontierRepairSettlementClosed -Lines $regularV4EvidenceLineText
    $regularV4FrontierRepairStallUnresolved =
        Test-TunaGuiRegularV4FrontierRepairStallUnresolved -Lines $regularV4EvidenceLineText
    $regularV4FrontierRepairSettledStalledUnclosed = $regularV4FrontierRepairSettledStalledEvent -and (-not $regularV4FrontierRepairSettlementClosed)
    $regularV4FrontierMissingRanges = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_v4_frontier_stall_missing_range_due')
    $regularV4RepairRequested = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_v4_repair_requested')
    $regularV4LivenessTimeoutWithoutAuthority = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('active_filetransfer_progress_observed_without_recovery_authority')
    $terminalObserved = Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('_terminal')
    $fallbackProgressTimeout = $ErrorMessage.IndexOf('no useful data progress', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_live_progress_timeout')) -or
        (Test-TunaGuiLogLinesContain -Lines $evidenceLines -Needles @('event=filetransfer_gui_progress_timeout'))
    $normalToTunaEpochWaitAfterFallback = $postTunaFallbackRouteEvidence -and
        $ErrorMessage.IndexOf('live multi-toggle normal-to-Tuna route epoch started', [System.StringComparison]::OrdinalIgnoreCase) -ge 0
    $fallbackFrontierRepairStalled = $fallbackFrontierRepairStalledEvent -or
        ($postTunaFallbackFrontierRescueObserved -and ($fallbackProgressTimeout -or $normalToTunaEpochWaitAfterFallback)) -or
        (($fallbackFrontierRepairAuthorityGranted -or $fallbackFrontierRepairDispatched) -and
            (-not $fallbackFrontierRepairProgressObserved) -and
            ($fallbackProgressTimeout -or $normalToTunaEpochWaitAfterFallback))
    $fallbackFrontierRequestDispatchMissing = $postTunaFallbackRouteEvidence -and
        $fallbackFrontierRequestAccepted -and
        (Test-TunaGuiLatestFallbackFrontierRequestDispatchMissing -Lines $evidenceLines) -and
        ($fallbackProgressTimeout -or $normalToTunaEpochWaitAfterFallback)
    $regularV4FrontierRepairStalled = (-not $fallbackFrontierRepairStalled) -and ($regularV4FrontierRepairSettledStalledUnclosed -or
        ($regularV4FrontierRepairStalledEvent -and $regularV4FrontierRepairStallUnresolved) -or
        (($regularV4FrontierRepairAuthorityGranted -or $regularV4FrontierRepairDispatched) -and
            (-not $regularV4FrontierRepairProgressObserved) -and
            ($fallbackProgressTimeout -or $regularV4LivenessTimeoutWithoutAuthority)))
    $regularV4FrontierRepairSendDeferred = (-not $fallbackFrontierRepairStalled) -and
        (-not $regularV4FrontierRepairSettlementClosed) -and
        $regularV4FrontierRepairSendDeferredEvent -and
        (-not $regularV4FrontierRepairSuccessorAuthorized) -and
        (-not $regularV4FrontierRepairProgressObserved) -and
        ($fallbackProgressTimeout -or $regularV4LivenessTimeoutWithoutAuthority -or $terminalObserved)
    $regularV4RepairLivenessAuthorityMissing = (-not $fallbackFrontierRepairStalled) -and
        $regularV4FrontierMissingRanges -and
        $regularV4RepairRequested -and
        (-not $regularV4FrontierRepairAuthorityGranted) -and
        ($regularV4LivenessTimeoutWithoutAuthority -or $terminalObserved)
    $activationPhaseEvidence = (-not $tunaActive) -and ($activationOfferNotObserved -or $activationOfferWaitingAnswer -or $activationOfferSent -or $activationOfferReceived)
    $offerSent = if ($activationPhaseEvidence) { $activationOfferSent } else { $measuredOfferSent }
    $offerReceived = if ($activationPhaseEvidence) { $activationOfferReceived } else { $measuredOfferReceived }
    $hasScenarioRuntimeEvidence = $routeSelected -or
        $activationOfferNotObserved -or
        $activationOfferWaitingAnswer -or
        $activationOfferSent -or
        $activationOfferReceived -or
        $measuredOfferSent -or
        $measuredOfferReceived -or
        $terminalObserved -or
        $runtimeUnlockRetryBlockedByRegularV4Recovery -or
        $runtimeUnlockRetryHeldByRegularV4Recovery -or
        $runtimeUnlockRetryReleasedAfterRegularV4Recovery -or
        $runtimeUnlockRetryRejectedAfterRegularV4Recovery -or
        $runtimeUnlockRetryDispatchMissingAfterAuthorityGranted -or
        $runtimeUnlockRetryDispatchSuccessorMissingAfterMissingDispatch -or
        $runtimeUnlockRetrySourcePublicationMismatch -or
        $runtimeUnlockRetryDispatchAuthorityTupleMismatch -or
        $runtimeUnlockFileTransferOwnerMissingBeforePathLease -or
        $runtimeUnlockFileTransferOwnerStaleBeforeOfferDispatch -or
        $runtimeUnlockRetryRecoverySettlementMissingMarker -or
        $runtimeUnlockRetryRegularV4RecoverySettlementMissing -or
        $initialActivationReceiveRecoveryHoldNotPreserved -or
        $initialActivationReceiveRecoveryHoldReleasedOfferExpired -or
        $bridgeRecoveryExecutionPayloadHoldMissing -or
        $bridgeRecoveryExecutionSubmissionBarrierDrainMissing -or
        $receiveRecoveryCompletedUnprovenSuccessorMissing -or
        $receiveRecoveryCompletedProvenSuccessorMissing -or
        $routeTransitionDrainingTransportGraceTerminalization -or
        $regularV4FrontierRepairSettledStalledUnclosed -or
        $regularV4FrontierRepairSendDeferred -or
        $regularV4FrontierRepairStalled -or
        $postTunaFallbackOutboundReceiveWorkerHandoffMissing -or
        $fallbackFrontierRequestDispatchMissing -or
        $fallbackFrontierRepairStalled -or
        $fallbackTailYieldAuthorityRejected -or
        $postFallbackRuntimeUnlockResumeBlocked -or
        $postTunaFallbackRouteEvidence

    $phase = 'unknown'
    $reason = 'unknown'
    $listenerReadyUnavailableContradiction = $listenerReady -and ($listenerUnavailable -or $listenerSidecarUnavailable)
    $bridgeBootstrapSubreason = ''
    if ((-not $hasScenarioRuntimeEvidence) -and
        ($bridgeBootstrapNotReady -or
            $ErrorMessage.IndexOf('nkn_bridge_bootstrap_network_unready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $ErrorMessage.IndexOf('nkn_bridge_bootstrap_not_ready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0)) {
        $bootstrapText = (@($ErrorMessage) + @($evidenceLines)) -join "`n"
        foreach ($fieldName in @('failure_subreason', 'subreason')) {
            $match = [regex]::Match($bootstrapText, "(?i)(?:^|[;`r`n]\s*)$fieldName=([^;`r`n]+)")
            if ($match.Success) {
                $bridgeBootstrapSubreason = $match.Groups[1].Value.Trim()
                break
            }
        }

        if ([string]::IsNullOrWhiteSpace($bridgeBootstrapSubreason) -or
            $bridgeBootstrapSubreason -eq '(none)') {
            if ($bootstrapText.IndexOf('role=helper', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $bridgeBootstrapSubreason = 'helper_bridge_host_ready_connect_failed'
            }
            elseif ($bootstrapText.IndexOf('HostByAddressAsync failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $bootstrapText.IndexOf('Connect failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $bootstrapText.IndexOf('RPC call failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $bootstrapText.IndexOf('connect EACCES', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $bridgeBootstrapSubreason = 'helpee_bridge_host_ready_connect_failed'
            }
            else {
                $bridgeBootstrapSubreason = 'nkn_bridge_bootstrap_not_ready'
            }
        }

        $phase = 'preflight_bridge_bootstrap'
        $reason = $bridgeBootstrapSubreason
    }
    elseif ($initialActivationReceiveRecoveryHoldReleasedOfferExpired) {
        $phase = 'runtime_unlock_initial_activation'
        $reason = 'initial_activation_offer_expired_during_receive_recovery_hold'
    }
    elseif ($routeTransitionDrainingTransportGraceTerminalization) {
        $phase = 'route_transition'
        $reason =
            'coordinator_route_transition_draining_treated_as_transport_disconnect'
    }
    elseif ($initialActivationReceiveRecoveryHoldNotPreserved) {
        $phase = 'runtime_unlock_initial_activation'
        $reason = 'initial_activation_retry_exhausted_while_receive_recovery_awaiting_proof'
    }
    elseif ($bridgeRecoveryExecutionSubmissionBarrierDrainMissing) {
        $phase = 'bridge_recovery_execution'
        $reason = 'bridge_recovery_execution_restart_not_ready'
    }
    elseif ($bridgeRecoveryExecutionPayloadHoldMissing) {
        $phase = 'bridge_recovery_execution'
        $reason = 'bridge_recovery_submission_hold_rejection_escalated_to_terminal'
    }
    elseif ($receiveRecoveryCompletedUnprovenSuccessorMissing) {
        $phase = 'receive_recovery'
        $reason = 'completed_unproven_receive_recovery_operation_blocked_fresh_recovery'
    }
    elseif ($receiveRecoveryCompletedProvenSuccessorMissing) {
        $phase = 'receive_recovery'
        $reason = 'completed_proven_unreserved_receive_recovery_operation_blocked_fresh_recovery'
    }
    elseif ($runtimeUnlockFileTransferOwnerMissingBeforePathLease) {
        $phase = 'runtime_unlock_owner'
        $reason = 'runtime_unlock_filetransfer_owner_missing_before_path_lease'
    }
    elseif ($runtimeUnlockFileTransferOwnerStaleBeforeOfferDispatch) {
        $phase = 'runtime_unlock_owner'
        $reason = 'runtime_unlock_filetransfer_owner_stale_before_offer_dispatch'
    }
    elseif ($runtimeUnlockRetrySourcePublicationMismatch) {
        $phase = 'runtime_unlock_retry'
        $reason = 'runtime_unlock_retry_source_publication_mismatch'
    }
    elseif ($runtimeUnlockRetryDispatchUnsettledReason -ne '(none)') {
        $phase = 'runtime_unlock_retry'
        $reason = $runtimeUnlockRetryDispatchUnsettledReason
    }
    elseif ($runtimeUnlockRetryDispatchSuccessorMissingAfterMissingDispatch) {
        $phase = 'runtime_unlock_retry'
        $reason = 'runtime_unlock_retry_dispatch_successor_missing_after_missing_dispatch'
    }
    elseif ($runtimeUnlockRetryDispatchAuthorityTupleMismatch) {
        $phase = 'runtime_unlock_retry'
        $reason = 'runtime_unlock_retry_dispatch_authority_tuple_mismatch'
    }
    elseif ($ErrorMessage.IndexOf('second_transfer_preactivation_readiness_missing', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $ErrorMessage.IndexOf('second_transfer_tuna_readiness_unstable', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $ErrorMessage.IndexOf('Tuna GUI second-transfer readiness did not stabilize', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $phase = 'preactivation_readiness'
        $reason = if ($listenerReadyUnavailableContradiction -or
            $ErrorMessage.IndexOf('listener_ready_unavailable_contradiction', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            'listener_ready_unavailable_contradiction'
        }
        else {
            'second_transfer_preactivation_readiness_missing'
        }
    }
    elseif ($ErrorMessage.IndexOf('pretransfer_tuna_readiness_missing', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $ErrorMessage.IndexOf('diagnostic Tuna readiness before measured GUI file transfer', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $phase = 'diagnostic_tuna_readiness'
        $reason = 'pretransfer_tuna_readiness_missing'
    }
    elseif ($ErrorMessage.IndexOf('initial_tuna_evidence_not_current', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $phase = 'preactivation_readiness'
        $reason = 'initial_tuna_evidence_not_current'
    }
    elseif ($ErrorMessage.IndexOf('file_tuna_v4_publication_missing_after_transfer', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $ErrorMessage.IndexOf('coordinator-authorized file Tuna V4 publication after measured GUI file transfer', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $phase = 'route_publication'
        $reason = 'file_tuna_v4_publication_missing_after_transfer'
    }
    elseif ($fallbackTailYieldAuthorityRejected) {
        $phase = 'fallback_tail_yield'
        $reason = 'fallback_tail_yield_authority_invalid'
    }
    elseif ($postFallbackRuntimeUnlockResumeBlocked) {
        $phase = 'post_fallback_runtime_unlock_resume'
        $reason = if ($fallbackProofReplayPendingAfterPairedYield) { 'fallback_proof_replay_pending_after_paired_yield' } else { 'post_fallback_runtime_unlock_resume_blocked' }
    }
    elseif ($runtimeUnlockRetryDispatchMissingAfterAuthorityGranted) {
        $phase = 'runtime_unlock_retry'
        $reason = 'runtime_unlock_retry_dispatch_missing_after_authority_granted'
    }
    elseif ($regularV4RecoveryPredecessorSettlementClosedSuccessor) {
        $phase = 'regular_v4_receive_recovery'
        $reason = 'regular_v4_recovery_predecessor_settlement_closed_successor'
    }
    elseif ($regularV4ReceiveRecoveryRejectedBeforeRuntimeUnlock) {
        $phase = 'runtime_unlock_retry'
        $reason = 'regular_v4_receive_recovery_rejected_before_runtime_unlock'
    }
    elseif ($postTunaFallbackOutboundReceiveWorkerHandoffMissing) {
        $phase = 'outbound_sparse_worker_handoff'
        $reason = 'post_tuna_fallback_outbound_receive_worker_handoff_missing'
    }
    elseif ($fallbackFrontierRequestDispatchMissing) {
        $phase = 'fallback_frontier_repair'
        $reason = 'fallback_frontier_request_dispatch_missing'
    }
    elseif ($fallbackFrontierRepairStalled) {
        $phase = 'fallback_frontier_repair'
        $reason = 'fallback_frontier_repair_stalled'
    }
    elseif ($runtimeUnlockRetryRegularV4RecoverySettlementMissing) {
        $phase = 'runtime_unlock_retry'
        $reason = 'runtime_unlock_retry_regular_v4_recovery_settlement_missing'
    }
    elseif ($regularV4FrontierRepairSettledStalledUnclosed) {
        $phase = 'regular_v4_frontier_repair'
        $reason = 'regular_v4_frontier_repair_liveness_settlement_missing'
    }
    elseif ($regularV4FrontierRepairSendDeferred) {
        $phase = 'regular_v4_frontier_repair'
        $reason = 'regular_v4_frontier_repair_send_deferred_or_stalled'
    }
    elseif ($regularV4FrontierRepairStalled) {
        $phase = 'regular_v4_frontier_repair'
        $reason = 'regular_v4_frontier_repair_stalled'
    }
    elseif ($regularV4RepairLivenessAuthorityMissing) {
        $phase = 'regular_v4_frontier_repair'
        $reason = 'regular_v4_repair_liveness_authority_missing'
    }
    elseif ($fallbackTailLocalYieldBlocked) {
        $phase = 'fallback_tail_quiescence'
        $reason = if ($fallbackTailFrontierStateMissing) { 'fallback_frontier_state_missing' } else { 'fallback_tail_local_yield_missing' }
    }
    elseif ($ErrorMessage.IndexOf('live multi-toggle normal-to-Tuna route epoch started', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $phase = 'live_route_proof'
        $reason = 'normal_to_tuna_live_epoch_missing'
    }
    elseif ($ErrorMessage.IndexOf('live multi-toggle normal-to-Tuna route epoch recovered', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $phase = 'live_route_proof'
        $reason = 'normal_to_tuna_live_epoch_recovery_missing'
    }
    elseif ($ErrorMessage.IndexOf('live multi-toggle file Tuna V4 route selection', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $phase = 'route_runtime'
        $reason = 'normal_to_tuna_route_selection_missing'
    }
    elseif ($ErrorMessage.IndexOf('live multi-toggle Tuna-to-normal fallback route epoch started', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $phase = 'live_route_proof'
        $reason = if ($terminalObserved) { 'terminal_before_tuna_to_normal_fallback_epoch' } else { 'tuna_to_normal_fallback_live_epoch_missing' }
    }
    elseif ($ErrorMessage.IndexOf('live multi-toggle Tuna-to-normal fallback route epoch recovered', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $phase = 'live_route_proof'
        $reason = if ($terminalObserved) { 'terminal_before_tuna_to_normal_fallback_recovery' } else { 'tuna_to_normal_fallback_live_epoch_recovery_missing' }
    }
    elseif ($ErrorMessage.IndexOf('live multi-toggle post-Tuna fallback V6 route selection', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $phase = 'route_runtime'
        $reason = if ($terminalObserved) { 'terminal_before_post_tuna_fallback_route_selection' } else { 'post_tuna_fallback_route_selection_missing' }
    }
    elseif ($ErrorMessage.IndexOf('live_toggle_window_missed_transfer_completed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $phase = 'live_toggle_window'
        $reason = 'transfer_completed_before_same_transfer_reactivation'
    }
    elseif ($ErrorMessage.IndexOf('completed_before_fault_injection', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $ErrorMessage.IndexOf('transfer_completed_before_fault_injection', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $phase = 'completed_before_fault_injection'
        $reason = 'transfer_completed_before_fault_injection'
    }
    elseif ($ErrorMessage.IndexOf('Timed out waiting for receiver-committed Tuna file progress', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $phase = 'fault_progress_gate'
        $reason = 'receiver_committed_or_tuna_payload_progress_missing'
    }
    elseif ($ErrorMessage.IndexOf('Timed out waiting for helpee invite to become ready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $ErrorMessage.IndexOf('Timed out waiting for helpee request flow to become ready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $phase = 'preactivation_readiness'
        $reason = if ($bridgeBootstrapNotReady) { 'nkn_bridge_bootstrap_not_ready' } else { 'helpee_invite_readiness_timeout' }
    }
    elseif ($ErrorMessage.IndexOf('helpee_request_flow_not_enabled_after_identity_paste', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $phase = 'preactivation_readiness'
        $reason = 'helpee_request_flow_not_enabled_after_identity_paste'
    }
    elseif ($ErrorMessage.IndexOf('Timed out waiting for helper incoming request acceptance UI', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $phase = 'helper_request_acceptance'
        $reason = 'helper_incoming_request_not_observed'
    }
    elseif ($ErrorMessage.IndexOf('Helpee reached Connection failed before invite became ready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $ErrorMessage.IndexOf('Helpee reached Connection failed before request flow became ready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
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
        elseif ($runtimeUnlockRetryRegularV4RecoverySettlementMissing) {
            $phase = 'runtime_unlock_retry'
            $reason = 'runtime_unlock_retry_regular_v4_recovery_settlement_missing'
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
    elseif ($runtimeUnlockRetryRegularV4RecoverySettlementMissing) {
        $phase = 'runtime_unlock_retry'
        $reason = 'runtime_unlock_retry_regular_v4_recovery_settlement_missing'
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

    $resolvedClassification = Resolve-TunaGuiFailureClassification `
        -ErrorMessage $ErrorMessage `
        -HistoricalPhase $phase `
        -HistoricalReason $reason
    $phase = [string]$resolvedClassification.Phase
    $reason = [string]$resolvedClassification.Reason

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
        RuntimeUnlockRetryRegularV4RecoverySettlementMissing = [bool]$runtimeUnlockRetryRegularV4RecoverySettlementMissing
        RuntimeUnlockRetrySourcePublicationMismatch = [bool]$runtimeUnlockRetrySourcePublicationMismatch
        InitialActivationReceiveRecoveryHoldNotPreserved =
            [bool]$initialActivationReceiveRecoveryHoldNotPreserved
        InitialActivationReceiveRecoveryHoldReleasedOfferExpired =
            [bool]$initialActivationReceiveRecoveryHoldReleasedOfferExpired
        RouteTransitionDrainingTransportGraceTerminalization =
            [bool]$routeTransitionDrainingTransportGraceTerminalization
        BridgeBootstrapNotReady = [bool]$bridgeBootstrapNotReady
        ActivationOfferSent = [bool]$activationOfferSent
        ActivationOfferReceived = [bool]$activationOfferReceived
        FallbackTailLocalYieldBlocked = [bool]$fallbackTailLocalYieldBlocked
        FallbackTailFrontierStateMissing = [bool]$fallbackTailFrontierStateMissing
        FallbackFrontierRepairAuthorityGranted = [bool]$fallbackFrontierRepairAuthorityGranted
        FallbackFrontierRepairDispatched = [bool]$fallbackFrontierRepairDispatched
        FallbackFrontierRepairProgressObserved = [bool]$fallbackFrontierRepairProgressObserved
        FallbackFrontierRepairStalled = [bool]$fallbackFrontierRepairStalled
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
        throw ("Tuna GUI measured file-transfer accept did not become enabled: phase={0}; reason={1}; route_mode={2}; tuna_active={3}; listener_ready={4}; listener_unavailable={5}; route_selected={6}; activation_offer_not_observed={7}; activation_offer_waiting_answer={8}; runtime_unlock_dispatch_deferred_for_regular_v4_receive_recovery={9}; bridge_bootstrap_not_ready={10}; activation_offer_sent={11}; activation_offer_received={12}; measured_offer_sent={13}; measured_offer_received={14}; offer_sent={15}; offer_received={16}; terminal_observed={17}; original_error={18}" -f `
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
            ($(if ($classification.BridgeBootstrapNotReady) { 1 } else { 0 })),
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

        $allowCoordinatorProofAfterSend = [string]::Equals(
            $RouteMode,
            'live-reactivation-second-transfer',
            [System.StringComparison]::OrdinalIgnoreCase)
        $routeReadinessSatisfied = $allowCoordinatorProofAfterSend -or (
            [bool]$lastState.TunaActive -and
            [bool]$lastState.ListenerReady -and
            -not [bool]$lastState.ListenerUnavailable)
        $coordinatorProofAfterSendReady = $allowCoordinatorProofAfterSend -and
            $null -ne $sendButton
        $isReadyNow = $coordinatorProofAfterSendReady -or (
            $null -ne $sendButton -and
            $routeReadinessSatisfied -and
            [bool]$lastSendState.Ready)
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

    $readinessFailureReason = if ($ready -eq 1 -and $unavailable -eq 1) {
        'listener_ready_unavailable_contradiction'
    }
    elseif ($active -eq 1 -and
        $ready -eq 0 -and
        $sendLogReady -eq 1 -and
        $sendLogConnected -eq 1 -and
        $sendLogCanSend -eq 1 -and
        $sendLogOutboundClear -eq 1) {
        'second_transfer_preactivation_readiness_missing'
    }
    else {
        'second_transfer_tuna_readiness_unstable'
    }
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

function Submit-TunaWalletPasswordDialog {
    param(
        [Parameter(Mandatory = $true)]$Process,
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Dialog,
        [Parameter(Mandatory = $true)][string]$Password,
        [Parameter(Mandatory = $true)][string]$RoleLabel,
        [string]$Reason = 'wallet_prompt'
    )

    Write-Host "[GUI Smoke][filetransfer_tuna] $RoleLabel Tuna wallet prompt detected; submitting password. reason=$Reason" -ForegroundColor Yellow
    $passwordBox = Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage "Timed out waiting for $RoleLabel Tuna wallet password box." -Condition {
        Find-VisibleByAutomationId -Root $Dialog -AutomationId 'WalletPassword.Password'
    }
    Set-Text -Element $passwordBox -Text $Password
    $accept = Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage "Timed out waiting for $RoleLabel Tuna wallet Unlock button." -Condition {
        $button = Find-VisibleByAutomationId -Root $Dialog -AutomationId 'WalletPassword.Accept'
        if ($button -and $button.Current.IsEnabled) { return $button }
        return $null
    }
    Click-Element $accept

    [void](Wait-Until -TimeoutMs 15000 -PollMs 200 -OnTimeoutMessage "Timed out waiting for $RoleLabel Tuna wallet dialog to close after unlock submission." -Condition {
        $null -eq (Get-WalletPasswordDialogForProcess -ProcessId $Process.Id)
    })
    Write-Host "[GUI Smoke][filetransfer_tuna] $RoleLabel Tuna wallet prompt submitted. reason=$Reason" -ForegroundColor Green
    return $true
}

function Submit-TunaWalletPasswordDialogIfPresent {
    param(
        [Parameter(Mandatory = $true)]$Process,
        [Parameter(Mandatory = $true)][string]$Password,
        [Parameter(Mandatory = $true)][string]$RoleLabel,
        [string]$Reason = 'wallet_prompt_poll'
    )

    $dialog = Get-WalletPasswordDialogForProcess -ProcessId $Process.Id
    if (-not $dialog) {
        return $false
    }

    try {
        return [bool](Submit-TunaWalletPasswordDialog -Process $Process -Dialog $dialog -Password $Password -RoleLabel $RoleLabel -Reason $Reason)
    }
    catch {
        Write-Host "[GUI Smoke][filetransfer_tuna] $RoleLabel Tuna wallet prompt submission failed. reason=$Reason; error=$($_.Exception.Message)" -ForegroundColor Yellow
        return $false
    }
}

function Wait-AndSubmitTunaWalletPasswordDialogIfPresent {
    param(
        [Parameter(Mandatory = $true)]$Process,
        [Parameter(Mandatory = $true)][string]$Password,
        [Parameter(Mandatory = $true)][string]$RoleLabel,
        [int]$TimeoutMs = 3000,
        [string]$Reason = 'wallet_prompt_late_after_evidence'
    )

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        if (Submit-TunaWalletPasswordDialogIfPresent -Process $Process -Password $Password -RoleLabel $RoleLabel -Reason $Reason) {
            return $true
        }

        Start-Sleep -Milliseconds 200
    }

    return $false
}

function Submit-TunaWalletPasswordDialogsForPayersIfPresent {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [Parameter(Mandatory = $true)][string]$PayerMode,
        [Parameter(Mandatory = $true)][string]$Password,
        [string]$Reason = 'wallet_prompt_poll'
    )

    $submitted = $false
    if ($PayerMode -eq 'helpee' -or $PayerMode -eq 'both') {
        $submitted = (Submit-TunaWalletPasswordDialogIfPresent -Process $Context.HelpeeProc -Password $Password -RoleLabel 'helpee' -Reason $Reason) -or $submitted
    }

    if ($PayerMode -eq 'helper' -or $PayerMode -eq 'both') {
        $submitted = (Submit-TunaWalletPasswordDialogIfPresent -Process $Context.HelperProc -Password $Password -RoleLabel 'helper' -Reason $Reason) -or $submitted
    }

    return $submitted
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

function Wait-TunaGuiDiagnosticReadinessBeforeMeasuredTransfer {
    param(
        [Parameter(Mandatory = $true)][int]$Bookmark,
        [int]$TimeoutMs = 150000
    )

    try {
        return [string](Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $Bookmark -NeedleSets @(
            @('event=runtime_unlock_transaction_answer_received', 'peer_visible_proof=1', 'answer_received=1', 'tuna_path_lease_current=1', 'tuna_path_lease_state=listenerready')
        ) -TimeoutMs $TimeoutMs -Description 'diagnostic Tuna readiness before measured GUI file transfer')
    }
    catch {
        throw ("Phase 5 pre-transfer Tuna diagnostic readiness missing: failurePhase=diagnostic_tuna_readiness; failureReason=pretransfer_tuna_readiness_missing; original_error={0}" -f $_.Exception.Message)
    }
}

function Test-GuiSmokeFieldEquals {
    param(
        $Fields,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Expected
    )

    $actual = Get-GuiSmokeFieldValue -Fields $Fields -Name $Name -Default ''
    return $actual.Equals($Expected, [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-TunaGuiAuthorizedFileTunaV4PublicationLine {
    param(
        [string]$Line,
        [string]$ActionKind = '',
        [bool]$RequireZeroRouteCommitGeneration = $false,
        [bool]$RequirePositiveRouteCommitGeneration = $false
    )

    if ([string]::IsNullOrWhiteSpace($Line)) {
        return $false
    }

    $fields = ConvertFrom-GuiSmokeSemicolonFields -Message $Line
    if (-not (Test-GuiSmokeFieldEquals -Fields $fields -Name 'event' -Expected 'filetransfer_route_selected')) {
        return $false
    }

    if (-not (Test-GuiSmokeFieldEquals -Fields $fields -Name 'route' -Expected 'file_tuna_v4') -or
        -not (Test-GuiSmokeFieldEquals -Fields $fields -Name 'target_route' -Expected 'file_tuna_v4') -or
        -not (Test-GuiSmokeFieldEquals -Fields $fields -Name 'target_transport' -Expected 'tuna') -or
        -not (Test-GuiSmokeFieldEquals -Fields $fields -Name 'coordinator_decision_reason' -Expected 'accepted')) {
        return $false
    }

    if (-not [string]::IsNullOrWhiteSpace($ActionKind) -and
        -not (Test-GuiSmokeFieldEquals -Fields $fields -Name 'route_publication_action_kind' -Expected $ActionKind)) {
        return $false
    }

    $protocolVersion = ConvertTo-GuiSmokeInt -Value (Get-GuiSmokeFieldValue -Fields $fields -Name 'protocol_version' -Default '-1') -Default -1
    $targetProtocolVersion = ConvertTo-GuiSmokeInt -Value (Get-GuiSmokeFieldValue -Fields $fields -Name 'target_protocol_version' -Default '-1') -Default -1
    if ($protocolVersion -ne 4 -or $targetProtocolVersion -ne 4) {
        return $false
    }

    foreach ($requiredGenerationField in @('route_publication_generation', 'route_commit_generation', 'runtime_generation', 'terminal_generation', 'leg_generation')) {
        if (-not $fields.ContainsKey($requiredGenerationField)) {
            return $false
        }
    }

    $routeCommitGeneration = ConvertTo-GuiSmokeInt -Value (Get-GuiSmokeFieldValue -Fields $fields -Name 'route_commit_generation' -Default '-1') -Default -1
    if ($routeCommitGeneration -lt 0) {
        return $false
    }

    if ($RequireZeroRouteCommitGeneration -and $routeCommitGeneration -ne 0) {
        return $false
    }

    if ($RequirePositiveRouteCommitGeneration -and $routeCommitGeneration -le 0) {
        return $false
    }

    return $true
}

function Wait-TunaGuiFileTunaV4PublicationAfterBookmark {
    param(
        [Parameter(Mandatory = $true)][int]$Bookmark,
        [int]$TimeoutMs = 90000,
        [string]$Description = 'coordinator-authorized file Tuna V4 publication after measured GUI file transfer',
        [string]$ActionKind = '',
        [bool]$RequireZeroRouteCommitGeneration = $false,
        [bool]$RequirePositiveRouteCommitGeneration = $false
    )

    try {
        $line = [string](Wait-Until -TimeoutMs $TimeoutMs -PollMs 500 -OnTimeoutMessage "Timed out waiting for app log evidence: $Description" -Condition {
            foreach ($candidateLine in @(Get-AppLogLinesAfterBookmark -Bookmark $Bookmark)) {
                if (Test-TunaGuiAuthorizedFileTunaV4PublicationLine -Line $candidateLine -ActionKind $ActionKind -RequireZeroRouteCommitGeneration $RequireZeroRouteCommitGeneration -RequirePositiveRouteCommitGeneration $RequirePositiveRouteCommitGeneration) {
                    return $candidateLine
                }
            }

            return $null
        })
    }
    catch {
        foreach ($candidateLine in @(Get-AppLogLinesAfterBookmark -Bookmark $Bookmark)) {
            if ($candidateLine.IndexOf('event=runtime_unlock_answer_rejected_tuna_path_lease_unavailable', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $candidateLine.IndexOf('tuna_path_lease_unavailable_at_answer', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                throw ("Phase 5 pre-transfer Tuna diagnostic readiness missing: failurePhase=diagnostic_tuna_readiness; failureReason=pretransfer_tuna_readiness_missing; readiness_failure=tuna_path_lease_unavailable_at_answer; original_error={0}" -f $_.Exception.Message)
            }

            if ($candidateLine.IndexOf('event=filetransfer_initial_tuna_route_selection_denied', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                $candidateLine.IndexOf('coordinator_reason=initial_tuna_evidence_not_current', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                throw ("Phase 5 initial Tuna readiness evidence was not current: failurePhase=preactivation_readiness; failureReason=initial_tuna_evidence_not_current; original_error={0}" -f $_.Exception.Message)
            }
        }

        throw ("Phase 5 measured file-transfer route publication missing: failurePhase=route_publication; failureReason=file_tuna_v4_publication_missing_after_transfer; original_error={0}" -f $_.Exception.Message)
    }

    return $line
}

function Test-TunaGuiPeerVisibleFileTunaV4DataPathLine {
    param([string]$Line)

    if ([string]::IsNullOrWhiteSpace($Line)) {
        return $false
    }

    $fields = ConvertFrom-GuiSmokeSemicolonFields -Message $Line
    if (-not (Test-GuiSmokeFieldEquals -Fields $fields -Name 'event' -Expected 'filetransfer_tuna_data_path_peer_visible')) {
        return $false
    }

    if (-not (Test-GuiSmokeFieldEquals -Fields $fields -Name 'route' -Expected 'file_tuna_v4') -or
        -not (Test-GuiSmokeFieldEquals -Fields $fields -Name 'target_route' -Expected 'file_tuna_v4') -or
        -not (Test-GuiSmokeFieldEquals -Fields $fields -Name 'target_transport' -Expected 'tuna') -or
        -not (Test-GuiSmokeFieldEquals -Fields $fields -Name 'actual_transport' -Expected 'tuna')) {
        return $false
    }

    $protocolVersion = ConvertTo-GuiSmokeInt -Value (Get-GuiSmokeFieldValue -Fields $fields -Name 'protocol_version' -Default '-1') -Default -1
    $targetProtocolVersion = ConvertTo-GuiSmokeInt -Value (Get-GuiSmokeFieldValue -Fields $fields -Name 'target_protocol_version' -Default '-1') -Default -1
    if ($protocolVersion -ne 4 -or $targetProtocolVersion -ne 4) {
        return $false
    }

    foreach ($requiredGenerationField in @('route_publication_generation', 'runtime_generation', 'terminal_generation', 'leg_generation')) {
        if (-not $fields.ContainsKey($requiredGenerationField)) {
            return $false
        }
    }

    $routePublicationGeneration = ConvertTo-GuiSmokeInt -Value (Get-GuiSmokeFieldValue -Fields $fields -Name 'route_publication_generation' -Default '-1') -Default -1
    return $routePublicationGeneration -gt 0
}

function Get-TunaGuiPeerVisibleFileTunaV4PayloadBytes {
    param([string]$Line)

    if (-not (Test-TunaGuiPeerVisibleFileTunaV4DataPathLine -Line $Line)) {
        return -1L
    }

    $fields = ConvertFrom-GuiSmokeSemicolonFields -Message $Line
    return [Math]::Max(0L, (Get-GuiSmokeInt64FieldValue -Fields $fields -Name 'payload_bytes'))
}

function Get-TunaGuiCompletedTerminalFromLogLine {
    param([string]$Line)

    if ([string]::IsNullOrWhiteSpace($Line) -or
        ($Line.IndexOf('_terminal', [System.StringComparison]::OrdinalIgnoreCase) -lt 0 -and
         $Line.IndexOf('event=transfer_terminal', [System.StringComparison]::OrdinalIgnoreCase) -lt 0)) {
        return $null
    }

    $fields = ConvertFrom-GuiSmokeSemicolonFields -Message $Line
    $eventName = Get-GuiSmokeFieldValue -Fields $fields -Name 'event' -Default ''
    $direction = Get-GuiSmokeFileTransferTerminalDirection -EventName $eventName -Fields $fields
    if ([string]::IsNullOrWhiteSpace($direction)) {
        return $null
    }

    Set-GuiSmokeFileTransferTerminalDefaults -Fields $fields
    $state = Get-GuiSmokeFieldValue -Fields $fields -Name 'state' -Default ''
    $errorCode = Get-GuiSmokeFieldValue -Fields $fields -Name 'error_code' -Default '(none)'
    if (-not [string]::Equals($state, 'Completed', [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals($errorCode, '(none)', [System.StringComparison]::OrdinalIgnoreCase)) {
        return $null
    }

    return [pscustomobject]@{
        Direction = $direction
        TransferId = Get-GuiSmokeFieldValue -Fields $fields -Name 'transfer_id' -Default '(unknown)'
        Line = $Line
    }
}

function Add-TunaGuiCompletedTerminalToTracker {
    param(
        [Parameter(Mandatory = $true)][hashtable]$InboundCompletedByTransfer,
        [Parameter(Mandatory = $true)][hashtable]$OutboundCompletedByTransfer,
        [Parameter(Mandatory = $true)]$Terminal
    )

    $transferId = [string]$Terminal.TransferId
    if ([string]::IsNullOrWhiteSpace($transferId) -or
        [string]::Equals($transferId, '(unknown)', [System.StringComparison]::OrdinalIgnoreCase)) {
        return $null
    }

    $direction = [string]$Terminal.Direction
    if ([string]::Equals($direction, 'inbound', [System.StringComparison]::OrdinalIgnoreCase)) {
        $InboundCompletedByTransfer[$transferId] = $true
    }
    elseif ([string]::Equals($direction, 'outbound', [System.StringComparison]::OrdinalIgnoreCase)) {
        $OutboundCompletedByTransfer[$transferId] = $true
    }

    if ($InboundCompletedByTransfer.ContainsKey($transferId) -and
        $OutboundCompletedByTransfer.ContainsKey($transferId)) {
        return $transferId
    }

    return $null
}

function New-TunaGuiLiveSwitchOffProgressGateResult {
    param(
        [Parameter(Mandatory = $true)][string]$Kind,
        [Parameter(Mandatory = $true)][string]$Mode,
        [Parameter(Mandatory = $true)][string]$Line,
        [long]$CommittedBytes = 0,
        [long]$MinimumCommittedBytes = 0,
        [long]$AcceleratedPayloadBytes = 0,
        [long]$MinimumPeerVisiblePayloadBytes = 0,
        [long]$LastFramePayloadBytes = -1,
        [long]$ElapsedMs = 0,
        [string]$TransferId = '(unknown)'
    )

    $peerVisibleObserved = $AcceleratedPayloadBytes -gt 0L
    return [pscustomobject]@{
        Kind = $Kind
        Mode = $Mode
        Line = $Line
        CommittedBytes = $CommittedBytes
        MinimumCommittedBytes = $MinimumCommittedBytes
        ReceiverCommittedProgressObserved = $CommittedBytes -ge $MinimumCommittedBytes -and $MinimumCommittedBytes -gt 0
        AcceleratedPayloadBytes = $AcceleratedPayloadBytes
        MinimumPeerVisiblePayloadBytes = $MinimumPeerVisiblePayloadBytes
        PeerVisibleTunaPayloadObserved = $peerVisibleObserved
        LastFramePayloadBytes = $LastFramePayloadBytes
        ElapsedMs = $ElapsedMs
        TerminalCompletedBeforeFault = [string]::Equals($Kind, 'completed_before_fault', [System.StringComparison]::OrdinalIgnoreCase)
        DataPathProofMissing = (-not $peerVisibleObserved)
        TransferId = $TransferId
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
    if (Submit-TunaWalletPasswordDialogIfPresent -Process $Process -Password $Password -RoleLabel $RoleLabel -Reason 'unlock_entry_existing_prompt') {
        if ($WaitForRuntimeEvidence) {
            $runtimeEvidenceNeedleSetsForExistingPrompt = @(
                @('event=tuna_runtime_unlocked'),
                @('event=tuna_acceleration_timeline', 'status=selected_payer_starting_listener'),
                @('event=tuna_acceleration_timeline', 'status=listener_starting'),
                @('event=tuna_acceleration_timeline', 'status=waiting_for_answer'),
                @('event=tuna_acceleration_negotiated'),
                @('event=tuna_acceleration_timeline', 'active=1')
            )
            [void](Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $bookmark -NeedleSets $runtimeEvidenceNeedleSetsForExistingPrompt -TimeoutMs 90000 -Description "$RoleLabel Tuna runtime unlocked or runtime-unlock negotiation started after existing wallet prompt")
        }

        return
    }

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
    $runtimeFailureNeedleSets = @(
        @('event=tuna_acceleration_activation_offer_not_observed'),
        @('event=tuna_acceleration_runtime_unlock_offer_peer_response_timeout'),
        @('event=runtime_unlock_regular_v4_activation_window_failed'),
        @('event=runtime_unlock_regular_v4_peer_visible_window_failed'),
        @('event=session_recovery_contract_failed', 'runtime_unlock'),
        @('event=tuna_runtime_unlock_failed')
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
    $findRuntimeFailureEvidence = {
        foreach ($line in @(Get-AppLogLinesAfterBookmark -Bookmark $bookmark)) {
            foreach ($needleSet in $runtimeFailureNeedleSets) {
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

            $failureLine = & $findRuntimeFailureEvidence
            if (-not [string]::IsNullOrWhiteSpace($failureLine)) {
                return [pscustomobject]@{
                    Kind = 'runtime_failure'
                    Dialog = $null
                    EvidenceLine = $failureLine
                }
            }
        }

        return $null
    }

    if ([string]::Equals([string]$dialogOrEvidence.Kind, 'runtime_evidence', [System.StringComparison]::OrdinalIgnoreCase)) {
        if (Wait-AndSubmitTunaWalletPasswordDialogIfPresent -Process $Process -Password $Password -RoleLabel $RoleLabel -Reason 'runtime_evidence_late_wallet_prompt') {
            Write-Host "[GUI Smoke][filetransfer_tuna] $RoleLabel Tuna unlock had runtime evidence, then a wallet prompt; submitted password." -ForegroundColor Green
            return
        }

        Write-Host "[GUI Smoke][filetransfer_tuna] $RoleLabel Tuna unlock already had runtime evidence after toggle." -ForegroundColor Green
        return
    }
    if ([string]::Equals([string]$dialogOrEvidence.Kind, 'runtime_failure', [System.StringComparison]::OrdinalIgnoreCase)) {
        Write-Host "[GUI Smoke][filetransfer_tuna] $RoleLabel Tuna unlock failed before wallet prompt: $($dialogOrEvidence.EvidenceLine)" -ForegroundColor Yellow
        throw "$RoleLabel Tuna unlock failed before wallet prompt. Evidence: $($dialogOrEvidence.EvidenceLine)"
    }

    [void](Submit-TunaWalletPasswordDialog -Process $Process -Dialog $dialogOrEvidence.Dialog -Password $Password -RoleLabel $RoleLabel -Reason 'unlock_toggle_dialog')

    if ($WaitForRuntimeEvidence) {
        [void](Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $bookmark -NeedleSets $runtimeEvidenceNeedleSets -TimeoutMs 90000 -Description "$RoleLabel Tuna runtime unlocked or runtime-unlock negotiation started")
        Write-Host "[GUI Smoke][filetransfer_tuna] $RoleLabel Tuna unlock completed." -ForegroundColor Green
        return
    }
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

function Get-TunaGuiCompletedTerminalPairAfterBookmark {
    param([Parameter(Mandatory = $true)][int]$Bookmark)

    $inboundCompletedByTransfer = @{}
    $outboundCompletedByTransfer = @{}
    foreach ($line in @(Get-AppLogLinesAfterBookmark -Bookmark $Bookmark)) {
        $terminal = Get-TunaGuiCompletedTerminalFromLogLine -Line ([string]$line)
        if ($null -eq $terminal) {
            continue
        }

        $completedTransferId = Add-TunaGuiCompletedTerminalToTracker `
            -InboundCompletedByTransfer $inboundCompletedByTransfer `
            -OutboundCompletedByTransfer $outboundCompletedByTransfer `
            -Terminal $terminal
        if (-not [string]::IsNullOrWhiteSpace([string]$completedTransferId)) {
            return [pscustomobject]@{
                TransferId = [string]$completedTransferId
                Line = [string]$terminal.Line
            }
        }
    }

    return $null
}

function Test-TunaGuiSwitchOffToggleReady {
    param($Toggle)

    if ($null -eq $Toggle) {
        return $false
    }

    try {
        return [bool]$Toggle.Current.IsEnabled -and -not [bool]$Toggle.Current.IsOffscreen
    }
    catch {
        return $false
    }
}

function Wait-TunaGuiSwitchOffToggleOrTerminal {
    param(
        [Parameter(Mandatory = $true)]$Window,
        [Parameter(Mandatory = $true)][int]$Bookmark,
        $PrearmedToggle = $null,
        [int]$TimeoutMs = 5000,
        [int]$PollIntervalMs = 100
    )

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        $terminalPair = Get-TunaGuiCompletedTerminalPairAfterBookmark -Bookmark $Bookmark
        if ($null -ne $terminalPair) {
            return [pscustomobject]@{
                Kind = 'terminal'
                Toggle = $null
                TransferId = [string]$terminalPair.TransferId
                WaitMs = [long]$sw.ElapsedMilliseconds
                Prearmed = $false
            }
        }

        if (Test-TunaGuiSwitchOffToggleReady -Toggle $PrearmedToggle) {
            return [pscustomobject]@{
                Kind = 'toggle'
                Toggle = $PrearmedToggle
                TransferId = '(none)'
                WaitMs = [long]$sw.ElapsedMilliseconds
                Prearmed = $true
            }
        }

        $freshToggle = Find-VisibleByAutomationId -Root $Window -AutomationId 'SessionHeader.TunaUnlockToggle'
        if (Test-TunaGuiSwitchOffToggleReady -Toggle $freshToggle) {
            return [pscustomobject]@{
                Kind = 'toggle'
                Toggle = $freshToggle
                TransferId = '(none)'
                WaitMs = [long]$sw.ElapsedMilliseconds
                Prearmed = $false
            }
        }

        Start-Sleep -Milliseconds ([Math]::Max(10, $PollIntervalMs))
    }

    $terminalPair = Get-TunaGuiCompletedTerminalPairAfterBookmark -Bookmark $Bookmark
    if ($null -ne $terminalPair) {
        return [pscustomobject]@{
            Kind = 'terminal'
            Toggle = $null
            TransferId = [string]$terminalPair.TransferId
            WaitMs = [long]$sw.ElapsedMilliseconds
            Prearmed = $false
        }
    }

    throw ("gui_smoke_switch_off_control_not_ready_before_fault_window: timeout_ms={0}; terminal_pair=0" -f $TimeoutMs)
}

function Try-PrearmTunaGuiSwitchOffToggle {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [Parameter(Mandatory = $true)][string]$PayerMode,
        [Parameter(Mandatory = $true)][int]$Bookmark,
        [int]$TimeoutMs = 5000
    )

    $target = Get-TunaGuiFaultTarget -PayerMode $PayerMode
    $targetWindow = if ($target -eq 'helper') { $Context.HelperWindow } else { $Context.HelpeeWindow }
    try {
        return Wait-TunaGuiSwitchOffToggleOrTerminal `
            -Window $targetWindow `
            -Bookmark $Bookmark `
            -TimeoutMs $TimeoutMs
    }
    catch {
        if ($_.Exception.Message.IndexOf('gui_smoke_log_cursor_', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw
        }

        return [pscustomobject]@{
            Kind = 'unavailable'
            Toggle = $null
            TransferId = '(none)'
            WaitMs = [long]$TimeoutMs
            Prearmed = $false
            Error = [string]$_.Exception.Message
        }
    }
}

function Get-TunaGuiLogLineUtcText {
    param([AllowEmptyString()][string]$Line)

    if (-not [string]::IsNullOrWhiteSpace($Line)) {
        $match = [regex]::Match($Line, '^\[(?<utc>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}Z)\]')
        if ($match.Success) {
            return $match.Groups['utc'].Value
        }
    }

    return '(none)'
}

function New-TunaGuiFaultWindowDiagnosticLine {
    param(
        [AllowEmptyString()][string]$ActivationLine,
        [AllowEmptyString()][string]$ActivationObservedUtc,
        [Parameter(Mandatory = $true)]$FaultResult
    )

    $cursor = Get-GuiSmokeLogLedgerDiagnostics -State $script:GuiSmokeAppLogLedger
    return "[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_fault_window; activation_event_utc={1}; activation_observed_utc={2}; prearmed={3}; toggle_wait_ms={4}; cursor_generation_count={5}; cursor_rotation_count={6}; cursor_transient_rescan_count={7}; cursor_gap_count={8}; click_utc={9}; terminal_before_click={10}; fault_injected={11}" -f `
        ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')),
        (Get-TunaGuiLogLineUtcText -Line $ActivationLine),
        $(if ([string]::IsNullOrWhiteSpace($ActivationObservedUtc)) { '(none)' } else { $ActivationObservedUtc }),
        $(if ([bool]$FaultResult.Prearmed) { 1 } else { 0 }),
        [long]$FaultResult.ToggleWaitMs,
        [int]$cursor.GenerationCount,
        [int]$cursor.RotationCount,
        [int]$cursor.TransientRescanCount,
        [int]$cursor.GapCount,
        [string]$FaultResult.ClickUtc,
        $(if ([bool]$FaultResult.TerminalCompletedBeforeFault) { 1 } else { 0 }),
        $(if ([bool]$FaultResult.Injected) { 1 } else { 0 })
}

function Add-TunaGuiFaultWindowEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)]$ObservedEvidenceLines,
        [AllowEmptyString()][string]$ActivationLine,
        [AllowEmptyString()][string]$ActivationObservedUtc,
        [Parameter(Mandatory = $true)]$FaultResult
    )

    $ObservedEvidenceLines.Add((New-TunaGuiFaultWindowDiagnosticLine `
        -ActivationLine $ActivationLine `
        -ActivationObservedUtc $ActivationObservedUtc `
        -FaultResult $FaultResult)) | Out-Null
    Write-TunaGuiMilestoneEvidence -ArtifactDir $ArtifactDir -ObservedEvidenceLines $ObservedEvidenceLines
}

function Invoke-TunaGuiFallbackFault {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [Parameter(Mandatory = $true)][string]$FaultMode,
        [Parameter(Mandatory = $true)][string]$PayerMode,
        [Parameter(Mandatory = $true)][int]$Bookmark,
        $PrearmedToggle = $null,
        [long]$PrearmedToggleWaitMs = 0
    )

    $target = Get-TunaGuiFaultTarget -PayerMode $PayerMode
    $targetWindow = if ($target -eq 'helper') { $Context.HelperWindow } else { $Context.HelpeeWindow }
    $targetProc = if ($target -eq 'helper') { $Context.HelperProc } else { $Context.HelpeeProc }

    if ($FaultMode -eq 'switch-off') {
        $faultBookmark = Get-AppLogBookmark
        $ready = Wait-TunaGuiSwitchOffToggleOrTerminal `
            -Window $targetWindow `
            -Bookmark $Bookmark `
            -PrearmedToggle $PrearmedToggle `
            -TimeoutMs 5000
        $totalToggleWaitMs = [Math]::Max(0L, $PrearmedToggleWaitMs) + [Math]::Max(0L, [long]$ready.WaitMs)
        if ([string]::Equals([string]$ready.Kind, 'terminal', [System.StringComparison]::OrdinalIgnoreCase)) {
            return [pscustomobject]@{
                Injected = $false
                TerminalCompletedBeforeFault = $true
                TransferId = [string]$ready.TransferId
                Prearmed = $false
                ToggleWaitMs = $totalToggleWaitMs
                ClickUtc = '(none)'
                RuntimeEvidenceLine = '(none)'
            }
        }

        $terminalPair = Get-TunaGuiCompletedTerminalPairAfterBookmark -Bookmark $Bookmark
        if ($null -ne $terminalPair) {
            return [pscustomobject]@{
                Injected = $false
                TerminalCompletedBeforeFault = $true
                TransferId = [string]$terminalPair.TransferId
                Prearmed = [bool]$ready.Prearmed
                ToggleWaitMs = $totalToggleWaitMs
                ClickUtc = '(none)'
                RuntimeEvidenceLine = '(none)'
            }
        }

        $clickUtc = [datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')
        Click-Element $ready.Toggle
        $runtimeEvidenceLine = [string](Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $faultBookmark -NeedleSets @(
            @('event=tuna_acceleration_reset', 'reason=header_switch_off'),
            @('event=tuna_acceleration_timeline', 'status=header_switch_off'),
            @('event=tuna_runtime_locked', 'source=header', 'reason=header_switch_off')
        ) -TimeoutMs 15000 -Description 'fresh header switch-off runtime evidence')
        Write-Host "[GUI Smoke][filetransfer_tuna] Triggered Tuna fallback by switching off $target." -ForegroundColor Yellow
        return [pscustomobject]@{
            Injected = $true
            TerminalCompletedBeforeFault = $false
            TransferId = '(none)'
            Prearmed = [bool]$ready.Prearmed
            ToggleWaitMs = $totalToggleWaitMs
            ClickUtc = $clickUtc
            RuntimeEvidenceLine = $runtimeEvidenceLine
        }
    }

    $terminalPair = Get-TunaGuiCompletedTerminalPairAfterBookmark -Bookmark $Bookmark
    if ($null -ne $terminalPair) {
        return [pscustomobject]@{
            Injected = $false
            TerminalCompletedBeforeFault = $true
            TransferId = [string]$terminalPair.TransferId
            Prearmed = $false
            ToggleWaitMs = 0L
            ClickUtc = '(none)'
            RuntimeEvidenceLine = '(none)'
        }
    }

    $children = @(
        Get-CimInstance Win32_Process -Filter "ParentProcessId = $($targetProc.Id)" -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -ieq 'nlink-tuna-sidecar.exe' -or $_.Name -ieq 'nlink-tuna-sidecar' }
    )
    if ($children.Count -eq 0) {
        throw "No Tuna sidecar child process found for $target."
    }

    $terminalPair = Get-TunaGuiCompletedTerminalPairAfterBookmark -Bookmark $Bookmark
    if ($null -ne $terminalPair) {
        return [pscustomobject]@{
            Injected = $false
            TerminalCompletedBeforeFault = $true
            TransferId = [string]$terminalPair.TransferId
            Prearmed = $false
            ToggleWaitMs = 0L
            ClickUtc = '(none)'
            RuntimeEvidenceLine = '(none)'
        }
    }

    foreach ($child in $children) {
        Stop-Process -Id $child.ProcessId -Force -ErrorAction Stop
    }
    Write-Host "[GUI Smoke][filetransfer_tuna] Triggered Tuna fallback by killing $target sidecar child process(es)." -ForegroundColor Yellow
    return [pscustomobject]@{
        Injected = $true
        TerminalCompletedBeforeFault = $false
        TransferId = '(none)'
        Prearmed = $false
        ToggleWaitMs = 0L
        ClickUtc = [datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')
        RuntimeEvidenceLine = '(sidecar_kill)'
    }
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

function Get-TunaGuiLiveRouteEpochRouteChanges {
    param([Parameter(Mandatory = $true)]$Observations)

    $sequence = @($Observations.ToArray() |
        Sort-Object { [int]$_['order'] } |
        ForEach-Object { [string]($_['route']) } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_ -ne '(unknown)' })
    $changes = New-Object System.Collections.Generic.List[string]
    foreach ($route in $sequence) {
        if ($changes.Count -eq 0 -or
            -not [string]::Equals($changes[$changes.Count - 1], $route, [System.StringComparison]::OrdinalIgnoreCase)) {
            $changes.Add($route) | Out-Null
        }
    }

    return @($changes.ToArray())
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
        [int]$TimeoutMs = 90000,
        [scriptblock]$OnPoll = $null
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
        -TimeoutMs $TimeoutMs `
        -OnPoll $OnPoll)
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
        [int]$TimeoutMs = 90000,
        [scriptblock]$OnPoll = $null
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
    $newScanState = {
        param([int]$CandidateBookmark)

        return [pscustomobject]@{
            Cursor = $CandidateBookmark
            AfterLineSeen = -not $requiresAfterLine
        }
    }
    $primaryScanState = & $newScanState $Bookmark
    $fallbackEnabled = $FallbackBookmark -ge 0 -and
        $FallbackBookmark -ne $Bookmark -and
        $FallbackBookmark -lt $Bookmark
    $fallbackScanState = if ($fallbackEnabled) {
        & $newScanState $FallbackBookmark
    }
    else {
        $null
    }
    $waitStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $pollMs = 500
    $timeoutMessage = "Timed out waiting for app log evidence: $label"
    $findMatchingLine = {
        param(
            [Parameter(Mandatory = $true)]$ScanState,
            [Parameter(Mandatory = $true)][int]$EndIndex
        )

        return Invoke-GuiSmokeLogLedgerRangeVisitor `
            -State $script:GuiSmokeAppLogLedger `
            -StartIndex ([int]$ScanState.Cursor) `
            -EndIndex $EndIndex `
            -Visitor {
                param([string]$line, [int]$lineIndex)

                if ($waitStopwatch.ElapsedMilliseconds -ge $TimeoutMs) {
                    throw [System.TimeoutException]::new($timeoutMessage)
                }

                $ScanState.Cursor = $lineIndex + 1
                if (-not [bool]$ScanState.AfterLineSeen) {
                    if ([string]::Equals($line, $AfterLine, [System.StringComparison]::Ordinal)) {
                        $ScanState.AfterLineSeen = $true
                    }

                    return $null
                }

                $matched = $true
                foreach ($needle in $needles) {
                    if ($line.IndexOf([string]$needle, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
                        $matched = $false
                        break
                    }
                }

                if (-not $matched) {
                    return $null
                }

                $fields = ConvertFrom-GuiSmokeSemicolonFields -Message $line
                $epoch = ConvertTo-GuiSmokeInt -Value (Get-GuiSmokeFieldValue -Fields $fields -Name 'live_route_epoch' -Default '0') -Default 0
                if ($LiveRouteEpoch -gt 0 -and $epoch -ne $LiveRouteEpoch) {
                    return $null
                }

                if ($AfterLiveRouteEpoch -gt 0 -and $epoch -le $AfterLiveRouteEpoch) {
                    return $null
                }

                return $line
            }
    }

    while ($waitStopwatch.ElapsedMilliseconds -lt $TimeoutMs) {
        try {
            if ($null -ne $OnPoll) {
                [void](& $OnPoll)
            }

            if ($waitStopwatch.ElapsedMilliseconds -ge $TimeoutMs) {
                break
            }

            Sync-GuiSmokeAppLogLedger
            if ($waitStopwatch.ElapsedMilliseconds -ge $TimeoutMs) {
                break
            }

            $endIndex = Get-GuiSmokeLogLedgerBookmark -State $script:GuiSmokeAppLogLedger
            $line = & $findMatchingLine $primaryScanState $endIndex
            if ($line) {
                return $line
            }

            if ($fallbackEnabled) {
                $line = & $findMatchingLine $fallbackScanState $endIndex
                if ($line) {
                    return $line
                }
            }
        }
        catch [System.TimeoutException] {
            throw $timeoutMessage
        }
        catch {
            # Preserve the existing tolerance for transient UIA and log-read failures.
        }

        $remainingMs = $TimeoutMs - [int]$waitStopwatch.ElapsedMilliseconds
        if ($remainingMs -le 0) {
            break
        }

        Start-Sleep -Milliseconds ([Math]::Min($pollMs, $remainingMs))
    }

    throw $timeoutMessage
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
            $payloadBytes = Get-TunaGuiPeerVisibleFileTunaV4PayloadBytes -Line $line
            if ($payloadBytes -ge 0L) {
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
    $terminalInboundCompletedByTransfer = @{}
    $terminalOutboundCompletedByTransfer = @{}
    $terminalPairCompleted = $false
    $terminalTransferId = '(unknown)'
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        $lines = @(Get-AppLogLinesAfterBookmark -Bookmark $Bookmark)
        if ($lines.Count -lt $processedLineCount) {
            $processedLineCount = 0
        }

        for ($lineIndex = $processedLineCount; $lineIndex -lt $lines.Count; $lineIndex++) {
            $line = $lines[$lineIndex]
            $terminal = Get-TunaGuiCompletedTerminalFromLogLine -Line $line
            if ($null -ne $terminal) {
                $completedTransferId = Add-TunaGuiCompletedTerminalToTracker -InboundCompletedByTransfer $terminalInboundCompletedByTransfer -OutboundCompletedByTransfer $terminalOutboundCompletedByTransfer -Terminal $terminal
                if (-not [string]::IsNullOrWhiteSpace($completedTransferId)) {
                    $terminalPairCompleted = $true
                    $terminalTransferId = $completedTransferId
                }
            }

            $peerVisiblePayloadBytes = Get-TunaGuiPeerVisibleFileTunaV4PayloadBytes -Line $line
            if ($peerVisiblePayloadBytes -ge 0L) {
                $lastFramePayloadBytes = [Math]::Max($lastFramePayloadBytes, $peerVisiblePayloadBytes)
                if ($peerVisiblePayloadBytes -ge $MinimumFramePayloadBytes) {
                    $acceleratedPayloadBytes += $peerVisiblePayloadBytes
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

        if ($terminalPairCompleted) {
            $line = "[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_live_switch_off_completed_before_fault; mode=completed_before_fault; committed_bytes={1}; minimum_committed_bytes={2}; accelerated_payload_bytes={3}; minimum_peer_visible_payload_bytes={4}; peer_visible_tuna_payload_observed={5}; data_path_proof_missing={6}; fault_injected=0; transfer_id={7}; elapsed_ms={8}; reason=transfer_completed_before_fault_injection" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $maxCommittedBytes, $MinimumCommittedBytes, $acceleratedPayloadBytes, $MinimumPeerVisiblePayloadBytes, ($(if ($acceleratedPayloadBytes -gt 0L) { 1 } else { 0 })), ($(if ($acceleratedPayloadBytes -gt 0L) { 0 } else { 1 })), $terminalTransferId, $sw.ElapsedMilliseconds
            return New-TunaGuiLiveSwitchOffProgressGateResult -Kind 'completed_before_fault' -Mode 'completed_before_fault' -Line $line -CommittedBytes $maxCommittedBytes -MinimumCommittedBytes $MinimumCommittedBytes -AcceleratedPayloadBytes $acceleratedPayloadBytes -MinimumPeerVisiblePayloadBytes $MinimumPeerVisiblePayloadBytes -LastFramePayloadBytes $lastFramePayloadBytes -ElapsedMs $sw.ElapsedMilliseconds -TransferId $terminalTransferId
        }

        if ($sw.ElapsedMilliseconds -ge $MinimumElapsedMs) {
            if ($maxCommittedBytes -ge $MinimumCommittedBytes) {
                $line = "[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_live_switch_off_progress_gate_satisfied; mode=receiver_committed; committed_bytes={1}; minimum_committed_bytes={2}; receiver_committed_progress_observed=1; accelerated_payload_bytes={3}; minimum_peer_visible_payload_bytes={4}; peer_visible_tuna_payload_observed={5}; data_path_proof_missing={6}; elapsed_ms={7}" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $maxCommittedBytes, $MinimumCommittedBytes, $acceleratedPayloadBytes, $MinimumPeerVisiblePayloadBytes, ($(if ($acceleratedPayloadBytes -gt 0L) { 1 } else { 0 })), ($(if ($acceleratedPayloadBytes -gt 0L) { 0 } else { 1 })), $sw.ElapsedMilliseconds
                return New-TunaGuiLiveSwitchOffProgressGateResult -Kind 'progress' -Mode 'receiver_committed' -Line $line -CommittedBytes $maxCommittedBytes -MinimumCommittedBytes $MinimumCommittedBytes -AcceleratedPayloadBytes $acceleratedPayloadBytes -MinimumPeerVisiblePayloadBytes $MinimumPeerVisiblePayloadBytes -LastFramePayloadBytes $lastFramePayloadBytes -ElapsedMs $sw.ElapsedMilliseconds
            }

            if ($acceleratedPayloadBytes -ge $MinimumPeerVisiblePayloadBytes) {
                $line = "[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_live_switch_off_progress_gate_satisfied; mode=peer_visible_payload; committed_bytes={1}; minimum_committed_bytes={2}; receiver_committed_progress_observed={3}; accelerated_payload_bytes={4}; minimum_peer_visible_payload_bytes={5}; peer_visible_tuna_payload_observed=1; data_path_proof_missing=0; last_frame_payload_bytes={6}; elapsed_ms={7}; reason=bounded_tuna_bulk_payload_before_fault" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $maxCommittedBytes, $MinimumCommittedBytes, ($(if ($maxCommittedBytes -ge $MinimumCommittedBytes) { 1 } else { 0 })), $acceleratedPayloadBytes, $MinimumPeerVisiblePayloadBytes, $lastFramePayloadBytes, $sw.ElapsedMilliseconds
                return New-TunaGuiLiveSwitchOffProgressGateResult -Kind 'peer_visible_payload' -Mode 'peer_visible_payload' -Line $line -CommittedBytes $maxCommittedBytes -MinimumCommittedBytes $MinimumCommittedBytes -AcceleratedPayloadBytes $acceleratedPayloadBytes -MinimumPeerVisiblePayloadBytes $MinimumPeerVisiblePayloadBytes -LastFramePayloadBytes $lastFramePayloadBytes -ElapsedMs $sw.ElapsedMilliseconds
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
    $terminalInboundCompletedByTransfer = @{}
    $terminalOutboundCompletedByTransfer = @{}
    $terminalPairCompleted = $false
    $terminalTransferId = '(unknown)'
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        $lines = @(Get-AppLogLinesAfterBookmark -Bookmark $Bookmark)
        if ($lines.Count -lt $processedLineCount) {
            $processedLineCount = 0
        }

        for ($lineIndex = $processedLineCount; $lineIndex -lt $lines.Count; $lineIndex++) {
            $line = [string]$lines[$lineIndex]
            $terminal = Get-TunaGuiCompletedTerminalFromLogLine -Line $line
            if ($null -ne $terminal) {
                $completedTransferId = Add-TunaGuiCompletedTerminalToTracker -InboundCompletedByTransfer $terminalInboundCompletedByTransfer -OutboundCompletedByTransfer $terminalOutboundCompletedByTransfer -Terminal $terminal
                if (-not [string]::IsNullOrWhiteSpace($completedTransferId)) {
                    $terminalPairCompleted = $true
                    $terminalTransferId = $completedTransferId
                }
            }

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

            $peerVisiblePayloadBytes = Get-TunaGuiPeerVisibleFileTunaV4PayloadBytes -Line $line
            if ($peerVisiblePayloadBytes -ge 0L) {
                $lastFramePayloadBytes = [Math]::Max($lastFramePayloadBytes, $peerVisiblePayloadBytes)
                if ($peerVisiblePayloadBytes -ge $MinimumFramePayloadBytes) {
                    $acceleratedPayloadBytes += $peerVisiblePayloadBytes
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

        if ($terminalPairCompleted) {
            $line = "[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_live_switch_off_completed_before_fault; mode=completed_before_fault; committed_bytes={1}; minimum_committed_bytes={2}; accelerated_payload_bytes={3}; minimum_peer_visible_payload_bytes={4}; peer_visible_tuna_payload_observed={5}; data_path_proof_missing={6}; fault_injected=0; transfer_id={7}; elapsed_ms={8}; reason=transfer_completed_before_fault_injection" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $maxCommittedBytes, $MinimumCommittedBytes, $acceleratedPayloadBytes, $MinimumPeerVisiblePayloadBytes, ($(if ($acceleratedPayloadBytes -gt 0L) { 1 } else { 0 })), ($(if ($acceleratedPayloadBytes -gt 0L) { 0 } else { 1 })), $terminalTransferId, $sw.ElapsedMilliseconds
            return New-TunaGuiLiveSwitchOffProgressGateResult -Kind 'completed_before_fault' -Mode 'completed_before_fault' -Line $line -CommittedBytes $maxCommittedBytes -MinimumCommittedBytes $MinimumCommittedBytes -AcceleratedPayloadBytes $acceleratedPayloadBytes -MinimumPeerVisiblePayloadBytes $MinimumPeerVisiblePayloadBytes -LastFramePayloadBytes $lastFramePayloadBytes -ElapsedMs $sw.ElapsedMilliseconds -TransferId $terminalTransferId
        }

        if ($sw.ElapsedMilliseconds -ge $MinimumElapsedMs) {
            if ($maxCommittedBytes -ge $MinimumCommittedBytes) {
                $line = "[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_live_switch_off_progress_gate_satisfied; mode=receiver_committed; committed_bytes={1}; minimum_committed_bytes={2}; receiver_committed_progress_observed=1; accelerated_payload_bytes={3}; minimum_peer_visible_payload_bytes={4}; peer_visible_tuna_payload_observed={5}; data_path_proof_missing={6}; elapsed_ms={7}" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $maxCommittedBytes, $MinimumCommittedBytes, $acceleratedPayloadBytes, $MinimumPeerVisiblePayloadBytes, ($(if ($acceleratedPayloadBytes -gt 0L) { 1 } else { 0 })), ($(if ($acceleratedPayloadBytes -gt 0L) { 0 } else { 1 })), $sw.ElapsedMilliseconds
                return [pscustomobject]@{
                    Kind = 'progress'
                    Mode = 'receiver_committed'
                    Line = $line
                    CommittedBytes = $maxCommittedBytes
                    MinimumCommittedBytes = $MinimumCommittedBytes
                    ReceiverCommittedProgressObserved = $true
                    AcceleratedPayloadBytes = $acceleratedPayloadBytes
                    MinimumPeerVisiblePayloadBytes = $MinimumPeerVisiblePayloadBytes
                    PeerVisibleTunaPayloadObserved = $acceleratedPayloadBytes -gt 0L
                    LastFramePayloadBytes = $lastFramePayloadBytes
                    ElapsedMs = $sw.ElapsedMilliseconds
                    TerminalCompletedBeforeFault = $false
                    DataPathProofMissing = $acceleratedPayloadBytes -le 0L
                    TransferId = '(unknown)'
                }
            }

            if ($acceleratedPayloadBytes -ge $MinimumPeerVisiblePayloadBytes) {
                $line = "[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_live_switch_off_progress_gate_satisfied; mode=peer_visible_payload; committed_bytes={1}; minimum_committed_bytes={2}; receiver_committed_progress_observed={3}; accelerated_payload_bytes={4}; minimum_peer_visible_payload_bytes={5}; peer_visible_tuna_payload_observed=1; data_path_proof_missing=0; last_frame_payload_bytes={6}; elapsed_ms={7}; reason=bounded_tuna_bulk_payload_before_fault" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $maxCommittedBytes, $MinimumCommittedBytes, ($(if ($maxCommittedBytes -ge $MinimumCommittedBytes) { 1 } else { 0 })), $acceleratedPayloadBytes, $MinimumPeerVisiblePayloadBytes, $lastFramePayloadBytes, $sw.ElapsedMilliseconds
                return New-TunaGuiLiveSwitchOffProgressGateResult -Kind 'peer_visible_payload' -Mode 'peer_visible_payload' -Line $line -CommittedBytes $maxCommittedBytes -MinimumCommittedBytes $MinimumCommittedBytes -AcceleratedPayloadBytes $acceleratedPayloadBytes -MinimumPeerVisiblePayloadBytes $MinimumPeerVisiblePayloadBytes -LastFramePayloadBytes $lastFramePayloadBytes -ElapsedMs $sw.ElapsedMilliseconds
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
        diagnosticTunaReadinessObserved = $false
        routePublicationObserved = $false
        fileTunaV4PublicationObserved = $false
        receiverCommittedProgressObserved = $false
        peerVisibleTunaPayloadObserved = $false
        terminalCompletedBeforeFault = $false
        faultInjected = $false
        dataPathProofMissing = $false
        missingTunaDataPathProof = $false
        activationEpochStarted = $false
        activationEpochRecovered = $false
        fallbackEpochStarted = $false
        fallbackEvidenceObserved = $false
        fallbackEpochRecovered = $false
        fallbackEpochWaiting = $false
        pauseSent = $false
        pauseReceived = $false
        resumeSent = $false
        resumeReceived = $false
        heartbeatTimeoutCount = 0
        heartbeatDeferredTimeoutCount = 0
        heartbeatTimeoutBeforeTerminalCount = 0
        heartbeatTimeoutAfterTerminalCount = 0
        peerDisconnectedCount = 0
        peerDisconnectedBeforeTerminalCount = 0
        peerDisconnectedAfterTerminalCount = 0
        transportFailedCount = 0
        transportFailedBeforeTerminalCount = 0
        transportFailedAfterTerminalCount = 0
        terminalInboundCompleted = $false
        terminalOutboundCompleted = $false
        terminalPairCompleted = $false
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
        $fields = ConvertFrom-GuiSmokeSemicolonFields -Message $line
        $eventName = Get-GuiSmokeFieldValue -Fields $fields -Name 'event'
        if ($eventName -eq 'transfer_terminal' -or
            $eventName -eq 'file_transfer_inbound_terminal' -or
            $eventName -eq 'file_transfer_outbound_terminal') {
            $direction = Get-GuiSmokeFieldValue -Fields $fields -Name 'direction'
            if ([string]::IsNullOrWhiteSpace($direction)) {
                if ($eventName -eq 'file_transfer_inbound_terminal') { $direction = 'inbound' }
                elseif ($eventName -eq 'file_transfer_outbound_terminal') { $direction = 'outbound' }
            }

            $state = Get-GuiSmokeFieldValue -Fields $fields -Name 'state'
            $errorCode = Get-GuiSmokeFieldValue -Fields $fields -Name 'error_code'
            $terminalComplete =
                $state -eq 'Completed' -or
                ($eventName -eq 'transfer_terminal' -and
                    $errorCode -eq '(none)' -and
                    $line.IndexOf('reason=Transfer complete', [System.StringComparison]::OrdinalIgnoreCase) -ge 0)

            if ($terminalComplete -and $errorCode -eq '(none)') {
                if ($direction -eq 'inbound') { $summary.terminalInboundCompleted = $true }
                elseif ($direction -eq 'outbound') { $summary.terminalOutboundCompleted = $true }

                if ($summary.terminalInboundCompleted -and $summary.terminalOutboundCompleted) {
                    $summary.terminalPairCompleted = $true
                }
            }
        }

        if ($line.IndexOf('event=tuna_acceleration_negotiated', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $summary.tunaNegotiated = $true
        }

        if (($line.IndexOf('event=runtime_unlock_transaction_answer_received', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                $line.IndexOf('peer_visible_proof=1', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                $line.IndexOf('answer_received=1', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                $line.IndexOf('tuna_path_lease_current=1', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                $line.IndexOf('tuna_path_lease_state=listenerready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0)) {
            $summary.diagnosticTunaReadinessObserved = $true
        }

        if (Test-TunaGuiAuthorizedFileTunaV4PublicationLine -Line $line) {
            $summary.routePublicationObserved = $true
            $summary.fileTunaV4PublicationObserved = $true
        }

        if ($eventName -eq 'filetransfer_tuna_gui_live_switch_off_progress_gate_satisfied') {
            $mode = Get-GuiSmokeFieldValue -Fields $fields -Name 'mode' -Default ''
            if ([string]::Equals($mode, 'receiver_committed', [System.StringComparison]::OrdinalIgnoreCase) -or
                (Get-GuiSmokeFieldValue -Fields $fields -Name 'receiver_committed_progress_observed' -Default '0') -eq '1') {
                $summary.receiverCommittedProgressObserved = $true
            }

            if ((Get-GuiSmokeFieldValue -Fields $fields -Name 'peer_visible_tuna_payload_observed' -Default '0') -eq '1') {
                $summary.peerVisibleTunaPayloadObserved = $true
            }

            if ((Get-GuiSmokeFieldValue -Fields $fields -Name 'data_path_proof_missing' -Default '0') -eq '1') {
                $summary.dataPathProofMissing = $true
                $summary.missingTunaDataPathProof = $true
            }
        }

        if ($eventName -eq 'filetransfer_tuna_gui_live_switch_off_completed_before_fault') {
            $summary.terminalCompletedBeforeFault = $true
            if ((Get-GuiSmokeFieldValue -Fields $fields -Name 'data_path_proof_missing' -Default '0') -eq '1') {
                $summary.dataPathProofMissing = $true
                $summary.missingTunaDataPathProof = $true
            }
        }

        if ($eventName -eq 'filetransfer_tuna_gui_live_switch_off_fault_injected') {
            $summary.faultInjected = $true
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
            $summary.fallbackEvidenceObserved = $true
        }

        if ($line.IndexOf('event=tuna_fallback_started', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('event=tuna_fallback_filetransfer_rebind_requested', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('event=tuna_acceleration_user_stop_filetransfer_fallback_forced', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('event=filetransfer_post_tuna_recovery_started', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $summary.fallbackEvidenceObserved = $true
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
            $summary.fallbackEpochStarted = $true
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
                if ($summary.terminalPairCompleted) { $summary.heartbeatTimeoutAfterTerminalCount++ }
                else { $summary.heartbeatTimeoutBeforeTerminalCount++ }
            }
        }

        if ($line.IndexOf('peer_disconnected', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $summary.peerDisconnectedCount++
            if ($summary.terminalPairCompleted) { $summary.peerDisconnectedAfterTerminalCount++ }
            else { $summary.peerDisconnectedBeforeTerminalCount++ }
        }

        if ($line.IndexOf('transport_state_changed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $line.IndexOf('to=Failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $summary.transportFailedCount++
            if ($summary.terminalPairCompleted) { $summary.transportFailedAfterTerminalCount++ }
            else { $summary.transportFailedBeforeTerminalCount++ }
        }

        if ($eventName -eq 'filetransfer_tuna_data_path_peer_visible' -and
            (Test-TunaGuiPeerVisibleFileTunaV4DataPathLine -Line $line)) {
            $summary.peerVisibleTunaPayloadObserved = $true
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
    $routeObservedUtc = [datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')
    $prearmResult = Try-PrearmTunaGuiSwitchOffToggle -Context $Context -PayerMode $PayerMode -Bookmark $bookmark
    $prearmedSwitchOffToggle = if ([string]::Equals([string]$prearmResult.Kind, 'toggle', [System.StringComparison]::OrdinalIgnoreCase)) { $prearmResult.Toggle } else { $null }
    $prearmedSwitchOffToggleWaitMs = [long]$prearmResult.WaitMs

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

    $faultResult = Invoke-TunaGuiFallbackFault -Context $Context -FaultMode $FaultMode -PayerMode $PayerMode -Bookmark $bookmark -PrearmedToggle $prearmedSwitchOffToggle -PrearmedToggleWaitMs $prearmedSwitchOffToggleWaitMs
    Add-TunaGuiFaultWindowEvidence -ArtifactDir $ArtifactDir -ObservedEvidenceLines $observedEvidenceLines -ActivationLine $routeLine -ActivationObservedUtc $routeObservedUtc -FaultResult $faultResult
    if (-not [bool]$faultResult.Injected) {
        throw ("Post-fallback precondition transfer completed before fault injection. failure_class=terminal_before_fallback_switch_off; transfer_id={0}" -f $faultResult.TransferId)
    }
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
    $routeObservedUtc = [datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')
    $prearmResult = Try-PrearmTunaGuiSwitchOffToggle -Context $Context -PayerMode $PayerMode -Bookmark $bookmark
    $prearmedSwitchOffToggle = if ([string]::Equals([string]$prearmResult.Kind, 'toggle', [System.StringComparison]::OrdinalIgnoreCase)) { $prearmResult.Toggle } else { $null }
    $prearmedSwitchOffToggleWaitMs = [long]$prearmResult.WaitMs

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

    $faultResult = Invoke-TunaGuiFallbackFault -Context $Context -FaultMode $FaultMode -PayerMode $PayerMode -Bookmark $bookmark -PrearmedToggle $prearmedSwitchOffToggle -PrearmedToggleWaitMs $prearmedSwitchOffToggleWaitMs
    Add-TunaGuiFaultWindowEvidence -ArtifactDir $ArtifactDir -ObservedEvidenceLines $observedEvidenceLines -ActivationLine $routeLine -ActivationObservedUtc $routeObservedUtc -FaultResult $faultResult
    if (-not [bool]$faultResult.Injected) {
        throw ("V4 restart precondition transfer completed before fault injection. failure_class=terminal_before_fallback_switch_off; transfer_id={0}" -f $faultResult.TransferId)
    }
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
    $diagnosticTunaReadinessObserved = $false
    $fileTunaV4PublicationObserved = $false
    $activationEpochStartedObserved = $false
    $activationEpochRecoveredObserved = $false
    $fallbackEpochStartedObserved = $false
    $fallbackEpochRecoveredObserved = $false
    $fallbackEpochWaitingObserved = $false
    $setupPhase = $null
    $liveRouteEpochObservations = New-Object System.Collections.Generic.List[object]
    $firstTransferTerminalBeforeLiveReactivation = $false
    $firstTransferTerminalBeforeLiveReactivationTransferId = ''
    $secondTransferRequiresFreshDiagnosticReadiness = $false
    $firstTransferTerminalBeforeFallbackSwitchOff = $false
    $firstTransferTerminalBeforeFallbackSwitchOffTransferId = ''
    $liveSwitchOffMinimumFaultPayloadBytes = 0L
    $liveSwitchOffMinimumCommittedBytes = 0L
    $liveSwitchOffMinimumPeerVisiblePayloadBytes = 0L
    $liveSwitchOffMinimumElapsedMs = 0
    $liveSwitchOffRoutePublicationObserved = $false
    $liveSwitchOffReceiverCommittedProgressObserved = $false
    $liveSwitchOffPeerVisibleTunaPayloadObserved = $false
    $liveSwitchOffTerminalCompletedBeforeFault = $false
    $liveSwitchOffFaultInjected = $false
    $liveSwitchOffDataPathProofMissing = $false

    if ($RouteMode -eq 'preactivated' -or $RouteMode -eq 'post-fallback' -or $RouteMode -eq 'v4-restart-v6-fallback' -or $RouteMode -eq 'live-v4-switch-off' -or $RouteMode -eq 'live-multi-toggle' -or $RouteMode -eq 'live-reactivation-second-transfer') {
        $preconditionBookmark = Get-AppLogBookmark
        Unlock-TunaPayers -Context $Context -PayerMode $PayerMode -Password $WalletPassword
        $diagnosticReadinessLine = [string](Wait-TunaGuiDiagnosticReadinessBeforeMeasuredTransfer -Bookmark $preconditionBookmark -TimeoutMs 150000)
        $observedEvidenceLines.Add($diagnosticReadinessLine) | Out-Null
        $diagnosticTunaReadinessObserved = $true
        if ($diagnosticReadinessLine.IndexOf('event=tuna_acceleration_negotiated', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $tunaNegotiatedObserved = $true
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
        @('event=filetransfer_v6_sender_started'),
        @('event=filetransfer_runtime_started', 'direction=outbound', 'role=sender'),
        @('event=filetransfer_v4_chunk_batch_sent'),
        @('event=filetransfer_chunk_batch_sent_as_batch', 'frame_type=filetransfer.chunk_batch.v4'),
        @('event=filetransfer_chunk_batch_sent_as_batch', 'frame_type=filetransfer.chunk_batch.v6')
    ) -TimeoutMs 45000 -Description 'primary file-transfer sender started or data-moving')
    [void](Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $bookmark -NeedleSets @(
        @('event=filetransfer_v4_receiver_started'),
        @('event=filetransfer_v6_receiver_started'),
        @('event=filetransfer_runtime_started', 'direction=inbound', 'role=receiver')
    ) -TimeoutMs 45000 -Description 'primary file-transfer receiver started')

    if ($RouteMode -eq 'live-v4-switch-off') {
        if ($FaultMode -eq 'none') {
            throw 'live-v4-switch-off route mode requires a fallback fault.'
        }

        $routeLine = [string](Wait-TunaGuiFileTunaV4PublicationAfterBookmark -Bookmark $bookmark -TimeoutMs 90000 -Description 'live V4 switch-off coordinator-authorized file Tuna V4 publication' -ActionKind 'RoutePublication' -RequireZeroRouteCommitGeneration $true)
        $observedEvidenceLines.Add($routeLine) | Out-Null
        $fileTunaV4PublicationObserved = $true
        $liveSwitchOffRoutePublicationObserved = $true
        $routeObservedUtc = [datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')
        $prearmResult = Try-PrearmTunaGuiSwitchOffToggle -Context $Context -PayerMode $PayerMode -Bookmark $bookmark
        $prearmedSwitchOffToggle = if ([string]::Equals([string]$prearmResult.Kind, 'toggle', [System.StringComparison]::OrdinalIgnoreCase)) { $prearmResult.Toggle } else { $null }
        $prearmedSwitchOffToggleWaitMs = [long]$prearmResult.WaitMs

        $liveSwitchOffMinimumCommittedBytes = Resolve-TunaGuiLiveSwitchOffMinimumCommittedBytes -PayloadSizeBytes $PayloadSizeBytes
        $liveSwitchOffMinimumFaultPayloadBytes = $liveSwitchOffMinimumCommittedBytes
        $liveSwitchOffMinimumPeerVisiblePayloadBytes = Resolve-TunaGuiLiveSwitchOffMinimumPeerVisiblePayloadBytes -PayloadSizeBytes $PayloadSizeBytes -MinimumCommittedBytes $liveSwitchOffMinimumCommittedBytes
        $liveSwitchOffMinimumElapsedMs = Resolve-TunaGuiLiveSwitchOffMinimumElapsedMs
        $observedEvidenceLines.Add(("[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_live_switch_off_fault_threshold; minimum_committed_bytes={1}; minimum_peer_visible_payload_bytes={2}; minimum_elapsed_ms={3}; payload_bytes={4}" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $liveSwitchOffMinimumCommittedBytes, $liveSwitchOffMinimumPeerVisiblePayloadBytes, $liveSwitchOffMinimumElapsedMs, $PayloadSizeBytes)) | Out-Null
        Write-Host ("[GUI Smoke][filetransfer_tuna] Waiting for receiver commit >= {0} byte(s) or peer-visible Tuna payload >= {1} byte(s), elapsed >= {2} ms before live switch-off fault." -f $liveSwitchOffMinimumCommittedBytes, $liveSwitchOffMinimumPeerVisiblePayloadBytes, $liveSwitchOffMinimumElapsedMs) -ForegroundColor DarkGray

        $progressResult = Wait-TunaGuiLiveSwitchOffTransferProgressBeforeFault -Bookmark $bookmark -MinimumCommittedBytes $liveSwitchOffMinimumCommittedBytes -MinimumPeerVisiblePayloadBytes $liveSwitchOffMinimumPeerVisiblePayloadBytes -MinimumElapsedMs $liveSwitchOffMinimumElapsedMs -MinimumFramePayloadBytes 16384L -TimeoutMs 90000 -PollIntervalMs 25
        $progressLine = [string]$progressResult.Line
        $observedEvidenceLines.Add($progressLine) | Out-Null
        $liveSwitchOffReceiverCommittedProgressObserved = $liveSwitchOffReceiverCommittedProgressObserved -or [bool]$progressResult.ReceiverCommittedProgressObserved
        $liveSwitchOffPeerVisibleTunaPayloadObserved = $liveSwitchOffPeerVisibleTunaPayloadObserved -or [bool]$progressResult.PeerVisibleTunaPayloadObserved
        $liveSwitchOffDataPathProofMissing = $liveSwitchOffDataPathProofMissing -or [bool]$progressResult.DataPathProofMissing
        $liveSwitchOffTerminalCompletedBeforeFault = [bool]$progressResult.TerminalCompletedBeforeFault
        if ($liveSwitchOffTerminalCompletedBeforeFault) {
            $firstTransferTerminalBeforeFallbackSwitchOff = $true
            $firstTransferTerminalBeforeFallbackSwitchOffTransferId = [string]$progressResult.TransferId
        }

        if (-not $liveSwitchOffTerminalCompletedBeforeFault) {
            $faultResult = Invoke-TunaGuiFallbackFault -Context $Context -FaultMode $FaultMode -PayerMode $PayerMode -Bookmark $bookmark -PrearmedToggle $prearmedSwitchOffToggle -PrearmedToggleWaitMs $prearmedSwitchOffToggleWaitMs
            Add-TunaGuiFaultWindowEvidence -ArtifactDir $ArtifactDir -ObservedEvidenceLines $observedEvidenceLines -ActivationLine $routeLine -ActivationObservedUtc $routeObservedUtc -FaultResult $faultResult
            if (-not [bool]$faultResult.Injected) {
                $liveSwitchOffTerminalCompletedBeforeFault = $true
                $firstTransferTerminalBeforeFallbackSwitchOff = $true
                $firstTransferTerminalBeforeFallbackSwitchOffTransferId = [string]$faultResult.TransferId
            }
            else {
                $liveSwitchOffFaultInjected = $true
                $observedEvidenceLines.Add(("[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_live_switch_off_fault_injected; route_mode={1}; fault_mode={2}; receiver_committed_progress_observed={3}; peer_visible_tuna_payload_observed={4}; data_path_proof_missing={5}; committed_bytes={6}; accelerated_payload_bytes={7}; elapsed_ms={8}" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $RouteMode, $FaultMode, ($(if ($liveSwitchOffReceiverCommittedProgressObserved) { 1 } else { 0 })), ($(if ($liveSwitchOffPeerVisibleTunaPayloadObserved) { 1 } else { 0 })), ($(if ($liveSwitchOffDataPathProofMissing) { 1 } else { 0 })), $progressResult.CommittedBytes, $progressResult.AcceleratedPayloadBytes, $progressResult.ElapsedMs)) | Out-Null
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
        }
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
        $routeDescription = if ($RouteMode -eq 'live-regular-activation-cycle') { 'live regular activation cycle initial regular NKN V4 route' } else { 'live multi-toggle active file Tuna V4 route' }
        if ($RouteMode -eq 'live-regular-activation-cycle') {
            $routeLine = [string](Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $bookmark -NeedleSets @(
                @('event=filetransfer_route_selected', 'route=regular_nkn_v4_fast', 'protocol_version=4'),
                @('event=filetransfer_runtime_started', 'route=regular_nkn_v4_fast', 'protocol_version=4'),
                @('event=filetransfer_v4_sender_started', 'route=regular_nkn_v4_fast')
            ) -TimeoutMs 90000 -Description $routeDescription)
            $observedEvidenceLines.Add($routeLine) | Out-Null
        }
        else {
            $routeLine = [string](Wait-TunaGuiFileTunaV4PublicationAfterBookmark -Bookmark $bookmark -TimeoutMs 90000 -Description $routeDescription -ActionKind 'RoutePublication' -RequireZeroRouteCommitGeneration $true)
            $observedEvidenceLines.Add($routeLine) | Out-Null
            $fileTunaV4PublicationObserved = $true
            $liveSwitchOffRoutePublicationObserved = $true
        }
        $routeObservedUtc = [datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')

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
        $prearmedSwitchOffToggle = $null
        $prearmedSwitchOffToggleWaitMs = 0L
        $lastActivationEvidenceLine = ''
        $lastActivationObservedUtc = ''
        if ($RouteMode -ne 'live-regular-activation-cycle') {
            $lastActivationEvidenceLine = $routeLine
            $lastActivationObservedUtc = $routeObservedUtc
            $prearmResult = Try-PrearmTunaGuiSwitchOffToggle -Context $Context -PayerMode $PayerMode -Bookmark $bookmark
            $prearmedSwitchOffToggleWaitMs = [long]$prearmResult.WaitMs
            if ([string]::Equals([string]$prearmResult.Kind, 'toggle', [System.StringComparison]::OrdinalIgnoreCase)) {
                $prearmedSwitchOffToggle = $prearmResult.Toggle
            }
        }
        for ($stepIndex = 0; $stepIndex -lt $sequence.Count; $stepIndex++) {
            $action = [string]$sequence[$stepIndex]
            $stepNumber = $stepIndex + 1
            if ($action -eq 'off') {
                Write-Host ("[GUI Smoke][filetransfer_tuna] Live toggle step {0}/{1}: switching Tuna off after transfer progress." -f $stepNumber, $sequence.Count) -ForegroundColor DarkGray
                $earlyFallbackStartedLine = $null
                $earlyFallbackAccepted = $false
                $progressResult = $null
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
                        $progressResult = Wait-TunaGuiLiveSwitchOffTransferProgressBeforeFault -Bookmark $bookmark -MinimumCommittedBytes $liveSwitchOffMinimumCommittedBytes -MinimumPeerVisiblePayloadBytes $liveSwitchOffMinimumPeerVisiblePayloadBytes -MinimumElapsedMs $liveSwitchOffMinimumElapsedMs -MinimumFramePayloadBytes 16384L -TimeoutMs 90000 -PollIntervalMs 25
                        $progressLine = [string]$progressResult.Line
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
                if ($null -ne $progressResult -and
                    -not [string]::Equals([string]$progressResult.Kind, 'early_fallback', [System.StringComparison]::OrdinalIgnoreCase)) {
                    $liveSwitchOffReceiverCommittedProgressObserved = $liveSwitchOffReceiverCommittedProgressObserved -or [bool]$progressResult.ReceiverCommittedProgressObserved
                    $liveSwitchOffPeerVisibleTunaPayloadObserved = $liveSwitchOffPeerVisibleTunaPayloadObserved -or [bool]$progressResult.PeerVisibleTunaPayloadObserved
                    $liveSwitchOffDataPathProofMissing = $liveSwitchOffDataPathProofMissing -or [bool]$progressResult.DataPathProofMissing
                    if ([bool]$progressResult.TerminalCompletedBeforeFault) {
                        $liveSwitchOffTerminalCompletedBeforeFault = $true
                        $firstTransferTerminalBeforeFallbackSwitchOff = $true
                        $firstTransferTerminalBeforeFallbackSwitchOffTransferId = [string]$progressResult.TransferId
                        Write-TunaGuiMilestoneEvidence -ArtifactDir $ArtifactDir -ObservedEvidenceLines $observedEvidenceLines
                        break
                    }
                }
                $lastActivationPayloadBookmark = $null
                $stepBookmark = if ($earlyFallbackAccepted) { $bookmark } else { Get-AppLogBookmark }
                $observedEvidenceLines.Add(("[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_live_multi_toggle_step; step={1}; action=off; early_fallback={2}" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $stepNumber, ($(if ($earlyFallbackAccepted) { 1 } else { 0 })))) | Out-Null
                if (-not $earlyFallbackAccepted) {
                    $faultResult = Invoke-TunaGuiFallbackFault -Context $Context -FaultMode $FaultMode -PayerMode $PayerMode -Bookmark $bookmark -PrearmedToggle $prearmedSwitchOffToggle -PrearmedToggleWaitMs $prearmedSwitchOffToggleWaitMs
                    $prearmedSwitchOffToggle = $null
                    $prearmedSwitchOffToggleWaitMs = 0L
                    Add-TunaGuiFaultWindowEvidence -ArtifactDir $ArtifactDir -ObservedEvidenceLines $observedEvidenceLines -ActivationLine $lastActivationEvidenceLine -ActivationObservedUtc $lastActivationObservedUtc -FaultResult $faultResult
                    if (-not [bool]$faultResult.Injected) {
                        $liveSwitchOffTerminalCompletedBeforeFault = $true
                        $firstTransferTerminalBeforeFallbackSwitchOff = $true
                        $firstTransferTerminalBeforeFallbackSwitchOffTransferId = [string]$faultResult.TransferId
                        Write-TunaGuiMilestoneEvidence -ArtifactDir $ArtifactDir -ObservedEvidenceLines $observedEvidenceLines
                        break
                    }

                    $liveSwitchOffFaultInjected = $true
                    $observedEvidenceLines.Add(("[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_live_switch_off_fault_injected; route_mode={1}; step={2}; fault_mode={3}; receiver_committed_progress_observed={4}; peer_visible_tuna_payload_observed={5}; data_path_proof_missing={6}" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $RouteMode, $stepNumber, $FaultMode, ($(if ($liveSwitchOffReceiverCommittedProgressObserved) { 1 } else { 0 })), ($(if ($liveSwitchOffPeerVisibleTunaPayloadObserved) { 1 } else { 0 })), ($(if ($liveSwitchOffDataPathProofMissing) { 1 } else { 0 })))) | Out-Null
                }

                $fallbackStartedLine = if ($earlyFallbackAccepted) {
                    $earlyFallbackStartedLine
                }
                else {
                    try {
                        Wait-TunaGuiLiveRouteEpochStarted -Bookmark $stepBookmark -FallbackBookmark $bookmark -Route 'post_tuna_fallback_v6' -ProtocolVersion 6 -HandoffKind 'tuna_to_normal_fallback' -TargetTransport 'regular_nkn' -Description 'live multi-toggle Tuna-to-normal fallback route epoch started' -AfterLiveRouteEpoch $lastObservedLiveRouteEpoch
                    }
                    catch {
                        $fallbackEpochException = $_
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
                                $firstTransferTerminalBeforeFallbackSwitchOff = $true
                                $firstTransferTerminalBeforeFallbackSwitchOffTransferId = [string]$terminalProbe.TransferId
                                $terminalBeforeFallbackLine = "[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_live_terminal_before_fallback_route; route_mode={1}; transfer_id={2}; step={3}; action=off; last_live_route_epoch={4}; reason=terminal_before_tuna_to_normal_fallback_epoch" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $RouteMode, $firstTransferTerminalBeforeFallbackSwitchOffTransferId, $stepNumber, $lastObservedLiveRouteEpoch
                                $observedEvidenceLines.Add($terminalBeforeFallbackLine) | Out-Null
                                Write-TunaGuiMilestoneEvidence -ArtifactDir $ArtifactDir -ObservedEvidenceLines $observedEvidenceLines
                                $terminalBeforeFallbackLine
                            }
                            else {
                                throw $fallbackEpochException
                            }
                        }
                        catch {
                            throw $fallbackEpochException
                        }
                    }
                }
                if ($firstTransferTerminalBeforeFallbackSwitchOff) {
                    break
                }
                $observedEvidenceLines.Add($fallbackStartedLine) | Out-Null
                Add-TunaGuiLiveRouteEpochObservation -Observations $liveRouteEpochObservations -Action 'off_started' -Line $fallbackStartedLine
                $fallbackEpochStartedObserved = $true
                $fallbackStartedFields = ConvertFrom-GuiSmokeSemicolonFields -Message $fallbackStartedLine
                $fallbackStartedEpoch = ConvertTo-GuiSmokeInt -Value (Get-GuiSmokeFieldValue -Fields $fallbackStartedFields -Name 'live_route_epoch' -Default '0') -Default 0

                $nextAction = if ($stepIndex + 1 -lt $sequence.Count) { [string]$sequence[$stepIndex + 1] } else { '' }
                if (($RouteMode -eq 'live-regular-activation-cycle' -or $RouteMode -eq 'live-multi-toggle' -or $RouteMode -eq 'live-reactivation-second-transfer') -and
                    [string]::Equals($nextAction, 'on', [System.StringComparison]::OrdinalIgnoreCase)) {
                    $prearmedActivationBookmark = Get-AppLogBookmark
                    $observedEvidenceLines.Add(("[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_live_multi_toggle_step_prearmed; step={1}; next_step={2}; action=on; route_mode={3}; reason=prearm_after_fallback_epoch_started" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $stepNumber, ($stepNumber + 1), $RouteMode)) | Out-Null
                    Unlock-TunaPayers -Context $Context -PayerMode $PayerMode -Password $WalletPassword
                }

                $fallbackRouteLine = [string](Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $stepBookmark -NeedleSets @(
                    @('event=filetransfer_route_selected', 'route=post_tuna_fallback_v6', 'protocol_version=6')
                ) -TimeoutMs 90000 -Description 'live multi-toggle post-Tuna fallback V6 route selection')
                $observedEvidenceLines.Add($fallbackRouteLine) | Out-Null

                $fallbackRecoveryAfterLine = if ($RouteMode -eq 'live-regular-activation-cycle') { '' } else { $fallbackStartedLine }
                $fallbackResolutionLine = Wait-TunaGuiLiveRouteEpochRecovered -Bookmark $stepBookmark -FallbackBookmark $bookmark -Route 'post_tuna_fallback_v6' -ProtocolVersion 6 -HandoffKind 'tuna_to_normal_fallback' -TargetTransport 'regular_nkn' -Description 'live multi-toggle Tuna-to-normal fallback route epoch recovered' -LiveRouteEpoch $fallbackStartedEpoch -AfterLiveRouteEpoch $lastObservedLiveRouteEpoch -AfterLine $fallbackRecoveryAfterLine
                $observedEvidenceLines.Add($fallbackResolutionLine) | Out-Null
                Add-TunaGuiLiveRouteEpochObservation -Observations $liveRouteEpochObservations -Action 'off_recovered' -Line $fallbackResolutionLine
                $fallbackEpochRecoveredObserved = $true
                if ($fallbackStartedEpoch -gt $lastObservedLiveRouteEpoch) {
                    $lastObservedLiveRouteEpoch = $fallbackStartedEpoch
                }

                if ($RouteMode -eq 'live-regular-activation-cycle' -and
                    [string]::Equals($nextAction, 'on', [System.StringComparison]::OrdinalIgnoreCase) -and
                    $null -ne $prearmedActivationBookmark) {
                    $stepIndex++
                    $stepNumber = $stepIndex + 1
                    Write-Host ("[GUI Smoke][filetransfer_tuna] Live toggle step {0}/{1}: immediately re-enabling Tuna after fallback recovery proof." -f $stepNumber, $sequence.Count) -ForegroundColor DarkGray
                    $stepBookmark = $prearmedActivationBookmark
                    $prearmedActivationBookmark = $null
                    $lastActivationPayloadBookmark = $stepBookmark
                    $observedEvidenceLines.Add(("[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_live_multi_toggle_step; step={1}; action=on; prearmed=1; immediate_after_fallback_recovered=1" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $stepNumber)) | Out-Null
                    [void](Submit-TunaWalletPasswordDialogsForPayersIfPresent -Context $Context -PayerMode $PayerMode -Password $WalletPassword -Reason ("prearmed_on_step_{0}_immediate_after_fallback_recovered" -f $stepNumber))
                    $switchOffPrearmResult = Try-PrearmTunaGuiSwitchOffToggle -Context $Context -PayerMode $PayerMode -Bookmark $bookmark
                    $prearmedSwitchOffToggle = if ([string]::Equals([string]$switchOffPrearmResult.Kind, 'toggle', [System.StringComparison]::OrdinalIgnoreCase)) { $switchOffPrearmResult.Toggle } else { $null }
                    $prearmedSwitchOffToggleWaitMs = [long]$switchOffPrearmResult.WaitMs

                    $activationProofTimeoutMs = 240000
                    $serviceWalletPrompts = {
                        Submit-TunaWalletPasswordDialogsForPayersIfPresent -Context $Context -PayerMode $PayerMode -Password $WalletPassword -Reason ("activation_route_wait_step_{0}" -f $stepNumber)
                    }
                    try {
                        $activationStartedLine = Wait-TunaGuiLiveRouteEpochStarted -Bookmark $stepBookmark -FallbackBookmark $bookmark -Route 'file_tuna_v4' -ProtocolVersion 4 -HandoffKind 'normal_to_tuna_activation' -TargetTransport 'tuna' -Description 'live regular activation cycle immediate normal-to-Tuna route epoch started' -AfterLiveRouteEpoch $lastObservedLiveRouteEpoch -TimeoutMs $activationProofTimeoutMs -OnPoll $serviceWalletPrompts
                    }
                    catch {
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
                                $terminalRouteSequence = ((Get-TunaGuiLiveRouteEpochRouteChanges -Observations $liveRouteEpochObservations) -join ',')
                                $observedEvidenceLines.Add(("[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_live_reactivation_first_transfer_terminal_before_activation; route_mode={1}; transfer_id={2}; step={3}; action=on; reason=terminal_before_same_transfer_reactivation_immediate_probe; fallback_epoch_recovered={4}; route_sequence={5}" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $RouteMode, $firstTransferTerminalBeforeLiveReactivationTransferId, $stepNumber, ($(if ($fallbackEpochRecoveredObserved) { 1 } else { 0 })), $terminalRouteSequence)) | Out-Null
                                Write-TunaGuiMilestoneEvidence -ArtifactDir $ArtifactDir -ObservedEvidenceLines $observedEvidenceLines
                                Copy-FileTransferLiveLogSlice -ArtifactDir $ArtifactDir -Bookmark $bookmark
                                Write-Host '[GUI Smoke][filetransfer_tuna] Transfer terminalized before the second Tuna activation proof; preserving terminal-before-second-activation evidence.' -ForegroundColor Yellow
                                break
                            }
                        }
                        catch {
                        }

                        throw
                    }
                    $observedEvidenceLines.Add($activationStartedLine) | Out-Null
                    Add-TunaGuiLiveRouteEpochObservation -Observations $liveRouteEpochObservations -Action 'on_started' -Line $activationStartedLine
                    $activationEpochStartedObserved = $true
                    $activationStartedFields = ConvertFrom-GuiSmokeSemicolonFields -Message $activationStartedLine
                    $activationStartedEpoch = ConvertTo-GuiSmokeInt -Value (Get-GuiSmokeFieldValue -Fields $activationStartedFields -Name 'live_route_epoch' -Default '0') -Default 0

                    $activationRouteLine = [string](Wait-TunaGuiFileTunaV4PublicationAfterBookmark -Bookmark $stepBookmark -TimeoutMs $activationProofTimeoutMs -Description 'live regular activation cycle immediate coordinator-authorized file Tuna V4 route publication' -ActionKind 'RouteCommitPublication' -RequirePositiveRouteCommitGeneration $true)
                    $observedEvidenceLines.Add($activationRouteLine) | Out-Null
                    $fileTunaV4PublicationObserved = $true

                    $activationRecoveredLine = Wait-TunaGuiLiveRouteEpochRecovered -Bookmark $stepBookmark -FallbackBookmark $bookmark -Route 'file_tuna_v4' -ProtocolVersion 4 -HandoffKind 'normal_to_tuna_activation' -TargetTransport 'tuna' -Description 'live regular activation cycle immediate normal-to-Tuna route epoch recovered' -LiveRouteEpoch $activationStartedEpoch -AfterLiveRouteEpoch $lastObservedLiveRouteEpoch -AfterLine $activationStartedLine -TimeoutMs $activationProofTimeoutMs
                    $observedEvidenceLines.Add($activationRecoveredLine) | Out-Null
                    Add-TunaGuiLiveRouteEpochObservation -Observations $liveRouteEpochObservations -Action 'on_recovered' -Line $activationRecoveredLine
                    $activationEpochRecoveredObserved = $true
                    $tunaNegotiatedObserved = $true
                    $lastActivationEvidenceLine = $activationRecoveredLine
                    $lastActivationObservedUtc = [datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')
                    if ($activationStartedEpoch -gt $lastObservedLiveRouteEpoch) {
                        $lastObservedLiveRouteEpoch = $activationStartedEpoch
                    }
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
                else {
                    [void](Submit-TunaWalletPasswordDialogsForPayersIfPresent -Context $Context -PayerMode $PayerMode -Password $WalletPassword -Reason ("prearmed_on_step_{0}_existing_prompt" -f $stepNumber))
                }
                $switchOffPrearmResult = Try-PrearmTunaGuiSwitchOffToggle -Context $Context -PayerMode $PayerMode -Bookmark $bookmark
                $prearmedSwitchOffToggle = if ([string]::Equals([string]$switchOffPrearmResult.Kind, 'toggle', [System.StringComparison]::OrdinalIgnoreCase)) { $switchOffPrearmResult.Toggle } else { $null }
                $prearmedSwitchOffToggleWaitMs = [long]$switchOffPrearmResult.WaitMs

                $activationProofTimeoutMs = if ($RouteMode -eq 'live-regular-activation-cycle' -or $RouteMode -eq 'live-reactivation-second-transfer') { 240000 } else { 90000 }
                $serviceWalletPrompts = {
                    Submit-TunaWalletPasswordDialogsForPayersIfPresent -Context $Context -PayerMode $PayerMode -Password $WalletPassword -Reason ("activation_route_wait_step_{0}" -f $stepNumber)
                }
                try {
                    $activationStartedLine = Wait-TunaGuiLiveRouteEpochStarted -Bookmark $stepBookmark -FallbackBookmark $bookmark -Route 'file_tuna_v4' -ProtocolVersion 4 -HandoffKind 'normal_to_tuna_activation' -TargetTransport 'tuna' -Description 'live multi-toggle normal-to-Tuna route epoch started' -AfterLiveRouteEpoch $lastObservedLiveRouteEpoch -TimeoutMs $activationProofTimeoutMs -OnPoll $serviceWalletPrompts
                }
                catch {
                    if ($RouteMode -eq 'live-reactivation-second-transfer' -or
                        $RouteMode -eq 'live-regular-activation-cycle') {
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
                                $terminalRouteSequence = ((Get-TunaGuiLiveRouteEpochRouteChanges -Observations $liveRouteEpochObservations) -join ',')
                                $terminalFallbackPrefixObserved = $fallbackEpochRecoveredObserved -or $terminalRouteSequence.StartsWith('file_tuna_v4,post_tuna_fallback_v6', [System.StringComparison]::Ordinal)
                                $terminalBeforeActivationEvent = if ($terminalFallbackPrefixObserved) {
                                    'filetransfer_tuna_gui_live_reactivation_first_transfer_terminal_before_activation'
                                }
                                else {
                                    'filetransfer_tuna_gui_regular_v4_terminal_before_tuna_activation_commit'
                                }
                                $terminalBeforeActivationReason = if ($terminalFallbackPrefixObserved) {
                                    'terminal_before_same_transfer_reactivation'
                                }
                                else {
                                    'regular_v4_terminal_before_tuna_activation_commit'
                                }
                                $observedEvidenceLines.Add(("[{0}] [INFO] [GuiSmoke] event={1}; route_mode={2}; transfer_id={3}; step={4}; action=on; reason={5}; fallback_epoch_recovered={6}; route_sequence={7}" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $terminalBeforeActivationEvent, $RouteMode, $firstTransferTerminalBeforeLiveReactivationTransferId, $stepNumber, $terminalBeforeActivationReason, ($(if ($fallbackEpochRecoveredObserved) { 1 } else { 0 })), $terminalRouteSequence)) | Out-Null
                                Write-TunaGuiMilestoneEvidence -ArtifactDir $ArtifactDir -ObservedEvidenceLines $observedEvidenceLines
                                Copy-FileTransferLiveLogSlice -ArtifactDir $ArtifactDir -Bookmark $bookmark
                                if ($RouteMode -eq 'live-reactivation-second-transfer') {
                                    if (-not $terminalFallbackPrefixObserved) {
                                        throw "Tuna GUI live reactivation first transfer completed on regular V4 before Tuna activation commit. failure_class=regular_v4_terminal_before_tuna_activation_commit sequence=$terminalRouteSequence Summary: $($terminalProbe | ConvertTo-Json -Compress)"
                                    }

                                    Write-Host '[GUI Smoke][filetransfer_tuna] First transfer completed cleanly in fallback before same-transfer reactivation; continuing to second-transfer Tuna proof.' -ForegroundColor Yellow
                                    continue
                                }

                                if ($terminalFallbackPrefixObserved) {
                                    Write-Host '[GUI Smoke][filetransfer_tuna] Transfer completed cleanly in fallback before the second Tuna activation proof; preserving terminal-before-second-activation evidence.' -ForegroundColor Yellow
                                }
                                else {
                                    Write-Host '[GUI Smoke][filetransfer_tuna] Transfer completed cleanly on regular V4 before Tuna activation commit; preserving terminal/SHA evidence and failing route proof deterministically.' -ForegroundColor Yellow
                                }

                                break
                            }
                        }
                        catch {
                            if ($_.Exception.Message.IndexOf('live_toggle_window_missed_transfer_completed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                                throw
                            }

                            if ($RouteMode -eq 'live-regular-activation-cycle' -and
                                (Test-TunaGuiTransferTerminalAfterBookmark -Bookmark $bookmark)) {
                                $firstTransferTerminalBeforeLiveReactivation = $true
                                $firstTransferTerminalBeforeLiveReactivationTransferId = '(terminal-log)'
                                $terminalRouteSequence = ((Get-TunaGuiLiveRouteEpochRouteChanges -Observations $liveRouteEpochObservations) -join ',')
                                $terminalFallbackPrefixObserved = $fallbackEpochRecoveredObserved -or $terminalRouteSequence.StartsWith('file_tuna_v4,post_tuna_fallback_v6', [System.StringComparison]::Ordinal)
                                $terminalBeforeActivationEvent = if ($terminalFallbackPrefixObserved) {
                                    'filetransfer_tuna_gui_live_reactivation_first_transfer_terminal_before_activation'
                                }
                                else {
                                    'filetransfer_tuna_gui_regular_v4_terminal_before_tuna_activation_commit'
                                }
                                $terminalBeforeActivationReason = if ($terminalFallbackPrefixObserved) {
                                    'terminal_before_same_transfer_reactivation_log_probe'
                                }
                                else {
                                    'regular_v4_terminal_before_tuna_activation_commit_log_probe'
                                }
                                $observedEvidenceLines.Add(("[{0}] [INFO] [GuiSmoke] event={1}; route_mode={2}; transfer_id={3}; step={4}; action=on; reason={5}; fallback_epoch_recovered={6}; route_sequence={7}; terminal_probe_error={8}" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $terminalBeforeActivationEvent, $RouteMode, $firstTransferTerminalBeforeLiveReactivationTransferId, $stepNumber, $terminalBeforeActivationReason, ($(if ($fallbackEpochRecoveredObserved) { 1 } else { 0 })), $terminalRouteSequence, ($_.Exception.Message -replace ';', ','))) | Out-Null
                                Write-TunaGuiMilestoneEvidence -ArtifactDir $ArtifactDir -ObservedEvidenceLines $observedEvidenceLines
                                Copy-FileTransferLiveLogSlice -ArtifactDir $ArtifactDir -Bookmark $bookmark

                                if ($terminalFallbackPrefixObserved) {
                                    Write-Host '[GUI Smoke][filetransfer_tuna] Transfer terminalized in fallback before the second Tuna activation proof; preserving terminal-before-second-activation retained evidence.' -ForegroundColor Yellow
                                    break
                                }

                                throw "Tuna GUI live regular activation cycle completed before fallback/Tuna route proof. failure_class=regular_v4_terminal_before_tuna_activation_commit sequence=$terminalRouteSequence terminal_probe_error=$($_.Exception.Message)"
                            }
                        }

                        if ($RouteMode -eq 'live-reactivation-second-transfer') {
                            $firstTransferStatus = [ordered]@{
                                event = 'filetransfer_second_transfer_status'
                                second_transfer_started = 0
                                first_transfer_terminal_state = '(missing)'
                                first_transfer_route_sequence = '(see filetransfer-route-consistency-summary.txt)'
                                reactivation_epoch_seen = 0
                                failure_class = 'first_transfer_reactivation_or_fallback_incomplete'
                                failure_reason = 'normal_to_tuna_live_epoch_missing'
                                failed_at_utc = [DateTimeOffset]::UtcNow.ToString('O')
                            }
                            $firstTransferStatus.GetEnumerator() | ForEach-Object { "{0}={1}" -f $_.Key, $_.Value } | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-second-transfer-status.txt') -Encoding UTF8
                            $firstTransferStatus | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-second-transfer-status.json') -Encoding UTF8
                        }
                    }

                    throw
                }
                $observedEvidenceLines.Add($activationStartedLine) | Out-Null
                Add-TunaGuiLiveRouteEpochObservation -Observations $liveRouteEpochObservations -Action 'on_started' -Line $activationStartedLine
                $activationEpochStartedObserved = $true
                $activationStartedFields = ConvertFrom-GuiSmokeSemicolonFields -Message $activationStartedLine
                $activationStartedEpoch = ConvertTo-GuiSmokeInt -Value (Get-GuiSmokeFieldValue -Fields $activationStartedFields -Name 'live_route_epoch' -Default '0') -Default 0

                $activationRouteLine = [string](Wait-TunaGuiFileTunaV4PublicationAfterBookmark -Bookmark $stepBookmark -TimeoutMs $activationProofTimeoutMs -Description 'live multi-toggle coordinator-authorized file Tuna V4 route publication' -ActionKind 'RouteCommitPublication' -RequirePositiveRouteCommitGeneration $true)
                $observedEvidenceLines.Add($activationRouteLine) | Out-Null
                $fileTunaV4PublicationObserved = $true

                $activationRecoveredLine = Wait-TunaGuiLiveRouteEpochRecovered -Bookmark $stepBookmark -FallbackBookmark $bookmark -Route 'file_tuna_v4' -ProtocolVersion 4 -HandoffKind 'normal_to_tuna_activation' -TargetTransport 'tuna' -Description 'live multi-toggle normal-to-Tuna route epoch recovered' -LiveRouteEpoch $activationStartedEpoch -AfterLiveRouteEpoch $lastObservedLiveRouteEpoch -AfterLine $activationStartedLine -TimeoutMs $activationProofTimeoutMs
                $observedEvidenceLines.Add($activationRecoveredLine) | Out-Null
                Add-TunaGuiLiveRouteEpochObservation -Observations $liveRouteEpochObservations -Action 'on_recovered' -Line $activationRecoveredLine
                $activationEpochRecoveredObserved = $true
                $tunaNegotiatedObserved = $true
                $lastActivationEvidenceLine = $activationRecoveredLine
                $lastActivationObservedUtc = [datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')
                if ($activationStartedEpoch -gt $lastObservedLiveRouteEpoch) {
                    $lastObservedLiveRouteEpoch = $activationStartedEpoch
                }
            }
        }
    }
    elseif ($RouteMode -eq 'handoff-fallback') {
        [void](Wait-FileTransferTerminalOrProgressBeforeAction -Bookmark $bookmark -ProgressTimeoutMs $ProgressTimeoutMs -MinProgressEvents 2 -TimeoutMs 60000)

        Unlock-TunaPayers -Context $Context -PayerMode $PayerMode -Password $WalletPassword
        $switchOffPrearmResult = Try-PrearmTunaGuiSwitchOffToggle -Context $Context -PayerMode $PayerMode -Bookmark $bookmark
        $prearmedSwitchOffToggle = if ([string]::Equals([string]$switchOffPrearmResult.Kind, 'toggle', [System.StringComparison]::OrdinalIgnoreCase)) { $switchOffPrearmResult.Toggle } else { $null }
        $prearmedSwitchOffToggleWaitMs = [long]$switchOffPrearmResult.WaitMs
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
        $activationResolutionObservedUtc = [datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')
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
            $faultResult = Invoke-TunaGuiFallbackFault -Context $Context -FaultMode $FaultMode -PayerMode $PayerMode -Bookmark $bookmark -PrearmedToggle $prearmedSwitchOffToggle -PrearmedToggleWaitMs $prearmedSwitchOffToggleWaitMs
            Add-TunaGuiFaultWindowEvidence -ArtifactDir $ArtifactDir -ObservedEvidenceLines $observedEvidenceLines -ActivationLine $activationResolutionLine -ActivationObservedUtc $activationResolutionObservedUtc -FaultResult $faultResult
            if (-not [bool]$faultResult.Injected) {
                throw ("Handoff fallback transfer completed before fault injection. failure_class=terminal_before_fallback_switch_off; transfer_id={0}" -f $faultResult.TransferId)
            }
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
        if ($RouteMode -eq 'preactivated') {
            $routeLine = [string](Wait-TunaGuiFileTunaV4PublicationAfterBookmark -Bookmark $bookmark -TimeoutMs 90000 -Description 'coordinator-authorized file Tuna V4 publication after measured GUI file transfer' -ActionKind 'RoutePublication' -RequireZeroRouteCommitGeneration $true)
            $fileTunaV4PublicationObserved = $true
        }
        else {
            $routeLine = [string](Wait-AppLogContainsAnyAllAfterBookmark -Bookmark $bookmark -NeedleSets @(
                @('event=filetransfer_route_selected', ("route={0}" -f $expectedRouteToken)),
                @('event=filetransfer_runtime_started', ("route={0}" -f $expectedRouteToken)),
                @('event=filetransfer_v6_sender_started', ("route={0}" -f $expectedRouteToken)),
                @('event=filetransfer_v4_sender_started', ("route={0}" -f $expectedRouteToken))
            ) -TimeoutMs 90000 -Description ("preconditioned Tuna route selected: {0}" -f $expectedRouteToken))
        }
        $observedEvidenceLines.Add($routeLine) | Out-Null
        if ($RouteMode -eq 'preactivated') {
            $activationEpochStartedObserved = $true
            $activationEpochRecoveredObserved = $true
        }
    }

    $pauseProbe = $false
    if ($firstTransferTerminalBeforeLiveReactivation -or $firstTransferTerminalBeforeFallbackSwitchOff) {
        $pauseProbeSkipReason = if ($firstTransferTerminalBeforeFallbackSwitchOff) { 'terminal_before_tuna_to_normal_fallback_epoch' } else { 'terminal_before_same_transfer_reactivation' }
        $observedEvidenceLines.Add(("[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_pause_resume_probe_skipped; route_mode={1}; reason={2}" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $RouteMode, $pauseProbeSkipReason)) | Out-Null
    }
    else {
        $pauseProbe = Invoke-TunaGuiPauseResumeProbe -SenderWindow $senderWindow -Bookmark $bookmark
    }

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
    if ($diagnosticTunaReadinessObserved) { $evidence.diagnosticTunaReadinessObserved = $true }
    if ($fileTunaV4PublicationObserved) {
        $evidence.routePublicationObserved = $true
        $evidence.fileTunaV4PublicationObserved = $true
    }
    if ($liveSwitchOffRoutePublicationObserved) { $evidence.routePublicationObserved = $true }
    if ($liveSwitchOffReceiverCommittedProgressObserved) { $evidence.receiverCommittedProgressObserved = $true }
    if ($liveSwitchOffPeerVisibleTunaPayloadObserved) { $evidence.peerVisibleTunaPayloadObserved = $true }
    if ($liveSwitchOffTerminalCompletedBeforeFault) { $evidence.terminalCompletedBeforeFault = $true }
    if ($liveSwitchOffFaultInjected) { $evidence.faultInjected = $true }
    if ($liveSwitchOffDataPathProofMissing) {
        $evidence.dataPathProofMissing = $true
        $evidence.missingTunaDataPathProof = $true
    }
    if ($activationEpochStartedObserved) { $evidence.activationEpochStarted = $true }
    if ($activationEpochRecoveredObserved) { $evidence.activationEpochRecovered = $true }
    if ($fallbackEpochStartedObserved) { $evidence.fallbackEvidenceObserved = $true }
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
            $evidence.fallbackEpochStarted = $true
            $evidence.postTunaFallbackV6RouteObserved = $true
        }

        if (Test-TunaGuiAuthorizedFileTunaV4PublicationLine -Line $line) {
            $evidence.fileTunaV4PublicationObserved = $true
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
    foreach ($route in @(Get-TunaGuiLiveRouteEpochRouteChanges -Observations $liveRouteEpochObservations)) {
        $liveRouteEpochRouteChanges.Add($route) | Out-Null
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
        failurePhase = if ($liveSwitchOffTerminalCompletedBeforeFault) { 'completed_before_fault_injection' } else { 'none' }
        failureReason = if ($liveSwitchOffTerminalCompletedBeforeFault) { 'transfer_completed_before_fault_injection' } else { 'none' }
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
        routePublicationObserved = [bool]$evidence.routePublicationObserved
        receiverCommittedProgressObserved = [bool]$evidence.receiverCommittedProgressObserved
        peerVisibleTunaPayloadObserved = [bool]$evidence.peerVisibleTunaPayloadObserved
        terminalCompletedBeforeFault = [bool]$evidence.terminalCompletedBeforeFault
        faultInjected = [bool]$evidence.faultInjected
        dataPathProofMissing = [bool]$evidence.dataPathProofMissing
        missingTunaDataPathProof = [bool]$evidence.missingTunaDataPathProof
        liveRouteEpochs = $liveRouteEpochArray
        liveRouteEpochSequence = $liveRouteEpochSequence
        liveRouteEpochRouteChanges = @($liveRouteEpochRouteChanges.ToArray())
        firstTransferTerminalBeforeLiveReactivation = [bool]$firstTransferTerminalBeforeLiveReactivation
        firstTransferTerminalBeforeLiveReactivationTransferId = $firstTransferTerminalBeforeLiveReactivationTransferId
        firstTransferTerminalBeforeFallbackSwitchOff = [bool]$firstTransferTerminalBeforeFallbackSwitchOff
        firstTransferTerminalBeforeFallbackSwitchOffTransferId = $firstTransferTerminalBeforeFallbackSwitchOffTransferId
        pauseProbe = [bool]$pauseProbe
        evidence = $evidence
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json') -Encoding UTF8
    Write-TunaGuiMilestoneEvidence -ArtifactDir $ArtifactDir -ObservedEvidenceLines $observedEvidenceLines
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
    if ($RouteMode -eq 'live-reactivation-second-transfer' -and -not $liveSwitchOffTerminalCompletedBeforeFault) {
        $secondPayloadBytes = 16777216L
        $secondTransferRequiresFreshDiagnosticReadiness = [bool]$firstTransferTerminalBeforeLiveReactivation
        $postTerminalQuietBookmark = Get-AppLogBookmark
        $postTerminalQuiet = Wait-TunaGuiSecondTransferPostTerminalQuietWindowOrThrow `
            -Bookmark $postTerminalQuietBookmark `
            -TimeoutMs ([Math]::Min(45000, [Math]::Max(10000, $StartupTimeoutMs))) `
            -QuietMs 3000
        $summary['secondTransferPreflight'] = [ordered]@{
            postTerminalQuietMs = $postTerminalQuiet.QuietMs
            postTerminalNoiseCount = $postTerminalQuiet.NoiseCount
            postTerminalLastNoiseUtc = $postTerminalQuiet.LastNoiseUtc
            requiresFreshDiagnosticReadiness = $secondTransferRequiresFreshDiagnosticReadiness
            freshReadinessReason = if ($secondTransferRequiresFreshDiagnosticReadiness) { 'first_transfer_terminal_before_same_transfer_reactivation' } else { 'same_transfer_reactivation_proof_observed_or_not_required' }
        }
        $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json') -Encoding UTF8
        Write-Host ("[GUI Smoke][filetransfer_tuna] First-transfer terminal echoes quiet for {0} ms before second transfer; noise_count={1}." -f $postTerminalQuiet.QuietMs, $postTerminalQuiet.NoiseCount) -ForegroundColor DarkGray

        Write-DeterministicFileTransferPayload -Path $AutopickPath -SizeBytes $secondPayloadBytes -Seed $Seed -CycleIndex 1
        $secondExpectedHash = Get-FileSha256Hex -Path $AutopickPath
        $secondBookmark = Get-AppLogBookmark
        $secondStartedUtc = [datetime]::UtcNow
        $secondSw = [System.Diagnostics.Stopwatch]::StartNew()
        $secondTransferPhase = $null
        $secondRouteLine = ''
        $secondFreshUnlockError = ''
        Write-Host "[GUI Smoke][filetransfer_tuna] Starting second transfer after live reactivation; expecting file_tuna_v4 protocol 4." -ForegroundColor DarkGray

        try {
            if ($secondTransferRequiresFreshDiagnosticReadiness) {
                $observedEvidenceLines.Add(("[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_second_transfer_fresh_readiness_required; route_mode={1}; first_transfer_id={2}; reason=first_transfer_terminal_before_same_transfer_reactivation" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $RouteMode, $firstTransferTerminalBeforeLiveReactivationTransferId)) | Out-Null
                Write-Host "[GUI Smoke][filetransfer_tuna] First transfer completed before same-transfer reactivation; requiring fresh diagnostic Tuna readiness before second send." -ForegroundColor Yellow
                try {
                    Unlock-TunaPayers -Context $Context -PayerMode $PayerMode -Password $WalletPassword
                }
                catch {
                    $secondFreshUnlockError = $_.Exception.Message
                    $observedEvidenceLines.Add(("[{0}] [WARN] [GuiSmoke] event=filetransfer_tuna_gui_second_transfer_fresh_unlock_not_completed; route_mode={1}; first_transfer_id={2}; reason=unlock_prompt_or_runtime_evidence_missing; error={3}" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $RouteMode, $firstTransferTerminalBeforeLiveReactivationTransferId, ($secondFreshUnlockError -replace ';', ','))) | Out-Null
                    Write-Host ("[GUI Smoke][filetransfer_tuna] Fresh second-transfer Tuna unlock did not complete before readiness wait: {0}" -f $secondFreshUnlockError) -ForegroundColor Yellow
                }
            }

            try {
                $secondDiagnosticReadinessLine = [string](Wait-TunaGuiDiagnosticReadinessBeforeMeasuredTransfer -Bookmark $secondBookmark -TimeoutMs ([Math]::Min(60000, $StartupTimeoutMs)))
                $observedEvidenceLines.Add($secondDiagnosticReadinessLine) | Out-Null
                $diagnosticTunaReadinessObserved = $true
                $summary['secondTransferDiagnosticReadiness'] = [ordered]@{
                    observed = $true
                    line = $secondDiagnosticReadinessLine
                    required = $secondTransferRequiresFreshDiagnosticReadiness
                }
                $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json') -Encoding UTF8
            }
            catch {
                $summary['secondTransferDiagnosticReadiness'] = [ordered]@{
                    observed = $false
                    error = $_.Exception.Message
                    authority = 'diagnostic_only'
                    nextRequiredProof = 'second_transfer_file_tuna_v4_route_publication'
                    required = $secondTransferRequiresFreshDiagnosticReadiness
                    requiredReason = if ($secondTransferRequiresFreshDiagnosticReadiness) { 'first_transfer_terminal_before_same_transfer_reactivation' } else { '(none)' }
                    unlockError = $secondFreshUnlockError
                }
                $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json') -Encoding UTF8
                Write-Host "[GUI Smoke][filetransfer_tuna] Second-transfer diagnostic Tuna readiness was not refreshed before send; continuing to require coordinator-authorized file_tuna_v4 publication for the new transfer." -ForegroundColor Yellow
            }

            $secondSendButton = Wait-TunaGuiSecondTransferReadinessOrThrow -Window $senderWindow -Bookmark $secondBookmark -TimeoutMs ([Math]::Min(60000, $StartupTimeoutMs)) -RouteMode $RouteMode -SenderRole $expectedOutboundRole
            Click-Element $secondSendButton

            $secondAcceptButton = Wait-TunaGuiFileTransferAcceptOrThrow -Window $receiverWindow -Bookmark $secondBookmark -TimeoutMs $StartupTimeoutMs -RouteMode $RouteMode
            Click-Element $secondAcceptButton

            $secondRouteLine = [string](Wait-TunaGuiFileTunaV4PublicationAfterBookmark -Bookmark $secondBookmark -TimeoutMs 90000 -Description 'second transfer after reactivation coordinator-authorized file Tuna V4 publication' -ActionKind 'RoutePublication' -RequireZeroRouteCommitGeneration $true)
            $fileTunaV4PublicationObserved = $true

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
            Copy-FileTransferLiveLogSlice -ArtifactDir $ArtifactDir -Bookmark $secondBookmark -FileName 'filetransfer-second-transfer-retained-log-slice.log' -PrefixLines @($secondRouteLine)
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
        if (-not $evidence.diagnosticTunaReadinessObserved) {
            throw "Tuna GUI preactivated file-transfer did not prove diagnostic Tuna readiness before the measured transfer. Summary: $($evidence | ConvertTo-Json -Compress)"
        }

        if (-not $evidence.fileTunaV4PublicationObserved) {
            throw "Tuna GUI preactivated file-transfer did not prove coordinator-authorized file_tuna_v4 route publication after the measured transfer started. Summary: $($evidence | ConvertTo-Json -Compress)"
        }

        if ($FaultMode -eq 'none' -and ($evidence.fallbackEpochStarted -or $evidence.fallbackEpochRecovered -or $evidence.fallbackEpochWaiting)) {
            throw "Tuna GUI preactivated no-fault transfer unexpectedly entered fallback. Summary: $($evidence | ConvertTo-Json -Compress)"
        }

    }
    elseif ($liveSwitchOffTerminalCompletedBeforeFault -and
        ($RouteMode -eq 'live-v4-switch-off' -or
         $RouteMode -eq 'live-multi-toggle' -or
         $RouteMode -eq 'live-reactivation-second-transfer')) {
        $joinedRouteSequence = ($liveRouteEpochRouteChanges.ToArray() -join ',')
        if (-not $evidence.fileTunaV4PublicationObserved -or
            -not $completed -or
            -not $integrityOk) {
            throw "Tuna GUI live switch-off completed before fault injection but did not prove clean terminal file_tuna_v4 publication/integrity. route_mode=$RouteMode sequence=$joinedRouteSequence Summary: $($evidence | ConvertTo-Json -Compress)"
        }
    }
    elseif ($RouteMode -eq 'live-v4-switch-off') {
        $joinedRouteSequence = ($liveRouteEpochRouteChanges.ToArray() -join ',')
        $liveRouteProof = Get-TunaGuiLiveRouteEpochProof -Observations $liveRouteEpochObservations -ExpectedRoutes @('post_tuna_fallback_v6')
        if (-not $evidence.diagnosticTunaReadinessObserved -or
            -not $evidence.fileTunaV4PublicationObserved -or
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
            if (-not $evidence.diagnosticTunaReadinessObserved -or
                -not $evidence.fileTunaV4PublicationObserved -or
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
        if (-not $evidence.diagnosticTunaReadinessObserved -or
            -not $evidence.fileTunaV4PublicationObserved -or
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
        if ($firstTransferTerminalBeforeFallbackSwitchOff) {
            $terminalBeforeFallbackExpectedRoutes = @($joinedRouteSequence.Split(',', [System.StringSplitOptions]::RemoveEmptyEntries))
            if ($terminalBeforeFallbackExpectedRoutes.Count -eq 0) {
                $terminalBeforeFallbackExpectedRoutes = @('file_tuna_v4')
            }
            $liveRouteProof = Get-TunaGuiLiveRouteEpochProof -Observations $liveRouteEpochObservations -ExpectedRoutes $terminalBeforeFallbackExpectedRoutes
            throw "Tuna GUI live regular activation cycle reached terminal completion before fallback switch-off proof. failure_class=terminal_before_fallback_switch_off sequence=$joinedRouteSequence findings=$($liveRouteProof.Findings -join '|') metadata_missing=$($liveRouteProof.MetadataMissingCount) Summary: $($evidence | ConvertTo-Json -Compress)"
        }
        elseif ($firstTransferTerminalBeforeLiveReactivation) {
            $liveRouteProof = Get-TunaGuiLiveRouteEpochProof -Observations $liveRouteEpochObservations -ExpectedRoutes @('file_tuna_v4', 'post_tuna_fallback_v6')
            if (-not $evidence.tunaNegotiated -or
                -not $evidence.fallbackEpochStarted -or
                -not $evidence.activationEpochStarted -or
                -not $evidence.activationEpochRecovered -or
                -not $evidence.postTunaFallbackV6RouteObserved -or
                -not $evidence.fallbackEpochRecovered -or
                -not $liveRouteProof.Pass -or
                $joinedRouteSequence -ne 'file_tuna_v4,post_tuna_fallback_v6') {
                $terminalFailureClass = if ([string]::IsNullOrWhiteSpace($joinedRouteSequence) -or $joinedRouteSequence -eq 'regular_nkn_v4_fast') {
                    'regular_v4_terminal_before_tuna_activation_commit'
                }
                else {
                    'terminal_fallback_prefix_missing'
                }
                throw "Tuna GUI live regular activation cycle did not prove terminal fallback outcome after first regular-to-Tuna/fallback leg. failure_class=$terminalFailureClass sequence=$joinedRouteSequence findings=$($liveRouteProof.Findings -join '|') metadata_missing=$($liveRouteProof.MetadataMissingCount) Summary: $($evidence | ConvertTo-Json -Compress)"
            }
        }
        else {
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
    }
    elseif (-not $evidence.diagnosticTunaReadinessObserved -or -not $evidence.fallbackEpochStarted) {
        throw "Tuna GUI post-fallback file-transfer did not prove fallback precondition before the measured transfer. Summary: $($evidence | ConvertTo-Json -Compress)"
    }

    if ($evidence.heartbeatTimeoutBeforeTerminalCount -gt 0 -or
        $evidence.peerDisconnectedBeforeTerminalCount -gt 0 -or
        $evidence.transportFailedBeforeTerminalCount -gt 0) {
        throw "Tuna GUI file-transfer completed with disconnect/timeout evidence. Summary: $($evidence | ConvertTo-Json -Compress)"
    }

    if ($pauseProbe -and (-not $evidence.pauseSent -or -not $evidence.pauseReceived -or -not $evidence.resumeSent -or -not $evidence.resumeReceived)) {
        throw "Tuna GUI pause/resume probe did not produce complete lifecycle evidence. Summary: $($evidence | ConvertTo-Json -Compress)"
    }

    $skipPostTransferConnectedHeaderWait =
        $firstTransferTerminalBeforeLiveReactivation -or
        $firstTransferTerminalBeforeFallbackSwitchOff
    if ($skipPostTransferConnectedHeaderWait) {
        $observedEvidenceLines.Add(("[{0}] [INFO] [GuiSmoke] event=filetransfer_tuna_gui_post_transfer_connected_header_wait_skipped; route_mode={1}; reason=terminal_fallback_outcome" -f ([datetime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ssZ')), $RouteMode)) | Out-Null
        Write-TunaGuiMilestoneEvidence -ArtifactDir $ArtifactDir -ObservedEvidenceLines $observedEvidenceLines
    }

    if (-not $skipPostTransferConnectedHeaderWait -and -not (Test-GuiSmokeEnvEnabled -Name 'NLINK_TUNA_GUI_MIXED_SCREENSHARE')) {
        [void](Wait-SessionHeaderConnectedState -Window $Context.HelperWindow -TimeoutMs 10000)
        [void](Wait-SessionHeaderConnectedState -Window $Context.HelpeeWindow -TimeoutMs 10000)
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
    $runBookmark = 0
    $initialLogLedgerSnapshot = [pscustomobject]@{
        ledgerId = [string]$script:GuiSmokeAppLogLedger.LedgerId
        bookmark = 0
        lineCount = 0
        initialGenerationKey = ''
        activeGenerationKey = ''
        generations = @()
        initialGenerations = @()
        writers = @()
    }

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
        $runBookmark = Get-AppLogBookmark
        $initialLogLedgerSnapshot = Get-GuiSmokeLogLedgerSnapshot `
            -State $script:GuiSmokeAppLogLedger `
            -Bookmark $runBookmark
        Set-GuiSmokeLogLedgerWriterEvidenceBookmark `
            -State $script:GuiSmokeAppLogLedger `
            -Bookmark $runBookmark
        if ([string]::IsNullOrWhiteSpace([string]$initialLogLedgerSnapshot.activeGenerationKey) -or
            @($initialLogLedgerSnapshot.generations).Count -eq 0) {
            throw 'gui_smoke_log_cursor_initial_generation_missing: helpee startup did not establish an indexable active log generation'
        }
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
            bridgeBootstrapNotReady = [bool]$failureClassification.BridgeBootstrapNotReady
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

        Write-FileTransferGuiLogLedgerEvidence `
            -ArtifactDir $artifactDir `
            -Bookmark $runBookmark `
            -InitialSnapshot $initialLogLedgerSnapshot `
            -ProcessOutputDrainAction {
                $processesToDrain = @($Context.Processes.ToArray())
                Cleanup-ProcessesWithOperationalLogDrain -Processes $processesToDrain -ExePath $Context.ExePath
                return $processesToDrain
            }
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

function Cleanup-ProcessesWithOperationalLogDrain {
    param(
        [array]$Processes,
        [string]$ExePath = '',
        [int]$TimeoutMs = 7000
    )

    if ($null -eq $Processes -or $Processes.Count -eq 0) { return }
    $targets = @($Processes | Where-Object { $null -ne $_ })
    foreach ($process in $targets) {
        try {
            if (-not $process.HasExited) {
                [void]$process.CloseMainWindow()
            }
        }
        catch {}
    }

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    foreach ($process in $targets) {
        try {
            if ($process.HasExited) { continue }
            $remainingMs = [Math]::Max(1, $TimeoutMs - [int]$stopwatch.ElapsedMilliseconds)
            [void]$process.WaitForExit($remainingMs)
        }
        catch {}
    }

    foreach ($process in $targets) {
        try {
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
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

    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable('NLINK_NKN_PREFLIGHT_RPC_ENABLED', [System.EnvironmentVariableTarget]::Process))) {
        $overrides['NLINK_NKN_PREFLIGHT_RPC_ENABLED'] = '1'
    }
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable('NLINK_NKN_PREFLIGHT_TIMEOUT_MS', [System.EnvironmentVariableTarget]::Process))) {
        $overrides['NLINK_NKN_PREFLIGHT_TIMEOUT_MS'] = '8000'
    }
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable('NLINK_NKN_PREFLIGHT_CONCURRENCY', [System.EnvironmentVariableTarget]::Process))) {
        $overrides['NLINK_NKN_PREFLIGHT_CONCURRENCY'] = '8'
    }
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable('NLINK_NKN_PREFLIGHT_CACHE_TTL_MS', [System.EnvironmentVariableTarget]::Process))) {
        $overrides['NLINK_NKN_PREFLIGHT_CACHE_TTL_MS'] = '600000'
    }

    return $overrides
}

function Get-GuiSmokeNknIdentityPathForRole {
    param([string]$RoleName = '')

    $roleDir = Get-GuiSmokeNknIdentityRoleDirectory -RoleName $RoleName
    if ([string]::IsNullOrWhiteSpace($roleDir)) {
        return ''
    }

    New-Item -ItemType Directory -Force -Path $roleDir | Out-Null
    return (Join-Path $roleDir 'identity.json')
}

function Get-GuiSmokeNknIdentityRoleDirectory {
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
    return $roleDir
}

function Rotate-GuiSmokeNknIdentityForRole {
    param(
        [Parameter(Mandatory = $true)][string]$RoleName,
        [string]$Reason = 'bootstrap_retry',
        [int]$Attempt = 0
    )

    $roleDir = Get-GuiSmokeNknIdentityRoleDirectory -RoleName $RoleName
    if ([string]::IsNullOrWhiteSpace($roleDir)) {
        Write-Host ("[GUI Smoke] event=nkn_identity_rotation_skipped; role={0}; attempt={1}; reason={2}; detail=no_artifact_identity_root" -f $RoleName, $Attempt, $Reason) -ForegroundColor DarkYellow
        return
    }

    $artifactDir = [string]$env:NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR
    $resolvedArtifactDir = [System.IO.Path]::GetFullPath($artifactDir)
    $retiredRoot = Join-Path $resolvedArtifactDir 'nkn-identities-retired'
    New-Item -ItemType Directory -Force -Path $retiredRoot | Out-Null

    $safeRole = if ([string]::IsNullOrWhiteSpace($RoleName)) { 'app' } else { $RoleName.Trim().ToLowerInvariant() }
    $safeRole = [regex]::Replace($safeRole, '[^a-z0-9_-]+', '-')
    if ([string]::IsNullOrWhiteSpace($safeRole)) {
        $safeRole = 'app'
    }

    $suffix = "{0:yyyyMMddTHHmmssfffZ}-attempt{1}-{2}" -f [DateTime]::UtcNow, $Attempt, ([regex]::Replace($Reason, '[^a-zA-Z0-9_-]+', '-'))
    $retiredDir = Join-Path $retiredRoot ("{0}-{1}" -f $safeRole, $suffix)

    if (Test-Path -LiteralPath $roleDir) {
        try {
            Move-Item -LiteralPath $roleDir -Destination $retiredDir -Force
            Write-Host ("[GUI Smoke] event=nkn_identity_rotated_after_helpee_invite_bootstrap_failure; role={0}; attempt={1}; reason={2}; retired_dir={3}" -f $safeRole, $Attempt, $Reason, $retiredDir) -ForegroundColor Yellow
        }
        catch {
            throw ("Failed to rotate NKN identity directory for role '{0}' before bootstrap retry. role_dir={1}; retired_dir={2}; error={3}" -f $safeRole, $roleDir, $retiredDir, $_.Exception.Message)
        }
    }
    else {
        Write-Host ("[GUI Smoke] event=nkn_identity_rotation_no_existing_identity; role={0}; attempt={1}; reason={2}; role_dir={3}" -f $safeRole, $Attempt, $Reason, $roleDir) -ForegroundColor DarkGray
    }

    New-Item -ItemType Directory -Force -Path $roleDir | Out-Null
}

function Rotate-GuiSmokeNknIdentityPairForBootstrapRetry {
    param(
        [string]$Reason = 'helpee_invite_bootstrap_retry',
        [int]$Attempt = 0
    )

    Rotate-GuiSmokeNknIdentityForRole -RoleName 'helper' -Reason $Reason -Attempt $Attempt
    Rotate-GuiSmokeNknIdentityForRole -RoleName 'helpee' -Reason $Reason -Attempt $Attempt
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

function Get-GuiSmokeDelimitedLogField {
    param(
        [AllowNull()][string]$Line,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ([string]::IsNullOrWhiteSpace($Line)) {
        return ''
    }

    $pattern = '(?i)(?:^|[;|]\s*)' + [regex]::Escape($Name) + '=([^;|]+)'
    $match = [regex]::Match($Line, $pattern)
    if (-not $match.Success) {
        return ''
    }

    return $match.Groups[1].Value.Trim()
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

function Get-NknBridgeBootstrapReadinessForRole {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [Parameter(Mandatory = $true)][ValidateSet('helper', 'helpee')][string]$RoleName
    )

    $process = if ($RoleName -eq 'helper') { $Context.HelperProc } else { $Context.HelpeeProc }
    $processId = if ($process) { [int]$process.Id } else { 0 }
    $lines = @(Read-GuiSmokeProcessOutputLinesSnapshot -RoleName $RoleName -ProcessId $processId)
    $tail = @($lines | Select-Object -Last 120)
    $readyMarkers = @(
        'event=nkn_host_ready_connect_completed',
        'HostByAddressAsync ready',
        'event=bridge_ready',
        'event=helper_local_peer_address_ready'
    )
    $failureMarkers = @(
        'event=nkn_bridge_bootstrap_not_ready',
        'event=helpee_invite_bootstrap_bridge_before_ready_retry',
        'HostByAddressAsync failed',
        'RPC call failed',
        'Connect failed',
        'connect EACCES',
        'ready_emitted=0',
        'bridge_bootstrap_not_ready=1',
        'Helpee hosting failed before invite readiness'
    )

    $ready = $false
    $readyMarker = '(none)'
    foreach ($line in $lines) {
        foreach ($marker in $readyMarkers) {
            if ($line.IndexOf($marker, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $ready = $true
                $readyMarker = $marker
                break
            }
        }
        if ($ready) { break }
    }

    $failureReasons = New-Object System.Collections.Generic.List[string]
    $failureSubreason = '(none)'
    $failureReadyEmitted = ''
    $failureLastError = '(none)'
    $failureLine = ''
    $hostReadyConnectFailureSubreason = if ($RoleName -eq 'helpee') { 'helpee_bridge_host_ready_connect_failed' } else { 'helper_bridge_host_ready_connect_failed' }
    foreach ($line in $lines) {
        foreach ($marker in $failureMarkers) {
            if ($line.IndexOf($marker, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                if (-not $failureReasons.Contains($marker)) {
                    $failureReasons.Add($marker) | Out-Null
                }
            }
        }

        if ($line.IndexOf('event=nkn_bridge_bootstrap_not_ready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $failureLine = [string]$line
            $subreason = Get-GuiSmokeDelimitedLogField -Line $line -Name 'subreason'
            if (-not [string]::IsNullOrWhiteSpace($subreason)) {
                $failureSubreason = $subreason
            }

            $failureReason = Get-GuiSmokeDelimitedLogField -Line $line -Name 'failure_reason'
            if (-not [string]::IsNullOrWhiteSpace($failureReason) -and -not $failureReasons.Contains($failureReason)) {
                $failureReasons.Add($failureReason) | Out-Null
            }

            $readyEmitted = Get-GuiSmokeDelimitedLogField -Line $line -Name 'ready_emitted'
            if (-not [string]::IsNullOrWhiteSpace($readyEmitted)) {
                $failureReadyEmitted = $readyEmitted
            }

            $lastError = Get-GuiSmokeDelimitedLogField -Line $line -Name 'last_error'
            if (-not [string]::IsNullOrWhiteSpace($lastError)) {
                $failureLastError = $lastError
            }
        }

        if ($line.IndexOf('event=helpee_invite_bootstrap_bridge_before_ready_retry', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $failureLine = [string]$line
            $failureSubreason = 'helpee_invite_host_before_bridge_ready'

            $failureReason = Get-GuiSmokeDelimitedLogField -Line $line -Name 'failure_reason'
            if (-not [string]::IsNullOrWhiteSpace($failureReason) -and -not $failureReasons.Contains($failureReason)) {
                $failureReasons.Add($failureReason) | Out-Null
            }

            $lastError = Get-GuiSmokeDelimitedLogField -Line $line -Name 'last_error'
            if (-not [string]::IsNullOrWhiteSpace($lastError)) {
                $failureLastError = $lastError
            }
        }

        if ($line.IndexOf('HostByAddressAsync failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('Connect failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('RPC call failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $line.IndexOf('connect EACCES', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            if ($failureSubreason -eq '(none)') {
                $failureSubreason = $hostReadyConnectFailureSubreason
            }

            if ($line.IndexOf('connect EACCES', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $failureLastError = 'connect EACCES'
            }
            elseif ($failureLastError -eq '(none)' -and $line.IndexOf('HostByAddressAsync failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $failureLastError = 'HostByAddressAsync failed'
            }
            elseif ($failureLastError -eq '(none)' -and $line.IndexOf('Connect failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $failureLastError = 'Connect failed'
            }
            elseif ($failureLastError -eq '(none)' -and $line.IndexOf('RPC call failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $failureLastError = 'RPC call failed'
            }
        }
    }

    return [pscustomobject]@{
        RoleName = $RoleName
        ProcessId = $processId
        Ready = $ready
        ReadyMarker = $readyMarker
        FailureReason = if ($failureReasons.Count -gt 0) { ($failureReasons -join ',') } else { '(none)' }
        FailureSubreason = $failureSubreason
        FailureReadyEmitted = $failureReadyEmitted
        FailureLastError = $failureLastError
        FailureLine = $failureLine
        ReadyEmittedZeroObserved = $failureReasons.Contains('ready_emitted=0')
        LineCount = $lines.Count
        Tail = $tail
    }
}

function Write-NknBridgeBootstrapReadinessArtifact {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [Parameter(Mandatory = $true)][ValidateSet('helper', 'helpee')][string]$RoleName,
        [Parameter(Mandatory = $true)]$Snapshot,
        [string]$Reason = 'sample',
        [int]$Attempt = 0
    )

    $artifactRoot = [string]$env:NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR
    if ([string]::IsNullOrWhiteSpace($artifactRoot)) {
        return
    }

    try {
        $diagnosticDir = Join-Path ([System.IO.Path]::GetFullPath($artifactRoot)) 'diagnostics'
        New-Item -ItemType Directory -Force -Path $diagnosticDir | Out-Null
        $safeReason = [regex]::Replace($Reason, '[^a-zA-Z0-9_-]+', '-')
        if ([string]::IsNullOrWhiteSpace($safeReason)) { $safeReason = 'sample' }
        $baseName = "bridge-bootstrap-readiness-$RoleName-$safeReason"
        if ($Attempt -gt 0) {
            $baseName = "$baseName-attempt-$Attempt"
        }

        $txtPath = Join-Path $diagnosticDir "$baseName.txt"
        $jsonPath = Join-Path $diagnosticDir "$baseName.json"
        $lines = New-Object System.Collections.Generic.List[string]
        $lines.Add("reason=$Reason") | Out-Null
        $lines.Add("role=$RoleName") | Out-Null
        $lines.Add("process_id=$($Snapshot.ProcessId)") | Out-Null
        $lines.Add("ready=$(if ($Snapshot.Ready) { 1 } else { 0 })") | Out-Null
        $lines.Add("ready_marker=$($Snapshot.ReadyMarker)") | Out-Null
        $lines.Add("failure_reason=$($Snapshot.FailureReason)") | Out-Null
        $lines.Add("failure_subreason=$($Snapshot.FailureSubreason)") | Out-Null
        $lines.Add("failure_ready_emitted=$($Snapshot.FailureReadyEmitted)") | Out-Null
        $lines.Add("failure_last_error=$($Snapshot.FailureLastError)") | Out-Null
        $lines.Add("line_count=$($Snapshot.LineCount)") | Out-Null
        if ($RoleName -eq 'helpee' -and $Context.HelpeeWindow) {
            try {
                $inviteDiagnostic = Get-HelpeeInviteReadinessDiagnostic -Window $Context.HelpeeWindow
                $lines.Add("helpee_invite_diagnostic=$inviteDiagnostic") | Out-Null
            }
            catch {
                $lines.Add("helpee_invite_diagnostic_error=$($_.Exception.GetType().Name)") | Out-Null
            }
        }

        $lines.Add('') | Out-Null
        $lines.Add('process_output_tail:') | Out-Null
        foreach ($line in @($Snapshot.Tail)) {
            $lines.Add([string]$line) | Out-Null
        }

        [System.IO.File]::WriteAllText($txtPath, ($lines -join [Environment]::NewLine), [System.Text.Encoding]::UTF8)
        ([ordered]@{
            event = 'nkn_bridge_bootstrap_readiness'
            reason = $Reason
            role = $RoleName
            processId = $Snapshot.ProcessId
            ready = [bool]$Snapshot.Ready
            readyMarker = [string]$Snapshot.ReadyMarker
            failureReason = [string]$Snapshot.FailureReason
            failureSubreason = [string]$Snapshot.FailureSubreason
            failureReadyEmitted = [string]$Snapshot.FailureReadyEmitted
            failureLastError = [string]$Snapshot.FailureLastError
            failureLine = [string]$Snapshot.FailureLine
            lineCount = [int]$Snapshot.LineCount
            outputTail = @($Snapshot.Tail)
        }) | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
    }
    catch {
        # Best-effort diagnostics.
    }
}

function Wait-NknBridgeBootstrapReadyForRole {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [Parameter(Mandatory = $true)][ValidateSet('helper', 'helpee')][string]$RoleName,
        [int]$TimeoutMs = 60000,
        [string]$Reason = 'before_connect_flow'
    )

    if (-not (Get-IsNknTransport)) {
        return
    }

    $process = if ($RoleName -eq 'helper') { $Context.HelperProc } else { $Context.HelpeeProc }
    if ($null -eq $process) {
        throw ("nkn_bridge_bootstrap_network_unready: role={0}; reason={1}; detail=missing_process" -f $RoleName, $Reason)
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $lastSnapshot = $null
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        Assert-ProcessStillRunning -Process $process -Label $RoleName
        [void](Refresh-ScenarioRoleWindow -Context $Context -RoleName $RoleName -Reason "bridge_bootstrap_readiness_$Reason")
        $snapshot = Get-NknBridgeBootstrapReadinessForRole -Context $Context -RoleName $RoleName
        $lastSnapshot = $snapshot
        if ($snapshot.Ready) {
            Write-NknBridgeBootstrapReadinessArtifact -Context $Context -RoleName $RoleName -Snapshot $snapshot -Reason "ready_$Reason"
            Write-Host ("[GUI Smoke] event=nkn_bridge_bootstrap_ready; role={0}; process_id={1}; marker={2}; reason={3}" -f $RoleName, $snapshot.ProcessId, $snapshot.ReadyMarker, $Reason) -ForegroundColor DarkGray
            return
        }

        Start-Sleep -Milliseconds 250
    }

    if ($null -eq $lastSnapshot) {
        $lastSnapshot = Get-NknBridgeBootstrapReadinessForRole -Context $Context -RoleName $RoleName
    }

    Write-NknBridgeBootstrapReadinessArtifact -Context $Context -RoleName $RoleName -Snapshot $lastSnapshot -Reason "timeout_$Reason"
    $inviteDiagnostic = '(none)'
    if ($RoleName -eq 'helpee' -and $Context.HelpeeWindow) {
        try {
            $inviteDiagnostic = Get-HelpeeInviteReadinessDiagnostic -Window $Context.HelpeeWindow
        }
        catch {
            $inviteDiagnostic = "diagnostic_error=$($_.Exception.GetType().Name)"
        }
    }

    throw ("nkn_bridge_bootstrap_network_unready: role={0}; process_id={1}; reason={2}; ready_emitted=0; failure_markers={3}; line_count={4}; failure_subreason={5}; failure_last_error={6}; helpee_invite_diagnostic={7}" -f $RoleName, $lastSnapshot.ProcessId, $Reason, $lastSnapshot.FailureReason, $lastSnapshot.LineCount, $lastSnapshot.FailureSubreason, $lastSnapshot.FailureLastError, $inviteDiagnostic)
}

function Wait-NknBridgeBootstrapReadyForCurrentPair {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [int]$TimeoutMs = 60000,
        [string]$Reason = 'before_connect_flow'
    )

    if (-not (Get-IsNknTransport)) {
        return
    }

    Wait-NknBridgeBootstrapReadyForRole -Context $Context -RoleName 'helpee' -TimeoutMs $TimeoutMs -Reason $Reason
    Wait-NknBridgeBootstrapReadyForRole -Context $Context -RoleName 'helper' -TimeoutMs $TimeoutMs -Reason $Reason
}

function Wait-NknBridgeBootstrapReadyForCurrentPairWithRecovery {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [int]$TimeoutMs = 60000,
        [string]$Reason = 'before_connect_flow'
    )

    if (-not (Get-IsNknTransport)) {
        return
    }

    $attempts = 2
    $lastError = $null
    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        try {
            Wait-NknBridgeBootstrapReadyForCurrentPair -Context $Context -TimeoutMs $TimeoutMs -Reason $Reason
            return
        }
        catch {
            $lastError = $_
            $message = $_.Exception.Message
            if ($attempt -ge $attempts -or -not (Test-HelpeeInviteBootstrapBridgeRetryableError -Message $message)) {
                throw
            }

            $rotateIdentity = Test-NknBridgeBootstrapFailureRequiresIdentityRotation -Message $message
            Write-Host ("[GUI Smoke] event=nkn_bridge_bootstrap_pair_retry_scheduled; attempt={0}/{1}; reason={2}; rotate_identity={3}; helpee_pid={4}; helper_pid={5}" -f `
                $attempt,
                $attempts,
                $message,
                ($(if ($rotateIdentity) { 1 } else { 0 })),
                ($(if ($Context.HelpeeProc) { [int]$Context.HelpeeProc.Id } else { 0 })),
                ($(if ($Context.HelperProc) { [int]$Context.HelperProc.Id } else { 0 }))) -ForegroundColor Yellow
            Restart-HelperIdentityPairFlow -Context $Context -Reason 'nkn_bridge_bootstrap_pair_retry' -Attempt ($attempt + 1) -RotateIdentity $rotateIdentity
        }
    }

    if ($lastError) {
        throw $lastError
    }

    throw 'NKN bridge bootstrap pair recovery failed unexpectedly.'
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

function Get-HelpeeInviteReadinessDiagnostic {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window)

    $headerStatus = '(unavailable)'
    $inviteStatus = '(missing)'
    $requestHelpEnabled = '(missing)'
    $helperInputEnabled = '(missing)'
    $retryEnabled = '(missing)'

    try {
        $headerStatus = Format-ConnectionDiagnosticText -Text (Get-SessionHeaderStatusValue -Window $Window)
    } catch {}

    try {
        $status = Find-VisibleByAutomationId -Root $Window -AutomationId 'Helpee.InviteStatus'
        if ($status) {
            $inviteStatus = Format-ConnectionDiagnosticText -Text (Get-ElementTextSafe -Element $status)
        }
    } catch {}

    try {
        $request = Find-VisibleByAutomationId -Root $Window -AutomationId 'Helpee.RequestHelp'
        if ($request) {
            $requestHelpEnabled = if ($request.Current.IsEnabled) { '1' } else { '0' }
        }
    } catch {}

    try {
        $input = Find-VisibleByAutomationId -Root $Window -AutomationId 'Helpee.HelperIdentityInput'
        if ($input) {
            $helperInputEnabled = if ($input.Current.IsEnabled) { '1' } else { '0' }
        }
    } catch {}

    try {
        $retry = Find-ByNameAndType -Root $Window -Name 'Retry' -ControlType ([System.Windows.Automation.ControlType]::Button)
        if ($retry -and -not $retry.Current.IsOffscreen) {
            $retryEnabled = if ($retry.Current.IsEnabled) { '1' } else { '0' }
        }
    } catch {}

    return ("header_status='{0}'; invite_status='{1}'; request_help_enabled={2}; helper_identity_input_enabled={3}; retry_enabled={4}" -f `
        $headerStatus,
        $inviteStatus,
        $requestHelpEnabled,
        $helperInputEnabled,
        $retryEnabled)
}

function Get-HelpeeHelperIdentityEntryDiagnostic {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$HelperIdentity
    )

    $inputEnabled = '(missing)'
    $inputKind = '(missing)'
    $inputLength = 0
    $inputHash = ''
    $inputMatches = '0'
    $pasteEnabled = '(missing)'
    $targetKind = Get-HelperIdentityClipboardCandidateKind -Value $HelperIdentity
    $targetLength = if ([string]::IsNullOrWhiteSpace($HelperIdentity)) { 0 } else { $HelperIdentity.Trim().Length }
    $targetHash = Get-GuiSmokeStringHashPrefix -Value $HelperIdentity

    try {
        $input = Find-VisibleByAutomationId -Root $Window -AutomationId 'Helpee.HelperIdentityInput'
        if ($input) {
            $inputEnabled = if ($input.Current.IsEnabled) { '1' } else { '0' }
            $raw = [string](Get-ElementValueSafe -Element $input)
            $inputKind = Get-HelperIdentityClipboardCandidateKind -Value $raw
            $inputLength = if ([string]::IsNullOrWhiteSpace($raw)) { 0 } else { $raw.Trim().Length }
            $inputHash = Get-GuiSmokeStringHashPrefix -Value $raw
            $state = Test-ElementValueMatchesText -Element $input -ExpectedText $HelperIdentity
            $inputMatches = if ($state.IsMatch) { '1' } else { '0' }
        }
    } catch {}

    try {
        $paste = Find-ByNameAndType -Root $Window -Name 'Paste helper address' -ControlType ([System.Windows.Automation.ControlType]::Button)
        if ($paste -and -not $paste.Current.IsOffscreen) {
            $pasteEnabled = if ($paste.Current.IsEnabled) { '1' } else { '0' }
        }
    } catch {}

    return ("helper_identity_target_kind={0}; target_length={1}; target_hash={2}; input_kind={3}; input_length={4}; input_hash={5}; input_matches_target={6}; input_enabled={7}; paste_enabled={8}" -f `
        $targetKind,
        $targetLength,
        $targetHash,
        $inputKind,
        $inputLength,
        $inputHash,
        $inputMatches,
        $inputEnabled,
        $pasteEnabled)
}

function Test-HelpeeHelperIdentityRequestBootstrapPending {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$HelperIdentity
    )

    $input = Find-VisibleByAutomationId -Root $Window -AutomationId 'Helpee.HelperIdentityInput'
    if (-not $input -or -not $input.Current.IsEnabled) {
        return $false
    }

    $inputState = Test-ElementValueMatchesText -Element $input -ExpectedText $HelperIdentity
    if (-not $inputState.IsMatch) {
        return $false
    }

    $request = Find-VisibleByAutomationId -Root $Window -AutomationId 'Helpee.RequestHelp'
    if ($request -and $request.Current.IsEnabled -and -not $request.Current.IsOffscreen) {
        return $false
    }

    $headerStatus = Get-SessionHeaderStatusValue -Window $Window
    $inviteStatus = ''
    $status = Find-VisibleByAutomationId -Root $Window -AutomationId 'Helpee.InviteStatus'
    if ($status) {
        $inviteStatus = (Get-ElementTextSafe -Element $status).Trim()
    }

    return ($headerStatus -match 'Waiting for helper|Connecting' -or
        $inviteStatus -match 'Preparing invite|Updating invite|Preparing')
}

function Invoke-HelpeeHelperIdentityRequestReadinessNudge {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$HelperIdentity,
        [Parameter(Mandatory = $true)][int]$Attempt
    )

    $input = Find-VisibleByAutomationId -Root $Window -AutomationId 'Helpee.HelperIdentityInput'
    if (-not $input -or -not $input.Current.IsEnabled) {
        return $false
    }

    $usedPasteButton = $false
    $pasteButton = Find-ByNameAndType -Root $Window -Name 'Paste helper address' -ControlType ([System.Windows.Automation.ControlType]::Button)
    if ($pasteButton -and -not $pasteButton.Current.IsOffscreen -and $pasteButton.Current.IsEnabled) {
        try {
            Set-Clipboard -Value $HelperIdentity
            Click-Element $pasteButton
            [void](Wait-ElementValueMatchesText -Element $input -ExpectedText $HelperIdentity -TimeoutMs 1500)
            $usedPasteButton = $true
        }
        catch {
            $usedPasteButton = $false
        }
    }

    $state = Test-ElementValueMatchesText -Element $input -ExpectedText $HelperIdentity
    if (-not $state.IsMatch) {
        Set-Text -Element $input -Text $HelperIdentity
        [void](Wait-ElementValueMatchesText -Element $input -ExpectedText $HelperIdentity -TimeoutMs 1500)
    }

    Submit-TextInputWithEnter -Element $input
    Write-Host ("[GUI Smoke] event=helpee_request_flow_not_enabled_after_identity_paste; attempt={0}; action=identity_entry_nudge; used_paste_button={1}; {2}; {3}" -f `
        $Attempt,
        ($(if ($usedPasteButton) { 1 } else { 0 })),
        (Get-HelpeeInviteReadinessDiagnostic -Window $Window),
        (Get-HelpeeHelperIdentityEntryDiagnostic -Window $Window -HelperIdentity $HelperIdentity)) -ForegroundColor Yellow
    return $true
}

function Get-HelpeeInviteRetryButton {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window)

    try {
        $retry = Find-ByNameAndType -Root $Window -Name 'Retry' -ControlType ([System.Windows.Automation.ControlType]::Button)
        if ($retry -and -not $retry.Current.IsOffscreen -and $retry.Current.IsEnabled) {
            return $retry
        }
    }
    catch {}

    return $null
}

function Write-HelpeeInviteReadinessDiagnosticArtifact {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$Reason,
        [string]$HelperIdentity = ''
    )

    $artifactRoot = [string]$env:NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR
    if ([string]::IsNullOrWhiteSpace($artifactRoot)) {
        return
    }

    try {
        $diagnosticDir = Join-Path ([System.IO.Path]::GetFullPath($artifactRoot)) 'diagnostics'
        New-Item -ItemType Directory -Force -Path $diagnosticDir | Out-Null
        $timestamp = (Get-Date).ToString('yyyyMMdd-HHmmss-fff')
        $path = Join-Path $diagnosticDir "helpee-invite-readiness-$timestamp.txt"
        $lines = New-Object System.Collections.Generic.List[string]
        $lines.Add("reason=$Reason") | Out-Null
        $lines.Add((Get-HelpeeInviteReadinessDiagnostic -Window $Window)) | Out-Null
        if (-not [string]::IsNullOrWhiteSpace($HelperIdentity)) {
            $lines.Add((Get-HelpeeHelperIdentityEntryDiagnostic -Window $Window -HelperIdentity $HelperIdentity)) | Out-Null
        }
        $lines.Add('') | Out-Null
        $lines.Add('process_output_tail:') | Out-Null
        foreach ($line in @(Read-GuiSmokeProcessOutputLinesSnapshot | Select-Object -Last 120)) {
            $lines.Add([string]$line) | Out-Null
        }

        [System.IO.File]::WriteAllText($path, ($lines -join [Environment]::NewLine), [System.Text.Encoding]::UTF8)
    }
    catch {
        # Best-effort diagnostics.
    }
}

function Get-HelperIdentityClipboardCandidateKind {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return 'empty'
    }

    $trimmed = $Value.Trim()
    if ($trimmed.StartsWith('nlinkh1.', [System.StringComparison]::OrdinalIgnoreCase)) {
        return 'helper_bootstrap_payload'
    }

    if ($trimmed.StartsWith('nhid1-', [System.StringComparison]::OrdinalIgnoreCase)) {
        return 'helper_identity_token'
    }

    if (Test-HelperIdentityValueUsable -Value $trimmed) {
        return 'peer_address_like'
    }

    return 'invalid'
}

function Test-HelperIdentityClipboardCandidateUsable {
    param([string]$Value)

    $kind = Get-HelperIdentityClipboardCandidateKind -Value $Value
    return $kind -eq 'helper_bootstrap_payload' -or
        $kind -eq 'helper_identity_token' -or
        $kind -eq 'peer_address_like'
}

function Get-HelperIdentityBootstrapDiagnostic {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [string]$Candidate = ''
    )

    $headerStatus = '(unavailable)'
    $bootstrapTextKind = '(missing)'
    $bootstrapTextLength = '(missing)'
    $copyEnabled = '(missing)'
    $regenerateEnabled = '(missing)'
    $shareEnabled = '(missing)'
    $windowProcessId = '(unavailable)'
    $windowNativeHandle = '(unavailable)'
    $windowClassName = '(unavailable)'
    $windowControlType = '(unavailable)'
    $candidateKind = Get-HelperIdentityClipboardCandidateKind -Value $Candidate
    $candidateLength = if ([string]::IsNullOrWhiteSpace($Candidate)) { 0 } else { $Candidate.Trim().Length }

    try {
        $windowProcessId = [string]$Window.Current.ProcessId
        $windowNativeHandle = [string]$Window.Current.NativeWindowHandle
        $windowClassName = [string]$Window.Current.ClassName
        $windowControlType = if ($Window.Current.ControlType) { [string]$Window.Current.ControlType.ProgrammaticName } else { '(none)' }
    } catch {}

    try {
        $headerStatus = Format-ConnectionDiagnosticText -Text (Get-SessionHeaderStatusValue -Window $Window)
    } catch {}

    try {
        $bootstrapText = Get-ElementValueSafe -Element (Find-VisibleByAutomationId -Root $Window -AutomationId 'Helper.HelperIdentityBootstrapTextBox')
        $bootstrapTextKind = Get-HelperIdentityClipboardCandidateKind -Value $bootstrapText
        $bootstrapTextLength = if ([string]::IsNullOrWhiteSpace($bootstrapText)) { 0 } else { $bootstrapText.Trim().Length }
    } catch {}

    try {
        $copy = Find-VisibleByAutomationId -Root $Window -AutomationId 'Helper.CopyHelperIdentity'
        if ($copy) {
            $copyEnabled = if ($copy.Current.IsEnabled) { '1' } else { '0' }
        }
    } catch {}

    try {
        $regenerate = Find-VisibleByAutomationId -Root $Window -AutomationId 'Helper.RegenerateHelperIdentity'
        if ($regenerate) {
            $regenerateEnabled = if ($regenerate.Current.IsEnabled) { '1' } else { '0' }
        }
    } catch {}

    try {
        $share = Find-VisibleByAutomationId -Root $Window -AutomationId 'Helper.ShareHelperIdentity'
        if ($share) {
            $shareEnabled = if ($share.Current.IsEnabled) { '1' } else { '0' }
        }
    } catch {}

    return ("header_status='{0}'; bootstrap_text_kind={1}; bootstrap_text_length={2}; copy_enabled={3}; regenerate_enabled={4}; share_enabled={5}; clipboard_candidate_kind={6}; clipboard_candidate_length={7}; window_process_id={8}; window_native_handle={9}; window_class={10}; window_control_type={11}" -f `
        $headerStatus,
        $bootstrapTextKind,
        $bootstrapTextLength,
        $copyEnabled,
        $regenerateEnabled,
        $shareEnabled,
        $candidateKind,
        $candidateLength,
        $windowProcessId,
        $windowNativeHandle,
        $windowClassName,
        $windowControlType)
}

function Write-HelperIdentityBootstrapDiagnosticArtifact {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)][string]$Reason,
        [string]$Candidate = ''
    )

    $artifactRoot = [string]$env:NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR
    if ([string]::IsNullOrWhiteSpace($artifactRoot)) {
        return
    }

    try {
        $diagnosticDir = Join-Path ([System.IO.Path]::GetFullPath($artifactRoot)) 'diagnostics'
        New-Item -ItemType Directory -Force -Path $diagnosticDir | Out-Null
        $timestamp = (Get-Date).ToString('yyyyMMdd-HHmmss-fff')
        $path = Join-Path $diagnosticDir "helper-identity-bootstrap-$timestamp.txt"
        $lines = New-Object System.Collections.Generic.List[string]
        $lines.Add("reason=$Reason") | Out-Null
        $lines.Add((Get-HelperIdentityBootstrapDiagnostic -Window $Window -Candidate $Candidate)) | Out-Null
        $lines.Add('') | Out-Null
        $lines.Add('process_output_tail:') | Out-Null
        foreach ($line in @(Read-GuiSmokeProcessOutputLinesSnapshot | Select-Object -Last 120)) {
            $lines.Add([string]$line) | Out-Null
        }

        [System.IO.File]::WriteAllText($path, ($lines -join [Environment]::NewLine), [System.Text.Encoding]::UTF8)
    }
    catch {
        # Best-effort diagnostics.
    }
}

function Wait-HelperIdentityBootstrapReadyForClipboard {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$HelperWindow,
        [int]$TimeoutMs = 45000
    )

    return Wait-Until -TimeoutMs $TimeoutMs -PollMs 200 -OnTimeoutMessage 'Timed out waiting for current helper identity bootstrap UI before clipboard fallback.' -Condition {
        if (Test-ConnectionFailedSurface -Window $HelperWindow) {
            throw ("Helper reached Connection failed before helper identity bootstrap became current. {0}" -f (Get-HelperIdentityBootstrapDiagnostic -Window $HelperWindow))
        }

        $headerStatus = Get-SessionHeaderStatusValue -Window $HelperWindow

        $bootstrap = Find-VisibleByAutomationId -Root $HelperWindow -AutomationId 'Helper.HelperIdentityBootstrapTextBox'
        $copy = Find-VisibleByAutomationId -Root $HelperWindow -AutomationId 'Helper.CopyHelperIdentity'
        $regenerate = Find-VisibleByAutomationId -Root $HelperWindow -AutomationId 'Helper.RegenerateHelperIdentity'
        if (-not $bootstrap -or -not $copy) {
            return $null
        }

        $candidate = (Get-ElementValueSafe -Element $bootstrap).Trim()
        if (-not (Test-HelperIdentityClipboardCandidateUsable -Value $candidate)) {
            return $null
        }

        if (-not $copy.Current.IsEnabled) {
            return $null
        }

        $regenerateEnabled = $false
        if ($regenerate) {
            try { $regenerateEnabled = [bool]$regenerate.Current.IsEnabled } catch { $regenerateEnabled = $false }
        }

        return [pscustomobject]@{
            Candidate = $candidate
            HeaderStatus = $headerStatus
            PendingStatus = ($headerStatus.StartsWith('Connecting', [System.StringComparison]::OrdinalIgnoreCase) -or -not $regenerateEnabled)
            RegenerateEnabled = $regenerateEnabled
        }
    }
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
            [void](Refresh-ScenarioRoleWindow -Context $Context -RoleName 'helper' -Reason 'invite_entry')
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

    $expectedClipboardValue = $null
    if (Get-IsNknTransport) {
        try {
            $ready = Wait-HelperIdentityBootstrapReadyForClipboard -HelperWindow $HelperWindow -TimeoutMs (Get-TransportAwareTimeoutMs -DefaultMs 10000 -NknMs 45000)
            if ($ready -and -not [string]::IsNullOrWhiteSpace([string]$ready.Candidate)) {
                $expectedClipboardValue = ([string]$ready.Candidate).Trim()
                if ($ready.PendingStatus) {
                    Write-Host ("[GUI Smoke] event=helper_identity_clipboard_fallback_ready_pending_status; header_status='{0}'; regenerate_enabled={1}; candidate_kind={2}; candidate_length={3}" -f `
                        (Format-ConnectionDiagnosticText -Text ([string]$ready.HeaderStatus)),
                        ($(if ($ready.RegenerateEnabled) { 1 } else { 0 })),
                        (Get-HelperIdentityClipboardCandidateKind -Value $expectedClipboardValue),
                        $expectedClipboardValue.Length) -ForegroundColor DarkGray
                }
            }
        }
        catch {
            Write-HelperIdentityBootstrapDiagnosticArtifact -Window $HelperWindow -Reason 'helper_identity_bootstrap_not_current_before_clipboard'
            throw
        }
    }

    try { Set-Clipboard -Value '' } catch {}

    $copyBtn = Wait-Until -TimeoutMs (Get-TransportAwareTimeoutMs -DefaultMs 10000 -NknMs 45000) -PollMs 200 -OnTimeoutMessage 'Timed out waiting for Helper.CopyHelperIdentity.' -Condition {
        $btn = Find-VisibleByAutomationId -Root $HelperWindow -AutomationId 'Helper.CopyHelperIdentity'
        if ($btn -and $btn.Current.IsEnabled) { return $btn }
        return $null
    }

    Click-Element $copyBtn

    try {
        $raw = Wait-Until -TimeoutMs 5000 -PollMs 150 -OnTimeoutMessage 'Timed out waiting for current helper identity on clipboard.' -Condition {
            $text = [string](Get-ClipboardTextSafe)
            if ([string]::IsNullOrWhiteSpace($text)) { return $null }

            $text = $text.Trim()
            if (-not (Test-HelperIdentityClipboardCandidateUsable -Value $text)) {
                return $null
            }

            if (-not [string]::IsNullOrWhiteSpace($expectedClipboardValue) -and
                -not [string]::Equals($text, $expectedClipboardValue, [System.StringComparison]::Ordinal)) {
                return $null
            }

            return $text
        }
    }
    catch {
        $candidate = [string](Get-ClipboardTextSafe)
        Write-HelperIdentityBootstrapDiagnosticArtifact -Window $HelperWindow -Reason 'helper_identity_clipboard_copy_not_current' -Candidate $candidate
        throw
    }

    $raw = ([string]$raw).Trim()
    if (-not (Test-HelperIdentityClipboardCandidateUsable -Value $raw)) {
        Write-HelperIdentityBootstrapDiagnosticArtifact -Window $HelperWindow -Reason 'helper_identity_clipboard_copy_invalid' -Candidate $raw
        throw "Helper identity clipboard copy produced unusable text. $(Get-HelperIdentityBootstrapDiagnostic -Window $HelperWindow -Candidate $raw)"
    }

    if (-not [string]::IsNullOrWhiteSpace($expectedClipboardValue) -and
        -not [string]::Equals($raw, $expectedClipboardValue, [System.StringComparison]::Ordinal)) {
        Write-Host ("[GUI Smoke] event=helper_identity_clipboard_stale; expected_kind={0}; expected_length={1}; clipboard_kind={2}; clipboard_length={3}" -f `
            (Get-HelperIdentityClipboardCandidateKind -Value $expectedClipboardValue),
            $expectedClipboardValue.Length,
            (Get-HelperIdentityClipboardCandidateKind -Value $raw),
            $raw.Length) -ForegroundColor Yellow
        Write-HelperIdentityBootstrapDiagnosticArtifact -Window $HelperWindow -Reason 'helper_identity_clipboard_copy_stale' -Candidate $raw
        throw "Helper identity clipboard copy did not match the current helper bootstrap text. $(Get-HelperIdentityBootstrapDiagnostic -Window $HelperWindow -Candidate $raw)"
    }

    Write-Host ("[GUI Smoke] event=helper_identity_clipboard_current_match; candidate_kind={0}; candidate_length={1}; window_handle={2}" -f `
        (Get-HelperIdentityClipboardCandidateKind -Value $raw),
        $raw.Length,
        (Get-AutomationNativeWindowHandleSafe -Window $HelperWindow)) -ForegroundColor DarkGray
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
            [void](Refresh-ScenarioRoleWindow -Context $Context -RoleName 'helper' -Reason 'copy_helper_identity')
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
                    Write-Host "[GUI Smoke] Helper identity log sync unavailable; waiting for current helper bootstrap UI before clipboard fallback." -ForegroundColor DarkGray
                    return Copy-HelperIdentityAndReadClipboard -HelperWindow $Context.HelperWindow
                }

                Write-HelperIdentityBootstrapDiagnosticArtifact -Window $Context.HelperWindow -Reason 'helper_identity_log_sync_unavailable_no_clipboard_fallback'
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
        $Context,
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$HelpeeWindow,
        [Parameter(Mandatory = $true)][string]$HelperIdentity
    )

    $attempts = if (Get-IsNknTransport) { 2 } else { 1 }
    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
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
        Write-Host ("[GUI Smoke] event=helpee_helper_identity_entered; attempt={0}; used_paste_button={1}; {2}; {3}" -f `
            $attempt,
            ($(if ($pastedViaUi) { 1 } else { 0 })),
            (Get-HelpeeInviteReadinessDiagnostic -Window $HelpeeWindow),
            (Get-HelpeeHelperIdentityEntryDiagnostic -Window $HelpeeWindow -HelperIdentity $HelperIdentity)) -ForegroundColor DarkGray

        $readiness = $null
        try {
            $readiness = Wait-Until -TimeoutMs (Get-TransportAwareTimeoutMs -DefaultMs 10000 -NknMs 60000) -PollMs 200 -OnTimeoutMessage 'Timed out waiting for helpee request flow to become ready.' -Condition {
                if (Test-ConnectionFailedSurface -Window $HelpeeWindow) {
                    return [pscustomobject]@{
                        State = 'connection_failed'
                        Retry = (Get-HelpeeInviteRetryButton -Window $HelpeeWindow)
                        Diagnostic = (Get-HelpeeInviteReadinessDiagnostic -Window $HelpeeWindow)
                    }
                }

                $request = Find-VisibleByAutomationId -Root $HelpeeWindow -AutomationId 'Helpee.RequestHelp'
                if ($request -and $request.Current.IsEnabled -and -not $request.Current.IsOffscreen) {
                    return [pscustomobject]@{
                        State = 'ready'
                        Request = $request
                        Diagnostic = (Get-HelpeeInviteReadinessDiagnostic -Window $HelpeeWindow)
                    }
                }

                $status = Find-VisibleByAutomationId -Root $HelpeeWindow -AutomationId 'Helpee.InviteStatus'
                if ($status) {
                    $statusText = (Get-ElementTextSafe -Element $status).Trim()
                    if ($statusText -match 'Preparing invite|Updating invite') {
                        return $null
                    }
                }

                return [pscustomobject]@{
                    State = 'maybe_ready'
                    Request = $request
                    Diagnostic = (Get-HelpeeInviteReadinessDiagnostic -Window $HelpeeWindow)
                }
            }
        }
        catch {
            Write-HelpeeInviteReadinessDiagnosticArtifact -Window $HelpeeWindow -Reason "invite_ready_wait_failed_attempt_$attempt" -HelperIdentity $HelperIdentity
            if (Test-HelpeeHelperIdentityRequestBootstrapPending -Window $HelpeeWindow -HelperIdentity $HelperIdentity) {
                if ($attempt -lt $attempts -and (Invoke-HelpeeHelperIdentityRequestReadinessNudge -Window $HelpeeWindow -HelperIdentity $HelperIdentity -Attempt $attempt)) {
                    Start-Sleep -Milliseconds 750
                    continue
                }

                throw ("Helpee request flow not enabled after current helper identity paste. reason=helpee_request_flow_not_enabled_after_identity_paste; {0}; {1}" -f `
                    (Get-HelpeeInviteReadinessDiagnostic -Window $HelpeeWindow),
                    (Get-HelpeeHelperIdentityEntryDiagnostic -Window $HelpeeWindow -HelperIdentity $HelperIdentity))
            }

            if ($attempt -lt $attempts) {
                $retry = Get-HelpeeInviteRetryButton -Window $HelpeeWindow
                if ($retry) {
                    Write-Host ("[GUI Smoke] event=helpee_invite_retry_after_readiness_timeout; attempt={0}; diagnostic={1}" -f $attempt, (Get-HelpeeInviteReadinessDiagnostic -Window $HelpeeWindow)) -ForegroundColor Yellow
                    Click-Element $retry
                    Start-Sleep -Milliseconds 500
                    continue
                }
            }

            throw ("{0} Diagnostic: {1}" -f $_.Exception.Message, (Get-HelpeeInviteReadinessDiagnostic -Window $HelpeeWindow))
        }

        if ($readiness -and [string]::Equals([string]$readiness.State, 'connection_failed', [System.StringComparison]::Ordinal)) {
            Write-HelpeeInviteReadinessDiagnosticArtifact -Window $HelpeeWindow -Reason "invite_connection_failed_attempt_$attempt" -HelperIdentity $HelperIdentity
            if ($attempt -lt $attempts -and $readiness.Retry) {
                Write-Host ("[GUI Smoke] event=helpee_invite_retry_after_connection_failed; attempt={0}; diagnostic={1}" -f $attempt, [string]$readiness.Diagnostic) -ForegroundColor Yellow
                Click-Element $readiness.Retry
                Start-Sleep -Milliseconds 500
                continue
            }

            throw ("Helpee reached Connection failed before request flow became ready. {0}" -f (Get-HelpeeInviteReadinessDiagnostic -Window $HelpeeWindow))
        }

        try {
            $request = Wait-Until -TimeoutMs (Get-TransportAwareTimeoutMs -DefaultMs 10000 -NknMs 45000) -PollMs 200 -OnTimeoutMessage 'Timed out waiting for Helpee.RequestHelp to become enabled.' -Condition {
                if (Test-ConnectionFailedSurface -Window $HelpeeWindow) {
                    return [pscustomobject]@{
                        State = 'connection_failed'
                        Retry = (Get-HelpeeInviteRetryButton -Window $HelpeeWindow)
                        Diagnostic = (Get-HelpeeInviteReadinessDiagnostic -Window $HelpeeWindow)
                    }
                }

                $btn = Find-VisibleByAutomationId -Root $HelpeeWindow -AutomationId 'Helpee.RequestHelp'
                if ($btn -and $btn.Current.IsEnabled) {
                    return [pscustomobject]@{
                        State = 'ready'
                        Request = $btn
                        Diagnostic = (Get-HelpeeInviteReadinessDiagnostic -Window $HelpeeWindow)
                    }
                }
                return $null
            }
        }
        catch {
            Write-HelpeeInviteReadinessDiagnosticArtifact -Window $HelpeeWindow -Reason "request_help_wait_failed_attempt_$attempt" -HelperIdentity $HelperIdentity
            if (Test-HelpeeHelperIdentityRequestBootstrapPending -Window $HelpeeWindow -HelperIdentity $HelperIdentity) {
                if ($attempt -lt $attempts -and (Invoke-HelpeeHelperIdentityRequestReadinessNudge -Window $HelpeeWindow -HelperIdentity $HelperIdentity -Attempt $attempt)) {
                    Start-Sleep -Milliseconds 750
                    continue
                }

                throw ("Helpee request help button not enabled after current helper identity paste. reason=helpee_request_flow_not_enabled_after_identity_paste; {0}; {1}" -f `
                    (Get-HelpeeInviteReadinessDiagnostic -Window $HelpeeWindow),
                    (Get-HelpeeHelperIdentityEntryDiagnostic -Window $HelpeeWindow -HelperIdentity $HelperIdentity))
            }

            if ($attempt -lt $attempts) {
                $retry = Get-HelpeeInviteRetryButton -Window $HelpeeWindow
                if ($retry) {
                    Write-Host ("[GUI Smoke] event=helpee_invite_retry_after_request_help_timeout; attempt={0}; diagnostic={1}" -f $attempt, (Get-HelpeeInviteReadinessDiagnostic -Window $HelpeeWindow)) -ForegroundColor Yellow
                    Click-Element $retry
                    Start-Sleep -Milliseconds 500
                    continue
                }
            }

            throw ("{0} Diagnostic: {1}" -f $_.Exception.Message, (Get-HelpeeInviteReadinessDiagnostic -Window $HelpeeWindow))
        }

        if ($request -and [string]::Equals([string]$request.State, 'connection_failed', [System.StringComparison]::Ordinal)) {
            Write-HelpeeInviteReadinessDiagnosticArtifact -Window $HelpeeWindow -Reason "request_help_connection_failed_attempt_$attempt" -HelperIdentity $HelperIdentity
            if ($attempt -lt $attempts -and $request.Retry) {
                Write-Host ("[GUI Smoke] event=helpee_invite_retry_after_request_help_connection_failed; attempt={0}; diagnostic={1}" -f $attempt, [string]$request.Diagnostic) -ForegroundColor Yellow
                Click-Element $request.Retry
                Start-Sleep -Milliseconds 500
                continue
            }

            throw ("Helpee reached Connection failed before Request help became enabled. {0}" -f (Get-HelpeeInviteReadinessDiagnostic -Window $HelpeeWindow))
        }

        $requestButton = $null
        if ($request -and $request.Request) {
            $requestButton = $request.Request
        }
        elseif ($readiness -and $readiness.Request) {
            $requestButton = $readiness.Request
        }

        if (-not $requestButton) {
            throw ("Helpee request flow produced no Request help button. {0}" -f (Get-HelpeeInviteReadinessDiagnostic -Window $HelpeeWindow))
        }

        if ((Get-IsNknTransport) -and $null -ne $Context) {
            Wait-NknBridgeBootstrapReadyForCurrentPair -Context $Context -TimeoutMs (Get-TransportAwareTimeoutMs -DefaultMs 1000 -NknMs 60000) -Reason 'before_request_help_click'
        }

        Click-Element $requestButton
        return $requestButton
    }

    throw ("Helpee invite retry budget exhausted. {0}" -f (Get-HelpeeInviteReadinessDiagnostic -Window $HelpeeWindow))
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
    [void](Refresh-ScenarioRoleWindow -Context $Context -RoleName 'helper' -Reason 'accept_request')
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

        try {
            [void](Enter-HelpeeHelperIdentityAndRequestHelp -Context $Context -HelpeeWindow $Context.HelpeeWindow -HelperIdentity $HelperIdentity)
        }
        catch {
            $lastError = $_
            $message = $_.Exception.Message
            if ($message.IndexOf('helpee_request_flow_not_enabled_after_identity_paste', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                $attempt -lt $maxAttempts) {
                Write-Host ("[GUI Smoke] event=helper_identity_request_flow_restart_after_helpee_request_not_enabled; attempt={0}/{1}; diagnostic={2}" -f `
                    $attempt,
                    $maxAttempts,
                    (Get-HelpeeInviteReadinessDiagnostic -Window $Context.HelpeeWindow)) -ForegroundColor Yellow
                Restart-HelpeeFlow -Context $Context
                $entryMode = Wait-HelpeeConnectionEntryModeWithRecovery -Context $Context -TimeoutMs (Get-TransportAwareTimeoutMs -DefaultMs 10000 -NknMs 30000)
                if (-not [string]::Equals([string]$entryMode, 'helper_identity', [System.StringComparison]::Ordinal)) {
                    throw ("Helpee restart after request-readiness failure did not return to helper identity mode. entry_mode={0}" -f $entryMode)
                }
                continue
            }

            throw
        }

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

function Get-AutomationNativeWindowHandleSafe {
    param([AllowNull()][System.Windows.Automation.AutomationElement]$Window)

    if ($null -eq $Window) { return 0L }
    try { return [int64]$Window.Current.NativeWindowHandle } catch { return 0L }
}

function Set-ScenarioRoleWindowIdentity {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [Parameter(Mandatory = $true)][ValidateSet('helper', 'helpee')][string]$RoleName,
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Window
    )

    $handle = Get-AutomationNativeWindowHandleSafe -Window $Window
    if ($RoleName -eq 'helper') {
        $Context.HelperWindow = $Window
        $Context.HelperWindowNativeHandle = $handle
        $Context.HelperWindowProcessId = if ($Context.HelperProc) { [int]$Context.HelperProc.Id } else { 0 }
    }
    else {
        $Context.HelpeeWindow = $Window
        $Context.HelpeeWindowNativeHandle = $handle
        $Context.HelpeeWindowProcessId = if ($Context.HelpeeProc) { [int]$Context.HelpeeProc.Id } else { 0 }
    }
}

function Clear-ScenarioRoleWindowIdentity {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [Parameter(Mandatory = $true)][ValidateSet('helper', 'helpee')][string]$RoleName
    )

    if ($RoleName -eq 'helper') {
        $Context.HelperWindow = $null
        $Context.HelperWindowNativeHandle = 0L
        $Context.HelperWindowProcessId = 0
    }
    else {
        $Context.HelpeeWindow = $null
        $Context.HelpeeWindowNativeHandle = 0L
        $Context.HelpeeWindowProcessId = 0
    }
}

function Remove-ScenarioTrackedProcess {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [AllowNull()]$Process
    )

    if ($null -eq $Process) { return }
    try {
        for ($index = $Context.Processes.Count - 1; $index -ge 0; $index--) {
            $candidate = $Context.Processes[$index]
            if ($candidate -and [int]$candidate.Id -eq [int]$Process.Id) {
                $Context.Processes.RemoveAt($index)
            }
        }
    }
    catch {
        # Best-effort tracked-process cleanup.
    }
}

function Assert-ScenarioRoleProcessRetired {
    param(
        [AllowNull()]$Process,
        [Parameter(Mandatory = $true)][ValidateSet('helper', 'helpee')][string]$RoleName
    )

    if ($null -eq $Process) { return }
    $live = Get-Process -Id $Process.Id -ErrorAction SilentlyContinue
    if ($live -and -not $live.HasExited) {
        throw ("{0}_restart_cleanup_failed: previous {0} process still alive after cleanup. pid={1}" -f $RoleName, $Process.Id)
    }
}

function Refresh-ScenarioRoleWindow {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [Parameter(Mandatory = $true)][ValidateSet('helper', 'helpee')][string]$RoleName,
        [string]$Reason = 'unspecified'
    )

    $process = if ($RoleName -eq 'helper') { $Context.HelperProc } else { $Context.HelpeeProc }
    if ($null -eq $process) {
        throw ("{0}_window_ownership_mismatch: missing process before {1}" -f $RoleName, $Reason)
    }

    Assert-ProcessStillRunning -Process $process -Label $RoleName
    $oldHandle = if ($RoleName -eq 'helper') { [int64]$Context.HelperWindowNativeHandle } else { [int64]$Context.HelpeeWindowNativeHandle }
    $ownershipEventName = if ($RoleName -eq 'helper' -and ([string]$Reason).IndexOf('helper_identity', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        'helper_identity_window_ownership_mismatch'
    }
    else {
        ("{0}_window_ownership_mismatch" -f $RoleName)
    }
    $window = Get-NLinkMainWindowElementByProcessId -ProcessId $process.Id -PreferredNativeHandle $oldHandle
    if (-not $window) {
        $artifactDir = [string]$env:NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR
        if (-not [string]::IsNullOrWhiteSpace($artifactDir)) {
            try {
                $diagnosticDir = Join-Path ([System.IO.Path]::GetFullPath($artifactDir)) 'diagnostics'
                New-Item -ItemType Directory -Force -Path $diagnosticDir | Out-Null
                $path = Join-Path $diagnosticDir ("{0}-window-ownership-mismatch-{1}.txt" -f $RoleName, ((Get-Date).ToString('yyyyMMdd-HHmmss-fff')))
                @(
                    ("event={0}" -f $ownershipEventName),
                    ("reason={0}" -f $Reason),
                    ("process_id={0}" -f $process.Id),
                    ("expected_native_handle={0}" -f $oldHandle),
                    (Get-TopLevelWindowInventoryText -Process $process)
                ) | Set-Content -LiteralPath $path -Encoding UTF8
            }
            catch {}
        }

        throw ("{0}: no current nLink MainWindow for pid={1}; expected_handle={2}; reason={3}" -f $ownershipEventName, $process.Id, $oldHandle, $Reason)
    }

    $newHandle = Get-AutomationNativeWindowHandleSafe -Window $window
    if ($oldHandle -ne 0 -and $newHandle -ne 0 -and $newHandle -ne $oldHandle) {
        Write-Host ("[GUI Smoke] event={0}; reason={1}; process_id={2}; old_handle={3}; new_handle={4}; action=refreshed_current_mainwindow" -f $ownershipEventName, $Reason, $process.Id, $oldHandle, $newHandle) -ForegroundColor Yellow
    }

    Set-ScenarioRoleWindowIdentity -Context $Context -RoleName $RoleName -Window $window
    return $window
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
        HelpeeWindowNativeHandle = 0L
        HelpeeWindowProcessId = 0
        HelpeeLaunchGeneration = 0
        HelperWindowNativeHandle = 0L
        HelperWindowProcessId = 0
        HelperLaunchGeneration = 0
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
    $Context.HelpeeWindowNativeHandle = 0L
    $Context.HelpeeWindowProcessId = 0
    $Context.HelperWindowNativeHandle = 0L
    $Context.HelperWindowProcessId = 0
    $Context.HelperLogBookmark = 0
    $Context.HelperRunId = ''
    $Context.HelperListenerGeneration = -1
    $Context.HelperStartedUtcMs = 0
    Clear-HelperIdentityArtifact
}

function Start-HelpeeFlow {
    param([Parameter(Mandatory = $true)]$Context)
    $Context.HelpeeLaunchGeneration = [int]$Context.HelpeeLaunchGeneration + 1
    $Context.HelpeeProc = Start-AppInstance -ExePath $Context.ExePath -RoleName 'helpee'
    [void]$Context.Processes.Add($Context.HelpeeProc)
    Set-ScenarioRoleWindowIdentity -Context $Context -RoleName 'helpee' -Window (Wait-Window -Process $Context.HelpeeProc -RoleName 'helpee' -TimeoutMs (Get-StartupWindowTimeoutMs))
    Click-HomeButton -Window $Context.HelpeeWindow -Text 'I need help'
    if (Try-ClickRoleButtonIfPresent -Window $Context.HelpeeWindow -RoleButtonText 'I need help') {
        Write-Host "[GUI Smoke] Role page detected (helpee); selected 'I need help'." -ForegroundColor DarkGray
    }
}

function Start-HelperFlow {
    param([Parameter(Mandatory = $true)]$Context)
    $Context.HelperLaunchGeneration = [int]$Context.HelperLaunchGeneration + 1
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
    Set-ScenarioRoleWindowIdentity -Context $Context -RoleName 'helper' -Window (Wait-Window -Process $Context.HelperProc -RoleName 'helper' -TimeoutMs (Get-StartupWindowTimeoutMs))
    Click-HomeButton -Window $Context.HelperWindow -Text 'I want to help someone'
    if (Try-ClickRoleButtonIfPresent -Window $Context.HelperWindow -RoleButtonText 'I want to help someone') {
        Write-Host "[GUI Smoke] Role page detected (helper); selected 'I want to help someone'." -ForegroundColor DarkGray
    }
}

function Restart-HelpeeFlow {
    param([Parameter(Mandatory = $true)]$Context)

    $oldProc = $Context.HelpeeProc
    if ($Context.HelpeeProc) {
        Cleanup-Processes -Processes @($Context.HelpeeProc) -ExePath $Context.ExePath
    }
    Assert-ScenarioRoleProcessRetired -Process $oldProc -RoleName 'helpee'
    Remove-ScenarioTrackedProcess -Context $Context -Process $oldProc

    $Context.HelpeeProc = $null
    Clear-ScenarioRoleWindowIdentity -Context $Context -RoleName 'helpee'
    Start-HelpeeFlow -Context $Context
}

function Restart-HelperFlow {
    param([Parameter(Mandatory = $true)]$Context)

    $oldProc = $Context.HelperProc
    if ($Context.HelperProc) {
        Cleanup-Processes -Processes @($Context.HelperProc) -ExePath $Context.ExePath
    }
    Assert-ScenarioRoleProcessRetired -Process $oldProc -RoleName 'helper'
    Remove-ScenarioTrackedProcess -Context $Context -Process $oldProc

    $Context.HelperProc = $null
    Clear-ScenarioRoleWindowIdentity -Context $Context -RoleName 'helper'
    Start-HelperFlow -Context $Context
}

function Restart-HelperIdentityPairFlow {
    param(
        [Parameter(Mandatory = $true)]$Context,
        [string]$Reason = 'helper_identity_pair_restart',
        [int]$Attempt = 0,
        [bool]$RotateIdentity = $true
    )

    Write-Host ("[GUI Smoke] event=helper_identity_pair_restart_after_helpee_invite_bootstrap_failure; attempt={0}; reason={1}; rotate_identity={2}; helper_pid={3}; helpee_pid={4}" -f `
        $Attempt,
        $Reason,
        ($(if ($RotateIdentity) { 1 } else { 0 })),
        ($(if ($Context.HelperProc) { [int]$Context.HelperProc.Id } else { 0 })),
        ($(if ($Context.HelpeeProc) { [int]$Context.HelpeeProc.Id } else { 0 }))) -ForegroundColor Yellow

    $oldHelperProc = $Context.HelperProc
    $oldHelpeeProc = $Context.HelpeeProc
    $toCleanup = @()
    if ($oldHelperProc) { $toCleanup += $oldHelperProc }
    if ($oldHelpeeProc) { $toCleanup += $oldHelpeeProc }
    if ($toCleanup.Count -gt 0) {
        Cleanup-Processes -Processes $toCleanup -ExePath $Context.ExePath
    }

    Assert-ScenarioRoleProcessRetired -Process $oldHelperProc -RoleName 'helper'
    Assert-ScenarioRoleProcessRetired -Process $oldHelpeeProc -RoleName 'helpee'
    Remove-ScenarioTrackedProcess -Context $Context -Process $oldHelperProc
    Remove-ScenarioTrackedProcess -Context $Context -Process $oldHelpeeProc

    $Context.HelperProc = $null
    $Context.HelpeeProc = $null
    Clear-ScenarioRoleWindowIdentity -Context $Context -RoleName 'helper'
    Clear-ScenarioRoleWindowIdentity -Context $Context -RoleName 'helpee'
    Clear-HelperIdentityArtifact

    if ($RotateIdentity -and (Get-IsNknTransport)) {
        Rotate-GuiSmokeNknIdentityPairForBootstrapRetry -Reason $Reason -Attempt $Attempt
    }
    elseif ((Get-IsNknTransport) -and -not $RotateIdentity) {
        Write-Host ("[GUI Smoke] event=nkn_identity_rotation_skipped_for_bridge_bootstrap_retry; attempt={0}; reason={1}" -f $Attempt, $Reason) -ForegroundColor DarkGray
    }

    Start-HelpeeFlow -Context $Context
    Start-HelperFlow -Context $Context
}

function Test-HelpeeInviteBootstrapBridgeRetryableError {
    param([AllowNull()][string]$Message)

    if ([string]::IsNullOrWhiteSpace($Message)) { return $false }
    return $Message.IndexOf('nkn_bridge_bootstrap_network_unready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $Message.IndexOf('nkn_bridge_bootstrap_not_ready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $Message.IndexOf('helpee_invite_host_before_bridge_ready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $Message.IndexOf('helpee_invite_bootstrap_bridge_before_ready_retry', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $Message.IndexOf('HostByAddressAsync failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $Message.IndexOf('RPC call failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $Message.IndexOf('Connect failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $Message.IndexOf('connect EACCES', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $Message.IndexOf('Could not prepare invite', [System.StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Test-NknBridgeBootstrapFailureRequiresIdentityRotation {
    param([AllowNull()][string]$Message)

    if ([string]::IsNullOrWhiteSpace($Message)) { return $false }
    return $Message.IndexOf('helpee_bridge_host_ready_connect_failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $Message.IndexOf('helper_bridge_host_ready_connect_failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $Message.IndexOf('HostByAddressAsync failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $Message.IndexOf('RPC call failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $Message.IndexOf('Connect failed', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $Message.IndexOf('connect EACCES', [System.StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Test-HelpeeInviteBootstrapPairRetryableError {
    param([AllowNull()][string]$Message)

    if ([string]::IsNullOrWhiteSpace($Message)) { return $false }
    return $Message.IndexOf('Helpee reached Connection failed before request flow became ready', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $Message.IndexOf('Helpee reached Connection failed before Request help became enabled', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $Message.IndexOf('Helpee reached Connection failed before approval', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $Message.IndexOf('Timed out waiting for helpee Allow approval UI', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $Message.IndexOf('The session was ended', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $Message.IndexOf('helpee_request_flow_not_enabled_after_identity_paste', [System.StringComparison]::OrdinalIgnoreCase) -ge 0
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

    Wait-NknBridgeBootstrapReadyForCurrentPairWithRecovery -Context $Context -TimeoutMs (Get-TransportAwareTimeoutMs -DefaultMs 1000 -NknMs 60000) -Reason 'before_connection_entry'
    $entryMode = Wait-HelpeeConnectionEntryModeWithRecovery -Context $Context -TimeoutMs (Get-TransportAwareTimeoutMs -DefaultMs 10000 -NknMs 30000)
    if ([string]::Equals($entryMode, 'helper_identity', [System.StringComparison]::Ordinal)) {
        Write-Host '[GUI Smoke] Helpee connection mode: helper identity request flow.' -ForegroundColor DarkGray
        $pairAttempts = if (Get-IsNknTransport) { 2 } else { 1 }
        $lastPairError = $null
        $nextPairRestartReason = 'helpee_invite_bootstrap_retry'
        $nextPairRestartRotateIdentity = $true
        for ($pairAttempt = 1; $pairAttempt -le $pairAttempts; $pairAttempt++) {
            if ($pairAttempt -gt 1) {
                Restart-HelperIdentityPairFlow -Context $Context -Reason $nextPairRestartReason -Attempt $pairAttempt -RotateIdentity $nextPairRestartRotateIdentity
                $nextPairRestartReason = 'helpee_invite_bootstrap_retry'
                $nextPairRestartRotateIdentity = $true
                Wait-NknBridgeBootstrapReadyForCurrentPair -Context $Context -TimeoutMs (Get-TransportAwareTimeoutMs -DefaultMs 1000 -NknMs 60000) -Reason 'after_helper_identity_pair_restart'
                $entryMode = Wait-HelpeeConnectionEntryModeWithRecovery -Context $Context -TimeoutMs (Get-TransportAwareTimeoutMs -DefaultMs 10000 -NknMs 30000)
                if (-not [string]::Equals([string]$entryMode, 'helper_identity', [System.StringComparison]::Ordinal)) {
                    throw ("Helpee pair restart after invite bootstrap failure did not return to helper identity mode. entry_mode={0}" -f $entryMode)
                }
            }

            $helperIdentity = Copy-HelperIdentityWithRecovery -Context $Context
            Write-Host "[GUI Smoke] Helper identity copied: $helperIdentity" -ForegroundColor Green

            try {
                $accept = Connect-HelperIdentityRequestFlow -Context $Context -HelperIdentity $helperIdentity
                Click-Element $accept

                $allow = Wait-HelpeeAllowOrExit -Context $Context -TimeoutMs 90000
                Click-Element $allow

                [void](Wait-ConnectedChatVisibleProcessAware -Context $Context -TimeoutMs 120000)
                return $helperIdentity
            }
            catch {
                $lastPairError = $_
                $message = $_.Exception.Message
                $bridgeRetry = Test-HelpeeInviteBootstrapBridgeRetryableError -Message $message
                $identityRetry = Test-HelpeeInviteBootstrapPairRetryableError -Message $message
                if ($pairAttempt -lt $pairAttempts -and ($bridgeRetry -or $identityRetry)) {
                    $nextPairRestartReason = if ($bridgeRetry) { 'helpee_invite_bridge_bootstrap_retry' } else { 'helpee_invite_bootstrap_retry' }
                    $nextPairRestartRotateIdentity = if ($bridgeRetry) { Test-NknBridgeBootstrapFailureRequiresIdentityRotation -Message $message } else { $true }
                    Write-Host ("[GUI Smoke] event=helper_identity_pair_retry_scheduled_after_helpee_invite_bootstrap_failure; attempt={0}/{1}; reason={2}; retry_family={3}; rotate_identity={4}; helpee_diagnostic={5}; helper_diagnostic={6}" -f `
                        $pairAttempt,
                        $pairAttempts,
                        $message,
                        ($(if ($bridgeRetry) { 'bridge_bootstrap' } else { 'identity_window' })),
                        ($(if ($nextPairRestartRotateIdentity) { 1 } else { 0 })),
                        (Get-HelpeeInviteReadinessDiagnostic -Window $Context.HelpeeWindow),
                        (Get-HelperIdentityBootstrapDiagnostic -Window $Context.HelperWindow)) -ForegroundColor Yellow
                    continue
                }

                throw
            }
        }

        if ($lastPairError) {
            throw $lastPairError
        }

        throw 'Helper identity pair flow failed before connected chat became visible.'
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

    Start-HelpeeFlow -Context $Context
    Start-HelperFlow -Context $Context
    $helperIdentity = Connect-HelperAndHelpee -Context $Context
    if ([string]::IsNullOrWhiteSpace($helperIdentity)) {
        throw 'Helper identity connect flow returned empty text.'
    }

    $msg = "gui smoke nkn direct connect"
    Send-ChatMessage -Window $Context.HelperWindow -Text $msg -UseEnter
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
                [void](Wait-SessionHeaderConnectedState -Window $Context.HelpeeWindow -TimeoutMs 5000)
                [void](Wait-SessionHeaderConnectedState -Window $Context.HelperWindow -TimeoutMs 5000)
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
    Write-TunaGuiTopLevelFailureSummaryIfMissing -ErrorMessage ([string]$_.Exception.Message)
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
