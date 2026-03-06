param(
    [string]$ExePath = ".\src\nLink.App\bin\Release\net8.0\nLink.exe",
    [string]$OutDir = ".\docs\images",
    [string]$Version = "0.4.0"
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

function Click($Element) {
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
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

$resolvedExe = (Resolve-Path $ExePath).Path
$resolvedOutDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir)
New-Item -ItemType Directory -Force -Path $resolvedOutDir | Out-Null

$oldTransport = $env:NLINK_TRANSPORT
if ([string]::IsNullOrWhiteSpace($env:NLINK_TRANSPORT)) {
    $env:NLINK_TRANSPORT = "DEVLOCAL"
}

Get-Process nLink -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300

$proc = Start-Process -FilePath $resolvedExe -PassThru
try {
    $window = Wait-Until -TimeoutMs 20000 -PollMs 200 -OnTimeoutMessage "Window timeout" -Condition {
        Get-Window $proc.Id
    }

    $helperButton = Wait-Until -TimeoutMs 12000 -PollMs 200 -OnTimeoutMessage "Helper button timeout" -Condition {
        $x = Find-ByNameAndType $window "I want to help someone" ([System.Windows.Automation.ControlType]::Button)
        if ($x -and -not $x.Current.IsOffscreen) { $x }
    }

    Save-Capture $proc.Id (Join-Path $resolvedOutDir ("home-{0}.png" -f $Version))

    Click $helperButton
    Start-Sleep -Milliseconds 700

    $roleTitle = Find-ByNameAndType $window "Choose your role" ([System.Windows.Automation.ControlType]::Text)
    if ($roleTitle -and -not $roleTitle.Current.IsOffscreen) {
        $roleButton = Find-ByNameAndType $window "I want to help someone" ([System.Windows.Automation.ControlType]::Button)
        if ($roleButton -and -not $roleButton.Current.IsOffscreen -and $roleButton.Current.IsEnabled) {
            Click $roleButton
            Start-Sleep -Milliseconds 400
        }
    }

    $connectButton = Wait-Until -TimeoutMs 12000 -PollMs 200 -OnTimeoutMessage "Connect button timeout" -Condition {
        $x = Find-ByNameAndType $window "Connect" ([System.Windows.Automation.ControlType]::Button)
        if ($x -and -not $x.Current.IsOffscreen) { $x }
    }

    Start-Sleep -Milliseconds 500
    Save-Capture $proc.Id (Join-Path $resolvedOutDir ("helper-{0}.png" -f $Version))

    Write-Output (Join-Path $resolvedOutDir ("home-{0}.png" -f $Version))
    Write-Output (Join-Path $resolvedOutDir ("helper-{0}.png" -f $Version))
}
finally {
    try {
        if ($proc -and -not $proc.HasExited) {
            Stop-Process -Id $proc.Id -Force
        }
    }
    catch {}

    if ($null -eq $oldTransport) {
        Remove-Item Env:NLINK_TRANSPORT -ErrorAction SilentlyContinue
    }
    else {
        $env:NLINK_TRANSPORT = $oldTransport
    }
}
