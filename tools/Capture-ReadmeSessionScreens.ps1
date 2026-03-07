param(
    [string]$ExePath = ".\src\nLink.App\bin\Release\net8.0\nLink.exe",
    [string]$OutDir = ".\docs\images",
    [string]$Version = "0.4.1"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32Capture {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
}
"@

function Wait-Until {
    param(
        [int]$TimeoutMs,
        [int]$PollMs,
        [scriptblock]$Condition,
        [string]$OnTimeoutMessage
    )

    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        try {
            $result = & $Condition
            if ($result) {
                return $result
            }
        }
        catch {}

        Start-Sleep -Milliseconds $PollMs
    }

    throw $OnTimeoutMessage
}

function Get-Window([int]$ProcessId) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $proc = [System.Windows.Automation.AutomationElement]::ProcessIdProperty
    $name = [System.Windows.Automation.AutomationElement]::NameProperty
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($proc, $ProcessId)),
        (New-Object System.Windows.Automation.PropertyCondition($name, "nLink"))
    )
    $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
}

function Find-ByNameAndType($Root, [string]$Name, $Type) {
    $np = [System.Windows.Automation.AutomationElement]::NameProperty
    $tp = [System.Windows.Automation.AutomationElement]::ControlTypeProperty
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($np, $Name)),
        (New-Object System.Windows.Automation.PropertyCondition($tp, $Type))
    )
    $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-VisibleByAutomationId($Root, [string]$AutomationId) {
    $id = [System.Windows.Automation.AutomationElement]::AutomationIdProperty
    $cond = New-Object System.Windows.Automation.PropertyCondition($id, $AutomationId)
    $all = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    foreach ($element in @($all)) {
        if ($element -and -not $element.Current.IsOffscreen) {
            return $element
        }
    }

    return $null
}

function Find-VisibleByAutomationIdOrName($Root, [string]$AutomationId, [string]$Name, $Type) {
    $candidate = Find-VisibleByAutomationId $Root $AutomationId
    if ($candidate) {
        return $candidate
    }

    $candidate = Find-ByNameAndType $Root $Name $Type
    if ($candidate -and -not $candidate.Current.IsOffscreen) {
        return $candidate
    }

    return $null
}

function Click($Element) {
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
}

function Try-SetValue($Element, [string]$Value) {
    try {
        if (-not $Element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
            return $false
        }

        ([System.Windows.Automation.ValuePattern]$pattern).SetValue($Value)
        return $true
    }
    catch {
        return $false
    }
}

function Set-Text($Element, [string]$Text) {
    if (Try-SetValue $Element $Text) {
        return
    }

    $element.SetFocus()
    Start-Sleep -Milliseconds 80
    [System.Windows.Forms.SendKeys]::SendWait("^a")
    Start-Sleep -Milliseconds 50
    [System.Windows.Forms.SendKeys]::SendWait($Text)
}

function Save-Capture([int]$ProcessId, [string]$Path) {
    $mainWindowHandle = (Get-Process -Id $ProcessId).MainWindowHandle
    if ($mainWindowHandle -eq 0) {
        throw "MainWindowHandle unavailable."
    }

    $rect = New-Object Win32Capture+RECT
    [void][Win32Capture]::GetWindowRect($mainWindowHandle, [ref]$rect)
    $width = [Math]::Max(1, $rect.Right - $rect.Left)
    $height = [Math]::Max(1, $rect.Bottom - $rect.Top)

    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
    $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose()
    $bitmap.Dispose()
}

function Wait-Window([System.Diagnostics.Process]$Process) {
    Wait-Until -TimeoutMs 20000 -PollMs 200 -OnTimeoutMessage "Window timeout for pid=$($Process.Id)" -Condition {
        Get-Window $Process.Id
    }
}

function Click-HomeRole($Window, [string]$ButtonText) {
    $button = Wait-Until -TimeoutMs 12000 -PollMs 200 -OnTimeoutMessage "Timed out waiting for '$ButtonText'." -Condition {
        $candidate = Find-ByNameAndType $Window $ButtonText ([System.Windows.Automation.ControlType]::Button)
        if ($candidate -and -not $candidate.Current.IsOffscreen) {
            return $candidate
        }

        return $null
    }

    Click $button
    Start-Sleep -Milliseconds 600

    $roleTitle = Find-ByNameAndType $Window "Choose your role" ([System.Windows.Automation.ControlType]::Text)
    if ($roleTitle -and -not $roleTitle.Current.IsOffscreen) {
        $roleButton = Find-ByNameAndType $Window $ButtonText ([System.Windows.Automation.ControlType]::Button)
        if ($roleButton -and -not $roleButton.Current.IsOffscreen -and $roleButton.Current.IsEnabled) {
            Click $roleButton
            Start-Sleep -Milliseconds 500
        }
    }
}

