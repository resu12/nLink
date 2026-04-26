param(
    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

Push-Location $repoRoot
try {
    $env:NLINK_UPDATE_CONTRACTS = "1"
    try {
        $argsList = @(
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            ".\tools\Test-Lanes.ps1",
            "-Lane",
            "ContractFreeze",
            "-Configuration",
            "Release"
        )
        if ($NoBuild) { $argsList += "-NoBuild" }
        & powershell @argsList
        if ($LASTEXITCODE -ne 0) {
            exit $LASTEXITCODE
        }
    }
    finally {
        Remove-Item Env:NLINK_UPDATE_CONTRACTS -ErrorAction SilentlyContinue
    }

    Write-Host "[Contracts] Updated approved contract files under tests/nLink.SmokeTests.Contracts/GoldenFiles/Contracts" -ForegroundColor Green
}
finally {
    Pop-Location
}
