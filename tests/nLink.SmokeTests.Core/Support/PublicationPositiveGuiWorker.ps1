param([string]$ExePath,[int]$TimeoutSeconds)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$settings=[IO.File]::ReadAllText($env:NLINK_GUI_WRITER_TEST_SETTINGS) | ConvertFrom-Json
& (Join-Path $settings.RepoRoot 'tests/nLink.SmokeTests.Core/Support/LocalGuiWriterWorker.ps1') -ExePath $ExePath -TimeoutSeconds $TimeoutSeconds
if($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
# Explicitly controlled GUI result, not an analyzer report or live transfer.
$summary=@{event='filetransfer_tuna_gui_handoff_fallback_summary';routeMode='live-regular-activation-cycle'
    completed=$true;integrityOk=$true;offlineFixture=$true}
[IO.File]::WriteAllText((Join-Path $settings.Output 'filetransfer-tuna-gui-summary.json'),($summary | ConvertTo-Json))
[IO.File]::WriteAllText((Join-Path $settings.Output 'controlled-gui.json'),(@{GuiPid=$PID;Live=$false} | ConvertTo-Json))
