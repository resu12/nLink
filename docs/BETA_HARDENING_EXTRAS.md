# Beta Hardening Extras (Windows)

These scripts add installer/environment resilience checks for beta stabilization without changing runtime architecture.

## Prerequisites

- Windows machine (interactive user session)
- Built artifacts for the target build:
  - installer EXE (`installer/Build-Installer.ps1`) for installer/permissions smoke
  - portable ZIP or portable folder (`installer/Build-Portable.ps1`) for permissions smoke
- `BetaReadiness` remains the required promotion gate:
  - `powershell -ExecutionPolicy Bypass -File .\tools\BetaReadiness-Check.ps1`

## Safety Notes

- `Installer-UpgradeRollback-Test.ps1` can modify the local `nLink` install registration (same stable Inno `AppId`).
- Run installer-focused scripts in an isolated test VM/profile when possible.
- The installer upgrade/rollback script refuses to run if an existing `nLink` uninstall entry is detected unless `-AllowExistingInstallImpact` is passed.

## Scripts

### 1) Installer Upgrade / Rollback

Validates silent old->current upgrade, uninstall, rollback reinstall, DevLocal CLI smoke, settings persistence behavior (current behavior included), and orphan `node.exe` cleanup.

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Installer-UpgradeRollback-Test.ps1 `
  -OldInstallerPath .\artifacts\releases\0.1.0-alpha.5\nLink-Setup-win-x64-0.1.0-alpha.5.exe `
  -CurrentInstallerPath .\artifacts\installer\nLink-Setup-win-x64-<current>.exe
```

Optional flags:
- `-AllowExistingInstallImpact` to bypass the safety stop (use only on isolated machines)
- `-InstallDir <path>` to use a custom test install path
- `-KeepInstalledForInspection`

Artifact:
- `artifacts/beta-hardening/installer-upgrade-rollback.txt`

### 2) Offline Smoke (Local-Only)

Runs a quick DevLocal CLI smoke with local-only transport and proxy blackhole env vars, then checks CLI output + new app log lines for unexpected npm/bridge network-fetch patterns.

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Offline-Smoke.ps1 `
  -ExePath .\artifacts\portable\nLink\win-x64\nLink.exe
```

Notes:
- This script does not toggle NICs.
- It enforces local-only execution (`DEVLOCAL`) and verifies no network/fetch indicators in logs/output.

Artifact:
- `artifacts/beta-hardening/offline-smoke.txt`

### 3) Permissions Smoke

Checks:
- installer attempt targeting `Program Files` (success if elevated, otherwise graceful denial)
- portable runtime from a non-writable directory (graceful failure when CWD is non-writable)
- portable runtime still works when exe dir is read-only but CWD is writable
- logs still write to `%LOCALAPPDATA%\nLink\logs`

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Permissions-Smoke.ps1 `
  -InstallerExePath .\artifacts\installer\nLink-Setup-win-x64-<current>.exe `
  -PortableZipPath .\artifacts\portable\nLink-Portable-win-x64-<current>.zip
```

Artifact:
- `artifacts/beta-hardening/permissions-smoke.txt`

## Suggested Local Sequence

1. Build artifacts (`Build-Portable.ps1`, `Build-Installer.ps1`).
2. Run the three beta-hardening scripts above.
3. Run the hang/network/resume checks below.
4. Run `BetaReadiness` and confirm PASS.

## Prompt 2: Hang Capture + Network/Resume Robustness

### Hang capture (UI freeze watchdog + manual report)

- UI heartbeat watchdog runs every 1s and captures a hang report if the UI heartbeat is missed for the configured threshold (default: 8s).
- Manual capture is available from the Diagnostics page via `Save Hang Report`.
- Hang reports are saved under:
  - `%LOCALAPPDATA%\nLink\artifacts\hang\hang-<timestamp>\`

Manual verification:
1. Open Diagnostics page and click `Save Hang Report`.
2. Confirm a new `hang-<timestamp>` folder is created.
3. Verify the folder contains `summary.txt`, `diagnostics-snapshot.txt`, `log-tail.txt`, and `resource-snapshot.txt`.

### Network change / resume robustness

- The app subscribes to:
  - `NetworkAvailabilityChanged`
  - `NetworkAddressChanged`
  - Windows power resume events (`SystemEvents.PowerModeChanged`, `Resume`) when available
- Events are debounced for 2s and coalesced.
- Recovery dispatch is single-flight (no overlapping recovery callbacks).

Manual verification:
1. Start a session (NKN or DevLocal).
2. Toggle network (disable/enable adapter) or change network (Wi-Fi/VPN switch).
3. Wait at least 2s after the final change and verify only one recovery attempt/preflight is triggered (see app log).
4. Put the machine to sleep and resume.
5. Verify a single debounced recovery dispatch after resume and that the app remains usable.

Automated verification:
- `dotnet test .\tests\nLink.SmokeTests\nLink.SmokeTests.csproj -c Release --filter FullyQualifiedName~NetworkResilienceCoordinatorTests`

## Prompt 3: Diagnostics Privacy + Cold Start Awareness

### Diagnostics privacy redaction

- Diagnostics copy/export and diagnostics packs apply diagnostics-focused redaction for:
  - `seedBase64`, `seedHex`
  - private keys / PEM private-key blocks
  - wallet seed / mnemonic-like key-value fields
- Redacted placeholder in diagnostics artifacts is:
  - `[REDACTED]`

Automated verification:
- `dotnet test .\tests\nLink.SmokeTests\nLink.SmokeTests.csproj -c Release --filter FullyQualifiedName~DiagnosticsRedactorTests`
- `dotnet test .\tests\nLink.SmokeTests\nLink.SmokeTests.csproj -c Release --filter FullyQualifiedName~DiagnosticsPackSmokeTests`

### Cold-start awareness

- See `docs/FIRST-RUN-PERFORMANCE.md` for first-run slowdown expectations (Defender/AV scanning, warm-up behavior).
- Diagnostics now include first-cold-start annotations (`bridge_first_cold_start_*`) when observed.
- Metrics include a diagnostic-only first cold-start gauge:
  - `bridge_cold_start_ms`

## BetaReadiness Optional Extras Flags (CI-safe defaults OFF)

`tools/BetaReadiness-Check.ps1` now supports optional extras sections that are skipped unless explicitly enabled:

- `-RunInstallerUpgradeRollback`
- `-RunOfflineSmoke`
- `-RunPermissionsSmoke`
- `-RunHangChecks`

Example (all extras enabled locally):

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\BetaReadiness-Check.ps1 `
  -RunInstallerUpgradeRollback `
  -RunOfflineSmoke `
  -RunPermissionsSmoke `
  -RunHangChecks
```

Interpretation in `artifacts/beta-readiness/report.md`:
- Optional extras appear as their own sections.
- `SKIP` means the flag was not enabled (default/CI-safe).
- `PASS`/`FAIL` affects overall BetaReadiness only when that optional section was explicitly enabled.
