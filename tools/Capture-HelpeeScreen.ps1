param(
    [string]$ExePath = ".\src\nLink.App\bin\Release\net8.0\nLink.exe",
    [string]$OutPath = ".\artifacts\manual\helpee-screen-now.png"
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
            $r = & $Condition
            if ($r) { return $r }
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

$resolvedExe = (Resolve-Path $ExePath).Path
$resolvedOut = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutPath)
$outDir = Split-Path -Parent $resolvedOut
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

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

    $helpeeButton = Wait-Until -TimeoutMs 12000 -PollMs 200 -OnTimeoutMessage "I need help button timeout" -Condition {
        $x = Find-ByNameAndType $window "I need help" ([System.Windows.Automation.ControlType]::Button)
        if ($x -and -not $x.Current.IsOffscreen) { $x }
    }
    Click $helpeeButton
    Start-Sleep -Milliseconds 700

    $roleTitle = Find-ByNameAndType $window "Choose your role" ([System.Windows.Automation.ControlType]::Text)
    if ($roleTitle -and -not $roleTitle.Current.IsOffscreen) {
        $roleButton = Find-ByNameAndType $window "I need help" ([System.Windows.Automation.ControlType]::Button)
        if ($roleButton -and -not $roleButton.Current.IsOffscreen -and $roleButton.Current.IsEnabled) {
            Click $roleButton
            Start-Sleep -Milliseconds 400
        }
    }

    Start-Sleep -Milliseconds 1000

    $mainWindowHandle = (Get-Process -Id $proc.Id).MainWindowHandle
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
    $bitmap.Save($resolvedOut, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose()
    $bitmap.Dispose()

    Write-Output $resolvedOut
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
