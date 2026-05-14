param(
    [string]$WalletPath = ".\artifacts\tuna-poc\wallet-test-nkn.json",
    [string]$WalletPassword = "",
    [string]$SidecarPath = ".\artifacts\portable\nLink\win-x64\tuna\win-x64\nlink-tuna-sidecar.exe",
    [ValidateSet("helpee", "helper", "both")]
    [string]$PayerMode = "helpee",
    [ValidateSet("none", "switch-off", "sidecar-kill")]
    [string]$Fault = "switch-off",
    [ValidateSet("helpee-to-helper", "helper-to-helpee")]
    [string]$Direction = "helpee-to-helper",
    [string]$PayloadSize = "128MiB",
    [int]$TimeoutSeconds = 900,
    [int]$ProgressTimeoutSeconds = 180,
    [string]$ArtifactDir = "",
    [string]$ExePath = ".\artifacts\portable\nLink\win-x64\nLink.exe",
    [switch]$Build
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {
    if ([string]::IsNullOrWhiteSpace($WalletPassword)) {
        $WalletPassword = [string]$env:NLINK_TUNA_TEST_WALLET_PASSWORD
    }

    if ([string]::IsNullOrWhiteSpace($WalletPassword)) {
        throw 'Provide -WalletPassword or set NLINK_TUNA_TEST_WALLET_PASSWORD.'
    }

    if ($Build) {
        & "$env:ProgramFiles\PowerShell\7\pwsh.exe" -NoProfile -ExecutionPolicy Bypass -File ".\installer\Build-Portable.ps1" -Runtime win-x64
    }

    $resolvedWallet = (Resolve-Path -LiteralPath $WalletPath).Path
    $resolvedSidecar = (Resolve-Path -LiteralPath $SidecarPath).Path
    $sidecarManifest = Join-Path ([System.IO.Path]::GetDirectoryName($resolvedSidecar)) 'tuna-sidecar-manifest.json'
    if (-not (Test-Path -LiteralPath $sidecarManifest)) {
        $packagedSidecar = Join-Path $repoRoot 'artifacts\portable\nLink\win-x64\tuna\win-x64\nlink-tuna-sidecar.exe'
        $packagedManifest = Join-Path ([System.IO.Path]::GetDirectoryName($packagedSidecar)) 'tuna-sidecar-manifest.json'
        if (-not (Test-Path -LiteralPath $packagedSidecar) -or -not (Test-Path -LiteralPath $packagedManifest)) {
            throw "Tuna sidecar manifest missing beside '$resolvedSidecar'. Build portable/installer first."
        }

        $resolvedSidecar = (Resolve-Path -LiteralPath $packagedSidecar).Path
        Write-Host "[Tuna GUI] Using packaged sidecar with manifest: $resolvedSidecar" -ForegroundColor DarkGray
    }
    $resolvedExe = (Resolve-Path -LiteralPath $ExePath).Path
    $exeDirectory = [System.IO.Path]::GetDirectoryName($resolvedExe)
    $bundledBridge = Join-Path $exeDirectory 'bridge\win-x64\index.js'
    if (-not (Test-Path -LiteralPath $bundledBridge)) {
        $repoBridgeDir = Join-Path $repoRoot 'artifacts\bridge\win-x64'
        $repoBridge = Join-Path $repoBridgeDir 'index.js'
        if (-not (Test-Path -LiteralPath $repoBridge)) {
            $repoBridgeDir = Join-Path $repoRoot 'artifacts\portable\nLink\win-x64\bridge\win-x64'
            $repoBridge = Join-Path $repoBridgeDir 'index.js'
        }

        if (-not (Test-Path -LiteralPath $repoBridge)) {
            throw "NKN bridge bundle not found beside ExePath or under repo artifacts. Build portable/installer first."
        }

        $repoNode = Join-Path $repoBridgeDir 'node.exe'
        if (-not (Test-Path -LiteralPath $repoNode)) {
            throw "NKN Node runtime not found beside repo bridge bundle. Build portable/installer first."
        }

        $env:NLINK_NKN_BRIDGE_PATH = (Resolve-Path -LiteralPath $repoBridge).Path
        $env:NLINK_NKN_NODE_PATH = (Resolve-Path -LiteralPath $repoNode).Path
        Write-Host "[Tuna GUI] Using bridge override: $($env:NLINK_NKN_BRIDGE_PATH)" -ForegroundColor DarkGray
        Write-Host "[Tuna GUI] Using node override: $($env:NLINK_NKN_NODE_PATH)" -ForegroundColor DarkGray
    }

    if ([string]::IsNullOrWhiteSpace($ArtifactDir)) {
        $timestamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'")
        $ArtifactDir = Join-Path $repoRoot "artifacts\gui-smoke\tuna-filetransfer-$timestamp"
    }

    $resolvedArtifactDir = [System.IO.Path]::GetFullPath($ArtifactDir)
    $repoArtifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
    if (-not $resolvedArtifactDir.StartsWith($repoArtifactsRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "ArtifactDir must resolve under repo artifacts/: $resolvedArtifactDir"
    }

    New-Item -ItemType Directory -Force -Path $resolvedArtifactDir | Out-Null
    $receivedRoot = Join-Path $resolvedArtifactDir 'received'
    New-Item -ItemType Directory -Force -Path $receivedRoot | Out-Null

    $env:NLINK_RUN_GUI_SMOKE = '1'
    $env:NLINK_RUN_TUNA_GUI_FILETRANSFER = '1'
    $env:NLINK_TRANSPORT = 'NKN'
    $env:NLINK_GUI_SMOKE_SCENARIOS = 'FILETRANSFER_TUNA_HANDOFF_FALLBACK'
    $env:NLINK_TUNA_GUI_WALLET_PATH = $resolvedWallet
    $env:NLINK_TUNA_TEST_WALLET_PASSWORD = $WalletPassword
    $env:NLINK_TUNA_GUI_SIDECAR_EXE = $resolvedSidecar
    $env:NLINK_TUNA_GUI_PAYER_MODE = $PayerMode
    $env:NLINK_TUNA_GUI_FAULT = $Fault
    $env:NLINK_TUNA_GUI_EXERCISE_PAUSE = '1'
    $env:NLINK_FILETRANSFER_SOAK_DIRECTION = $Direction
    $env:NLINK_FILETRANSFER_SOAK_PAYLOAD_SIZES = $PayloadSize
    $env:NLINK_FILETRANSFER_SOAK_CYCLE_TIMEOUT_SECONDS = [string]$TimeoutSeconds
    $env:NLINK_FILETRANSFER_SOAK_PROGRESS_TIMEOUT_SECONDS = [string]$ProgressTimeoutSeconds
    $env:NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR = $resolvedArtifactDir
    $env:NLINK_FILE_TRANSFER_TEST_INBOUND_ROOT = $receivedRoot

    Write-Host "[Tuna GUI] Running file-transfer handoff/fallback GUI smoke." -ForegroundColor Cyan
    Write-Host "[Tuna GUI] Artifacts: $resolvedArtifactDir" -ForegroundColor DarkGray
    Write-Host "[Tuna GUI] Direction=$Direction Payer=$PayerMode Fault=$Fault Payload=$PayloadSize" -ForegroundColor DarkGray

    & ".\tools\GuiSmoke-Windows.ps1" -ExePath $resolvedExe -TimeoutSeconds $TimeoutSeconds
    $guiSmokeExitCode = $LASTEXITCODE
    if ($guiSmokeExitCode -ne 0) {
        throw "GUI smoke failed with exit code $guiSmokeExitCode. Artifacts: $resolvedArtifactDir"
    }

    $summaryPath = Join-Path $resolvedArtifactDir 'filetransfer-tuna-gui-summary.json'
    if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf)) {
        throw "GUI smoke did not write file-transfer Tuna summary. Artifacts: $resolvedArtifactDir"
    }

    $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
    if (-not [bool]$summary.completed -or -not [bool]$summary.integrityOk) {
        throw ("GUI smoke summary reports incomplete transfer. completed={0}; integrity_ok={1}; inbound_state={2}; outbound_state={3}; inbound_error={4}; outbound_error={5}; artifacts={6}" -f `
            $summary.completed,
            $summary.integrityOk,
            $summary.inboundState,
            $summary.outboundState,
            $summary.inboundErrorCode,
            $summary.outboundErrorCode,
            $resolvedArtifactDir)
    }
}
finally {
    Pop-Location
}