function Copy-Invite($HelpeeWindow) {
    $copyButton = Wait-Until -TimeoutMs 15000 -PollMs 200 -OnTimeoutMessage "Timed out waiting for helpee invite copy button." -Condition {
        $candidate = Find-VisibleByAutomationIdOrName $HelpeeWindow "Helpee.CopyInvite" "Copy invite" ([System.Windows.Automation.ControlType]::Button)
        if ($candidate -and $candidate.Current.IsEnabled) {
            return $candidate
        }

        return $null
    }

    Click $copyButton

    return Wait-Until -TimeoutMs 7000 -PollMs 150 -OnTimeoutMessage "Timed out waiting for invite text on clipboard." -Condition {
        $text = [string](Get-Clipboard)
        if (-not [string]::IsNullOrWhiteSpace($text) -and $text.Trim().Length -gt 20) {
            return $text.Trim()
        }

        return $null
    }
}

function Connect-Pair($HelpeeWindow, $HelperWindow) {
    $invite = Copy-Invite $HelpeeWindow

    $input = Wait-Until -TimeoutMs 12000 -PollMs 200 -OnTimeoutMessage "Timed out waiting for helper invite input." -Condition {
        $candidate = Find-VisibleByAutomationId $HelperWindow "Helper.CodeInput"
        if ($candidate -and $candidate.Current.IsEnabled) {
            return $candidate
        }

        return $null
    }

    Set-Text $input $invite

    $connect = Find-VisibleByAutomationIdOrName $HelperWindow "Helper.Connect" "Connect" ([System.Windows.Automation.ControlType]::Button)
    if (-not $connect -or -not $connect.Current.IsEnabled) {
        $pasteButton = Wait-Until -TimeoutMs 8000 -PollMs 200 -OnTimeoutMessage "Timed out waiting for helper Paste invite button." -Condition {
            $candidate = Find-VisibleByAutomationIdOrName $HelperWindow "Helper.PasteFromClipboard" "Paste invite" ([System.Windows.Automation.ControlType]::Button)
            if ($candidate -and $candidate.Current.IsEnabled) {
                return $candidate
            }

            return $null
        }

        Click $pasteButton
    }

    $connect = Wait-Until -TimeoutMs 12000 -PollMs 200 -OnTimeoutMessage "Timed out waiting for helper Connect." -Condition {
        $candidate = Find-VisibleByAutomationIdOrName $HelperWindow "Helper.Connect" "Connect" ([System.Windows.Automation.ControlType]::Button)
        if ($candidate -and $candidate.Current.IsEnabled) {
            return $candidate
        }

        return $null
    }

    Click $connect

    $allow = Wait-Until -TimeoutMs 25000 -PollMs 200 -OnTimeoutMessage "Timed out waiting for helpee Allow." -Condition {
        $candidate = Find-VisibleByAutomationIdOrName $HelpeeWindow "Helpee.Allow" "Allow" ([System.Windows.Automation.ControlType]::Button)
        if ($candidate -and $candidate.Current.IsEnabled) {
            return $candidate
        }

        return $null
    }

    Click $allow

    [void](Wait-Until -TimeoutMs 15000 -PollMs 200 -OnTimeoutMessage "Timed out waiting for both sides to connect." -Condition {
        $helperStatus = Find-VisibleByAutomationId $HelperWindow "SessionHeader.StatusText"
        $helpeeStatus = Find-VisibleByAutomationId $HelpeeWindow "SessionHeader.StatusText"
        if ($helperStatus -and $helpeeStatus) {
            $helperText = [string]$helperStatus.Current.Name
            $helpeeText = [string]$helpeeStatus.Current.Name
            if ($helperText.Contains("Connected") -and $helpeeText.Contains("Connected")) {
                return $true
            }
        }

        return $null
    })
}

function Wait-ScreenShareStarted($HelpeeWindow, $HelperWindow) {
    $shareButton = Wait-Until -TimeoutMs 15000 -PollMs 200 -OnTimeoutMessage "Timed out waiting for helpee Share screen." -Condition {
        $candidate = Find-VisibleByAutomationId $HelpeeWindow "SessionHeader.ShareScreen"
        if ($candidate -and $candidate.Current.IsEnabled) {
            return $candidate
        }

        return $null
    }

    Click $shareButton

    [void](Wait-Until -TimeoutMs 12000 -PollMs 200 -OnTimeoutMessage "Timed out waiting for screen sharing to start." -Condition {
        $viewerError = Find-VisibleByAutomationId $HelpeeWindow "ScreenShare.ViewerMessage"
        if ($viewerError) {
            $message = [string]$viewerError.Current.Name
            throw "Screen sharing failed to start: $message"
        }

        $helpeeShare = Find-VisibleByAutomationId $HelpeeWindow "SessionHeader.ShareScreen"
        if ($helpeeShare) {
            $shareText = [string]$helpeeShare.Current.Name
            if ($shareText.Contains("Stop sharing")) {
                return $true
            }
        }

        return $null
    })

    Start-Sleep -Milliseconds 800
}

