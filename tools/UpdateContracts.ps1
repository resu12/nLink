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
        $argsList = @("test", "-c", "Release", "--filter", "Category=ContractFreeze")
        if ($NoBuild) { $argsList += "--no-build" }
        & dotnet @argsList
        if ($LASTEXITCODE -ne 0) {
            exit $LASTEXITCODE
        }
    }
    finally {
        Remove-Item Env:NLINK_UPDATE_CONTRACTS -ErrorAction SilentlyContinue
    }

    Write-Host "[Contracts] Updated approved contract files under tests/nLink.SmokeTests/GoldenFiles/Contracts" -ForegroundColor Green
}
finally {
    Pop-Location
}