function Wait-RemoteControlStarted($HelpeeWindow, $HelperWindow) {
    $requestButton = Wait-Until -TimeoutMs 30000 -PollMs 200 -OnTimeoutMessage "Timed out waiting for helper Request control." -Condition {
        $candidate = Find-VisibleByAutomationId $HelperWindow "SessionHeader.RequestControl"
        if ($candidate -and $candidate.Current.IsEnabled) {
            return $candidate
        }

        return $null
    }

    Click $requestButton

    $allowControl = Wait-Until -TimeoutMs 15000 -PollMs 200 -OnTimeoutMessage "Timed out waiting for helpee Allow control." -Condition {
        $candidate = Find-VisibleByAutomationIdOrName $HelpeeWindow "Helpee.ControlConsent.Allow" "Allow control" ([System.Windows.Automation.ControlType]::Button)
        if ($candidate -and $candidate.Current.IsEnabled) {
            return $candidate
        }

        return $null
    }

    Click $allowControl

    [void](Wait-Until -TimeoutMs 15000 -PollMs 200 -OnTimeoutMessage "Timed out waiting for helper remote control to become active." -Condition {
        $activeBadge = Find-VisibleByAutomationId $HelperWindow "SessionHeader.RemoteControlActive"
        $status = Find-VisibleByAutomationId $HelperWindow "SessionHeader.StatusText"
        if ($activeBadge -and $status) {
            $statusText = [string]$status.Current.Name
            if (-not $statusText.Contains("mapping unavailable")) {
                return $true
            }
        }

        return $null
    })

    $controlMode = Find-VisibleByAutomationId $HelperWindow "SessionHeader.ControlMode"
    if ($controlMode -and $controlMode.Current.IsEnabled) {
        $label = [string]$controlMode.Current.Name
        if ($label.Contains("Off")) {
            Click $controlMode
            Start-Sleep -Milliseconds 500
        }
    }
}

$resolvedExe = (Resolve-Path $ExePath).Path
$resolvedOutDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir)
New-Item -ItemType Directory -Force -Path $resolvedOutDir | Out-Null

$oldTransport = $env:NLINK_TRANSPORT
$oldScaffold = $env:NLINK_FEATURE_SCREENCAP_SCAFFOLD
$processes = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()

try {
    $env:NLINK_TRANSPORT = "DEVLOCAL"
    $env:NLINK_FEATURE_SCREENCAP_SCAFFOLD = "1"

    Get-Process nLink -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500

    $helpee = Start-Process -FilePath $resolvedExe -PassThru
    $processes.Add($helpee)
    $helpeeWindow = Wait-Window $helpee
    Click-HomeRole $helpeeWindow "I need help"

    $helper = Start-Process -FilePath $resolvedExe -PassThru
    $processes.Add($helper)
    $helperWindow = Wait-Window $helper
    Click-HomeRole $helperWindow "I want to help someone"

    Connect-Pair $helpeeWindow $helperWindow
    Wait-ScreenShareStarted $helpeeWindow $helperWindow

    $screenShareOut = Join-Path $resolvedOutDir ("screenshare-{0}.png" -f $Version)
    Save-Capture $helpee.Id $screenShareOut
    Write-Output $screenShareOut

    Wait-RemoteControlStarted $helpeeWindow $helperWindow

    $remoteControlOut = Join-Path $resolvedOutDir ("remote-control-{0}.png" -f $Version)
    Save-Capture $helper.Id $remoteControlOut
    Write-Output $remoteControlOut
}
finally {
    foreach ($process in @($processes)) {
        try {
            if ($process -and -not $process.HasExited) {
                Stop-Process -Id $process.Id -Force
            }
        }
        catch {}
    }

    if ($null -eq $oldTransport) {
        Remove-Item Env:NLINK_TRANSPORT -ErrorAction SilentlyContinue
    }
    else {
        $env:NLINK_TRANSPORT = $oldTransport
    }

    if ($null -eq $oldScaffold) {
        Remove-Item Env:NLINK_FEATURE_SCREENCAP_SCAFFOLD -ErrorAction SilentlyContinue
    }
    else {
        $env:NLINK_FEATURE_SCREENCAP_SCAFFOLD = $oldScaffold
    }
}
